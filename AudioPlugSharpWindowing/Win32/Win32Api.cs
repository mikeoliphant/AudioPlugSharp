using System.Runtime.InteropServices;

namespace AudioPlugSharpWindowing;

/// <summary>The Win32 functions and structures the Windows implementation uses (user32, gdi32, winmm, kernel32).</summary>
internal static unsafe class Win32Api
{
    public const uint WM_NULL = 0x0000, WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_SETFOCUS = 0x0007,
        WM_KILLFOCUS = 0x0008, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_QUIT = 0x0012, WM_ERASEBKGND = 0x0014,
        WM_SETCURSOR = 0x0020, WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082, WM_GETDLGCODE = 0x0087,
        WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105,
        WM_SYSCHAR = 0x0106, WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209, WM_MOUSEWHEEL = 0x020A,
        WM_CAPTURECHANGED = 0x0215, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_MOUSELEAVE = 0x02A3,
        WM_DPICHANGED = 0x02E0, WM_DPICHANGED_AFTERPARENT = 0x02E3;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_THICKFRAME = 0x00040000, WS_MAXIMIZEBOX = 0x00010000,
        WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000,
        WS_CLIPSIBLINGS = 0x04000000;

    public const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002, CS_DBLCLKS = 0x0008, CS_OWNDC = 0x0020;

    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
        SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNA = 8, SW_RESTORE = 9;
    public const int GWL_STYLE = -16, GWLP_WNDPROC = -4;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const int SIZE_MINIMIZED = 1;
    public const int HTCLIENT = 1;
    public const uint DLGC_WANTALLKEYS = 0x0004, DLGC_WANTCHARS = 0x0080;
    public const uint TME_LEAVE = 0x0002;
    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint GA_ROOT = 2;
    public const uint MAPVK_VSC_TO_VK_EX = 3;
    public const int BLACK_BRUSH = 4;
    public const int IDC_ARROW = 32512;
    public static readonly nint HWND_TOP = 0;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public POINT Pt;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint Size, Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATESTRUCTW { public nint CreateParams; } // only the first field is read

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint Size, Flags; public nint Hwnd; public uint HoverTime; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint Size; public RECT Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint Hdc;
        public int Erase;
        public RECT Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    private const string User32 = "user32.dll";

    [DllImport(User32, SetLastError = true)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(User32, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport(User32)] public static extern int DestroyWindow(nint hwnd);
    [DllImport(User32)] public static extern int IsWindow(nint hwnd);
    [DllImport(User32)] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport(User32)] public static extern nint CallWindowProcW(nint proc, nint hwnd, uint msg, nint wp, nint lp);
    [DllImport(User32)] public static extern int PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport(User32)] public static extern int TranslateMessage(MSG* msg);
    [DllImport(User32)] public static extern nint DispatchMessageW(MSG* msg);
    [DllImport(User32)] public static extern int PostThreadMessageW(uint threadId, uint msg, nint wp, nint lp);
    [DllImport(User32)]
    public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport(User32)]
    public static extern int SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport(User32)] public static extern int ShowWindow(nint hwnd, int cmd);
    [DllImport(User32)] public static extern int IsZoomed(nint hwnd);
    [DllImport(User32)] public static extern int SetWindowTextW(nint hwnd, char* text);
    [DllImport(User32)] public static extern int GetClientRect(nint hwnd, RECT* rect);
    [DllImport(User32)] public static extern int AdjustWindowRectExForDpi(RECT* rect, uint style, int menu, uint exStyle, uint dpi);
    [DllImport(User32)] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport(User32)] public static extern uint GetDpiForSystem();
    [DllImport(User32)] public static extern int SetProcessDpiAwarenessContext(nint context);
    [DllImport(User32)] public static extern nint SetFocus(nint hwnd);
    [DllImport(User32)] public static extern nint GetFocus();
    [DllImport(User32)] public static extern nint SetCapture(nint hwnd);
    [DllImport(User32)] public static extern int ReleaseCapture();
    [DllImport(User32)] public static extern nint GetCapture();
    [DllImport(User32)] public static extern int TrackMouseEvent(TRACKMOUSEEVENT* tme);
    [DllImport(User32)] public static extern int ScreenToClient(nint hwnd, POINT* point);
    [DllImport(User32)] public static extern int GetCursorPos(POINT* point);
    [DllImport(User32)] public static extern nint WindowFromPoint(POINT point);
    [DllImport(User32)] public static extern nint SetCursor(nint cursor);
    [DllImport(User32)] public static extern nint LoadCursorW(nint instance, nint name);
    [DllImport(User32)] public static extern short GetKeyState(int virtualKey);
    [DllImport(User32)] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport(User32)] public static extern int SetPropW(nint hwnd, char* name, nint data);
    [DllImport(User32)] public static extern nint GetPropW(nint hwnd, char* name);
    [DllImport(User32)] public static extern nint RemovePropW(nint hwnd, char* name);
    [DllImport(User32)] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport(User32)] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport(User32)] public static extern nint SetParent(nint child, nint parent);
    [DllImport(User32)] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport(User32)] public static extern int GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [DllImport(User32)] public static extern int SetForegroundWindow(nint hwnd);
    [DllImport(User32)] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport(User32)] public static extern nuint SetTimer(nint hwnd, nuint id, uint elapse, nint timerProc);
    [DllImport(User32)] public static extern int KillTimer(nint hwnd, nuint id);
    [DllImport(User32)] public static extern nint SendMessageW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport(User32)] public static extern nint BeginPaint(nint hwnd, PAINTSTRUCT* paint);
    [DllImport(User32)] public static extern int EndPaint(nint hwnd, PAINTSTRUCT* paint);
    [DllImport(User32)] public static extern int GetWindowRect(nint hwnd, RECT* rect);

    [DllImport("gdi32.dll")] public static extern nint GetStockObject(int index);
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);

    public static int LowWord(nint value) => (int)(value & 0xFFFF);
    public static int HighWord(nint value) => (int)((value >> 16) & 0xFFFF);
    public static int SignedLowWord(nint value) => (short)(value & 0xFFFF);
    public static int SignedHighWord(nint value) => (short)((value >> 16) & 0xFFFF);
}
