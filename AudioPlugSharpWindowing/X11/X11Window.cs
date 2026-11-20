using System.Text;
using static AudioPlugSharpWindowing.X11Api;

namespace AudioPlugSharpWindowing;

/// <summary>
/// Linux (X11) implementation. Input is translated as AudioPlugSharp's native editor window does it
/// (AudioPlugSharpBridge/Native/Core/EditorWindowX11.cpp), so an editor gets the same events in a DAW and in a
/// standalone host:
/// - One X connection for all of this process's windows, read on the loop's thread. A graphics library that wraps a
///   window (SDL, for OpenGL) uses a connection of its own, and must not select button events on it: X allows only
///   one client to do that.
/// - A button press makes X grab the pointer for the window until release (an implicit grab), so drags that leave
///   the window keep reporting.
/// - A click takes keyboard focus. Keys are translated to Windows virtual-key codes; text comes from the keysym
///   (Latin-1 and Unicode keysyms; no input method yet).
/// - The cursor is hidden with a blank cursor. A child window goes fullscreen as an override-redirect window on the
///   root covering the screen (no window manager round trips), and back into its parent; on several monitors the
///   screen spans them all (per-monitor geometry needs XRandR). A top-level window asks the window manager.
/// - The display scale is 1: X11 has no standard one (Xft.dpi, GDK_SCALE, ... are toolkit conventions).
/// </summary>
internal unsafe class X11Window : NativeWindow
{
    const long ChildEvents = ExposureMask | StructureNotifyMask | PointerMotionMask | ButtonPressMask | ButtonReleaseMask |
                             KeyPressMask | KeyReleaseMask | EnterWindowMask | LeaveWindowMask | FocusChangeMask;
    const long TopLevelEvents = ChildEvents;

    // ---- the connection ------------------------------------------------------------------------------------------

    static nint display;
    static int screen;
    static nuint root;
    static nuint wmProtocols, wmDeleteWindow, netWmName, utf8String, netWmState, netWmStateFullscreen;
    static readonly Dictionary<nuint, X11Window> windows = new();
    static int wakeRead = -1, wakeWrite = -1;

    static nint Display
    {
        get
        {
            if (display == 0) Open();
            return display;
        }
    }

    static void Open()
    {
        // Before any other Xlib call in the process: editors draw with Xlib (SDL's own connection) on other threads.
        XInitThreads();
        display = XOpenDisplay(null);
        if (display == 0) throw new InvalidOperationException("Could not open the X display (is DISPLAY set?)");
        // Xlib's default error handler ends the process.
        XSetErrorHandler(&OnXError);
        screen = XDefaultScreen(display);
        root = XDefaultRootWindow(display);
        wmProtocols = Atom("WM_PROTOCOLS");
        wmDeleteWindow = Atom("WM_DELETE_WINDOW");
        netWmName = Atom("_NET_WM_NAME");
        utf8String = Atom("UTF8_STRING");
        netWmState = Atom("_NET_WM_STATE");
        netWmStateFullscreen = Atom("_NET_WM_STATE_FULLSCREEN");
    }

    static nuint Atom(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return XInternAtom(display, p, 0);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static int OnXError(nint display, nint errorEvent)
    {
        // XErrorEvent: error_code at 32, request_code at 33.
        byte* e = (byte*)errorEvent;
        Console.Error.WriteLine($"[AudioPlugSharpWindowing] X error {e[32]} (request {e[33]})");
        return 0;
    }

    // ---- creation --------------------------------------------------------------------------------------------------

    readonly bool isChild;
    nuint window;
    X11Window? focusChild;
    bool resizable = true;
    int lastWidth, lastHeight;

    int buttons; // held mouse buttons (bit per MouseButton)
    nuint blankCursor;
    bool fullscreen;
    nuint windowedParent;
    int windowedWidth, windowedHeight;

    X11Window(bool isChild) => this.isChild = isChild;

    public static X11Window Create(string title, int width, int height, bool resizable)
    {
        nint d = Display;
        var w = new X11Window(isChild: false) { resizable = resizable, lastWidth = width, lastHeight = height };
        XSetWindowAttributes attributes = new() { BackgroundPixel = XBlackPixel(d, screen), EventMask = TopLevelEvents };
        w.window = XCreateWindow(d, root, 0, 0, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1), 0, 0, InputOutput, 0,
            CWBackPixel | CWEventMask, &attributes);
        if (w.window == 0) throw new InvalidOperationException("XCreateWindow failed");
        windows[w.window] = w;

        nuint delete = wmDeleteWindow;
        XSetWMProtocols(d, w.window, &delete, 1); // the close button sends a message instead of killing the connection
        w.SetSizeHints(width, height);
        w.SetTitle(title);
        return w;
    }

    public override NativeWindow CreateChild(int x, int y, int width, int height)
    {
        nint d = Display;
        var child = new X11Window(isChild: true) { lastWidth = width, lastHeight = height };
        // No background: the content paints everything (no flash of a background colour).
        XSetWindowAttributes attributes = new() { BackgroundPixmap = 0, EventMask = ChildEvents };
        child.window = XCreateWindow(d, window, x, y, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1), 0, 0, InputOutput, 0,
            CWBackPixmap | CWEventMask, &attributes);
        if (child.window == 0) throw new InvalidOperationException("XCreateWindow failed");
        windows[child.window] = child;
        XMapWindow(d, child.window);
        XSync(d, 0); // the window must exist on the server before another connection (SDL) wraps it
        focusChild = child;
        return child;
    }

    // A window that isn't resizable gets the same minimum and maximum size.
    void SetSizeHints(int width, int height)
    {
        if (isChild || resizable) return;
        XSizeHints hints = new() { Flags = PMinSize | PMaxSize, MinWidth = width, MinHeight = height, MaxWidth = width, MaxHeight = height };
        XSetWMNormalHints(display, window, &hints);
    }

    // ---- NativeWindow ------------------------------------------------------------------------------------------

    public override nint Handle => (nint)window;

    public override NativeWindowType WindowType => NativeWindowType.X11;

    public override double Scale => 1;

    public override int ClientWidth
    {
        get { XWindowAttributes a; return XGetWindowAttributes(display, window, &a) != 0 ? a.Width : lastWidth; }
    }

    public override int ClientHeight
    {
        get { XWindowAttributes a; return XGetWindowAttributes(display, window, &a) != 0 ? a.Height : lastHeight; }
    }

    public override void SetClientSize(int width, int height)
    {
        if (fullscreen)
        {
            windowedWidth = width;
            windowedHeight = height;
            return;
        }
        SetSizeHints(width, height);
        XResizeWindow(display, window, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1));
        XFlush(display);
    }

    public override void SetPosition(int x, int y)
    {
        XMoveWindow(display, window, x, y);
        XFlush(display);
    }

    public override void SetTitle(string title)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(title + "\0");
        fixed (byte* p = utf8)
        {
            XStoreName(display, window, p);
            XChangeProperty(display, window, netWmName, utf8String, 8, PropModeReplace, p, utf8.Length - 1);
        }
        XFlush(display);
    }

    public override void Show()
    {
        XMapWindow(display, window);
        XFlush(display);
    }

    public override void Hide()
    {
        XUnmapWindow(display, window);
        XFlush(display);
    }

    public override void Focus()
    {
        // XSetInputFocus on a window that is not viewable is a BadMatch error.
        if (Viewable) XSetInputFocus(display, window, RevertToParent, 0);
        XFlush(display);
    }

    bool Viewable
    {
        get { XWindowAttributes a; return XGetWindowAttributes(display, window, &a) != 0 && a.MapState == IsViewable; }
    }

    public override bool SetCursorVisible(bool visible)
    {
        if (visible)
        {
            XUndefineCursor(display, window);
        }
        else
        {
            if (blankCursor == 0)
            {
                byte empty = 0;
                nuint pixmap = XCreateBitmapFromData(display, window, &empty, 1, 1);
                XColor black = default;
                blankCursor = XCreatePixmapCursor(display, pixmap, pixmap, &black, &black, 0, 0);
                XFreePixmap(display, pixmap);
            }
            XDefineCursor(display, window, blankCursor);
        }
        XFlush(display);
        return true;
    }

    public override bool IsFullscreen => fullscreen;

    public override bool SetFullscreen(bool fullscreen)
    {
        if (fullscreen == this.fullscreen) return fullscreen;
        if (!isChild) return SetTopLevelFullscreen(fullscreen);

        XWindowAttributes a;
        if (XGetWindowAttributes(display, window, &a) == 0) return this.fullscreen;
        XSetWindowAttributes change = default;
        if (fullscreen)
        {
            nuint rootWindow, parent;
            nuint* children = null;
            uint count;
            if (XQueryTree(display, window, &rootWindow, &parent, &children, &count) == 0) return false;
            if (children != null) XFree(children);
            windowedParent = parent;
            windowedWidth = a.Width;
            windowedHeight = a.Height;
            // override_redirect can only change while unmapped.
            XUnmapWindow(display, window);
            change.OverrideRedirect = 1;
            XChangeWindowAttributes(display, window, CWOverrideRedirect, &change);
            XReparentWindow(display, window, root, 0, 0);
            XMoveResizeWindow(display, window, 0, 0, (uint)XDisplayWidth(display, screen), (uint)XDisplayHeight(display, screen));
            XMapRaised(display, window);
            XSync(display, 0);
            this.fullscreen = true;
            if (Viewable) XSetInputFocus(display, window, RevertToPointerRoot, 0);
        }
        else
        {
            XUnmapWindow(display, window);
            change.OverrideRedirect = 0;
            XChangeWindowAttributes(display, window, CWOverrideRedirect, &change);
            XReparentWindow(display, window, windowedParent, 0, 0);
            XResizeWindow(display, window, (uint)Math.Max(windowedWidth, 1), (uint)Math.Max(windowedHeight, 1));
            XMapWindow(display, window);
            XSync(display, 0);
            this.fullscreen = false;
            if (Viewable) XSetInputFocus(display, window, RevertToParent, 0);
        }
        XFlush(display);
        return this.fullscreen;
    }

    // _NET_WM_STATE_FULLSCREEN, which the window manager applies.
    bool SetTopLevelFullscreen(bool fullscreen)
    {
        XEvent e = default;
        e.Type = ClientMessage;
        e.Window = window;
        e.MessageType = netWmState;
        e.Format = 32;
        e.Data0 = fullscreen ? 1 : 0; // add, remove
        e.Data1 = (nint)netWmStateFullscreen;
        e.Data3 = 1; // from an application
        XSendEvent(display, root, 0, SubstructureRedirectMask | SubstructureNotifyMask, &e);
        XFlush(display);
        this.fullscreen = fullscreen;
        return fullscreen;
    }

    public override void KeepControl()
    {
        // Nothing to do: a library that wraps the window uses its own connection and doesn't take its events.
    }

    public override void Dispose()
    {
        if (window == 0) return;
        if (fullscreen) SetFullscreen(false);
        if (blankCursor != 0) XFreeCursor(display, blankCursor);
        windows.Remove(window);
        XDestroyWindow(display, window);
        XFlush(display);
        window = 0;
    }

    // ---- loop ----------------------------------------------------------------------------------------------------

    /// <summary>WindowLoop.Run on Linux: X events, and the timers in between.</summary>
    internal static void RunLoop()
    {
        nint d = Display;
        if (wakeRead < 0)
        {
            int* fds = stackalloc int[2];
            if (pipe(fds) != 0) throw new InvalidOperationException("pipe failed");
            wakeRead = fds[0];
            wakeWrite = fds[1];
        }
        int writeFd = wakeWrite;
        WindowLoop.Wake = () =>
        {
            byte b = 1;
            write(writeFd, &b, 1);
        };

        try
        {
            PollFd* fds = stackalloc PollFd[2];
            byte* drain = stackalloc byte[64];
            XEvent e;
            while (!WindowLoop.Quitting)
            {
                while (!WindowLoop.Quitting && XPending(d) > 0)
                {
                    XNextEvent(d, &e);
                    Dispatch(&e);
                }
                if (WindowLoop.Quitting) break;

                WindowLoop.RunDueTimers();
                if (XPending(d) > 0) continue; // events read (and output flushed) while the timers ran

                double wait = WindowLoop.SecondsUntilNextTimer;
                int milliseconds = double.IsPositiveInfinity(wait) ? -1 : (int)Math.Clamp(Math.Ceiling(wait * 1000), 0, int.MaxValue);
                fds[0] = new PollFd { Fd = XConnectionNumber(d), Events = POLLIN };
                fds[1] = new PollFd { Fd = wakeRead, Events = POLLIN };
                poll(fds, 2, milliseconds);
                if ((fds[1].Revents & POLLIN) != 0) read(wakeRead, drain, 64);
            }
        }
        finally
        {
            WindowLoop.Wake = null;
        }
    }

    static void Dispatch(XEvent* e)
    {
        if (!windows.TryGetValue(e->Window, out X11Window? w)) return;
        try
        {
            w.HandleEvent(e);
        }
        catch (Exception exception)
        {
            WindowLoop.Fail(exception);
        }
    }

    void HandleEvent(XEvent* e)
    {
        switch (e->Type)
        {
            case MotionNotify:
                Send(WindowInputEventType.MouseMove, e->State, e->X, e->Y);
                break;

            case ButtonPress:
            case ButtonRelease:
            {
                bool down = e->Type == ButtonPress;
                uint button = e->KeycodeOrButton;
                if (button == Button4 || button == Button5) // the wheel: one press per notch
                {
                    if (down) Send(WindowInputEventType.MouseWheel, e->State, e->X, e->Y, wheel: button == Button4 ? 120 : -120);
                    break;
                }
                int b = button == Button1 ? 0 : button == Button3 ? 1 : button == Button2 ? 2 : -1;
                if (b < 0) break; // horizontal wheel, extra buttons
                if (down && Viewable) XSetInputFocus(display, window, RevertToParent, e->Time);
                if (down) buttons |= 1 << b;
                else if ((buttons & (1 << b)) == 0) break; // the press went elsewhere
                else buttons &= ~(1 << b);
                Send(down ? WindowInputEventType.MouseDown : WindowInputEventType.MouseUp, e->State, e->X, e->Y, (MouseButton)b);
                break;
            }

            case LeaveNotify:
                if (e->CrossingMode == NotifyNormal) Send(WindowInputEventType.MouseLeave, e->CrossingState);
                // Another client grabbed the pointer, ending our implicit grab: the button releases won't come here,
                // so release the held buttons now.
                if (e->CrossingMode == NotifyGrab && buttons != 0)
                {
                    for (int b = 0; b < 3; b++)
                        if ((buttons & (1 << b)) != 0) Send(WindowInputEventType.MouseUp, e->CrossingState, e->X, e->Y, (MouseButton)b);
                    buttons = 0;
                }
                break;

            case KeyPress:
            case KeyRelease:
            {
                // Auto-repeat arrives as release + press with the same time: report only the presses.
                if (e->Type == KeyRelease && XPending(display) > 0)
                {
                    XEvent next;
                    XPeekEvent(display, &next);
                    if (next.Type == KeyPress && next.Time == e->Time && next.KeycodeOrButton == e->KeycodeOrButton) break;
                }
                bool down = e->Type == KeyPress;
                int vk = VirtualKey(XLookupKeysym(e, 0));
                if (vk != 0) Send(down ? WindowInputEventType.KeyDown : WindowInputEventType.KeyUp, e->State, key: vk);
                if (down && (e->State & (ControlMask | Mod1Mask | Mod4Mask)) == 0)
                {
                    byte* text = stackalloc byte[8];
                    nuint typed = 0;
                    XLookupString(e, text, 8, &typed, null);
                    int codePoint = CodePoint(typed);
                    if (codePoint != 0) Send(WindowInputEventType.Text, e->State, character: codePoint);
                }
                break;
            }

            case FocusIn:
                if (!isChild && focusChild != null && focusChild.window != 0 && focusChild.Viewable)
                    XSetInputFocus(display, focusChild.window, RevertToParent, 0); // a top-level window passes focus on
                else
                    Send(WindowInputEventType.FocusGained, 0);
                break;
            case FocusOut:
                Send(WindowInputEventType.FocusLost, 0);
                break;

            case ConfigureNotify:
                if (e->ConfigureWidth != lastWidth || e->ConfigureHeight != lastHeight)
                {
                    lastWidth = e->ConfigureWidth;
                    lastHeight = e->ConfigureHeight;
                    RaiseResized(lastWidth, lastHeight);
                }
                break;

            case ClientMessage:
                if (e->MessageType == wmProtocols && (nuint)e->Data0 == wmDeleteWindow && RaiseCloseRequested())
                {
                    XDestroyWindow(display, window);
                    XFlush(display);
                }
                break;

            case DestroyNotify:
                windows.Remove(window);
                window = 0;
                RaiseClosed();
                break;
        }
    }

    // ---- input -------------------------------------------------------------------------------------------------

    void Send(WindowInputEventType type, uint state, int x = 0, int y = 0, MouseButton button = MouseButton.Left,
        int wheel = 0, int key = 0, int character = 0)
    {
        WindowInputEvent e = new()
        {
            Type = type,
            Modifiers = Modifiers(state),
            X = x,
            Y = y,
            Button = button,
            WheelDelta = wheel,
            Key = key,
            Character = character,
        };
        RaiseInput(in e);
    }

    static KeyModifiers Modifiers(uint state)
    {
        KeyModifiers m = KeyModifiers.None;
        if ((state & ShiftMask) != 0) m |= KeyModifiers.Shift;
        if ((state & ControlMask) != 0) m |= KeyModifiers.Control;
        if ((state & Mod1Mask) != 0) m |= KeyModifiers.Alt;
        if ((state & Mod4Mask) != 0) m |= KeyModifiers.Meta;
        return m;
    }

    // Keysym -> Windows virtual-key code (MonoGame Keys), 0 for keys without one.
    static int VirtualKey(nuint keysym)
    {
        ulong ks = keysym;
        if (ks >= 'a' && ks <= 'z') return (int)('A' + (ks - 'a'));
        if (ks >= 'A' && ks <= 'Z') return (int)ks;
        if (ks >= '0' && ks <= '9') return (int)ks;
        if (ks >= 0xFFBE && ks <= 0xFFD5) return (int)(0x70 + (ks - 0xFFBE)); // F1..F24
        if (ks >= 0xFFB0 && ks <= 0xFFB9) return (int)(0x60 + (ks - 0xFFB0)); // keypad 0..9
        return ks switch
        {
            0xFF08 => 0x08,                         // BackSpace
            0xFF09 or 0xFE20 => 0x09,               // Tab, ISO_Left_Tab
            0xFF0D or 0xFF8D => 0x0D,               // Return, KP_Enter
            0xFF13 => 0x13,                         // Pause
            0xFFE5 => 0x14,                         // Caps_Lock
            0xFF1B => 0x1B,                         // Escape
            0x20 => 0x20,                           // space
            0xFF55 => 0x21,                         // Prior (Page Up)
            0xFF56 => 0x22,                         // Next (Page Down)
            0xFF57 => 0x23,                         // End
            0xFF50 => 0x24,                         // Home
            0xFF51 => 0x25,                         // Left
            0xFF52 => 0x26,                         // Up
            0xFF53 => 0x27,                         // Right
            0xFF54 => 0x28,                         // Down
            0xFF63 => 0x2D,                         // Insert
            0xFFFF => 0x2E,                         // Delete
            0xFFEB => 0x5B,                         // Super_L
            0xFFEC => 0x5C,                         // Super_R
            0xFF67 => 0x5D,                         // Menu
            0xFFAA => 0x6A,                         // KP_Multiply
            0xFFAB => 0x6B,                         // KP_Add
            0xFFAD => 0x6D,                         // KP_Subtract
            0xFFAE => 0x6E,                         // KP_Decimal
            0xFFAF => 0x6F,                         // KP_Divide
            0xFF7F => 0x90,                         // Num_Lock
            0xFF14 => 0x91,                         // Scroll_Lock
            0xFFE1 => 0xA0,                         // Shift_L
            0xFFE2 => 0xA1,                         // Shift_R
            0xFFE3 => 0xA2,                         // Control_L
            0xFFE4 => 0xA3,                         // Control_R
            0xFFE9 or 0xFFE7 => 0xA4,               // Alt_L, Meta_L
            0xFFEA or 0xFFE8 or 0xFE03 => 0xA5,     // Alt_R, Meta_R, ISO_Level3_Shift (AltGr)
            0x3B => 0xBA,
            0x3D => 0xBB,
            0x2C => 0xBC,
            0x2D => 0xBD,
            0x2E => 0xBE,
            0x2F => 0xBF,
            0x60 => 0xC0,
            0x5B => 0xDB,
            0x5C => 0xDC,
            0x5D => 0xDD,
            0x27 => 0xDE,
            _ => 0,
        };
    }

    // The Unicode code point a keysym types, or 0.
    static int CodePoint(nuint keysym)
    {
        ulong ks = keysym;
        if ((ks >= 0x20 && ks <= 0x7E) || (ks >= 0xA0 && ks <= 0xFF)) return (int)ks; // Latin-1
        if ((ks & 0xFF000000) == 0x01000000) return (int)(ks & 0x00FFFFFF);           // Unicode keysyms
        if (ks >= 0xFFB0 && ks <= 0xFFB9) return (int)('0' + (ks - 0xFFB0));          // keypad digits
        return 0;
    }
}
