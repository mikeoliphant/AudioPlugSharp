using System;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A level meter: the meter background nine-patch (<see cref="AudioTextures"/>, in its
/// <see cref="NinePatchWrapper.Color"/>), with its bar nine-patch filled to the level on a decibel scale
/// (<see cref="FloorDb"/> to 0 dB) in the meter colors: low, then hot from <see cref="HotLevel"/>, then clipping from
/// <see cref="ClipLevel"/>. The background and the hot color come from the layout's theme. Too low a level for the bar image's width shows
/// no bar. The owner sets <see cref="Level"/> each frame (for a falling meter, the higher of the new peak and the last
/// level decayed). Audio-specific, so in AudioPlugSharp rather than UILayout.
/// </summary>
public class LevelMeter : NinePatchWrapper
{
    NinePatchWrapper bar;
    float level;

    public LevelMeter()
        : base(Layout.Current.GetImage(AudioTextures.MeterBackground))
    {
        UITheme theme = Layout.Current.Theme;

        HorizontalAlignment = EHorizontalAlignment.Left;
        VerticalAlignment = EVerticalAlignment.Center;

        // Recessed: the background darkened, on dark and light themes alike
        Color = theme.Background * 0.75f;
        HotColor = theme.Warning;

        // The bar covers the background, not inside its corners.
        Padding = new LayoutPadding(0);

        bar = new NinePatchWrapper(Layout.Current.GetImage(AudioTextures.MeterBar))
        {
            HorizontalAlignment = EHorizontalAlignment.Left,
            VerticalAlignment = EVerticalAlignment.Stretch,
            Color = LowColor,
            Visible = false
        };

        Child = bar;
    }

    /// <summary>The bar's colors when low, hot (the theme's warning color by default) and clipping.</summary>
    public UIColor LowColor { get; set; } = new UIColor(80, 190, 90);
    public UIColor HotColor { get; set; }
    public UIColor ClipColor { get; set; } = new UIColor(230, 60, 50);

    /// <summary>The level at the meter's left end, in dB.</summary>
    public float FloorDb { get; set; } = -60;

    /// <summary>The level (amplitude) from which the bar is in the hot color: -6 dB.</summary>
    public float HotLevel { get; set; } = 0.5f;

    /// <summary>The level (amplitude) from which the bar is in the clipping color: about -0.5 dB.</summary>
    public float ClipLevel { get; set; } = 0.95f;

    /// <summary>The peak level, as an amplitude (1 is full scale).</summary>
    public float Level
    {
        get { return level; }

        set
        {
            if (value == level)
                return;

            level = value;

            UpdateContentLayout();
        }
    }

    // Also called when the meter's bounds change (a resize), so the bar follows the width.
    public override void UpdateContentLayout()
    {
        float db = (level > 0) ? (20 * MathF.Log10(level)) : FloorDb;
        float width = ContentBounds.Width * Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

        // Narrower than the bar image, the nine-patch can't draw: no bar.
        bar.Visible = width >= bar.Image.LogicalWidth;
        bar.DesiredWidth = width;
        bar.Color = (level >= ClipLevel) ? ClipColor : ((level >= HotLevel) ? HotColor : LowColor);

        base.UpdateContentLayout();
    }
}
