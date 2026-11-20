using System.Runtime.InteropServices;
using static AudioPlugSharpWindowing.Win32Api;

namespace AudioPlugSharpWindowing;

/// <summary>
/// Windows implementation. Input is translated as AudioPlugSharp's native editor window does it
/// (AudioPlugSharpBridge/Native/Core/EditorWindowWin32.cpp), so an editor gets the same events in a DAW and in a
/// standalone host:
/// - The instance is found through a window property, not GWLP_USERDATA: SDL subclasses a window when it wraps it
///   (SDL_CreateWindowFrom) and keeps its own data there.
/// - The mouse is captured while a button is held, so drags that leave the window keep reporting.
/// - A click takes keyboard focus.
/// - KeepControl puts a second subclass on top of SDL's, which answers WM_SETCURSOR itself (cursor hiding) and would
///   close a fullscreen window on Alt+F4 (it goes back instead).
/// - Fullscreen moves the same HWND (and so an OpenGL context on it) into a borderless popup covering its monitor,
///   and back.
/// </summary>
internal unsafe class Win32Window : NativeWindow
{
    const string InstanceProp = "AudioPlugSharpWindowing.Window";
    const string OuterProp = "AudioPlugSharpWindowing.Outer";
    const string InnerProcProp = "AudioPlugSharpWindowing.InnerProc";
    const string TopLevelClass = "AudioPlugSharpWindowing.TopLevel";
    const string ChildClass = "AudioPlugSharpWindowing.Child";
    const nuint ModalLoopTimerId = 1;

    static bool classesRegistered;

    static Win32Window()
    {
        // Real pixels and per-monitor scales (an unaware process gets stretched bitmaps). Fails harmlessly when the
        // application's manifest already chose an awareness.
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    readonly bool isChild;
    GCHandle self;
    nint hwnd;
    Win32Window? focusChild;

    bool tracking;
    int buttons;
    uint highSurrogate;
    bool cursorVisible = true;

    bool fullscreen;
    nint windowedParent;
    int windowedWidth, windowedHeight;
    nint windowedStyle;
    RECT windowedRect;

    Win32Window(bool isChild)
    {
        this.isChild = isChild;
        self = GCHandle.Alloc(this);
    }

    public static Win32Window Create(string title, int width, int height, bool resizable)
    {
        RegisterClasses();

        uint style = WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN;
        if (!resizable) style &= ~(WS_THICKFRAME | WS_MAXIMIZEBOX);

        RECT frame = new() { Right = width, Bottom = height };
        AdjustWindowRectExForDpi(&frame, style, 0, 0, GetDpiForSystem());

        var window = new Win32Window(isChild: false);
        fixed (char* cls = TopLevelClass, text = title)
        {
            window.hwnd = CreateWindowExW(0, cls, text, style, CW_USEDEFAULT, CW_USEDEFAULT, frame.Right - frame.Left,
                frame.Bottom - frame.Top, 0, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(window.self));
        }
        window.CheckCreated();
        window.SetClientSize(width, height); // for the DPI of the monitor it opened on
        return window;
    }

    public override NativeWindow CreateChild(int x, int y, int width, int height)
    {
        var child = new Win32Window(isChild: true);
        fixed (char* cls = ChildClass)
        {
            child.hwnd = CreateWindowExW(0, cls, null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, x, y,
                width, height, hwnd, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(child.self));
        }
        child.CheckCreated();
        focusChild = child;
        return child;
    }

    void CheckCreated()
    {
        if (hwnd != 0) return;
        int error = Marshal.GetLastPInvokeError();
        if (self.IsAllocated) self.Free();
        throw new InvalidOperationException($"CreateWindowEx failed ({error})");
    }

    static void RegisterClasses()
    {
        if (classesRegistered) return;
        nint instance = GetModuleHandleW(null);
        nint arrow = LoadCursorW(0, IDC_ARROW);

        fixed (char* top = TopLevelClass, child = ChildClass)
        {
            WNDCLASSEXW wc = new()
            {
                Size = (uint)sizeof(WNDCLASSEXW),
                Style = CS_HREDRAW | CS_VREDRAW,
                WndProc = &WndProc,
                Instance = instance,
                Cursor = arrow,
                Background = GetStockObject(BLACK_BRUSH),
                ClassName = top,
            };
            Register(&wc);

            wc.Style = CS_OWNDC | CS_DBLCLKS; // CS_OWNDC: OpenGL keeps one DC per window
            wc.Background = 0; // the content paints everything
            wc.ClassName = child;
            Register(&wc);
        }
        classesRegistered = true;

        static void Register(WNDCLASSEXW* wc)
        {
            const int ERROR_CLASS_ALREADY_EXISTS = 1410;
            if (RegisterClassExW(wc) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    // ---- NativeWindow ----------------------------------------------------------------------------------------

    public override nint Handle => hwnd;

    public override NativeWindowType WindowType => NativeWindowType.Win32;

    public override double Scale
    {
        get
        {
            uint dpi = hwnd != 0 ? GetDpiForWindow(hwnd) : GetDpiForSystem();
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
    }

    public override int ClientWidth
    {
        get { RECT r; GetClientRect(hwnd, &r); return r.Right; }
    }

    public override int ClientHeight
    {
        get { RECT r; GetClientRect(hwnd, &r); return r.Bottom; }
    }

    public override void SetClientSize(int width, int height)
    {
        if (fullscreen)
        {
            windowedWidth = width;
            windowedHeight = height;
            return;
        }
        if (isChild)
        {
            SetWindowPos(hwnd, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }
        uint style = (uint)GetWindowLongPtrW(hwnd, GWL_STYLE);
        RECT frame = new() { Right = width, Bottom = height };
        AdjustWindowRectExForDpi(&frame, style, 0, 0, GetDpiForWindow(hwnd));
        SetWindowPos(hwnd, 0, 0, 0, frame.Right - frame.Left, frame.Bottom - frame.Top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public override void SetPosition(int x, int y) =>
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public override void SetTitle(string title)
    {
        fixed (char* text = title) SetWindowTextW(hwnd, text);
    }

    public override void Show() => ShowWindow(hwnd, isChild ? SW_SHOWNA : SW_SHOW);

    public override void Hide() => ShowWindow(hwnd, SW_HIDE);

    public override void Focus() => SetFocus(hwnd);

    public override bool SetCursorVisible(bool visible)
    {
        cursorVisible = visible;
        POINT pt;
        if (GetCursorPos(&pt) != 0 && WindowFromPoint(pt) == hwnd)
        {
            if (!visible) SetCursor(0);
            else SendMessageW(hwnd, WM_SETCURSOR, hwnd, HTCLIENT | ((nint)WM_MOUSEMOVE << 16));
        }
        return true;
    }

    public override bool IsFullscreen => fullscreen;

    public override bool SetFullscreen(bool fullscreen)
    {
        if (fullscreen == this.fullscreen) return fullscreen;

        if (fullscreen)
        {
            MONITORINFO monitor = new() { Size = (uint)sizeof(MONITORINFO) };
            if (GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor) == 0) return false;
            RECT client;
            GetClientRect(hwnd, &client);
            windowedWidth = client.Right;
            windowedHeight = client.Bottom;
            windowedStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);
            RECT r = monitor.Monitor;

            if (isChild)
            {
                // To top level: SetParent first, then WS_CHILD -> WS_POPUP (the order SetParent's documentation gives).
                windowedParent = GetAncestor(hwnd, 1 /* GA_PARENT */);
                SetParent(hwnd, 0);
                SetWindowLongPtrW(hwnd, GWL_STYLE, (nint)(((uint)windowedStyle & ~WS_CHILD) | WS_POPUP));
            }
            else
            {
                RECT frame;
                GetWindowRect(hwnd, &frame);
                windowedRect = frame;
                SetWindowLongPtrW(hwnd, GWL_STYLE, (nint)(((uint)windowedStyle & ~WS_OVERLAPPEDWINDOW) | WS_POPUP));
            }
            this.fullscreen = true;
            SetWindowPos(hwnd, HWND_TOP, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        else
        {
            if (isChild)
            {
                // Back into the parent: WS_POPUP -> WS_CHILD first, then SetParent.
                SetWindowLongPtrW(hwnd, GWL_STYLE, windowedStyle);
                SetParent(hwnd, windowedParent);
                this.fullscreen = false;
                SetWindowPos(hwnd, 0, 0, 0, windowedWidth, windowedHeight, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
                if (windowedParent != 0) SetForegroundWindow(GetAncestor(windowedParent, GA_ROOT));
                SetFocus(hwnd);
            }
            else
            {
                SetWindowLongPtrW(hwnd, GWL_STYLE, windowedStyle);
                this.fullscreen = false;
                RECT r = windowedRect;
                SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_FRAMECHANGED);
            }
        }
        return this.fullscreen;
    }

    public override void KeepControl()
    {
        nint current = GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
        delegate* unmanaged<nint, uint, nint, nint, nint> outer = &OuterProc;
        if (current == (nint)outer) return;
        fixed (char* inner = InnerProcProp, outerName = OuterProp)
        {
            SetPropW(hwnd, inner, current);
            SetPropW(hwnd, outerName, GCHandle.ToIntPtr(self));
        }
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (nint)outer);
    }

    void RemoveOuterProc()
    {
        fixed (char* inner = InnerProcProp, outerName = OuterProp)
        {
            if (GetPropW(hwnd, outerName) == 0) return;
            delegate* unmanaged<nint, uint, nint, nint, nint> outer = &OuterProc;
            if (GetWindowLongPtrW(hwnd, GWLP_WNDPROC) == (nint)outer)
            {
                SetWindowLongPtrW(hwnd, GWLP_WNDPROC, GetPropW(hwnd, inner));
                RemovePropW(hwnd, inner);
            }
            // Otherwise someone subclassed on top of us: OuterProc stays in the chain, passing everything through.
            RemovePropW(hwnd, outerName);
        }
    }

    public override void Dispose()
    {
        if (hwnd == 0) return;
        if (fullscreen) SetFullscreen(false);
        RemoveOuterProc();
        DestroyWindow(hwnd); // WM_NCDESTROY releases the rest
    }

    // ---- window procedures ---------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wp, nint lp)
    {
        try
        {
            fixed (char* prop = InstanceProp)
            {
                if (msg == WM_NCCREATE) SetPropW(hwnd, prop, ((CREATESTRUCTW*)lp)->CreateParams);
                nint handle = GetPropW(hwnd, prop);
                if (handle != 0 && GCHandle.FromIntPtr(handle).Target is Win32Window window)
                {
                    if (window.hwnd == 0) window.hwnd = hwnd; // messages sent during CreateWindowEx
                    if (window.HandleMessage(msg, wp, lp, out nint result)) return result;
                    if (msg == WM_NCDESTROY)
                    {
                        RemovePropW(hwnd, prop);
                        window.Destroyed();
                    }
                }
            }
        }
        catch (Exception e)
        {
            WindowLoop.Fail(e);
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    // The subclass on top of the content's own (see KeepControl).
    [UnmanagedCallersOnly]
    static nint OuterProc(nint hwnd, uint msg, nint wp, nint lp)
    {
        nint inner;
        try
        {
            fixed (char* outerName = OuterProp, innerName = InnerProcProp)
            {
                nint handle = GetPropW(hwnd, outerName);
                if (handle != 0 && GCHandle.FromIntPtr(handle).Target is Win32Window window)
                {
                    if (msg == WM_SETCURSOR && !window.cursorVisible && LowWord(lp) == HTCLIENT)
                    {
                        SetCursor(0);
                        return 1;
                    }
                    if (msg == WM_CLOSE && window.fullscreen) // Alt+F4 on a fullscreen window: back instead
                    {
                        window.SetFullscreen(false);
                        return 0;
                    }
                }
                inner = GetPropW(hwnd, innerName);
            }
        }
        catch (Exception e)
        {
            WindowLoop.Fail(e);
            return DefWindowProcW(hwnd, msg, wp, lp);
        }
        return inner != 0 ? CallWindowProcW(inner, hwnd, msg, wp, lp) : DefWindowProcW(hwnd, msg, wp, lp);
    }

    void Destroyed()
    {
        hwnd = 0;
        if (self.IsAllocated) self.Free();
    }

    bool HandleMessage(uint msg, nint wp, nint lp, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case WM_SETCURSOR:
                if (!cursorVisible && LowWord(lp) == HTCLIENT)
                {
                    SetCursor(0);
                    result = 1;
                    return true;
                }
                return false;

            case WM_ERASEBKGND:
                if (!isChild) return false;
                result = 1; // the content paints everything
                return true;

            case WM_PAINT:
                if (!isChild) return false;
                PAINTSTRUCT ps;
                BeginPaint(hwnd, &ps); // the content draws on its own schedule
                EndPaint(hwnd, &ps);
                return true;

            case WM_GETDLGCODE:
                result = (nint)(DLGC_WANTALLKEYS | DLGC_WANTCHARS);
                return true;

            case WM_SIZE:
                if (wp != SIZE_MINIMIZED) RaiseResized(LowWord(lp), HighWord(lp));
                return false;

            case WM_DPICHANGED:
                if (isChild) return false;
                // Windows suggests a frame for the new DPI that keeps the window's place.
                RECT* suggested = (RECT*)lp;
                SetWindowPos(hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left,
                    suggested->Bottom - suggested->Top, SWP_NOZORDER | SWP_NOACTIVATE);
                RaiseScaleChanged(LowWord(wp) / 96.0);
                return true;

            case WM_DPICHANGED_AFTERPARENT:
                RaiseScaleChanged(Scale);
                return false;

            // Windows runs its own loop while a window is moved or resized: keep the timers going.
            case WM_ENTERSIZEMOVE:
                SetTimer(hwnd, ModalLoopTimerId, 5, 0);
                return false;
            case WM_EXITSIZEMOVE:
                KillTimer(hwnd, ModalLoopTimerId);
                return false;
            case WM_TIMER:
                if (wp != (nint)ModalLoopTimerId) return false;
                WindowLoop.RunDueTimers();
                return true;

            case WM_CLOSE:
                if (fullscreen)
                    SetFullscreen(false);
                else if (RaiseCloseRequested())
                    DestroyWindow(hwnd);
                return true;

            case WM_DESTROY:
                RaiseClosed();
                return false;

            case WM_SETFOCUS:
                if (!isChild && focusChild != null && focusChild.hwnd != 0)
                {
                    SetFocus(focusChild.hwnd); // a top-level window passes focus on to its content
                    return true;
                }
                Send(WindowInputEventType.FocusGained);
                return false;
            case WM_KILLFOCUS:
                highSurrogate = 0;
                Send(WindowInputEventType.FocusLost);
                return false;

            case WM_MOUSEMOVE:
                if (!tracking)
                {
                    TRACKMOUSEEVENT tme = new() { Size = (uint)sizeof(TRACKMOUSEEVENT), Flags = TME_LEAVE, Hwnd = hwnd };
                    tracking = TrackMouseEvent(&tme) != 0;
                }
                Send(WindowInputEventType.MouseMove, SignedLowWord(lp), SignedHighWord(lp));
                return true;
            case WM_MOUSELEAVE:
                tracking = false;
                Send(WindowInputEventType.MouseLeave);
                return true;

            case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: MouseButtonEvent(MouseButton.Left, true, lp); return true;
            case WM_LBUTTONUP: MouseButtonEvent(MouseButton.Left, false, lp); return true;
            case WM_RBUTTONDOWN: case WM_RBUTTONDBLCLK: MouseButtonEvent(MouseButton.Right, true, lp); return true;
            case WM_RBUTTONUP: MouseButtonEvent(MouseButton.Right, false, lp); return true;
            case WM_MBUTTONDOWN: case WM_MBUTTONDBLCLK: MouseButtonEvent(MouseButton.Middle, true, lp); return true;
            case WM_MBUTTONUP: MouseButtonEvent(MouseButton.Middle, false, lp); return true;
            case WM_CAPTURECHANGED:
                if (lp != hwnd && buttons != 0) ReleaseButtons();
                return false;

            case WM_MOUSEWHEEL:
            {
                POINT pt = new() { X = SignedLowWord(lp), Y = SignedHighWord(lp) }; // screen coordinates
                ScreenToClient(hwnd, &pt);
                WindowInputEvent e = new()
                {
                    Type = WindowInputEventType.MouseWheel,
                    Modifiers = Modifiers(),
                    X = pt.X,
                    Y = pt.Y,
                    WheelDelta = SignedHighWord(wp),
                };
                RaiseInput(in e);
                return true;
            }

            case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_KEYUP: case WM_SYSKEYUP:
            {
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                WindowInputEvent e = new()
                {
                    Type = down ? WindowInputEventType.KeyDown : WindowInputEventType.KeyUp,
                    Modifiers = Modifiers(),
                    Key = VirtualKey(wp, lp),
                };
                RaiseInput(in e);
                // System keys (Alt+...) still go to DefWindowProc, so Alt+F4 and the system menu work.
                return msg == WM_KEYDOWN || msg == WM_KEYUP;
            }

            // Alt+Enter (editors use it for fullscreen) would make DefWindowProc beep: there is no menu to open.
            case WM_SYSCHAR:
                return wp == '\r';

            case WM_CHAR:
            {
                uint unit = (uint)wp;
                uint codePoint = unit;
                if (unit >= 0xD800 && unit < 0xDC00)
                {
                    highSurrogate = unit;
                    return true;
                }
                if (unit >= 0xDC00 && unit < 0xE000)
                {
                    if (highSurrogate == 0) return true;
                    codePoint = 0x10000 + ((highSurrogate - 0xD800) << 10) + (unit - 0xDC00);
                }
                highSurrogate = 0;
                WindowInputEvent e = new()
                {
                    Type = WindowInputEventType.Text,
                    Modifiers = Modifiers(),
                    Character = (int)codePoint,
                };
                RaiseInput(in e);
                return true;
            }

            default:
                return false;
        }
    }

    // ---- input -------------------------------------------------------------------------------------------------

    static KeyModifiers Modifiers()
    {
        KeyModifiers m = KeyModifiers.None;
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) m |= KeyModifiers.Shift;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) m |= KeyModifiers.Control;
        if ((GetKeyState(VK_MENU) & 0x8000) != 0) m |= KeyModifiers.Alt;
        if (((GetKeyState(VK_LWIN) | GetKeyState(VK_RWIN)) & 0x8000) != 0) m |= KeyModifiers.Meta;
        return m;
    }

    // Generic modifier codes -> left/right-specific ones (MonoGame's Keys only has the specific ones).
    static int VirtualKey(nint wp, nint lp)
    {
        bool extended = ((lp >> 24) & 1) != 0;
        return (int)wp switch
        {
            VK_SHIFT => (int)MapVirtualKeyW((uint)((lp >> 16) & 0xFF), MAPVK_VSC_TO_VK_EX),
            VK_CONTROL => extended ? VK_RCONTROL : VK_LCONTROL,
            VK_MENU => extended ? VK_RMENU : VK_LMENU,
            _ => (int)wp,
        };
    }

    void Send(WindowInputEventType type, int x = 0, int y = 0)
    {
        WindowInputEvent e = new() { Type = type, Modifiers = Modifiers(), X = x, Y = y };
        RaiseInput(in e);
    }

    void MouseButtonEvent(MouseButton button, bool down, nint lp)
    {
        int bit = 1 << (int)button;
        if (down)
        {
            if (GetFocus() != hwnd) SetFocus(hwnd);
            if (buttons == 0) SetCapture(hwnd);
            buttons |= bit;
        }
        else
        {
            if ((buttons & bit) == 0) return; // the press went elsewhere
            buttons &= ~bit;
        }
        WindowInputEvent e = new()
        {
            Type = down ? WindowInputEventType.MouseDown : WindowInputEventType.MouseUp,
            Modifiers = Modifiers(),
            X = SignedLowWord(lp),
            Y = SignedHighWord(lp),
            Button = button,
        };
        RaiseInput(in e);
        if (!down && buttons == 0 && GetCapture() == hwnd) ReleaseCapture();
    }

    // Capture lost while buttons were held (another window took it, Alt+Tab, ...): release them.
    void ReleaseButtons()
    {
        POINT pt;
        GetCursorPos(&pt);
        ScreenToClient(hwnd, &pt);
        for (int button = 0; button < 3; button++)
        {
            if ((buttons & (1 << button)) == 0) continue;
            WindowInputEvent e = new()
            {
                Type = WindowInputEventType.MouseUp,
                Modifiers = Modifiers(),
                X = pt.X,
                Y = pt.Y,
                Button = (MouseButton)button,
            };
            RaiseInput(in e);
        }
        buttons = 0;
    }
}
