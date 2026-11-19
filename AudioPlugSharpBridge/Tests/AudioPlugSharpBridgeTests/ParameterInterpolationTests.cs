using AudioPlugSharp;
using Xunit;

namespace AudioPlugSharpBridgeTests;

/// <summary>
/// Sample-accurate parameter interpolation as plugins use it (AddParameterChangePoint at the start of a segment,
/// GetInterpolatedProcessValue per sample, ResetParameterChange at the end of the block), including a
/// DecibelParameter's -inf dB minimum: automation that reaches 0 must not produce NaN or a jump.
/// </summary>
public class ParameterInterpolationTests
{
    const int Frames = 256;

    static double[] Block(AudioPluginParameter p, double normalizedTarget, int offset = Frames - 1)
    {
        p.AddParameterChangePoint(normalizedTarget, offset);
        var gains = new double[Frames];
        bool update = p.NeedInterpolationUpdate;
        double value = p.ProcessValue;
        for (int i = 0; i < Frames; i++)
        {
            if (update) value = p.GetInterpolatedProcessValue(i);
            gains[i] = AudioPluginParameter.DBToLinear(value);
        }
        p.ResetParameterChange();
        return gains;
    }

    static DecibelParameter Gain()
    {
        var p = new DecibelParameter { ID = "gain", Name = "Gain", MaxValue = 12 };
        p.NormalizedProcessValue = 0.5;
        p.ResetParameterChange();
        return p;
    }

    [Fact]
    public void Ramp_to_minus_infinity_is_finite_and_gradual()
    {
        var p = Gain();
        var gains = Block(p, 0.0);
        Assert.All(gains, g => Assert.True(double.IsFinite(g)));
        Assert.True(gains[0] > 0.01, $"first sample already silent: {gains[0]}");  // no instant cut
        Assert.Equal(0.0, gains[^1], 6);
    }

    [Fact]
    public void Ramp_from_minus_infinity_is_finite_and_gradual()
    {
        var p = Gain();
        Block(p, 0.0);
        var gains = Block(p, 0.5);
        Assert.All(gains, g => Assert.True(double.IsFinite(g), $"gain {g}"));
        Assert.True(gains[0] < gains[^1]);
    }

    [Fact]
    public void Staying_at_minus_infinity_is_silent_not_NaN()
    {
        var p = Gain();
        Block(p, 0.0);
        var gains = Block(p, 0.0);
        Assert.All(gains, g => Assert.Equal(0.0, g));
    }

    [Fact]
    public void After_the_last_point_the_value_holds_the_new_value()
    {
        // Host automation: one point in the middle of the block.
        var p = new AudioPluginParameter { ID = "x", Name = "X", MinValue = 0, MaxValue = 1 };
        p.NormalizedProcessValue = 0.0;
        p.ResetParameterChange();
        p.AddParameterChangePoint(1.0, 100);
        Assert.Equal(1.0, p.GetInterpolatedProcessValue(100), 9);
        Assert.Equal(1.0, p.GetInterpolatedProcessValue(101), 9); // not back to 0
        Assert.Equal(1.0, p.GetInterpolatedProcessValue(Frames - 1), 9);
    }

    [Fact]
    public void Ordinary_ramp_is_unchanged()
    {
        var p = new AudioPluginParameter { ID = "x", Name = "X", MinValue = -1, MaxValue = 1 };
        p.NormalizedProcessValue = 0.5;
        p.ResetParameterChange();
        p.AddParameterChangePoint(1.0, Frames - 1);
        Assert.Equal(0.0 + 1.0 / Frames, p.GetInterpolatedProcessValue(0), 9);
        Assert.Equal(1.0, p.GetInterpolatedProcessValue(Frames - 1), 9);
    }
}
