using AudioPlugSharp;
using Xunit;

namespace AudioPlugSharpBridgeTests;

/// <summary>
/// Change points from the host (automation) ramp exactly onto their samples; while the user edits a parameter from the
/// plugin's editor (and a moment after), they glide over SmoothingTime instead, carried across blocks (hosts call
/// ResetParameterChange(frames) at every block's end), so a control's once-per-frame edits are smooth.
/// </summary>
public class ParameterRampTests
{
    const int Frames = 128;

    static AudioPluginParameter Linear(double start = 0)
    {
        var p = new AudioPluginParameter { ID = "x", Name = "X", MinValue = 0, MaxValue = 1 };

        p.NormalizedProcessValue = start;
        p.ResetParameterChange();

        return p;
    }

    // One block as a plugin runs it: an optional point, the value per sample, then the host's block end
    static double[] Block(AudioPluginParameter p, double? point = null, int offset = Frames - 1)
    {
        if (point is double value)
            p.AddParameterChangePoint(value, offset);

        var values = new double[Frames];
        bool update = p.NeedInterpolationUpdate;

        for (int i = 0; i < Frames; i++)
            values[i] = update ? p.GetInterpolatedProcessValue(i) : p.ProcessValue;

        p.ResetParameterChange(Frames);

        return values;
    }

    // ---- Automation: sample-accurate ------------------------------------------------------------------------------

    [Fact]
    public void Automation_every_block_ramps_exactly_to_each_point()
    {
        var p = Linear();

        for (int block = 1; block <= 4; block++)
        {
            double target = block * 0.1;
            double[] values = Block(p, target);

            Assert.Equal(target, values[Frames - 1], 9);
            Assert.Equal(target - 0.1 + (0.1 / Frames), values[0], 9);
        }
    }

    [Fact]
    public void Two_automation_points_in_a_block_ramp_exactly()
    {
        var p = Linear();

        p.AddParameterChangePoint(0.5, 49);
        Assert.Equal(0.5, p.GetInterpolatedProcessValue(49), 9);

        p.AddParameterChangePoint(1.0, 99);
        Assert.Equal(0.75, p.GetInterpolatedProcessValue(74), 9);
        Assert.Equal(1.0, p.GetInterpolatedProcessValue(99), 9);
        Assert.Equal(1.0, p.GetInterpolatedProcessValue(Frames - 1), 9);
    }

    [Fact]
    public void An_automation_step_lands_on_its_sample()
    {
        var p = Linear();

        for (int b = 0; b < 100; b++)
            Block(p);

        double[] values = Block(p, 1.0, 100);

        Assert.Equal(1.0, values[100], 9);
        Assert.Equal(1.0, values[Frames - 1], 9);
    }

    // ---- Edits from the editor: smoothed ----------------------------------------------------------------------------

    [Fact]
    public void An_edit_glides_over_SmoothingTime()
    {
        var p = Linear();
        p.SmoothingTime = 0.01;   // 480 samples at 48 kHz (the rate before a host)

        Block(p);

        p.BeginEdit();

        var values = new List<double>();

        values.AddRange(Block(p, 1.0, 0));

        for (int b = 0; b < 4; b++)
            values.AddRange(Block(p));

        p.EndEdit();

        int reached = values.FindIndex(v => v >= 1.0);

        Assert.InRange(reached, 478, 481);
        Assert.InRange(values[240], 0.45, 0.55);
    }

    [Fact]
    public void Edits_once_per_editor_frame_glide_without_holding()
    {
        // An editor frame every 6 blocks at a small buffer size (16 ms at 128 frames, 48 kHz)
        var p = Linear();
        var all = new List<double>();
        double target = 0;

        p.BeginEdit();

        for (int edit = 0; edit < 5; edit++)
        {
            target += 0.1;

            all.AddRange(Block(p, target));

            for (int b = 0; b < 5; b++)
                all.AddRange(Block(p));
        }

        p.EndEdit();

        // Rising all the way while the control moves (no flat stretches), in small steps
        for (int i = 1; i < all.Count; i++)
        {
            Assert.True(all[i] > all[i - 1], $"held at sample {i}: {all[i]}");
            Assert.True(all[i] - all[i - 1] < 0.0002, $"jumped at sample {i}");
        }

        // The glide settles on the last edit
        for (int b = 0; b < 10; b++)
            Block(p);

        Assert.Equal(target, Block(p)[0], 9);
    }

    [Fact]
    public void An_edit_just_after_the_gesture_ended_still_glides()
    {
        // A wheel notch or double-click is a whole gesture on the UI thread, over before its point reaches the audio
        var p = Linear();
        p.SmoothingTime = 0.01;

        Block(p);

        p.BeginEdit();
        p.EndEdit();

        double[] values = Block(p, 1.0, 0);

        Assert.True(values[Frames - 1] < 1.0, "it jumped");
    }

    [Fact]
    public void A_while_after_an_edit_points_are_exact_again()
    {
        var p = Linear();

        Block(p);

        p.BeginEdit();
        p.EndEdit();

        Thread.Sleep(400);

        double[] values = Block(p, 1.0, 50);

        Assert.Equal(1.0, values[50], 9);
    }

    [Fact]
    public void SmoothingTime_zero_ramps_edits_within_the_block()
    {
        var p = Linear();
        p.SmoothingTime = 0;

        Block(p);

        p.BeginEdit();

        double[] values = Block(p, 1.0);

        p.EndEdit();

        Assert.Equal(1.0, values[Frames - 1], 9);
    }

    [Fact]
    public void A_new_edit_mid_glide_carries_on_from_where_the_value_is()
    {
        var p = Linear();

        Block(p);

        p.BeginEdit();

        double[] first = Block(p, 1.0);   // a 20 ms glide (960 samples)
        double before = first[Frames - 1];
        double[] next = Block(p, 0.0);    // turns back after one block

        p.EndEdit();

        Assert.True(before > 0 && before < 0.5);
        Assert.True(Math.Abs(next[0] - before) < 0.01, $"jumped from {before} to {next[0]}");
    }

    [Fact]
    public void A_glide_to_minus_infinity_stays_finite_across_blocks()
    {
        var p = new DecibelParameter { ID = "gain", Name = "Gain", MaxValue = 12 };

        p.NormalizedProcessValue = 0.5;
        p.ResetParameterChange();

        p.BeginEdit();

        var gains = new List<double>();

        gains.AddRange(Block(p, 0.0));

        for (int b = 0; b < 10; b++)
            gains.AddRange(Block(p));

        p.EndEdit();

        Assert.All(gains, g => Assert.False(double.IsNaN(g)));
        Assert.True(double.IsNegativeInfinity(gains[^1]));
        Assert.True(double.IsFinite(gains[Frames]), "the glide carried into the next block");
    }

    [Fact]
    public void Gliding_allocates_nothing()
    {
        var p = Linear();

        p.BeginEdit();

        Block(p, 0.5);
        Block(p);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int b = 0; b < 10; b++)
        {
            p.AddParameterChangePoint(b % 2, Frames - 1);

            for (int i = 0; i < Frames; i++)
                p.GetInterpolatedProcessValue(i);

            p.ResetParameterChange(Frames);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        p.EndEdit();

        Assert.Equal(0, allocated);
    }
}
