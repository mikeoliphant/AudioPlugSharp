using System.Runtime.InteropServices;
using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// Native channel buffers for a plugin's ports, in the plugin's sample format, as a DAW would pass them: each port
/// gets an array of channel pointers (see <see cref="AudioIOPort.SetAudioBufferPtrs"/>).
/// </summary>
internal unsafe class PortBuffers : IDisposable
{
    readonly AudioIOPort[] ports;
    readonly void*** channels; // [port][channel] -> samples
    readonly nuint bytesPerSample;

    public PortBuffers(AudioIOPort[]? ports, uint maxFrames, EAudioBitsPerSample format)
    {
        this.ports = ports ?? [];
        bytesPerSample = format == EAudioBitsPerSample.Bits64 ? 8u : 4u;
        channels = (void***)NativeMemory.AllocZeroed((nuint)Math.Max(this.ports.Length, 1), (nuint)sizeof(void**));
        for (int p = 0; p < this.ports.Length; p++)
        {
            uint count = this.ports[p].NumChannels;
            channels[p] = (void**)NativeMemory.AllocZeroed(Math.Max(count, 1u), (nuint)sizeof(void*));
            for (int c = 0; c < count; c++)
                channels[p][c] = NativeMemory.AllocZeroed(maxFrames, bytesPerSample);
        }
    }

    /// <summary>[audio thread] Hands the buffers to the ports for a block, silent if <paramref name="clear"/>.</summary>
    public void Bind(uint frames, bool clear)
    {
        for (int p = 0; p < ports.Length; p++)
        {
            if (clear)
                for (int c = 0; c < ports[p].NumChannels; c++)
                    NativeMemory.Clear(channels[p][c], frames * bytesPerSample);
            ports[p].SetCurrentBufferSize(frames);
            ports[p].SetAudioBufferPtrs((IntPtr)channels[p]);
        }
    }

    /// <summary>[audio thread] Silences the first <paramref name="frames"/> samples of every channel.</summary>
    public void Clear(uint frames)
    {
        for (int p = 0; p < ports.Length; p++)
            for (int c = 0; c < ports[p].NumChannels; c++)
                NativeMemory.Clear(channels[p][c], frames * bytesPerSample);
    }

    public void Dispose()
    {
        for (int p = 0; p < ports.Length; p++)
        {
            for (int c = 0; c < ports[p].NumChannels; c++)
                NativeMemory.Free(channels[p][c]);
            NativeMemory.Free(channels[p]);
        }
        NativeMemory.Free(channels);
    }
}
