// ClapAdapter.cpp - the generic CLAP adapter for AudioPlugSharp plugins.
//
// One binary serves every plugin. It is shipped as <Plugin>.clap in a folder that also contains <Plugin>.dll
// (the managed plugin) and the bridge (AudioPlugSharpBridge.dll, AudioPlugSharp.dll, runtimeconfig). The
// adapter only translates between CLAP and the generated ABI; all plugin behaviour lives in C#.
//
// Threading follows CLAP: init/activate/params/state on the main thread; process (and params.flush while
// processing) on the audio thread. The audio path does not allocate. gui calls are main-thread too.
//
// Editors (gui extension): embedded only. On set_parent the adapter creates an aps::EditorWindow (a child
// window with its own idle timer and input translation) and opens the managed editor in it.
#include "aps/EditorWindow.h"
#include "aps/ManagedPluginFactory.h"

#include <clap/clap.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace fs = std::filesystem;

namespace {

// ---------------------------------------------------------------------------
// Module state: one runtime boot and one cached descriptor per .clap file.
// ---------------------------------------------------------------------------
struct Module {
    std::mutex mutex;
    fs::path clapPath;
    fs::path dir;
    fs::path pluginAssembly;

    std::unique_ptr<aps::ManagedPluginFactory> factory;
    bool factoryFailed = false;

    bool descriptorReady = false;
    bool descriptorFailed = false;
    std::string id, name, vendor, url, version, description;
    std::vector<std::string> featureStrings;
    std::vector<const char*> features;
    clap_plugin_descriptor_t descriptor{};
};

Module g;

void moduleLog(const std::string& message) { std::fprintf(stderr, "[AudioPlugSharp CLAP] %s\n", message.c_str()); }

// Boots the runtime on first use. Caller holds g.mutex.
aps::ManagedPluginFactory* factoryLocked() {
    if (g.factory) return g.factory.get();
    if (g.factoryFailed) return nullptr;
    auto factory = std::make_unique<aps::ManagedPluginFactory>();
    std::string error;
    if (!factory->initialize(g.dir, error)) {
        moduleLog("could not start the .NET runtime: " + error);
        g.factoryFailed = true;
        return nullptr;
    }
    g.factory = std::move(factory);
    return g.factory.get();
}

// A host that only logs; used to read the plugin description while scanning.
class ProbeHost final : public aps::HostApi {
public:
    aps::HostInfo getInfo() override {
        aps::HostInfo info;
        info.name = "AudioPlugSharp CLAP scan";
        return info;
    }
    void log(aps::LogLevel level, std::string_view message) override {
        if (level >= aps::LogLevel::Warning) moduleLog(std::string(message));
    }
    void beginEdit(uint32_t) override {}
    void performEdit(uint32_t, double) override {}
    void endEdit(uint32_t) override {}
};

std::string toLower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

// AudioPlugSharp categories are VST3-style ("Fx", "Instrument|Synth", "Fx|Delay"); map them to CLAP features.
std::vector<std::string> featuresFor(const aps::PluginInfo& info, uint32_t mainOutputChannels) {
    std::vector<std::string> result;
    auto add = [&](const char* f) {
        if (std::find(result.begin(), result.end(), f) == result.end()) result.emplace_back(f);
    };
    std::string category = toLower(info.category);
    size_t start = 0;
    while (start <= category.size()) {
        size_t end = category.find('|', start);
        std::string token = category.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (token == "fx") add(CLAP_PLUGIN_FEATURE_AUDIO_EFFECT);
        else if (token == "instrument") add(CLAP_PLUGIN_FEATURE_INSTRUMENT);
        else if (token == "synth") add(CLAP_PLUGIN_FEATURE_SYNTHESIZER);
        else if (token == "sampler") add(CLAP_PLUGIN_FEATURE_SAMPLER);
        else if (token == "drum") add(CLAP_PLUGIN_FEATURE_DRUM);
        else if (token == "delay") add(CLAP_PLUGIN_FEATURE_DELAY);
        else if (token == "reverb") add(CLAP_PLUGIN_FEATURE_REVERB);
        else if (token == "distortion") add(CLAP_PLUGIN_FEATURE_DISTORTION);
        else if (token == "eq") add(CLAP_PLUGIN_FEATURE_EQUALIZER);
        else if (token == "filter") add(CLAP_PLUGIN_FEATURE_FILTER);
        else if (token == "dynamics") add(CLAP_PLUGIN_FEATURE_COMPRESSOR);
        else if (token == "analyzer") add(CLAP_PLUGIN_FEATURE_ANALYZER);
        else if (!token.empty()) result.push_back(token);
        if (end == std::string::npos) break;
        start = end + 1;
    }
    const bool hasMain = std::any_of(result.begin(), result.end(), [](const std::string& f) {
        return f == CLAP_PLUGIN_FEATURE_AUDIO_EFFECT || f == CLAP_PLUGIN_FEATURE_INSTRUMENT ||
               f == CLAP_PLUGIN_FEATURE_NOTE_EFFECT || f == CLAP_PLUGIN_FEATURE_ANALYZER;
    });
    if (!hasMain) result.insert(result.begin(), info.numInputPorts > 0 ? CLAP_PLUGIN_FEATURE_AUDIO_EFFECT
                                                                       : CLAP_PLUGIN_FEATURE_INSTRUMENT);
    if (mainOutputChannels == 1) add(CLAP_PLUGIN_FEATURE_MONO);
    if (mainOutputChannels == 2) add(CLAP_PLUGIN_FEATURE_STEREO);
    return result;
}

// Loads a throwaway instance to read the description. Caller holds g.mutex.
const clap_plugin_descriptor_t* descriptorLocked() {
    if (g.descriptorReady) return &g.descriptor;
    if (g.descriptorFailed) return nullptr;
    auto* factory = factoryLocked();
    if (!factory) { g.descriptorFailed = true; return nullptr; }

    ProbeHost host;
    std::string error;
    auto plugin = factory->load(g.pluginAssembly, host, error);
    if (!plugin) {
        moduleLog("could not load " + g.pluginAssembly.string() + ": " + error);
        g.descriptorFailed = true;
        return nullptr;
    }
    const auto info = plugin->getInfo().value;
    const uint32_t mainOut = info.numOutputPorts > 0 ? plugin->getOutputPort(0).value.numChannels : 0;
    plugin.reset();

    char idHex[17];
    std::snprintf(idHex, sizeof(idHex), "%016llx", static_cast<unsigned long long>(info.pluginId));
    g.id = std::string("audioplugsharp.") + idHex;
    g.name = info.name;
    g.vendor = info.company;
    g.url = info.website;
    g.version = info.version;
    g.description = info.name + " (AudioPlugSharp)";
    g.featureStrings = featuresFor(info, mainOut);
    g.features.clear();
    for (auto& f : g.featureStrings) g.features.push_back(f.c_str());
    g.features.push_back(nullptr);

    g.descriptor = clap_plugin_descriptor_t{};
    g.descriptor.clap_version = CLAP_VERSION;
    g.descriptor.id = g.id.c_str();
    g.descriptor.name = g.name.c_str();
    g.descriptor.vendor = g.vendor.c_str();
    g.descriptor.url = g.url.c_str();
    g.descriptor.manual_url = "";
    g.descriptor.support_url = "";
    g.descriptor.version = g.version.c_str();
    g.descriptor.description = g.description.c_str();
    g.descriptor.features = g.features.data();
    g.descriptorReady = true;
    return &g.descriptor;
}

// ---------------------------------------------------------------------------
// Lock-free single-producer/single-consumer queue (plugin edits: main thread -> process/flush).
// ---------------------------------------------------------------------------
template <typename T, size_t N>
class SpscQueue {
public:
    bool push(const T& item) {
        const size_t head = head_.load(std::memory_order_relaxed);
        const size_t next = (head + 1) % N;
        if (next == tail_.load(std::memory_order_acquire)) return false;
        items_[head] = item;
        head_.store(next, std::memory_order_release);
        return true;
    }
    bool pop(T& item) {
        const size_t tail = tail_.load(std::memory_order_relaxed);
        if (tail == head_.load(std::memory_order_acquire)) return false;
        item = items_[tail];
        tail_.store((tail + 1) % N, std::memory_order_release);
        return true;
    }

private:
    std::array<T, N> items_{};
    std::atomic<size_t> head_{0}, tail_{0};
};

struct Edit {
    enum Kind : uint8_t { Begin, Value, End } kind = Value;
    uint32_t index = 0;
    double value = 0;
};

uint32_t fnv1a(const std::string& s) {
    uint32_t h = 2166136261u;
    for (unsigned char c : s) { h ^= c; h *= 16777619u; }
    return h;
}

// ---------------------------------------------------------------------------
// One plugin instance.
// ---------------------------------------------------------------------------
class Instance final : public aps::HostApi, private aps::EditorWindow::Client {
public:
    explicit Instance(const clap_host_t* host) : host_(host) {
        clap_.desc = &g.descriptor;
        clap_.plugin_data = this;
        clap_.init = [](const clap_plugin_t* p) { return self(p)->init(); };
        clap_.destroy = [](const clap_plugin_t* p) { delete self(p); };
        clap_.activate = [](const clap_plugin_t* p, double sr, uint32_t minFrames, uint32_t maxFrames) {
            return self(p)->activate(sr, minFrames, maxFrames);
        };
        clap_.deactivate = [](const clap_plugin_t* p) { self(p)->deactivate(); };
        clap_.start_processing = [](const clap_plugin_t*) { return true; };
        clap_.stop_processing = [](const clap_plugin_t*) {};
        clap_.reset = [](const clap_plugin_t*) {};
        clap_.process = [](const clap_plugin_t* p, const clap_process_t* process) { return self(p)->process(process); };
        clap_.get_extension = [](const clap_plugin_t* p, const char* id) { return self(p)->extension(id); };
        clap_.on_main_thread = [](const clap_plugin_t* p) { self(p)->onMainThread(); };
    }

    ~Instance() override { destroyEditor(); } // before api_ goes: the managed editor draws into the window

    const clap_plugin_t* clap() const { return &clap_; }

    // ---- aps::HostApi: called by the managed plugin (main thread) ---------------------
    aps::HostInfo getInfo() override {
        aps::HostInfo info;
        info.name = host_->name ? host_->name : "CLAP host";
        return info;
    }
    void log(aps::LogLevel level, std::string_view message) override {
        std::string text(message);
        if (hostLog_) {
            const clap_log_severity severity = level == aps::LogLevel::Debug ? CLAP_LOG_DEBUG
                                             : level == aps::LogLevel::Info  ? CLAP_LOG_INFO
                                             : level == aps::LogLevel::Warning ? CLAP_LOG_WARNING
                                                                                : CLAP_LOG_ERROR;
            hostLog_->log(host_, severity, text.c_str());
        } else {
            moduleLog(text);
        }
    }
    void beginEdit(uint32_t index) override { queueEdit({Edit::Begin, index, 0}); }
    void performEdit(uint32_t index, double normalized) override { queueEdit({Edit::Value, index, normalized}); }
    void endEdit(uint32_t index) override { queueEdit({Edit::End, index, 0}); }
    void setEditorCursorVisible(bool visible) override {
        if (!editorOpen_ || !editorWindow_->setCursorVisible(visible)) throw aps::NotSupported{};
    }
    bool setEditorFullscreen(bool fullscreen) override {
        if (!editorOpen_ || (fullscreen && !editorResizable())) return false;
        return editorWindow_->setFullscreen(fullscreen);
    }

private:
    static Instance* self(const clap_plugin_t* p) { return static_cast<Instance*>(p->plugin_data); }

    // ---- lifecycle (main thread) ------------------------------------------------------
    bool init() {
        hostLog_ = static_cast<const clap_host_log_t*>(host_->get_extension(host_, CLAP_EXT_LOG));
        hostGui_ = static_cast<const clap_host_gui_t*>(host_->get_extension(host_, CLAP_EXT_GUI));
        hostParams_ = static_cast<const clap_host_params_t*>(host_->get_extension(host_, CLAP_EXT_PARAMS));
        hostState_ = static_cast<const clap_host_state_t*>(host_->get_extension(host_, CLAP_EXT_STATE));
        hostTimer_ = static_cast<const clap_host_timer_support_t*>(host_->get_extension(host_, CLAP_EXT_TIMER_SUPPORT));
        hostFd_ = static_cast<const clap_host_posix_fd_support_t*>(host_->get_extension(host_, CLAP_EXT_POSIX_FD_SUPPORT));

        aps::ManagedPluginFactory* factory;
        {
            std::lock_guard lock(g.mutex);
            factory = factoryLocked();
        }
        if (!factory) return false;
        std::string error;
        api_ = factory->load(g.pluginAssembly, *this, error);
        if (!api_) {
            log(aps::LogLevel::Error, "could not create plugin: " + error);
            return false;
        }

        info_ = api_->getInfo().value;
        for (uint32_t i = 0; i < info_.numInputPorts; ++i) inPorts_.push_back(api_->getInputPort(i).value);
        for (uint32_t i = 0; i < info_.numOutputPorts; ++i) outPorts_.push_back(api_->getOutputPort(i).value);
        format_ = aps::hasFlag(info_.sampleFormats, aps::SampleFormat::Float32) ? aps::SampleFormat::Float32
                                                                                 : aps::SampleFormat::Float64;

        // CLAP parameter ids must be stable: derive them from the plugin's string ids, not from positions.
        const uint32_t count = api_->getParamCount().valueOr(0);
        for (uint32_t i = 0; i < count; ++i) {
            auto p = api_->getParamInfo(i).value;
            clap_id id = fnv1a(p.key.empty() ? p.name : p.key) & 0x7fffffffu;
            while (idToIndex_.count(id)) id = (id + 1) & 0x7fffffffu; // resolve the (unlikely) collision
            idToIndex_[id] = i;
            params_.push_back({id, p});
        }
        pending_.assign(count, NAN);
        values_ = std::make_unique<std::atomic<double>[]>(count);
        valueDirty_ = std::make_unique<std::atomic<bool>[]>(count);
        refreshValues();

        inBuffers_.resize(inPorts_.size());
        outBuffers_.resize(outPorts_.size());

        refreshEditorInfo(); // stays zero (no editor) with a bridge that lacks the editor members
        return true;
    }

    bool activate(double sampleRate, uint32_t, uint32_t maxFrames) {
        if (!api_) return false;
        if (!api_->setupProcessing(sampleRate, maxFrames, format_)) return false;
        if (!api_->setActive(true)) return false;
        sampleRate_ = sampleRate;
        active_ = true;
        return true;
    }

    void deactivate() {
        if (!active_) return;
        api_->setActive(false);
        active_ = false;
    }

    // ---- processing (audio thread) ----------------------------------------------------
    clap_process_status process(const clap_process_t* p) {
        if (!active_) return CLAP_PROCESS_ERROR;
        uint32_t n = 0;
        collectEvents(p->in_events, p->out_events, p->frames_count, n);

        const bool use64 = format_ == aps::SampleFormat::Float64;
        for (size_t i = 0; i < inBuffers_.size(); ++i) {
            void** data = nullptr;
            uint32_t channels = 0;
            if (i < p->audio_inputs_count) {
                data = use64 ? reinterpret_cast<void**>(p->audio_inputs[i].data64)
                             : reinterpret_cast<void**>(p->audio_inputs[i].data32);
                channels = p->audio_inputs[i].channel_count;
            }
            inBuffers_[i] = aps_audio_port_buffers{data, data ? channels : 0, 0};
        }
        for (size_t i = 0; i < outBuffers_.size(); ++i) {
            void** data = nullptr;
            uint32_t channels = 0;
            if (i < p->audio_outputs_count) {
                data = use64 ? reinterpret_cast<void**>(p->audio_outputs[i].data64)
                             : reinterpret_cast<void**>(p->audio_outputs[i].data32);
                channels = p->audio_outputs[i].channel_count;
                p->audio_outputs[i].constant_mask = 0;
            }
            outBuffers_[i] = aps_audio_port_buffers{data, data ? channels : 0, 0};
        }

        aps::ProcessData d{};
        d.inputs = inBuffers_.data();
        d.outputs = outBuffers_.data();
        d.num_input_ports = static_cast<uint32_t>(inBuffers_.size());
        d.num_output_ports = static_cast<uint32_t>(outBuffers_.size());
        d.events = events_.data();
        d.num_events = n;
        d.output_events = outEvents_.data();
        d.output_event_capacity = static_cast<uint32_t>(outEvents_.size());
        d.num_frames = p->frames_count;
        d.sample_format = static_cast<aps_sample_format>(format_);
        d.tempo = 120.0;
        d.project_sample = p->steady_time >= 0 ? p->steady_time : 0;
        if (const auto* t = p->transport) {
            if (t->flags & CLAP_TRANSPORT_HAS_TEMPO) d.tempo = t->tempo;
            if (t->flags & CLAP_TRANSPORT_IS_PLAYING) d.transport_flags = APS_TRANSPORT_FLAGS_PLAYING;
            if (t->flags & CLAP_TRANSPORT_HAS_SECONDS_TIMELINE)
                d.project_sample = static_cast<int64_t>(std::llround(double(t->song_pos_seconds) /
                                                                     double(CLAP_SECTIME_FACTOR) * sampleRate_));
        }

        if (!api_->process(d)) return CLAP_PROCESS_ERROR;
        pushOutputEvents(d, p->out_events);
        requestValueSync();
        return CLAP_PROCESS_CONTINUE;
    }

    // ---- current parameter values -------------------------------------------------------
    // In CLAP the plugin is the source of truth for parameter values, and host changes arrive as events in
    // process/flush. AudioPlugSharp only applies those to the DSP (ProcessValue), so the adapter tracks the
    // current value itself (get_value reads it) and brings the plugin's edit values up to date on the main
    // thread (host request_callback -> on_main_thread).

    void refreshValues() {
        for (uint32_t i = 0; i < params_.size(); ++i) {
            values_[i].store(api_->getParamNormalized(i).valueOr(params_[i].info.defaultNormalized), std::memory_order_relaxed);
            valueDirty_[i].store(false, std::memory_order_relaxed);
        }
    }

    // Any thread.
    void hostChangedValue(uint32_t index, double value) {
        values_[index].store(value, std::memory_order_relaxed);
        valueDirty_[index].store(true, std::memory_order_release);
        valuesNeedSync_.store(true, std::memory_order_release);
    }

    // Any thread (request_callback is thread-safe).
    void requestValueSync() {
        if (valuesNeedSync_.load(std::memory_order_acquire) && !callbackRequested_.exchange(true))
            host_->request_callback(host_);
    }

    // Main thread.
    void onMainThread() {
        callbackRequested_.store(false);
        if (!valuesNeedSync_.exchange(false, std::memory_order_acq_rel)) return;
        for (uint32_t i = 0; i < params_.size(); ++i)
            if (valueDirty_[i].exchange(false, std::memory_order_acq_rel))
                api_->setParamNormalized(i, values_[i].load(std::memory_order_relaxed));
    }

    // Host events + plugin edits -> the bridge's sorted event array. Also forwards plugin edits to the host.
    void collectEvents(const clap_input_events_t* in, const clap_output_events_t* out, uint32_t frames, uint32_t& n) {
        auto add = [&](const aps::AudioEvent& e) {
            if (n < events_.size()) events_[n++] = e;
        };

        // Values the host set while we were inactive (params.flush on the main thread) reach the DSP here.
        if (havePending_.exchange(false, std::memory_order_acquire))
            for (uint32_t i = 0; i < pending_.size(); ++i)
                if (!std::isnan(pending_[i])) {
                    add({APS_AUDIO_EVENT_TYPE_PARAM_CHANGE, 0, i, 0, pending_[i]});
                    pending_[i] = NAN;
                }

        const uint32_t count = in ? in->size(in) : 0;
        for (uint32_t i = 0; i < count; ++i) {
            const clap_event_header_t* h = in->get(in, i);
            if (!h || h->space_id != CLAP_CORE_EVENT_SPACE_ID) continue;
            switch (h->type) {
                case CLAP_EVENT_NOTE_ON:
                case CLAP_EVENT_NOTE_OFF: {
                    const auto* e = reinterpret_cast<const clap_event_note_t*>(h);
                    if (e->key < 0) break; // wildcard (e.g. "all notes off on channel") - not representable
                    add({h->type == CLAP_EVENT_NOTE_ON ? APS_AUDIO_EVENT_TYPE_NOTE_ON : APS_AUDIO_EVENT_TYPE_NOTE_OFF,
                         h->time, static_cast<uint32_t>(e->key), std::max<int16_t>(e->channel, 0), e->velocity});
                    break;
                }
                case CLAP_EVENT_MIDI: {
                    const auto* e = reinterpret_cast<const clap_event_midi_t*>(h);
                    const uint8_t status = e->data[0] & 0xF0, channel = e->data[0] & 0x0F;
                    const uint32_t d1 = e->data[1] & 0x7F, d2 = e->data[2] & 0x7F;
                    if (status == 0x90 && d2 > 0)
                        add({APS_AUDIO_EVENT_TYPE_NOTE_ON, h->time, d1, channel, d2 / 127.0});
                    else if (status == 0x80 || (status == 0x90 && d2 == 0))
                        add({APS_AUDIO_EVENT_TYPE_NOTE_OFF, h->time, d1, channel, d2 / 127.0});
                    else if (status == 0xA0)
                        add({APS_AUDIO_EVENT_TYPE_POLY_PRESSURE, h->time, d1, channel, d2 / 127.0});
                    else if (status == 0xB0)
                        add({APS_AUDIO_EVENT_TYPE_CONTROL_CHANGE, h->time, d1, channel, double(d2)});
                    break;
                }
                case CLAP_EVENT_PARAM_VALUE: {
                    const auto* e = reinterpret_cast<const clap_event_param_value_t*>(h);
                    auto it = idToIndex_.find(e->param_id);
                    if (it != idToIndex_.end()) {
                        const double v = std::clamp(e->value, 0.0, 1.0);
                        add({APS_AUDIO_EVENT_TYPE_PARAM_CHANGE, h->time, it->second, 0, v});
                        hostChangedValue(it->second, v);
                    }
                    break;
                }
                default: break;
            }
        }

        // CLAP does not echo a plugin's own edits back to it: apply them to the DSP and tell the host. They go at
        // the block's last sample, after every host event (which keeps the array sorted): AudioPlugSharp ramps a
        // parameter from its previous change point to the next one, so a UI edit glides across the block instead
        // of stepping at its start (zipper noise while a control is dragged).
        const uint32_t editOffset = frames > 0 ? frames - 1 : 0;
        Edit edit;
        while (edits_.pop(edit)) {
            if (edit.index >= params_.size()) continue;
            const clap_id id = params_[edit.index].id;
            if (edit.kind == Edit::Value) {
                add({APS_AUDIO_EVENT_TYPE_PARAM_CHANGE, editOffset, edit.index, 0, edit.value});
                clap_event_param_value_t ev{};
                ev.header = {sizeof(ev), 0, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_PARAM_VALUE, 0};
                ev.param_id = id;
                ev.note_id = -1;
                ev.port_index = ev.channel = ev.key = -1;
                ev.value = edit.value;
                if (out) out->try_push(out, &ev.header);
            } else {
                clap_event_param_gesture_t ev{};
                ev.header = {sizeof(ev), 0, CLAP_CORE_EVENT_SPACE_ID,
                             static_cast<uint16_t>(edit.kind == Edit::Begin ? CLAP_EVENT_PARAM_GESTURE_BEGIN
                                                                            : CLAP_EVENT_PARAM_GESTURE_END),
                             0};
                ev.param_id = id;
                if (out) out->try_push(out, &ev.header);
            }
        }
    }

    void pushOutputEvents(const aps::ProcessData& d, const clap_output_events_t* out) {
        if (!out) return;
        for (uint32_t i = 0; i < d.num_output_events; ++i) {
            const auto& e = outEvents_[i];
            if (e.type == APS_AUDIO_EVENT_TYPE_NOTE_ON || e.type == APS_AUDIO_EVENT_TYPE_NOTE_OFF) {
                clap_event_note_t ev{};
                ev.header = {sizeof(ev), e.sample_offset, CLAP_CORE_EVENT_SPACE_ID,
                             static_cast<uint16_t>(e.type == APS_AUDIO_EVENT_TYPE_NOTE_ON ? CLAP_EVENT_NOTE_ON
                                                                                          : CLAP_EVENT_NOTE_OFF),
                             0};
                ev.note_id = -1;
                ev.port_index = 0;
                ev.channel = static_cast<int16_t>(e.channel);
                ev.key = static_cast<int16_t>(e.index);
                ev.velocity = e.value;
                out->try_push(out, &ev.header);
            } else if (e.type == APS_AUDIO_EVENT_TYPE_POLY_PRESSURE || e.type == APS_AUDIO_EVENT_TYPE_CONTROL_CHANGE) {
                clap_event_midi_t ev{};
                ev.header = {sizeof(ev), e.sample_offset, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_MIDI, 0};
                ev.port_index = 0;
                const bool cc = e.type == APS_AUDIO_EVENT_TYPE_CONTROL_CHANGE;
                ev.data[0] = static_cast<uint8_t>((cc ? 0xB0 : 0xA0) | (e.channel & 0x0F));
                ev.data[1] = static_cast<uint8_t>(e.index & 0x7F);
                ev.data[2] = static_cast<uint8_t>(cc ? std::clamp(e.value, 0.0, 127.0)
                                                     : std::clamp(e.value * 127.0, 0.0, 127.0));
                out->try_push(out, &ev.header);
            }
        }
    }

    void queueEdit(const Edit& edit) {
        if (edit.kind == Edit::Value && edit.index < params_.size())
            values_[edit.index].store(edit.value, std::memory_order_relaxed); // the edit value is already set
        if (!edits_.push(edit)) moduleLog("parameter edit queue full; edit dropped");
        if (hostParams_) hostParams_->request_flush(host_);
        if (edit.kind == Edit::End && hostState_) hostState_->mark_dirty(host_);
    }

    // ---- extensions --------------------------------------------------------------------
    const void* extension(const char* id) {
        if (!std::strcmp(id, CLAP_EXT_AUDIO_PORTS)) return &audioPorts_;
        if (!std::strcmp(id, CLAP_EXT_NOTE_PORTS)) return &notePorts_;
        if (!std::strcmp(id, CLAP_EXT_PARAMS)) return &params_ext_;
        if (!std::strcmp(id, CLAP_EXT_STATE)) return &state_;
        if (!std::strcmp(id, CLAP_EXT_GUI) && hasEditor()) return &gui_;
        if (!std::strcmp(id, CLAP_EXT_TIMER_SUPPORT)) return &timerSupport_;
        if (!std::strcmp(id, CLAP_EXT_POSIX_FD_SUPPORT)) return &fdSupport_;
        return nullptr;
    }

    static const clap_plugin_audio_ports_t audioPorts_;
    static const clap_plugin_note_ports_t notePorts_;
    static const clap_plugin_params_t params_ext_;
    static const clap_plugin_state_t state_;
    static const clap_plugin_gui_t gui_;
    static const clap_plugin_timer_support_t timerSupport_;
    static const clap_plugin_posix_fd_support_t fdSupport_;

    // audio-ports
    static uint32_t portsCount(const clap_plugin_t* p, bool isInput) {
        auto* s = self(p);
        return static_cast<uint32_t>(isInput ? s->inPorts_.size() : s->outPorts_.size());
    }
    static bool portsGet(const clap_plugin_t* p, uint32_t index, bool isInput, clap_audio_port_info_t* info) {
        {
            auto* s = self(p);
            const auto& ports = isInput ? s->inPorts_ : s->outPorts_;
            if (index >= ports.size() || !info) return false;
            *info = clap_audio_port_info_t{};
            info->id = index;
            aps::detail::writeFixed(ports[index].name, info->name, sizeof(info->name));
            info->flags = index == 0 ? CLAP_AUDIO_PORT_IS_MAIN : 0;
            if (s->format_ == aps::SampleFormat::Float64)
                info->flags |= CLAP_AUDIO_PORT_SUPPORTS_64BITS | CLAP_AUDIO_PORT_PREFERS_64BITS |
                               CLAP_AUDIO_PORT_REQUIRES_COMMON_SAMPLE_SIZE;
            info->channel_count = ports[index].numChannels;
            info->port_type = ports[index].numChannels == 1 ? CLAP_PORT_MONO
                            : ports[index].numChannels == 2 ? CLAP_PORT_STEREO
                                                             : nullptr;
            info->in_place_pair = CLAP_INVALID_ID;
            return true;
        }
    }

    // note-ports: AudioPlugSharp plugins always get one note input and one output (as with the VST3 bridge).
    static uint32_t notePortsCount(const clap_plugin_t*, bool) { return 1; }
    static bool notePortsGet(const clap_plugin_t*, uint32_t index, bool isInput, clap_note_port_info_t* info) {
        if (index != 0 || !info) return false;
        *info = clap_note_port_info_t{};
        info->id = 0;
        info->supported_dialects = CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI;
        info->preferred_dialect = CLAP_NOTE_DIALECT_CLAP;
        aps::detail::writeFixed(isInput ? "Event In" : "Event Out", info->name, sizeof(info->name));
        return true;
    }

    // params: exposed normalized (0..1); text comes from the plugin's FormatValue.
    static uint32_t paramsCount(const clap_plugin_t* p) { return static_cast<uint32_t>(self(p)->params_.size()); }
    static bool paramsGetInfo(const clap_plugin_t* p, uint32_t index, clap_param_info_t* info) {
        {
            auto* s = self(p);
            if (index >= s->params_.size() || !info) return false;
            const auto& param = s->params_[index];
            *info = clap_param_info_t{};
            info->id = param.id;
            info->flags = CLAP_PARAM_IS_AUTOMATABLE;
            aps::detail::writeFixed(param.info.name, info->name, sizeof(info->name));
            info->min_value = 0.0;
            info->max_value = 1.0;
            info->default_value = std::clamp(param.info.defaultNormalized, 0.0, 1.0);
            return true;
        }
    }
    static bool paramsGetValue(const clap_plugin_t* p, clap_id id, double* value) {
        {
            auto* s = self(p);
            auto it = s->idToIndex_.find(id);
            if (it == s->idToIndex_.end() || !value) return false;
            *value = s->values_[it->second].load(std::memory_order_relaxed);
            return true;
        }
    }
    static bool paramsValueToText(const clap_plugin_t* p, clap_id id, double value, char* buffer, uint32_t capacity) {
        {
            auto* s = self(p);
            auto it = s->idToIndex_.find(id);
            if (it == s->idToIndex_.end() || !buffer || capacity == 0) return false;
            auto r = s->api_->formatParam(it->second, value);
            if (!r) return false;
            aps::detail::writeFixed(r.value.text, buffer, capacity);
            return true;
        }
    }
    static bool paramsTextToValue(const clap_plugin_t*, clap_id, const char*, double*) { return false; }
    static void paramsFlush(const clap_plugin_t* p, const clap_input_events_t* in, const clap_output_events_t* out) {
        self(p)->flush(in, out);
    }

    // Called on the audio thread while processing, otherwise on the main thread.
    void flush(const clap_input_events_t* in, const clap_output_events_t* out) {
        if (active_) {
            // Events-only block: the bridge delivers the parameter changes without audio.
            uint32_t n = 0;
            collectEvents(in, out, 0, n);
            aps::ProcessData d{};
            d.events = events_.data();
            d.num_events = n;
            d.output_events = outEvents_.data();
            d.output_event_capacity = static_cast<uint32_t>(outEvents_.size());
            d.sample_format = static_cast<aps_sample_format>(format_);
            api_->process(d);
            pushOutputEvents(d, out);
            requestValueSync();
            return;
        }
        // Not active (main thread): update the edit values now and replay the values to the DSP on the
        // first block after activation.
        Edit edit;
        while (edits_.pop(edit)) {
            if (edit.index >= params_.size() || !out) continue;
            if (edit.kind == Edit::Value) {
                clap_event_param_value_t ev{};
                ev.header = {sizeof(ev), 0, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_PARAM_VALUE, 0};
                ev.param_id = params_[edit.index].id;
                ev.note_id = -1;
                ev.port_index = ev.channel = ev.key = -1;
                ev.value = edit.value;
                out->try_push(out, &ev.header);
                pending_[edit.index] = edit.value;
                havePending_.store(true, std::memory_order_release);
            }
        }
        if (!in) return;
        for (uint32_t i = 0, count = in->size(in); i < count; ++i) {
            const clap_event_header_t* h = in->get(in, i);
            if (!h || h->space_id != CLAP_CORE_EVENT_SPACE_ID || h->type != CLAP_EVENT_PARAM_VALUE) continue;
            const auto* e = reinterpret_cast<const clap_event_param_value_t*>(h);
            auto it = idToIndex_.find(e->param_id);
            if (it == idToIndex_.end()) continue;
            const double v = std::clamp(e->value, 0.0, 1.0);
            api_->setParamNormalized(it->second, v);
            values_[it->second].store(v, std::memory_order_relaxed);
            pending_[it->second] = v;
            havePending_.store(true, std::memory_order_release);
        }
    }

    // state
    static bool stateSave(const clap_plugin_t* p, const clap_ostream_t* stream) {
        {
            auto r = self(p)->api_->saveState();
            if (!r || !stream) return false;
            const uint8_t* data = r.value.data();
            uint64_t left = r.value.size();
            while (left > 0) {
                const int64_t written = stream->write(stream, data, left);
                if (written <= 0) return false;
                data += written;
                left -= static_cast<uint64_t>(written);
            }
            return true;
        }
    }
    static bool stateLoad(const clap_plugin_t* p, const clap_istream_t* stream) {
        {
            if (!stream) return false;
            std::vector<uint8_t> data;
            uint8_t chunk[4096];
            for (;;) {
                const int64_t read = stream->read(stream, chunk, sizeof(chunk));
                if (read < 0) return false;
                if (read == 0) break;
                data.insert(data.end(), chunk, chunk + read);
            }
            auto* s = self(p);
            if (!s->api_->loadState(std::span<const uint8_t>(data))) return false;
            s->refreshValues();
            return true;
        }
    }

    // ---- gui (main thread) ----------------------------------------------------------------
    // Sizes on the gui extension are window pixels: the editor's logical size x the UI scale (the host's from
    // set_scale, otherwise the OS's). The editor has a fixed size unless it says it is resizable.

    bool hasEditor() const {
        const auto type = aps::EditorWindow::platformWindowType();
        // X11 editors run from the host's run loop: CLAP requires timer-support there.
        if (aps::EditorWindow::platformNeedsHostTimer() && !(hostTimer_ && hostTimer_->register_timer)) return false;
        return type != aps::EditorWindowType::None &&
               aps::hasFlag(static_cast<aps::EditorFlags>(editorInfo_.flags), aps::EditorFlags::HasEditor) &&
               (editorInfo_.window_types & (1u << static_cast<int>(type))) != 0;
    }

    bool editorResizable() const {
        return aps::hasFlag(static_cast<aps::EditorFlags>(editorInfo_.flags), aps::EditorFlags::Resizable);
    }

    static const char* clapWindowApi(aps::EditorWindowType type) {
        switch (type) {
            case aps::EditorWindowType::Win32: return CLAP_WINDOW_API_WIN32;
            case aps::EditorWindowType::X11: return CLAP_WINDOW_API_X11;
            case aps::EditorWindowType::Cocoa: return CLAP_WINDOW_API_COCOA;
            default: return nullptr;
        }
    }

    bool guiApiSupported(const char* api, bool isFloating) const {
        const char* ours = clapWindowApi(aps::EditorWindow::platformWindowType());
        return !isFloating && api && ours && !std::strcmp(api, ours) && hasEditor();
    }

    void refreshEditorInfo() {
        if (auto r = api_->getEditorInfo()) editorInfo_ = r.value;
    }

    bool openEditor(intptr_t parent) {
        closeEditor();
        refreshEditorInfo();
        osScale_ = aps::EditorWindow::platformScale(parent);
        const double scale = editorScale();
        editorWindow_ = aps::EditorWindow::create(parent, aps::toWindowPixels(editorInfo_.width, scale),
                                                  aps::toWindowPixels(editorInfo_.height, scale), *this);
        if (!editorWindow_) {
            log(aps::LogLevel::Error, "could not create the editor window");
            return false;
        }
        appliedScale_ = scale;
        if (auto r = api_->openEditor(editorWindow_->handle(), editorWindow_->windowType(), scale); !r) {
            log(aps::LogLevel::Error, "the plugin could not open its editor (error " + std::to_string(r.code) + ")");
            editorWindow_.reset();
            return false;
        }
        editorOpen_ = true;
        editorWindow_->editorOpened();
        editorWindow_->setVisible(editorVisible_);
        editorWindow_->setTimerRunning(editorVisible_);
        if (editorWindow_->needsHostTimer()) {
            if (!hostTimer_->register_timer(host_, aps::EditorWindow::kHostTimerMs, &editorTimerId_)) {
                log(aps::LogLevel::Error, "the host refused the editor's timer");
                closeEditor();
                return false;
            }
            const int fd = editorWindow_->eventFd();
            if (fd >= 0 && hostFd_ && hostFd_->register_fd(host_, fd, CLAP_POSIX_FD_READ)) editorFd_ = fd;
        }
        return true;
    }

    void closeEditor() {
        if (editorTimerId_ != CLAP_INVALID_ID) {
            hostTimer_->unregister_timer(host_, editorTimerId_);
            editorTimerId_ = CLAP_INVALID_ID;
        }
        if (editorFd_ >= 0) {
            hostFd_->unregister_fd(host_, editorFd_);
            editorFd_ = -1;
        }
        if (editorOpen_) {
            if (editorWindow_) {
                editorWindow_->editorClosing(); // leaves fullscreen (the editor follows) while it is still open
                editorWindow_->setTimerRunning(false);
            }
            editorOpen_ = false;
            api_->closeEditor();
        }
        editorWindow_.reset();
    }

    // timer-support / posix-fd-support: the host's run loop drives editor windows that cannot drive themselves.
    static void onTimer(const clap_plugin_t* p, clap_id timerId) {
        auto* s = self(p);
        if (timerId == s->editorTimerId_ && s->editorWindow_) s->editorWindow_->onHostTimer();
    }
    static void onFd(const clap_plugin_t* p, int fd, clap_posix_fd_flags_t) {
        auto* s = self(p);
        if (fd == s->editorFd_ && s->editorWindow_) s->editorWindow_->onEventFd();
    }

    void destroyEditor() {
        if (api_) closeEditor();
        guiCreated_ = false;
    }

    bool setEditorVisible(bool visible) {
        if (!guiCreated_) return false;
        editorVisible_ = visible;
        if (editorWindow_) {
            editorWindow_->setVisible(visible);
            editorWindow_->setTimerRunning(visible && editorOpen_);
        }
        return true;
    }

    // aps::EditorWindow::Client
    void editorIdle(double secondsElapsed) override {
        if (!editorOpen_) return;
        if (!api_->editorIdle(secondsElapsed)) {
            // The bridge logged the exception; don't repeat it 60 times a second.
            log(aps::LogLevel::Error, "the editor failed while drawing; it is stopped until it is reopened");
            editorWindow_->setTimerRunning(false);
        }
    }
    bool editorInput(const aps::EditorInputEvent& event) override {
        return editorOpen_ && api_->editorInput(event).valueOr(false);
    }
    void editorWindowResized(uint32_t width, uint32_t height) override {
        const double scale = editorScale();
        if (editorOpen_ && api_->setEditorSize(aps::toLogicalUnits(width, scale), aps::toLogicalUnits(height, scale)).valueOr(false))
            refreshEditorInfo();
    }
    void editorDpiChanged(double scale) override {
        osScale_ = scale;
        if (hostScale_ <= 0) applyEditorScale();
    }

    // The UI scale: the host's if it gave one (gui.set_scale), otherwise the OS's for the editor window.
    double editorScale() const { return hostScale_ > 0 ? hostScale_ : osScale_; }

    // The open editor follows a new scale: it redraws at the scale, its window gets its logical size at the scale,
    // and the host is asked to resize (it then calls set_size).
    void applyEditorScale() {
        const double scale = editorScale();
        if (!editorOpen_ || scale == appliedScale_) return;
        appliedScale_ = scale;
        api_->setEditorScale(scale);
        if (editorFullscreen()) return; // the window keeps the monitor's size
        const uint32_t width = aps::toWindowPixels(editorInfo_.width, scale);
        const uint32_t height = aps::toWindowPixels(editorInfo_.height, scale);
        editorWindow_->setSize(width, height);
        if (hostGui_) hostGui_->request_resize(host_, width, height);
    }
    bool editorFullscreen() const { return editorWindow_ && editorWindow_->isFullscreen(); }

    static bool guiIsApiSupported(const clap_plugin_t* p, const char* api, bool isFloating) {
        return self(p)->guiApiSupported(api, isFloating);
    }
    static bool guiGetPreferredApi(const clap_plugin_t* p, const char** api, bool* isFloating) {
        const char* ours = clapWindowApi(aps::EditorWindow::platformWindowType());
        if (!api || !isFloating || !ours || !self(p)->hasEditor()) return false;
        *api = ours;
        *isFloating = false;
        return true;
    }
    static bool guiCreate(const clap_plugin_t* p, const char* api, bool isFloating) {
        auto* s = self(p);
        if (!s->guiApiSupported(api, isFloating) || s->guiCreated_) return false;
        s->refreshEditorInfo();
        s->guiCreated_ = true;
        s->editorVisible_ = true; // an embedded window shows with its parent unless the host hides it
        return true;
    }
    static void guiDestroy(const clap_plugin_t* p) { self(p)->destroyEditor(); }
    // Sizes are window pixels; the host's scale overrides the OS's (the window's DPI).
    static bool guiSetScale(const clap_plugin_t* p, double scale) {
        auto* s = self(p);
        if (!(scale > 0)) return false;
        s->hostScale_ = scale;
        s->applyEditorScale();
        return true;
    }
    static bool guiGetSize(const clap_plugin_t* p, uint32_t* width, uint32_t* height) {
        auto* s = self(p);
        if (!s->guiCreated_ || !width || !height) return false;
        if (s->editorFullscreen()) { // the host's view keeps the windowed size
            s->editorWindow_->windowedSize(*width, *height);
            return true;
        }
        s->refreshEditorInfo();
        *width = aps::toWindowPixels(s->editorInfo_.width, s->editorScale());
        *height = aps::toWindowPixels(s->editorInfo_.height, s->editorScale());
        return true;
    }
    static bool guiCanResize(const clap_plugin_t* p) { return self(p)->editorResizable(); }
    static bool guiGetResizeHints(const clap_plugin_t* p, clap_gui_resize_hints_t* hints) {
        if (!hints || !self(p)->editorResizable()) return false;
        *hints = clap_gui_resize_hints_t{true, true, false, 1, 1};
        return true;
    }
    static bool guiAdjustSize(const clap_plugin_t* p, uint32_t* width, uint32_t* height) {
        auto* s = self(p);
        if (!width || !height) return false;
        if (!s->editorResizable()) {
            *width = aps::toWindowPixels(s->editorInfo_.width, s->editorScale());
            *height = aps::toWindowPixels(s->editorInfo_.height, s->editorScale());
        }
        return true;
    }
    static bool guiSetSize(const clap_plugin_t* p, uint32_t width, uint32_t height) {
        auto* s = self(p);
        if (!s->guiCreated_ || s->editorFullscreen()) return false;
        const double scale = s->editorScale();
        const uint32_t units = aps::toLogicalUnits(width, scale), unitsHigh = aps::toLogicalUnits(height, scale);
        if (units != s->editorInfo_.width || unitsHigh != s->editorInfo_.height) {
            if (!s->editorResizable() || !s->api_->setEditorSize(units, unitsHigh).valueOr(false)) return false;
            s->refreshEditorInfo();
        }
        if (s->editorWindow_)
            s->editorWindow_->setSize(aps::toWindowPixels(s->editorInfo_.width, scale), aps::toWindowPixels(s->editorInfo_.height, scale));
        return true;
    }
    static bool guiSetParent(const clap_plugin_t* p, const clap_window_t* window) {
        auto* s = self(p);
        if (!s->guiCreated_ || !window || !s->guiApiSupported(window->api, false)) return false;
        const intptr_t parent = !std::strcmp(window->api, CLAP_WINDOW_API_X11)
                                    ? static_cast<intptr_t>(window->x11)
                                    : reinterpret_cast<intptr_t>(window->ptr); // win32 / cocoa
        return s->openEditor(parent);
    }
    static bool guiSetTransient(const clap_plugin_t*, const clap_window_t*) { return false; }
    static void guiSuggestTitle(const clap_plugin_t*, const char*) {}
    static bool guiShow(const clap_plugin_t* p) { return self(p)->setEditorVisible(true); }
    static bool guiHide(const clap_plugin_t* p) { return self(p)->setEditorVisible(false); }

    struct Param {
        clap_id id;
        aps::ParamInfo info;
    };

    clap_plugin_t clap_{};
    const clap_host_t* host_;
    const clap_host_log_t* hostLog_ = nullptr;
    const clap_host_gui_t* hostGui_ = nullptr;
    const clap_host_params_t* hostParams_ = nullptr;
    const clap_host_state_t* hostState_ = nullptr;

    std::unique_ptr<aps::PluginApi> api_;
    aps::PluginInfo info_;
    std::vector<aps::PortInfo> inPorts_, outPorts_;
    std::vector<Param> params_;
    std::unordered_map<clap_id, uint32_t> idToIndex_; // built in init, read-only afterwards
    aps::SampleFormat format_ = aps::SampleFormat::Float32;
    double sampleRate_ = 48000;
    std::atomic<bool> active_{false};

    // Preallocated per-block storage (audio thread).
    std::vector<aps_audio_port_buffers> inBuffers_, outBuffers_;
    std::array<aps::AudioEvent, 1024> events_{};
    std::array<aps::AudioEvent, 256> outEvents_{};

    SpscQueue<Edit, 512> edits_;
    std::vector<double> pending_;          // host values set while inactive (NaN = none)
    std::atomic<bool> havePending_{false};

    std::unique_ptr<std::atomic<double>[]> values_;   // current normalized value per parameter (get_value)
    std::unique_ptr<std::atomic<bool>[]> valueDirty_; // changed by the host; edit value not yet updated
    std::atomic<bool> valuesNeedSync_{false};
    std::atomic<bool> callbackRequested_{false};

    // Editor (main thread).
    aps::EditorInfo editorInfo_{}; // sizes in logical units
    double hostScale_ = 0;         // from gui.set_scale (0: none)
    double osScale_ = aps::EditorWindow::platformScale(0);
    double appliedScale_ = 0;      // the open editor's scale
    bool guiCreated_ = false;
    bool editorVisible_ = false;
    bool editorOpen_ = false;
    std::unique_ptr<aps::EditorWindow> editorWindow_;
    const clap_host_timer_support_t* hostTimer_ = nullptr;
    const clap_host_posix_fd_support_t* hostFd_ = nullptr;
    clap_id editorTimerId_ = CLAP_INVALID_ID;
    int editorFd_ = -1;
};

const clap_plugin_audio_ports_t Instance::audioPorts_{&Instance::portsCount, &Instance::portsGet};
const clap_plugin_note_ports_t Instance::notePorts_{&Instance::notePortsCount, &Instance::notePortsGet};
const clap_plugin_params_t Instance::params_ext_{&Instance::paramsCount,      &Instance::paramsGetInfo,
                                                 &Instance::paramsGetValue,   &Instance::paramsValueToText,
                                                 &Instance::paramsTextToValue, &Instance::paramsFlush};
const clap_plugin_state_t Instance::state_{&Instance::stateSave, &Instance::stateLoad};
const clap_plugin_gui_t Instance::gui_{&Instance::guiIsApiSupported, &Instance::guiGetPreferredApi,
                                      &Instance::guiCreate,         &Instance::guiDestroy,
                                      &Instance::guiSetScale,       &Instance::guiGetSize,
                                      &Instance::guiCanResize,      &Instance::guiGetResizeHints,
                                      &Instance::guiAdjustSize,     &Instance::guiSetSize,
                                      &Instance::guiSetParent,      &Instance::guiSetTransient,
                                      &Instance::guiSuggestTitle,   &Instance::guiShow,
                                      &Instance::guiHide};
const clap_plugin_timer_support_t Instance::timerSupport_{&Instance::onTimer};
const clap_plugin_posix_fd_support_t Instance::fdSupport_{&Instance::onFd};

// ---------------------------------------------------------------------------
// Factory and entry point.
// ---------------------------------------------------------------------------
const clap_plugin_factory_t kFactory{
    [](const clap_plugin_factory_t*) -> uint32_t {
        std::lock_guard lock(g.mutex);
        return descriptorLocked() ? 1u : 0u;
    },
    [](const clap_plugin_factory_t*, uint32_t index) -> const clap_plugin_descriptor_t* {
        if (index != 0) return nullptr;
        std::lock_guard lock(g.mutex);
        return descriptorLocked();
    },
    [](const clap_plugin_factory_t*, const clap_host_t* host, const char* pluginId) -> const clap_plugin_t* {
        if (!host || !pluginId || !clap_version_is_compatible(host->clap_version)) return nullptr;
        {
            std::lock_guard lock(g.mutex);
            const auto* desc = descriptorLocked();
            if (!desc || std::strcmp(desc->id, pluginId) != 0) return nullptr;
        }
        try {
            return (new Instance(host))->clap();
        } catch (...) {
            return nullptr;
        }
    },
};

bool entryInit(const char* pluginPath) {
    try {
        std::lock_guard lock(g.mutex);
        if (!g.clapPath.empty()) return true;
        g.clapPath = fs::path(std::u8string_view(reinterpret_cast<const char8_t*>(pluginPath)));
        g.dir = g.clapPath.parent_path();
        g.pluginAssembly = g.dir / (g.clapPath.stem().string() + ".dll");
        return true;
    } catch (...) {
        return false;
    }
}

void entryDeinit() {
    // The .NET runtime cannot be unloaded; it stays for the life of the process. Instances are gone by now.
}

const void* entryGetFactory(const char* factoryId) {
    return factoryId && !std::strcmp(factoryId, CLAP_PLUGIN_FACTORY_ID) ? &kFactory : nullptr;
}

} // namespace

extern "C" {
#ifdef _WIN32
__declspec(dllexport)
#else
__attribute__((visibility("default")))
#endif
extern const clap_plugin_entry_t clap_entry;
const clap_plugin_entry_t clap_entry = {CLAP_VERSION, entryInit, entryDeinit, entryGetFactory};
}
