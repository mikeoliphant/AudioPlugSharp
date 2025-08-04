using System;
using AudioPlugSharp;

namespace SimpleExample
{
    public class SimpleExamplePlugin : AudioPluginBase
    {
        public SimpleExamplePlugin()
        {
            Company = "My Company";
            Website = "www.mywebsite.com";
            Contact = "contact@my.email";
            PluginName = "Simple Gain Plugin";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0xF57703946AFC4EF8;

            SampleFormatsSupported = EAudioBitsPerSample.Bits32;
        }

        FloatAudioIOPort monoInput;
        FloatAudioIOPort monoOutput;

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { monoInput = new FloatAudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { monoOutput = new FloatAudioIOPort("Mono Output", EAudioChannelConfiguration.Mono) };

            AddParameter(new DecibelParameter
            {
                ID = "gain",
                Name = "Gain",
                MaxValue = 12,
                ValueFormat = "{0:0.0}dB"
            });
        }

        public override void Process()
        {
            base.Process();

            // This will trigger all Midi note events and parameter changes that happened during this process window
            // For sample-accurate tracking, see the WPFExample or MidiExample plugins
            Host.ProcessAllEvents();

            double gainDb = GetParameter("gain").ProcessValue;
            float linearGain = (float)AudioPluginParameter.DBToLinear(gainDb);

            ReadOnlySpan<float> inSamples = monoInput.GetAudioBuffer(0);
            Span<float> outSamples = monoOutput.GetAudioBuffer(0);

            for (int i = 0; i < inSamples.Length; i++)
            {
                outSamples[i] = inSamples[i] * linearGain;
            }
        }
    }
}
