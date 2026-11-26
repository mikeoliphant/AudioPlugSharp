using System;
using System.Text;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A value that pops in over a control while it changes (from SpiceForge's ParameterValueDisplay): the text of
/// <see cref="Text"/> in a tight <see cref="Frame"/>, translucent (<see cref="Opacity"/>) so the control shows through.
/// Shown by <see cref="Show"/>, held for <see cref="HoldSeconds"/> after the last call and then faded out over
/// <see cref="FadeSeconds"/>. Call <see cref="Update"/> each frame. Its size follows the text, so the element holding
/// it lays it out again after <see cref="Show"/> (UpdateContentLayout; as <see cref="ParameterControl"/> does).
/// Doesn't allocate.
/// </summary>
public class ValuePopup : Frame
{
    PopupText text;
    UIColor fillColor;
    UIColor borderColor;
    UIColor textColor;
    float shownSeconds;
    bool held;

    public ValuePopup()
        : base(new PopupText())
    {
        UITheme theme = Layout.Current.Theme;

        text = (PopupText)Content;

        HorizontalAlignment = EHorizontalAlignment.Center;
        VerticalAlignment = EVerticalAlignment.Center;

        fillColor = theme.Surface;
        borderColor = theme.Border;
        textColor = theme.Text;

        // The text closer to the edge than a frame has it (inside the border's corners)
        ContentPadding = new LayoutPadding(4, 1);

        Visible = false;
    }

    /// <summary>The text shown. Fill it (for example with a parameter's FormatValue), then call Show.</summary>
    public StringBuilder Text => text.Text;

    /// <summary>How opaque the frame is when fully shown (0.7: the control shows through); the text is
    /// opaque.</summary>
    public float Opacity { get; set; } = 0.7f;

    /// <summary>How long it stays after the last Show, and how long it then takes to fade out.</summary>
    public float HoldSeconds { get; set; } = 0.25f;
    public float FadeSeconds { get; set; } = 0.25f;

    /// <summary>Shows the popup at full strength with the current <see cref="Text"/>, and starts its hold time again.
    /// While <paramref name="hold"/> is true (for example during a drag) it stays until a Show with false.</summary>
    public void Show(bool hold = false)
    {
        held = hold;
        shownSeconds = 0;

        if (!Visible)
            Visible = true;

        SetStrength(1);
    }

    /// <summary>Hides it at once.</summary>
    public void Hide()
    {
        held = false;

        if (!Visible)
            return;

        Visible = false;

        Layout.Current.AddDirtyRect(LayoutBounds);
    }

    /// <summary>Holds, fades and hides the popup. Call each frame.</summary>
    public void Update()
    {
        if (!Visible || held)
            return;

        shownSeconds += Layout.Current.SecondsElapsed;

        if (shownSeconds <= HoldSeconds)
            return;

        float fade = (shownSeconds - HoldSeconds) / Math.Max(FadeSeconds, 0.001f);

        if (fade >= 1)
        {
            Hide();

            return;
        }

        SetStrength(1 - fade);
    }

    // 1 fully shown, 0 gone: the frame at Opacity times that, the text at that
    void SetStrength(float strength)
    {
        strength = Math.Clamp(strength, 0, 1);

        byte frameAlpha = (byte)(255 * strength * Math.Clamp(Opacity, 0, 1));
        byte textAlpha = (byte)(255 * strength);

        FillColor = new UIColor(fillColor.R, fillColor.G, fillColor.B, frameAlpha);
        BorderColor = new UIColor(borderColor.R, borderColor.G, borderColor.B, frameAlpha);
        text.TextColor = new UIColor(textColor.R, textColor.G, textColor.B, textAlpha);

        Layout.Current.AddDirtyRect(LayoutBounds);
    }

    // The text, from a StringBuilder (drawing it doesn't allocate)
    class PopupText : UIElement
    {
        public StringBuilder Text { get; } = new StringBuilder(64);
        public UIColor TextColor { get; set; }
        public UIFont TextFont { get; set; } = Layout.Current.Theme.SmallFont ?? Layout.Current.Theme.Font;

        public PopupText()
        {
            HorizontalAlignment = EHorizontalAlignment.Center;
            VerticalAlignment = EVerticalAlignment.Center;
        }

        protected override void GetContentSize(out float width, out float height)
        {
            TextFont.MeasureString(Text, out width, out height);
        }

        protected override void DrawContents()
        {
            base.DrawContents();

            Layout.Current.GraphicsContext.DrawText(Text, TextFont, ContentBounds.X, ContentBounds.Y, TextColor);
        }
    }
}
