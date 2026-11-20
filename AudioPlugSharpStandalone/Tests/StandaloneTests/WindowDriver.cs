namespace StandaloneTests;

/// <summary>Finds, inspects and drives another process's windows, with synthetic input.</summary>
abstract class WindowDriver
{
    /// <summary>Waits (up to 10 s) for the top-level window with this title and the editor window inside it.</summary>
    public abstract bool Find(string title, out nint top, out nint editor);

    /// <summary>The window's client size in pixels, as "WxH".</summary>
    public abstract string Size(nint window);

    /// <summary>The display scale of the window (1 = 96 DPI).</summary>
    public abstract double Scale(nint window);

    /// <summary>The size of the screen (monitor) the window is on, as "WxH".</summary>
    public abstract string ScreenSize(nint window);

    public abstract void SetClientSize(nint top, int width, int height);

    /// <summary>Whether <see cref="ChangeScale"/> can simulate a move to a monitor with another scale.</summary>
    public virtual bool CanChangeScale => false;

    public virtual void ChangeScale(nint top, double scale) => throw new NotSupportedException();

    /// <summary>Presses and releases a key, given as a Windows virtual-key code.</summary>
    public abstract void Key(nint window, uint virtualKey);

    public abstract void MouseMove(nint window, int x, int y, bool buttonHeld);

    /// <summary>The left button.</summary>
    public abstract void MouseButton(nint window, int x, int y, bool down);

    /// <summary>Whether the window is a top-level window (not inside another).</summary>
    public abstract bool IsTopLevel(nint window);

    public abstract bool IsChildOf(nint window, nint parent);

    /// <summary>Asks the window to close, as its close button does.</summary>
    public abstract void Close(nint top);

    /// <summary>Saves what the window shows as a BMP file.</summary>
    public abstract void Capture(nint window, string path);

    /// <summary>Writes a 24-bit BMP from top-down rows of 0xRRGGBB pixels.</summary>
    protected static void WriteBmp(string path, int width, int height, Func<int, int, uint> pixel)
    {
        int stride = (width * 3 + 3) & ~3;
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((ushort)0x4D42); w.Write(54 + stride * height); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(-height); w.Write((ushort)1); w.Write((ushort)24); w.Write(0);
        w.Write(stride * height); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        var row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint p = pixel(x, y);
                row[x * 3] = (byte)p;
                row[x * 3 + 1] = (byte)(p >> 8);
                row[x * 3 + 2] = (byte)(p >> 16);
            }
            w.Write(row);
        }
    }
}
