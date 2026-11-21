using System;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A slider for a value from 0 to 1 (<see cref="IValueControl"/>; <see cref="ParameterControl"/> binds one to a
/// parameter), horizontal (low at the left) or vertical (low at the bottom): a recessed track, filled in the accent
/// color up to a thumb. Drag the thumb, or press on the track to move the thumb there and drag on; turn the mouse wheel
/// over it; hold Shift for fine adjustment (<see cref="ControlInput"/>); double-click for the default. It stretches
/// along its length; give it the room to slide in. Images: <see cref="AudioTextures"/>, tinted from the layout's theme;
/// the thumb's ring takes the accent color on hover and while editing.
/// </summary>
public class ValueSlider : Dock, IValueControl
{
    bool vertical;
    NinePatchWrapper track;
    NinePatchWrapper fill;
    Dock thumb;
    ImageElement thumbBody;
    ImageElement thumbRing;
    float thumbSize;
    float trackThickness;
    double value = -1;
    float dragOffset;

    // Fine adjustment (Shift) in a drag: the value moves from an anchor, relative to the touch, at a lower speed
    bool fineActive;
    float fineAnchorPosition;
    double fineAnchorValue;

    public ValueSlider(bool vertical = false)
    {
        this.vertical = vertical;

        UITheme theme = Layout.Current.Theme;

        RingColor = theme.Border;
        ActiveRingColor = theme.Accent;

        UIImage trackImage = Layout.Current.GetImage(vertical ? AudioTextures.SliderTrackVertical :
            AudioTextures.SliderTrack);

        trackThickness = vertical ? trackImage.LogicalWidth : trackImage.LogicalHeight;
        thumbSize = Layout.Current.GetImage(AudioTextures.ThumbBody).LogicalWidth;

        // Stretched along its length, as thick as the thumb
        if (vertical)
        {
            HorizontalAlignment = EHorizontalAlignment.Center;
            VerticalAlignment = EVerticalAlignment.Stretch;
            DesiredWidth = thumbSize;
        }
        else
        {
            HorizontalAlignment = EHorizontalAlignment.Stretch;
            VerticalAlignment = EVerticalAlignment.Center;
            DesiredHeight = thumbSize;
        }

        // The track and fill run between the thumb's centers at either end; drawn at the image's thickness (only
        // their length stretches)
        track = new NinePatchWrapper(trackImage)
        {
            Padding = new LayoutPadding(0),
            Color = theme.Background * 0.75f
        };

        fill = new NinePatchWrapper(trackImage)
        {
            Padding = new LayoutPadding(0),
            Color = theme.Accent
        };

        float inset = (thumbSize - trackThickness) / 2;

        if (vertical)
        {
            track.HorizontalAlignment = EHorizontalAlignment.Center;
            track.VerticalAlignment = EVerticalAlignment.Stretch;
            track.DesiredWidth = trackThickness;
            track.Margin = new LayoutPadding(0, inset);

            fill.HorizontalAlignment = EHorizontalAlignment.Center;
            fill.VerticalAlignment = EVerticalAlignment.Bottom;
            fill.DesiredWidth = trackThickness;
        }
        else
        {
            track.HorizontalAlignment = EHorizontalAlignment.Stretch;
            track.VerticalAlignment = EVerticalAlignment.Center;
            track.DesiredHeight = trackThickness;
            track.Margin = new LayoutPadding(inset, 0);

            fill.HorizontalAlignment = EHorizontalAlignment.Left;
            fill.VerticalAlignment = EVerticalAlignment.Center;
            fill.DesiredHeight = trackThickness;
        }

        thumb = new Dock
        {
            HorizontalAlignment = vertical ? EHorizontalAlignment.Center : EHorizontalAlignment.Left,
            VerticalAlignment = vertical ? EVerticalAlignment.Top : EVerticalAlignment.Center
        };

        thumb.Children.Add(new ImageElement(AudioTextures.ThumbShadow) { Color = new UIColor(0, 0, 0, 160) });
        thumb.Children.Add(thumbBody = new ImageElement(AudioTextures.ThumbBody)
        {
            Color = UIColor.Lerp(theme.Surface, theme.Border, 0.6f)
        });
        thumb.Children.Add(new ImageElement(AudioTextures.ThumbHighlight));
        thumb.Children.Add(thumbRing = new ImageElement(AudioTextures.ThumbRing) { Color = RingColor });

        Children.Add(track);
        Children.Add(fill);
        Children.Add(thumb);

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

            UpdateContentLayout();
        }
    }

    public double DefaultValue { get; set; } = 0.5;

    public bool IsEditing { get; private set; }

    public Action EditStarted { get; set; }
    public Action ValueChanged { get; set; }
    public Action EditEnded { get; set; }

    public UIColor TrackColor
    {
        get { return track.Color; }
        set { track.Color = value; }
    }

    public UIColor FillColor
    {
        get { return fill.Color; }
        set { fill.Color = value; }
    }

    public UIColor ThumbColor
    {
        get { return thumbBody.Color; }
        set { thumbBody.Color = value; }
    }

    /// <summary>The thumb's ring color, and its color on hover and while editing.</summary>
    public UIColor RingColor { get; set; }
    public UIColor ActiveRingColor { get; set; }

    public void Update()
    {
        UIColor ringColor = (IsEditing || IsMouseOver) ? ActiveRingColor : RingColor;

        if (thumbRing.Color == ringColor)
            return;

        thumbRing.Color = ringColor;

        Layout.Current.AddDirtyRect(ContentBounds);
    }

    // How far the thumb moves: the length less the thumb
    float Travel => Math.Max(0, (vertical ? ContentBounds.Height : ContentBounds.Width) - thumbSize);

    // Also called when the slider's bounds change (a resize), so the thumb and fill follow the length.
    public override void UpdateContentLayout()
    {
        float inset = (thumbSize - trackThickness) / 2;

        // The thumb's offset from the low end, and the fill from the track's low end to the thumb's center
        float thumbOffset = (float)value * Travel;
        float fillLength = thumbOffset + (thumbSize / 2) - inset;

        if (vertical)
        {
            thumb.Margin = new LayoutPadding(0, Travel - thumbOffset, 0, 0);

            fill.DesiredHeight = fillLength;
            fill.Margin = new LayoutPadding(0, 0, 0, inset);
        }
        else
        {
            thumb.Margin = new LayoutPadding(thumbOffset, 0, 0, 0);

            fill.DesiredWidth = fillLength;
            fill.Margin = new LayoutPadding(inset, 0, 0, 0);
        }

        // Shorter than the image's corners, the nine-patch can't draw (it's under the thumb then anyway)
        UIImage fillImage = fill.Image;

        fill.Visible = fillLength >= (vertical ? fillImage.LogicalHeight : fillImage.LogicalWidth);

        base.UpdateContentLayout();

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

    // Where the touch is along the slider, from its low end's edge in the direction the value grows
    float Position(in Touch touch)
    {
        return vertical ? (ContentBounds.Bottom - touch.Position.Y) : (touch.Position.X - ContentBounds.X);
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

                if (EditStarted != null)
                    EditStarted();

                float position = Position(touch);
                float thumbStart = (float)value * Travel;

                fineActive = ControlInput.IsFine;
                fineAnchorPosition = position;
                fineAnchorValue = value;

                // On the thumb (or in fine adjustment), drag it from where it was taken; elsewhere, its center jumps
                // to the touch
                if (fineActive || ((position >= thumbStart) && (position <= (thumbStart + thumbSize))))
                {
                    dragOffset = position - thumbStart;
                }
                else
                {
                    dragOffset = thumbSize / 2;

                    MoveTo(position);
                }
                break;

            case ETouchState.Moved:
            case ETouchState.Held:
                if (IsEditing)
                    MoveTo(Position(touch));
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

    // The thumb's grabbed point to the position; in fine adjustment, the value from its anchor at the fine speed
    void MoveTo(float position)
    {
        float travel = Travel;

        if (travel <= 0)
            return;

        bool fine = ControlInput.IsFine;

        // Shift pressed or released mid-drag: go on from here, the thumb staying where it is
        if (fine != fineActive)
        {
            fineActive = fine;

            if (fine)
            {
                fineAnchorPosition = position;
                fineAnchorValue = value;
            }
            else
            {
                dragOffset = position - ((float)value * travel);
            }
        }

        Change(fine ?
            fineAnchorValue + (((position - fineAnchorPosition) / travel) * ControlInput.FineScale) :
            (position - dragOffset) / travel);
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
