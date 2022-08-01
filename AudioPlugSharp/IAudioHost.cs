using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioPlugSharp
{
    public interface IAudioHost
    {
        double SampleRate { get; }
        UInt32 MaxAudioBufferSize { get; }
        UInt32 CurrentAudioBufferSize { get; }
        EAudioBitsPerSample BitsPerSample { get; }
        double BPM { get; }
        UInt64 CurrentProjectSample { get; }
        bool IsPlaying { get; }

        void SendNoteOn(int noteNumber, float velocity, int sampleOffset);
        void SendNoteOff(int noteNumber, float velocity, int sampleOffset);
        void SendCC(int ccNumber, int ccValue, int sampleOffset);
        void SendPolyPressure(int noteNumber, float pressure, int sampleOffset);

        void ProcessAllEvents();
        int ProcessEvents();

        void BeginEdit(int parameter);
        void PerformEdit(int parameter, double normalizedValue);
        void EndEdit(int parameter);
    }
}
