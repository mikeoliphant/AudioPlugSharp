using System.Diagnostics;

namespace AudioPlugSharpStandalone;

/// <summary>
/// Stands in for an audio device: a thread that asks for a block of audio at the block rate. Used until real audio
/// devices are supported.
/// </summary>
internal class DummyAudioDevice
{
    readonly double sampleRate;
    readonly uint blockSize;
    readonly Action<uint> process;
    Thread? thread;
    volatile bool running;

    public DummyAudioDevice(double sampleRate, uint blockSize, Action<uint> process)
    {
        this.sampleRate = sampleRate;
        this.blockSize = blockSize;
        this.process = process;
    }

    public void Start()
    {
        if (thread != null) return;
        running = true;
        thread = new Thread(Run) { Name = "AudioPlugSharp dummy audio", IsBackground = true, Priority = ThreadPriority.Highest };
        thread.Start();
    }

    public void Stop()
    {
        if (thread == null) return;
        running = false;
        thread.Join();
        thread = null;
    }

    void Run()
    {
        double period = blockSize / sampleRate;
        var clock = Stopwatch.StartNew();
        double next = 0;

        while (running)
        {
            process(blockSize);

            next += period;
            double wait = next - clock.Elapsed.TotalSeconds;
            if (wait < -4 * period)
                next = clock.Elapsed.TotalSeconds; // far behind (a debugger break, a suspended machine): start over
            else if (wait > 0)
                Thread.Sleep(TimeSpan.FromSeconds(wait));
        }
    }
}
