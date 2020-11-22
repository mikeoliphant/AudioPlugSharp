using System;
using System.Collections.Generic;
using System.Text;
using AudioPlugSharp;

namespace ExamplePlugin
{
    public class ExampleProcessor : IAudioProcessor
    {
        public AudioIOPort[] InputPorts { get; private set; }
        public AudioIOPort[] OutputPorts { get; private set; }

        AudioIOPort monoInput;
        AudioIOPort monoOutput;

        public void Initialize()
        {
            Logger.Log("Initializing processor");

            InputPorts = new AudioIOPort[] { monoInput = new AudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { monoOutput = new AudioIOPort("Mono Output", EAudioChannelConfiguration.Mono) };
        }

        public void Start()
        {
            Logger.Log("Start Processor");
        }

        public void Stop()
        {
            Logger.Log("Stop Processor");
        }

        public void Process()
        {
            monoInput.ReadData();

            double[] inSamples = monoInput.GetAudioBuffers()[0];
            double[] outSamples = monoOutput.GetAudioBuffers()[0];

            for (int i = 0; i < inSamples.Length; i++)
            {
                outSamples[i] = inSamples[i] * 0.5;
            }

            monoOutput.WriteData();
        }
    }
}
