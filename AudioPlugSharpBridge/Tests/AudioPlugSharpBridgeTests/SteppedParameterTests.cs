using System.Text;
using AudioPlugSharp;
using Xunit;

namespace AudioPlugSharpBridgeTests;

/// <summary>
/// Stepped parameters (StepCount, ValueNames, BoolParameter, ChoiceParameter): values snap to steps the way VST3
/// divides the normalized range, names replace numbers, and processing switches at a change point instead of ramping.
/// </summary>
public class SteppedParameterTests
{
    const int Frames = 256;

    static ChoiceParameter Mode()
    {
        return new ChoiceParameter { ID = "mode", Name = "Mode", Choices = new[] { "Clean", "Crunch", "Lead" } };
    }

    [Fact]
    public void Choice_parameter_sets_range_and_steps()
    {
        var p = Mode();

        Assert.Equal(0, p.MinValue);
        Assert.Equal(2, p.MaxValue);
        Assert.Equal(2, p.StepCount);
        Assert.True(p.IsStepped);
        Assert.Equal(0, p.NormalizedDefaultValue);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.33, 0)]
    [InlineData(0.34, 1)]
    [InlineData(0.5, 1)]
    [InlineData(0.67, 2)]
    [InlineData(1.0, 2)]
    public void Normalized_values_fall_in_equal_parts(double normalized, int step)
    {
        var p = Mode();

        Assert.Equal(step, p.GetStep(normalized));
        Assert.Equal(step, p.GetValueDenormalized(normalized));
    }

    [Fact]
    public void Steps_round_trip()
    {
        var p = Mode();

        for (int step = 0; step <= 2; step++)
        {
            double normalized = p.GetValueNormalized(step);

            Assert.Equal(step / 2.0, normalized, 12);
            Assert.Equal(step, p.GetValueDenormalized(normalized));
        }
    }

    [Fact]
    public void Values_have_names()
    {
        var p = Mode();
        var text = new StringBuilder();

        Assert.Equal("Crunch", p.FormatValue(1));

        p.FormatValue(2, text);

        Assert.Equal("Lead", text.ToString());
    }

    [Fact]
    public void Bool_parameter_is_on_from_half()
    {
        var p = new BoolParameter { ID = "mute", Name = "Mute" };

        Assert.Equal(1, p.StepCount);
        Assert.Equal(0, p.GetValueDenormalized(0.49));
        Assert.Equal(1, p.GetValueDenormalized(0.5));
        Assert.Equal("On", p.FormatValue(1));

        p.NormalizedProcessValue = 1;

        Assert.True(p.IsOn);
    }

    [Fact]
    public void Stepped_parameter_ignores_range_power()
    {
        var p = new AudioPluginParameter { MinValue = 1, MaxValue = 8, StepCount = 7, RangePower = 3 };

        Assert.Equal(5, p.GetValueDenormalized(p.GetValueNormalized(5)));
        Assert.Equal(4.0 / 7, p.GetValueNormalized(5), 12);
    }

    enum Shape
    {
        Soft,
        HardClip,
        [System.ComponentModel.Description("Wave fold")] Fold,
        LFOSpeed = 10,
        Mode2 = 20,
    }

    [Fact]
    public void Enum_parameter_names_its_members()
    {
        var p = new EnumParameter<Shape> { ID = "shape", Name = "Shape" };

        Assert.Equal(new[] { "Soft", "Hard Clip", "Wave fold", "LFO Speed", "Mode 2" }, p.ValueNames);
        Assert.Equal(4, p.StepCount);
        Assert.Equal("Wave fold", p.FormatValue(2));
    }

    [Fact]
    public void Enum_parameter_value_is_the_member_at_the_position()
    {
        var p = new EnumParameter<Shape>();

        p.NormalizedProcessValue = p.GetValueNormalized(3);

        Assert.Equal(Shape.LFOSpeed, p.Value);

        p.NormalizedProcessValue = 1;

        Assert.Equal(Shape.Mode2, p.Value);

        // Read without allocating (the audio thread reads it)
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        Shape value = p.Value;
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Shape.Mode2, value);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Enum_parameter_default_choice()
    {
        var p = new EnumParameter<Shape> { DefaultChoice = Shape.Fold };

        Assert.Equal(2, p.DefaultValue);
        Assert.Equal(Shape.Fold, p.DefaultChoice);
        Assert.Equal(0.5, p.NormalizedDefaultValue, 12);
        Assert.Equal(Shape.Soft, new EnumParameter<Shape>().DefaultChoice);
    }

    [Fact]
    public void Stepped_parameter_switches_at_the_change_point()
    {
        var p = Mode();

        p.NormalizedProcessValue = 0;
        p.ResetParameterChange();

        p.AddParameterChangePoint(1.0, 100);

        Assert.True(p.NeedInterpolationUpdate);

        for (int i = 0; i < Frames; i++)
            Assert.Equal((i < 100) ? 0 : 2, p.GetInterpolatedProcessValue(i));
    }
}
