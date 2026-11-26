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

    /// <summary>
    /// The value after the wheel turned by the delta (120 per notch, positive away from the user). With steps, a
    /// notch is one step.
    /// </summary>
    public static double WheelValue(double value, int delta, int stepCount = 0)
    {
        if (stepCount > 0)
        {
            int notches = (delta > 0) ? Math.Max(1, delta / 120) : Math.Min(-1, delta / 120);

            return Snap(value + ((double)notches / stepCount), stepCount);
        }

        float step = WheelStep * (IsFine ? FineScale : 1);

        return Math.Clamp(value + ((delta / 120.0) * step), 0, 1);
    }

    /// <summary>The value at its nearest step (0, 1 / stepCount, ... 1), or just kept in 0-1 without steps.</summary>
    public static double Snap(double value, int stepCount)
    {
        value = Math.Clamp(value, 0, 1);

        if (stepCount <= 0)
            return value;

        return Math.Round(value * stepCount) / stepCount;
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

    /// <summary>
    /// Steps the user's changes snap to (0, the default: none; N: 0, 1 / N, ... 1), with a wheel notch moving one
    /// step. A switch has its two values whatever this says.
    /// </summary>
    int StepCount { get; set; }

    /// <summary>Whether the user is changing the value (between EditStarted and EditEnded).</summary>
    bool IsEditing { get; }

    Action EditStarted { get; set; }
    Action ValueChanged { get; set; }
    Action EditEnded { get; set; }

    /// <summary>Call each frame: shows hover. Doesn't allocate.</summary>
    void Update();
}
