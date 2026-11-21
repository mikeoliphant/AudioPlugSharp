using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// An on/off switch bound to a parameter: a lamp, lit when the parameter is on (its normalized value at least 0.5).
/// A click toggles it, as one edit gesture (BeginEdit/EndEdit) setting the parameter to its maximum or minimum. Call
/// <see cref="Update"/> each frame, for example in EditorUpdate: the switch follows the parameter's edit value (host
/// automation). Images: <see cref="AudioTextures"/>, tinted with <see cref="LampColor"/> (green by default).
/// </summary>
public class ParameterSwitch : Button
{
    AudioPluginParameter parameter;
    ImageElement onLamp;
    ImageElement offLamp;

    public ParameterSwitch(AudioPluginParameter parameter)
    {
        this.parameter = parameter;

        HorizontalAlignment = EHorizontalAlignment.Center;
        VerticalAlignment = EVerticalAlignment.Center;

        IsToggleButton = true;

        PressedElement = onLamp = new ImageElement(AudioTextures.SwitchOn);
        UnpressedElement = offLamp = new ImageElement(AudioTextures.SwitchOff);

        LampColor = new UIColor(76, 214, 68);

        // A toggle button changes state on press: the parameter follows it
        ClickAction = delegate
        {
            parameter.BeginEdit();

            parameter.NormalizedEditValue = IsPressed ? 1 : 0;

            parameter.EndEdit();
        };

        Update();
    }

    public AudioPluginParameter Parameter
    {
        get { return parameter; }
    }

    /// <summary>Whether the parameter is on: its normalized edit value at least 0.5.</summary>
    public bool IsOn => parameter.NormalizedEditValue >= 0.5;

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

    /// <summary>Shows whether the parameter is on, if it changed. Doesn't allocate.</summary>
    public void Update()
    {
        if (IsPressed == IsOn)
            return;

        SetPressed(IsOn);

        Layout.Current.AddDirtyRect(ContentBounds);
    }
}
