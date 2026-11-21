using System;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A slider bound to a parameter, horizontal (low at the left) or vertical (low at the bottom): a recessed track,
/// filled in the accent color up to a thumb. Drag the thumb, or press on the track to move the thumb there and drag on;
/// double-click for the default. Each drag is one edit gesture (BeginEdit/EndEdit). Call <see cref="Update"/> each
/// frame, for example in EditorUpdate: the slider follows the parameter's edit value (host automation) and shows hover.
/// It stretches along its length; give it the room to slide in. Images: <see cref="AudioTextures"/>, tinted from the
/// layout's theme.
/// </summary>
public class ParameterSlider : Dock
{
    AudioPluginParameter parameter;
    bool vertical;
    NinePatchWrapper track;
    NinePatchWrapper fill;
    Dock thumb;
    ImageElement thumbBody;
    ImageElement thumbRing;
    float thumbSize;
    float trackThickness;
    double shownValue = double.NaN;
    float dragOffset;

    public ParameterSlider(AudioPluginParameter parameter, bool vertical = false)
    {
        this.parameter = parameter;
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

        Update();
    }

    public AudioPluginParameter Parameter
    {
        get { return parameter; }
    }

    /// <summary>Whether the slider is being dragged (an edit gesture is open).</summary>
    public bool IsDragging { get; private set; }

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

    /// <summary>The thumb's ring color, and its color on hover and while dragging.</summary>
    public UIColor RingColor { get; set; }
    public UIColor ActiveRingColor { get; set; }

    /// <summary>
    /// Shows the parameter's edit value, if it changed since the last call, and hover. Doesn't allocate.
    /// </summary>
    public void Update()
    {
        UIColor ringColor = (IsDragging || IsMouseOver) ? ActiveRingColor : RingColor;

        if (thumbRing.Color != ringColor)
        {
            thumbRing.Color = ringColor;

            Layout.Current.AddDirtyRect(ContentBounds);
        }

        double value = parameter.NormalizedEditValue;

        if (value.Equals(shownValue))
            return;

        shownValue = value;

        UpdateContentLayout();
    }

    // How far the thumb moves: the length less the thumb
    float Travel => Math.Max(0, (vertical ? ContentBounds.Height : ContentBounds.Width) - thumbSize);

    // Also called when the slider's bounds change (a resize), so the thumb and fill follow the length.
    public override void UpdateContentLayout()
    {
        float value = (float)Math.Clamp(parameter.NormalizedEditValue, 0, 1);
        float inset = (thumbSize - trackThickness) / 2;

        // The thumb's offset from the low end, and the fill from the track's low end to the thumb's center
        float thumbOffset = value * Travel;
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

                IsDragging = true;

                parameter.BeginEdit();

                float position = Position(touch);
                float thumbStart = (float)Math.Clamp(parameter.NormalizedEditValue, 0, 1) * Travel;

                // On the thumb, drag it from where it was taken; elsewhere, its center jumps to the touch
                if ((position >= thumbStart) && (position <= (thumbStart + thumbSize)))
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
                if (IsDragging)
                    MoveTo(Position(touch));
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

    // The thumb's grabbed point to the position
    void MoveTo(float position)
    {
        float travel = Travel;

        if (travel <= 0)
            return;

        parameter.NormalizedEditValue = Math.Clamp((position - dragOffset) / travel, 0, 1);

        Update();
    }
}
