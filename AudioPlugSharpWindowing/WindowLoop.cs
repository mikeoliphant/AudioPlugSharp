using System.Diagnostics;
using System.Runtime.ExceptionServices;
using static AudioPlugSharpWindowing.Win32Api;

namespace AudioPlugSharpWindowing;

/// <summary>
/// The message loop of the thread that owns the windows, and timers that run on it.
///
/// Timers are run by the loop itself, at about millisecond resolution, rather than by the system's coarser window
/// timers (about 16 ms on Windows, which would turn a 60 Hz frame timer into about 30 Hz).
/// </summary>
public static unsafe class WindowLoop
{
    static readonly List<WindowTimer> timers = new();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static volatile bool quit;
    static uint loopThreadId;
    static ExceptionDispatchInfo? failure;

    /// <summary>
    /// Runs the loop on the calling thread until <see cref="Quit"/>. An exception thrown by a window's event handler
    /// or a timer ends the loop and is rethrown here.
    /// </summary>
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AudioPlugSharpWindowing supports Windows only so far");

        quit = false;
        failure = null;
        loopThreadId = GetCurrentThreadId();
        timeBeginPeriod(1); // so the wait below wakes on time

        try
        {
            MSG msg;
            while (!quit)
            {
                while (!quit && PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
                {
                    if (msg.Message == WM_QUIT)
                    {
                        quit = true;
                        break;
                    }
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                if (quit) break;

                RunDueTimers();

                double wait = NextTimerDue() - Now;
                uint milliseconds = double.IsPositiveInfinity(wait) ? INFINITE : (uint)Math.Clamp(Math.Ceiling(wait * 1000), 0, int.MaxValue);
                MsgWaitForMultipleObjectsEx(0, null, milliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            }
        }
        finally
        {
            timeEndPeriod(1);
            loopThreadId = 0;
        }

        failure?.Throw();
    }

    /// <summary>Ends <see cref="Run"/>. May be called from any thread.</summary>
    public static void Quit()
    {
        quit = true;
        uint thread = loopThreadId;
        if (thread != 0) PostThreadMessageW(thread, WM_NULL, 0, 0); // wake the loop
    }

    /// <summary>
    /// Calls <paramref name="tick"/> on the loop's thread about every <paramref name="interval"/>, with the seconds
    /// since its previous call (0 the first time). Dispose the timer to stop it. Call on the loop's thread.
    /// </summary>
    public static WindowTimer StartTimer(TimeSpan interval, Action<double> tick)
    {
        var timer = new WindowTimer(interval.TotalSeconds, tick, Now);
        timers.Add(timer);
        return timer;
    }

    internal static void StopTimer(WindowTimer timer) => timers.Remove(timer);

    static double Now => clock.Elapsed.TotalSeconds;

    static double NextTimerDue()
    {
        double next = double.PositiveInfinity;
        foreach (WindowTimer timer in timers) next = Math.Min(next, timer.Due);
        return next;
    }

    /// <summary>Runs the timers that are due. Also called while Windows runs a modal loop (moving or resizing a
    /// window), where <see cref="Run"/>'s loop doesn't run.</summary>
    internal static void RunDueTimers()
    {
        double now = Now;
        for (int i = 0; i < timers.Count; i++) // by index: a tick may start or stop timers
        {
            WindowTimer timer = timers[i];
            if (timer.Due > now) continue;
            timer.Fire(now);
            if (quit) return;
        }
    }

    /// <summary>An exception from a window's event handler or a timer: ends the loop, which rethrows it.</summary>
    internal static void Fail(Exception exception)
    {
        failure ??= ExceptionDispatchInfo.Capture(exception);
        Quit();
    }
}

/// <summary>A timer started by <see cref="WindowLoop.StartTimer"/>.</summary>
public class WindowTimer : IDisposable
{
    readonly double interval;
    readonly Action<double> tick;
    double last = double.NaN;

    internal WindowTimer(double interval, Action<double> tick, double now)
    {
        this.interval = Math.Max(interval, 0.001);
        this.tick = tick;
        Due = now + this.interval;
    }

    internal double Due { get; private set; }

    internal void Fire(double now)
    {
        double elapsed = double.IsNaN(last) ? 0 : now - last;
        last = now;
        // Keep the rhythm, but don't try to catch up on ticks missed while the thread was busy.
        Due = (now - Due > interval) ? now + interval : Due + interval;
        try
        {
            tick(elapsed);
        }
        catch (Exception e)
        {
            WindowLoop.Fail(e);
        }
    }

    public void Dispose() => WindowLoop.StopTimer(this);
}
