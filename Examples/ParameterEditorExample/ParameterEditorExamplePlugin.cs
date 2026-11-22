using System;
using AudioPlugSharp;
using AudioPlugSharpUILayout;

namespace ParameterEditorExample
{
    /// <summary>The drive's shapes: the Mode parameter's choices (EnumParameter names them from the members).</summary>
    public enum DriveMode
    {
        Soft,
        Hard,
        Fold,
    }

    /// <summary>
    /// A drive, mono in and stereo out, whose editor is generated from its parameters: it derives from AudioPluginUILayout and doesn't
    /// override CreateEditorUI, so each parameter gets the control that suits it (knobs for Drive, Tone and Output, a
    /// drop-down for the Mode choices, a switch for Bypass), and the editor is sized to fit them.
    /// </summary>
    public class ParameterEditorExamplePlugin : AudioPluginUILayout
    {
        FloatAudioIOPort input;
        FloatAudioIOPort output;
        AudioPluginParameter drive;
        AudioPluginParameter tone;
        EnumParameter<DriveMode> mode;
        BoolParameter bypass;
        DecibelParameter outputGain;

        // The tone filter's state (a one-pole low-pass)
        float lowPass;

        public ParameterEditorExamplePlugin()
        {
            Company = "AudioPlugSharp";
            Website = "github.com/mikeoliphant/AudioPlugSharp";
            Contact = "";
            PluginName = "Parameter Editor Drive";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0x5E3A9D17C4B28F06;

            SampleFormatsSupported = EAudioBitsPerSample.Bits32;
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { input = new FloatAudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { output = new FloatAudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };

            AddParameter(drive = new AudioPluginParameter
            {
                ID = "drive",
                Name = "Drive",
                MinValue = 0,
                MaxValue = 30,
                DefaultValue = 6,
                ValueFormat = "{0:0.0}dB",
            });

            AddParameter(tone = new AudioPluginParameter
            {
                ID = "tone",
                Name = "Tone",
                MinValue = 500,
                MaxValue = 20000,
                DefaultValue = 8000,
                RangePower = 0.3,
                ValueFormat = "{0:0}Hz",
            });

            AddParameter(mode = new EnumParameter<DriveMode>
            {
                ID = "mode",
                Name = "Mode",
            });

            AddParameter(bypass = new BoolParameter
            {
                ID = "bypass",
                Name = "Bypass",
            });

            AddParameter(outputGain = new DecibelParameter
            {
                ID = "output",
                Name = "Output",
                MaxValue = 6,
                DefaultValue = -6,
            });
        }

        public override void Process()
        {
            base.Process();

            ReadOnlySpan<float> inSamples = input.GetAudioBuffer(0);
            Span<float> outLeft = output.GetAudioBuffer(0);
            Span<float> outRight = output.GetAudioBuffer(1);

            float driveGain = (float)AudioPluginParameter.DBToLinear(drive.ProcessValue);
            float outGain = (float)AudioPluginParameter.DBToLinear(outputGain.ProcessValue);
            float toneCoefficient = ToneCoefficient(tone.ProcessValue);
            float state = lowPass;

            // Sample-accurate parameters: each stretch runs up to the host's next change point, and the continuous
            // parameters ramp toward it (a value that jumps once per block while a control moves is heard as crackle)
            int currentSample = 0;
            int nextSample;

            do
            {
                nextSample = Host.ProcessEvents();

                bool driveRamp = drive.NeedInterpolationUpdate;
                bool toneRamp = tone.NeedInterpolationUpdate;
                bool outputRamp = outputGain.NeedInterpolationUpdate;

                // The switches take their value for the stretch
                DriveMode shape = mode.Value;
                bool bypassed = bypass.IsOn;

                for (int i = currentSample; i < nextSample; i++)
                {
                    if (driveRamp)
                        driveGain = (float)AudioPluginParameter.DBToLinear(drive.GetInterpolatedProcessValue(i));

                    if (toneRamp)
                        toneCoefficient = ToneCoefficient(tone.GetInterpolatedProcessValue(i));

                    if (outputRamp)
                        outGain = (float)AudioPluginParameter.DBToLinear(outputGain.GetInterpolatedProcessValue(i));

                    // The same on both sides
                    if (bypassed)
                    {
                        outLeft[i] = outRight[i] = inSamples[i];

                        continue;
                    }

                    state += toneCoefficient * (Shape(inSamples[i] * driveGain, shape) - state);

                    outLeft[i] = outRight[i] = state * outGain;
                }

                currentSample = nextSample;
            }
            while (nextSample < inSamples.Length);

            lowPass = state;
        }

        // The one-pole low-pass's coefficient for a cutoff frequency
        float ToneCoefficient(double frequency)
        {
            return 1 - MathF.Exp(-2 * MathF.PI * (float)frequency / (float)Host.SampleRate);
        }

        static float Shape(float sample, DriveMode shape)
        {
            switch (shape)
            {
                case DriveMode.Soft:
                    return MathF.Tanh(sample);

                case DriveMode.Hard:
                    return Math.Clamp(sample, -1, 1);

                case DriveMode.Fold:
                    return MathF.Sin(sample);
            }

            return sample;
        }
    }
}
