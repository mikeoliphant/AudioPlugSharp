namespace AudioPlugSharpWindowing;

/// <summary>The kind of native handle a <see cref="NativeWindow"/> has.</summary>
public enum NativeWindowType
{
    /// <summary>Windows: an HWND.</summary>
    Win32 = 1,
    /// <summary>Linux: an X11 Window id.</summary>
    X11 = 2,
}

/// <summary>
/// A native window: a top-level window (<see cref="CreateTopLevel"/>) or a child window inside another
/// (<see cref="CreateChild"/>), for example one that a graphics library draws into through its handle.
///
/// Sizes and positions are window pixels; <see cref="Scale"/> is the window's display scale (1 = 96 DPI). Windows
/// belong to the thread that creates them, which runs <see cref="WindowLoop.Run"/>; their members and events are
/// called on that thread.
/// </summary>
public abstract class NativeWindow : IDisposable
{
    /// <summary>
    /// Creates a top-level window with a client area of the given size, hidden until <see cref="Show"/>. A window
    /// that isn't <paramref name="resizable"/> can't be resized by the user (its size can still be set).
    /// </summary>
    public static NativeWindow CreateTopLevel(string title, int width, int height, bool resizable)
    {
        if (OperatingSystem.IsWindows()) return Win32Window.Create(title, width, height, resizable);
        if (OperatingSystem.IsLinux()) return X11Window.Create(title, width, height, resizable);
        throw new PlatformNotSupportedException("AudioPlugSharpWindowing supports Windows and Linux (X11)");
    }

    /// <summary>The platform's kind of window, or null where windows aren't supported.</summary>
    public static NativeWindowType? PlatformWindowType =>
        OperatingSystem.IsWindows() ? NativeWindowType.Win32 : OperatingSystem.IsLinux() ? NativeWindowType.X11 : null;

    /// <summary>Creates a visible child window at the given position in this window's client area.</summary>
    public abstract NativeWindow CreateChild(int x, int y, int width, int height);

    public abstract IntPtr Handle { get; }

    public abstract NativeWindowType WindowType { get; }

    /// <summary>The display scale of the window's monitor (1 = 96 DPI).</summary>
    public abstract double Scale { get; }

    public abstract int ClientWidth { get; }

    public abstract int ClientHeight { get; }

    /// <summary>Resizes the client area. A fullscreen window takes the size when it leaves fullscreen.</summary>
    public abstract void SetClientSize(int width, int height);

    /// <summary>Whether the user can resize a top-level window (see <see cref="CreateTopLevel"/>), keeping its client
    /// size. A maximized window that stops being resizable is restored first. Child windows ignore it.</summary>
    public abstract void SetResizable(bool resizable);

    /// <summary>Moves the window: within its parent's client area for a child window, on the screen otherwise.</summary>
    public abstract void SetPosition(int x, int y);

    public abstract void SetTitle(string title);

    public abstract void Show();

    public abstract void Hide();

    /// <summary>Gives the window keyboard focus.</summary>
    public abstract void Focus();

    /// <summary>Shows or hides the mouse cursor while it is over the window. Returns false if not supported.</summary>
    public abstract bool SetCursorVisible(bool visible);

    /// <summary>
    /// Covers the window's monitor with the window (true), or puts it back (false). A child window leaves its parent
    /// while fullscreen and returns to it, at its previous size. Returns whether the window is fullscreen now.
    /// </summary>
    public abstract bool SetFullscreen(bool fullscreen);

    public abstract bool IsFullscreen { get; }

    /// <summary>
    /// Call after another library took over the window's messages (for example SDL, when it wraps the window for
    /// OpenGL), so that this window still controls its cursor and close requests.
    /// </summary>
    public abstract void KeepControl();

    /// <summary>Mouse, keyboard and focus input.</summary>
    public event WindowInputHandler? Input;

    /// <summary>The client area was resized (pixels).</summary>
    public event Action<int, int>? Resized;

    /// <summary>The window moved to a monitor with another display scale. A top-level window keeps its size in
    /// pixels; resize it if its content should keep its size.</summary>
    public event Action<double>? ScaleChanged;

    /// <summary>The user asked to close the window. Return false to keep it open. Without a handler it closes.</summary>
    public event Func<bool>? CloseRequested;

    /// <summary>The window was closed (destroyed).</summary>
    public event Action? Closed;

    protected bool RaiseInput(in WindowInputEvent inputEvent) => Input?.Invoke(in inputEvent) ?? false;

    protected void RaiseResized(int width, int height) => Resized?.Invoke(width, height);

    protected void RaiseScaleChanged(double scale) => ScaleChanged?.Invoke(scale);

    protected bool RaiseCloseRequested() => CloseRequested?.Invoke() ?? true;

    protected void RaiseClosed() => Closed?.Invoke();

    public abstract void Dispose();
}
