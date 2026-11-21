using System;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// An on/off switch (<see cref="IValueControl"/>, 0 or 1; <see cref="ParameterControl"/> binds one to a parameter): a
/// lamp, lit when on (a value of at least 0.5). A click toggles it, as one edit. Images: <see cref="AudioTextures"/>,
/// tinted with <see cref="LampColor"/> (green by default).
/// </summary>
public class LampSwitch : Button, IValueControl
{
    ImageElement onLamp;
    ImageElement offLamp;

    public LampSwitch()
    {
        HorizontalAlignment = EHorizontalAlignment.Center;
        VerticalAlignment = EVerticalAlignment.Center;

        IsToggleButton = true;

        PressedElement = onLamp = new ImageElement(AudioTextures.SwitchOn);
        UnpressedElement = offLamp = new ImageElement(AudioTextures.SwitchOff);

        LampColor = new UIColor(76, 214, 68);

        // A toggle button changes state on press: report it as one edit
        ClickAction = delegate
        {
            if (EditStarted != null)
                EditStarted();

            if (ValueChanged != null)
                ValueChanged();

            if (EditEnded != null)
                EditEnded();
        };
    }

    public double Value
    {
        get { return IsPressed ? 1 : 0; }

        set
        {
            bool on = value >= 0.5;

            if (on == IsPressed)
                return;

            SetPressed(on);

            Layout.Current.AddDirtyRect(ContentBounds);
        }
    }

    public double DefaultValue { get; set; }

    /// <summary>Always false: a click is over at once.</summary>
    public bool IsEditing => false;

    public Action EditStarted { get; set; }
    public Action ValueChanged { get; set; }
    public Action EditEnded { get; set; }

    /// <summary>The lamp's color, lit (the unlit lamp is a dark shade of it).</summary>
    public UIColor LampColor
    {
        get { return onLamp.Color; }

        set
        {
            onLamp.Color = value;
            offLamp.Color = value;
        }
    }

    public void Update()
    {
    }
}
