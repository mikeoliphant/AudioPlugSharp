using System;
using AudioPlugSharp;

namespace ExamplePlugin
{
    public class ExamplePlugin : AudioPluginBase
    {
        public ExamplePlugin()
        {
            Company = "My Company";
            Website = "www.mywebsite.com";
            Contact = "contact@my.email";
            PluginName = "Example Plugin";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0xF57703946AFC4EF8;
        }

        AudioIOPort monoInput;
        AudioIOPort monoOutput;

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { monoInput = new AudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { monoOutput = new AudioIOPort("Mono Output", EAudioChannelConfiguration.Mono) };

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
        }

        public override void InitializeEditor()
        {
            base.InitializeEditor();
        }

        public override void Start()
        {
            base.Start();
        }

        public override void Stop()
        {
            base.Stop();
        }

        public override void Process()
        {
            base.Process();

            double gain = GetParameter("gain").Value;

            Logger.Log("gain is: " + gain);

            double linearGain = Math.Pow(10.0, 0.05 * gain);

            monoInput.ReadData();

            double[] inSamples = monoInput.GetAudioBuffers()[0];
            double[] outSamples = monoOutput.GetAudioBuffers()[0];

            for (int i = 0; i < inSamples.Length; i++)
            {
                outSamples[i] = inSamples[i] * linearGain;
            }

            monoOutput.WriteData();
        }
    }
}
