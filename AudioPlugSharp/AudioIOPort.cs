using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

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
        public EAudioChannelConfiguration ChannelConfiguration { get; private set; }

        /// <summary>
        /// The number of audio channels
        /// </summary>
        public uint NumChannels
        {
            get { return (uint)((ChannelConfiguration == EAudioChannelConfiguration.Mono) ? 1 : 2); }
        }

        /// <summary>
        /// The current size of our managed buffer in samples
        /// </summary>
        public uint CurrentBufferSize
        {
            get { return currentBufferSize; }
        }

        /// <summary>
        /// The number of bits per sample
        /// </summary>
        public EAudioBitsPerSample BitsPerSample
        {
            get { return bitsPerSample; }
        }

        /// <summary>
        /// Create an audio I/O port
        /// </summary>
        /// <param name="name">The port name</param>
        /// <param name="channelConfiguration">The port channel configuration</param>
        /// <param name="forceCopy">Whether to force having a managed copy of the sample data</param>
        public AudioIOPort(string name, EAudioChannelConfiguration channelConfiguration, bool forceCopy = false)
        {
            this.Name = name;
            this.ChannelConfiguration = channelConfiguration;
            this.forceCopy = forceCopy;

            numChannels = (channelConfiguration == EAudioChannelConfiguration.Mono) ? 1 : 2;
            audioBuffers = new double[numChannels][];
        }

        int numChannels;
        IntPtr audioBufferPtrs = IntPtr.Zero;
        double[][] audioBuffers;
        EAudioBitsPerSample bitsPerSample;
        uint currentBufferSize = 0;
        bool forceCopy;
        bool doCopy;

        /// <summary>
        /// Sets the maximum number of samples that will be processed, and the number of bits per sample
        /// </summary>
        /// <param name="maxSamples">The maximum number of samples that will be processed in a block</param>
        /// <param name="bitsPerSample">The number of bits in each sample (current 32 or 64)</param>
        public void SetMaxSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
        {
            this.bitsPerSample = bitsPerSample;

            Logger.Log("Port [" + Name + "] max sample size is: " + maxSamples + ", bits per sample is: " + bitsPerSample);

            if (bitsPerSample != EAudioBitsPerSample.Bits64)
            {
                CreateBackingBuffer(maxSamples);
            }

            if (forceCopy)
            {
                int size = 0;

                if (audioBuffers[0] != null)
                    size = audioBuffers[0].Length;

                if (size != maxSamples)
                {
                    for (int i = 0; i < numChannels; i++)
                    {
                        Array.Resize(ref audioBuffers[i], (int)maxSamples);
                    }
                }
            }
        }

        unsafe void CreateBackingBuffer(uint numSamples)
        {
            if (audioBufferPtrs != IntPtr.Zero)
            {
                for (int i = 0; i < numChannels; i++)
                {
                    IntPtr channelPtr = (IntPtr)((double**)audioBufferPtrs)[i];

                    Marshal.FreeHGlobal(channelPtr);
                }

                Marshal.FreeHGlobal(audioBufferPtrs);
            }

            audioBufferPtrs = Marshal.AllocHGlobal(numChannels * Marshal.SizeOf(typeof(IntPtr)));

            double** bufferPtrs = (double **)audioBufferPtrs;

            for (int i = 0; i < numChannels; i++)
            {
                bufferPtrs[i] = (double *)Marshal.AllocHGlobal((int)numSamples * sizeof(double));
            }
        }

        /// <summary>
        /// Set the current audio buffer size
        /// </summary>
        /// <param name="numSamples">The number of samples per processing cycle</param>
        public void SetCurrentBufferSize(uint numSamples)
        {
            this.currentBufferSize = numSamples;
        }

        /// <summary>
        /// Set the unmanaged pointers for the port (called by the host)
        /// </summary>
        /// <param name="ptrs">The unmanaged buffer pointers</param>
        public unsafe void SetAudioBufferPtrs(IntPtr ptrs)
        {
            if (bitsPerSample == EAudioBitsPerSample.Bits64)
            {
                audioBufferPtrs = ptrs;
            }
            else
            {
                // We need to convert float samples to double
                for (int i = 0; i < numChannels; i++)
                {
                    float* floatPtr = ((float**)ptrs)[i];
                    double* dblPtr = ((double**)audioBufferPtrs)[i];

                    for (int s = 0; s < this.currentBufferSize; s++)
                    {
                        dblPtr[s] = floatPtr[s];
                    }
                }
            }
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
        /// Reads the data from unmanaged memory to managed memory
        /// </summary>
        internal unsafe void ReadData()
        {
            if (!doCopy || (audioBufferPtrs == IntPtr.Zero))
                return;

            for (int i = 0; i < numChannels; i++)
            {
                if (bitsPerSample == EAudioBitsPerSample.Bits32)
                {
                    float* bufferPtr = ((float**)audioBufferPtrs)[i];
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
        internal unsafe void WriteData()
        {
            if (!doCopy || (audioBufferPtrs == IntPtr.Zero))
                return;

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
        /// Gets the audio buffer for a channel
        /// </summary>
        /// <returns>The sample array for the channel</returns>
        public unsafe Span<double> GetAudioBuffer(int channel)
        {
            if (!doCopy)
                return new Span<double>((void*)((double**)audioBufferPtrs)[channel], (int)currentBufferSize);

            return audioBuffers[channel];
        }

        /// <summary>
        /// Get a managed copy of the audio buffers (requires "forceCopy" in constructor)
        /// </summary>
        /// <returns>The managed audio buffer arrays</returns>
        public double[][] GetAudioBuffers()
        {
            if (!doCopy)
                throw new InvalidOperationException("Managed buffers only available if \"forceCopy\" is true constructor");

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

            if (doCopy != destinationPort.doCopy)
            {
                throw new InvalidOperationException("Pass through only supported when \"forceCopy\" is the same for source and destination");
            }

            if (doCopy)
            {
                for (int channel = 0; channel < numChannels; channel++)
                {
                    audioBuffers[channel].CopyTo(destinationPort.audioBuffers[channel], 0);
                }
            }
            else
            {
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
}
