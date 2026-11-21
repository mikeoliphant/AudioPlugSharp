using System;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A knob bound to a parameter. Drag up to raise it and down to lower it (<see cref="DragRange"/> units for the whole
/// range); double-click for the default. Each drag is one edit gesture (BeginEdit/EndEdit). Call <see cref="Update"/>
/// each frame, for example in EditorUpdate: the knob follows the parameter's edit value (host automation) and shows
/// hover. Its images (<see cref="AudioTextures"/>) are a soft shadow, a body with a soft light on top, a ring and a
/// pointer turned over a 270-degree sweep, tinted from the layout's theme.
/// </summary>
public class ParameterKnob : Dock
{
    const float SweepDegrees = 270;

    AudioPluginParameter parameter;
    ImageElement shadow;
    ImageElement body;
    ImageElement highlight;
    ImageElement ring;
    RotatingImageElement pointer;
    double shownValue = double.NaN;
    double dragStartValue;

    public ParameterKnob(AudioPluginParameter parameter)
    {
        this.parameter = parameter;

        // The images' size, rather than stretched (a Dock's default)
        HorizontalAlignment = EHorizontalAlignment.Center;
        VerticalAlignment = EVerticalAlignment.Center;

        UITheme theme = Layout.Current.Theme;

        RingColor = theme.Border;
        ActiveRingColor = theme.Accent;

        shadow = new ImageElement(AudioTextures.KnobShadow)
        {
            HorizontalAlignment = EHorizontalAlignment.Center,
            VerticalAlignment = EVerticalAlignment.Center,
            Color = new UIColor(0, 0, 0, 160)
        };

        body = new ImageElement(AudioTextures.KnobBody)
        {
            HorizontalAlignment = EHorizontalAlignment.Center,
            VerticalAlignment = EVerticalAlignment.Center,
            // Between the surface and border colors: lighter than input fields, so it stands off the background
            Color = UIColor.Lerp(theme.Surface, theme.Border, 0.6f)
        };

        highlight = new ImageElement(AudioTextures.KnobHighlight)
        {
            HorizontalAlignment = EHorizontalAlignment.Center,
            VerticalAlignment = EVerticalAlignment.Center
        };

        ring = new ImageElement(AudioTextures.KnobRing)
        {
            HorizontalAlignment = EHorizontalAlignment.Center,
            VerticalAlignment = EVerticalAlignment.Center,
            Color = RingColor
        };

        pointer = new RotatingImageElement(AudioTextures.KnobPointer)
        {
            HorizontalAlignment = EHorizontalAlignment.Center,
            VerticalAlignment = EVerticalAlignment.Center,
            Color = theme.Text
        };

        Children.Add(shadow);
        Children.Add(body);
        Children.Add(highlight);
        Children.Add(ring);
        Children.Add(pointer);

        Update();
    }

    public AudioPluginParameter Parameter
    {
        get { return parameter; }
    }

    /// <summary>How far (layout units) a drag goes for the whole range.</summary>
    public float DragRange { get; set; } = 200;

    /// <summary>Whether the knob is being dragged (an edit gesture is open).</summary>
    public bool IsDragging { get; private set; }

    public UIColor ShadowColor
    {
        get { return shadow.Color; }
        set { shadow.Color = value; }
    }

    public UIColor BodyColor
    {
        get { return body.Color; }
        set { body.Color = value; }
    }

    public UIColor PointerColor
    {
        get { return pointer.Color; }
        set { pointer.Color = value; }
    }

    /// <summary>The ring's color, and its color on hover and while dragging.</summary>
    public UIColor RingColor { get; set; }
    public UIColor ActiveRingColor { get; set; }

    /// <summary>
    /// Shows the parameter's edit value, if it changed since the last call, and hover. Doesn't allocate.
    /// </summary>
    public void Update()
    {
        UIColor ringColor = (IsDragging || IsMouseOver) ? ActiveRingColor : RingColor;

        if (ring.Color != ringColor)
        {
            ring.Color = ringColor;

            Layout.Current.AddDirtyRect(ContentBounds);
        }

        double value = parameter.NormalizedEditValue;

        if (value.Equals(shownValue))
            return;

        shownValue = value;

        pointer.Rotation = MathUtil.ToRadians((float)((value - 0.5) * SweepDegrees));

        Layout.Current.AddDirtyRect(ContentBounds);
    }

    public override bool HandleTouch(in Touch touch)
    {
        // Seen for every touch state, so it can count the taps
        bool doubleTap = IsDoubleTap(touch, this);

        switch (touch.TouchState)
        {
            case ETouchState.Pressed:
                CaptureTouch(touch);

                IsDragging = true;
                dragStartValue = parameter.NormalizedEditValue;

                parameter.BeginEdit();
                break;

            case ETouchState.Moved:
            case ETouchState.Held:
                if (IsDragging)
                {
                    float delta = TouchCaptureStartPosition.Y - touch.Position.Y;

                    parameter.NormalizedEditValue = Math.Clamp(dragStartValue + (delta / DragRange), 0, 1);

                    Update();
                }
                break;

            case ETouchState.Released:
            case ETouchState.Invalid:
                if (IsDragging)
                {
                    if (doubleTap)
                        parameter.NormalizedEditValue = parameter.NormalizedDefaultValue;

                    IsDragging = false;

                    ReleaseTouch();

                    parameter.EndEdit();

                    Update();
                }
                break;
        }

        return true;
    }
}
