using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioPluginProcessor
    {
        AudioIOPort[] InputPorts { get; }
        AudioIOPort[] OutputPorts { get; }
        EAudioBitsPerSample SampleFormatsSupported { get; }
        IReadOnlyList<AudioPluginParameter> Parameters { get; }

        void Initialize();
        void AddParameter(AudioPluginParameter parameter);
        AudioPluginParameter GetParameter(string paramID);
        AudioPluginParameter GetParameterByMidiController(uint ccNumber);
        byte[] SaveState();
        void RestoreState(byte[] stateData);

        void InitializeProcessing();

        void Start();
        void Stop();

        void HandleParameterChange(AudioPluginParameter parameter, double newNormalizedValue, int sampleOffset);
        void HandleNoteOn(int channel, int noteNumber, float velocity, int sampleOffset);
        void HandleNoteOff(int channel, int noteNumber, float velocity, int sampleOffset);
        void HandlePolyPressure(int channel, int noteNumber, float pressure, int sampleOffset);

        void Process();
    }
}
