using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// UILayout's <see cref="Slider"/> as a value control (<see cref="IValueControl"/>), for
/// <see cref="ParameterControl"/> to bind to a parameter.
/// </summary>
public class ValueSlider : Slider, IValueControl
{
    public ValueSlider(bool vertical = false)
        : base(vertical)
    {
    }
}
