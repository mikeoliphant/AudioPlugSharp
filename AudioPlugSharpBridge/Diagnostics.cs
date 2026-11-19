using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioPlugSharpBridge;

/// <summary>Test hooks used by the native stress test. Not part of the plugin API.</summary>
public static class Diagnostics
{
    private static Thread? _pressureThread;
    private static volatile bool _running;

    /// <summary>Starts a background thread that allocates garbage at roughly the given rate.</summary>
    [UnmanagedCallersOnly]
    public static void StartGcPressure(int megabytesPerSecond)
    {
        try
        {
            StopPressure();
            _running = true;
            _pressureThread = new Thread(() => PressureLoop(megabytesPerSecond))
            {
                IsBackground = true,
                Name = "AudioPlugSharpBridge GC pressure",
            };
            _pressureThread.Start();
        }
        catch (Exception e) { Console.Error.WriteLine(e); }
    }

    [UnmanagedCallersOnly]
    public static void StopGcPressure()
    {
        try { StopPressure(); }
        catch (Exception e) { Console.Error.WriteLine(e); }
    }

    [UnmanagedCallersOnly]
    public static int GcCount(int generation) => generation is >= 0 and <= 2 ? GC.CollectionCount(generation) : 0;

    [UnmanagedCallersOnly]
    public static double GcTotalPauseMs() => GC.GetTotalPauseDuration().TotalMilliseconds;

    /// <summary>Forces full GCs, then returns how many plugin load contexts are still alive.</summary>
    [UnmanagedCallersOnly]
    public static int LivePluginContexts()
    {
        try
        {
            // Unloading a collectible context takes a few GC + finalizer passes.
            for (int i = 0; i < 10 && Interop.InstanceLoader.LiveContextCount() > 0; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return Interop.InstanceLoader.LiveContextCount();
        }
        catch (Exception e) { Console.Error.WriteLine(e); return -1; }
    }

    /// <summary>Bytes allocated on audio threads inside the bridge (outside plugin code), process-wide. Must stay 0.</summary>
    [UnmanagedCallersOnly]
    public static long BridgeAllocatedBytes() => Interop.AllocationCounters.Bridge;

    /// <summary>Bytes allocated on audio threads inside plugin code (PreProcess/Process/PostProcess), process-wide.</summary>
    [UnmanagedCallersOnly]
    public static long PluginAllocatedBytes() => Interop.AllocationCounters.Plugin;

    [UnmanagedCallersOnly]
    public static void ResetAllocationCounters() => Interop.AllocationCounters.Reset();

    private static void StopPressure()
    {
        _running = false;
        _pressureThread?.Join();
        _pressureThread = null;
    }

    private static void PressureLoop(int megabytesPerSecond)
    {
        // Keep a random subset alive so objects get promoted and gen1/gen2 collections happen too.
        var survivors = new object[16384];
        var rng = new Random(1234);
        double bytesPerSecond = megabytesPerSecond * 1024.0 * 1024.0;
        var clock = Stopwatch.StartNew();
        long allocated = 0;

        while (_running)
        {
            long target = (long)(clock.Elapsed.TotalSeconds * bytesPerSecond);
            while (allocated < target)
            {
                int size = 64 + rng.Next(4096);
                var garbage = new byte[size];
                if (rng.Next(8) == 0) survivors[rng.Next(survivors.Length)] = garbage;
                allocated += size;
            }
            Thread.Sleep(1);
        }
    }
}
