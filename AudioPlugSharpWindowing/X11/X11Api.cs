using System.Runtime.InteropServices;

namespace AudioPlugSharpWindowing;

/// <summary>The Xlib (libX11.so.6) and libc functions and structures the Linux implementation uses (64-bit layouts).</summary>
internal static unsafe class X11Api
{
    const string X11 = "libX11.so.6";
    const string LibC = "libc.so.6";

    // Event types
    public const int KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6,
        EnterNotify = 7, LeaveNotify = 8, FocusIn = 9, FocusOut = 10, Expose = 12, DestroyNotify = 17,
        ConfigureNotify = 22, ClientMessage = 33;

    // Event masks
    public const long KeyPressMask = 1L << 0, KeyReleaseMask = 1L << 1, ButtonPressMask = 1L << 2,
        ButtonReleaseMask = 1L << 3, EnterWindowMask = 1L << 4, LeaveWindowMask = 1L << 5, PointerMotionMask = 1L << 6,
        ExposureMask = 1L << 15, StructureNotifyMask = 1L << 17, SubstructureNotifyMask = 1L << 19,
        SubstructureRedirectMask = 1L << 20, FocusChangeMask = 1L << 21;

    // Window attribute flags
    public const ulong CWBackPixmap = 1 << 0, CWBackPixel = 1 << 1, CWOverrideRedirect = 1 << 9, CWEventMask = 1 << 11;

    // Key and button state
    public const uint ShiftMask = 1 << 0, ControlMask = 1 << 2, Mod1Mask = 1 << 3, Mod4Mask = 1 << 6;
    public const uint Button1 = 1, Button2 = 2, Button3 = 3, Button4 = 4, Button5 = 5;

    public const int NotifyNormal = 0, NotifyGrab = 1;
    public const int IsViewable = 2;
    public const int RevertToPointerRoot = 1, RevertToParent = 2;
    public const int InputOutput = 1;
    public const int PropModeReplace = 0;
    public const long PMinSize = 1 << 4, PMaxSize = 1 << 5;

    /// <summary>XEvent: a union of 24 longs. Fields are read at their offsets in the event structure of each type.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    public struct XEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(32)] public nuint Window;      // xany.window; xconfigure.event; xdestroywindow.event

        // XKeyEvent, XButtonEvent, XMotionEvent, XCrossingEvent
        [FieldOffset(56)] public nuint Time;
        [FieldOffset(64)] public int X;
        [FieldOffset(68)] public int Y;
        [FieldOffset(80)] public uint State;        // key, button, motion
        [FieldOffset(84)] public uint KeycodeOrButton;
        [FieldOffset(80)] public int CrossingMode;  // crossing
        [FieldOffset(96)] public uint CrossingState;

        // XConfigureEvent
        [FieldOffset(56)] public int ConfigureWidth;
        [FieldOffset(60)] public int ConfigureHeight;

        // XClientMessageEvent
        [FieldOffset(40)] public nuint MessageType;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public nint Data0;
        [FieldOffset(64)] public nint Data1;
        [FieldOffset(72)] public nint Data2;
        [FieldOffset(80)] public nint Data3;

        // send_event / display, set for XSendEvent
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public nint Display;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct XSetWindowAttributes
    {
        [FieldOffset(0)] public nuint BackgroundPixmap;
        [FieldOffset(8)] public nuint BackgroundPixel;
        [FieldOffset(72)] public long EventMask;
        [FieldOffset(88)] public int OverrideRedirect;
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    public struct XWindowAttributes
    {
        [FieldOffset(8)] public int Width;
        [FieldOffset(12)] public int Height;
        [FieldOffset(92)] public int MapState;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct XSizeHints
    {
        [FieldOffset(0)] public long Flags;
        [FieldOffset(24)] public int MinWidth;
        [FieldOffset(28)] public int MinHeight;
        [FieldOffset(32)] public int MaxWidth;
        [FieldOffset(36)] public int MaxHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XColor { public nuint Pixel; public ushort Red, Green, Blue; public byte Flags, Pad; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd { public int Fd; public short Events, Revents; }
    public const short POLLIN = 1;

    [DllImport(X11)] public static extern int XInitThreads();
    [DllImport(X11)] public static extern nint XOpenDisplay(byte* name);
    [DllImport(X11)] public static extern int XConnectionNumber(nint display);
    [DllImport(X11)] public static extern int XDefaultScreen(nint display);
    [DllImport(X11)] public static extern nuint XDefaultRootWindow(nint display);
    [DllImport(X11)] public static extern nuint XBlackPixel(nint display, int screen);
    [DllImport(X11)] public static extern int XDisplayWidth(nint display, int screen);
    [DllImport(X11)] public static extern int XDisplayHeight(nint display, int screen);
    [DllImport(X11)] public static extern nint XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);
    [DllImport(X11)] public static extern nuint XInternAtom(nint display, byte* name, int onlyIfExists);

    [DllImport(X11)]
    public static extern nuint XCreateWindow(nint display, nuint parent, int x, int y, uint width, uint height,
        uint borderWidth, int depth, uint windowClass, nint visual, ulong valueMask, XSetWindowAttributes* attributes);
    [DllImport(X11)] public static extern int XDestroyWindow(nint display, nuint window);
    [DllImport(X11)] public static extern int XMapWindow(nint display, nuint window);
    [DllImport(X11)] public static extern int XMapRaised(nint display, nuint window);
    [DllImport(X11)] public static extern int XUnmapWindow(nint display, nuint window);
    [DllImport(X11)] public static extern int XResizeWindow(nint display, nuint window, uint width, uint height);
    [DllImport(X11)] public static extern int XMoveWindow(nint display, nuint window, int x, int y);
    [DllImport(X11)] public static extern int XMoveResizeWindow(nint display, nuint window, int x, int y, uint width, uint height);
    [DllImport(X11)] public static extern int XReparentWindow(nint display, nuint window, nuint parent, int x, int y);
    [DllImport(X11)] public static extern int XChangeWindowAttributes(nint display, nuint window, ulong valueMask, XSetWindowAttributes* attributes);
    [DllImport(X11)] public static extern int XGetWindowAttributes(nint display, nuint window, XWindowAttributes* attributes);
    [DllImport(X11)]
    public static extern int XQueryTree(nint display, nuint window, nuint* root, nuint* parent, nuint** children, uint* count);
    [DllImport(X11)] public static extern int XFree(void* data);
    [DllImport(X11)] public static extern int XStoreName(nint display, nuint window, byte* name);
    [DllImport(X11)]
    public static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode,
        byte* data, int count);
    [DllImport(X11)] public static extern int XSetWMProtocols(nint display, nuint window, nuint* protocols, int count);
    [DllImport(X11)] public static extern void XSetWMNormalHints(nint display, nuint window, XSizeHints* hints);
    [DllImport(X11)] public static extern int XSetInputFocus(nint display, nuint window, int revertTo, nuint time);
    [DllImport(X11)] public static extern int XSendEvent(nint display, nuint window, int propagate, long eventMask, XEvent* e);

    [DllImport(X11)] public static extern nuint XCreateBitmapFromData(nint display, nuint drawable, byte* data, uint width, uint height);
    [DllImport(X11)]
    public static extern nuint XCreatePixmapCursor(nint display, nuint source, nuint mask, XColor* foreground,
        XColor* background, uint x, uint y);
    [DllImport(X11)] public static extern int XFreePixmap(nint display, nuint pixmap);
    [DllImport(X11)] public static extern int XFreeCursor(nint display, nuint cursor);
    [DllImport(X11)] public static extern int XDefineCursor(nint display, nuint window, nuint cursor);
    [DllImport(X11)] public static extern int XUndefineCursor(nint display, nuint window);

    [DllImport(X11)] public static extern int XPending(nint display);
    [DllImport(X11)] public static extern int XNextEvent(nint display, XEvent* e);
    [DllImport(X11)] public static extern int XPeekEvent(nint display, XEvent* e);
    [DllImport(X11)] public static extern int XFlush(nint display);
    [DllImport(X11)] public static extern int XSync(nint display, int discard);
    [DllImport(X11)] public static extern nuint XLookupKeysym(XEvent* keyEvent, int index);
    [DllImport(X11)] public static extern int XLookupString(XEvent* keyEvent, byte* buffer, int bytes, nuint* keysym, void* status);

    [DllImport(LibC, SetLastError = true)] public static extern int poll(PollFd* fds, nuint count, int timeout);
    [DllImport(LibC, SetLastError = true)] public static extern int pipe(int* fds);
    [DllImport(LibC, SetLastError = true)] public static extern nint read(int fd, void* buffer, nuint count);
    [DllImport(LibC, SetLastError = true)] public static extern nint write(int fd, void* buffer, nuint count);
}
