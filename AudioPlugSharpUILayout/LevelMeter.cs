using UILayout;

namespace AudioPlugSharpStandalone;

/// <summary>
/// A level meter: the meter background nine-patch (<see cref="AudioTextures"/>), with its bar nine-patch filled to the
/// level on a decibel scale (<see cref="FloorDb"/> to 0 dB) in the meter colors: low, then hot from
/// <see cref="HotLevel"/>, then clipping from <see cref="ClipLevel"/>. Too low a level for the bar image's width shows
/// no bar. Audio-specific, so in AudioPlugSharp rather than UILayout (for a later toolset for AudioPlugSharp's UILayout
/// editors).
/// </summary>
public class LevelMeter : NinePatchWrapper
{
    NinePatchWrapper bar;
    float level;

    public LevelMeter()
        : base(Layout.Current.GetImage(AudioTextures.MeterBackground))
    {
        HorizontalAlignment = EHorizontalAlignment.Left;
        VerticalAlignment = EVerticalAlignment.Center;
        Color = MeterBackgroundColor;

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

    /// <summary>The meters' colors: the background, and the bar when low, hot and clipping.</summary>
    public static UIColor MeterBackgroundColor { get; set; } = new UIColor(20, 22, 26);
    public static UIColor LowColor { get; set; } = new UIColor(80, 190, 90);
    public static UIColor HotColor { get; set; } = new UIColor(255, 180, 60);
    public static UIColor ClipColor { get; set; } = new UIColor(230, 60, 50);

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

            float db = (level > 0) ? (20 * MathF.Log10(level)) : FloorDb;
            float width = ContentBounds.Width * Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

            // Narrower than the bar image, the nine-patch can't draw: no bar.
            bar.Visible = width >= bar.Image.Width;
            bar.DesiredWidth = width;
            bar.Color = (level >= ClipLevel) ? ClipColor : ((level >= HotLevel) ? HotColor : LowColor);

            UpdateContentLayout();
        }
    }
}
