using System;
using System.Collections.Generic;
using System.Text;
using AudioPlugSharp;

namespace ExamplePlugin
{
    public class ExampleProcessor : IAudioProcessor
    {
        public List<AudioIOPort> InputPorts { get; private set; }

        public List<AudioIOPort> OutputPorts { get; private set; }

        public void Initialize()
        {
            InputPorts = new List<AudioIOPort>();

            InputPorts.Add(new AudioIOPort { Name = "Mono Input", ChannelConfiguration = EAudioChannelConfiguration.Mono });
            OutputPorts.Add(new AudioIOPort { Name = "Mono Output", ChannelConfiguration = EAudioChannelConfiguration.Mono });
        }

        public void Process(IntPtr inputs, IntPtr outputs, uint numSamples)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            throw new NotImplementedException();
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }
    }
}
