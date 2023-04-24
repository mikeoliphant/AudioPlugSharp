using System;
using System.Windows.Controls;
using AudioPlugSharp;
using AudioPlugSharpWPF;

namespace WPFExample
{
    public class WPFExamplePlugin : AudioPluginWPF
    {
        AudioIOPort monoInput;
        AudioIOPort stereoOutput;

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

            InputPorts = new AudioIOPort[] { monoInput = new AudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { stereoOutput = new AudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };

            AddParameter(new AudioPluginParameter
            {
                ID = "gain",
                Name = "Gain",
                Type = EAudioPluginParameterType.Float,
                MinValue = -20,
                MaxValue = 20,
                DefaultValue = 0,
                ValueFormat = "{0:0.0}dB"
            });

            AddParameter(new AudioPluginParameter
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

            // Read our input into managed data
            monoInput.ReadData();

            double[] inSamples = monoInput.GetAudioBuffers()[0];

            double[] outLeftSamples = stereoOutput.GetAudioBuffers()[0];
            double[] outRightSamples = stereoOutput.GetAudioBuffers()[1];

            int currentSample = 0;
            int nextSample = 0;

            do
            {
                nextSample = Host.ProcessEvents();  // Handle sample-accurate parameters

                double gain = GetParameter("gain").ProcessValue;
                double linearGain = Math.Pow(10.0, 0.05 * gain);

                double pan = GetParameter("pan").ProcessValue;

                for (int i = currentSample; i < nextSample; i++)
                {
                    outLeftSamples[i] = inSamples[i] * linearGain * (1 - pan);
                    outRightSamples[i] = inSamples[i] * linearGain * (1 + pan);
                }

                currentSample = nextSample;
            }
            while (nextSample < inSamples.Length); // Continue looping until we hit the end of the buffer


            // Write out our managed audio data
            stereoOutput.WriteData();
        }
    }
}
