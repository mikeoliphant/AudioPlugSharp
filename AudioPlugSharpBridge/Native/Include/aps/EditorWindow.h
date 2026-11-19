// EditorWindow.h - the native child window that hosts a plugin editor (DESIGN.md §9.3), shared by the
// format adapters.
//
// The adapter creates one EditorWindow per open editor inside the host's parent window. The window drives
// the editor: a ~60 Hz timer calls EditorIdle, and mouse, wheel, key, text and focus input is translated to
// aps::EditorInputEvent (window pixels, Windows virtual-key codes, UTF-32 text) and passed to EditorInput.
// Everything runs on the host's UI thread.
//
// Implementations: Win32 (EditorWindowWin32.cpp, drives itself with WM_TIMER) and X11 (EditorWindowX11.cpp,
// driven by the host's run loop; see needsHostTimer).
#pragma once

#include "aps/generated/abi.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <memory>

namespace aps {

// Editor sizes are logical units (1 unit = 1 pixel at UI scale 1, 96 DPI); windows are sized in pixels.
inline uint32_t toWindowPixels(uint32_t units, double scale) {
    return uint32_t(std::max(1L, std::lround(double(units) * (scale > 0 ? scale : 1.0))));
}
inline uint32_t toLogicalUnits(uint32_t pixels, double scale) {
    return uint32_t(std::max(1L, std::lround(double(pixels) / (scale > 0 ? scale : 1.0))));
}

class EditorWindow {
public:
    // What the window calls; implemented by the adapter (usually forwarding to aps::PluginApi).
    class Client {
    public:
        virtual void editorIdle(double secondsElapsed) = 0;
        // Returns whether the editor used the event.
        virtual bool editorInput(const EditorInputEvent& event) = 0;
        // The window changed size by itself (entering or leaving fullscreen; window pixels): the editor should
        // follow. The host's view keeps its size.
        virtual void editorWindowResized(uint32_t width, uint32_t height) = 0;
        // The window's DPI changed (the host's window moved to another monitor): the new OS UI scale.
        virtual void editorDpiChanged(double scale) = 0;

    protected:
        ~Client() = default;
    };

    virtual ~EditorWindow() = default;

    // The window type this platform creates, and the handle to pass to OpenEditor.
    virtual EditorWindowType windowType() const = 0;
    virtual intptr_t handle() const = 0;

    virtual void setSize(uint32_t width, uint32_t height) = 0;
    virtual void setVisible(bool visible) = 0;

    // Start or stop the idle timer (stopped while hidden).
    virtual void setTimerRunning(bool running) = 0;

    // The editor opened in the window / is about to close. Window services hook in after the editor (which may
    // have subclassed the window, as SDL does on Win32), and are undone before it closes: closing leaves
    // fullscreen and shows the cursor again.
    virtual void editorOpened() {}
    virtual void editorClosing() {}

    // Window services (DESIGN.md §9.3). Each returns false where the platform does not support it.
    // Show or hide the cursor while it is over the window.
    virtual bool setCursorVisible(bool) { return false; }
    // Move the window out of its parent into a borderless top-level window covering its monitor, or back. On a
    // change the window reports its new size through Client::editorWindowResized. Returns whether the window is
    // fullscreen afterwards. While fullscreen, setSize only records the size to return to.
    virtual bool setFullscreen(bool) { return false; }
    virtual bool isFullscreen() const { return false; }
    // The size the window has, or will have again, inside its parent (window pixels).
    virtual void windowedSize(uint32_t& width, uint32_t& height) const = 0;

    // The OS UI scale of this window (1 = 96 DPI).
    virtual double dpiScale() const { return 1.0; }

    // The OS UI scale for an editor about to open in `parent` (0: no window yet, the default for the calling
    // thread). Windows: the window's DPI (96 in a DPI-unaware host, which Windows then stretches). Elsewhere 1.
    static double platformScale(intptr_t parent);

    // The adapter passed this keystroke to the editor itself, from the host's key callback (VST3
    // IPlugView::onKeyDown/onKeyUp: hosts such as Reaper offer keys there first). If the host then also delivers
    // it to the window, the window drops it: the next matching key message, and for a key press that typed
    // `character` (0: none), the next matching character message.
    virtual void keyFromHost(int virtualKey, bool down, uint32_t character) {
        (void)virtualKey; (void)down; (void)character;
    }

    // Run-loop integration, for platforms where the window cannot drive itself (X11 has no WM_TIMER, and its
    // events arrive on a connection someone must read). When needsHostTimer() is true, the adapter calls
    // onHostTimer() about every kHostTimerMs from the host's run loop (CLAP timer-support, VST3 IRunLoop, ...);
    // it reads pending events and, while the timer is running, calls editorIdle. If eventFd() is not -1, the
    // adapter should also call onEventFd() when it becomes readable, so input does not wait for the timer.
    static constexpr uint32_t kHostTimerMs = 16;
    virtual bool needsHostTimer() const { return false; }
    virtual void onHostTimer() {}
    virtual int eventFd() const { return -1; }
    virtual void onEventFd() {}

    // Creates a child window of `parent` (HWND / X11 Window), `width` x `height` window pixels, visible, with
    // the timer stopped. `client` must outlive the window. Returns nullptr on failure (and logs to stderr).
    static std::unique_ptr<EditorWindow> create(intptr_t parent, uint32_t width, uint32_t height, Client& client);

    // The window type create() makes on this platform (None if editors are not supported here).
    static EditorWindowType platformWindowType();

    // Whether windows on this platform need the host's run loop (see needsHostTimer), so an adapter can decline
    // to offer an editor when the host provides no timer.
    static bool platformNeedsHostTimer() { return platformWindowType() == EditorWindowType::X11; }
};

} // namespace aps
