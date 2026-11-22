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
    /// A stereo drive whose editor is generated from its parameters: it derives from AudioPluginUILayout and doesn't
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

        // The tone filter's state per channel (a one-pole low-pass)
        float[] lowPass = new float[2];

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

            InputPorts = new AudioIOPort[] { input = new FloatAudioIOPort("Stereo Input", EAudioChannelConfiguration.Stereo) };
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

            // Parameter changes in the block (one value per block is enough for this example)
            Host.ProcessAllEvents();

            for (int channel = 0; channel < 2; channel++)
            {
                ReadOnlySpan<float> inSamples = input.GetAudioBuffer(channel);
                Span<float> outSamples = output.GetAudioBuffer(channel);

                if (bypass.IsOn)
                {
                    inSamples.CopyTo(outSamples);

                    continue;
                }

                float driveGain = (float)AudioPluginParameter.DBToLinear(drive.ProcessValue);
                float outGain = (float)AudioPluginParameter.DBToLinear(outputGain.ProcessValue);
                float toneCoefficient = 1 - MathF.Exp(-2 * MathF.PI * (float)tone.ProcessValue / (float)Host.SampleRate);
                DriveMode shape = mode.Value;
                float state = lowPass[channel];

                for (int i = 0; i < inSamples.Length; i++)
                {
                    float driven = Shape(inSamples[i] * driveGain, shape);

                    state += toneCoefficient * (driven - state);

                    outSamples[i] = state * outGain;
                }

                lowPass[channel] = state;
            }
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
