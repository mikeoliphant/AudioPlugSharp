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
        EAudioBitsPerSample BitsPerSample { get; }
        double BPM { get; }

        void SendNoteOn(int noteNumber, float velocity);
        void SendNoteOff(int noteNumber, float velocity);
        void SendPolyPressure(int noteNumber, float pressure);

        void BeginEdit(int parameter);
        void PerformEdit(int parameter, double normalizedValue);
        void EndEdit(int parameter);
    }
}
