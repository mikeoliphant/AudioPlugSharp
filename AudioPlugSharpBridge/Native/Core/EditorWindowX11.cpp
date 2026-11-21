// EditorWindowX11.cpp - X11 implementation of aps::EditorWindow.
//
// - Each window has its own X connection, read only on the UI thread from the host's run loop (onHostTimer /
//   onEventFd). The plugin's SDL (MonoGame) opens a connection of its own for OpenGL; it must not select
//   button events on this window, because X allows only one client to do that (this one).
// - A button press makes X grab the pointer for this window until release (an implicit grab), so drags that
//   leave the window keep reporting, as SetCapture does on Win32.
// - A click takes keyboard focus. Keys are translated to Windows virtual-key codes; text comes from the keysym
//   (Latin-1 and Unicode keysyms; no input method yet).
// - Window services: the cursor is hidden with a blank cursor. Fullscreen moves the window to the root as an
//   override-redirect window covering the screen (no window manager round trips to wait for), with keyboard focus
//   set on it, and back into the parent. The screen is X's whole screen: on several monitors it spans them all
//   (per-monitor geometry needs XRandR; not yet).
#include "aps/EditorWindow.h"
#include "aps/NativeLog.h"

#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/keysym.h>

#include <chrono>
#include <cstdio>

namespace aps {
namespace {

// Windows virtual-key codes (MonoGame Keys).
int virtualKey(KeySym ks) {
    if (ks >= XK_a && ks <= XK_z) return int('A' + (ks - XK_a));
    if (ks >= XK_A && ks <= XK_Z) return int('A' + (ks - XK_A));
    if (ks >= XK_0 && ks <= XK_9) return int('0' + (ks - XK_0));
    if (ks >= XK_F1 && ks <= XK_F24) return int(0x70 + (ks - XK_F1));
    if (ks >= XK_KP_0 && ks <= XK_KP_9) return int(0x60 + (ks - XK_KP_0));
    switch (ks) {
        case XK_BackSpace: return 0x08;
        case XK_Tab: case XK_ISO_Left_Tab: return 0x09;
        case XK_Return: case XK_KP_Enter: return 0x0D;
        case XK_Pause: return 0x13;
        case XK_Caps_Lock: return 0x14;
        case XK_Escape: return 0x1B;
        case XK_space: return 0x20;
        case XK_Prior: return 0x21;
        case XK_Next: return 0x22;
        case XK_End: return 0x23;
        case XK_Home: return 0x24;
        case XK_Left: return 0x25;
        case XK_Up: return 0x26;
        case XK_Right: return 0x27;
        case XK_Down: return 0x28;
        case XK_Insert: return 0x2D;
        case XK_Delete: return 0x2E;
        case XK_Super_L: return 0x5B;
        case XK_Super_R: return 0x5C;
        case XK_Menu: return 0x5D;
        case XK_KP_Multiply: return 0x6A;
        case XK_KP_Add: return 0x6B;
        case XK_KP_Subtract: return 0x6D;
        case XK_KP_Decimal: return 0x6E;
        case XK_KP_Divide: return 0x6F;
        case XK_Num_Lock: return 0x90;
        case XK_Scroll_Lock: return 0x91;
        case XK_Shift_L: return 0xA0;
        case XK_Shift_R: return 0xA1;
        case XK_Control_L: return 0xA2;
        case XK_Control_R: return 0xA3;
        case XK_Alt_L: case XK_Meta_L: return 0xA4;
        case XK_Alt_R: case XK_Meta_R: case XK_ISO_Level3_Shift: return 0xA5;
        case XK_semicolon: return 0xBA;
        case XK_equal: return 0xBB;
        case XK_comma: return 0xBC;
        case XK_minus: return 0xBD;
        case XK_period: return 0xBE;
        case XK_slash: return 0xBF;
        case XK_grave: return 0xC0;
        case XK_bracketleft: return 0xDB;
        case XK_backslash: return 0xDC;
        case XK_bracketright: return 0xDD;
        case XK_apostrophe: return 0xDE;
        default: return 0;
    }
}

// The Unicode code point a keysym types, or 0.
uint32_t codePoint(KeySym ks) {
    if ((ks >= 0x20 && ks <= 0x7E) || (ks >= 0xA0 && ks <= 0xFF)) return uint32_t(ks); // Latin-1
    if ((ks & 0xFF000000u) == 0x01000000u) return uint32_t(ks & 0x00FFFFFFu);          // Unicode keysyms
    if (ks >= XK_KP_0 && ks <= XK_KP_9) return uint32_t('0' + (ks - XK_KP_0));
    return 0;
}

int modifiers(unsigned state) {
    int m = 0;
    if (state & ShiftMask) m |= 1;
    if (state & ControlMask) m |= 2;
    if (state & Mod1Mask) m |= 4;
    if (state & Mod4Mask) m |= 8;
    return m;
}

class EditorWindowX11 final : public EditorWindow {
public:
    explicit EditorWindowX11(Client& client) : client_(client) {}

    ~EditorWindowX11() override {
        if (display_) {
            if (blankCursor_) XFreeCursor(display_, blankCursor_);
            if (window_) XDestroyWindow(display_, window_);
            XCloseDisplay(display_);
        }
    }

    bool create(::Window parent, uint32_t width, uint32_t height) {
        display_ = XOpenDisplay(nullptr);
        if (!display_) {
            nativeLog("XOpenDisplay failed (DISPLAY not set?)");
            return false;
        }
        XSetWindowAttributes attributes{};
        attributes.event_mask = ExposureMask | StructureNotifyMask | PointerMotionMask | ButtonPressMask |
                                ButtonReleaseMask | KeyPressMask | KeyReleaseMask | EnterWindowMask |
                                LeaveWindowMask | FocusChangeMask;
        attributes.background_pixmap = None; // the editor paints everything; no flash of a background colour
        window_ = XCreateWindow(display_, parent, 0, 0, width, height, 0, CopyFromParent, InputOutput,
                                CopyFromParent, CWEventMask | CWBackPixmap, &attributes);
        if (!window_) {
            nativeLog("XCreateWindow failed");
            return false;
        }
        XMapWindow(display_, window_);
        XSync(display_, False); // the window must exist on the server before SDL (another connection) wraps it
        return true;
    }

    EditorWindowType windowType() const override { return EditorWindowType::X11; }
    intptr_t handle() const override { return static_cast<intptr_t>(window_); }

    void setSize(uint32_t width, uint32_t height) override {
        if (fullscreen_) { // applied when leaving fullscreen
            windowedWidth_ = width;
            windowedHeight_ = height;
            return;
        }
        XResizeWindow(display_, window_, width, height);
        XFlush(display_);
    }

    void setVisible(bool visible) override {
        visible_ = visible;
        if (visible) XMapWindow(display_, window_);
        else XUnmapWindow(display_, window_);
        XFlush(display_);
    }

    void setTimerRunning(bool running) override {
        if (running && !timerRunning_) firstTick_ = true;
        timerRunning_ = running;
    }

    bool needsHostTimer() const override { return true; }
    int eventFd() const override { return ConnectionNumber(display_); }
    void onEventFd() override { readEvents(); }

    void onHostTimer() override {
        readEvents();
        if (!timerRunning_) return;
        const auto now = Clock::now();
        const double elapsed = firstTick_ ? 0.0 : std::chrono::duration<double>(now - lastTick_).count();
        lastTick_ = now;
        firstTick_ = false;
        client_.editorIdle(elapsed);
    }

    void editorClosing() override {
        setFullscreen(false);
        setCursorVisible(true);
    }

    bool setCursorVisible(bool visible) override {
        if (visible) {
            XUndefineCursor(display_, window_);
        } else {
            if (!blankCursor_) {
                static const char empty[1] = {0};
                const Pixmap pixmap = XCreateBitmapFromData(display_, window_, empty, 1, 1);
                XColor black{};
                blankCursor_ = XCreatePixmapCursor(display_, pixmap, pixmap, &black, &black, 0, 0);
                XFreePixmap(display_, pixmap);
            }
            XDefineCursor(display_, window_, blankCursor_);
        }
        XFlush(display_);
        return true;
    }

    bool setFullscreen(bool fullscreen) override {
        if (fullscreen == fullscreen_) return fullscreen_;
        XWindowAttributes attributes{};
        if (!XGetWindowAttributes(display_, window_, &attributes)) return fullscreen_;
        XSetWindowAttributes change{};
        if (fullscreen) {
            ::Window root = 0, parent = 0, *children = nullptr;
            unsigned count = 0;
            if (!XQueryTree(display_, window_, &root, &parent, &children, &count)) return false;
            if (children) XFree(children);
            parent_ = parent;
            windowedWidth_ = uint32_t(attributes.width);
            windowedHeight_ = uint32_t(attributes.height);
            const uint32_t width = uint32_t(WidthOfScreen(attributes.screen));
            const uint32_t height = uint32_t(HeightOfScreen(attributes.screen));
            // override_redirect can only change while unmapped.
            XUnmapWindow(display_, window_);
            change.override_redirect = True;
            XChangeWindowAttributes(display_, window_, CWOverrideRedirect, &change);
            XReparentWindow(display_, window_, root, 0, 0);
            XMoveResizeWindow(display_, window_, 0, 0, width, height);
            XMapRaised(display_, window_);
            XSync(display_, False);
            fullscreen_ = true;
            if (viewable()) XSetInputFocus(display_, window_, RevertToPointerRoot, CurrentTime);
            XFlush(display_);
            client_.editorWindowResized(width, height);
        } else {
            XUnmapWindow(display_, window_);
            change.override_redirect = False;
            XChangeWindowAttributes(display_, window_, CWOverrideRedirect, &change);
            XReparentWindow(display_, window_, parent_, 0, 0);
            XResizeWindow(display_, window_, windowedWidth_, windowedHeight_);
            if (visible_) XMapWindow(display_, window_);
            XSync(display_, False);
            fullscreen_ = false;
            client_.editorWindowResized(windowedWidth_, windowedHeight_);
        }
        return fullscreen_;
    }

    bool isFullscreen() const override { return fullscreen_; }

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
        XWindowAttributes attributes{};
        XGetWindowAttributes(display_, window_, &attributes);
        width = uint32_t(attributes.width);
        height = uint32_t(attributes.height);
    }

private:
    using Clock = std::chrono::steady_clock;

    void send(EditorInputEventType type, unsigned state, int x = 0, int y = 0, int button = 0, int wheel = 0,
              int key = 0, uint32_t character = 0) {
        EditorInputEvent e{};
        e.type = static_cast<aps_editor_input_event_type>(type);
        e.modifiers = modifiers(state);
        e.x = x;
        e.y = y;
        e.button = button;
        e.wheel_delta = wheel;
        e.key = key;
        e.character = static_cast<int32_t>(character);
        client_.editorInput(e);
    }

    // XSetInputFocus on a window that is not viewable is a BadMatch error, which Xlib's default handler turns
    // into exit(). Real clicks only reach viewable windows, but synthetic ones need not.
    bool viewable() const {
        XWindowAttributes attributes{};
        return XGetWindowAttributes(display_, window_, &attributes) && attributes.map_state == IsViewable;
    }

    void releaseButtons(unsigned state, int x, int y) {
        for (int button = 0; button < 3; ++button)
            if (buttons_ & (1 << button)) send(EditorInputEventType::MouseUp, state, x, y, button);
        buttons_ = 0;
    }

    void readEvents() {
        while (XPending(display_) > 0) {
            XEvent ev;
            XNextEvent(display_, &ev);
            if (ev.xany.window != window_) continue;
            handle(ev);
        }
    }

    void handle(XEvent& ev) {
        switch (ev.type) {
            case MotionNotify:
                send(EditorInputEventType::MouseMove, ev.xmotion.state, ev.xmotion.x, ev.xmotion.y);
                break;
            case ButtonPress:
            case ButtonRelease: {
                const bool down = ev.type == ButtonPress;
                const auto& b = ev.xbutton;
                if (b.button == Button4 || b.button == Button5) { // the wheel: one press per notch
                    if (down) send(EditorInputEventType::MouseWheel, b.state, b.x, b.y, 0, b.button == Button4 ? 120 : -120);
                    break;
                }
                const int button = b.button == Button1 ? 0 : b.button == Button3 ? 1 : b.button == Button2 ? 2 : -1;
                if (button < 0) break; // horizontal wheel, extra buttons
                if (down && viewable()) XSetInputFocus(display_, window_, RevertToParent, b.time);
                if (down) buttons_ |= 1 << button;
                else if (!(buttons_ & (1 << button))) break; // the press went elsewhere
                else buttons_ &= ~(1 << button);
                send(down ? EditorInputEventType::MouseDown : EditorInputEventType::MouseUp, b.state, b.x, b.y, button);
                break;
            }
            case LeaveNotify:
                if (ev.xcrossing.mode == NotifyNormal) send(EditorInputEventType::MouseLeave, ev.xcrossing.state);
                // Another client grabbed the pointer, ending our implicit grab: the button releases will not come
                // here (as WM_CAPTURECHANGED on Win32), so release the held buttons now.
                if (ev.xcrossing.mode == NotifyGrab && buttons_) releaseButtons(ev.xcrossing.state, ev.xcrossing.x, ev.xcrossing.y);
                break;
            case KeyPress:
            case KeyRelease: {
                auto& k = ev.xkey;
                // Auto-repeat arrives as release + press with the same time; report only the presses.
                if (ev.type == KeyRelease && XPending(display_) > 0) {
                    XEvent next;
                    XPeekEvent(display_, &next);
                    if (next.type == KeyPress && next.xkey.time == k.time && next.xkey.keycode == k.keycode) break;
                }
                const int vk = virtualKey(XLookupKeysym(&k, 0));
                const bool down = ev.type == KeyPress;
                // Already passed on from the host's key callback (keyFromHost): drop the key and the text it typed
                const bool fromHost = hostKey_ && vk == hostKey_ && down == hostKeyDown_;
                if (fromHost) hostKey_ = 0;
                if (vk && !fromHost) send(down ? EditorInputEventType::KeyDown : EditorInputEventType::KeyUp, k.state, 0, 0, 0, 0, vk);
                if (down && !(k.state & (ControlMask | Mod1Mask | Mod4Mask))) {
                    char text[8];
                    KeySym typed = NoSymbol;
                    XLookupString(&k, text, sizeof(text), &typed, nullptr);
                    const uint32_t cp = codePoint(typed);
                    if (cp && fromHost && cp == hostChar_) hostChar_ = 0;
                    else if (cp) send(EditorInputEventType::Text, k.state, 0, 0, 0, 0, 0, cp);
                }
                break;
            }
            case FocusIn: send(EditorInputEventType::FocusGained, 0); break;
            case FocusOut: send(EditorInputEventType::FocusLost, 0); break;
            default: break;
        }
    }

    Client& client_;
    Display* display_ = nullptr;
    ::Window window_ = 0;
    bool timerRunning_ = false;
    bool firstTick_ = true;
    Clock::time_point lastTick_{};
    int buttons_ = 0; // held mouse buttons (bit per EditorMouseButton)
    bool visible_ = true;
    Cursor blankCursor_ = 0;
    bool fullscreen_ = false;
    ::Window parent_ = 0;
    int hostKey_ = 0; // see keyFromHost
    bool hostKeyDown_ = false;
    uint32_t hostChar_ = 0;
    uint32_t windowedWidth_ = 0, windowedHeight_ = 0;
};

} // namespace

std::unique_ptr<EditorWindow> EditorWindow::create(intptr_t parent, uint32_t width, uint32_t height, Client& client) {
    if (!parent) return nullptr;
    auto window = std::make_unique<EditorWindowX11>(client);
    if (!window->create(static_cast<::Window>(parent), width, height)) return nullptr;
    return window;
}

EditorWindowType EditorWindow::platformWindowType() { return EditorWindowType::X11; }

// No standard X11 UI scale to read (Xft.dpi, GDK_SCALE, ... are toolkit conventions); hosts can give one (CLAP
// set_scale, VST3 setContentScaleFactor).
double EditorWindow::platformScale(intptr_t) { return 1.0; }

} // namespace aps
