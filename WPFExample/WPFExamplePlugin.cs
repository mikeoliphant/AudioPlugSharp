using System;
using System.Windows.Controls;
using AudioPlugSharp;
using AudioPlugSharpWPF;

namespace WPFExample
{
    public class WPFExamplePlugin : AudioPluginWPF
    {
        DoubleAudioIOPort monoInput;
        DoubleAudioIOPort stereoOutput;

        AudioPluginParameter gainParameter = null;
        AudioPluginParameter panParameter = null;

        public WPFExamplePlugin()
        {
            Company = "My Company";
            Website = "www.mywebsite.com";
            Contact = "contact@my.email";
            PluginName = "WPF Example Plugin";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0x1E92758E710B4947;

            HasUserInterface = true;
            EditorWidth = 200;
            EditorHeight = 100;
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { monoInput = new DoubleAudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { stereoOutput = new DoubleAudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };

            AddParameter(gainParameter = new AudioPluginParameter
            {
                ID = "gain",
                Name = "Gain",
                Type = EAudioPluginParameterType.Float,
                MinValue = -20,
                MaxValue = 20,
                DefaultValue = 0,
                ValueFormat = "{0:0.0}dB"
            });

            AddParameter(panParameter = new AudioPluginParameter
            {
                ID = "pan",
                Name = "Pan",
                Type = EAudioPluginParameterType.Float,
                MinValue = -1,
                MaxValue = 1,
                DefaultValue = 0,
                ValueFormat = "{0:0.0}"
            });
        }

        public override void Process()
        {
            base.Process();

            ReadOnlySpan<double> inSamples = monoInput.GetAudioBuffer(0);

            Span<double> outLeftSamples = stereoOutput.GetAudioBuffer(0);
            Span<double> outRightSamples = stereoOutput.GetAudioBuffer(1);

            int currentSample = 0;
            int nextSample = 0;

            double linearGain = AudioPluginParameter.DbToLinear(gainParameter.ProcessValue);
            double pan = panParameter.ProcessValue;

            do
            {
                nextSample = Host.ProcessEvents();  // Handle sample-accurate parameters - see the SimpleExample plugin for a simpler, per-buffer parameter approach

                bool needGainUpdate = gainParameter.NeedInterpolationUpdate;
                bool needPanUpdate = panParameter.NeedInterpolationUpdate;

                for (int i = currentSample; i < nextSample; i++)
                {
                    if (needGainUpdate)
                    {
                        linearGain = AudioPluginParameter.DbToLinear(gainParameter.GetInterpolatedProcessValue(i));
                    }

                    if (needPanUpdate)
                    {
                        pan = panParameter.GetInterpolatedProcessValue(i);
                    }

                    outLeftSamples[i] = inSamples[i] * linearGain * (1 - pan);
                    outRightSamples[i] = inSamples[i] * linearGain * (1 + pan);
                }

                currentSample = nextSample;
            }
            while (nextSample < inSamples.Length); // Continue looping until we hit the end of the buffer
        }
    }
}
