using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioPluginProcessor
    {
        AudioIOPort[] InputPorts { get; }
        AudioIOPort[] OutputPorts { get; }
        IReadOnlyList<AudioPluginParameter> Parameters { get; }

        void Initialize();
        void AddParameter(AudioPluginParameter parameter);
        AudioPluginParameter GetParameter(string paramID);
        byte[] SaveState();
        void RestoreState(byte[] stateData);

        void InitializeProcessing();

        void Start();
        void Stop();

        void Process();
    }
}
