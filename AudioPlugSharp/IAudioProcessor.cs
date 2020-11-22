using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioProcessor
    {
        AudioIOPort[] InputPorts { get; }
        AudioIOPort[] OutputPorts { get; }

        void Initialize();
        void Start();
        void Stop();
        void Process();
    }
}
