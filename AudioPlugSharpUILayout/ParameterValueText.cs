using System;
using System.Text;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// A parameter's edit value as text (its FormatValue), following the value as the host or the editor changes it. It is
/// as wide as the widest text the parameter shows across its range, so its size doesn't change with the value; the
/// text is aligned in that width by <see cref="TextAlignment"/> (right by default). Call <see cref="Update"/> each
/// frame, for example in EditorUpdate.
/// </summary>
public class ParameterValueText : UIElement
{
    // Normalized values sampled for the widest text, evenly across the range. Below the first step above 0 (about
    // -168 dB for a DecibelParameter's -inf to +12 dB) a value can have more digits than sampled, and its text is wider
    // than the element; only host automation gets there.
    const int Samples = 1000;

    AudioPluginParameter parameter;
    double shownValue = double.NaN;
    UIFont measuredFont;
    float widestText;

    public ParameterValueText(AudioPluginParameter parameter)
    {
        this.parameter = parameter;

        TextFont = Layout.Current.DefaultFont;
        TextColor = Layout.Current.Theme.Text;

        Update();
    }

    public AudioPluginParameter Parameter
    {
        get { return parameter; }
    }

    /// <summary>The text shown: the parameter's formatted edit value as of the last <see cref="Update"/>. Reused for
    /// each new value (formatting into it doesn't allocate).</summary>
    public StringBuilder Text { get; } = new StringBuilder(TextCapacity);

    // Room for any value's text from the start, so the builder never grows.
    const int TextCapacity = 64;

    public UIColor TextColor { get; set; }
    public UIFont TextFont { get; set; }

    /// <summary>Where the text sits in the element's width: Left, Center or Right (the default).</summary>
    public EHorizontalAlignment TextAlignment { get; set; } = EHorizontalAlignment.Right;

    /// <summary>
    /// Shows the parameter's edit value, if it changed since the last call. Doesn't allocate, unless the parameter
    /// formats its values only as strings (see AudioPluginParameter.FormatValue).
    /// </summary>
    public void Update()
    {
        double value = parameter.EditValue;

        if (value.Equals(shownValue))
            return;

        shownValue = value;

        parameter.FormatValue(value, Text);

        UpdateContentLayout();
    }

    protected override void GetContentSize(out float width, out float height)
    {
        if (TextFont != measuredFont)
        {
            measuredFont = TextFont;
            widestText = MeasureWidestText();
        }

        TextFont.MeasureString(Text, out _, out height);

        width = widestText;
    }

    protected override void DrawContents()
    {
        base.DrawContents();

        TextFont.MeasureString(Text, out float textWidth, out _);

        float x = TextAlignment switch
        {
            EHorizontalAlignment.Right => ContentBounds.Right - textWidth,
            EHorizontalAlignment.Center => ContentBounds.X + ((ContentBounds.Width - textWidth) / 2),
            _ => ContentBounds.X,
        };

        Layout.Current.GraphicsContext.DrawText(Text, TextFont, x, ContentBounds.Y, TextColor);
    }

    float MeasureWidestText()
    {
        char widestDigit = WidestDigit();
        float widest = 0;

        for (int i = 0; i <= Samples; i++)
            widest = Math.Max(widest, MeasureWithDigit(parameter.GetValueDenormalized((double)i / Samples), widestDigit));

        return widest;
    }

    // The text for the value with every digit made the widest one: as wide as any value with as many digits.
    float MeasureWithDigit(double value, char digit)
    {
        char[] text = parameter.FormatValue(value).ToCharArray();

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]))
                text[i] = digit;
        }

        TextFont.MeasureString(new string(text), out float width, out _);

        return width;
    }

    char WidestDigit()
    {
        char widestDigit = '0';
        float widest = 0;

        for (char digit = '0'; digit <= '9'; digit++)
        {
            TextFont.MeasureString(digit.ToString(), out float width, out _);

            if (width > widest)
            {
                widest = width;
                widestDigit = digit;
            }
        }

        return widestDigit;
    }
}
