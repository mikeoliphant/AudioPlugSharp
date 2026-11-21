// main.cpp - BridgeTestHost: loads unmodified AudioPlugSharp plugins through the bridge, runs a functional demo,
// an isolation test, a realtime stress test, and the audio-thread allocation check.
#include "aps/ManagedPluginFactory.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <mmsystem.h>
#else
#include <pthread.h>
#include <sched.h>
#include <unistd.h>
#endif
#ifdef __APPLE__
#include <mach-o/dyld.h>
#endif

namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;

namespace {

constexpr double kPi = 3.14159265358979323846;

fs::path executableDir() {
#ifdef _WIN32
    wchar_t buf[MAX_PATH];
    DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    return fs::path(std::wstring(buf, n)).parent_path();
#elif defined(__APPLE__)
    char buf[4096];
    uint32_t size = sizeof(buf);
    if (_NSGetExecutablePath(buf, &size) == 0) return fs::canonical(buf).parent_path();
    return fs::current_path();
#else
    return fs::canonical("/proc/self/exe").parent_path();
#endif
}

void setRealtimePriority() {
#ifdef _WIN32
    if (!SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL))
        std::printf("  (could not raise audio thread priority)\n");
#else
    sched_param sp{};
    sp.sched_priority = std::min(80, sched_get_priority_max(SCHED_FIFO));
    if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp) != 0)
        std::printf("  (SCHED_FIFO not permitted; audio thread runs at normal priority)\n");
#endif
}

const char* levelName(aps::LogLevel level) {
    switch (level) {
        case aps::LogLevel::Debug: return "debug";
        case aps::LogLevel::Info: return "info";
        case aps::LogLevel::Warning: return "warn";
        default: return "error";
    }
}

class ConsoleHost final : public aps::HostApi {
public:
    aps::HostInfo getInfo() override {
        aps::HostInfo info;
        info.name = "BridgeTestHost";
        return info;
    }
    void log(aps::LogLevel level, std::string_view message) override {
        std::printf("  [plugin %s] %.*s\n", levelName(level), static_cast<int>(message.size()), message.data());
    }
    void beginEdit(uint32_t) override { ++begins; }
    void performEdit(uint32_t paramIndex, double normalized) override {
        ++performs;
        lastIndex = paramIndex;
        lastValue = normalized;
    }
    void endEdit(uint32_t) override { ++ends; }

    int begins = 0, performs = 0, ends = 0;
    uint32_t lastIndex = 0;
    double lastValue = 0;
};

// ---------------------------------------------------------------------------
// Buffers for all of a plugin's ports, in float or double.
// ---------------------------------------------------------------------------
class Buffers {
public:
    Buffers(const aps::PluginApi& plugin, const aps::PluginInfo& info, uint32_t frames, aps::SampleFormat format)
        : frames_(frames), sampleSize_(format == aps::SampleFormat::Float64 ? 8 : 4), format_(format) {
        addPorts(plugin, info.numInputPorts, true);
        addPorts(plugin, info.numOutputPorts, false);
        outEvents_.resize(64);
    }

    void fillInputSine(double freq, double rate, double amp) {
        for (auto& port : inputs_)
            for (auto& ch : port.channels)
                for (uint32_t i = 0; i < frames_; ++i) write(ch.data(), i, amp * std::sin(2 * kPi * freq * i / rate));
    }

    // Peak over all output channels; NaN if any sample is non-finite.
    double outputPeak() const {
        double peak = 0;
        for (auto& port : outputs_)
            for (auto& ch : port.channels)
                for (uint32_t i = 0; i < frames_; ++i) {
                    double s = read(ch.data(), i);
                    if (!std::isfinite(s)) return NAN;
                    peak = std::max(peak, std::fabs(s));
                }
        return peak;
    }

    aps::ProcessData data(uint32_t frames, const aps::AudioEvent* events, uint32_t numEvents, int64_t position) {
        aps::ProcessData d{};
        d.inputs = inputBuffers_.data();
        d.outputs = outputBuffers_.data();
        d.num_input_ports = static_cast<uint32_t>(inputBuffers_.size());
        d.num_output_ports = static_cast<uint32_t>(outputBuffers_.size());
        d.events = events;
        d.num_events = numEvents;
        d.output_events = outEvents_.data();
        d.output_event_capacity = static_cast<uint32_t>(outEvents_.size());
        d.num_frames = frames;
        d.sample_format = static_cast<aps_sample_format>(format_);
        d.transport_flags = APS_TRANSPORT_FLAGS_PLAYING;
        d.project_sample = position;
        d.tempo = 120.0;
        return d;
    }

    size_t inputPorts() const { return inputs_.size(); }

private:
    struct Port {
        std::vector<std::vector<uint8_t>> channels;
        std::vector<void*> pointers;
    };

    void addPorts(const aps::PluginApi& plugin, uint32_t count, bool input) {
        auto& ports = input ? inputs_ : outputs_;
        ports.resize(count);
        for (uint32_t p = 0; p < count; ++p) {
            auto info = input ? plugin.getInputPort(p) : plugin.getOutputPort(p);
            uint32_t channels = info ? info->numChannels : 1;
            ports[p].channels.assign(channels, std::vector<uint8_t>(size_t(frames_) * sampleSize_));
            for (auto& ch : ports[p].channels) ports[p].pointers.push_back(ch.data());
        }
        auto& buffers = input ? inputBuffers_ : outputBuffers_;
        for (auto& port : ports)
            buffers.push_back(aps_audio_port_buffers{port.pointers.data(), static_cast<uint32_t>(port.pointers.size()), 0});
    }

    void write(uint8_t* ch, uint32_t i, double v) const {
        if (sampleSize_ == 8) reinterpret_cast<double*>(ch)[i] = v;
        else reinterpret_cast<float*>(ch)[i] = static_cast<float>(v);
    }
    double read(const uint8_t* ch, uint32_t i) const {
        return sampleSize_ == 8 ? reinterpret_cast<const double*>(ch)[i] : reinterpret_cast<const float*>(ch)[i];
    }

    uint32_t frames_;
    int sampleSize_;
    aps::SampleFormat format_;
    std::vector<Port> inputs_, outputs_;
    std::vector<aps_audio_port_buffers> inputBuffers_, outputBuffers_;
    std::vector<aps::AudioEvent> outEvents_;
};

aps::AudioEvent noteEvent(aps_audio_event_type type, uint32_t offset, uint32_t note, double velocity) {
    return aps::AudioEvent{type, offset, note, 0, velocity};
}

aps::AudioEvent paramEvent(uint32_t offset, uint32_t index, double normalized) {
    return aps::AudioEvent{APS_AUDIO_EVENT_TYPE_PARAM_CHANGE, offset, index, 0, normalized};
}

bool gPreferFloat64 = false;

aps::SampleFormat chooseFormat(const aps::PluginInfo& info) {
    const auto preferred = gPreferFloat64 ? aps::SampleFormat::Float64 : aps::SampleFormat::Float32;
    if (aps::hasFlag(info.sampleFormats, preferred)) return preferred;
    return gPreferFloat64 ? aps::SampleFormat::Float32 : aps::SampleFormat::Float64;
}

std::string paramText(const aps::PluginApi& plugin, uint32_t index, double normalized) {
    auto t = plugin.formatParam(index, normalized);
    return t ? t->text : std::string("?");
}

struct Options {
    double sampleRate = 48000;
    uint32_t frames = 128;
    double seconds = 5;
    int pressure = 200;
};

// ---------------------------------------------------------------------------
// Functional demo: description, parameters, processing with events, state.
// ---------------------------------------------------------------------------
bool runDemo(aps::PluginApi& plugin, ConsoleHost& host, const Options& opt) {
    const auto info = plugin.getInfo().value;
    std::printf("\n== %s ==\n", info.name.c_str());
    std::printf("  %s (%s), %s, v%s, id %016llx\n", info.company.c_str(), info.website.c_str(), info.category.c_str(),
                info.version.c_str(), static_cast<unsigned long long>(info.pluginId));
    std::printf("  formats:%s%s\n", aps::hasFlag(info.sampleFormats, aps::SampleFormat::Float32) ? " float32" : "",
                aps::hasFlag(info.sampleFormats, aps::SampleFormat::Float64) ? " float64" : "");
    for (uint32_t i = 0; i < info.numInputPorts; ++i)
        if (auto p = plugin.getInputPort(i)) std::printf("  input  %u: %s (%u ch)\n", i, p->name.c_str(), p->numChannels);
    for (uint32_t i = 0; i < info.numOutputPorts; ++i)
        if (auto p = plugin.getOutputPort(i)) std::printf("  output %u: %s (%u ch)\n", i, p->name.c_str(), p->numChannels);

    const uint32_t paramCount = plugin.getParamCount().valueOr(0);
    for (uint32_t i = 0; i < paramCount; ++i)
        if (auto p = plugin.getParamInfo(i))
            std::printf("  param  %u: %s (\"%s\"), default %.3f = %s\n", i, p->name.c_str(), p->key.c_str(),
                        p->defaultNormalized, paramText(plugin, i, p->defaultNormalized).c_str());

    const auto format = chooseFormat(info);
    if (!plugin.setupProcessing(opt.sampleRate, opt.frames, format) || !plugin.setActive(true)) {
        std::printf("  setup/activate failed\n");
        return false;
    }
    Buffers buf(plugin, info, opt.frames, format);
    buf.fillInputSine(220.0, opt.sampleRate, 0.5);
    int64_t pos = 0;
    uint32_t outputEvents = 0;
    auto run = [&](int blocks, const aps::AudioEvent* first, uint32_t numFirst) {
        for (int b = 0; b < blocks; ++b) {
            auto d = buf.data(opt.frames, b == 0 ? first : nullptr, b == 0 ? numFirst : 0, pos);
            if (!plugin.process(d)) std::printf("  process returned an error\n");
            outputEvents += d.num_output_events;
            pos += opt.frames;
        }
    };

    std::printf("  processing (%s):\n", format == aps::SampleFormat::Float32 ? "float32" : "float64");
    run(20, nullptr, 0);
    std::printf("    %-40s peak %.3f\n", buf.inputPorts() ? "sine in, defaults:" : "no input, defaults:", buf.outputPeak());
    if (paramCount > 0) {
        const auto change = paramEvent(0, 0, 0.5);
        run(20, &change, 1);
        std::printf("    %-40s peak %.3f\n", ("param 0 -> " + paramText(plugin, 0, 0.5) + ":").c_str(), buf.outputPeak());
    }
    const auto on = noteEvent(APS_AUDIO_EVENT_TYPE_NOTE_ON, 0, 60, 0.8);
    run(100, &on, 1);
    std::printf("    %-40s peak %.3f\n", "note on 60:", buf.outputPeak());
    const auto off = noteEvent(APS_AUDIO_EVENT_TYPE_NOTE_OFF, 0, 60, 0.0);
    run(400, &off, 1);
    std::printf("    %-40s peak %.3f\n", "note off:", buf.outputPeak());
    if (paramCount > 0) {
        // Events-only block: the bridge delivers events even without audio (upstream dropped them).
        const auto flush = paramEvent(0, 0, 0.75);
        auto d = buf.data(0, &flush, 1, pos);
        std::printf("    %-40s %s\n", "events-only block:", plugin.process(d) ? "ok" : "error");
    }
    if (outputEvents) std::printf("    output events: %u\n", outputEvents);
    plugin.setActive(false); // the bridge reports its allocation/GC diagnostics here

    if (paramCount > 0) {
        // AudioPlugSharp saves *process* values and restores them as edit values (which notifies the host).
        plugin.setParamNormalized(0, 0.9);
        auto state = plugin.saveState().value;
        host.begins = host.performs = host.ends = 0;
        plugin.setParamNormalized(0, 0.1);
        plugin.loadState(state);
        std::printf("  state: %zu bytes; edit value 0.1 -> after load %.3f (%s); host edits: %d begin, %d perform, %d end\n",
                    state.size(), plugin.getParamNormalized(0).valueOr(NAN),
                    paramText(plugin, 0, plugin.getParamNormalized(0).valueOr(0)).c_str(), host.begins, host.performs,
                    host.ends);
    }
    return true;
}

// ---------------------------------------------------------------------------
// Isolation: more instances of the same plugin process independently and unload when destroyed.
// ---------------------------------------------------------------------------
bool runIsolationTest(aps::ManagedPluginFactory& factory, aps::HostApi& host, const fs::path& pluginPath,
                      const Options& opt) {
    std::printf("\n== Isolation: 3 more instances of %s ==\n", pluginPath.stem().string().c_str());
    const auto& diag = factory.diagnostics();
    const int liveBefore = diag.livePluginContexts ? diag.livePluginContexts() : -1;

    std::vector<std::unique_ptr<aps::PluginApi>> instances;
    for (int i = 0; i < 3; ++i) {
        std::string error;
        auto p = factory.load(pluginPath, host, error);
        if (!p) {
            std::printf("  load failed: %s\n", error.c_str());
            return false;
        }
        instances.push_back(std::move(p));
    }

    // Different parameter values per instance; same input. Peaks must follow the values.
    const double values[] = {1.0, 0.75, 0.5};
    double previous = INFINITY;
    bool ordered = true;
    for (size_t i = 0; i < instances.size(); ++i) {
        auto& p = *instances[i];
        const auto info = p.getInfo().value;
        const auto format = chooseFormat(info);
        p.setupProcessing(opt.sampleRate, opt.frames, format);
        p.setActive(true);
        Buffers buf(p, info, opt.frames, format);
        buf.fillInputSine(220.0, opt.sampleRate, 0.5);
        const auto change = paramEvent(0, 0, values[i]);
        auto d = buf.data(opt.frames, &change, 1, 0);
        p.process(d);
        const double peak = buf.outputPeak();
        ordered = ordered && peak < previous;
        previous = peak;
        std::printf("  instance %zu: param 0 = %-8s -> peak %.3f\n", i + 1, paramText(p, 0, values[i]).c_str(), peak);
        p.setActive(false);
    }

    const int liveLoaded = diag.livePluginContexts ? diag.livePluginContexts() : -1;
    instances.clear();
    const int liveAfter = diag.livePluginContexts ? diag.livePluginContexts() : -1;
    std::printf("  outputs follow their own parameter values: %s\n", ordered ? "yes" : "NO");
    std::printf("  live plugin load contexts: before %d, with instances %d, after destroy %d\n", liveBefore, liveLoaded,
                liveAfter);
    return ordered && liveAfter == liveBefore;
}

// ---------------------------------------------------------------------------
// Realtime stress test.
// ---------------------------------------------------------------------------
void runStress(aps::PluginApi& plugin, const aps::PluginDiagnostics& diag, const Options& opt, int pressureMBps) {
    std::printf("\n== Stress: %.0f s, %u frames @ %.0f Hz, GC pressure %s ==\n", opt.seconds, opt.frames,
                opt.sampleRate, pressureMBps > 0 ? (std::to_string(pressureMBps) + " MB/s").c_str() : "off");

    const auto info = plugin.getInfo().value;
    const auto format = chooseFormat(info);
    if (!plugin.setupProcessing(opt.sampleRate, opt.frames, format) || !plugin.setActive(true)) {
        std::printf("  setup/activate failed\n");
        return;
    }
    const bool hasParams = plugin.getParamCount().valueOr(0) > 0;

    const size_t numBlocks = static_cast<size_t>(opt.seconds * opt.sampleRate / opt.frames);
    const auto period = std::chrono::nanoseconds(static_cast<int64_t>(1e9 * opt.frames / opt.sampleRate));
    std::vector<int64_t> durations(numBlocks); // preallocated; no allocation on the audio thread
    Buffers buf(plugin, info, opt.frames, format);
    buf.fillInputSine(220.0, opt.sampleRate, 0.25);

    int gc0[3] = {};
    double pause0 = 0;
    if (diag.gcCount)
        for (int g = 0; g < 3; ++g) gc0[g] = diag.gcCount(g);
    if (diag.gcPauseMs) pause0 = diag.gcPauseMs();
    if (pressureMBps > 0 && diag.startGcPressure) diag.startGcPressure(pressureMBps);

    std::atomic<int> errors{0};
    std::thread audio([&] {
        setRealtimePriority();
        aps::AudioEvent events[3];
        auto next = Clock::now();
        for (size_t b = 0; b < numBlocks; ++b) {
            next += period;
            uint32_t n = 0;
            if (b % 100 == 0) events[n++] = noteEvent(APS_AUDIO_EVENT_TYPE_NOTE_ON, 0, 60 + (b / 100) % 12, 0.8);
            if (b % 100 == 50) events[n++] = noteEvent(APS_AUDIO_EVENT_TYPE_NOTE_OFF, 0, 60 + (b / 100) % 12, 0.0);
            if (hasParams && b % 10 == 0) events[n++] = paramEvent(opt.frames / 2, 0, 0.5 + 0.4 * double(b % 120) / 120.0);

            auto d = buf.data(opt.frames, events, n, int64_t(b) * opt.frames);
            const auto t0 = Clock::now();
            if (!plugin.process(d)) errors.fetch_add(1, std::memory_order_relaxed);
            const auto t1 = Clock::now();
            durations[b] = std::chrono::duration_cast<std::chrono::nanoseconds>(t1 - t0).count();
            std::this_thread::sleep_until(next); // returns immediately if we are behind
        }
    });
    audio.join();

    if (pressureMBps > 0 && diag.stopGcPressure) diag.stopGcPressure();
    plugin.setActive(false);

    const double budgetUs = period.count() / 1000.0;
    std::vector<int64_t> sorted(durations.begin() + 1, durations.end());
    std::sort(sorted.begin(), sorted.end());
    auto pct = [&](double p) { return sorted[std::min(sorted.size() - 1, size_t(p * double(sorted.size())))] / 1000.0; };
    double sum = 0;
    size_t over = 0, overHalf = 0;
    for (auto d : sorted) {
        sum += double(d);
        if (d / 1000.0 > budgetUs) ++over;
        if (d / 1000.0 > budgetUs * 0.5) ++overHalf;
    }
    std::printf("  blocks %zu, budget %.0f us/block, errors %d\n", numBlocks, budgetUs, errors.load());
    std::printf("  first call on audio thread: %.1f us\n", durations[0] / 1000.0);
    std::printf("  process (us): min %.1f  median %.1f  mean %.1f  p99 %.1f  p99.9 %.1f  max %.1f\n",
                sorted.front() / 1000.0, pct(0.5), sum / double(sorted.size()) / 1000.0, pct(0.99), pct(0.999),
                sorted.back() / 1000.0);
    std::printf("  blocks over budget: %zu   over 50%% budget: %zu\n", over, overHalf);
    if (diag.gcCount && diag.gcPauseMs)
        std::printf("  GCs during run: gen0 %d, gen1 %d, gen2 %d; total GC pause %.2f ms\n", diag.gcCount(0) - gc0[0],
                    diag.gcCount(1) - gc0[1], diag.gcCount(2) - gc0[2], diag.gcPauseMs() - pause0);
}

void usage() {
    std::printf(
        "usage: BridgeTestHost [options]\n"
        "  --plugin <path>    plugin assembly; repeatable (default: SimpleExample and MidiExample)\n"
        "  --seconds <n>      stress duration per run, on the first plugin (default 5, 0 = skip)\n"
        "  --pressure <MB/s>  GC pressure for the second stress run (default 200, 0 = skip)\n"
        "  --frames <n>       block size (default 128)\n"
        "  --rate <hz>        sample rate (default 48000)\n"
        "  --float64          process in 64-bit where the plugin supports it\n");
}

} // namespace

int main(int argc, char** argv) {
    const fs::path exeDir = executableDir();
    std::vector<fs::path> plugins;
    Options opt;

    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        auto next = [&]() -> const char* { return i + 1 < argc ? argv[++i] : ""; };
        if (a == "--plugin") plugins.emplace_back(next());
        else if (a == "--seconds") opt.seconds = std::atof(next());
        else if (a == "--pressure") opt.pressure = std::atoi(next());
        else if (a == "--frames") opt.frames = static_cast<uint32_t>(std::atoi(next()));
        else if (a == "--rate") opt.sampleRate = std::atof(next());
        else if (a == "--float64") gPreferFloat64 = true;
        else { usage(); return a == "--help" || a == "-h" ? 0 : 1; }
    }
    if (opt.frames == 0 || opt.sampleRate <= 0) { usage(); return 1; }
    if (plugins.empty())
        for (const char* name : {"SimpleExample", "MidiExample"})
            plugins.push_back(exeDir / "plugins" / name / (std::string(name) + ".dll"));

#ifdef _WIN32
    timeBeginPeriod(1); // 1 ms scheduler granularity for the paced audio loop
#endif

    std::printf("== Boot .NET runtime ==\n");
    const auto t0 = Clock::now();
    aps::ManagedPluginFactory factory;
    std::string error;
    if (!factory.initialize(exeDir / "bridge", error)) {
        std::fprintf(stderr, "error: %s\n", error.c_str());
        return 1;
    }
    std::printf("  runtime start %.1f ms\n", std::chrono::duration<double, std::milli>(Clock::now() - t0).count());
    const auto& diag = factory.diagnostics();

    ConsoleHost host;
    bool ok = true;
    struct Allocations { std::string name; long long bridge, plugin; };
    std::vector<Allocations> allocations;

    for (size_t i = 0; i < plugins.size(); ++i) {
        const auto t1 = Clock::now();
        auto plugin = factory.load(plugins[i], host, error);
        if (!plugin) {
            std::fprintf(stderr, "error loading %s: %s\n", plugins[i].string().c_str(), error.c_str());
            return 1;
        }
        std::printf("  loaded %s in %.1f ms\n", plugins[i].filename().string().c_str(),
                    std::chrono::duration<double, std::milli>(Clock::now() - t1).count());

        if (diag.resetAllocationCounters) diag.resetAllocationCounters();
        ok = runDemo(*plugin, host, opt) && ok;
        if (i == 0) {
            ok = runIsolationTest(factory, host, plugins[i], opt) && ok;
            if (opt.seconds > 0) {
                runStress(*plugin, diag, opt, 0);
                if (opt.pressure > 0) runStress(*plugin, diag, opt, opt.pressure);
            }
        }
        if (diag.bridgeAllocatedBytes && diag.pluginAllocatedBytes)
            allocations.push_back({plugins[i].stem().string(), static_cast<long long>(diag.bridgeAllocatedBytes()),
                                   static_cast<long long>(diag.pluginAllocatedBytes())});
    }

    // The bridge must not allocate on the audio path. Plugin allocations are reported only.
    std::printf("\n== Audio-path allocations (process calls after setup) ==\n");
    for (const auto& a : allocations) {
        std::printf("  %-16s bridge %lld B %s, plugin %lld B\n", a.name.c_str(), a.bridge, a.bridge == 0 ? "(ok)" : "(FAIL)",
                    a.plugin);
        ok = ok && a.bridge == 0;
    }

#ifdef _WIN32
    timeEndPeriod(1);
#endif
    std::printf("\n%s\n", ok ? "done." : "FAILED");
    return ok ? 0 : 1;
}
