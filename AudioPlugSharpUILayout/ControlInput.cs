using System;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// The value controls' shared parts: the interface <see cref="ParameterControl"/> binds through, and input settings
/// (fine adjustment while Shift is held, the mouse wheel's step).
/// </summary>
public static class ControlInput
{
    /// <summary>How much slower a drag or the wheel moves the value while Shift is held.</summary>
    public static float FineScale { get; set; } = 0.1f;

    /// <summary>How far one notch of the mouse wheel moves the value (0.02 is 2% of the range).</summary>
    public static float WheelStep { get; set; } = 0.02f;

    /// <summary>
    /// Whether Shift is held (fine adjustment). The editor sees key events while it has keyboard focus.
    /// </summary>
    public static bool IsFine
    {
        get
        {
            InputManager inputManager = Layout.Current.InputManager;

            return inputManager.IsKeyDown(InputKey.LeftShift) || inputManager.IsKeyDown(InputKey.RightShift);
        }
    }

    /// <summary>The value after the wheel turned by the delta (120 per notch, positive away from the user).</summary>
    public static double WheelValue(double value, int delta)
    {
        float step = WheelStep * (IsFine ? FineScale : 1);

        return Math.Clamp(value + ((delta / 120.0) * step), 0, 1);
    }
}

/// <summary>
/// A control for a value from 0 to 1 (<see cref="Knob"/>, <see cref="ValueSlider"/>, <see cref="LampSwitch"/>),
/// knowing nothing of what the value means. The user's changes come as an edit: <see cref="EditStarted"/>, then
/// <see cref="ValueChanged"/> for each new value, then <see cref="EditEnded"/> (a drag is one edit, as is a click or a
/// wheel notch). Setting <see cref="Value"/> from code (for example to follow host automation) reports nothing.
/// </summary>
public interface IValueControl
{
    /// <summary>The value, from 0 to 1.</summary>
    double Value { get; set; }

    /// <summary>The value a double-click returns to.</summary>
    double DefaultValue { get; set; }

    /// <summary>Whether the user is changing the value (between EditStarted and EditEnded).</summary>
    bool IsEditing { get; }

    Action EditStarted { get; set; }
    Action ValueChanged { get; set; }
    Action EditEnded { get; set; }

    /// <summary>Call each frame: shows hover. Doesn't allocate.</summary>
    void Update();
}
