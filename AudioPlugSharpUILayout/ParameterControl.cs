using System;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A parameter's control as a plugin editor shows it: the parameter's name above a value control (<see cref="Knob"/>,
/// <see cref="ValueSlider"/>, <see cref="LampSwitch"/>, or any <see cref="IValueControl"/>), with its formatted value
/// popping in over the control while the user changes it (<see cref="ValuePopup"/>). Binds the control to the
/// parameter: each user edit is one edit gesture (BeginEdit/EndEdit) setting the normalized edit value, and
/// <see cref="Update"/> (call it each frame, for example in EditorUpdate) makes the control follow the parameter's edit
/// value (host automation; the popup shows only for the user's edits). Depends only on the parameter, so any
/// plugin's parameters can be shown this way (for example an editor generated from them).
/// </summary>
public class ParameterControl : VerticalStack
{
    AudioPluginParameter parameter;
    IValueControl control;
    TextBlock nameText;
    ValuePopup popup;
    ControlArea area;

    /// <summary>A knob for the parameter.</summary>
    public static ParameterControl Knob(AudioPluginParameter parameter)
    {
        return new ParameterControl(parameter, new Knob());
    }

    /// <summary>A slider for the parameter, horizontal (stretched across its row) or vertical.</summary>
    public static ParameterControl Slider(AudioPluginParameter parameter, bool vertical = false)
    {
        ParameterControl control = new ParameterControl(parameter, new ValueSlider(vertical));

        if (vertical)
        {
            control.VerticalAlignment = EVerticalAlignment.Stretch;
        }
        else
        {
            control.HorizontalAlignment = EHorizontalAlignment.Stretch;
        }

        return control;
    }

    /// <summary>
    /// An on/off switch for the parameter (on at a normalized value of at least 0.5; sets 1 or 0), in a knob-sized
    /// space so it lines up with knobs in a row.
    /// </summary>
    public static ParameterControl Switch(AudioPluginParameter parameter)
    {
        return new ParameterControl(parameter, new LampSwitch())
        {
            ShowValuePopup = false,
            MinControlHeight = Layout.Current.GetImage(AudioTextures.KnobBody).LogicalHeight
        };
    }

    /// <summary>
    /// The control that suits the parameter. A knob for now: parameters don't yet say whether they are on/off,
    /// stepped or a list of choices.
    /// </summary>
    public static ParameterControl For(AudioPluginParameter parameter)
    {
        return Knob(parameter);
    }

    public ParameterControl(AudioPluginParameter parameter, IValueControl control)
    {
        if (!(control is UIElement controlElement))
            throw new ArgumentException("The control must be a UIElement", nameof(control));

        this.parameter = parameter;
        this.control = control;

        UITheme theme = Layout.Current.Theme;

        // Top-aligned, so the names of controls in a row line up
        HorizontalAlignment = EHorizontalAlignment.Center;
        VerticalAlignment = EVerticalAlignment.Top;
        ChildSpacing = 4;

        nameText = new TextBlock(parameter.Name)
        {
            TextColor = theme.MutedText,
            TextFont = Layout.Current.DefaultSmallFont ?? Layout.Current.DefaultFont,
            HorizontalAlignment = EHorizontalAlignment.Center
        };
        Children.Add(nameText);

        // The popup over the control, centered on it (it may be wider than the control); the area takes the
        // control's size, so the popup showing doesn't change the layout
        area = new ControlArea(controlElement)
        {
            HorizontalAlignment = EHorizontalAlignment.Stretch,
            VerticalAlignment = EVerticalAlignment.Stretch
        };
        area.Children.Add(controlElement);
        area.Children.Add(popup = new ValuePopup());
        Children.Add(area);

        control.DefaultValue = parameter.NormalizedDefaultValue;
        control.Value = parameter.NormalizedEditValue;

        // Formatted once now: the first formatting's one-time costs come while the editor opens, not on the first edit
        parameter.FormatValue(parameter.EditValue, popup.Text);

        control.EditStarted = delegate
        {
            parameter.BeginEdit();

            ShowValue();
        };

        control.ValueChanged = delegate
        {
            parameter.NormalizedEditValue = control.Value;

            ShowValue();
        };

        control.EditEnded = delegate
        {
            parameter.EndEdit();

            ShowValue();
        };
    }

    public AudioPluginParameter Parameter
    {
        get { return parameter; }
    }

    /// <summary>The value control (a <see cref="Knob"/>, <see cref="ValueSlider"/>, ...), for example to set its
    /// colors.</summary>
    public IValueControl Control
    {
        get { return control; }
    }

    /// <summary>Whether the parameter's name shows above the control (default true).</summary>
    public bool ShowName
    {
        get { return nameText.Visible; }

        set
        {
            if (nameText.Visible == value)
                return;

            nameText.Visible = value;

            UpdateContentLayout();
        }
    }

    /// <summary>Whether the value pops in over the control while the user changes it (default true; not for a
    /// switch, whose lamp shows its value).</summary>
    public bool ShowValuePopup { get; set; } = true;

    /// <summary>The least height of the control's space (the control centered in it), so controls of different
    /// sizes line up in a row: a switch takes a knob's height. 0 (the default): the control's own height.</summary>
    public float MinControlHeight
    {
        get { return area.MinHeight; }

        set
        {
            area.MinHeight = value;

            UpdateContentLayout();
        }
    }

    /// <summary>
    /// Follows the parameter's edit value (host automation), shows hover, and fades the value popup. Call each
    /// frame. Doesn't allocate, unless the parameter formats its values only as strings.
    /// </summary>
    public void Update()
    {
        control.Update();

        if (!control.IsEditing)
            control.Value = parameter.NormalizedEditValue;

        popup.Update();
    }

    // The value in the popup: held while the user is editing (a drag), fading after
    void ShowValue()
    {
        if (!ShowValuePopup)
            return;

        popup.Text.Clear();

        parameter.FormatValue(parameter.EditValue, popup.Text);

        popup.Show(hold: control.IsEditing);

        // Placed again: it was hidden, or its text is a different width
        area.UpdateContentLayout();
    }

    // A Dock sized by its control alone (the popup over it doesn't count), at least MinHeight high
    class ControlArea : Dock
    {
        UIElement control;

        public ControlArea(UIElement control)
        {
            this.control = control;
        }

        public float MinHeight { get; set; }

        protected override void GetContentSize(out float width, out float height)
        {
            control.GetSize(out width, out height);

            height = Math.Max(height, MinHeight);
        }
    }
}
