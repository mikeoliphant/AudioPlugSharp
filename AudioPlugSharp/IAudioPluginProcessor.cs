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

        [Realtime]
        void HandleParameterChange(AudioPluginParameter parameter, double newNormalizedValue, int sampleOffset);
        [Realtime]
        void HandleNoteOn(int channel, int noteNumber, float velocity, int sampleOffset);
        [Realtime]
        void HandleNoteOff(int channel, int noteNumber, float velocity, int sampleOffset);
        [Realtime]
        void HandlePolyPressure(int channel, int noteNumber, float pressure, int sampleOffset);

        void SetMaxAudioBufferSize(uint maxSamples, EAudioBitsPerSample bitsPerSample);
        [Realtime]
        void PreProcess();
        [Realtime]
        void Process();
        [Realtime]
        void PostProcess();
    }
}
