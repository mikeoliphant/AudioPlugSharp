// ClapTestHost.cpp - a small CLAP host for automated tests of the AudioPlugSharp CLAP adapter. It also opens
// the editors of plugins with the gui extension in a window of its own and drives them (gui::test; Win32 or
// X11, which needs a display).
//
// Loads each .clap given on the command line (default: the example bundles next to this executable) the way
// a DAW would, exercises the plugin lifecycle, and checks results. All modules stay loaded, so the run also
// covers several AudioPlugSharp plugins (each with its own bridge copy) sharing one .NET runtime.
#include <clap/clap.h>

#include "TestWindow.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <memory>
#include <thread>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <dlfcn.h>
#include <poll.h>
#endif

namespace fs = std::filesystem;

namespace {

int gFailures = 0;

void check(bool ok, const char* what) {
    std::printf("    [%s] %s\n", ok ? " ok " : "FAIL", what);
    if (!ok) ++gFailures;
}

using aps_test::executableDir;

const clap_plugin_entry_t* loadEntry(const fs::path& path) {
#ifdef _WIN32
    HMODULE lib = LoadLibraryW(path.c_str());
    if (!lib) return nullptr;
    return reinterpret_cast<const clap_plugin_entry_t*>(GetProcAddress(lib, "clap_entry"));
#else
    void* lib = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!lib) {
        std::printf("  dlopen: %s\n", dlerror());
        return nullptr;
    }
    return static_cast<const clap_plugin_entry_t*>(dlsym(lib, "clap_entry"));
#endif
}

// ---------------------------------------------------------------------------
// Host side: log, params (request_flush), state (mark_dirty), and a run loop (timer-support,
// posix-fd-support) that service() drives. CLAP host callbacks don't say which plugin calls, so a plugin that
// registers timers needs a TestHost of its own.
// ---------------------------------------------------------------------------
struct TestHost {
    clap_host_t host{};
    std::atomic<int> flushRequests{0}, dirtyMarks{0};
    // request_flush from the editor's edits must come on the main thread, also from editors that render on a
    // thread of their own (the call is allowed from other threads, but the adapter's edit queue is main-thread).
    const std::thread::id mainThread = std::this_thread::get_id();
    std::atomic<int> offThreadFlushes{0};
    std::atomic<bool> callbackRequested{false};
    // gui: the plugin's resize requests (it asks after a scale change).
    int resizeRequests = 0;
    uint32_t requestedWidth = 0, requestedHeight = 0;

    struct Timer {
        clap_id id;
        std::chrono::milliseconds period;
        std::chrono::steady_clock::time_point next;
    };
    std::vector<Timer> timers;
    std::vector<int> fds;
    clap_id nextTimerId = 1;

    TestHost() {
        host.clap_version = CLAP_VERSION;
        host.host_data = this;
        host.name = "AudioPlugSharp ClapTestHost";
        host.vendor = "AudioPlugSharp";
        host.url = "";
        host.version = "1.0";
        host.get_extension = [](const clap_host_t*, const char* id) -> const void* {
            if (!std::strcmp(id, CLAP_EXT_LOG)) return &kLog;
            if (!std::strcmp(id, CLAP_EXT_PARAMS)) return &kParams;
            if (!std::strcmp(id, CLAP_EXT_STATE)) return &kState;
            if (!std::strcmp(id, CLAP_EXT_TIMER_SUPPORT)) return &kTimer;
            if (!std::strcmp(id, CLAP_EXT_GUI)) return &kGui;
#ifndef _WIN32
            if (!std::strcmp(id, CLAP_EXT_POSIX_FD_SUPPORT)) return &kFd;
#endif
            return nullptr;
        };
        host.request_restart = [](const clap_host_t*) {};
        host.request_process = [](const clap_host_t*) {};
        host.request_callback = [](const clap_host_t* h) { self(h)->callbackRequested = true; };
    }

    static TestHost* self(const clap_host_t* h) { return static_cast<TestHost*>(h->host_data); }

    static void log(const clap_host_t*, clap_log_severity severity, const char* msg) {
        static const char* names[] = {"debug", "info", "warn", "error", "fatal", "host-misbehaving", "plugin-misbehaving"};
        std::printf("    [plugin %s] %s\n", severity >= 0 && severity <= 6 ? names[severity] : "?", msg);
    }
    static void rescan(const clap_host_t*, clap_param_rescan_flags) {}
    static void clear(const clap_host_t*, clap_id, clap_param_clear_flags) {}
    static void requestFlush(const clap_host_t* h) {
        self(h)->flushRequests++;
        if (std::this_thread::get_id() != self(h)->mainThread) self(h)->offThreadFlushes++;
    }
    static void markDirty(const clap_host_t* h) { self(h)->dirtyMarks++; }

    static void resizeHintsChanged(const clap_host_t*) {}
    static bool requestResize(const clap_host_t* h, uint32_t width, uint32_t height) {
        auto* s = self(h);
        s->resizeRequests++;
        s->requestedWidth = width;
        s->requestedHeight = height;
        return true; // the test calls set_size, as a host would
    }
    static bool requestShow(const clap_host_t*) { return false; }
    static bool requestHide(const clap_host_t*) { return false; }
    static void closed(const clap_host_t*, bool) {}

    static bool registerTimer(const clap_host_t* h, uint32_t periodMs, clap_id* id) {
        auto* s = self(h);
        if (!id) return false;
        *id = s->nextTimerId++;
        const auto period = std::chrono::milliseconds(std::max<uint32_t>(periodMs, 1));
        s->timers.push_back({*id, period, std::chrono::steady_clock::now() + period});
        return true;
    }
    static bool unregisterTimer(const clap_host_t* h, clap_id id) {
        auto& t = self(h)->timers;
        const auto n = t.size();
        t.erase(std::remove_if(t.begin(), t.end(), [&](const Timer& x) { return x.id == id; }), t.end());
        return t.size() != n;
    }
    static bool registerFd(const clap_host_t* h, int fd, clap_posix_fd_flags_t) {
        self(h)->fds.push_back(fd);
        return true;
    }
    static bool modifyFd(const clap_host_t*, int, clap_posix_fd_flags_t) { return true; }
    static bool unregisterFd(const clap_host_t* h, int fd) {
        auto& f = self(h)->fds;
        const auto n = f.size();
        f.erase(std::remove(f.begin(), f.end(), fd), f.end());
        return f.size() != n;
    }

    // Main thread: due timers and readable fds -> the plugin; then the main-thread callback if requested.
    void service(const clap_plugin_t* plugin) {
        const auto now = std::chrono::steady_clock::now();
        std::vector<clap_id> due;
        for (auto& t : timers)
            if (now >= t.next) {
                due.push_back(t.id);
                t.next = std::max(t.next + t.period, now);
            }
        if (!due.empty())
            if (const auto* ts = static_cast<const clap_plugin_timer_support_t*>(plugin->get_extension(plugin, CLAP_EXT_TIMER_SUPPORT)))
                for (clap_id id : due) ts->on_timer(plugin, id); // may unregister timers; iterate a copy
#ifndef _WIN32
        if (!fds.empty()) {
            std::vector<pollfd> polls;
            for (int fd : fds) polls.push_back({fd, POLLIN, 0});
            if (poll(polls.data(), polls.size(), 0) > 0)
                if (const auto* fs = static_cast<const clap_plugin_posix_fd_support_t*>(plugin->get_extension(plugin, CLAP_EXT_POSIX_FD_SUPPORT)))
                    for (const auto& p : polls)
                        if (p.revents & POLLIN) fs->on_fd(plugin, p.fd, CLAP_POSIX_FD_READ);
        }
#endif
        if (callbackRequested.exchange(false)) plugin->on_main_thread(plugin);
    }

    static const clap_host_log_t kLog;
    static const clap_host_params_t kParams;
    static const clap_host_state_t kState;
    static const clap_host_timer_support_t kTimer;
    static const clap_host_posix_fd_support_t kFd;
    static const clap_host_gui_t kGui;
};

const clap_host_log_t TestHost::kLog{&TestHost::log};
const clap_host_params_t TestHost::kParams{&TestHost::rescan, &TestHost::clear, &TestHost::requestFlush};
const clap_host_state_t TestHost::kState{&TestHost::markDirty};
const clap_host_timer_support_t TestHost::kTimer{&TestHost::registerTimer, &TestHost::unregisterTimer};
const clap_host_posix_fd_support_t TestHost::kFd{&TestHost::registerFd, &TestHost::modifyFd, &TestHost::unregisterFd};
const clap_host_gui_t TestHost::kGui{&TestHost::resizeHintsChanged, &TestHost::requestResize, &TestHost::requestShow,
                                     &TestHost::requestHide, &TestHost::closed};

// ---------------------------------------------------------------------------
// Event lists and streams.
// ---------------------------------------------------------------------------
struct InEvents {
    std::vector<std::vector<uint8_t>> storage;
    clap_input_events_t list{};

    InEvents() {
        list.ctx = this;
        list.size = [](const clap_input_events_t* l) { return static_cast<uint32_t>(static_cast<InEvents*>(l->ctx)->storage.size()); };
        list.get = [](const clap_input_events_t* l, uint32_t i) {
            return reinterpret_cast<const clap_event_header_t*>(static_cast<InEvents*>(l->ctx)->storage[i].data());
        };
    }
    template <typename E>
    void add(const E& e) {
        storage.emplace_back(reinterpret_cast<const uint8_t*>(&e), reinterpret_cast<const uint8_t*>(&e) + sizeof(E));
    }
    void clear() { storage.clear(); }
};

struct OutEvents {
    std::vector<std::vector<uint8_t>> storage;
    clap_output_events_t list{};

    OutEvents() {
        list.ctx = this;
        list.try_push = [](const clap_output_events_t* l, const clap_event_header_t* h) {
            auto* s = static_cast<OutEvents*>(l->ctx);
            s->storage.emplace_back(reinterpret_cast<const uint8_t*>(h), reinterpret_cast<const uint8_t*>(h) + h->size);
            return true;
        };
    }
    int count(uint16_t type) const {
        int n = 0;
        for (auto& e : storage) n += reinterpret_cast<const clap_event_header_t*>(e.data())->type == type;
        return n;
    }
    void clear() { storage.clear(); }
};

clap_event_param_value_t paramEvent(uint32_t time, clap_id id, double value) {
    clap_event_param_value_t e{};
    e.header = {sizeof(e), time, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_PARAM_VALUE, 0};
    e.param_id = id;
    e.note_id = -1;
    e.port_index = e.channel = e.key = -1;
    e.value = value;
    return e;
}

clap_event_note_t noteEvent(uint16_t type, uint32_t time, int16_t key, double velocity) {
    clap_event_note_t e{};
    e.header = {sizeof(e), time, CLAP_CORE_EVENT_SPACE_ID, type, 0};
    e.note_id = -1;
    e.port_index = 0;
    e.channel = 0;
    e.key = key;
    e.velocity = velocity;
    return e;
}

struct Buffer {
    std::vector<uint8_t> bytes;
    int64_t pos = 0;
};

// ---------------------------------------------------------------------------
// Audio buffers for all ports (float32).
// ---------------------------------------------------------------------------
struct Audio {
    std::vector<std::vector<std::vector<float>>> in, out;
    std::vector<std::vector<float*>> inPtrs, outPtrs;
    std::vector<clap_audio_buffer_t> inBufs, outBufs;

    Audio(const clap_plugin_t* plugin, const clap_plugin_audio_ports_t* ports, uint32_t frames) {
        for (int dir = 0; dir < 2; ++dir) {
            const bool isInput = dir == 0;
            auto& chans = isInput ? in : out;
            auto& ptrs = isInput ? inPtrs : outPtrs;
            auto& bufs = isInput ? inBufs : outBufs;
            const uint32_t count = ports ? ports->count(plugin, isInput) : 0;
            chans.resize(count);
            ptrs.resize(count);
            for (uint32_t p = 0; p < count; ++p) {
                clap_audio_port_info_t info{};
                ports->get(plugin, p, isInput, &info);
                chans[p].assign(info.channel_count, std::vector<float>(frames));
                for (auto& c : chans[p]) ptrs[p].push_back(c.data());
            }
            for (uint32_t p = 0; p < count; ++p) {
                clap_audio_buffer_t b{};
                b.data32 = ptrs[p].data();
                b.channel_count = static_cast<uint32_t>(ptrs[p].size());
                bufs.push_back(b);
            }
        }
    }
    void sine(double freq, double rate, float amp) {
        for (auto& port : in)
            for (auto& ch : port)
                for (size_t i = 0; i < ch.size(); ++i) ch[i] = amp * float(std::sin(2 * 3.14159265358979 * freq * double(i) / rate));
    }
    float peak() const {
        float p = 0;
        for (auto& port : out)
            for (auto& ch : port)
                for (float s : ch) {
                    if (!std::isfinite(s)) return NAN;
                    p = std::max(p, std::fabs(s));
                }
        return p;
    }
};

// ---------------------------------------------------------------------------
// One plugin: the full lifecycle.
// ---------------------------------------------------------------------------
const clap_plugin_t* testPlugin(const fs::path& path, TestHost& host, const clap_plugin_entry_t*& entryOut) {
    std::printf("\n== %s ==\n", path.filename().string().c_str());
    const auto* entry = loadEntry(path);
    check(entry != nullptr, "clap_entry exported");
    if (!entry) return nullptr;
    entryOut = entry;
    check(clap_version_is_compatible(entry->clap_version), "CLAP version compatible");
    const auto u8 = path.u8string();
    check(entry->init(reinterpret_cast<const char*>(u8.c_str())), "entry init");

    const auto* factory = static_cast<const clap_plugin_factory_t*>(entry->get_factory(CLAP_PLUGIN_FACTORY_ID));
    check(factory != nullptr, "plugin factory");
    if (!factory) return nullptr;
    check(factory->get_plugin_count(factory) == 1, "one plugin in the factory");
    const auto* desc = factory->get_plugin_descriptor(factory, 0);
    check(desc != nullptr, "descriptor");
    if (!desc) return nullptr;
    std::printf("    %s | %s | %s | v%s | features:", desc->id, desc->name, desc->vendor, desc->version);
    for (auto f = desc->features; f && *f; ++f) std::printf(" %s", *f);
    std::printf("\n");

    const auto* plugin = factory->create_plugin(factory, &host.host, desc->id);
    check(plugin != nullptr, "create_plugin");
    if (!plugin) return nullptr;
    check(plugin->init(plugin), "plugin init");

    const auto* ports = static_cast<const clap_plugin_audio_ports_t*>(plugin->get_extension(plugin, CLAP_EXT_AUDIO_PORTS));
    const auto* notes = static_cast<const clap_plugin_note_ports_t*>(plugin->get_extension(plugin, CLAP_EXT_NOTE_PORTS));
    const auto* params = static_cast<const clap_plugin_params_t*>(plugin->get_extension(plugin, CLAP_EXT_PARAMS));
    const auto* state = static_cast<const clap_plugin_state_t*>(plugin->get_extension(plugin, CLAP_EXT_STATE));
    check(ports && notes && params && state, "extensions: audio-ports, note-ports, params, state");
    if (!ports || !notes || !params || !state) return plugin;

    for (int dir = 0; dir < 2; ++dir)
        for (uint32_t i = 0; i < ports->count(plugin, dir == 0); ++i) {
            clap_audio_port_info_t info{};
            ports->get(plugin, i, dir == 0, &info);
            std::printf("    %s port %u: %s, %u ch, %s%s\n", dir == 0 ? "input " : "output", i, info.name,
                        info.channel_count, info.port_type ? info.port_type : "-",
                        (info.flags & CLAP_AUDIO_PORT_IS_MAIN) ? ", main" : "");
        }
    check(notes->count(plugin, true) == 1 && notes->count(plugin, false) == 1, "one note port each way");

    const uint32_t paramCount = params->count(plugin);
    std::vector<clap_param_info_t> infos(paramCount);
    for (uint32_t i = 0; i < paramCount; ++i) {
        params->get_info(plugin, i, &infos[i]);
        double value = -1;
        params->get_value(plugin, infos[i].id, &value);
        char text[64] = {}, defText[64] = {};
        params->value_to_text(plugin, infos[i].id, value, text, sizeof(text));
        params->value_to_text(plugin, infos[i].id, infos[i].default_value, defText, sizeof(defText));
        std::printf("    param %u: id %08x \"%s\" [%g..%g] default %.3f (%s), value %.3f (%s)\n", i, infos[i].id,
                    infos[i].name, infos[i].min_value, infos[i].max_value, infos[i].default_value, defText, value, text);
    }

    // A list of choices (UILayout Test's "Mode": Clean, Crunch, Lead) is stepped, as whole numbers 0 to 2 with names.
    if (std::strcmp(desc->name, "UILayout Test") == 0) {
        check(paramCount == 2, "UILayout Test has two parameters");
        if (paramCount == 2) {
            const clap_param_info_t& mode = infos[1];
            check((mode.flags & CLAP_PARAM_IS_STEPPED) && (mode.flags & CLAP_PARAM_IS_ENUM),
                  "a list of choices is stepped and an enum");
            check(mode.min_value == 0 && mode.max_value == 2 && mode.default_value == 0, "its range is 0 to 2, default 0");
            char text[64] = {};
            params->value_to_text(plugin, mode.id, 1, text, sizeof(text));
            check(std::strcmp(text, "Crunch") == 0, "its values have the choices' names");
            InEvents in;
            OutEvents out;
            in.add(paramEvent(0, mode.id, 2));
            params->flush(plugin, &in.list, &out.list);
            double value = -1;
            params->get_value(plugin, mode.id, &value);
            check(value == 2, "a host value of 2 reads back as 2");
            in.clear();
            in.add(paramEvent(0, mode.id, 0));
            params->flush(plugin, &in.list, &out.list);
        }
    }

    // Parameters set while inactive must reach the DSP once processing starts.
    if (paramCount > 0) {
        InEvents in;
        OutEvents out;
        in.add(paramEvent(0, infos[0].id, 0.5));
        params->flush(plugin, &in.list, &out.list);
        double value = -1;
        params->get_value(plugin, infos[0].id, &value);
        check(std::fabs(value - 0.5) < 1e-9, "flush while inactive updates the value");
    }

    const double rate = 48000;
    const uint32_t frames = 256;
    check(plugin->activate(plugin, rate, 32, frames), "activate");
    check(plugin->start_processing(plugin), "start_processing");

    Audio audio(plugin, ports, frames);
    audio.sine(220, rate, 0.5f);
    InEvents in;
    OutEvents out;
    clap_process_t proc{};
    proc.frames_count = frames;
    proc.audio_inputs = audio.inBufs.data();
    proc.audio_outputs = audio.outBufs.data();
    proc.audio_inputs_count = static_cast<uint32_t>(audio.inBufs.size());
    proc.audio_outputs_count = static_cast<uint32_t>(audio.outBufs.size());
    proc.in_events = &in.list;
    proc.out_events = &out.list;
    auto run = [&](int blocks) {
        bool ok = true;
        for (int b = 0; b < blocks; ++b) {
            ok = plugin->process(plugin, &proc) == CLAP_PROCESS_CONTINUE && ok;
            proc.steady_time += frames;
            in.clear();
        }
        return ok;
    };

    const bool hasInput = !audio.inBufs.empty();
    // The exact checks below need a gain (to +12 dB) as the first parameter, as most examples have
    const bool gainFirst = paramCount > 0 && std::strcmp(infos[0].name, "Gain") == 0;
    check(run(10), "process");
    const float p1 = audio.peak();
    std::printf("    peak after inactive flush to 0.5: %.3f\n", p1);
    if (hasInput && gainFirst) {
        check(std::fabs(p1 - 0.249f) < 0.01f, "inactive flush value reached the DSP (0.5 -> -6.1 dB)");
        in.add(paramEvent(0, infos[0].id, 1.0));
        run(10);
        const float p2 = audio.peak();
        std::printf("    peak after param event 1.0: %.3f\n", p2);
        check(p2 > 1.9f, "parameter event reaches the DSP (1.0 -> +12 dB)");
        // What Reaper's generic UI does: poll get_value/value_to_text while the host automates the parameter.
        check(host.callbackRequested.load(), "host value change requests a main-thread callback");
        if (host.callbackRequested.exchange(false)) plugin->on_main_thread(plugin);
        double v = -1;
        char text[64] = {};
        params->get_value(plugin, infos[0].id, &v);
        params->value_to_text(plugin, infos[0].id, v, text, sizeof(text));
        std::printf("    get_value after the event: %.3f (%s)\n", v, text);
        check(std::fabs(v - 1.0) < 1e-9 && std::string(text) == "12.0dB", "get_value follows host parameter events");
    } else if (hasInput) {
        // Another effect (ParameterEditorExample): it passes sound
        check(p1 > 0.01f, "the effect passes sound");
    } else {
        in.add(noteEvent(CLAP_EVENT_NOTE_ON, 0, 60, 0.8));
        run(100);
        const float on = audio.peak();
        in.add(noteEvent(CLAP_EVENT_NOTE_OFF, 0, 60, 0.0));
        run(400);
        const float off = audio.peak();
        std::printf("    note on: peak %.3f, note off: peak %.3f\n", on, off);
        check(on > 0.1f && off < 0.001f, "note on/off produce and stop sound");
        // MIDI dialect too.
        clap_event_midi_t midi{};
        midi.header = {sizeof(midi), 0, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_MIDI, 0};
        midi.data[0] = 0x90; midi.data[1] = 64; midi.data[2] = 100;
        in.add(midi);
        run(100);
        check(audio.peak() > 0.1f, "MIDI note on produces sound");
    }

    // Flush while processing goes through the bridge's events-only path.
    if (paramCount > 0) {
        InEvents fin;
        OutEvents fout;
        fin.add(paramEvent(0, infos[0].id, 0.75));
        params->flush(plugin, &fin.list, &fout.list);
        if (host.callbackRequested.exchange(false)) plugin->on_main_thread(plugin);
        double v = -1;
        params->get_value(plugin, infos[0].id, &v);
        check(std::fabs(v - 0.75) < 1e-9, "get_value follows a flush while active");
    }

    plugin->stop_processing(plugin);
    plugin->deactivate(plugin);

    // State round trip through CLAP streams.
    Buffer buf;
    clap_ostream_t os{&buf, [](const clap_ostream_t* s, const void* data, uint64_t size) -> int64_t {
                          auto* b = static_cast<Buffer*>(s->ctx);
                          b->bytes.insert(b->bytes.end(), static_cast<const uint8_t*>(data), static_cast<const uint8_t*>(data) + size);
                          return static_cast<int64_t>(size);
                      }};
    check(state->save(plugin, &os) && !buf.bytes.empty(), "state save");
    clap_istream_t is{&buf, [](const clap_istream_t* s, void* data, uint64_t size) -> int64_t {
                          auto* b = static_cast<Buffer*>(s->ctx);
                          const int64_t n = std::min<int64_t>(int64_t(size), int64_t(b->bytes.size()) - b->pos);
                          std::memcpy(data, b->bytes.data() + b->pos, size_t(n));
                          b->pos += n;
                          return n;
                      }};
    const int flushesBefore = host.flushRequests.load();
    check(state->load(plugin, &is), "state load");
    check(host.flushRequests.load() == flushesBefore, "state load does not report edits to the host");
    if (paramCount > 0) {
        double v = -1;
        params->get_value(plugin, infos[0].id, &v);
        check(std::fabs(v - 0.75) < 1e-9, "get_value after state load is the saved value");
    }
    std::printf("    state: %zu bytes\n", buf.bytes.size());
    return plugin;
}

// ---------------------------------------------------------------------------
// GUI: two editors of one plugin side by side in one top-level window, as a DAW shows two instances.
// Checks that both render (window-only capture, not the screen), that a synthetic slider drag reaches the
// host as one gesture with values (and get_value follows), that a host value change reaches the editor, and
// that closing removes the editor window. Expects the UILayoutTestPlugin editor: 320x160, background
// (32,36,44), a gain slider (black track) and a green level meter; positions are found in the capture.
// Win32: posted mouse messages and PrintWindow. X11: XSendEvent and XGetImage. Each instance has its own
// TestHost, whose run loop (timer-support, posix-fd-support) drives X11 editors.
// ---------------------------------------------------------------------------
namespace gui {

using aps_test::Capture;
using aps_test::Key;
using aps_test::Mouse;
using aps_test::saveBmp;

// The DAW window: shared with the VST3 test host; CLAP wraps its slot handles in clap_window_t.
struct DawWindow : aps_test::DawWindow {
#ifdef _WIN32
    static constexpr const char* kApi = CLAP_WINDOW_API_WIN32;
#else
    static constexpr const char* kApi = CLAP_WINDOW_API_X11;
#endif
    bool open(int width, int height) { return aps_test::DawWindow::open(width, height, "ClapTestHost - two editors"); }
    clap_window_t addSlot(int x, int y, int width, int height) {
        const intptr_t handle = aps_test::DawWindow::addSlot(x, y, width, height);
        clap_window_t window{kApi, {}};
#ifdef _WIN32
        window.win32 = reinterpret_cast<void*>(handle);
#else
        window.x11 = static_cast<clap_xwnd>(handle);
#endif
        return window;
    }
};

// One plugin instance with its own host, editor and audio, processed on this thread while the UI runs.
struct Editor {
    std::unique_ptr<TestHost> host = std::make_unique<TestHost>();
    const clap_plugin_t* plugin = nullptr;
    const clap_plugin_gui_t* gui = nullptr;
    const clap_plugin_params_t* params = nullptr;
    clap_id paramId = 0;
    clap_window_t slot{};
    std::unique_ptr<Audio> audio;
    clap_process_t proc{};
    InEvents in;
    OutEvents out;

    void process() {
        if (!plugin) return;
        plugin->process(plugin, &proc);
        proc.steady_time += proc.frames_count;
        in.clear();
    }
    double value() const {
        double v = -1;
        params->get_value(plugin, paramId, &v);
        return v;
    }
};

void pump(int milliseconds, DawWindow& daw, std::vector<Editor>& editors) {
    const auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    do {
        daw.pumpEvents();
        for (auto& e : editors) {
            e.process();
            e.host->service(e.plugin);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    } while (std::chrono::steady_clock::now() < end);
}

void test(const clap_plugin_entry_t* entry) {
    const auto* factory = static_cast<const clap_plugin_factory_t*>(entry->get_factory(CLAP_PLUGIN_FACTORY_ID));
    const auto* desc = factory ? factory->get_plugin_descriptor(factory, 0) : nullptr;
    if (!desc) return;
    std::printf("\n== GUI (%s): two %s editors in one window ==\n", DawWindow::kApi, desc->name);

    constexpr double kRate = 48000;
    constexpr uint32_t kFrames = 256;
    std::vector<Editor> editors(2);
    uint32_t width = 0, height = 0;
    for (auto& e : editors) {
        e.plugin = factory->create_plugin(factory, &e.host->host, desc->id);
        if (!e.plugin || !e.plugin->init(e.plugin)) { check(false, "create and init an instance"); return; }
        e.gui = static_cast<const clap_plugin_gui_t*>(e.plugin->get_extension(e.plugin, CLAP_EXT_GUI));
        e.params = static_cast<const clap_plugin_params_t*>(e.plugin->get_extension(e.plugin, CLAP_EXT_PARAMS));
        clap_param_info_t info{};
        e.params->get_info(e.plugin, 0, &info);
        e.paramId = info.id;
        e.plugin->activate(e.plugin, kRate, 32, kFrames);
        e.plugin->start_processing(e.plugin);
        const auto* ports = static_cast<const clap_plugin_audio_ports_t*>(e.plugin->get_extension(e.plugin, CLAP_EXT_AUDIO_PORTS));
        e.audio = std::make_unique<Audio>(e.plugin, ports, kFrames);
        e.audio->sine(220, kRate, 0.5f);
        e.proc.frames_count = kFrames;
        e.proc.audio_inputs = e.audio->inBufs.data();
        e.proc.audio_outputs = e.audio->outBufs.data();
        e.proc.audio_inputs_count = uint32_t(e.audio->inBufs.size());
        e.proc.audio_outputs_count = uint32_t(e.audio->outBufs.size());
        e.proc.in_events = &e.in.list;
        e.proc.out_events = &e.out.list;
    }
    auto& a = editors[0];
    auto& b = editors[1];
    const auto shutdown = [&] {
        for (auto& e : editors) {
            if (!e.plugin) continue;
            if (e.gui) e.gui->destroy(e.plugin);
            e.plugin->stop_processing(e.plugin);
            e.plugin->deactivate(e.plugin);
            e.plugin->destroy(e.plugin);
            e.plugin = nullptr;
        }
    };
    check(a.gui && a.gui->is_api_supported(a.plugin, DawWindow::kApi, false), "gui: embedded window supported");
    check(a.gui && !a.gui->is_api_supported(a.plugin, DawWindow::kApi, true), "gui: floating not supported");
    if (!a.gui || !b.gui) { shutdown(); return; }
    for (auto& e : editors) {
        check(e.gui->create(e.plugin, DawWindow::kApi, false), "gui create");
        e.gui->get_size(e.plugin, &width, &height);
    }
    std::printf("    editor size %ux%u\n", width, height);
    // UILayoutTestPlugin's editor gets the detailed checks (layout, meter, drag, host -> editor); any other editor
    // the generic ones (it renders, closes and reopens).
    const bool uilayout = std::strcmp(desc->name, "UILayout Test") == 0;
    if (uilayout) check(width == 320 && height == 160, "gui get_size");
    else check(width > 0 && height > 0 && width <= 4096 && height <= 4096, "gui get_size");

    constexpr int kMargin = 10;
    const auto x0 = [&](int i) { return kMargin + i * (int(width) + kMargin); };
    DawWindow daw;
    if (!daw.open(3 * kMargin + 2 * int(width), 2 * kMargin + int(height))) {
        check(false, "open a top-level window");
        shutdown();
        return;
    }
    for (int i = 0; i < 2; ++i) {
        auto& e = editors[size_t(i)];
        e.slot = daw.addSlot(x0(i), kMargin, int(width), int(height));
        check(e.gui->set_parent(e.plugin, &e.slot), "gui set_parent (opens the editor)");
        check(e.gui->show(e.plugin), "gui show");
    }
    pump(800, daw, editors);

    // Something other than the slot's gray background was drawn over most of the editor's area.
    const auto renders = [&](const Capture& c, int i) {
        const int area = int(width * height);
        const int gray = c.countNear(x0(i), kMargin, x0(i) + int(width), kMargin + int(height), 128, 128, 128, 3);
        return area - gray > area / 2;
    };
    Capture first = daw.capture();
    for (int i = 0; i < 2; ++i) check(renders(first, i), "editor renders (window capture)");
    std::string captureName = std::string("clap-gui-") + desc->name + ".bmp";
    for (auto& ch : captureName) if (ch == ' ') ch = '_';
    const auto path = executableDir() / captureName;
    saveBmp(first, path);
    std::printf("    capture: %s\n", path.string().c_str());
    check(daw.hasEditor(0), "editor child window exists");

    int sliderY = -1;
    if (uilayout) {
    // The slider's track is the row that is mostly black (found in the capture, not assumed from the layout).
    const auto sliderRow = [&](const Capture& c, int i) {
        for (int y = kMargin; y < kMargin + int(height); ++y)
            if (c.countNear(x0(i), y, x0(i) + int(width), y + 1, 0, 0, 0, 8) > int(width) / 2) return y - kMargin;
        return -1;
    };
    for (int i = 0; i < 2; ++i) {
        const int background = first.countNear(x0(i), kMargin, x0(i) + int(width), kMargin + int(height), 32, 36, 44, 4);
        // LevelMeter's bar: low or hot color (the test signal peaks at -6 dB, its hot threshold)
        const int meterGreen = first.countNear(x0(i), kMargin, x0(i) + int(width), kMargin + int(height), 80, 190, 90, 12) +
                               first.countNear(x0(i), kMargin, x0(i) + int(width), kMargin + int(height), 255, 180, 60, 12);
        sliderY = sliderRow(first, i);
        std::printf("    editor %d: %d background pixels, %d meter pixels, slider at y %d\n", i, background, meterGreen, sliderY);
        check(background > int(width * height) / 3, "editor draws its background");
        check(meterGreen > 500, "level meter shows the processed audio");
        check(sliderY > 0, "slider found");
    }
    if (sliderY <= 0) sliderY = int(height) / 2;
    sliderY += 1; // the middle of the track

    // Editor -> host: press at the slider's left end and drag past its right end (outside the editor).
    for (auto& e : editors) e.out.clear();
    const double before = a.value(), otherBefore = b.value();
    daw.mouse(0, Mouse::Move, 40, sliderY);
    pump(60, daw, editors);
    daw.mouse(0, Mouse::Down, 40, sliderY);
    pump(60, daw, editors);
    for (int x = 60; x <= 320; x += 20) {
        daw.mouse(0, Mouse::Move, x, sliderY);
        pump(40, daw, editors);
    }
    daw.mouse(0, Mouse::Up, 320, sliderY);
    pump(200, daw, editors);
    const int begins = a.out.count(CLAP_EVENT_PARAM_GESTURE_BEGIN), ends = a.out.count(CLAP_EVENT_PARAM_GESTURE_END);
    const int values = a.out.count(CLAP_EVENT_PARAM_VALUE);
    const double after = a.value();
    std::printf("    drag: gesture begin %d, end %d, %d values; value %.3f -> %.3f\n", begins, ends, values, before, after);
    check(begins == 1 && ends == 1 && values >= 3, "slider drag reaches the host as one gesture with values");
    check(a.host->offThreadFlushes == 0, "edits reach the host on the main thread (request_flush)");
    check(after > 0.99, "get_value follows the editor (dragged to the maximum)");
    check(b.out.storage.empty() && b.value() == otherBefore, "the other instance is unaffected");

    }

    // Host -> editor (automation): set B's first parameter to two mid-range values (the ends can differ by
    // accident, e.g. a dial that draws NaN at -inf). The UILayout editor's slider row must change; for any other
    // editor, its area (it has no meter or other animation).
    const auto hostSet = [&](Editor& e, double v) {
        InEvents fin;
        OutEvents fout;
        fin.add(paramEvent(0, e.paramId, v));
        e.params->flush(e.plugin, &fin.list, &fout.list);
        pump(300, daw, editors);
    };
    hostSet(b, 0.75);
    Capture high = daw.capture();
    hostSet(b, 0.25);
    Capture low = daw.capture();
    const int changed = uilayout
        ? high.countDifferent(low, x0(1), kMargin + sliderY - 10, x0(1) + int(width), kMargin + sliderY + 10)
        : high.countDifferent(low, x0(1), kMargin, x0(1) + int(width), kMargin + int(height));
    std::printf("    host value change: %d editor pixels changed\n", changed);
    check(changed > 50, "the editor follows host parameter changes");

    // A resizable editor: the host shrinks it (set_size), it reports and draws the new size (the strip it no
    // longer covers shows the slot's gray), then grows back.
    if (a.gui->can_resize(a.plugin)) {
        uint32_t w2 = width - 120, h2 = height - 80;
        check(a.gui->adjust_size(a.plugin, &w2, &h2) && w2 == width - 120 && h2 == height - 80,
              "adjust_size accepts a smaller size");
        check(a.gui->set_size(a.plugin, w2, h2), "set_size (smaller)");
        pump(400, daw, editors);
        uint32_t nw = 0, nh = 0;
        a.gui->get_size(a.plugin, &nw, &nh);
        Capture c = daw.capture();
        const int strip = c.countNear(x0(0) + int(w2), kMargin, x0(0) + int(width), kMargin + int(height), 128, 128, 128, 3);
        const int stripArea = int(width - w2) * int(height);
        std::printf("    resized to %ux%u (reported %ux%u), freed strip %d/%d gray\n", w2, h2, nw, nh, strip, stripArea);
        check(nw == w2 && nh == h2, "get_size reports the new size");
        check(strip > stripArea * 9 / 10, "the editor window shrank");
        check(c.countNear(x0(0), kMargin, x0(0) + int(w2), kMargin + int(h2), 128, 128, 128, 3) < int(w2 * h2) / 2,
              "the resized editor renders");
        check(a.gui->set_size(a.plugin, width, height), "set_size (back to the original size)");
        pump(300, daw, editors);
        check(renders(daw.capture(), 0), "the editor renders at its original size again");
    } else {
        std::printf("    editor is not resizable\n");
    }

    // Window services (UILayout Test: Alt+Enter toggles fullscreen; while fullscreen the cursor hides when the mouse
    // is still). The requests may come from the editor's render thread; the host applies them on this thread.
    if (uilayout) {
        const auto altEnter = [&] {
            daw.key(0, Key::LeftAlt, true);
            pump(60, daw, editors);
            daw.key(0, Key::Return, true, true);
            pump(100, daw, editors);
            daw.key(0, Key::Return, false, true);
            pump(60, daw, editors);
            daw.key(0, Key::LeftAlt, false);
        };
        altEnter();
        pump(500, daw, editors);
        check(daw.isFullscreen(0), "Alt+Enter: the editor goes fullscreen");
        uint32_t fw = 0, fh = 0;
        a.gui->get_size(a.plugin, &fw, &fh);
        check(fw == uint32_t(width) && fh == uint32_t(height), "the host's view keeps its size while the editor is fullscreen");
#ifdef _WIN32
        pump(1500, daw, editors);
        check(daw.cursorHidden(0), "the cursor hides (fullscreen, mouse still)");
        daw.mouse(0, Mouse::Move, 100, 100);
        pump(300, daw, editors);
        check(!daw.cursorHidden(0), "the cursor shows again when the mouse moves");
#endif
        altEnter();
        pump(500, daw, editors);
        check(!daw.isFullscreen(0) && daw.hasEditor(0), "Alt+Enter again: the editor is back in the host's window");
#ifdef _WIN32
        // Host shortcuts, as Reaper asks for them (CLAP has no key callback): the window carries Reaper's marker and
        // answers WM_GETDLGCODE per key from the editor. UILayout Test claims only the keys it maps (F11, ...).
        check(daw.hasWantsKeysMarker(0), "the editor window carries Reaper's wants-keys marker (GWLP_USERDATA)");
        check(daw.askForKey(0, Key::Space) == 0, "WM_GETDLGCODE: an unmapped key (space) is left to the host");
        const auto f11 = [&] {
            const bool claimed = (daw.askForKey(0, Key::F11) & DLGC_WANTALLKEYS) != 0;
            daw.key(0, Key::F11, true); // the host then delivers it (dropped once: the editor already has it)
            pump(100, daw, editors);
            daw.key(0, Key::F11, false);
            pump(500, daw, editors);
            return claimed;
        };
        check(f11(), "WM_GETDLGCODE: a mapped key (F11) is claimed");
        check(daw.isFullscreen(0), "F11 asked through WM_GETDLGCODE: the editor goes fullscreen");
        check(f11() && !daw.isFullscreen(0), "F11 again: back in the host's window");
#endif
        pump(300, daw, editors);
        check(renders(daw.capture(), 0), "the editor renders in the host's window again");
    }

    // DPI scaling: the host's scale (gui.set_scale). The editor keeps its logical size (320 x 160); get_size reports
    // logical x scale pixels, the plugin asks the host to resize (request_resize) and draws at the scale. The slot
    // stays 320 x 160, so the capture shows its top-left part.
    if (uilayout) {
        // The first row with white text (the "Gain" header): it moves down 2x at scale 2.
        const auto trackRow = [&](const Capture& c) {
            for (int y = kMargin; y < kMargin + int(height); ++y)
                if (c.countNear(x0(0), y, x0(0) + int(width), y + 1, 255, 255, 255, 40) > 0) return y - kMargin;
            return -1;
        };
        const int row1 = trackRow(daw.capture());
        auto& h = *a.host;
        const auto setScale = [&](double scale) {
            const int before = h.resizeRequests;
            const bool ok = a.gui->set_scale(a.plugin, scale);
            pump(100, daw, editors);
            const bool asked = h.resizeRequests > before;
            if (asked) a.gui->set_size(a.plugin, h.requestedWidth, h.requestedHeight);
            pump(300, daw, editors);
            return ok && asked;
        };
        check(setScale(2.0), "set_scale(2): accepted, and the plugin asks the host to resize (request_resize)");
        uint32_t gw = 0, gh = 0;
        a.gui->get_size(a.plugin, &gw, &gh);
        int ew = 0, eh = 0;
        daw.editorSize(0, ew, eh);
        const int row2 = trackRow(daw.capture());
        std::printf("    scale 2: request_resize %ux%u, get_size %ux%u, window %dx%d, text row %d (%d at scale 1)\n",
                    h.requestedWidth, h.requestedHeight, gw, gh, ew, eh, row2, row1);
        check(h.requestedWidth == 2 * width && h.requestedHeight == 2 * height, "request_resize asks for the logical size x 2");
        check(gw == 2 * width && gh == 2 * height, "get_size reports window pixels at scale 2");
        check(ew == int(2 * width) && eh == int(2 * height), "the editor window has the scaled size");
        check(row1 > 0 && row2 >= 2 * row1 - 3 && row2 <= 2 * row1 + 3, "the editor draws at scale 2 (its text moved down 2x)");
        check(setScale(1.0), "set_scale(1): back");
        a.gui->get_size(a.plugin, &gw, &gh);
        daw.editorSize(0, ew, eh);
        check(gw == width && gh == height && ew == int(width) && eh == int(height), "back to the logical size at scale 1");
        check(trackRow(daw.capture()) == row1 && renders(daw.capture(), 0), "the editor draws at scale 1 again");
    }

    // Close one editor and reopen it, then close both.
    a.gui->destroy(a.plugin);
    pump(50, daw, editors);
    check(!daw.hasEditor(0), "gui destroy removes the editor window");
    check(a.gui->create(a.plugin, DawWindow::kApi, false), "gui create again");
    check(a.gui->set_parent(a.plugin, &a.slot), "editor reopens");
    pump(300, daw, editors);
    check(renders(daw.capture(), 0), "reopened editor renders");

    for (auto& e : editors) e.gui->hide(e.plugin);
    shutdown();
    for (auto& e : editors) check(e.host->timers.empty() && e.host->fds.empty(), "timers and fds unregistered");
}

} // namespace gui

} // namespace

int main(int argc, char** argv) {
#ifdef _WIN32
    // Like a DAW's UI thread: a single-threaded COM apartment (WPF editors require one).
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
#endif
    std::vector<fs::path> paths;
    for (int i = 1; i < argc; ++i) paths.emplace_back(argv[i]);
    if (paths.empty()) {
        const auto dir = executableDir() / "clap";
        // The examples (generic checks) and the test plugins (detailed checks: UILayoutTestPlugin).
        std::vector<std::string> names = {"SimpleExample", "MidiExample", "UILayoutExample", "ParameterEditorExample",
                                          "UILayoutTestPlugin"};
#ifdef _WIN32
        names.push_back("WPFExample");
#endif
        for (const auto& name : names) paths.push_back(dir / name / (name + ".clap"));
    }

    TestHost host;
    std::vector<const clap_plugin_t*> plugins;
    std::vector<const clap_plugin_entry_t*> entries, guiEntries;
    for (const auto& path : paths) {
        const clap_plugin_entry_t* entry = nullptr;
        if (const auto* plugin = testPlugin(path, host, entry)) {
            plugins.push_back(plugin);
            const bool hasGui = plugin->get_extension(plugin, CLAP_EXT_GUI) != nullptr;
            std::printf("    gui extension: %s\n", hasGui ? "yes" : "no");
            if (hasGui) guiEntries.push_back(entry);
        }
        if (entry) entries.push_back(entry);
    }

    // Editors, while the other modules' instances stay alive.
    for (const auto* entry : guiEntries) gui::test(entry);

    // All plugins stay alive together: several AudioPlugSharp modules in one process, one runtime.
    std::printf("\n== %zu plugin instances from %zu modules alive at once ==\n", plugins.size(), entries.size());
    for (const auto* p : plugins) {
        check(p->activate(p, 44100, 32, 128), "re-activate at 44.1 kHz");
        p->deactivate(p);
    }
    for (const auto* p : plugins) p->destroy(p);
    for (const auto* e : entries) e->deinit();

    std::printf("\n%s (%d failure%s)\n", gFailures == 0 ? "PASSED" : "FAILED", gFailures, gFailures == 1 ? "" : "s");
    return gFailures == 0 ? 0 : 1;
}
