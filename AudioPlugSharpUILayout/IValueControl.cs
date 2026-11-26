using System;
using UILayout;

namespace AudioPlugSharpUILayout;

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
