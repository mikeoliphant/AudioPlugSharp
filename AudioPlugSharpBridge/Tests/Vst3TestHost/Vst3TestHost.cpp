// Vst3TestHost.cpp - a small VST3 host for automated tests of the AudioPlugSharp VST3 adapter (the VST3 sibling
// of ClapTestHost). Written against pluginterfaces only.
//
// Loads each .vst3 bundle given on the command line (default: the example bundles next to this executable) the
// way a DAW would, exercises the lifecycle (factory, component/controller, buses, parameters, processing in 32
// and 64 bit, events, state), and checks results. Plugins with an editor are then opened twice in one window
// and driven with synthetic input (gui::test; Win32 or X11, which needs a display). All modules stay loaded, so
// the run also covers several AudioPlugSharp plugins sharing one .NET runtime.
#define INIT_CLASS_IID // this translation unit defines the interfaces' FUID iid statics

#include "TestWindow.h"

#include "pluginterfaces/base/funknown.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/base/ipluginbase.h"
#include "pluginterfaces/base/keycodes.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivsthostapplication.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#ifndef _WIN32
#include <dlfcn.h>
#include <poll.h>
#endif

#ifndef APS_VST3_ARCH_DIR
#error "APS_VST3_ARCH_DIR (the bundle's Contents subfolder) must be defined by the build"
#endif

namespace fs = std::filesystem;
using namespace Steinberg;
using namespace Steinberg::Vst;
using aps_test::Capture;
using aps_test::executableDir;
using aps_test::Key;
using aps_test::Mouse;
using aps_test::saveBmp;

namespace {

int gFailures = 0;

void check(bool ok, const char* what) {
    std::printf("    [%s] %s\n", ok ? " ok " : "FAIL", what);
    if (!ok) ++gFailures;
}

bool iidEqual(const TUID a, const TUID b) { return std::memcmp(a, b, sizeof(TUID)) == 0; }

std::string narrow(const char16* s) {
    std::string out;
    for (size_t i = 0; s[i] && i < 256; ++i) out.push_back(s[i] < 0x80 ? char(s[i]) : '?');
    return out;
}

// Host-side objects live on the stack of the test; their reference counts are not tracked.
#define TEST_NO_REFCOUNT                                    \
    uint32 PLUGIN_API addRef() override { return 1; }       \
    uint32 PLUGIN_API release() override { return 1; }

#define TEST_QUERY_INTERFACE(Iface)                                                  \
    tresult PLUGIN_API queryInterface(const TUID queried, void** obj) override {     \
        if (iidEqual(queried, FUnknown::iid) || iidEqual(queried, Iface::iid)) {     \
            *obj = static_cast<Iface*>(this);                                        \
            return kResultOk;                                                        \
        }                                                                            \
        *obj = nullptr;                                                              \
        return kNoInterface;                                                         \
    }

// ---------------------------------------------------------------------------
// Host objects.
// ---------------------------------------------------------------------------
struct HostApplication final : IHostApplication {
    tresult PLUGIN_API getName(String128 name) override {
        const char* n = "AudioPlugSharp Vst3TestHost";
        size_t i = 0;
        for (; n[i]; ++i) name[i] = char16(n[i]);
        name[i] = 0;
        return kResultOk;
    }
    tresult PLUGIN_API createInstance(TUID, TUID, void** obj) override {
        *obj = nullptr;
        return kResultFalse;
    }
    TEST_QUERY_INTERFACE(IHostApplication)
    TEST_NO_REFCOUNT
};

HostApplication gHostApp;

// Records the plugin's edits (gesture begin / values / end).
struct ComponentHandler final : IComponentHandler {
    int begins = 0, ends = 0, performs = 0, restarts = 0;
    ParamID lastId = 0;
    double lastValue = -1;
    // Edits must come on the main (UI) thread, also from editors that render on a thread of their own.
    const std::thread::id mainThread = std::this_thread::get_id();
    std::atomic<int> offThread{0};
    void onCall() { if (std::this_thread::get_id() != mainThread) ++offThread; }

    tresult PLUGIN_API beginEdit(ParamID) override { onCall(); ++begins; return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override {
        onCall();
        ++performs;
        lastId = id;
        lastValue = value;
        return kResultOk;
    }
    tresult PLUGIN_API endEdit(ParamID) override { onCall(); ++ends; return kResultOk; }
    tresult PLUGIN_API restartComponent(int32) override { ++restarts; return kResultOk; }
    int total() const { return begins + ends + performs; }
    void clear() { begins = ends = performs = 0; lastValue = -1; }
    TEST_QUERY_INTERFACE(IComponentHandler)
    TEST_NO_REFCOUNT
};

struct ParamQueue final : IParamValueQueue {
    ParamID id = 0;
    std::vector<std::pair<int32, ParamValue>> points;

    ParamID PLUGIN_API getParameterId() override { return id; }
    int32 PLUGIN_API getPointCount() override { return int32(points.size()); }
    tresult PLUGIN_API getPoint(int32 index, int32& sampleOffset, ParamValue& value) override {
        if (index < 0 || size_t(index) >= points.size()) return kInvalidArgument;
        sampleOffset = points[size_t(index)].first;
        value = points[size_t(index)].second;
        return kResultOk;
    }
    tresult PLUGIN_API addPoint(int32 sampleOffset, ParamValue value, int32& index) override {
        index = int32(points.size());
        points.emplace_back(sampleOffset, value);
        return kResultOk;
    }
    TEST_QUERY_INTERFACE(IParamValueQueue)
    TEST_NO_REFCOUNT
};

struct ParamChanges final : IParameterChanges {
    std::vector<std::unique_ptr<ParamQueue>> queues;

    int32 PLUGIN_API getParameterCount() override { return int32(queues.size()); }
    IParamValueQueue* PLUGIN_API getParameterData(int32 index) override {
        return index >= 0 && size_t(index) < queues.size() ? queues[size_t(index)].get() : nullptr;
    }
    IParamValueQueue* PLUGIN_API addParameterData(const ParamID& id, int32& index) override {
        for (size_t i = 0; i < queues.size(); ++i)
            if (queues[i]->id == id) { index = int32(i); return queues[i].get(); }
        index = int32(queues.size());
        queues.push_back(std::make_unique<ParamQueue>());
        queues.back()->id = id;
        return queues.back().get();
    }
    void add(ParamID id, int32 offset, double value) {
        int32 i = 0;
        addParameterData(id, i)->addPoint(offset, value, i);
    }
    void clear() { queues.clear(); }
    TEST_QUERY_INTERFACE(IParameterChanges)
    TEST_NO_REFCOUNT
};

struct EventList final : IEventList {
    std::vector<Event> events;

    int32 PLUGIN_API getEventCount() override { return int32(events.size()); }
    tresult PLUGIN_API getEvent(int32 index, Event& e) override {
        if (index < 0 || size_t(index) >= events.size()) return kInvalidArgument;
        e = events[size_t(index)];
        return kResultOk;
    }
    tresult PLUGIN_API addEvent(Event& e) override {
        events.push_back(e);
        return kResultOk;
    }
    int count(uint16 type) const {
        return int(std::count_if(events.begin(), events.end(), [&](const Event& e) { return e.type == type; }));
    }
    void clear() { events.clear(); }
    TEST_QUERY_INTERFACE(IEventList)
    TEST_NO_REFCOUNT
};

Event noteEvent(bool on, int32 offset, int16 pitch, float velocity) {
    Event e{};
    e.busIndex = 0;
    e.sampleOffset = offset;
    if (on) {
        e.type = Event::kNoteOnEvent;
        e.noteOn.pitch = pitch;
        e.noteOn.velocity = velocity;
        e.noteOn.noteId = -1;
    } else {
        e.type = Event::kNoteOffEvent;
        e.noteOff.pitch = pitch;
        e.noteOff.velocity = velocity;
        e.noteOff.noteId = -1;
    }
    return e;
}

struct MemoryStream final : IBStream {
    std::vector<uint8_t> bytes;
    int64 pos = 0;

    tresult PLUGIN_API read(void* buffer, int32 numBytes, int32* numBytesRead) override {
        const int64 n = std::max<int64>(0, std::min<int64>(numBytes, int64(bytes.size()) - pos));
        if (n > 0) std::memcpy(buffer, bytes.data() + pos, size_t(n));
        pos += n;
        if (numBytesRead) *numBytesRead = int32(n);
        return kResultOk;
    }
    tresult PLUGIN_API write(void* buffer, int32 numBytes, int32* numBytesWritten) override {
        if (numBytes < 0) return kInvalidArgument;
        if (size_t(pos) + size_t(numBytes) > bytes.size()) bytes.resize(size_t(pos) + size_t(numBytes));
        std::memcpy(bytes.data() + pos, buffer, size_t(numBytes));
        pos += numBytes;
        if (numBytesWritten) *numBytesWritten = numBytes;
        return kResultOk;
    }
    tresult PLUGIN_API seek(int64 offset, int32 mode, int64* result) override {
        const int64 base = mode == kIBSeekSet ? 0 : mode == kIBSeekCur ? pos : int64(bytes.size());
        pos = std::clamp<int64>(base + offset, 0, int64(bytes.size()));
        if (result) *result = pos;
        return kResultOk;
    }
    tresult PLUGIN_API tell(int64* result) override {
        if (result) *result = pos;
        return kResultOk;
    }
    TEST_QUERY_INTERFACE(IBStream)
    TEST_NO_REFCOUNT
};

// The editor's frame. On Linux it is also the host run loop (IRunLoop): timers and fd handlers, serviced from
// the test's loop like a DAW's UI thread.
struct PlugFrame final : IPlugFrame, Linux::IRunLoop {
    struct Timer {
        Linux::ITimerHandler* handler;
        std::chrono::milliseconds period;
        std::chrono::steady_clock::time_point next;
    };
    std::vector<Timer> timers;
    std::vector<std::pair<Linux::IEventHandler*, int>> fds;
    int resizeRequests = 0;
    ViewRect lastResize{};

    // As a host does: resize the view's container (not modelled here) and tell the view (onSize).
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* rect) override {
        ++resizeRequests;
        if (!view || !rect) return kInvalidArgument;
        lastResize = *rect;
        return view->onSize(rect) == kResultTrue ? kResultTrue : kResultFalse;
    }
    tresult PLUGIN_API registerEventHandler(Linux::IEventHandler* handler, Linux::FileDescriptor fd) override {
        if (!handler) return kInvalidArgument;
        handler->addRef();
        fds.emplace_back(handler, fd);
        return kResultOk;
    }
    tresult PLUGIN_API unregisterEventHandler(Linux::IEventHandler* handler) override {
        const auto n = fds.size();
        for (auto& f : fds)
            if (f.first == handler) f.first->release();
        fds.erase(std::remove_if(fds.begin(), fds.end(), [&](auto& f) { return f.first == handler; }), fds.end());
        return fds.size() != n ? kResultOk : kResultFalse;
    }
    tresult PLUGIN_API registerTimer(Linux::ITimerHandler* handler, Linux::TimerInterval ms) override {
        if (!handler) return kInvalidArgument;
        handler->addRef();
        const auto period = std::chrono::milliseconds(std::max<Linux::TimerInterval>(ms, 1));
        timers.push_back({handler, period, std::chrono::steady_clock::now() + period});
        return kResultOk;
    }
    tresult PLUGIN_API unregisterTimer(Linux::ITimerHandler* handler) override {
        const auto n = timers.size();
        for (auto& t : timers)
            if (t.handler == handler) t.handler->release();
        timers.erase(std::remove_if(timers.begin(), timers.end(), [&](auto& t) { return t.handler == handler; }), timers.end());
        return timers.size() != n ? kResultOk : kResultFalse;
    }

    void service() {
        const auto now = std::chrono::steady_clock::now();
        std::vector<Linux::ITimerHandler*> due;
        for (auto& t : timers)
            if (now >= t.next) {
                due.push_back(t.handler);
                t.next = std::max(t.next + t.period, now);
            }
        for (auto* h : due) h->onTimer();
#ifndef _WIN32
        if (!fds.empty()) {
            std::vector<pollfd> polls;
            for (auto& f : fds) polls.push_back({f.second, POLLIN, 0});
            const auto handlers = fds;
            if (poll(polls.data(), polls.size(), 0) > 0)
                for (size_t i = 0; i < polls.size(); ++i)
                    if (polls[i].revents & POLLIN) handlers[i].first->onFDIsSet(polls[i].fd);
        }
#endif
    }

    tresult PLUGIN_API queryInterface(const TUID queried, void** obj) override {
        if (iidEqual(queried, FUnknown::iid) || iidEqual(queried, IPlugFrame::iid)) {
            *obj = static_cast<IPlugFrame*>(this);
            return kResultOk;
        }
#ifndef _WIN32
        if (iidEqual(queried, Linux::IRunLoop::iid)) {
            *obj = static_cast<Linux::IRunLoop*>(this);
            return kResultOk;
        }
#endif
        *obj = nullptr;
        return kNoInterface;
    }
    TEST_NO_REFCOUNT
};

// ---------------------------------------------------------------------------
// Module loading.
// ---------------------------------------------------------------------------
using GetFactoryFn = IPluginFactory*(PLUGIN_API*)();

struct LoadedModule {
    fs::path binary;
    IPluginFactory* factory = nullptr;
    void* handle = nullptr;
};

LoadedModule loadModule(const fs::path& bundle) {
    LoadedModule m;
    const std::string name = bundle.stem().string();
#ifdef _WIN32
    m.binary = bundle / "Contents" / APS_VST3_ARCH_DIR / (name + ".vst3");
    HMODULE lib = LoadLibraryW(m.binary.c_str());
    if (!lib) return m;
    m.handle = lib;
    if (auto init = reinterpret_cast<bool (*)()>(GetProcAddress(lib, "InitDll"))) init();
    if (auto get = reinterpret_cast<GetFactoryFn>(GetProcAddress(lib, "GetPluginFactory"))) m.factory = get();
#else
    m.binary = bundle / "Contents" / APS_VST3_ARCH_DIR / (name + ".so");
    void* lib = dlopen(m.binary.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!lib) {
        std::printf("    dlopen: %s\n", dlerror());
        return m;
    }
    m.handle = lib;
    auto entry = reinterpret_cast<bool (*)(void*)>(dlsym(lib, "ModuleEntry"));
    check(entry != nullptr, "ModuleEntry exported");
    if (entry) entry(lib);
    check(dlsym(lib, "ModuleExit") != nullptr, "ModuleExit exported");
    if (auto get = reinterpret_cast<GetFactoryFn>(dlsym(lib, "GetPluginFactory"))) m.factory = get();
#endif
    return m;
}

void unloadModule(LoadedModule& m) {
#ifdef _WIN32
    if (auto exit = reinterpret_cast<bool (*)()>(GetProcAddress(static_cast<HMODULE>(m.handle), "ExitDll"))) exit();
#else
    if (auto exit = reinterpret_cast<bool (*)()>(dlsym(m.handle, "ModuleExit"))) exit();
#endif
    // The libraries stay loaded: the .NET runtime cannot be unloaded.
}

// ---------------------------------------------------------------------------
// One instance: component + controller (the same object) + processor.
// ---------------------------------------------------------------------------
struct Instance {
    IComponent* component = nullptr;
    IAudioProcessor* processor = nullptr;
    IEditController* controller = nullptr;
    ComponentHandler handler;

    bool create(IPluginFactory* factory, const TUID cid) {
        if (factory->createInstance(cid, IComponent::iid, reinterpret_cast<void**>(&component)) != kResultOk || !component)
            return false;
        if (component->initialize(&gHostApp) != kResultOk) return false;
        component->queryInterface(IAudioProcessor::iid, reinterpret_cast<void**>(&processor));
        component->queryInterface(IEditController::iid, reinterpret_cast<void**>(&controller));
        if (controller) controller->setComponentHandler(&handler);
        return processor && controller;
    }
    void destroy() {
        if (controller) {
            controller->setComponentHandler(nullptr);
            controller->release();
            controller = nullptr;
        }
        if (processor) {
            processor->release();
            processor = nullptr;
        }
        if (component) {
            component->terminate();
            component->release();
            component = nullptr;
        }
    }
};

// Audio buffers for all buses, float32 or float64.
template <typename T>
struct Audio {
    std::vector<std::vector<std::vector<T>>> in, out;
    std::vector<std::vector<T*>> inPtrs, outPtrs;
    std::vector<AudioBusBuffers> inBufs, outBufs;

    Audio(IComponent* component, int32 frames) {
        for (int dir = 0; dir < 2; ++dir) {
            const BusDirection d = dir == 0 ? kInput : kOutput;
            auto& chans = dir == 0 ? in : out;
            auto& ptrs = dir == 0 ? inPtrs : outPtrs;
            auto& bufs = dir == 0 ? inBufs : outBufs;
            const int32 count = component->getBusCount(kAudio, d);
            chans.resize(size_t(count));
            ptrs.resize(size_t(count));
            for (int32 b = 0; b < count; ++b) {
                BusInfo info{};
                component->getBusInfo(kAudio, d, b, info);
                chans[size_t(b)].assign(size_t(info.channelCount), std::vector<T>(size_t(frames)));
                for (auto& c : chans[size_t(b)]) ptrs[size_t(b)].push_back(c.data());
            }
            for (int32 b = 0; b < count; ++b) {
                AudioBusBuffers buf{};
                buf.numChannels = int32(ptrs[size_t(b)].size());
                if constexpr (sizeof(T) == 4) buf.channelBuffers32 = reinterpret_cast<Sample32**>(ptrs[size_t(b)].data());
                else buf.channelBuffers64 = reinterpret_cast<Sample64**>(ptrs[size_t(b)].data());
                bufs.push_back(buf);
            }
        }
    }
    void sine(double freq, double rate, double amp) {
        for (auto& bus : in)
            for (auto& ch : bus)
                for (size_t i = 0; i < ch.size(); ++i) ch[i] = T(amp * std::sin(2 * 3.14159265358979 * freq * double(i) / rate));
    }
    double peak() const {
        double p = 0;
        for (auto& bus : out)
            for (auto& ch : bus)
                for (T s : ch) {
                    if (!std::isfinite(double(s))) return NAN;
                    p = std::max(p, std::fabs(double(s)));
                }
        return p;
    }
};

struct Processing {
    ProcessData data{};
    ProcessContext context{};
    ParamChanges params;
    EventList inEvents, outEvents;

    template <typename T>
    void attach(Audio<T>& audio, int32 frames, int32 sampleSize) {
        data.processMode = kRealtime;
        data.symbolicSampleSize = sampleSize;
        data.numSamples = frames;
        data.numInputs = int32(audio.inBufs.size());
        data.numOutputs = int32(audio.outBufs.size());
        data.inputs = audio.inBufs.data();
        data.outputs = audio.outBufs.data();
        data.inputParameterChanges = &params;
        data.inputEvents = &inEvents;
        data.outputEvents = &outEvents;
        data.processContext = &context;
        context.state = ProcessContext::kTempoValid | ProcessContext::kPlaying;
        context.tempo = 120;
        context.sampleRate = 48000;
    }

    bool run(IAudioProcessor* p, int blocks) {
        bool ok = true;
        for (int b = 0; b < blocks; ++b) {
            ok = p->process(data) == kResultOk && ok;
            context.projectTimeSamples += data.numSamples;
            params.clear();
            inEvents.clear();
        }
        return ok;
    }
};

struct Tested {
    LoadedModule module;
    Instance* instance = nullptr;
    TUID cid{};
    bool hasEditor = false;
    std::string name;
};

constexpr uint32 kAudioPlugSharpId = 0xB0FA8B5C;
constexpr uint32 kProcessorId = 0xC4B5BA02;
constexpr ParamID kParamBase = 128;

// ---------------------------------------------------------------------------
// The full lifecycle of one plugin.
// ---------------------------------------------------------------------------
bool testPlugin(const fs::path& bundle, Tested& t) {
    std::printf("\n== %s ==\n", bundle.filename().string().c_str());
    t.module = loadModule(bundle);
    check(t.module.factory != nullptr, "GetPluginFactory");
    if (!t.module.factory) return false;
    IPluginFactory* factory = t.module.factory;

    PFactoryInfo finfo{};
    check(factory->getFactoryInfo(&finfo) == kResultOk, "getFactoryInfo");
    check(factory->countClasses() == 1, "one class in the factory");
    IPluginFactory2* factory2 = nullptr;
    factory->queryInterface(IPluginFactory2::iid, reinterpret_cast<void**>(&factory2));
    IPluginFactory3* factory3 = nullptr;
    factory->queryInterface(IPluginFactory3::iid, reinterpret_cast<void**>(&factory3));
    check(factory2 && factory3, "IPluginFactory2 and IPluginFactory3");
    PClassInfo2 info{};
    if (!factory2 || factory2->getClassInfo2(0, &info) != kResultOk) { check(false, "getClassInfo2"); return false; }
    PClassInfoW winfo{};
    check(factory3 && factory3->getClassInfoUnicode(0, &winfo) == kResultOk && narrow(winfo.name) == info.name,
          "getClassInfoUnicode matches getClassInfo2");
    std::memcpy(t.cid, info.cid, sizeof(TUID));
    t.name = info.name;
    const FUID fuid = FUID::fromTUID(info.cid);
    char cidText[33] = {};
    fuid.toString(cidText);
    std::printf("    %s | %s | %s | v%s | %s | %s | cid %s\n", info.name, info.vendor, finfo.url, info.version,
                info.category, info.subCategories, cidText);
    check(std::strcmp(info.category, kVstAudioEffectClass) == 0, "class category is Audio Module Class");
    // Compatibility with the C++/CLI bridge: FUID(AUDIO_PLUG_SHARP_ID, PROCESSOR_ID, id >> 32, id & 0xffffffff).
    check(fuid.getLong1() == kAudioPlugSharpId && fuid.getLong2() == kProcessorId, "class UID uses the bridge's formula");
    if (factory2) factory2->release();
    if (factory3) factory3->release();

    auto* inst = new Instance();
    t.instance = inst;
    check(inst->create(factory, t.cid), "createInstance + initialize; IAudioProcessor and IEditController on the component");
    if (!inst->processor || !inst->controller) return false;
    TUID controllerCid{};
    check(inst->component->getControllerClassId(controllerCid) != kResultOk, "single component (no separate controller class)");
    IMidiMapping* midi = nullptr;
    inst->component->queryInterface(IMidiMapping::iid, reinterpret_cast<void**>(&midi));
    check(midi != nullptr, "IMidiMapping");

    // Buses.
    for (int dir = 0; dir < 2; ++dir) {
        const BusDirection d = dir == 0 ? kInput : kOutput;
        for (int32 b = 0; b < inst->component->getBusCount(kAudio, d); ++b) {
            BusInfo bus{};
            inst->component->getBusInfo(kAudio, d, b, bus);
            std::printf("    %s bus %d: %s, %d ch%s\n", dir == 0 ? "input " : "output", b, narrow(bus.name).c_str(),
                        bus.channelCount, bus.busType == kMain ? ", main" : "");
        }
    }
    check(inst->component->getBusCount(kEvent, kInput) == 1 && inst->component->getBusCount(kEvent, kOutput) == 1,
          "one event bus each way");
    std::vector<SpeakerArrangement> ins(size_t(inst->component->getBusCount(kAudio, kInput)));
    std::vector<SpeakerArrangement> outs(size_t(inst->component->getBusCount(kAudio, kOutput)));
    for (size_t i = 0; i < ins.size(); ++i) inst->processor->getBusArrangement(kInput, int32(i), ins[i]);
    for (size_t i = 0; i < outs.size(); ++i) inst->processor->getBusArrangement(kOutput, int32(i), outs[i]);
    check(inst->processor->setBusArrangements(ins.data(), int32(ins.size()), outs.data(), int32(outs.size())) == kResultTrue,
          "setBusArrangements accepts the plugin's own arrangement");
    if (!outs.empty() && SpeakerArr::getChannelCount(outs[0]) == 2) {
        std::vector<SpeakerArrangement> bad = outs;
        bad[0] = SpeakerArr::k51;
        check(inst->processor->setBusArrangements(ins.data(), int32(ins.size()), bad.data(), int32(bad.size())) != kResultTrue,
              "setBusArrangements refuses another channel count");
    }

    // Parameters.
    const int32 paramCount = inst->controller->getParameterCount();
    std::vector<ParameterInfo> params(static_cast<size_t>(paramCount));
    bool idsOk = true;
    for (int32 i = 0; i < paramCount; ++i) {
        auto& p = params[size_t(i)];
        inst->controller->getParameterInfo(i, p);
        idsOk = idsOk && p.id == kParamBase + ParamID(i);
        String128 text{}, defText{};
        const double v = inst->controller->getParamNormalized(p.id);
        inst->controller->getParamStringByValue(p.id, v, text);
        inst->controller->getParamStringByValue(p.id, p.defaultNormalizedValue, defText);
        std::printf("    param %d: id %u \"%s\" default %.3f (%s), value %.3f (%s)%s\n", i, p.id, narrow(p.title).c_str(),
                    p.defaultNormalizedValue, narrow(defText).c_str(), v, narrow(text).c_str(),
                    (p.flags & ParameterInfo::kCanAutomate) ? ", automatable" : "");
    }
    check(idsOk, "parameter IDs are index + 128 (as with the C++/CLI bridge)");
    if (paramCount > 0) {
        const ParamID id = params[0].id;
        inst->controller->setParamNormalized(id, 0.5);
        check(std::fabs(inst->controller->getParamNormalized(id) - 0.5) < 1e-9, "setParamNormalized / getParamNormalized");
        check(inst->handler.total() == 0, "host values are not reported back as edits");
    }
    if (midi) {
        ParamID mapped = 0;
        const bool cc7 = midi->getMidiControllerAssignment(0, 0, 7, mapped) == kResultTrue;
        std::printf("    MIDI CC 7: %s\n", cc7 ? ("parameter " + std::to_string(mapped)).c_str() : "not mapped");
        if (t.name == "UILayout Test") check(cc7 && mapped == kParamBase, "CC 7 maps to the gain parameter (AddMidiControllerMapping)");
        ParamID other = 0;
        check(midi->getMidiControllerAssignment(0, 0, 1, other) != kResultTrue, "unmapped CC has no assignment");
        midi->release();
    }

    IPlugView* view = inst->controller->createView(ViewType::kEditor);
    t.hasEditor = view != nullptr;
    std::printf("    editor: %s\n", t.hasEditor ? "yes" : "no");
    if (view) view->release();

    // Processing, 32 bit, then 64 bit if supported.
    const bool hasInput = inst->component->getBusCount(kAudio, kInput) > 0;
    for (int32 size : {int32(kSample32), int32(kSample64)}) {
        const bool is64 = size == kSample64;
        if (inst->processor->canProcessSampleSize(size) != kResultTrue) {
            std::printf("    %s: not supported\n", is64 ? "64 bit" : "32 bit");
            continue;
        }
        std::printf("    -- %s --\n", is64 ? "64 bit" : "32 bit");
        constexpr int32 kFrames = 256;
        ProcessSetup setup{kRealtime, size, kFrames, 48000};
        check(inst->processor->setupProcessing(setup) == kResultOk, "setupProcessing");
        check(inst->component->setActive(true) == kResultOk, "setActive(true)");
        check(inst->processor->setProcessing(true) == kResultOk, "setProcessing(true)");

        auto body = [&](auto& audio) {
            Processing proc;
            proc.attach(audio, kFrames, size);
            audio.sine(220, 48000, 0.5);
            if (hasInput && paramCount > 0) {
                proc.params.add(params[0].id, 0, 0.5);
                check(proc.run(inst->processor, 10), "process");
                const double p1 = audio.peak();
                proc.params.add(params[0].id, 0, 1.0);
                proc.run(inst->processor, 10);
                const double p2 = audio.peak();
                std::printf("    peak at 0.5: %.3f, at 1.0: %.3f\n", p1, p2);
                check(std::fabs(p1 - 0.249) < 0.01, "parameter change reaches the DSP (0.5 -> -6.1 dB)");
                check(p2 > 1.9, "parameter change reaches the DSP (1.0 -> +12 dB)");
                // A change in the middle of the block, then a later block: the value holds.
                proc.params.add(params[0].id, 128, 0.5);
                proc.run(inst->processor, 3);
                check(std::fabs(audio.peak() - 0.249) < 0.01, "a mid-block change point holds afterwards");
            } else {
                proc.inEvents.events.push_back(noteEvent(true, 0, 60, 0.8f));
                check(proc.run(inst->processor, 100), "process");
                const double on = audio.peak();
                proc.inEvents.events.push_back(noteEvent(false, 0, 60, 0.0f));
                proc.run(inst->processor, 400);
                const double off = audio.peak();
                std::printf("    note on: peak %.3f, note off: peak %.3f\n", on, off);
                check(on > 0.1 && off < 0.001, "note on/off produce and stop sound");
                // A note on with velocity 0 is a note off.
                proc.inEvents.events.push_back(noteEvent(true, 0, 62, 0.8f));
                proc.run(inst->processor, 50);
                Event zero = noteEvent(true, 0, 62, 0.0f);
                proc.inEvents.events.push_back(zero);
                proc.run(inst->processor, 400);
                check(audio.peak() < 0.001, "note on with velocity 0 stops the note");
            }
            // Events-only block (a parameter flush): no audio buffers, 0 samples.
            if (paramCount > 0) {
                ProcessData flush{};
                flush.processMode = kRealtime;
                flush.symbolicSampleSize = size;
                ParamChanges changes;
                changes.add(params[0].id, 0, 0.75);
                flush.inputParameterChanges = &changes;
                check(inst->processor->process(flush) == kResultOk, "events-only process (flush)");
            }
        };
        if (is64) {
            Audio<double> audio(inst->component, kFrames);
            body(audio);
        } else {
            Audio<float> audio(inst->component, kFrames);
            body(audio);
        }
        check(inst->processor->setProcessing(false) == kResultOk, "setProcessing(false)");
        check(inst->component->setActive(false) == kResultOk, "setActive(false)");
    }

    // State: the component's bytes; the controller has none of its own.
    MemoryStream state;
    check(inst->component->getState(&state) == kResultOk && !state.bytes.empty(), "component getState");
    MemoryStream controllerState;
    check(inst->controller->getState(&controllerState) == kResultOk && controllerState.bytes.empty(),
          "controller getState is empty");
    // The plugin saves the DSP value (ProcessValue): the last change processed, the 0.75 of the flush above. (A real
    // host keeps the controller and processor values equal; this test set the controller to 0.5 on its own.)
    const double saved = 0.75;
    if (paramCount > 0) inst->controller->setParamNormalized(params[0].id, 0.1);
    inst->handler.clear();
    state.pos = 0;
    check(inst->component->setState(&state) == kResultOk, "component setState");
    state.pos = 0;
    check(inst->controller->setComponentState(&state) == kResultOk, "controller setComponentState");
    check(inst->handler.total() == 0, "state load does not report edits to the host");
    if (paramCount > 0)
        check(std::fabs(inst->controller->getParamNormalized(params[0].id) - saved) < 1e-9,
              "getParamNormalized after setState is the saved value");
    std::printf("    state: %zu bytes\n", state.bytes.size());
    return true;
}

// ---------------------------------------------------------------------------
// GUI: two editors of one plugin side by side in one window (see ClapTestHost's gui::test for the checks).
// ---------------------------------------------------------------------------
namespace gui {

#ifdef _WIN32
const char* const kPlatform = kPlatformTypeHWND;
#else
const char* const kPlatform = kPlatformTypeX11EmbedWindowID;
#endif

struct Editor {
    Instance instance;
    IPlugView* view = nullptr;
    PlugFrame frame;
    intptr_t slot = 0;
    std::unique_ptr<Audio<float>> audio;
    std::unique_ptr<Processing> proc;
    ParamID paramId = kParamBase;

    void process() {
        if (!proc) return;
        // The host's part of VST3: the plugin's edits come back to the processor as parameter changes.
        if (instance.handler.lastValue >= 0) proc->params.add(instance.handler.lastId, 0, instance.handler.lastValue);
        proc->run(instance.processor, 1);
    }
    bool open() {
        view = instance.controller->createView(ViewType::kEditor);
        if (!view) return false;
        view->setFrame(&frame);
        return view->attached(reinterpret_cast<void*>(slot), kPlatform) == kResultOk;
    }
    void close() {
        if (!view) return;
        view->removed();
        view->setFrame(nullptr);
        view->release();
        view = nullptr;
    }
};

void pump(int milliseconds, aps_test::DawWindow& daw, std::vector<Editor>& editors) {
    const auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    do {
        daw.pumpEvents();
        for (auto& e : editors) {
            e.process();
            e.frame.service();
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    } while (std::chrono::steady_clock::now() < end);
}

void test(const Tested& t) {
    std::printf("\n== GUI (%s): two %s editors in one window ==\n", kPlatform, t.name.c_str());
    constexpr int32 kFrames = 256;
    std::vector<Editor> editors(2);
    for (auto& e : editors) {
        if (!e.instance.create(t.module.factory, t.cid)) { check(false, "create an instance"); return; }
        ProcessSetup setup{kRealtime, kSample32, kFrames, 48000};
        e.instance.processor->setupProcessing(setup);
        e.instance.component->setActive(true);
        e.instance.processor->setProcessing(true);
        e.audio = std::make_unique<Audio<float>>(e.instance.component, kFrames);
        e.audio->sine(220, 48000, 0.5);
        e.proc = std::make_unique<Processing>();
        e.proc->attach(*e.audio, kFrames, kSample32);
    }
    const auto shutdown = [&] {
        for (auto& e : editors) {
            e.close();
            if (!e.instance.processor) continue;
            e.instance.processor->setProcessing(false);
            e.instance.component->setActive(false);
            e.instance.destroy();
        }
    };
    auto& a = editors[0];
    auto& b = editors[1];

    for (auto& e : editors) {
        e.view = e.instance.controller->createView(ViewType::kEditor);
        if (!e.view) { check(false, "createView"); shutdown(); return; }
    }
    check(a.view->isPlatformTypeSupported(kPlatform) == kResultTrue, "isPlatformTypeSupported");
    check(a.view->isPlatformTypeSupported("NSView") != kResultTrue, "other platform types are refused");
    check(a.instance.controller->createView(ViewType::kEditor) == nullptr, "one view at a time");
    ViewRect rect{};
    check(a.view->getSize(&rect) == kResultOk, "getSize");
    const int width = rect.getWidth(), height = rect.getHeight();
    std::printf("    editor size %dx%d\n", width, height);
    const bool uilayout = t.name == "UILayout Test";
    if (uilayout) check(width == 320 && height == 160, "getSize is the editor's size");
    else check(width > 0 && height > 0 && width <= 4096 && height <= 4096, "getSize");
    for (auto& e : editors) { e.view->release(); e.view = nullptr; }

    constexpr int kMargin = 10;
    const auto x0 = [&](int i) { return kMargin + i * (width + kMargin); };
    aps_test::DawWindow daw;
    if (!daw.open(3 * kMargin + 2 * width, 2 * kMargin + height, "Vst3TestHost - two editors")) {
        check(false, "open a top-level window");
        shutdown();
        return;
    }
    for (int i = 0; i < 2; ++i) {
        auto& e = editors[size_t(i)];
        e.slot = daw.addSlot(x0(i), kMargin, width, height);
        check(e.open(), "createView + attached (opens the editor)");
    }
    pump(800, daw, editors);

    const auto renders = [&](const Capture& c, int i) {
        const int area = width * height;
        const int gray = c.countNear(x0(i), kMargin, x0(i) + width, kMargin + height, 128, 128, 128, 3);
        return area - gray > area / 2;
    };
    Capture first = daw.capture();
    for (int i = 0; i < 2; ++i) check(renders(first, i), "editor renders (window capture)");
    std::string captureName = "vst3-gui-" + t.name + ".bmp";
    for (auto& ch : captureName) if (ch == ' ') ch = '_';
    const auto path = executableDir() / captureName;
    saveBmp(first, path);
    std::printf("    capture: %s\n", path.string().c_str());
    check(daw.hasEditor(0), "editor child window exists");

    int sliderY = height / 2;
    if (uilayout) {
        const auto sliderRow = [&](const Capture& c, int i) {
            for (int y = kMargin; y < kMargin + height; ++y)
                if (c.countNear(x0(i), y, x0(i) + width, y + 1, 0, 0, 0, 8) > width / 2) return y - kMargin;
            return -1;
        };
        for (int i = 0; i < 2; ++i) {
            const int meterGreen = first.countNear(x0(i), kMargin, x0(i) + width, kMargin + height, 64, 200, 96, 12);
            sliderY = sliderRow(first, i);
            std::printf("    editor %d: %d meter pixels, slider at y %d\n", i, meterGreen, sliderY);
            check(meterGreen > 500, "level meter shows the processed audio");
            check(sliderY > 0, "slider found");
        }
        if (sliderY <= 0) sliderY = height / 2;
        sliderY += 1;

        // Editor -> host: one gesture through IComponentHandler.
        for (auto& e : editors) e.instance.handler.clear();
        daw.mouse(0, Mouse::Move, 40, sliderY);
        pump(60, daw, editors);
        daw.mouse(0, Mouse::Down, 40, sliderY);
        pump(60, daw, editors);
        {
            // During the gesture, the host's echo of an edit (one point at sample 0) ramps across the block:
            // the gain near the start is still close to the old (low) value, the gain at the end is the new one.
            a.proc->params.add(a.paramId, 0, 1.0);
            a.proc->run(a.instance.processor, 1);
            const auto& inCh = a.audio->in[0][0];
            const auto& outCh = a.audio->out[0][0];
            const auto gainAt = [&](size_t from, size_t to) {
                for (size_t i = from; i < to; ++i)
                    if (std::fabs(inCh[i]) > 0.2f) return std::fabs(outCh[i] / inCh[i]);
                return -1.0f;
            };
            const float early = gainAt(0, 64), late = gainAt(inCh.size() - 64, inCh.size());
            std::printf("    echoed edit during the gesture: gain %.3f early in the block, %.3f at its end\n", early, late);
            // A step would put the full +12 dB (gain 3.98) at the start; the ramp is still near the old value there
            // ("late" is measured some way before the block's end, partway up the ramp).
            check(early >= 0 && early < 0.5f && late > early * 4, "an echoed edit ramps across the block (no step)");
            a.proc->params.add(a.paramId, 0, a.instance.handler.lastValue >= 0 ? a.instance.handler.lastValue : 0.0);
            a.proc->run(a.instance.processor, 1);
        }
        for (int x = 60; x <= 320; x += 20) {
            daw.mouse(0, Mouse::Move, x, sliderY);
            pump(40, daw, editors);
        }
        daw.mouse(0, Mouse::Up, 320, sliderY);
        pump(200, daw, editors);
        const auto& h = a.instance.handler;
        const double after = a.instance.controller->getParamNormalized(a.paramId);
        std::printf("    drag: beginEdit %d, endEdit %d, %d performEdit (last %.3f, id %u); value %.3f\n", h.begins,
                    h.ends, h.performs, h.lastValue, h.lastId, after);
        check(h.begins == 1 && h.ends == 1 && h.performs >= 3, "slider drag reaches the host as one gesture with values");
        check(h.offThread == 0, "edits reach the host on the main thread");
        check(h.lastId == a.paramId && h.lastValue > 0.99 && after > 0.99, "the edit and getParamNormalized reach the maximum");
        check(b.instance.handler.total() == 0, "the other instance is unaffected");
    }

    // Host -> editor: setParamNormalized on the controller (VST3 automation display).
    const auto hostSet = [&](Editor& e, double v) {
        e.instance.controller->setParamNormalized(e.paramId, v);
        pump(300, daw, editors);
    };
    hostSet(b, 0.75);
    Capture high = daw.capture();
    hostSet(b, 0.25);
    Capture low = daw.capture();
    const int changed = uilayout
        ? high.countDifferent(low, x0(1), kMargin + sliderY - 10, x0(1) + width, kMargin + sliderY + 10)
        : high.countDifferent(low, x0(1), kMargin, x0(1) + width, kMargin + height);
    std::printf("    host value change: %d editor pixels changed\n", changed);
    check(changed > 50, "the editor follows host parameter changes");
    check(b.instance.handler.total() == 0, "host values are not reported back as edits");

    // A resizable editor: the host shrinks it (onSize), it reports and draws the new size (the strip it no longer
    // covers shows the slot's gray), then grows back.
    if (a.view->canResize() == kResultTrue) {
        const int w2 = width - 120, h2 = height - 80;
        ViewRect smaller(0, 0, w2, h2);
        check(a.view->checkSizeConstraint(&smaller) == kResultTrue && smaller.getWidth() == w2 && smaller.getHeight() == h2,
              "checkSizeConstraint accepts a smaller size");
        check(a.view->onSize(&smaller) == kResultTrue, "onSize (smaller)");
        pump(400, daw, editors);
        ViewRect now{};
        a.view->getSize(&now);
        Capture c = daw.capture();
        const int strip = c.countNear(x0(0) + w2, kMargin, x0(0) + width, kMargin + height, 128, 128, 128, 3);
        const int stripArea = (width - w2) * height;
        std::printf("    resized to %dx%d (reported %dx%d), freed strip %d/%d gray\n", w2, h2, now.getWidth(),
                    now.getHeight(), strip, stripArea);
        check(now.getWidth() == w2 && now.getHeight() == h2, "getSize reports the new size");
        check(strip > stripArea * 9 / 10, "the editor window shrank");
        check(c.countNear(x0(0), kMargin, x0(0) + w2, kMargin + h2, 128, 128, 128, 3) < w2 * h2 / 2,
              "the resized editor renders");
        ViewRect full(0, 0, width, height);
        check(a.view->onSize(&full) == kResultTrue, "onSize (back to the original size)");
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
        ViewRect during{};
        a.view->getSize(&during);
        const uint32_t fw = uint32_t(during.getWidth()), fh = uint32_t(during.getHeight());
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

        // Keys through IPlugView::onKeyDown/onKeyUp, as Reaper offers them: what the editor declines stays a host
        // shortcut. UILayout Test claims only the keys it maps (F11, Enter, Alt), not the space bar.
        check(a.view->onKeyDown(' ', KEY_SPACE, 0) == kResultFalse, "onKeyDown: an unmapped key (space) is left to the host");
        a.view->onKeyUp(' ', KEY_SPACE, 0);
        const auto f11 = [&] {
            const bool claimed = a.view->onKeyDown(0, KEY_F11, 0) == kResultTrue;
            pump(100, daw, editors);
            a.view->onKeyUp(0, KEY_F11, 0);
            pump(500, daw, editors);
            return claimed;
        };
        check(f11(), "onKeyDown: a mapped key (F11) is claimed");
        check(daw.isFullscreen(0), "F11 through onKeyDown: the editor goes fullscreen");
        check(f11() && !daw.isFullscreen(0), "F11 again: back in the host's window");
        pump(300, daw, editors);
        check(renders(daw.capture(), 0), "the editor renders in the host's window again");
    }

    // DPI scaling: the host's content scale (IPlugViewContentScaleSupport). The editor keeps its logical size
    // (320 x 160); the view becomes logical x scale pixels (the plugin asks with resizeView, the host answers with
    // onSize) and the editor draws at the scale. The slot stays 320 x 160, so the capture shows its top-left part.
    if (uilayout) {
        // The first row with white text (the "Gain" header): it moves down 2x at scale 2.
        const auto trackRow = [&](const Capture& c) {
            for (int y = kMargin; y < kMargin + int(height); ++y)
                if (c.countNear(x0(0), y, x0(0) + int(width), y + 1, 255, 255, 255, 40) > 0) return y - kMargin;
            return -1;
        };
        const int row1 = trackRow(daw.capture());
        IPlugViewContentScaleSupport* scaling = nullptr;
        a.view->queryInterface(IPlugViewContentScaleSupport::iid, reinterpret_cast<void**>(&scaling));
        check(scaling != nullptr, "the view supports IPlugViewContentScaleSupport");
        if (scaling) {
            const auto setScale = [&](float scale) {
                const int before = a.frame.resizeRequests;
                const bool ok = scaling->setContentScaleFactor(scale) == kResultTrue;
                pump(400, daw, editors);
                return ok && a.frame.resizeRequests > before;
            };
            check(setScale(2.0f), "setContentScaleFactor(2): the plugin asks the host to resize (resizeView)");
            ViewRect r{};
            a.view->getSize(&r);
            int ew = 0, eh = 0;
            daw.editorSize(0, ew, eh);
            const int row2 = trackRow(daw.capture());
            std::printf("    scale 2: resizeView %dx%d, getSize %dx%d, window %dx%d, text row %d (%d at scale 1)\n",
                        a.frame.lastResize.getWidth(), a.frame.lastResize.getHeight(), r.getWidth(), r.getHeight(), ew, eh,
                        row2, row1);
            check(a.frame.lastResize.getWidth() == 2 * width && a.frame.lastResize.getHeight() == 2 * height,
                  "resizeView asks for the logical size x 2");
            check(r.getWidth() == 2 * width && r.getHeight() == 2 * height, "getSize reports window pixels at scale 2");
            check(ew == 2 * width && eh == 2 * height, "the editor window has the scaled size");
            check(row1 > 0 && row2 >= 2 * row1 - 3 && row2 <= 2 * row1 + 3, "the editor draws at scale 2 (its text moved down 2x)");
            check(setScale(1.0f), "setContentScaleFactor(1): back");
            a.view->getSize(&r);
            daw.editorSize(0, ew, eh);
            check(r.getWidth() == width && r.getHeight() == height && ew == width && eh == height, "back to the logical size at scale 1");
            check(trackRow(daw.capture()) == row1 && renders(daw.capture(), 0), "the editor draws at scale 1 again");
            scaling->release();
        }
    }

    // Close one editor and reopen it, then close both.
    a.close();
    pump(50, daw, editors);
    check(!daw.hasEditor(0), "removed + release removes the editor window");
    check(a.frame.timers.empty() && a.frame.fds.empty(), "run loop timers and handlers unregistered");
    check(a.open(), "editor reopens");
    pump(300, daw, editors);
    check(renders(daw.capture(), 0), "reopened editor renders");

    shutdown();
    for (auto& e : editors) check(e.frame.timers.empty() && e.frame.fds.empty(), "run loop timers and handlers unregistered");
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
        const auto dir = executableDir() / "vst3";
        // The examples (generic checks) and the test plugins (detailed checks: UILayoutTestPlugin).
        std::vector<std::string> names = {"SimpleExample", "MidiExample", "UILayoutExample", "UILayoutTestPlugin"};
#ifdef _WIN32
        names.push_back("WPFExample");
#endif
        for (const auto& name : names) paths.push_back(dir / (name + ".vst3"));
    }

    std::vector<Tested> tested(paths.size());
    for (size_t i = 0; i < paths.size(); ++i) testPlugin(paths[i], tested[i]);

    // Editors, while the other modules' instances stay alive.
    for (const auto& t : tested)
        if (t.hasEditor) gui::test(t);

    // All plugins stay alive together: several AudioPlugSharp modules in one process, one runtime.
    std::printf("\n== %zu plugin instances alive at once ==\n", tested.size());
    for (auto& t : tested) {
        if (!t.instance || !t.instance->processor) continue;
        ProcessSetup setup{kRealtime, kSample32, 128, 44100};
        check(t.instance->processor->setupProcessing(setup) == kResultOk && t.instance->component->setActive(true) == kResultOk,
              "re-activate at 44.1 kHz");
        t.instance->component->setActive(false);
    }
    for (auto& t : tested) {
        if (t.instance) {
            t.instance->destroy();
            delete t.instance;
        }
        if (t.module.handle) unloadModule(t.module);
    }

    std::printf("\n%s (%d failure%s)\n", gFailures == 0 ? "PASSED" : "FAILED", gFailures, gFailures == 1 ? "" : "s");
    return gFailures == 0 ? 0 : 1;
}
