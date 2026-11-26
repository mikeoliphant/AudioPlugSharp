using System;
using System.Collections.Generic;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A drop-down for a value from 0 to 1 that is one of a list of named choices (<see cref="IValueControl"/>;
/// <see cref="ParameterControl"/> binds one to a parameter with names, such as a ChoiceParameter): choice i of N is
/// the value i / (N - 1). Choosing an item is one edit.
/// </summary>
public class ChoiceDropDown : DropDown<int>, IValueControl
{
    public ChoiceDropDown(IReadOnlyList<string> names)
    {
        (string, int)[] items = new (string, int)[names.Count];

        for (int i = 0; i < items.Length; i++)
            items[i] = (names[i], i);

        SetItems(items);

        // The small font, as parameter names have, so choices fit a knob-sized cell
        Font = Layout.Current.Theme.SmallFont ?? Layout.Current.Theme.Font;

        StepCount = Math.Max(names.Count - 1, 1);

        SelectAction = delegate
        {
            if (EditStarted != null)
                EditStarted();

            if (ValueChanged != null)
                ValueChanged();

            if (EditEnded != null)
                EditEnded();
        };
    }

    public new double Value
    {
        get { return (double)SelectedIndex / StepCount; }

        set { SelectIndex((int)Math.Round(Math.Clamp(value, 0, 1) * StepCount)); }
    }

    public double DefaultValue { get; set; }

    /// <summary>The number of choices - 1 (set from the names).</summary>
    public int StepCount { get; set; }

    /// <summary>Always false: a choice is made at once.</summary>
    public bool IsEditing => false;

    public Action EditStarted { get; set; }
    public Action ValueChanged { get; set; }
    public Action EditEnded { get; set; }

    /// <summary>Nothing to do: the drop-down shows hover itself.</summary>
    public void Update()
    {
    }
}
