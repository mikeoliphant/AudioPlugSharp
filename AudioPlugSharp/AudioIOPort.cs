using System;
using System.Runtime.InteropServices;

namespace AudioPlugSharp
{
    public enum EAudioChannelConfiguration
    {
        Mono,
        Stereo
    }

    [Flags]
    public enum EAudioBitsPerSample
    {
        BitsNone = 0,
        Bits32 = 1,
        Bits64 = 2,
    }

    /// <summary>
    /// Manages audio data input and output
    /// </summary>
    public class AudioIOPort
    {
        /// <summary>
        /// The name of the audio port
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The channel configuration (currently Mono or Stereo
        /// </summary>
        public EAudioChannelConfiguration ChannelConfiguration { get; private set;  }

        /// <summary>
        /// The current size of our managed buffer in samples
        /// </summary>
        public uint CurrentBufferSize
        {
            get { return currentBufferSize; }
        }

        /// <summary>
        /// Create an audio I/O port
        /// </summary>
        /// <param name="name">The port name</param>
        /// <param name="channelConfiguration">The port channel configuration</param>
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

        /// <summary>
        /// Set the unmanaged pointers for the port (called by the host)
        /// </summary>
        /// <param name="ptrs">The unmanaged buffer pointers</param>
        /// <param name="bitsPerSample">The number of bits in each sample (current 32 or 64)</param>
        /// <param name="numSamples">The number of samples in the buffers</param>
        public void SetAudioBufferPtrs(IntPtr ptrs, EAudioBitsPerSample bitsPerSample, uint numSamples)
        {
            audioBufferPtrs = ptrs;
            this.bitsPerSample = bitsPerSample;
            this.currentBufferSize = numSamples;

            SyncPointers();
        }

        /// <summary>
        /// Get the unmanaged audio pointers
        /// </summary>
        /// <returns>An IntPtr to the unmanaged audio buffers</returns>
        public IntPtr GetAudioBufferPtrs()
        {
            return audioBufferPtrs;
        }

        /// <summary>
        /// Resizes our internal managed buffers, if necessary
        /// </summary>
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

        /// <summary>
        /// Reads the data from unmanaged memory to managed memory
        /// </summary>
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

        /// <summary>
        /// Writes the data from managed memory to unmanaged memory
        /// </summary>
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

        /// <summary>
        /// Gets the managed audio buffers
        /// </summary>
        /// <returns>An array of arrays of double</returns>
        public double[][] GetAudioBuffers()
        {
            return audioBuffers;
        }

        /// <summary>
        /// Pass through unmanaged data from this buffer to another buffer
        /// </summary>
        /// <param name="destinationPort">The port to copy data to</param>
        public unsafe void PassThroughTo(AudioIOPort destinationPort)
        {
            if (destinationPort.CurrentBufferSize != CurrentBufferSize)
                throw new InvalidOperationException("Destination port does not have the same size");

            void** ptrs = (void**)audioBufferPtrs;
            void** destPtrs = (void**)destinationPort.GetAudioBufferPtrs();

            uint length = currentBufferSize * (uint)((bitsPerSample == EAudioBitsPerSample.Bits32) ? 4 : 8);

            for (int channel = 0; channel < numChannels; channel++)
            {
                Buffer.MemoryCopy(ptrs[channel], destPtrs[channel], length, length);
            }
        }
    }
}
