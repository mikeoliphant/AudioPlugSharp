using System.Runtime.InteropServices;

namespace AudioPlugSharpWindowing;

public enum WindowInputEventType : int
{
    None = 0,
    /// <summary>The pointer moved (X, Y).</summary>
    MouseMove = 1,
    /// <summary>A mouse button was pressed (Button, X, Y).</summary>
    MouseDown = 2,
    /// <summary>A mouse button was released (Button, X, Y).</summary>
    MouseUp = 3,
    /// <summary>The wheel turned (WheelDelta, X, Y).</summary>
    MouseWheel = 4,
    /// <summary>The pointer left the window.</summary>
    MouseLeave = 5,
    /// <summary>A key went down (Key). Auto-repeat sends further KeyDown events.</summary>
    KeyDown = 6,
    /// <summary>A key went up (Key).</summary>
    KeyUp = 7,
    /// <summary>Text was typed (Character), after keyboard layout and dead-key processing.</summary>
    Text = 8,
    /// <summary>The window got keyboard focus.</summary>
    FocusGained = 9,
    /// <summary>The window lost keyboard focus.</summary>
    FocusLost = 10,
}

public enum MouseButton : int
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

[Flags]
public enum KeyModifiers : int
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    /// <summary>Windows key / Super / Command.</summary>
    Meta = 8,
}

/// <summary>
/// An input event for a window. Blittable, so it can be queued without allocating. Positions are window pixels from
/// the top left of the window's client area.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WindowInputEvent
{
    public WindowInputEventType Type;
    public KeyModifiers Modifiers;
    public int X;
    public int Y;
    /// <summary>MouseDown/MouseUp.</summary>
    public MouseButton Button;
    /// <summary>MouseWheel: 120 per notch, positive away from the user.</summary>
    public int WheelDelta;
    /// <summary>
    /// KeyDown/KeyUp: a Windows virtual-key code (VK_*) on every platform; other platforms translate to it. Shift,
    /// Control and Alt use the left/right-specific codes (VK_LSHIFT, VK_RCONTROL, ...), never the generic ones.
    /// </summary>
    public int Key;
    /// <summary>Text: a Unicode code point (UTF-32).</summary>
    public int Character;
}

/// <summary>Handles an input event; returns whether it was used.</summary>
public delegate bool WindowInputHandler(in WindowInputEvent inputEvent);
