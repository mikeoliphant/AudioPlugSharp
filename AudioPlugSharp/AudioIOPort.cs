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
        public AudioIOPort(string name, EAudioChannelConfiguration channelConfiguration)
        {
            this.Name = name;
            this.ChannelConfiguration = channelConfiguration;

            numChannels = (channelConfiguration == EAudioChannelConfiguration.Mono) ? 1 : 2;
        }

        protected virtual bool NeedBackingBuffer
        {
            get { throw new NotImplementedException(); }
        }

        int numChannels;
        EAudioBitsPerSample bitsPerSample;
        uint currentBufferSize = 0;
        bool doCopy;

        protected IntPtr hostAudioBufferPtrs = IntPtr.Zero;
        protected IntPtr backingAudioBufferPtrs = IntPtr.Zero;

        /// <summary>
        /// Sets the maximum number of samples that will be processed, and the number of bits per sample
        /// </summary>
        /// <param name="maxSamples">The maximum number of samples that will be processed in a block</param>
        /// <param name="bitsPerSample">The number of bits in each sample (current 32 or 64)</param>
        public virtual void SetMaxSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
        {
            this.bitsPerSample = bitsPerSample;

            Logger.Log("Port [" + Name + "] max sample size is: " + maxSamples + ", bits per sample is: " + bitsPerSample);

            if (NeedBackingBuffer)
            {
                CreateBackingBuffer(maxSamples);
            }
        }

        protected virtual void CreateBackingBuffer(uint numSamples)
        {
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
        public virtual void SetAudioBufferPtrs(IntPtr ptrs)
        {
        }

        /// <summary>
        /// Get the unmanaged audio pointers
        /// </summary>
        /// <returns>An IntPtr to the unmanaged audio buffers</returns>
        public IntPtr GetAudioBufferPtrs()
        {
            return backingAudioBufferPtrs;
        }

        public virtual void CopyFrom(ReadOnlySpan<float> buffer, int channel)
        {
        }

        public virtual void CopyFrom(ReadOnlySpan<Int32> buffer, int channel)
        {
        }

        public virtual void CopyFrom(ReadOnlySpan<double> buffer, int channel)
        {
        }

        public virtual void CopyTo(Span<float> buffer, int channel)
        {
        }

        public virtual void CopyTo(Span<Int32> buffer, int channel)
        {
        }

        public virtual void CopyTo(Span<double> buffer, int channel)
        {
        }

        /// <summary>
        /// Reads the data from the host pointers (if necessary)
        /// </summary>
        internal virtual void ReadData()
        {
        }

        /// <summary>
        /// Writes data to the host pointers (if necessary)
        /// </summary>
        internal virtual void WriteData()
        {
        }
    }

    public class AudioIOPort<T> : AudioIOPort
    {
        public AudioIOPort(string name, EAudioChannelConfiguration channelConfiguration)
            : base(name, channelConfiguration)
        {

        }

        protected override unsafe void CreateBackingBuffer(uint numSamples)
        {
            if (backingAudioBufferPtrs != IntPtr.Zero)
            {
                for (int i = 0; i < NumChannels; i++)
                {
                    IntPtr channelPtr = (IntPtr)((T**)backingAudioBufferPtrs)[i];

                    Marshal.FreeHGlobal(channelPtr);
                }

                Marshal.FreeHGlobal(backingAudioBufferPtrs);
            }

            backingAudioBufferPtrs = Marshal.AllocHGlobal((int)NumChannels * Marshal.SizeOf(typeof(IntPtr)));

            T** bufferPtrs = (T**)backingAudioBufferPtrs;

            for (int i = 0; i < NumChannels; i++)
            {
                bufferPtrs[i] = (T*)Marshal.AllocHGlobal((int)numSamples * sizeof(T));
            }
        }

        /// <summary>
        /// Gets the audio buffer for a channel
        /// </summary>
        /// <returns>The sample array for the channel</returns>
        public virtual unsafe Span<T> GetAudioBuffer(int channel)
        {
            return new Span<T>((void*)((T**)backingAudioBufferPtrs)[channel], (int)CurrentBufferSize);
        }

        /// <summary>
        /// Pass through unmanaged data from this buffer to another buffer
        /// </summary>
        /// <param name="destinationPort">The port to copy data to</param>
        public unsafe virtual void PassThroughTo(AudioIOPort<double> destinationPort)
        {
            if (destinationPort.CurrentBufferSize != CurrentBufferSize)
                throw new InvalidOperationException("Destination port does not have the same size");

            for (int i = 0; i < NumChannels; i++)
            {
                CopyTo(destinationPort.GetAudioBuffer(i), i);
            }
        }

        /// <summary>
        /// Pass through unmanaged data from this buffer to another buffer
        /// </summary>
        /// <param name="destinationPort">The port to copy data to</param>
        public unsafe virtual void PassThroughTo(AudioIOPort<float> destinationPort)
        {
            if (destinationPort.CurrentBufferSize != CurrentBufferSize)
                throw new InvalidOperationException("Destination port does not have the same size");

            for (int i = 0; i < NumChannels; i++)
            {
                CopyTo(destinationPort.GetAudioBuffer(i), i);
            }
        }
    }

    public class FloatAudioIOPort : AudioIOPort<float>
    {
        protected override bool NeedBackingBuffer
        {
            get { return (BitsPerSample != EAudioBitsPerSample.Bits32); }
        }

        public FloatAudioIOPort(string name, EAudioChannelConfiguration channelConfiguration)
            : base(name, channelConfiguration)
        {
        }

        public override void SetAudioBufferPtrs(IntPtr ptrs)
        {
            hostAudioBufferPtrs = ptrs;

            if (!NeedBackingBuffer)
            {
                backingAudioBufferPtrs = hostAudioBufferPtrs;
            }
        }

        public override void CopyFrom(ReadOnlySpan<float> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            Span<float> channelBuf = GetAudioBuffer(channel);

            buffer.CopyTo(channelBuf);
        }

        public override void CopyFrom(ReadOnlySpan<Int32> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            Span<float> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                channelBuf[i] = (float)buffer[i] / (float)Int32.MaxValue;
            }
        }

        public override void CopyFrom(ReadOnlySpan<double> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            Span<float> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                channelBuf[i] = (float)buffer[i];
            }
        }

        public override void CopyTo(Span<float> buffer, int channel)
        {
            ReadOnlySpan<float> channelBuf = GetAudioBuffer(channel);

            channelBuf.CopyTo(buffer);
        }

        public override void CopyTo(Span<Int32> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            ReadOnlySpan<float> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                buffer[i] = (Int32)(channelBuf[i] * Int32.MaxValue);
            }
        }

        public override void CopyTo(Span<double> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            ReadOnlySpan<float> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                buffer[i] = (float)channelBuf[i];
            }
        }

        internal override unsafe void ReadData()
        {
            if (NeedBackingBuffer && (hostAudioBufferPtrs != IntPtr.Zero))
            {
                // We need to convert double samples to float
                for (int i = 0; i < NumChannels; i++)
                {
                    float* floatPtr = ((float**)backingAudioBufferPtrs)[i];
                    double* dblPtr = ((double**)hostAudioBufferPtrs)[i];

                    for (int sample = 0; sample < CurrentBufferSize; sample++)
                    {
                        floatPtr[sample] = (float)dblPtr[sample];
                    }
                }
            }
        }

        /// <summary>
        /// Writes data to the host pointers (if necessary)
        /// </summary>
        internal override unsafe void WriteData()
        {
            if (NeedBackingBuffer && (hostAudioBufferPtrs != IntPtr.Zero))
            {
                // We need to convert float samples to back to float
                for (int i = 0; i < NumChannels; i++)
                {
                    float* floatPtr = ((float**)backingAudioBufferPtrs)[i];
                    double* dblPtr = ((double**)hostAudioBufferPtrs)[i];

                    for (int sample = 0; sample < CurrentBufferSize; sample++)
                    {
                        dblPtr[sample] = floatPtr[sample];
                    }
                }
            }
        }
    }

    public class DoubleAudioIOPort : AudioIOPort<double>
    {
        protected override bool NeedBackingBuffer
        {
            get { return (BitsPerSample != EAudioBitsPerSample.Bits64); }
        }


        public DoubleAudioIOPort(string name, EAudioChannelConfiguration channelConfiguration)
            : base(name, channelConfiguration)
        {
        }

        public override void SetAudioBufferPtrs(IntPtr ptrs)
        {
            hostAudioBufferPtrs = ptrs;

            if (!NeedBackingBuffer)
            {
                backingAudioBufferPtrs = hostAudioBufferPtrs;
            }
        }


        public override void CopyFrom(ReadOnlySpan<float> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            Span<double> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                channelBuf[i] = buffer[i];
            }
        }

        public override void CopyFrom(ReadOnlySpan<Int32> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            Span<double> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                channelBuf[i] = (float)buffer[i] / (float)Int32.MaxValue;
            }
        }

        public override void CopyFrom(ReadOnlySpan<double> buffer, int channel)
        {
            Span<double> channelBuf = GetAudioBuffer(channel);

            buffer.CopyTo(channelBuf);
        }

        public override void CopyTo(Span<float> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            ReadOnlySpan<double> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                buffer[i] = (float)channelBuf[i];
            }
        }

        public override void CopyTo(Span<Int32> buffer, int channel)
        {
            int toCopy = Math.Min(buffer.Length, (int)CurrentBufferSize);

            ReadOnlySpan<double> channelBuf = GetAudioBuffer(channel);

            for (int i = 0; i < toCopy; i++)
            {
                buffer[i] = (Int32)(channelBuf[i] * Int32.MaxValue);
            }
        }

        public override void CopyTo(Span<double> buffer, int channel)
        {
            ReadOnlySpan<double> channelBuf = GetAudioBuffer(channel);

            channelBuf.CopyTo(buffer);
        }


        internal override unsafe void ReadData()
        {
            if (NeedBackingBuffer && (hostAudioBufferPtrs != IntPtr.Zero))
            {
                // We need to convert float samples to double
                for (int i = 0; i < NumChannels; i++)
                {
                    float* floatPtr = ((float**)hostAudioBufferPtrs)[i];
                    double* dblPtr = ((double**)backingAudioBufferPtrs)[i];

                    for (int sample = 0; sample < CurrentBufferSize; sample++)
                    {
                        dblPtr[sample] = floatPtr[sample];
                    }
                }
            }
        }

        /// <summary>
        /// Writes data to the host pointers (if necessary)
        /// </summary>
        internal override unsafe void WriteData()
        {
            if (NeedBackingBuffer && (hostAudioBufferPtrs != IntPtr.Zero))
            {
                // We need to convert float samples to back to float
                for (int i = 0; i < NumChannels; i++)
                {
                    float* floatPtr = ((float**)hostAudioBufferPtrs)[i];
                    double* dblPtr = ((double**)backingAudioBufferPtrs)[i];

                    for (int sample = 0; sample < CurrentBufferSize; sample++)
                    {
                        floatPtr[sample] = (float)dblPtr[sample];
                    }
                }
            }
        }
    }

    public class AudioIOPortManaged : DoubleAudioIOPort
    {
        double[][] audioBuffers;

        public AudioIOPortManaged(string name, EAudioChannelConfiguration channelConfiguration)
            : base(name, channelConfiguration)
        {
            audioBuffers = new double[NumChannels][];
        }

        public override void SetMaxSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
        {
            base.SetMaxSize(maxSamples, bitsPerSample);

            int size = 0;

            if (audioBuffers[0] != null)
                size = audioBuffers[0].Length;

            if (size != maxSamples)
            {
                for (int i = 0; i < NumChannels; i++)
                {
                    Array.Resize(ref audioBuffers[i], (int)maxSamples);
                }
            }
        }

        unsafe internal override void ReadData()
        {
            for (int i = 0; i < NumChannels; i++)
            {
                if (BitsPerSample == EAudioBitsPerSample.Bits32)
                {
                    float* bufferPtr = ((float**)hostAudioBufferPtrs)[i];
                    double[] audioBuffer = audioBuffers[i];

                    for (int sample = 0; sample < CurrentBufferSize; sample++)
                    {
                        audioBuffer[sample] = bufferPtr[sample];
                    }
                }
                else
                {
                    IntPtr bufferPtr = (IntPtr)((double**)hostAudioBufferPtrs)[i];

                    Marshal.Copy(bufferPtr, audioBuffers[i], 0, (int)CurrentBufferSize);
                }
            }
        }

        unsafe internal override void WriteData()
        {
            for (int i = 0; i < NumChannels; i++)
            {
                if (BitsPerSample == EAudioBitsPerSample.Bits32)
                {
                    float* bufferPtr = ((float**)hostAudioBufferPtrs)[i];
                    double[] audioBuffer = audioBuffers[i];

                    for (int sample = 0; sample < CurrentBufferSize; sample++)
                    {
                        bufferPtr[sample] = (float)audioBuffer[sample];
                    }
                }
                else
                {
                    IntPtr bufferPtr = (IntPtr)((double**)hostAudioBufferPtrs)[i];

                    Marshal.Copy(audioBuffers[i], 0, bufferPtr, (int)CurrentBufferSize);
                }
            }
        }

        public override void PassThroughTo(AudioIOPort<double> destinationPort)
        {
            if (destinationPort.CurrentBufferSize != CurrentBufferSize)
                throw new InvalidOperationException("Destination port does not have the same size");

            AudioIOPortManaged managedPort = destinationPort as AudioIOPortManaged;

            if (managedPort == null)
            {
                for (int channel = 0; channel < NumChannels; channel++)
                {
                    ReadOnlySpan<double> srcSpan = GetAudioBuffer(channel);
                    Span<double> dstSpan = destinationPort.GetAudioBuffer(channel);

                    srcSpan.CopyTo(dstSpan);
                }
            }
            else
            {
                for (int channel = 0; channel < NumChannels; channel++)
                {
                    audioBuffers[channel].CopyTo(managedPort.audioBuffers[channel], 0);
                }
            }
        }

        public override Span<double> GetAudioBuffer(int channel)
        {
            return audioBuffers[channel];
        }

        /// <summary>
        /// Get a managed copy of the audio buffers
        /// </summary>
        /// <returns>The managed audio buffer arrays</returns>
        public double[][] GetAudioBuffers()
        {
            return audioBuffers;
        }
    }
}
