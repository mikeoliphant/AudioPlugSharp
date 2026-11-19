// TestWindow.h - shared by the format test hosts (ClapTestHost, Vst3TestHost): a top-level "DAW" window with
// child slots that parent plugin editors, window-only captures, synthetic mouse and key input, and queries for
// editor window services (fullscreen, cursor).
// Win32: posted messages and PrintWindow. X11: XSendEvent and XGetImage (needs a display).
#pragma once

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <thread>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/keysym.h>
#endif

namespace aps_test {

namespace fs = std::filesystem;

inline fs::path executableDir() {
#ifdef _WIN32
    wchar_t buf[MAX_PATH];
    DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    return fs::path(std::wstring(buf, n)).parent_path();
#else
    return fs::canonical("/proc/self/exe").parent_path();
#endif
}

struct Capture {
    int width = 0, height = 0;
    std::vector<uint32_t> pixels; // 0xAARRGGBB, top-down

    int r(int x, int y) const { return (pixels[size_t(y) * width + x] >> 16) & 0xFF; }
    int g(int x, int y) const { return (pixels[size_t(y) * width + x] >> 8) & 0xFF; }
    int b(int x, int y) const { return pixels[size_t(y) * width + x] & 0xFF; }

    // Pixels in [x0,x1) x [y0,y1) within `tolerance` of a colour.
    int countNear(int x0, int y0, int x1, int y1, int cr, int cg, int cb, int tolerance) const {
        int n = 0;
        for (int y = std::max(y0, 0); y < std::min(y1, height); ++y)
            for (int x = std::max(x0, 0); x < std::min(x1, width); ++x)
                n += std::abs(r(x, y) - cr) <= tolerance && std::abs(g(x, y) - cg) <= tolerance && std::abs(b(x, y) - cb) <= tolerance;
        return n;
    }

    int countDifferent(const Capture& other, int x0, int y0, int x1, int y1) const {
        if (other.width != width || other.height != height) return width * height;
        int n = 0;
        for (int y = std::max(y0, 0); y < std::min(y1, height); ++y)
            for (int x = std::max(x0, 0); x < std::min(x1, width); ++x)
                n += pixels[size_t(y) * width + x] != other.pixels[size_t(y) * width + x];
        return n;
    }
};

// 32-bit top-down BMP.
inline void saveBmp(const Capture& c, const fs::path& path) {
    std::FILE* f = nullptr;
#ifdef _WIN32
    if (_wfopen_s(&f, path.c_str(), L"wb") != 0) f = nullptr;
#else
    f = std::fopen(path.c_str(), "wb");
#endif
    if (!f) return;
    const uint32_t imageBytes = uint32_t(c.pixels.size() * 4);
    uint8_t header[54] = {'B', 'M'};
    auto put32 = [&](int at, uint32_t v) { for (int i = 0; i < 4; ++i) header[at + i] = uint8_t(v >> (8 * i)); };
    put32(2, 54 + imageBytes); // file size
    put32(10, 54);             // pixel data offset
    put32(14, 40);             // BITMAPINFOHEADER
    put32(18, uint32_t(c.width));
    put32(22, uint32_t(-c.height)); // negative: top-down
    header[26] = 1;                 // planes
    header[28] = 32;                // bits per pixel
    std::fwrite(header, 1, sizeof(header), f);
    std::fwrite(c.pixels.data(), 1, imageBytes, f);
    std::fclose(f);
}

enum class Mouse { Move, Down, Up };

// Keys the tests send (Windows virtual-key codes).
enum class Key { Return = 0x0D, Space = 0x20, F11 = 0x7A, LeftAlt = 0xA4 };

#ifdef _WIN32
// The DAW's window: a top-level window with child "slots", each the parent of one editor.
class DawWindow {
public:

    ~DawWindow() {
        if (top_) DestroyWindow(top_);
    }

    bool open(int width, int height, const char* title) {
        RECT r{0, 0, width, height};
        AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
        top_ = make(L"ApsTestHostTop", L"", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                    CW_USEDEFAULT, CW_USEDEFAULT, r.right - r.left, r.bottom - r.top, nullptr);
        if (top_) SetWindowTextA(top_, title);
        if (top_) ShowWindow(top_, SW_SHOWNOACTIVATE);
        return top_ != nullptr;
    }

    // A child "slot" of the top-level window, the parent of one editor. Returns its HWND.
    intptr_t addSlot(int x, int y, int width, int height) {
        HWND slot = make(L"ApsTestHostSlot", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, x, y, width, height, top_);
        slots_.push_back(slot);
        return reinterpret_cast<intptr_t>(slot);
    }

    bool hasEditor(size_t slot) const { return GetWindow(slots_[slot], GW_CHILD) != nullptr; }

    // The slot's editor window, also while it is fullscreen (no longer the slot's child).
    HWND editor(size_t slot) {
        if (editors_.size() < slots_.size()) editors_.resize(slots_.size(), nullptr);
        if (HWND child = GetWindow(slots_[slot], GW_CHILD)) editors_[slot] = child;
        if (editors_[slot] && !IsWindow(editors_[slot])) editors_[slot] = nullptr;
        return editors_[slot];
    }

    // The editor window's size in pixels (0 x 0 without one).
    void editorSize(size_t slot, int& width, int& height) {
        width = height = 0;
        RECT r{};
        if (HWND e = editor(slot); e && GetClientRect(e, &r)) {
            width = r.right - r.left;
            height = r.bottom - r.top;
        }
    }

    // Fullscreen: a top-level window covering its monitor.
    bool isFullscreen(size_t slot) {
        HWND e = editor(slot);
        if (!e || GetParent(e) != nullptr || !IsWindowVisible(e)) return false;
        MONITORINFO monitor{};
        monitor.cbSize = sizeof(monitor);
        if (!GetMonitorInfoW(MonitorFromWindow(e, MONITOR_DEFAULTTONEAREST), &monitor)) return false;
        RECT r{};
        GetWindowRect(e, &r);
        return EqualRect(&r, &monitor.rcMonitor) != FALSE;
    }

    // What the editor window sets as the cursor over its client area (asked as Windows does, with WM_SETCURSOR).
    bool cursorHidden(size_t slot) {
        HWND e = editor(slot);
        if (!e) return false;
        HCURSOR before = GetCursor();
        SendMessageW(e, WM_SETCURSOR, reinterpret_cast<WPARAM>(e), MAKELPARAM(HTCLIENT, WM_MOUSEMOVE));
        const bool hidden = GetCursor() == nullptr;
        SetCursor(before);
        return hidden;
    }

    // As Reaper asks before using a key as its shortcut: WM_GETDLGCODE with the key's WM_KEYDOWN. Returns the
    // window's answer (DLGC_* flags). Also whether the window carries Reaper's "may want keys" marker.
    LRESULT askForKey(size_t slot, Key k) {
        HWND e = editor(slot);
        if (!e) return 0;
        MSG msg{};
        msg.hwnd = e;
        msg.message = WM_KEYDOWN;
        msg.wParam = WPARAM(k);
        msg.lParam = 1 | (LPARAM(MapVirtualKeyW(UINT(k), MAPVK_VK_TO_VSC)) << 16);
        return SendMessageW(e, WM_GETDLGCODE, msg.wParam, reinterpret_cast<LPARAM>(&msg));
    }
    bool hasWantsKeysMarker(size_t slot) {
        HWND e = editor(slot);
        return e && GetWindowLongPtrW(e, GWLP_USERDATA) == static_cast<LONG_PTR>(0xdeadf00b);
    }

    // A key press or release, with Alt held for Return when altHeld (system keys, as Windows sends them).
    void key(size_t slot, Key k, bool down, bool altHeld = false) {
        HWND e = editor(slot);
        if (!e) return;
        const bool system = altHeld || k == Key::LeftAlt;
        const UINT msg = system ? (down ? WM_SYSKEYDOWN : WM_SYSKEYUP) : (down ? WM_KEYDOWN : WM_KEYUP);
        const WPARAM vk = k == Key::LeftAlt ? VK_MENU : WPARAM(k); // VK_MENU: the window tells left from right
        LPARAM lp = 1 | (LPARAM(MapVirtualKeyW(UINT(vk), MAPVK_VK_TO_VSC)) << 16);
        if (system && down) lp |= LPARAM(1) << 29; // context code: Alt is down
        if (!down) lp |= (LPARAM(1) << 30) | (LPARAM(1) << 31);
        PostMessageW(e, msg, vk, lp);
    }

    void mouse(size_t slot, Mouse m, int x, int y) {
        HWND editor = this->editor(slot);
        if (!editor) return;
        if (m == Mouse::Down) held_ = true;
        if (m == Mouse::Up) held_ = false;
        const UINT msg = m == Mouse::Move ? WM_MOUSEMOVE : m == Mouse::Down ? WM_LBUTTONDOWN : WM_LBUTTONUP;
        PostMessageW(editor, msg, held_ ? MK_LBUTTON : 0, MAKELPARAM(x, y));
    }

    // The window's own content (PrintWindow with PW_RENDERFULLCONTENT also captures OpenGL child windows).
    Capture capture() const {
        Capture c;
        RECT rc{};
        GetClientRect(top_, &rc);
        c.width = rc.right - rc.left;
        c.height = rc.bottom - rc.top;
        c.pixels.assign(size_t(c.width) * c.height, 0);
        HDC windowDc = GetDC(top_);
        HDC memDc = CreateCompatibleDC(windowDc);
        HBITMAP bitmap = CreateCompatibleBitmap(windowDc, c.width, c.height);
        HGDIOBJ old = SelectObject(memDc, bitmap);
        PrintWindow(top_, memDc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
        SelectObject(memDc, old);
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
        bi.bmiHeader.biWidth = c.width;
        bi.bmiHeader.biHeight = -c.height; // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        GetDIBits(memDc, bitmap, 0, UINT(c.height), c.pixels.data(), &bi, DIB_RGB_COLORS);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(top_, windowDc);
        return c;
    }

    void pumpEvents() {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

private:
    static HWND make(const wchar_t* cls, const wchar_t* title, DWORD style, int x, int y, int w, int h, HWND parent) {
        static bool registered = false;
        if (!registered) {
            for (const wchar_t* name : {L"ApsTestHostTop", L"ApsTestHostSlot"}) {
                WNDCLASSEXW wc{};
                wc.cbSize = sizeof(wc);
                wc.lpfnWndProc = DefWindowProcW;
                wc.hInstance = GetModuleHandleW(nullptr);
                wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)); // IDC_ARROW (this file is not built with UNICODE)
                wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(GRAY_BRUSH));
                wc.lpszClassName = name;
                RegisterClassExW(&wc);
            }
            registered = true;
        }
        return CreateWindowExW(0, cls, title, style, x, y, w, h, parent, nullptr, GetModuleHandleW(nullptr), nullptr);
    }

    HWND top_ = nullptr;
    std::vector<HWND> slots_;
    std::vector<HWND> editors_;
    bool held_ = false;
};
#else
class DawWindow {
public:

    ~DawWindow() {
        if (display_) {
            if (top_) XDestroyWindow(display_, top_);
            XCloseDisplay(display_);
        }
    }

    bool open(int width, int height, const char* title) {
        display_ = XOpenDisplay(nullptr);
        if (!display_) {
            const char* d = std::getenv("DISPLAY");
            std::printf("    no X display (DISPLAY=%s)\n", d ? d : "");
            return false;
        }
        width_ = width;
        height_ = height;
        top_ = XCreateSimpleWindow(display_, DefaultRootWindow(display_), 0, 0, unsigned(width), unsigned(height), 0, 0, kGray);
        XStoreName(display_, top_, title);
        XSelectInput(display_, top_, StructureNotifyMask);
        XMapWindow(display_, top_);
        // Wait until it is mapped: capture and focus need a viewable window.
        const auto end = std::chrono::steady_clock::now() + std::chrono::seconds(3);
        bool mapped = false;
        while (!mapped && std::chrono::steady_clock::now() < end) {
            while (XPending(display_) > 0) {
                XEvent ev;
                XNextEvent(display_, &ev);
                if (ev.type == MapNotify && ev.xmap.window == top_) mapped = true;
            }
            if (!mapped) std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
        return mapped;
    }

    // A child "slot" of the top-level window, the parent of one editor. Returns its X11 Window id.
    intptr_t addSlot(int x, int y, int width, int height) {
        ::Window slot = XCreateSimpleWindow(display_, top_, x, y, unsigned(width), unsigned(height), 0, 0, kGray);
        XMapWindow(display_, slot);
        XSync(display_, False);
        slots_.push_back(slot);
        return static_cast<intptr_t>(slot);
    }

    bool hasEditor(size_t slot) const { return child(slot) != 0; }

    // The slot's editor window, also while it is fullscreen (no longer the slot's child).
    ::Window editor(size_t slot) {
        if (editors_.size() < slots_.size()) editors_.resize(slots_.size(), 0);
        if (::Window c = child(slot)) editors_[slot] = c;
        return editors_[slot];
    }

    // The editor window's size in pixels (0 x 0 without one).
    void editorSize(size_t slot, int& width, int& height) {
        width = height = 0;
        XWindowAttributes a{};
        if (const ::Window e = editor(slot); e && XGetWindowAttributes(display_, e, &a)) {
            width = a.width;
            height = a.height;
        }
    }

    // Fullscreen: a child of the root covering the screen.
    bool isFullscreen(size_t slot) {
        const ::Window e = editor(slot);
        if (!e) return false;
        ::Window root = 0, parent = 0, *children = nullptr;
        unsigned count = 0;
        if (!XQueryTree(display_, e, &root, &parent, &children, &count)) return false;
        if (children) XFree(children);
        XWindowAttributes a{};
        if (!XGetWindowAttributes(display_, e, &a)) return false;
        return parent == root && a.map_state == IsViewable && a.width == WidthOfScreen(a.screen) &&
               a.height == HeightOfScreen(a.screen);
    }

    // Not observable from another client without XFixes; the tests skip it on X11.
    bool cursorHidden(size_t) { return false; }

    void key(size_t slot, Key k, bool down, bool altHeld = false) {
        const ::Window e = editor(slot);
        if (!e) return;
        XEvent ev{};
        auto& x = ev.xkey;
        x.type = down ? KeyPress : KeyRelease;
        x.display = display_;
        x.window = e;
        x.root = DefaultRootWindow(display_);
        x.time = ++keyTime_;
        x.state = altHeld ? Mod1Mask : 0;
        const KeySym sym = k == Key::LeftAlt ? XK_Alt_L : k == Key::Space ? XK_space : k == Key::F11 ? XK_F11 : XK_Return;
        x.keycode = XKeysymToKeycode(display_, sym);
        x.same_screen = True;
        XSendEvent(display_, e, False, down ? KeyPressMask : KeyReleaseMask, &ev);
        XFlush(display_);
    }

    // Synthetic events go to the client that selected them on the editor window (the adapter's connection).
    void mouse(size_t slot, Mouse m, int x, int y) {
        const ::Window editor = this->editor(slot);
        if (!editor) return;
        XEvent ev{};
        long mask = 0;
        const unsigned state = held_ ? Button1Mask : 0; // state before the event, as X reports it
        if (m == Mouse::Move) {
            ev.type = MotionNotify;
            auto& e = ev.xmotion;
            e.display = display_;
            e.window = editor;
            e.root = DefaultRootWindow(display_);
            e.x = e.x_root = x;
            e.y = e.y_root = y;
            e.state = state;
            e.same_screen = True;
            mask = PointerMotionMask;
        } else {
            ev.type = m == Mouse::Down ? ButtonPress : ButtonRelease;
            auto& e = ev.xbutton;
            e.display = display_;
            e.window = editor;
            e.root = DefaultRootWindow(display_);
            e.x = e.x_root = x;
            e.y = e.y_root = y;
            e.state = state;
            e.button = Button1;
            e.same_screen = True;
            mask = m == Mouse::Down ? ButtonPressMask : ButtonReleaseMask;
            held_ = m == Mouse::Down;
        }
        XSendEvent(display_, editor, False, mask, &ev);
        XFlush(display_);
    }

    // The window's contents including its children (the editors' OpenGL output), not the screen.
    Capture capture() const {
        Capture c;
        XImage* image = XGetImage(display_, top_, 0, 0, unsigned(width_), unsigned(height_), AllPlanes, ZPixmap);
        if (!image) return c;
        c.width = width_;
        c.height = height_;
        c.pixels.resize(size_t(width_) * height_);
        const unsigned long rm = image->red_mask, gm = image->green_mask, bm = image->blue_mask;
        const auto channel = [](unsigned long pixel, unsigned long mask) {
            if (!mask) return 0u;
            int shift = 0;
            while (!((mask >> shift) & 1)) ++shift;
            return unsigned(((pixel & mask) >> shift) * 255 / (mask >> shift));
        };
        for (int y = 0; y < height_; ++y)
            for (int x = 0; x < width_; ++x) {
                const unsigned long p = XGetPixel(image, x, y);
                c.pixels[size_t(y) * width_ + x] = 0xFF000000u | (channel(p, rm) << 16) | (channel(p, gm) << 8) | channel(p, bm);
            }
        XDestroyImage(image);
        return c;
    }

    void pumpEvents() {
        while (XPending(display_) > 0) {
            XEvent ev;
            XNextEvent(display_, &ev);
        }
    }

private:
    static constexpr unsigned long kGray = 0x808080;

    ::Window child(size_t slot) const {
        ::Window root = 0, parent = 0, *children = nullptr, result = 0;
        unsigned count = 0;
        if (XQueryTree(display_, slots_[slot], &root, &parent, &children, &count) && count > 0) result = children[0];
        if (children) XFree(children);
        return result;
    }

    Display* display_ = nullptr;
    ::Window top_ = 0;
    std::vector<::Window> slots_;
    std::vector<::Window> editors_;
    int width_ = 0, height_ = 0;
    bool held_ = false;
    Time keyTime_ = 1;
};
#endif

} // namespace aps_test
