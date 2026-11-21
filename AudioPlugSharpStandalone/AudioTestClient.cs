using AudioIOSharp;

namespace AudioPlugSharpStandalone;

/// <summary>What the audio test plays: a sine tone, or the sum of the inputs.</summary>
public enum AudioTestSignal
{
    Tone,
    Inputs,
}

/// <summary>
/// The settings panel's audio test (as SpiceForge's): a 440 Hz tone on every output, or the sum of the inputs looped
/// back to them, with each channel's peak for the meters. Runs on the device in place of the plugin. The output fades
/// in, and <see cref="FadeOut"/> fades it out, so the test starts and stops without a click.
/// </summary>
public class AudioTestClient : IAudioClient
{
    public const double ToneHz = 440;

    /// <summary>The tone's level: −18 dBFS, loud enough to hear and well clear of clipping.</summary>
    public static readonly float ToneAmplitude = (float)Math.Pow(10, -18 / 20.0);

    const double FadeSeconds = 0.02;

    readonly AudioTestSignal signal;
    volatile AudioFormat format = new(48000, 0, [], []);
    double sampleRate = 48000;
    float[] inputPeaks = [], outputPeaks = [];

    // The audio thread's: the tone's phase (in cycles) and the fade's position, 0 (silent) to 1 (full).
    double phase, fade;
    volatile bool fadingOut;
    int silentBlocks;
    readonly ManualResetEventSlim silent = new();

    public AudioTestClient(AudioTestSignal signal)
    {
        this.signal = signal;
    }

    public AudioTestSignal Signal => signal;

    /// <summary>The device's open input channels, in the order of the peaks.</summary>
    public IReadOnlyList<string> InputNames => format.Inputs;

    /// <summary>The device's open output channels, in the order of the peaks.</summary>
    public IReadOnlyList<string> OutputNames => format.Outputs;

    public void Prepare(AudioFormat format)
    {
        sampleRate = format.SampleRate > 0 ? format.SampleRate : 48000;

        inputPeaks = new float[format.Inputs.Count];
        outputPeaks = new float[format.Outputs.Count];

        this.format = format;
    }

    public void Process(in AudioBuffers buffers)
    {
        int frames = buffers.Frames;
        double step = ToneHz / sampleRate, fadeStep = 1 / (FadeSeconds * sampleRate);
        bool fadingOut = this.fadingOut, wasSilent = fade <= 0;

        // Written to the first output, then copied to the others.
        Span<float> first = buffers.OutputCount > 0 ? buffers.Output(0) : Span<float>.Empty;
        float outPeak = 0;

        for (int n = 0; n < frames; n++)
        {
            float sum = 0;

            for (int i = 0; i < buffers.InputCount; i++)
                sum += buffers.Input(i)[n];

            fade = fadingOut ? Math.Max(0, fade - fadeStep) : Math.Min(1, fade + fadeStep);
            float gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * fade));

            float output;

            if (signal == AudioTestSignal.Tone)
            {
                output = gain * ToneAmplitude * (float)Math.Sin(2 * Math.PI * phase);

                phase += step;
                if (phase >= 1) phase -= 1;
            }
            else
            {
                output = gain * sum;
            }

            if (first.Length > 0) first[n] = output;

            outPeak = Math.Max(outPeak, Math.Abs(output));
        }

        for (int o = 1; o < buffers.OutputCount; o++)
            first.CopyTo(buffers.Output(o));

        // Two whole blocks of silence after the fade-out: a device plays one block while the next is filled.
        if (fadingOut && wasSilent && ++silentBlocks >= 2)
            silent.Set();

        float[] inPeaks = inputPeaks, outPeaks = outputPeaks;

        for (int i = 0; i < buffers.InputCount && i < inPeaks.Length; i++)
        {
            float peak = 0;

            foreach (float x in buffers.Input(i))
                peak = Math.Max(peak, Math.Abs(x));

            if (peak > Volatile.Read(ref inPeaks[i])) Volatile.Write(ref inPeaks[i], peak);
        }

        for (int o = 0; o < outPeaks.Length; o++)
            if (outPeak > Volatile.Read(ref outPeaks[o])) Volatile.Write(ref outPeaks[o], outPeak);
    }

    /// <summary>[UI thread] Each input's peak since the last call (0 to 1, and over 1 when clipping).</summary>
    public void TakeInputPeaks(List<float> peaks) => Take(inputPeaks, peaks);

    /// <summary>[UI thread] Each output's peak since the last call.</summary>
    public void TakeOutputPeaks(List<float> peaks) => Take(outputPeaks, peaks);

    static void Take(float[] source, List<float> peaks)
    {
        peaks.Clear();

        for (int i = 0; i < source.Length; i++)
            peaks.Add(Interlocked.Exchange(ref source[i], 0));
    }

    /// <summary>Fades the output out, and returns once it's silent, or after <paramref name="timeout"/> if the device
    /// has stopped calling.</summary>
    public void FadeOut(TimeSpan timeout)
    {
        fadingOut = true;

        silent.Wait(timeout);
    }
}
