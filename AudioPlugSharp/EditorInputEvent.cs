using System;
using System.Runtime.InteropServices;

namespace AudioPlugSharp
{
    public enum EditorInputEventType : int
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
        /// <summary>The editor window got keyboard focus.</summary>
        FocusGained = 9,
        /// <summary>The editor window lost keyboard focus.</summary>
        FocusLost = 10,
    }

    public enum EditorMouseButton : int
    {
        Left = 0,
        Right = 1,
        Middle = 2,
    }

    [Flags]
    public enum EditorModifiers : int
    {
        None = 0,
        Shift = 1,
        Control = 2,
        Alt = 4,
        /// <summary>Windows key / Super / Command.</summary>
        Meta = 8,
    }

    /// <summary>
    /// An input event for an embedded editor (<see cref="IAudioPluginEditor.EditorInput"/>). Blittable,
    /// so adapters can pass it across the native boundary and editors can queue it without allocating.
    /// Positions are window pixels from the top left of the editor window.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EditorInputEvent
    {
        public EditorInputEventType Type;
        public EditorModifiers Modifiers;
        public int X;
        public int Y;
        /// <summary>MouseDown/MouseUp.</summary>
        public EditorMouseButton Button;
        /// <summary>MouseWheel: 120 per notch, positive away from the user (as WM_MOUSEWHEEL).</summary>
        public int WheelDelta;
        /// <summary>
        /// KeyDown/KeyUp: a Windows virtual-key code (VK_*) on every platform, which is also the value of
        /// MonoGame's <c>Keys</c>. Adapters on other platforms translate to it. Shift, Control and Alt use the
        /// left/right-specific codes (VK_LSHIFT, VK_RCONTROL, ...), never the generic VK_SHIFT/VK_CONTROL/VK_MENU.
        /// </summary>
        public int Key;
        /// <summary>Text: a Unicode code point (UTF-32; adapters combine UTF-16 surrogate pairs).</summary>
        public int Character;
    }
}
