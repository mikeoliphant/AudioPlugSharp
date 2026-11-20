// Vst3Adapter.cpp - the generic VST3 adapter for AudioPlugSharp plugins.
//
// One binary serves every plugin. It is shipped in a VST3 bundle, <Plugin>.vst3/Contents/<arch>-<os>/, next to
// <Plugin>.dll (the managed plugin) and the bridge (AudioPlugSharpBridge.dll, AudioPlugSharp.dll,
// runtimeconfig). The binary's name gives the plugin assembly: <Plugin>.vst3 (Windows) / <Plugin>.so (Linux),
// with a trailing "Bridge" removed (<Plugin>Bridge.vst3 is the C++/CLI bridge's name). The adapter only
// translates between VST3 and the generated ABI; plugin behaviour lives in C#.
//
// Written against the VST3 interfaces only (pluginterfaces, MIT), without the SDK's helper classes.
//
// Structure: a single component. One object is the IComponent/IAudioProcessor and the IEditController (the
// host finds the controller by queryInterface; getControllerClassId returns kNotImplemented), because both
// halves share one managed plugin instance. Compatibility with the C++/CLI bridge: the same
// processor class UID, parameter IDs = index + 128, and the same state bytes.
//
// Threading follows VST3: setup, state, parameters and the view on the UI thread; process on the audio thread.
// Plugin edits go to the host through IComponentHandler (UI thread); the host sends them back to the processor
// as parameter changes, so (unlike CLAP) they are not injected into process here.
#ifndef INIT_CLASS_IID
#define INIT_CLASS_IID // this translation unit defines the interfaces' FUID iid statics
#endif

#include "aps/EditorWindow.h"
#include "aps/ManagedPluginFactory.h"

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
#include "pluginterfaces/vst/ivstunits.h"
#include "pluginterfaces/vst/vstspeaker.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace fs = std::filesystem;

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

// The C++/CLI bridge's UID parts and parameter base (AudioPlugSharpFactory.h, AudioPlugSharpController.h).
constexpr uint32 kAudioPlugSharpId = 0xB0FA8B5C;
constexpr uint32 kProcessorId = 0xC4B5BA02;
constexpr ParamID kParamBase = 128; // PLUGIN_PARAMETER_USER_START

void moduleLog(const std::string& message) { std::fprintf(stderr, "[AudioPlugSharp VST3] %s\n", message.c_str()); }

bool iidEqual(const TUID a, const TUID b) { return std::memcmp(a, b, sizeof(TUID)) == 0; }

// A VST3 key (VirtualKeyCodes, or the character for keys without one) as a Windows virtual-key code (the editor's
// key codes, EditorInputEvent.key), or 0.
int vst3VirtualKey(char16 character, int16 keyCode) {
    static constexpr uint8_t kCodes[] = {
        0,    0x08, 0x09, 0x0C, 0x0D, 0x13, 0x1B, 0x20, 0x22, 0x23, 0x24, // -, BACK..HOME (NEXT = page down)
        0x25, 0x26, 0x27, 0x28, 0x21, 0x22,                               // LEFT..PAGEDOWN
        0x29, 0x2A, 0x0D, 0x2C, 0x2D, 0x2E, 0x2F,                         // SELECT..HELP
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,       // NUMPAD0..9
        0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,                               // MULTIPLY..DIVIDE
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, // F1..F12
        0x90, 0x91,                                                       // NUMLOCK, SCROLL
        0xA0, 0xA2, 0xA4,                                                 // SHIFT, CONTROL, ALT (left ones)
        0xBB, 0x5D,                                                       // EQUALS, CONTEXTMENU
        0xB3, 0xB2, 0xB1, 0xB0, 0xAF, 0xAE,                               // media keys
        0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, // F13..F24
        0x5B,                                                             // SUPER
    };
    if (keyCode > 0 && size_t(keyCode) < sizeof(kCodes)) return kCodes[keyCode];
    uint32_t c = keyCode >= VKEY_FIRST_ASCII ? uint32_t(keyCode - VKEY_FIRST_ASCII) : uint32_t(character);
    if (c >= 'a' && c <= 'z') c -= 'a' - 'A';
    if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return int(c);
    switch (c) { // US layout punctuation (OEM keys)
        case ';': return 0xBA;
        case '=': return 0xBB;
        case ',': return 0xBC;
        case '-': return 0xBD;
        case '.': return 0xBE;
        case '/': return 0xBF;
        case '`': return 0xC0;
        case '[': return 0xDB;
        case '\\': return 0xDC;
        case ']': return 0xDD;
        case '\'': return 0xDE;
        default: return 0;
    }
}

// UTF-8 -> UTF-16 into a fixed, NUL-terminated buffer (truncates).
void toUtf16(const std::string& s, char16* dst, size_t capacity) {
    size_t n = 0;
    for (size_t i = 0; i < s.size() && n + 1 < capacity;) {
        const unsigned char c = static_cast<unsigned char>(s[i]);
        uint32_t cp;
        int len;
        if (c < 0x80) { cp = c; len = 1; }
        else if ((c >> 5) == 0x6) { cp = c & 0x1F; len = 2; }
        else if ((c >> 4) == 0xE) { cp = c & 0x0F; len = 3; }
        else if ((c >> 3) == 0x1E) { cp = c & 0x07; len = 4; }
        else { ++i; continue; }
        if (i + len > s.size()) break;
        for (int k = 1; k < len; ++k) cp = (cp << 6) | (static_cast<unsigned char>(s[i + k]) & 0x3F);
        i += len;
        if (cp >= 0x10000) {
            if (n + 2 >= capacity) break;
            cp -= 0x10000;
            dst[n++] = static_cast<char16>(0xD800 + (cp >> 10));
            dst[n++] = static_cast<char16>(0xDC00 + (cp & 0x3FF));
        } else {
            dst[n++] = static_cast<char16>(cp);
        }
    }
    dst[n] = 0;
}

void copyAscii(const std::string& s, char8* dst, size_t capacity) {
    const size_t n = std::min(s.size(), capacity - 1);
    std::memcpy(dst, s.data(), n);
    dst[n] = 0;
}

// ---------------------------------------------------------------------------
// Module state: the binary's location, one runtime boot, and the cached plugin description.
// ---------------------------------------------------------------------------
struct Module {
    std::mutex mutex;
    fs::path binary, dir, pluginAssembly;

    std::unique_ptr<aps::ManagedPluginFactory> factory;
    bool factoryFailed = false;

    bool infoReady = false, infoFailed = false;
    aps::PluginInfo info;
    TUID cid{};
};

Module g;

fs::path ownPath() {
#ifdef _WIN32
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&moduleLog), &module))
        return {};
    wchar_t buf[MAX_PATH];
    const DWORD n = GetModuleFileNameW(module, buf, MAX_PATH);
    return fs::path(std::wstring(buf, n));
#else
    Dl_info info{};
    if (!dladdr(reinterpret_cast<void*>(&moduleLog), &info) || !info.dli_fname) return {};
    return fs::path(info.dli_fname);
#endif
}

// Caller holds g.mutex.
bool locateLocked() {
    if (!g.binary.empty()) return true;
    g.binary = ownPath();
    if (g.binary.empty()) return false;
    g.dir = g.binary.parent_path();
    std::string stem = g.binary.stem().string();
    if (stem.size() > 6 && stem.compare(stem.size() - 6, 6, "Bridge") == 0) stem.resize(stem.size() - 6);
    g.pluginAssembly = g.dir / (stem + ".dll");
    return true;
}

// Boots the runtime on first use. Caller holds g.mutex.
aps::ManagedPluginFactory* factoryLocked() {
    if (g.factory) return g.factory.get();
    if (g.factoryFailed || !locateLocked()) return nullptr;
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
        info.name = "AudioPlugSharp VST3 scan";
        return info;
    }
    void log(aps::LogLevel level, std::string_view message) override {
        if (level >= aps::LogLevel::Warning) moduleLog(std::string(message));
    }
    void beginEdit(uint32_t) override {}
    void performEdit(uint32_t, double) override {}
    void endEdit(uint32_t) override {}
};

// Loads a throwaway instance to read the description. Caller holds g.mutex.
bool infoLocked() {
    if (g.infoReady) return true;
    if (g.infoFailed) return false;
    auto* factory = factoryLocked();
    if (!factory) { g.infoFailed = true; return false; }
    ProbeHost host;
    std::string error;
    auto plugin = factory->load(g.pluginAssembly, host, error);
    if (!plugin) {
        moduleLog("could not load " + g.pluginAssembly.string() + ": " + error);
        g.infoFailed = true;
        return false;
    }
    g.info = plugin->getInfo().value;
    plugin.reset();
    // The C++/CLI bridge's formula: FUID(AUDIO_PLUG_SHARP_ID, AUDIO_PLUG_SHARP_PROCESSOR_ID, id >> 32, id & 0xffffffff).
    const FUID fuid(kAudioPlugSharpId, kProcessorId, static_cast<uint32>(g.info.pluginId >> 32),
                    static_cast<uint32>(g.info.pluginId & 0xffffffffu));
    fuid.toTUID(g.cid);
    g.infoReady = true;
    return true;
}

// ---------------------------------------------------------------------------
// Reference counting for the small helper objects.
// ---------------------------------------------------------------------------
class RefCounted {
public:
    uint32 addRefImpl() { return ++refs_; }
    template <typename T>
    uint32 releaseImpl(T* self) {
        const uint32 r = --refs_;
        if (r == 0) delete self;
        return r;
    }

private:
    std::atomic<uint32> refs_{1};
};

#define APS_FUNKNOWN_REFCOUNT                                                        \
    uint32 PLUGIN_API addRef() override { return refs_.addRefImpl(); }               \
    uint32 PLUGIN_API release() override { return refs_.releaseImpl(this); }         \
    RefCounted refs_;

class Plugin;

// ---------------------------------------------------------------------------
// Linux: the editor window runs from the host's IRunLoop (a timer, and the X connection's fd).
// ---------------------------------------------------------------------------
class RunLoopHandler final : public Linux::ITimerHandler, public Linux::IEventHandler {
public:
    explicit RunLoopHandler(aps::EditorWindow* window) : window_(window) {}
    virtual ~RunLoopHandler() = default;
    void detach() { window_ = nullptr; }

    void PLUGIN_API onTimer() override {
        if (window_) window_->onHostTimer();
    }
    void PLUGIN_API onFDIsSet(Linux::FileDescriptor) override {
        if (window_) window_->onEventFd();
    }

    tresult PLUGIN_API queryInterface(const TUID queried, void** obj) override {
        if (iidEqual(queried, FUnknown::iid) || iidEqual(queried, Linux::ITimerHandler::iid))
            *obj = static_cast<Linux::ITimerHandler*>(this);
        else if (iidEqual(queried, Linux::IEventHandler::iid))
            *obj = static_cast<Linux::IEventHandler*>(this);
        else {
            *obj = nullptr;
            return kNoInterface;
        }
        addRef();
        return kResultOk;
    }
    uint32 PLUGIN_API addRef() override { return refs_.addRefImpl(); }
    uint32 PLUGIN_API release() override { return refs_.releaseImpl(this); }

private:
    aps::EditorWindow* window_;
    RefCounted refs_;
};

// ---------------------------------------------------------------------------
// The editor view: a thin IPlugView over the plugin's editor (the plugin owns the window and editor state).
// ---------------------------------------------------------------------------
// Sizes on this interface are window pixels; the plugin's editor works in logical units (see Plugin::editorScale).
class View final : public IPlugView, public IPlugViewContentScaleSupport {
public:
    explicit View(Plugin* plugin);
    virtual ~View();

    tresult PLUGIN_API isPlatformTypeSupported(FIDString type) override;
    tresult PLUGIN_API attached(void* parent, FIDString type) override;
    tresult PLUGIN_API removed() override;
    tresult PLUGIN_API onWheel(float) override { return kResultFalse; }      // the editor window gets input itself
    // Hosts such as Reaper offer keys here first and use the ones we decline as their shortcuts (see Plugin::hostKey).
    tresult PLUGIN_API onKeyDown(char16 key, int16 keyCode, int16 modifiers) override;
    tresult PLUGIN_API onKeyUp(char16 key, int16 keyCode, int16 modifiers) override;
    tresult PLUGIN_API getSize(ViewRect* size) override;
    tresult PLUGIN_API onSize(ViewRect* newSize) override;
    tresult PLUGIN_API onFocus(TBool) override { return kResultTrue; }
    tresult PLUGIN_API setFrame(IPlugFrame* frame) override {
        frame_ = frame; // not owned (the host's frame outlives the view's attachment)
        return kResultTrue;
    }
    tresult PLUGIN_API canResize() override;
    tresult PLUGIN_API checkSizeConstraint(ViewRect* rect) override;

    // IPlugViewContentScaleSupport: the host's UI scale, which overrides the OS's.
    tresult PLUGIN_API setContentScaleFactor(ScaleFactor factor) override;

    // Asks the host to resize the view (it then calls onSize).
    void requestResize(uint32_t width, uint32_t height) {
        if (!frame_) return;
        ViewRect rect(0, 0, int32(width), int32(height));
        frame_->resizeView(this, &rect);
    }

    tresult PLUGIN_API queryInterface(const TUID queried, void** obj) override {
        if (iidEqual(queried, FUnknown::iid) || iidEqual(queried, IPlugView::iid)) {
            *obj = static_cast<IPlugView*>(this);
            addRef();
            return kResultOk;
        }
        if (iidEqual(queried, IPlugViewContentScaleSupport::iid)) {
            *obj = static_cast<IPlugViewContentScaleSupport*>(this);
            addRef();
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    APS_FUNKNOWN_REFCOUNT

private:
    Plugin* plugin_;
    IPlugFrame* frame_ = nullptr;
    bool attached_ = false;
};

// IComponent and IEditController both declare setState/getState(IBStream*). These bases route them to
// distinct names, so the component's bytes don't end up in the controller's state.
class ComponentBase : public IComponent {
public:
    tresult PLUGIN_API setState(IBStream* state) final { return setComponentState_(state); }
    tresult PLUGIN_API getState(IBStream* state) final { return getComponentState_(state); }

protected:
    virtual tresult setComponentState_(IBStream* state) = 0;
    virtual tresult getComponentState_(IBStream* state) = 0;
};

class ControllerBase : public IEditController {
public:
    tresult PLUGIN_API setState(IBStream* state) final { return setControllerState_(state); }
    tresult PLUGIN_API getState(IBStream* state) final { return getControllerState_(state); }

protected:
    virtual tresult setControllerState_(IBStream* state) = 0;
    virtual tresult getControllerState_(IBStream* state) = 0;
};

// ---------------------------------------------------------------------------
// One plugin instance: component, processor and controller in one object.
// ---------------------------------------------------------------------------
class Plugin final : public ComponentBase,
                     public IAudioProcessor,
                     public ControllerBase,
                     public IMidiMapping,
                     public IProcessContextRequirements,
                     public aps::HostApi,
                     private aps::EditorWindow::Client {
public:
    Plugin() = default;
    virtual ~Plugin() { shutdown(); }

    // ---- FUnknown -----------------------------------------------------------------------
    tresult PLUGIN_API queryInterface(const TUID queried, void** obj) override {
        if (!obj) return kInvalidArgument;
        if (iidEqual(queried, FUnknown::iid) || iidEqual(queried, IPluginBase::iid) || iidEqual(queried, IComponent::iid))
            *obj = static_cast<IComponent*>(this);
        else if (iidEqual(queried, IAudioProcessor::iid))
            *obj = static_cast<IAudioProcessor*>(this);
        else if (iidEqual(queried, IEditController::iid))
            *obj = static_cast<IEditController*>(this);
        else if (iidEqual(queried, IMidiMapping::iid))
            *obj = static_cast<IMidiMapping*>(this);
        else if (iidEqual(queried, IProcessContextRequirements::iid))
            *obj = static_cast<IProcessContextRequirements*>(this);
        else {
            *obj = nullptr;
            return kNoInterface;
        }
        addRef();
        return kResultOk;
    }
    uint32 PLUGIN_API addRef() override { return refs_.addRefImpl(); }
    uint32 PLUGIN_API release() override { return refs_.releaseImpl(this); }

    // ---- IPluginBase (shared by IComponent and IEditController: the host may call it on both) ------
    tresult PLUGIN_API initialize(FUnknown* context) override {
        if (initialized_) return kResultOk;
        if (context) {
            IHostApplication* app = nullptr;
            if (context->queryInterface(IHostApplication::iid, reinterpret_cast<void**>(&app)) == kResultOk && app) {
                String128 name{};
                if (app->getName(name) == kResultOk) {
                    std::string ascii;
                    for (size_t i = 0; i < 128 && name[i]; ++i) ascii.push_back(name[i] < 0x80 ? char(name[i]) : '?');
                    hostName_ = ascii;
                }
                app->release();
            }
        }
        aps::ManagedPluginFactory* factory;
        {
            std::lock_guard lock(g.mutex);
            factory = factoryLocked();
            if (factory) infoLocked();
        }
        if (!factory) return kResultFalse;
        std::string error;
        api_ = factory->load(g.pluginAssembly, *this, error);
        if (!api_) {
            moduleLog("could not create plugin: " + error);
            return kResultFalse;
        }
        info_ = api_->getInfo().value;
        for (uint32_t i = 0; i < info_.numInputPorts; ++i) inPorts_.push_back(api_->getInputPort(i).value);
        for (uint32_t i = 0; i < info_.numOutputPorts; ++i) outPorts_.push_back(api_->getOutputPort(i).value);
        const uint32_t count = api_->getParamCount().valueOr(0);
        for (uint32_t i = 0; i < count; ++i) params_.push_back(api_->getParamInfo(i).value);
        gesture_ = std::make_unique<std::atomic<uint8_t>[]>(count);
        inBuffers_.resize(inPorts_.size());
        outBuffers_.resize(outPorts_.size());
        if (auto r = api_->getEditorInfo()) editorInfo_ = r.value;
        initialized_ = true;
        return kResultOk;
    }

    tresult PLUGIN_API terminate() override {
        shutdown();
        return kResultOk;
    }

    // ---- IComponent ------------------------------------------------------------------------------
    tresult PLUGIN_API getControllerClassId(TUID) override { return kNotImplemented; } // single component
    tresult PLUGIN_API setIoMode(IoMode) override { return kResultOk; }

    int32 PLUGIN_API getBusCount(MediaType type, BusDirection dir) override {
        if (type == kAudio) return static_cast<int32>(dir == kInput ? inPorts_.size() : outPorts_.size());
        if (type == kEvent) return 1; // one event input and output, as with the C++/CLI bridge
        return 0;
    }

    tresult PLUGIN_API getBusInfo(MediaType type, BusDirection dir, int32 index, BusInfo& bus) override {
        if (index < 0) return kInvalidArgument;
        bus = BusInfo{};
        bus.mediaType = type;
        bus.direction = dir;
        bus.flags = BusInfo::kDefaultActive;
        if (type == kAudio) {
            const auto& ports = dir == kInput ? inPorts_ : outPorts_;
            if (size_t(index) >= ports.size()) return kInvalidArgument;
            bus.channelCount = static_cast<int32>(ports[size_t(index)].numChannels);
            bus.busType = index == 0 ? kMain : kAux;
            toUtf16(ports[size_t(index)].name, bus.name, 128);
            return kResultOk;
        }
        if (type == kEvent && index == 0) {
            bus.channelCount = 16;
            bus.busType = kMain;
            toUtf16(dir == kInput ? "Event In" : "Event Out", bus.name, 128);
            return kResultOk;
        }
        return kInvalidArgument;
    }

    tresult PLUGIN_API getRoutingInfo(RoutingInfo&, RoutingInfo&) override { return kNotImplemented; }
    tresult PLUGIN_API activateBus(MediaType, BusDirection, int32, TBool) override { return kResultOk; }

    tresult PLUGIN_API setActive(TBool state) override {
        if (!api_) return kResultFalse;
        if (state) {
            if (!setUp_) return kResultFalse;
            if (!api_->setActive(true)) return kResultFalse;
            active_ = true;
        } else if (active_) {
            api_->setActive(false);
            active_ = false;
        }
        return kResultOk;
    }

    // The component state: the plugin's bytes, unchanged (compatible with the C++/CLI bridge).
    tresult setComponentState_(IBStream* state) override {
        if (!api_ || !state) return kInvalidArgument;
        std::vector<uint8_t> data;
        uint8_t chunk[4096];
        for (;;) {
            int32 read = 0;
            if (state->read(chunk, sizeof(chunk), &read) != kResultOk || read <= 0) break;
            data.insert(data.end(), chunk, chunk + read);
        }
        return api_->loadState(std::span<const uint8_t>(data)) ? kResultOk : kResultFalse;
    }

    tresult getComponentState_(IBStream* state) override {
        if (!api_ || !state) return kInvalidArgument;
        auto r = api_->saveState();
        if (!r) return kResultFalse;
        const uint8_t* data = r.value.data();
        int32 left = static_cast<int32>(r.value.size());
        while (left > 0) {
            int32 written = 0;
            if (state->write(const_cast<uint8_t*>(data), left, &written) != kResultOk || written <= 0) return kResultFalse;
            data += written;
            left -= written;
        }
        return kResultOk;
    }

    // ---- IAudioProcessor ---------------------------------------------------------------------------
    static SpeakerArrangement arrangementFor(uint32_t channels) {
        if (channels == 1) return SpeakerArr::kMono;
        if (channels == 2) return SpeakerArr::kStereo;
        return channels >= 64 ? ~SpeakerArrangement(0) : (SpeakerArrangement(1) << channels) - 1;
    }

    // Accepted only when it matches the plugin's ports; the host then asks for ours (getBusArrangement).
    tresult PLUGIN_API setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs,
                                          int32 numOuts) override {
        if (numIns != int32(inPorts_.size()) || numOuts != int32(outPorts_.size())) return kResultFalse;
        for (int32 i = 0; i < numIns; ++i)
            if (SpeakerArr::getChannelCount(inputs[i]) != int32(inPorts_[size_t(i)].numChannels)) return kResultFalse;
        for (int32 i = 0; i < numOuts; ++i)
            if (SpeakerArr::getChannelCount(outputs[i]) != int32(outPorts_[size_t(i)].numChannels)) return kResultFalse;
        return kResultTrue;
    }

    tresult PLUGIN_API getBusArrangement(BusDirection dir, int32 index, SpeakerArrangement& arr) override {
        const auto& ports = dir == kInput ? inPorts_ : outPorts_;
        if (index < 0 || size_t(index) >= ports.size()) return kInvalidArgument;
        arr = arrangementFor(ports[size_t(index)].numChannels);
        return kResultOk;
    }

    tresult PLUGIN_API canProcessSampleSize(int32 size) override {
        if (size == kSample32) return aps::hasFlag(info_.sampleFormats, aps::SampleFormat::Float32) ? kResultTrue : kResultFalse;
        if (size == kSample64) return aps::hasFlag(info_.sampleFormats, aps::SampleFormat::Float64) ? kResultTrue : kResultFalse;
        return kResultFalse;
    }

    uint32 PLUGIN_API getLatencySamples() override { return 0; }

    tresult PLUGIN_API setupProcessing(ProcessSetup& setup) override {
        if (!api_) return kResultFalse;
        if (setup.symbolicSampleSize == kSample32) format_ = aps::SampleFormat::Float32;
        else if (setup.symbolicSampleSize == kSample64) format_ = aps::SampleFormat::Float64;
        else return kResultFalse;
        if (canProcessSampleSize(setup.symbolicSampleSize) != kResultTrue) return kResultFalse;
        if (setup.maxSamplesPerBlock <= 0 || setup.sampleRate <= 0) return kResultFalse;
        if (!api_->setupProcessing(setup.sampleRate, uint32_t(setup.maxSamplesPerBlock), format_)) return kResultFalse;
        sampleRate_ = setup.sampleRate;
        setUp_ = true;
        return kResultOk;
    }

    tresult PLUGIN_API setProcessing(TBool) override { return kResultOk; }

    tresult PLUGIN_API process(ProcessData& data) override {
        if (!active_) return kResultFalse;
        uint32_t n = collectEvents(data);

        const bool use64 = data.symbolicSampleSize == kSample64;
        const uint32_t frames = data.numSamples > 0 ? uint32_t(data.numSamples) : 0;
        for (size_t i = 0; i < inBuffers_.size(); ++i) {
            void** channels = nullptr;
            uint32_t count = 0;
            if (frames > 0 && data.inputs && int32(i) < data.numInputs) {
                const auto& bus = data.inputs[i];
                channels = use64 ? reinterpret_cast<void**>(bus.channelBuffers64) : reinterpret_cast<void**>(bus.channelBuffers32);
                count = channels ? uint32_t(bus.numChannels) : 0;
            }
            inBuffers_[i] = aps_audio_port_buffers{channels, count, 0};
        }
        for (size_t i = 0; i < outBuffers_.size(); ++i) {
            void** channels = nullptr;
            uint32_t count = 0;
            if (frames > 0 && data.outputs && int32(i) < data.numOutputs) {
                auto& bus = data.outputs[i];
                channels = use64 ? reinterpret_cast<void**>(bus.channelBuffers64) : reinterpret_cast<void**>(bus.channelBuffers32);
                count = channels ? uint32_t(bus.numChannels) : 0;
                bus.silenceFlags = 0;
            }
            outBuffers_[i] = aps_audio_port_buffers{channels, count, 0};
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
        d.num_frames = frames; // 0: events only (a parameter flush)
        d.sample_format = static_cast<aps_sample_format>(format_);
        d.tempo = 120.0;
        if (const ProcessContext* c = data.processContext) {
            if (c->state & ProcessContext::kTempoValid) d.tempo = c->tempo;
            if (c->state & ProcessContext::kPlaying) d.transport_flags = APS_TRANSPORT_FLAGS_PLAYING;
            d.project_sample = c->projectTimeSamples;
        }

        if (!api_->process(d)) return kResultFalse;
        pushOutputEvents(d, data.outputEvents);
        return kResultOk;
    }

    uint32 PLUGIN_API getTailSamples() override { return kNoTail; }

    // ---- IProcessContextRequirements -----------------------------------------------------------------
    uint32 PLUGIN_API getProcessContextRequirements() override {
        return kNeedTempo | kNeedTransportState;
    }

    // ---- IEditController -------------------------------------------------------------------------------
    tresult PLUGIN_API setComponentState(IBStream*) override { return kResultOk; } // already loaded (same object)
    // The controller has no state of its own (as with the C++/CLI bridge).
    tresult setControllerState_(IBStream*) override { return kResultOk; }
    tresult getControllerState_(IBStream*) override { return kResultOk; }

    int32 PLUGIN_API getParameterCount() override { return static_cast<int32>(params_.size()); }

    tresult PLUGIN_API getParameterInfo(int32 index, ParameterInfo& info) override {
        if (index < 0 || size_t(index) >= params_.size()) return kInvalidArgument;
        const auto& p = params_[size_t(index)];
        info = ParameterInfo{};
        info.id = kParamBase + ParamID(index);
        toUtf16(p.name, info.title, 128);
        toUtf16(p.name, info.shortTitle, 128);
        info.stepCount = 0;
        info.defaultNormalizedValue = std::clamp(p.defaultNormalized, 0.0, 1.0);
        info.unitId = kRootUnitId;
        info.flags = ParameterInfo::kCanAutomate;
        return kResultOk;
    }

    tresult PLUGIN_API getParamStringByValue(ParamID id, ParamValue value, String128 string) override {
        const int32 index = indexOf(id);
        if (index < 0 || !string) return kResultFalse;
        auto r = api_->formatParam(uint32_t(index), std::clamp(value, 0.0, 1.0));
        if (!r) return kResultFalse;
        toUtf16(r.value.text, string, 128);
        return kResultOk;
    }

    tresult PLUGIN_API getParamValueByString(ParamID, TChar*, ParamValue&) override { return kResultFalse; }
    ParamValue PLUGIN_API normalizedParamToPlain(ParamID, ParamValue value) override { return value; }
    ParamValue PLUGIN_API plainParamToNormalized(ParamID, ParamValue value) override { return value; }

    ParamValue PLUGIN_API getParamNormalized(ParamID id) override {
        const int32 index = indexOf(id);
        if (index < 0) return 0;
        return api_->getParamNormalized(uint32_t(index)).valueOr(params_[size_t(index)].defaultNormalized);
    }

    // The host's value (automation, its generic UI, a preset): updates the plugin's edit value and editor.
    tresult PLUGIN_API setParamNormalized(ParamID id, ParamValue value) override {
        const int32 index = indexOf(id);
        if (index < 0) return kResultFalse;
        return api_->setParamNormalized(uint32_t(index), std::clamp(value, 0.0, 1.0)) ? kResultOk : kResultFalse;
    }

    tresult PLUGIN_API setComponentHandler(IComponentHandler* handler) override {
        if (handler == handler_) return kResultOk;
        if (handler_) handler_->release();
        handler_ = handler;
        if (handler_) handler_->addRef();
        return kResultOk;
    }

    IPlugView* PLUGIN_API createView(FIDString name) override {
        if (!name || std::strcmp(name, ViewType::kEditor) != 0 || !hasEditor() || view_) return nullptr;
        return new View(this);
    }

    // ---- IMidiMapping: CCs the plugin maps to parameters (AddMidiControllerMapping) -------------------
    tresult PLUGIN_API getMidiControllerAssignment(int32 busIndex, int16, CtrlNumber controller, ParamID& id) override {
        if (busIndex != 0 || controller < 0 || controller > 127 || !api_ || !api_->supportsGetMidiControllerParam())
            return kResultFalse;
        const int32_t index = api_->getMidiControllerParam(uint32_t(controller)).valueOr(-1);
        if (index < 0 || size_t(index) >= params_.size()) return kResultFalse;
        id = kParamBase + ParamID(index);
        return kResultTrue;
    }

    // ---- aps::HostApi: called by the managed plugin (UI thread) -----------------------------------------
    aps::HostInfo getInfo() override {
        aps::HostInfo info;
        info.name = hostName_.empty() ? "VST3 host" : hostName_;
        return info;
    }
    void log(aps::LogLevel level, std::string_view message) override {
        if (level >= aps::LogLevel::Info) moduleLog(std::string(message));
    }
    void beginEdit(uint32_t index) override {
        if (index >= params_.size()) return;
        gesture_[index].store(kGestureActive, std::memory_order_release);
        if (handler_) handler_->beginEdit(kParamBase + index);
    }
    void performEdit(uint32_t index, double normalized) override {
        if (handler_ && index < params_.size()) handler_->performEdit(kParamBase + index, normalized);
    }
    void endEdit(uint32_t index) override {
        if (index >= params_.size()) return;
        gesture_[index].store(kGestureEnding, std::memory_order_release);
        if (handler_) handler_->endEdit(kParamBase + index);
    }
    void setEditorCursorVisible(bool visible) override {
        if (!editorOpen_ || !editorWindow_->setCursorVisible(visible)) throw aps::NotSupported{};
    }
    bool setEditorFullscreen(bool fullscreen) override {
        if (!editorOpen_ || (fullscreen && !editorResizable())) return false;
        return editorWindow_->setFullscreen(fullscreen);
    }

    // ---- editor (UI thread; called by View) -------------------------------------------------------------
    static const char* platformType() {
        switch (aps::EditorWindow::platformWindowType()) {
            case aps::EditorWindowType::Win32: return kPlatformTypeHWND;
            case aps::EditorWindowType::X11: return kPlatformTypeX11EmbedWindowID;
            default: return nullptr;
        }
    }

    bool hasEditor() const {
        const auto type = aps::EditorWindow::platformWindowType();
        return api_ && type != aps::EditorWindowType::None &&
               aps::hasFlag(static_cast<aps::EditorFlags>(editorInfo_.flags), aps::EditorFlags::HasEditor) &&
               (editorInfo_.window_types & (1u << static_cast<int>(type))) != 0;
    }

    bool editorResizable() const {
        return aps::hasFlag(static_cast<aps::EditorFlags>(editorInfo_.flags), aps::EditorFlags::Resizable);
    }

    const aps::EditorInfo& editorInfo() {
        if (api_) if (auto r = api_->getEditorInfo()) editorInfo_ = r.value;
        return editorInfo_;
    }

    // The size the host's view has (window pixels): the windowed size while the editor is fullscreen.
    void viewSize(uint32_t& width, uint32_t& height) {
        if (editorFullscreen()) return editorWindow_->windowedSize(width, height);
        editorInfo();
        width = aps::toWindowPixels(editorInfo_.width, editorScale());
        height = aps::toWindowPixels(editorInfo_.height, editorScale());
    }

    // The UI scale: the host's if it gave one (setContentScaleFactor), otherwise the OS's for the editor window.
    double editorScale() const { return hostScale_ > 0 ? hostScale_ : osScale_; }

    void setHostScale(double scale) {
        hostScale_ = scale > 0 ? scale : 0;
        applyEditorScale();
    }

    // The open editor follows a new scale: it redraws at the scale, and the view is resized to its logical size
    // at the scale (the host is asked to resize; it then calls onSize).
    void applyEditorScale() {
        const double scale = editorScale();
        if (!editorOpen_ || scale == appliedScale_) return;
        appliedScale_ = scale;
        api_->setEditorScale(scale);
        if (editorFullscreen()) return; // the window keeps the monitor's size
        uint32_t width = 0, height = 0;
        viewSize(width, height);
        editorWindow_->setSize(width, height);
        if (view_) view_->requestResize(width, height);
    }

    bool editorFullscreen() const { return editorWindow_ && editorWindow_->isFullscreen(); }

    void viewCreated(View* view) { view_ = view; }
    void viewDestroyed(View* view) {
        if (view_ == view) view_ = nullptr;
    }

    bool openEditor(intptr_t parent, IPlugFrame* frame) {
        closeEditor();
        editorInfo();
        osScale_ = aps::EditorWindow::platformScale(parent);
        const double scale = editorScale();
        editorWindow_ = aps::EditorWindow::create(parent, aps::toWindowPixels(editorInfo_.width, scale),
                                                  aps::toWindowPixels(editorInfo_.height, scale), *this);
        if (!editorWindow_) {
            moduleLog("could not create the editor window");
            return false;
        }
        if (editorWindow_->needsHostTimer()) {
            // X11: the host's run loop (IRunLoop, from the frame) drives the window.
            if (frame) frame->queryInterface(Linux::IRunLoop::iid, reinterpret_cast<void**>(&runLoop_));
            if (!runLoop_) {
                moduleLog("the host has no IRunLoop; editors need it on Linux");
                editorWindow_.reset();
                return false;
            }
            runLoopHandler_ = new RunLoopHandler(editorWindow_.get());
            if (runLoop_->registerTimer(runLoopHandler_, aps::EditorWindow::kHostTimerMs) != kResultOk) {
                moduleLog("the host refused the editor's timer");
                closeEditor();
                return false;
            }
            timerRegistered_ = true;
            const int fd = editorWindow_->eventFd();
            if (fd >= 0 && runLoop_->registerEventHandler(runLoopHandler_, fd) == kResultOk) fdRegistered_ = true;
        }
        appliedScale_ = scale;
        if (auto r = api_->openEditor(editorWindow_->handle(), editorWindow_->windowType(), scale); !r) {
            moduleLog("the plugin could not open its editor (error " + std::to_string(r.code) + ")");
            closeEditor();
            return false;
        }
        editorOpen_ = true;
        editorWindow_->editorOpened();
        editorWindow_->setVisible(true);
        editorWindow_->setTimerRunning(true);
        return true;
    }

    void closeEditor() {
        if (runLoop_) {
            if (fdRegistered_) runLoop_->unregisterEventHandler(runLoopHandler_);
            if (timerRegistered_) runLoop_->unregisterTimer(runLoopHandler_);
            fdRegistered_ = timerRegistered_ = false;
            runLoop_->release();
            runLoop_ = nullptr;
        }
        if (runLoopHandler_) {
            runLoopHandler_->detach();
            runLoopHandler_->release();
            runLoopHandler_ = nullptr;
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

    // IPlugView::onKeyDown/onKeyUp. The editor sees the key either way; the answer is whether it claims it
    // (kResultTrue: the host does not use it as a shortcut). A claimed key press also delivers the character it
    // types. The window drops the same keystroke if the host delivers it there too.
    tresult hostKey(bool down, char16 character, int16 keyCode, int16 modifiers) {
        if (!editorOpen_ || !editorWindow_) return kResultFalse;
        aps::EditorInputEvent e{};
        e.modifiers = ((modifiers & kShiftKey) ? 1 : 0) | ((modifiers & kCommandKey) ? 2 : 0) |
                      ((modifiers & kAlternateKey) ? 4 : 0) | ((modifiers & kControlKey) ? 8 : 0);
        const int vk = vst3VirtualKey(character, keyCode);
        bool used = false;
        if (vk) {
            e.type = static_cast<aps_editor_input_event_type>(down ? aps::EditorInputEventType::KeyDown
                                                                   : aps::EditorInputEventType::KeyUp);
            e.key = vk;
            used = api_->editorInput(e).valueOr(false);
        }
        uint32_t typed = 0;
        const bool printable = character >= 0x20 && character != 0x7F && (character < 0xD800 || character >= 0xE000);
        if (down && (used || !vk) && printable && !(modifiers & (kCommandKey | kControlKey))) {
            e.type = static_cast<aps_editor_input_event_type>(aps::EditorInputEventType::Text);
            e.key = 0;
            e.character = int32_t(character);
            used = api_->editorInput(e).valueOr(false) || used;
            typed = character; // delivered: the window drops it if the host types it there too
        }
        editorWindow_->keyFromHost(vk, down, typed);
        return used ? kResultTrue : kResultFalse;
    }

    // onSize: window pixels from the host.
    bool resizeEditor(uint32_t width, uint32_t height) {
        if (editorFullscreen()) return false;
        const double scale = editorScale();
        const uint32_t units = aps::toLogicalUnits(width, scale), unitsHigh = aps::toLogicalUnits(height, scale);
        if (units != editorInfo_.width || unitsHigh != editorInfo_.height) {
            if (!editorResizable() || !api_->setEditorSize(units, unitsHigh).valueOr(false)) return false;
            editorInfo();
        }
        if (editorWindow_)
            editorWindow_->setSize(aps::toWindowPixels(editorInfo_.width, scale), aps::toWindowPixels(editorInfo_.height, scale));
        return true;
    }

private:
    int32 indexOf(ParamID id) const {
        if (id < kParamBase || id - kParamBase >= params_.size() || !api_) return -1;
        return int32(id - kParamBase);
    }

    void shutdown() {
        if (!initialized_) return;
        initialized_ = false;
        closeEditor();
        if (active_ && api_) api_->setActive(false);
        active_ = false;
        api_.reset();
        if (handler_) {
            handler_->release();
            handler_ = nullptr;
        }
    }

    // Host events -> the bridge's event array, sorted by offset (insertion sort: stable and allocation-free).
    uint32_t collectEvents(ProcessData& data) {
        uint32_t n = 0;
        auto add = [&](const aps::AudioEvent& e) {
            if (n >= events_.size()) return;
            uint32_t i = n++;
            while (i > 0 && events_[i - 1].sample_offset > e.sample_offset) {
                events_[i] = events_[i - 1];
                --i;
            }
            events_[i] = e;
        };
        auto offset = [&](int32 o) {
            return uint32_t(std::clamp<int32>(o, 0, data.numSamples > 0 ? data.numSamples - 1 : 0));
        };

        if (IParameterChanges* changes = data.inputParameterChanges) {
            const int32 count = changes->getParameterCount();
            for (int32 q = 0; q < count; ++q) {
                IParamValueQueue* queue = changes->getParameterData(q);
                if (!queue) continue;
                const int32 index = indexOf(queue->getParameterId());
                if (index < 0) continue;
                const int32 points = queue->getPointCount();
                // The plugin's own gesture, echoed by the host (typically one point at sample 0 per block): the
                // last value goes at the block's last sample, so the bridge ramps across the block instead of
                // stepping at its start (zipper noise while a control is dragged; the CLAP adapter does the
                // same). The block after endEdit still carries the final echo. Automation keeps its offsets.
                uint8_t gesture = gesture_[size_t(index)].load(std::memory_order_acquire);
                if (gesture != kGestureNone && points > 0 && data.numSamples > 0) {
                    if (gesture == kGestureEnding)
                        gesture_[size_t(index)].compare_exchange_strong(gesture, kGestureNone, std::memory_order_acq_rel);
                    int32 sampleOffset = 0;
                    ParamValue value = 0;
                    if (queue->getPoint(points - 1, sampleOffset, value) == kResultOk)
                        add({APS_AUDIO_EVENT_TYPE_PARAM_CHANGE, uint32_t(data.numSamples - 1), uint32_t(index), 0,
                             std::clamp(value, 0.0, 1.0)});
                    continue;
                }
                for (int32 p = 0; p < points; ++p) {
                    int32 sampleOffset = 0;
                    ParamValue value = 0;
                    if (queue->getPoint(p, sampleOffset, value) != kResultOk) continue;
                    add({APS_AUDIO_EVENT_TYPE_PARAM_CHANGE, offset(sampleOffset), uint32_t(index), 0,
                         std::clamp(value, 0.0, 1.0)});
                }
            }
        }

        if (IEventList* events = data.inputEvents) {
            const int32 count = events->getEventCount();
            for (int32 i = 0; i < count; ++i) {
                Event e{};
                if (events->getEvent(i, e) != kResultOk || e.busIndex != 0) continue;
                const uint32_t at = offset(e.sampleOffset);
                switch (e.type) {
                    case Event::kNoteOnEvent:
                        if (e.noteOn.velocity > 0)
                            add({APS_AUDIO_EVENT_TYPE_NOTE_ON, at, uint32_t(e.noteOn.pitch) & 0x7F, e.noteOn.channel, e.noteOn.velocity});
                        else // velocity 0 is a note off
                            add({APS_AUDIO_EVENT_TYPE_NOTE_OFF, at, uint32_t(e.noteOn.pitch) & 0x7F, e.noteOn.channel, 0});
                        break;
                    case Event::kNoteOffEvent:
                        add({APS_AUDIO_EVENT_TYPE_NOTE_OFF, at, uint32_t(e.noteOff.pitch) & 0x7F, e.noteOff.channel, e.noteOff.velocity});
                        break;
                    case Event::kPolyPressureEvent:
                        add({APS_AUDIO_EVENT_TYPE_POLY_PRESSURE, at, uint32_t(e.polyPressure.pitch) & 0x7F,
                             e.polyPressure.channel, e.polyPressure.pressure});
                        break;
                    case Event::kLegacyMIDICCOutEvent: // not normally sent to plugins, but harmless
                        if (e.midiCCOut.controlNumber < 128)
                            add({APS_AUDIO_EVENT_TYPE_CONTROL_CHANGE, at, e.midiCCOut.controlNumber, e.midiCCOut.channel,
                                 double(e.midiCCOut.value)});
                        break;
                    default: break;
                }
            }
        }
        return n;
    }

    void pushOutputEvents(const aps::ProcessData& d, IEventList* out) {
        if (!out) return;
        for (uint32_t i = 0; i < d.num_output_events; ++i) {
            const auto& e = outEvents_[i];
            Event ev{};
            ev.busIndex = 0;
            ev.sampleOffset = int32(e.sample_offset);
            switch (e.type) {
                case APS_AUDIO_EVENT_TYPE_NOTE_ON:
                    ev.type = Event::kNoteOnEvent;
                    ev.noteOn.channel = int16(e.channel);
                    ev.noteOn.pitch = int16(e.index);
                    ev.noteOn.velocity = float(e.value);
                    ev.noteOn.noteId = -1;
                    break;
                case APS_AUDIO_EVENT_TYPE_NOTE_OFF:
                    ev.type = Event::kNoteOffEvent;
                    ev.noteOff.channel = int16(e.channel);
                    ev.noteOff.pitch = int16(e.index);
                    ev.noteOff.velocity = float(e.value);
                    ev.noteOff.noteId = -1;
                    break;
                case APS_AUDIO_EVENT_TYPE_POLY_PRESSURE:
                    ev.type = Event::kPolyPressureEvent;
                    ev.polyPressure.channel = int16(e.channel);
                    ev.polyPressure.pitch = int16(e.index);
                    ev.polyPressure.pressure = float(e.value);
                    ev.polyPressure.noteId = -1;
                    break;
                case APS_AUDIO_EVENT_TYPE_CONTROL_CHANGE:
                    ev.type = Event::kLegacyMIDICCOutEvent;
                    ev.midiCCOut.controlNumber = uint8(e.index & 0x7F);
                    ev.midiCCOut.channel = int8(e.channel & 0x0F);
                    ev.midiCCOut.value = int8(std::clamp(e.value, 0.0, 127.0));
                    break;
                default: continue;
            }
            out->addEvent(ev);
        }
    }

    // aps::EditorWindow::Client
    void editorIdle(double secondsElapsed) override {
        if (!editorOpen_) return;
        if (!api_->editorIdle(secondsElapsed)) {
            moduleLog("the editor failed while drawing; it is stopped until it is reopened");
            editorWindow_->setTimerRunning(false);
        }
    }
    bool editorInput(const aps::EditorInputEvent& event) override {
        return editorOpen_ && api_->editorInput(event).valueOr(false);
    }
    void editorWindowResized(uint32_t width, uint32_t height) override {
        const double scale = editorScale();
        if (editorOpen_ && api_->setEditorSize(aps::toLogicalUnits(width, scale), aps::toLogicalUnits(height, scale)).valueOr(false))
            editorInfo();
    }
    void editorDpiChanged(double scale) override {
        osScale_ = scale;
        if (hostScale_ <= 0) applyEditorScale();
    }

    RefCounted refs_;
    bool initialized_ = false;
    std::string hostName_;
    IComponentHandler* handler_ = nullptr;

    std::unique_ptr<aps::PluginApi> api_;
    aps::PluginInfo info_;
    std::vector<aps::PortInfo> inPorts_, outPorts_;
    std::vector<aps::ParamInfo> params_;
    // Per parameter: the plugin's edit gesture (UI thread writes, audio thread reads; see collectEvents).
    static constexpr uint8_t kGestureNone = 0, kGestureActive = 1, kGestureEnding = 2;
    std::unique_ptr<std::atomic<uint8_t>[]> gesture_;
    aps::SampleFormat format_ = aps::SampleFormat::Float32;
    double sampleRate_ = 48000;
    bool setUp_ = false;
    std::atomic<bool> active_{false};

    // Preallocated per-block storage (audio thread).
    std::vector<aps_audio_port_buffers> inBuffers_, outBuffers_;
    std::array<aps::AudioEvent, 1024> events_{};
    std::array<aps::AudioEvent, 256> outEvents_{};

    // Editor (UI thread).
    aps::EditorInfo editorInfo_{}; // sizes in logical units
    double hostScale_ = 0;         // from setContentScaleFactor (0: none)
    double osScale_ = aps::EditorWindow::platformScale(0);
    double appliedScale_ = 0;      // the open editor's scale
    View* view_ = nullptr; // not owned; the view holds a reference to this plugin
    bool editorOpen_ = false;
    std::unique_ptr<aps::EditorWindow> editorWindow_;
    Linux::IRunLoop* runLoop_ = nullptr;
    RunLoopHandler* runLoopHandler_ = nullptr;
    bool timerRegistered_ = false, fdRegistered_ = false;
};

// ---- View ---------------------------------------------------------------------------------------------
View::View(Plugin* plugin) : plugin_(plugin) {
    plugin_->addRef();
    plugin_->viewCreated(this);
}

View::~View() {
    if (attached_) plugin_->closeEditor();
    plugin_->viewDestroyed(this);
    plugin_->release();
}

tresult PLUGIN_API View::isPlatformTypeSupported(FIDString type) {
    const char* ours = Plugin::platformType();
    return type && ours && !std::strcmp(type, ours) && plugin_->hasEditor() ? kResultTrue : kResultFalse;
}

tresult PLUGIN_API View::attached(void* parent, FIDString type) {
    if (attached_ || !parent || isPlatformTypeSupported(type) != kResultTrue) return kResultFalse;
    if (!plugin_->openEditor(reinterpret_cast<intptr_t>(parent), frame_)) return kResultFalse;
    attached_ = true;
    return kResultOk;
}

tresult PLUGIN_API View::removed() {
    if (!attached_) return kResultFalse;
    attached_ = false;
    plugin_->closeEditor();
    return kResultOk;
}

tresult PLUGIN_API View::getSize(ViewRect* size) {
    if (!size) return kInvalidArgument;
    uint32_t width = 0, height = 0;
    plugin_->viewSize(width, height);
    *size = ViewRect(0, 0, int32(width), int32(height));
    return kResultOk;
}

tresult PLUGIN_API View::onKeyDown(char16 key, int16 keyCode, int16 modifiers) {
    return attached_ ? plugin_->hostKey(true, key, keyCode, modifiers) : kResultFalse;
}

tresult PLUGIN_API View::onKeyUp(char16 key, int16 keyCode, int16 modifiers) {
    return attached_ ? plugin_->hostKey(false, key, keyCode, modifiers) : kResultFalse;
}

tresult PLUGIN_API View::onSize(ViewRect* newSize) {
    if (!newSize) return kInvalidArgument;
    return plugin_->resizeEditor(uint32_t(std::max(newSize->getWidth(), 1)), uint32_t(std::max(newSize->getHeight(), 1)))
               ? kResultTrue
               : kResultFalse;
}

tresult PLUGIN_API View::canResize() { return plugin_->editorResizable() ? kResultTrue : kResultFalse; }

tresult PLUGIN_API View::checkSizeConstraint(ViewRect* rect) {
    if (!rect) return kInvalidArgument;
    if (!plugin_->editorResizable()) {
        uint32_t width = 0, height = 0;
        plugin_->viewSize(width, height);
        rect->right = rect->left + int32(width);
        rect->bottom = rect->top + int32(height);
    }
    return kResultTrue;
}

tresult PLUGIN_API View::setContentScaleFactor(ScaleFactor factor) {
    if (!(factor > 0)) return kInvalidArgument;
    plugin_->setHostScale(factor);
    return kResultTrue;
}

// ---------------------------------------------------------------------------
// Factory.
// ---------------------------------------------------------------------------
class Factory final : public IPluginFactory3 {
public:
    virtual ~Factory() = default;

    tresult PLUGIN_API getFactoryInfo(PFactoryInfo* info) override {
        if (!info) return kInvalidArgument;
        std::lock_guard lock(g.mutex);
        *info = PFactoryInfo{};
        if (infoLocked()) {
            copyAscii(g.info.company, info->vendor, PFactoryInfo::kNameSize);
            copyAscii(g.info.website, info->url, PFactoryInfo::kURLSize);
            copyAscii(g.info.contact, info->email, PFactoryInfo::kEmailSize);
        }
        info->flags = PFactoryInfo::kUnicode;
        return kResultOk;
    }

    int32 PLUGIN_API countClasses() override {
        std::lock_guard lock(g.mutex);
        return infoLocked() ? 1 : 0;
    }

    tresult PLUGIN_API getClassInfo(int32 index, PClassInfo* info) override {
        std::lock_guard lock(g.mutex);
        if (index != 0 || !info || !infoLocked()) return kInvalidArgument;
        *info = PClassInfo{};
        std::memcpy(info->cid, g.cid, sizeof(TUID));
        info->cardinality = PClassInfo::kManyInstances;
        copyAscii(kVstAudioEffectClass, info->category, PClassInfo::kCategorySize);
        copyAscii(g.info.name, info->name, PClassInfo::kNameSize);
        return kResultOk;
    }

    tresult PLUGIN_API getClassInfo2(int32 index, PClassInfo2* info) override {
        std::lock_guard lock(g.mutex);
        if (index != 0 || !info || !infoLocked()) return kInvalidArgument;
        *info = PClassInfo2{};
        std::memcpy(info->cid, g.cid, sizeof(TUID));
        info->cardinality = PClassInfo::kManyInstances;
        copyAscii(kVstAudioEffectClass, info->category, PClassInfo::kCategorySize);
        copyAscii(g.info.name, info->name, PClassInfo::kNameSize);
        info->classFlags = 0;
        copyAscii(g.info.category, info->subCategories, PClassInfo2::kSubCategoriesSize);
        copyAscii(g.info.company, info->vendor, PClassInfo2::kVendorSize);
        copyAscii(g.info.version, info->version, PClassInfo2::kVersionSize);
        copyAscii(kVstVersionString, info->sdkVersion, PClassInfo2::kVersionSize);
        return kResultOk;
    }

    tresult PLUGIN_API getClassInfoUnicode(int32 index, PClassInfoW* info) override {
        std::lock_guard lock(g.mutex);
        if (index != 0 || !info || !infoLocked()) return kInvalidArgument;
        *info = PClassInfoW{};
        std::memcpy(info->cid, g.cid, sizeof(TUID));
        info->cardinality = PClassInfo::kManyInstances;
        copyAscii(kVstAudioEffectClass, info->category, PClassInfo::kCategorySize);
        toUtf16(g.info.name, info->name, PClassInfo::kNameSize);
        info->classFlags = 0;
        copyAscii(g.info.category, info->subCategories, PClassInfo2::kSubCategoriesSize);
        toUtf16(g.info.company, info->vendor, PClassInfo2::kVendorSize);
        toUtf16(g.info.version, info->version, PClassInfo2::kVersionSize);
        toUtf16(kVstVersionString, info->sdkVersion, PClassInfo2::kVersionSize);
        return kResultOk;
    }

    tresult PLUGIN_API setHostContext(FUnknown*) override { return kResultOk; }

    tresult PLUGIN_API createInstance(FIDString cid, FIDString requested, void** obj) override {
        if (!cid || !requested || !obj) return kInvalidArgument;
        *obj = nullptr;
        {
            std::lock_guard lock(g.mutex);
            if (!infoLocked() || std::memcmp(cid, g.cid, sizeof(TUID)) != 0) return kNoInterface;
        }
        Plugin* plugin = nullptr;
        try {
            plugin = new Plugin();
        } catch (...) {
            return kOutOfMemory;
        }
        const tresult r = plugin->queryInterface(requested, obj);
        plugin->release();
        return r;
    }

    tresult PLUGIN_API queryInterface(const TUID queried, void** obj) override {
        if (iidEqual(queried, FUnknown::iid) || iidEqual(queried, IPluginFactory::iid) || iidEqual(queried, IPluginFactory2::iid) ||
            iidEqual(queried, IPluginFactory3::iid)) {
            *obj = static_cast<IPluginFactory3*>(this);
            addRef();
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    // A module-lifetime singleton: the host's references never delete it.
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

Factory gFactory;

} // namespace

// ---------------------------------------------------------------------------
// Module entry points.
// ---------------------------------------------------------------------------
#ifdef _WIN32
#define APS_EXPORT extern "C" __declspec(dllexport)
#else
#define APS_EXPORT extern "C" __attribute__((visibility("default")))
#endif

APS_EXPORT IPluginFactory* PLUGIN_API GetPluginFactory() { return &gFactory; }

// The .NET runtime cannot be unloaded; it stays for the life of the process.
#ifdef _WIN32
APS_EXPORT bool InitDll() { return true; }
APS_EXPORT bool ExitDll() { return true; }
#else
APS_EXPORT bool ModuleEntry(void*) { return true; }
APS_EXPORT bool ModuleExit() { return true; }
#endif
