using System.Runtime.InteropServices;
using System.Text;

namespace StandaloneTests;

/// <summary>X11: synthetic events sent to the windows (XSendEvent), on a connection of the test's own.</summary>
unsafe class X11Driver : WindowDriver
{
    const int KeyPressType = 2, KeyReleaseType = 3, ButtonPressType = 4, ButtonReleaseType = 5, MotionNotifyType = 6, ClientMessageType = 33;
    const long KeyPressMask = 1, KeyReleaseMask = 2, ButtonPressMask = 4, ButtonReleaseMask = 8, PointerMotionMask = 64;
    const uint Button1Mask = 1 << 8;
    const uint XK_F11 = 0xFFC8;

    readonly nint display;
    readonly nuint root;
    readonly int screen;

    public X11Driver()
    {
        display = XOpenDisplay(null);
        if (display == 0) throw new InvalidOperationException("Could not open the X display (is DISPLAY set?)");
        screen = XDefaultScreen(display);
        root = XDefaultRootWindow(display);
    }

    // By name: a window manager may have put the window into a frame of its own.
    protected override nint FindTopLevel(string title) => (nint)FindNamed(root, title);

    public override nint[] Children(nint window) => Children((nuint)window).Select(c => (nint)c).ToArray();

    public override (int X, int Y) Position(nint window)
    {
        byte* attributes = stackalloc byte[136]; // XWindowAttributes: x at 0, y at 4
        XGetWindowAttributes(display, (nuint)window, attributes);
        return (*(int*)attributes, *(int*)(attributes + 4));
    }

    nuint FindNamed(nuint window, string name)
    {
        byte* text = null;
        if (XFetchName(display, window, &text) != 0 && text != null)
        {
            string? windowName = Marshal.PtrToStringUTF8((nint)text);
            XFree(text);
            if (windowName == name) return window;
        }
        foreach (nuint child in Children(window))
        {
            nuint found = FindNamed(child, name);
            if (found != 0) return found;
        }
        return 0;
    }

    nuint[] Children(nuint window)
    {
        nuint rootWindow, parent;
        nuint* children = null;
        uint count;
        if (XQueryTree(display, window, &rootWindow, &parent, &children, &count) == 0) return [];
        var result = new nuint[count];
        for (int i = 0; i < count; i++) result[i] = children[i];
        if (children != null) XFree(children);
        return result;
    }

    nuint Parent(nint window)
    {
        nuint rootWindow, parent;
        nuint* children = null;
        uint count;
        XQueryTree(display, (nuint)window, &rootWindow, &parent, &children, &count);
        if (children != null) XFree(children);
        return parent;
    }

    (int Width, int Height) Dimensions(nint window)
    {
        byte* attributes = stackalloc byte[136]; // XWindowAttributes: width at 8, height at 12
        XGetWindowAttributes(display, (nuint)window, attributes);
        return (*(int*)(attributes + 8), *(int*)(attributes + 12));
    }

    public override string Size(nint window)
    {
        var (width, height) = Dimensions(window);
        return $"{width}x{height}";
    }

    public override double Scale(nint window) => 1; // as the host: X11 has no standard display scale

    public override string ScreenSize(nint window) => $"{XDisplayWidth(display, screen)}x{XDisplayHeight(display, screen)}";

    public override void SetClientSize(nint top, int width, int height)
    {
        XResizeWindow(display, (nuint)top, (uint)width, (uint)height);
        XFlush(display);
    }

    public override void Key(nint window, uint virtualKey)
    {
        uint keysym = virtualKey == 0x7A ? XK_F11 : throw new NotSupportedException($"key {virtualKey:X}");
        uint keycode = XKeysymToKeycode(display, keysym);
        Send(window, KeyPressType, KeyPressMask, 0, 0, 0, keycode);
        Thread.Sleep(50);
        Send(window, KeyReleaseType, KeyReleaseMask, 0, 0, 0, keycode);
        Thread.Sleep(50);
    }

    public override void MouseMove(nint window, int x, int y, bool buttonHeld)
    {
        Send(window, MotionNotifyType, PointerMotionMask, x, y, buttonHeld ? Button1Mask : 0, 0);
        Thread.Sleep(60);
    }

    public override void MouseButton(nint window, int x, int y, bool down)
    {
        Send(window, down ? ButtonPressType : ButtonReleaseType, down ? ButtonPressMask : ButtonReleaseMask, x, y,
            down ? 0 : Button1Mask, 1);
        Thread.Sleep(100);
    }

    // A key, button or motion event (they share the layout up to here).
    void Send(nint window, int type, long mask, int x, int y, uint state, uint keycodeOrButton)
    {
        byte* e = stackalloc byte[192];
        new Span<byte>(e, 192).Clear();
        *(int*)e = type;
        *(int*)(e + 16) = 1;                    // send_event
        *(nint*)(e + 24) = display;
        *(nuint*)(e + 32) = (nuint)window;
        *(nuint*)(e + 40) = root;
        *(nuint*)(e + 56) = (nuint)Environment.TickCount64; // time
        *(int*)(e + 64) = x;
        *(int*)(e + 68) = y;
        *(uint*)(e + 80) = state;
        *(uint*)(e + 84) = keycodeOrButton;     // keycode, button (motion: is_hint, 0)
        *(int*)(e + 88) = 1;                    // same_screen
        XSendEvent(display, (nuint)window, 1, mask, e);
        XFlush(display);
    }

    public override bool IsTopLevel(nint window) => Parent(window) == root;

    public override bool IsChildOf(nint window, nint parent) => Parent(window) == (nuint)parent;

    // WM_DELETE_WINDOW, as the window manager sends it for the close button.
    public override void Close(nint top)
    {
        byte* e = stackalloc byte[192];
        new Span<byte>(e, 192).Clear();
        *(int*)e = ClientMessageType;
        *(nuint*)(e + 32) = (nuint)top;
        *(nuint*)(e + 40) = Atom("WM_PROTOCOLS");
        *(int*)(e + 48) = 32;
        *(nuint*)(e + 56) = Atom("WM_DELETE_WINDOW");
        XSendEvent(display, (nuint)top, 0, 0, e);
        XFlush(display);
    }

    nuint Atom(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return XInternAtom(display, p, 0);
    }

    public override void Capture(nint window, string path)
    {
        var (width, height) = Dimensions(window);
        if (width <= 0 || height <= 0) return;
        nint image = XGetImage(display, (nuint)window, 0, 0, (uint)width, (uint)height, ulong.MaxValue, 2 /* ZPixmap */);
        if (image == 0) return;
        WriteBmp(path, width, height, (x, y) => (uint)XGetPixel(image, x, y) & 0xFFFFFF);
    }

    const string X11 = "libX11.so.6";
    [DllImport(X11)] static extern nint XOpenDisplay(byte* name);
    [DllImport(X11)] static extern int XDefaultScreen(nint display);
    [DllImport(X11)] static extern nuint XDefaultRootWindow(nint display);
    [DllImport(X11)] static extern int XDisplayWidth(nint display, int screen);
    [DllImport(X11)] static extern int XDisplayHeight(nint display, int screen);
    [DllImport(X11)] static extern int XFetchName(nint display, nuint window, byte** name);
    [DllImport(X11)] static extern int XFree(void* data);
    [DllImport(X11)] static extern int XQueryTree(nint display, nuint window, nuint* root, nuint* parent, nuint** children, uint* count);
    [DllImport(X11)] static extern int XGetWindowAttributes(nint display, nuint window, byte* attributes);
    [DllImport(X11)] static extern uint XKeysymToKeycode(nint display, nuint keysym);
    [DllImport(X11)] static extern int XSendEvent(nint display, nuint window, int propagate, long mask, byte* e);
    [DllImport(X11)] static extern int XFlush(nint display);
    [DllImport(X11)] static extern nuint XInternAtom(nint display, byte* name, int onlyIfExists);
    [DllImport(X11)] static extern int XResizeWindow(nint display, nuint window, uint width, uint height);
    [DllImport(X11)] static extern nint XGetImage(nint display, nuint window, int x, int y, uint width, uint height, ulong planes, int format);
    [DllImport(X11)] static extern ulong XGetPixel(nint image, int x, int y);
}
