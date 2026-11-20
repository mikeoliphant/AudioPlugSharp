using System.Runtime.InteropServices;

namespace StandaloneTests;

/// <summary>Windows: messages posted to the windows, as the input system would send them.</summary>
unsafe class WindowsDriver : WindowDriver
{
    const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_DPICHANGED = 0x02E0;
    const int GWL_STYLE = -16;

    public WindowsDriver()
    {
        // Real pixels, as the host (per-monitor DPI aware) sees them.
        SetProcessDpiAwarenessContext(-4);
    }

    public override bool Find(string title, out nint top, out nint editor)
    {
        for (int i = 0; i < 100; i++)
        {
            top = FindWindowW("AudioPlugSharpWindowing.TopLevel", title);
            editor = top != 0 ? FindWindowExW(top, 0, "AudioPlugSharpWindowing.Child", null) : 0;
            if (editor != 0) return true;
            Thread.Sleep(100);
        }
        top = editor = 0;
        return false;
    }

    public override string Size(nint window)
    {
        RECT r;
        GetClientRect(window, &r);
        return $"{r.Right}x{r.Bottom}";
    }

    public override double Scale(nint window) => GetDpiForWindow(window) / 96.0;

    public override string ScreenSize(nint window)
    {
        MONITORINFO info = new() { Size = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(MonitorFromWindow(window, 2), &info);
        return $"{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}";
    }

    public override void SetClientSize(nint top, int width, int height)
    {
        RECT frame = new() { Right = width, Bottom = height };
        AdjustWindowRectExForDpi(&frame, (uint)GetWindowLongPtrW(top, GWL_STYLE), 0, 0, GetDpiForWindow(top));
        SetWindowPos(top, 0, 0, 0, frame.Right - frame.Left, frame.Bottom - frame.Top, 0x0002 | 0x0004 | 0x0010); // NOMOVE|NOZORDER|NOACTIVATE
    }

    public override bool CanChangeScale => true;

    // As Windows tells a per-monitor aware window that moved to a monitor with another DPI.
    public override void ChangeScale(nint top, double scale)
    {
        int dpi = (int)Math.Round(scale * 96);
        RECT r;
        GetWindowRect(top, &r);
        SendMessageW(top, WM_DPICHANGED, dpi | (dpi << 16), (nint)(&r));
    }

    public override void Key(nint window, uint virtualKey)
    {
        PostMessageW(window, WM_KEYDOWN, (nint)virtualKey, 1);
        Thread.Sleep(50);
        PostMessageW(window, WM_KEYUP, (nint)virtualKey, unchecked((nint)0xC0000001));
        Thread.Sleep(50);
    }

    public override void MouseMove(nint window, int x, int y, bool buttonHeld)
    {
        PostMessageW(window, WM_MOUSEMOVE, buttonHeld ? 1 : 0, Point(x, y));
        Thread.Sleep(60);
    }

    public override void MouseButton(nint window, int x, int y, bool down)
    {
        PostMessageW(window, down ? WM_LBUTTONDOWN : WM_LBUTTONUP, down ? 1 : 0, Point(x, y));
        Thread.Sleep(100);
    }

    static nint Point(int x, int y) => (y << 16) | (x & 0xFFFF);

    public override bool IsTopLevel(nint window) => GetParent(window) == 0;

    public override bool IsChildOf(nint window, nint parent) => GetParent(window) == parent;

    public override void Close(nint top) => PostMessageW(top, WM_CLOSE, 0, 0);

    // PrintWindow with PW_RENDERFULLCONTENT also captures OpenGL content, whatever covers the window.
    public override void Capture(nint window, string path)
    {
        RECT r;
        GetClientRect(window, &r);
        int width = r.Right, height = r.Bottom;
        if (width <= 0 || height <= 0) return;

        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint old = SelectObject(dc, bitmap);
        PrintWindow(window, dc, 1 | 2); // PW_CLIENTONLY | PW_RENDERFULLCONTENT

        var pixels = new uint[width * height];
        BITMAPINFOHEADER header = new() { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        fixed (uint* p = pixels) GetDIBits(dc, bitmap, 0, (uint)height, p, &header, 0);

        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(0, screen);

        WriteBmp(path, width, height, (x, y) => pixels[y * width + x] & 0xFFFFFF);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public uint Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    const string User32 = "user32.dll", Gdi32 = "gdi32.dll";
    [DllImport(User32)] static extern int SetProcessDpiAwarenessContext(nint context);
    [DllImport(User32, CharSet = CharSet.Unicode)] static extern nint FindWindowW(string className, string title);
    [DllImport(User32, CharSet = CharSet.Unicode)] static extern nint FindWindowExW(nint parent, nint after, string className, string? title);
    [DllImport(User32)] static extern int GetClientRect(nint hwnd, RECT* rect);
    [DllImport(User32)] static extern int GetWindowRect(nint hwnd, RECT* rect);
    [DllImport(User32)] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport(User32)] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport(User32)] static extern int GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [DllImport(User32)] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport(User32)] static extern int AdjustWindowRectExForDpi(RECT* rect, uint style, int menu, uint exStyle, uint dpi);
    [DllImport(User32)] static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport(User32)] static extern nint SendMessageW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport(User32)] static extern int PostMessageW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport(User32)] static extern nint GetParent(nint hwnd);
    [DllImport(User32)] static extern nint GetDC(nint hwnd);
    [DllImport(User32)] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport(User32)] static extern int PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport(Gdi32)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport(Gdi32)] static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport(Gdi32)] static extern nint SelectObject(nint dc, nint obj);
    [DllImport(Gdi32)] static extern int DeleteObject(nint obj);
    [DllImport(Gdi32)] static extern int DeleteDC(nint dc);
    [DllImport(Gdi32)] static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BITMAPINFOHEADER* info, uint usage);
}
