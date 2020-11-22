using System;
using System.Runtime.InteropServices;

namespace AudioPlugSharp
{
    public enum EAudioChannelConfiguration
    {
        Mono,
        Stereo
    }

    public enum EAudioBitsPerSample
    {
        Bits32,
        Bits64
    }

    public class AudioIOPort
    {
        public string Name { get; set; }
        public EAudioChannelConfiguration ChannelConfiguration { get; private set;  }

        public AudioIOPort(string name, EAudioChannelConfiguration channelConfiguration)
        {
            this.Name = name;
            this.ChannelConfiguration = channelConfiguration;

            numChannels = (channelConfiguration == EAudioChannelConfiguration.Mono) ? 1 : 2;
            audioBuffers = new double[numChannels][];
        }

        int numChannels;
        IntPtr audioBufferPtrs = IntPtr.Zero;
        double[][] audioBuffers;
        EAudioBitsPerSample bitsPerSample;
        uint currentBufferSize = 0;

        public void SetAudioBufferPtrs(IntPtr ptrs, EAudioBitsPerSample bitsPerSample, uint numSamples)
        {
            audioBufferPtrs = ptrs;
            this.bitsPerSample = bitsPerSample;
            this.currentBufferSize = numSamples;

            SyncPointers();
        }

        public IntPtr GetAudioBufferPtrs()
        {
            return audioBufferPtrs;
        }

        unsafe void SyncPointers()
        {
            int size = 0;

            if (audioBuffers[0] != null)
                size = audioBuffers[0].Length;

            if (size != currentBufferSize)
            {
                for (int i = 0; i < numChannels; i++)
                {
                    Array.Resize(ref audioBuffers[i], (int)currentBufferSize);
                }
            }
        }

        public unsafe void ReadData()
        {
            for (int i = 0; i < numChannels; i++)
            {
                if (bitsPerSample == EAudioBitsPerSample.Bits32)
                {
                    float *bufferPtr = ((float**)audioBufferPtrs)[i];
                    double[] audioBuffer = audioBuffers[i];

                    for (int sample = 0; sample < currentBufferSize; sample++)
                    {
                        audioBuffer[sample] = bufferPtr[sample];
                    }
                }
                else
                {
                    IntPtr bufferPtr = (IntPtr)((double**)audioBufferPtrs)[i];

                    Marshal.Copy(bufferPtr, audioBuffers[i], 0, (int)currentBufferSize);
                }
            }
        }

        public unsafe void WriteData()
        {
            for (int i = 0; i < numChannels; i++)
            {
                if (bitsPerSample == EAudioBitsPerSample.Bits32)
                {
                    float* bufferPtr = ((float**)audioBufferPtrs)[i];
                    double[] audioBuffer = audioBuffers[i];

                    for (int sample = 0; sample < currentBufferSize; sample++)
                    {
                        bufferPtr[sample] = (float)audioBuffer[sample];
                    }
                }
                else
                {
                    IntPtr bufferPtr = (IntPtr)((double**)audioBufferPtrs)[i];

                    Marshal.Copy(audioBuffers[i], 0, bufferPtr, (int)currentBufferSize);
                }
            }
        }

        public double[][] GetAudioBuffers()
        {
            return audioBuffers;
        }
    }
}
