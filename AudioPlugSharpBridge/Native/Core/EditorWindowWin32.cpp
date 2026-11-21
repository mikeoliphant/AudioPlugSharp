// EditorWindowWin32.cpp - Win32 implementation of aps::EditorWindow.
//
// - The window class name includes this module's address: two adapter binaries (two .clap files) in one
//   process must not share a window procedure, since either may be unloaded first.
// - Host shortcuts: Reaper (and Cockos' WDL) treat a window whose GWLP_USERDATA is 0xdeadf00b as one that may
//   want keys: before using a key as its own shortcut (space: transport) Reaper asks it with WM_GETDLGCODE and the
//   key's message, as the dialog manager does. The window answers per key from the editor (EditorInput's "used",
//   as VST3 onKeyDown does), passing the key on there and dropping the WM_KEYDOWN that follows. Keys Reaper
//   keeps without asking (Escape closes the FX window, Alt combinations) never arrive. VST3 hosts ask through
//   onKeyDown instead (keyFromHost); this covers CLAP, which has no key callback.
// - The instance pointer is stored with SetProp, not GWLP_USERDATA: SDL subclasses the window when MonoGame
//   wraps it (SDL_CreateWindowFrom) and keeps its own data, so the slot is left alone.
// - The mouse is captured while a button is held, so drags that leave the window keep reporting.
// - A click takes keyboard focus, so key and text input reach the editor after the user clicked into it.
// - Window services: SDL subclasses the window when the editor opens and answers WM_SETCURSOR itself, so
//   editorOpened() puts a second subclass on top of SDL's (cursor hiding; WM_CLOSE while fullscreen), and
//   editorClosing() removes it before SDL lets go of the window. Fullscreen moves the same HWND (and so the
//   editor's OpenGL context) into a borderless popup covering its monitor, and back.
#include "aps/EditorWindow.h"
#include "aps/NativeLog.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <windowsx.h>

#include <chrono>
#include <cstdio>
#include <cwchar>

namespace aps {
namespace {

constexpr UINT_PTR kTimerId = 1;
constexpr UINT kTimerMs = 16; // ~60 Hz (the system timer resolution rounds it up)
constexpr const wchar_t* kInstanceProp = L"AudioPlugSharp.EditorWindow";
constexpr LONG_PTR kWantsKeysMarker = static_cast<LONG_PTR>(0xdeadf00b); // GWLP_USERDATA: see the file comment
constexpr const wchar_t* kOuterProp = L"AudioPlugSharp.EditorWindow.Outer";     // the instance, for outerProc
constexpr const wchar_t* kInnerProcProp = L"AudioPlugSharp.EditorWindow.Inner"; // the procedure outerProc wraps
#ifndef WM_DPICHANGED_AFTERPARENT
#define WM_DPICHANGED_AFTERPARENT 0x02E3
#endif

// Per-window DPI (Windows 10 1607 and later), looked up at run time.
struct DpiApi {
    UINT(WINAPI* forWindow)(HWND) = nullptr;
    UINT(WINAPI* forSystem)() = nullptr;
    DPI_AWARENESS_CONTEXT(WINAPI* threadContext)() = nullptr;
    DPI_AWARENESS(WINAPI* awareness)(DPI_AWARENESS_CONTEXT) = nullptr;

    DpiApi() {
        if (HMODULE user32 = GetModuleHandleW(L"user32.dll")) {
            forWindow = reinterpret_cast<decltype(forWindow)>(GetProcAddress(user32, "GetDpiForWindow"));
            forSystem = reinterpret_cast<decltype(forSystem)>(GetProcAddress(user32, "GetDpiForSystem"));
            threadContext = reinterpret_cast<decltype(threadContext)>(GetProcAddress(user32, "GetThreadDpiAwarenessContext"));
            awareness = reinterpret_cast<decltype(awareness)>(GetProcAddress(user32, "GetAwarenessFromDpiAwarenessContext"));
        }
    }

    static const DpiApi& get() {
        static const DpiApi api;
        return api;
    }

    double windowScale(HWND hwnd) const {
        const UINT dpi = forWindow && hwnd ? forWindow(hwnd) : 0;
        return dpi ? dpi / 96.0 : threadScale();
    }

    // No window yet: the system DPI if the calling thread is DPI aware (an unaware thread's windows are stretched).
    double threadScale() const {
        if (!forSystem || !threadContext || !awareness) return 1.0;
        if (awareness(threadContext()) == DPI_AWARENESS_UNAWARE) return 1.0;
        const UINT dpi = forSystem();
        return dpi ? dpi / 96.0 : 1.0;
    }
};

HMODULE thisModule() {
    HMODULE module = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&thisModule), &module);
    return module;
}

class EditorWindowWin32 final : public EditorWindow {
public:
    EditorWindowWin32(Client& client) : client_(client) {}

    ~EditorWindowWin32() override {
        if (hwnd_) {
            removeOuterProc();
            KillTimer(hwnd_, kTimerId);
            RemovePropW(hwnd_, kInstanceProp);
            DestroyWindow(hwnd_);
        }
    }

    bool create(HWND parent, uint32_t width, uint32_t height) {
        const wchar_t* cls = windowClass();
        if (!cls) return false;
        hwnd_ = CreateWindowExW(0, cls, L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0,
                                static_cast<int>(width), static_cast<int>(height), parent, nullptr, thisModule(),
                                this);
        if (!hwnd_) {
            nativeLog("CreateWindowEx failed (" + std::to_string(GetLastError()) + ")");
            return false;
        }
        SetWindowLongPtrW(hwnd_, GWLP_USERDATA, kWantsKeysMarker);
        return true;
    }

    EditorWindowType windowType() const override { return EditorWindowType::Win32; }
    intptr_t handle() const override { return reinterpret_cast<intptr_t>(hwnd_); }

    void setSize(uint32_t width, uint32_t height) override {
        if (fullscreen_) { // applied when leaving fullscreen
            windowedWidth_ = width;
            windowedHeight_ = height;
            return;
        }
        SetWindowPos(hwnd_, nullptr, 0, 0, static_cast<int>(width), static_cast<int>(height),
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void setVisible(bool visible) override { ShowWindow(hwnd_, visible ? SW_SHOWNA : SW_HIDE); }

    void setTimerRunning(bool running) override {
        if (running == timerRunning_) return;
        timerRunning_ = running;
        if (running) {
            lastTick_ = Clock::now();
            firstTick_ = true;
            SetTimer(hwnd_, kTimerId, kTimerMs, nullptr);
        } else {
            KillTimer(hwnd_, kTimerId);
        }
    }

    void editorOpened() override {
        // On top of whatever the editor installed (SDL's subclass), which would otherwise answer WM_SETCURSOR.
        const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(hwnd_, GWLP_WNDPROC));
        if (current == &EditorWindowWin32::outerProc) return;
        SetPropW(hwnd_, kInnerProcProp, reinterpret_cast<HANDLE>(current));
        SetPropW(hwnd_, kOuterProp, this);
        SetWindowLongPtrW(hwnd_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&EditorWindowWin32::outerProc));
    }

    void editorClosing() override {
        setFullscreen(false);
        setCursorVisible(true);
        removeOuterProc();
    }

    bool setCursorVisible(bool visible) override {
        cursorVisible_ = visible;
        POINT pt{};
        if (GetCursorPos(&pt) && WindowFromPoint(pt) == hwnd_) {
            if (!visible) SetCursor(nullptr);
            else SendMessageW(hwnd_, WM_SETCURSOR, reinterpret_cast<WPARAM>(hwnd_), MAKELPARAM(HTCLIENT, WM_MOUSEMOVE));
        }
        return true;
    }

    bool setFullscreen(bool fullscreen) override {
        if (fullscreen == fullscreen_) return fullscreen_;
        if (fullscreen) {
            MONITORINFO monitor{};
            monitor.cbSize = sizeof(monitor);
            if (!GetMonitorInfoW(MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST), &monitor)) return false;
            RECT client{};
            GetClientRect(hwnd_, &client);
            windowedWidth_ = uint32_t(client.right);
            windowedHeight_ = uint32_t(client.bottom);
            parent_ = GetParent(hwnd_);
            // To top level: SetParent first, then WS_CHILD -> WS_POPUP (the order SetParent's documentation gives).
            SetParent(hwnd_, nullptr);
            SetWindowLongPtrW(hwnd_, GWL_STYLE, (GetWindowLongPtrW(hwnd_, GWL_STYLE) & ~LONG_PTR(WS_CHILD)) | WS_POPUP);
            const RECT& r = monitor.rcMonitor;
            fullscreen_ = true;
            SetWindowPos(hwnd_, HWND_TOP, r.left, r.top, r.right - r.left, r.bottom - r.top,
                         SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd_);
            SetFocus(hwnd_);
            client_.editorWindowResized(uint32_t(r.right - r.left), uint32_t(r.bottom - r.top));
        } else {
            // Back into the parent: WS_POPUP -> WS_CHILD first, then SetParent.
            SetWindowLongPtrW(hwnd_, GWL_STYLE, (GetWindowLongPtrW(hwnd_, GWL_STYLE) & ~LONG_PTR(WS_POPUP)) | WS_CHILD);
            SetParent(hwnd_, parent_);
            fullscreen_ = false;
            SetWindowPos(hwnd_, nullptr, 0, 0, int(windowedWidth_), int(windowedHeight_),
                         SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
            if (parent_) SetForegroundWindow(GetAncestor(parent_, GA_ROOT));
            client_.editorWindowResized(windowedWidth_, windowedHeight_);
        }
        return fullscreen_;
    }

    bool isFullscreen() const override { return fullscreen_; }

    double dpiScale() const override { return DpiApi::get().windowScale(hwnd_); }

    void keyFromHost(int virtualKey, bool down, uint32_t character) override {
        hostKey_ = virtualKey;
        hostKeyDown_ = down;
        hostChar_ = down ? character : 0;
    }

    void windowedSize(uint32_t& width, uint32_t& height) const override {
        if (fullscreen_) {
            width = windowedWidth_;
            height = windowedHeight_;
            return;
        }
        RECT client{};
        GetClientRect(hwnd_, &client);
        width = uint32_t(client.right);
        height = uint32_t(client.bottom);
    }

private:
    using Clock = std::chrono::steady_clock;

    void removeOuterProc() {
        if (!GetPropW(hwnd_, kOuterProp)) return;
        const auto inner = reinterpret_cast<WNDPROC>(GetPropW(hwnd_, kInnerProcProp));
        if (reinterpret_cast<WNDPROC>(GetWindowLongPtrW(hwnd_, GWLP_WNDPROC)) == &EditorWindowWin32::outerProc) {
            SetWindowLongPtrW(hwnd_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(inner));
            RemovePropW(hwnd_, kInnerProcProp);
        }
        // Otherwise someone subclassed on top of us: outerProc stays in the chain, passing everything through.
        RemovePropW(hwnd_, kOuterProp);
    }

    // The subclass on top of the editor's own (see editorOpened).
    static LRESULT CALLBACK outerProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
        if (auto* self = static_cast<EditorWindowWin32*>(GetPropW(hwnd, kOuterProp))) {
            if (msg == WM_SETCURSOR && !self->cursorVisible_ && LOWORD(lp) == HTCLIENT) {
                SetCursor(nullptr);
                return TRUE;
            }
            if (msg == WM_CLOSE && self->fullscreen_) { // Alt+F4 on the fullscreen window: back into the host
                self->setFullscreen(false);
                return 0;
            }
        }
        const auto inner = reinterpret_cast<WNDPROC>(GetPropW(hwnd, kInnerProcProp));
        return inner ? CallWindowProcW(inner, hwnd, msg, wp, lp) : DefWindowProcW(hwnd, msg, wp, lp);
    }

    // Registered once per module; the name is unique to this module.
    static const wchar_t* windowClass() {
        static wchar_t name[64] = {};
        static bool registered = false;
        if (registered) return name;
        std::swprintf(name, 64, L"AudioPlugSharpEditor_%p", static_cast<void*>(thisModule()));
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_OWNDC | CS_DBLCLKS; // CS_OWNDC: OpenGL keeps one DC per window
        wc.lpfnWndProc = &EditorWindowWin32::wndProc;
        wc.hInstance = thisModule();
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.lpszClassName = name;
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            nativeLog("RegisterClassEx failed (" + std::to_string(GetLastError()) + ")");
            return nullptr;
        }
        registered = true;
        return name;
    }

    static LRESULT CALLBACK wndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
        if (msg == WM_NCCREATE) {
            auto* cs = reinterpret_cast<CREATESTRUCTW*>(lp);
            SetPropW(hwnd, kInstanceProp, cs->lpCreateParams);
        }
        auto* self = static_cast<EditorWindowWin32*>(GetPropW(hwnd, kInstanceProp));
        if (self) {
            LRESULT result = 0;
            if (self->handle(msg, wp, lp, result)) return result;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    static int modifiers() {
        int m = 0;
        if (GetKeyState(VK_SHIFT) & 0x8000) m |= 1;
        if (GetKeyState(VK_CONTROL) & 0x8000) m |= 2;
        if (GetKeyState(VK_MENU) & 0x8000) m |= 4;
        if ((GetKeyState(VK_LWIN) | GetKeyState(VK_RWIN)) & 0x8000) m |= 8;
        return m;
    }

    // Generic modifier codes -> left/right-specific ones (MonoGame's Keys only has the specific ones).
    static int virtualKey(WPARAM wp, LPARAM lp) {
        const bool extended = (lp >> 24) & 1;
        switch (wp) {
            case VK_SHIFT: {
                const UINT scan = (lp >> 16) & 0xFF;
                return static_cast<int>(MapVirtualKeyW(scan, MAPVK_VSC_TO_VK_EX));
            }
            case VK_CONTROL: return extended ? VK_RCONTROL : VK_LCONTROL;
            case VK_MENU: return extended ? VK_RMENU : VK_LMENU;
            default: return static_cast<int>(wp);
        }
    }

    bool send(EditorInputEventType type, int x = 0, int y = 0) {
        EditorInputEvent e{};
        e.type = static_cast<aps_editor_input_event_type>(type);
        e.modifiers = modifiers();
        e.x = x;
        e.y = y;
        return client_.editorInput(e);
    }

    void mouseButton(int button, bool down, LPARAM lp) {
        const int bit = 1 << button;
        if (down) {
            if (GetFocus() != hwnd_) SetFocus(hwnd_);
            if (buttons_ == 0) SetCapture(hwnd_);
            buttons_ |= bit;
        } else {
            if (!(buttons_ & bit)) return; // the press went elsewhere
            buttons_ &= ~bit;
        }
        EditorInputEvent e{};
        e.type = static_cast<aps_editor_input_event_type>(down ? EditorInputEventType::MouseDown
                                                               : EditorInputEventType::MouseUp);
        e.modifiers = modifiers();
        e.x = GET_X_LPARAM(lp);
        e.y = GET_Y_LPARAM(lp);
        e.button = button;
        client_.editorInput(e);
        if (!down && buttons_ == 0 && GetCapture() == hwnd_) ReleaseCapture();
    }

    // Capture lost while buttons were held (another window took it, Alt+Tab, ...): release them.
    void releaseButtons() {
        POINT pt{};
        GetCursorPos(&pt);
        ScreenToClient(hwnd_, &pt);
        for (int button = 0; button < 3; ++button) {
            if (!(buttons_ & (1 << button))) continue;
            EditorInputEvent e{};
            e.type = static_cast<aps_editor_input_event_type>(EditorInputEventType::MouseUp);
            e.modifiers = modifiers();
            e.x = pt.x;
            e.y = pt.y;
            e.button = button;
            client_.editorInput(e);
        }
        buttons_ = 0;
    }

    bool handle(UINT msg, WPARAM wp, LPARAM lp, LRESULT& result) {
        switch (msg) {
            case WM_TIMER:
                if (wp != kTimerId) return false;
                {
                    const auto now = Clock::now();
                    const double elapsed = firstTick_ ? 0.0 : std::chrono::duration<double>(now - lastTick_).count();
                    lastTick_ = now;
                    firstTick_ = false;
                    client_.editorIdle(elapsed);
                }
                return true;

            case WM_ERASEBKGND: result = 1; return true; // the editor paints everything
            case WM_PAINT: {
                PAINTSTRUCT ps;
                BeginPaint(hwnd_, &ps); // frames are drawn on the timer
                EndPaint(hwnd_, &ps);
                return true;
            }
            case WM_GETDLGCODE: {
                // The host (or a dialog manager) asks whether we want a key before using it itself: the editor
                // answers. It gets the key now; if the host then delivers it to us too, it is dropped once.
                const MSG* asked = reinterpret_cast<const MSG*>(lp);
                if (asked && (asked->message == WM_KEYDOWN || asked->message == WM_SYSKEYDOWN)) {
                    EditorInputEvent e{};
                    e.type = static_cast<aps_editor_input_event_type>(EditorInputEventType::KeyDown);
                    e.modifiers = modifiers();
                    e.key = virtualKey(asked->wParam, asked->lParam);
                    const bool used = client_.editorInput(e);
                    if (!used) {
                        // The host keeps it (its key-up may not come here either): a press and release
                        e.type = static_cast<aps_editor_input_event_type>(EditorInputEventType::KeyUp);
                        client_.editorInput(e);
                    }
                    keyFromHost(e.key, true, 0);
                    result = used ? (DLGC_WANTALLKEYS | DLGC_WANTCHARS) : 0;
                    return true;
                }
                result = DLGC_WANTALLKEYS | DLGC_WANTCHARS;
                return true;
            }

            case WM_MOUSEMOVE:
                if (!tracking_) {
                    TRACKMOUSEEVENT tme{sizeof(tme), TME_LEAVE, hwnd_, 0};
                    tracking_ = TrackMouseEvent(&tme) != FALSE;
                }
                send(EditorInputEventType::MouseMove, GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
                return true;
            case WM_MOUSELEAVE:
                tracking_ = false;
                send(EditorInputEventType::MouseLeave);
                return true;

            case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: mouseButton(0, true, lp); return true;
            case WM_LBUTTONUP: mouseButton(0, false, lp); return true;
            case WM_RBUTTONDOWN: case WM_RBUTTONDBLCLK: mouseButton(1, true, lp); return true;
            case WM_RBUTTONUP: mouseButton(1, false, lp); return true;
            case WM_MBUTTONDOWN: case WM_MBUTTONDBLCLK: mouseButton(2, true, lp); return true;
            case WM_MBUTTONUP: mouseButton(2, false, lp); return true;
            case WM_CAPTURECHANGED:
                if (reinterpret_cast<HWND>(lp) != hwnd_ && buttons_) releaseButtons();
                return false;

            case WM_MOUSEWHEEL: {
                POINT pt{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)}; // screen coordinates
                ScreenToClient(hwnd_, &pt);
                EditorInputEvent e{};
                e.type = static_cast<aps_editor_input_event_type>(EditorInputEventType::MouseWheel);
                e.modifiers = modifiers();
                e.x = pt.x;
                e.y = pt.y;
                e.wheel_delta = GET_WHEEL_DELTA_WPARAM(wp);
                client_.editorInput(e);
                return true;
            }

            case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_KEYUP: case WM_SYSKEYUP: {
                const bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                EditorInputEvent e{};
                e.type = static_cast<aps_editor_input_event_type>(down ? EditorInputEventType::KeyDown
                                                                       : EditorInputEventType::KeyUp);
                e.modifiers = modifiers();
                e.key = virtualKey(wp, lp);
                if (hostKey_ && e.key == hostKey_ && down == hostKeyDown_) {
                    hostKey_ = 0; // already passed on from the host's key callback
                } else {
                    client_.editorInput(e);
                }
                // System keys (Alt+...) still go to DefWindowProc, so Alt+F4 and menu accelerators work.
                return msg == WM_KEYDOWN || msg == WM_KEYUP;
            }
            // Alt+Enter (editors use it for fullscreen) would make DefWindowProc beep: there is no menu to open.
            // Other Alt+key combinations still go on, to the host's menus.
            case WM_SYSCHAR: return wp == '\r';
            case WM_CHAR: {
                const auto unit = static_cast<uint32_t>(wp);
                uint32_t codePoint = unit;
                if (unit >= 0xD800 && unit < 0xDC00) { highSurrogate_ = unit; return true; }
                if (unit >= 0xDC00 && unit < 0xE000) {
                    if (!highSurrogate_) return true;
                    codePoint = 0x10000 + ((highSurrogate_ - 0xD800) << 10) + (unit - 0xDC00);
                }
                highSurrogate_ = 0;
                if (hostChar_ && codePoint == hostChar_) { // already passed on from the host's key callback
                    hostChar_ = 0;
                    return true;
                }
                EditorInputEvent e{};
                e.type = static_cast<aps_editor_input_event_type>(EditorInputEventType::Text);
                e.modifiers = modifiers();
                e.character = static_cast<int32_t>(codePoint);
                client_.editorInput(e);
                return true;
            }

            // The host's (per-monitor DPI aware) window moved to a monitor with another DPI.
            case WM_DPICHANGED_AFTERPARENT: client_.editorDpiChanged(dpiScale()); return false;

            case WM_SETFOCUS: send(EditorInputEventType::FocusGained); return false;
            case WM_KILLFOCUS:
                highSurrogate_ = 0;
                send(EditorInputEventType::FocusLost);
                return false;

            default: return false;
        }
    }

    Client& client_;
    HWND hwnd_ = nullptr;
    bool timerRunning_ = false;
    bool firstTick_ = true;
    Clock::time_point lastTick_{};
    bool tracking_ = false;
    int buttons_ = 0;
    uint32_t highSurrogate_ = 0;
    bool cursorVisible_ = true;
    bool fullscreen_ = false;
    HWND parent_ = nullptr;
    int hostKey_ = 0; // see keyFromHost
    bool hostKeyDown_ = false;
    uint32_t hostChar_ = 0;
    uint32_t windowedWidth_ = 0, windowedHeight_ = 0;
};

} // namespace

std::unique_ptr<EditorWindow> EditorWindow::create(intptr_t parent, uint32_t width, uint32_t height, Client& client) {
    if (!parent || !IsWindow(reinterpret_cast<HWND>(parent))) return nullptr;
    auto window = std::make_unique<EditorWindowWin32>(client);
    if (!window->create(reinterpret_cast<HWND>(parent), width, height)) return nullptr;
    return window;
}

EditorWindowType EditorWindow::platformWindowType() { return EditorWindowType::Win32; }

double EditorWindow::platformScale(intptr_t parent) {
    return DpiApi::get().windowScale(reinterpret_cast<HWND>(parent));
}

} // namespace aps
