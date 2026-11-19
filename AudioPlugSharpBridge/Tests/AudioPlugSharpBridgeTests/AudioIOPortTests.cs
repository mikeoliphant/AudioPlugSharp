using System;
using System.Runtime.InteropServices;
using AudioPlugSharp;
using Xunit;

namespace AudioPlugSharpBridgeTests;

/// <summary>
/// Port buffers across sample-format changes. Without a backing buffer (64-bit audio into a DoubleAudioIOPort)
/// the port uses the host's buffers directly; switching back to 32 bit must not free them (it did: heap
/// corruption, "free(): invalid pointer" on Linux, when a VST3 host re-ran setupProcessing in 32 bit).
/// </summary>
public unsafe class AudioIOPortTests
{
    [Fact]
    public void Switching_32_64_32_bit_never_frees_host_buffers()
    {
        var port = new DoubleAudioIOPort("Out", EAudioChannelConfiguration.Stereo);
        const int frames = 64;

        // Host buffers (two channels of double), owned by the "host" (this test).
        double* left = (double*)NativeMemory.AllocZeroed(frames * sizeof(double));
        double* right = (double*)NativeMemory.AllocZeroed(frames * sizeof(double));
        double** channels = (double**)NativeMemory.Alloc((nuint)(2 * sizeof(double*)));
        channels[0] = left;
        channels[1] = right;
        try
        {
            port.SetMaxSize(frames, EAudioBitsPerSample.Bits32); // backing buffer (doubles, converted)
            port.SetMaxSize(frames, EAudioBitsPerSample.Bits64); // no backing buffer
            port.SetAudioBufferPtrs((IntPtr)channels);
            Assert.Equal((IntPtr)channels, port.GetAudioBufferPtrs()); // the host's buffers, directly

            port.SetMaxSize(frames, EAudioBitsPerSample.Bits32); // must not free the host's buffers
            Assert.NotEqual((IntPtr)channels, port.GetAudioBufferPtrs());
            port.SetMaxSize(frames * 2, EAudioBitsPerSample.Bits32); // re-creating its own buffer frees only that

            // The host's memory is intact and still ours to free.
            channels[0][frames - 1] = 1.0;
            Assert.Equal(1.0, left[frames - 1]);
        }
        finally
        {
            NativeMemory.Free(left);
            NativeMemory.Free(right);
            NativeMemory.Free(channels);
        }
    }
}
