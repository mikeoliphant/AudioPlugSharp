using System;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A knob for a value from 0 to 1 (<see cref="IValueControl"/>; <see cref="ParameterControl"/> binds one to a
/// parameter). Drag up to raise it and down to lower it (<see cref="DragRange"/> units for the whole range), or turn
/// the mouse wheel over it; hold Shift for fine adjustment (<see cref="ControlInput"/>); double-click for the default.
/// Its images (<see cref="AudioTextures"/>) are a soft shadow, a body with a soft light on top, a ring and a pointer turned
/// over a 270-degree sweep, tinted from the layout's theme; the ring takes the accent color on hover and while editing.
/// </summary>
public class Knob : Dock, IValueControl
{
    const float SweepDegrees = 270;

    ImageElement shadow;
    ImageElement body;
    ImageElement highlight;
    ImageElement ring;
    RotatingImageElement pointer;
    double value = -1;

    // The drag's anchor: the touch position and value it moves from, re-taken when fine adjustment starts or stops
    float anchorPosition;
    double anchorValue;
    bool anchorFine;

    public Knob()
    {
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

        Value = 0.5;
    }

    public double Value
    {
        get { return value; }

        set
        {
            value = Math.Clamp(value, 0, 1);

            if (value == this.value)
                return;

            this.value = value;

            pointer.Rotation = MathUtil.ToRadians((float)((value - 0.5) * SweepDegrees));

            Layout.Current.AddDirtyRect(ContentBounds);
        }
    }

    public double DefaultValue { get; set; } = 0.5;

    public bool IsEditing { get; private set; }

    public Action EditStarted { get; set; }
    public Action ValueChanged { get; set; }
    public Action EditEnded { get; set; }

    /// <summary>How far (layout units) a drag goes for the whole range.</summary>
    public float DragRange { get; set; } = 200;

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

    /// <summary>The ring's color, and its color on hover and while editing.</summary>
    public UIColor RingColor { get; set; }
    public UIColor ActiveRingColor { get; set; }

    public void Update()
    {
        UIColor ringColor = (IsEditing || IsMouseOver) ? ActiveRingColor : RingColor;

        if (ring.Color == ringColor)
            return;

        ring.Color = ringColor;

        Layout.Current.AddDirtyRect(ContentBounds);
    }

    // A value from the user, reported if it changed
    void Change(double newValue)
    {
        double before = value;

        Value = newValue;

        if ((value != before) && (ValueChanged != null))
            ValueChanged();
    }

    public override bool HandleTouch(in Touch touch)
    {
        // Seen for every touch state, so it can count the taps
        bool doubleTap = IsDoubleTap(touch, this);

        switch (touch.TouchState)
        {
            case ETouchState.Pressed:
                CaptureTouch(touch);

                IsEditing = true;

                anchorPosition = touch.Position.Y;
                anchorValue = value;
                anchorFine = ControlInput.IsFine;

                if (EditStarted != null)
                    EditStarted();
                break;

            case ETouchState.Moved:
            case ETouchState.Held:
                if (IsEditing)
                {
                    bool fine = ControlInput.IsFine;

                    // Shift pressed or released mid-drag: go on from here at the new speed
                    if (fine != anchorFine)
                    {
                        anchorPosition = touch.Position.Y;
                        anchorValue = value;
                        anchorFine = fine;
                    }

                    float delta = (anchorPosition - touch.Position.Y) / DragRange;

                    if (fine)
                        delta *= ControlInput.FineScale;

                    Change(anchorValue + delta);
                }
                break;

            case ETouchState.Released:
            case ETouchState.Invalid:
                if (IsEditing)
                {
                    if (doubleTap)
                        Change(DefaultValue);

                    IsEditing = false;

                    ReleaseTouch();

                    if (EditEnded != null)
                        EditEnded();
                }
                break;
        }

        return true;
    }

    public override bool HandleMouseWheel(int delta)
    {
        if (IsEditing)
            return true;

        if (EditStarted != null)
            EditStarted();

        Change(ControlInput.WheelValue(value, delta));

        if (EditEnded != null)
            EditEnded();

        return true;
    }
}
