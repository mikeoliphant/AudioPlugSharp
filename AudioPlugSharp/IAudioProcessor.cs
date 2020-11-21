using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioProcessor
    {
        List<AudioIOPort> InputPorts { get; }
        List<AudioIOPort> OutputPorts { get; }

        void Initialize();
        void Start();
        void Stop();
        void Process(IntPtr inputs, IntPtr outputs, uint numSamples);
    }
}
