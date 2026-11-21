using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioIOSharp;
using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// Connects a plugin's input or output ports to the device's channels for each block, as a DAW would hand them over: an
/// array of channel pointers per port (<see cref="AudioIOPort.SetAudioBufferPtrs"/>). The device's channels go to the
/// ports' channels in order: the device's first channel to the first port's first channel, and so on. Channels the
/// device doesn't have read silence, or are written to buffers of the host's own that go nowhere.
/// <list type="bullet">
/// <item>Inputs are copied into the host's own buffers: a plugin may write to its inputs, and with JACK an input's
/// buffer can be another program's output.</item>
/// <item>Outputs of a 32-bit plugin are the device's own buffers (with JACK, nothing is copied); a 64-bit plugin writes
/// to the host's buffers, converted to the device's after processing.</item>
/// </list>
/// Everything is allocated up front, so binding a block allocates nothing.
/// </summary>
internal unsafe class PortBinding : IDisposable
{
    readonly AudioIOPort[] ports;
    readonly bool input, doubles;
    readonly nuint sampleSize;
    readonly int[] firstChannel;  // the device channel of each port's first channel
    readonly int channelCount;    // all the ports' channels
    readonly void*** tables;      // [port][channel]: what the plugin reads or writes this block
    readonly void*** own;         // [port][channel]: the host's own buffers

    public PortBinding(AudioIOPort[]? ports, bool input, uint maxFrames, EAudioBitsPerSample format)
    {
        this.ports = ports ?? [];
        this.input = input;
        doubles = format == EAudioBitsPerSample.Bits64;
        sampleSize = doubles ? 8u : 4u;
        firstChannel = new int[this.ports.Length];
        tables = (void***)NativeMemory.AllocZeroed((nuint)Math.Max(this.ports.Length, 1), (nuint)sizeof(void**));
        own = (void***)NativeMemory.AllocZeroed((nuint)Math.Max(this.ports.Length, 1), (nuint)sizeof(void**));
        for (int p = 0; p < this.ports.Length; p++)
        {
            uint count = this.ports[p].NumChannels;
            firstChannel[p] = channelCount;
            channelCount += (int)count;
            tables[p] = (void**)NativeMemory.AllocZeroed(Math.Max(count, 1u), (nuint)sizeof(void*));
            own[p] = (void**)NativeMemory.AllocZeroed(Math.Max(count, 1u), (nuint)sizeof(void*));
            for (int c = 0; c < count; c++)
                own[p][c] = NativeMemory.AllocZeroed(Math.Max(maxFrames, 1u), sampleSize);
        }
    }

    /// <summary>[audio thread] Hands the block's channels to the ports, before the plugin processes.</summary>
    public void Bind(in AudioBuffers device, int frames)
    {
        int deviceChannels = input ? device.InputCount : device.OutputCount;
        for (int p = 0; p < ports.Length; p++)
        {
            for (int c = 0; c < ports[p].NumChannels; c++)
            {
                int d = firstChannel[p] + c;
                void* buffer = own[p][c];
                if (input)
                {
                    if (d >= deviceChannels)
                        NativeMemory.Clear(buffer, (nuint)frames * sampleSize);
                    else if (doubles)
                        Widen(device.Input(d), new Span<double>(buffer, frames));
                    else
                        device.Input(d).CopyTo(new Span<float>(buffer, frames));
                }
                else if (!doubles && d < deviceChannels)
                {
                    buffer = Unsafe.AsPointer(ref MemoryMarshal.GetReference(device.Output(d)));
                }
                tables[p][c] = buffer;
            }
            ports[p].SetCurrentBufferSize((uint)frames);
            ports[p].SetAudioBufferPtrs((IntPtr)tables[p]);
        }
        // The device's outputs beyond the plugin's are silent.
        if (!input)
            for (int d = channelCount; d < deviceChannels; d++)
                device.Output(d).Clear();
    }

    /// <summary>[audio thread] After the plugin processed: a 64-bit plugin's outputs go to the device.</summary>
    public void Finish(in AudioBuffers device, int frames)
    {
        if (input || !doubles)
            return;
        for (int p = 0; p < ports.Length; p++)
            for (int c = 0; c < ports[p].NumChannels; c++)
            {
                int d = firstChannel[p] + c;
                if (d < device.OutputCount)
                    Narrow(new ReadOnlySpan<double>(own[p][c], frames), device.Output(d));
            }
    }

    static void Widen(ReadOnlySpan<float> from, Span<double> to)
    {
        for (int n = 0; n < to.Length; n++)
            to[n] = from[n];
    }

    static void Narrow(ReadOnlySpan<double> from, Span<float> to)
    {
        for (int n = 0; n < to.Length; n++)
            to[n] = (float)from[n];
    }

    public void Dispose()
    {
        for (int p = 0; p < ports.Length; p++)
        {
            for (int c = 0; c < ports[p].NumChannels; c++)
                NativeMemory.Free(own[p][c]);
            NativeMemory.Free(own[p]);
            NativeMemory.Free(tables[p]);
        }
        NativeMemory.Free(own);
        NativeMemory.Free(tables);
    }
}
