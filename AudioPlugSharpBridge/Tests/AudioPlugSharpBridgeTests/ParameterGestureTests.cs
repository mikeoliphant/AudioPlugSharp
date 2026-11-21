using System.Collections.Generic;
using AudioPlugSharp;
using Xunit;

namespace AudioPlugSharpBridgeTests;

/// <summary>
/// How parameter edits reach the host: one gesture per drag between BeginEdit()/EndEdit(), a gesture of its own for
/// a lone edit, and nothing for values that come from the host.
/// </summary>
public class ParameterGestureTests
{
    class RecordingHost : IAudioHost
    {
        public readonly List<string> Calls = new();

        public double SampleRate => 48000;
        public uint MaxAudioBufferSize => 256;
        public uint CurrentAudioBufferSize => 256;
        public EAudioBitsPerSample BitsPerSample => EAudioBitsPerSample.Bits32;
        public double BPM => 120;
        public long CurrentProjectSample => 0;
        public bool IsPlaying => false;
        public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset) { }
        public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset) { }
        public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset) { }
        public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset) { }
        public void ProcessAllEvents() { }
        public int ProcessEvents() => 0;
        public void BeginEdit(int parameter) => Calls.Add($"begin {parameter}");
        public void PerformEdit(int parameter, double normalizedValue) => Calls.Add($"perform {parameter} {normalizedValue:0.###}");
        public void EndEdit(int parameter) => Calls.Add($"end {parameter}");
    }

    static (RecordingHost host, DecibelParameter gain) Setup()
    {
        var plugin = new AudioPluginBase();
        var gain = new DecibelParameter { ID = "gain", Name = "Gain", MaxValue = 12 };
        plugin.AddParameter(gain);
        var host = new RecordingHost();
        plugin.Host = host;
        return (host, gain);
    }

    [Fact]
    public void Adding_a_parameter_is_not_an_edit()
    {
        var plugin = new AudioPluginBase { Host = new RecordingHost() };
        var x = new AudioPluginParameter { ID = "x", Name = "X", DefaultValue = 0.25 };
        plugin.AddParameter(x);
        Assert.Empty(((RecordingHost)plugin.Host).Calls);
        Assert.Equal(0.25, x.EditValue);
    }

    [Fact]
    public void A_drag_is_one_gesture()
    {
        var (host, gain) = Setup();
        gain.BeginEdit();
        gain.NormalizedEditValue = 0.25;
        gain.NormalizedEditValue = 0.5;
        gain.BeginEdit(); // ignored
        gain.EndEdit();
        gain.EndEdit();   // ignored
        Assert.Equal(new[] { "begin 0", "perform 0 0.25", "perform 0 0.5", "end 0" }, host.Calls);
        Assert.False(gain.IsEditing);
    }

    [Fact]
    public void A_lone_edit_is_its_own_gesture()
    {
        var (host, gain) = Setup();
        gain.NormalizedEditValue = 0.5;
        Assert.Equal(new[] { "begin 0", "perform 0 0.5", "end 0" }, host.Calls);
    }

    [Fact]
    public void Host_values_are_not_reported_back_but_notify_the_UI()
    {
        var (host, gain) = Setup();
        var changed = new List<string>();
        gain.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        gain.SetNormalizedValueFromHost(0.0);
        Assert.Empty(host.Calls);
        Assert.True(double.IsNegativeInfinity(gain.EditValue));
        Assert.Equal(0.0, gain.NormalizedEditValue);
        Assert.Contains("NormalizedEditValue", changed);
        Assert.Contains("EditValue", changed);
        Assert.Contains("DisplayValue", changed);
    }

    [Fact]
    public void Normalized_default_value()
    {
        var gain = new DecibelParameter { ID = "gain", Name = "Gain", MaxValue = 12 };
        Assert.Equal(gain.GetValueNormalized(0), gain.NormalizedDefaultValue);
        Assert.InRange(gain.NormalizedDefaultValue, 0.01, 0.99);
    }
}
