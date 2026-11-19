using System;
using System.Diagnostics;
using System.Threading;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout
{
    /// <summary>
    /// Runs an editor's frames on a thread of its own (render-thread mode of AudioPluginUILayout): the thread owns
    /// the MonoGameHost while it runs and does update, draw and present with vsync. The UI thread hands it input
    /// and size changes through a preallocated single-producer/single-consumer queue, so neither side allocates
    /// or blocks in the steady state.
    ///
    /// The host is created on the UI thread (window and context setup stay there) and handed over by Start; Stop
    /// hands it back.
    /// </summary>
    class EditorRenderThread
    {
        // Frames shorter than this mean Present did not wait for the vertical retrace (no vsync on this system, or
        // a hidden window); the thread then sleeps so that it does not spin.
        static readonly long minFrameTicks = Stopwatch.Frequency / 240;

        // About 4 seconds of mouse moves at 250 Hz. When full, new events are dropped and all input is released
        // once the thread catches up, so that no button or key stays stuck.
        const int QueueSize = 1024;

        readonly MonoGameHost host;
        readonly Action<float> frame;
        readonly Thread thread;
        readonly EditorInputEvent[] queue = new EditorInputEvent[QueueSize];

        // Producer (UI thread) writes queueHead, consumer (render thread) writes queueTail; both only increase.
        int queueHead;
        int queueTail;
        volatile bool inputDropped;

        // Packed (width << 32) | height, or -1 when unchanged.
        long pendingSize = -1;

        volatile bool stopRequested;

        // Written by the render thread, read after it stopped
        long frameCount;
        long runTicks;

        /// <param name="host">The editor's host, bound to the calling thread.</param>
        /// <param name="frame">Run on the render thread for each frame: update, draw and present.</param>
        /// <param name="name">The thread's name.</param>
        public EditorRenderThread(MonoGameHost host, Action<float> frame, string name)
        {
            this.host = host;
            this.frame = frame;

            thread = new Thread(Run)
            {
                Name = name,
                IsBackground = true,
            };
        }

        /// <summary>Whether a frame failed: the thread then stopped rendering (the failure was logged).</summary>
        public bool Failed { get; private set; }

        /// <summary>Hands the host to the render thread and starts it. Call on the host's thread.</summary>
        public void Start()
        {
            host.ReleaseFromCurrentThread();

            thread.Start();
        }

        /// <summary>
        /// Stops the thread and binds the host to the calling thread again. Returns false if the thread did not
        /// stop in time; the host must then be left alone (it may still be in use).
        /// </summary>
        public bool Stop()
        {
            stopRequested = true;

            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                Logger.Log("Editor render thread did not stop");

                return false;
            }

            host.BindToCurrentThread();

            if (runTicks > 0)
                Logger.Log(thread.Name + ": " + frameCount + " frames, " + (frameCount * (double)Stopwatch.Frequency / runTicks).ToString("0.0") + " fps");

            return true;
        }

        /// <summary>Queues an input event for the next frame. Call on the UI thread.</summary>
        public void PostInput(in EditorInputEvent inputEvent)
        {
            int head = queueHead;

            if ((head - Volatile.Read(ref queueTail)) >= QueueSize)
            {
                inputDropped = true;

                return;
            }

            queue[head & (QueueSize - 1)] = inputEvent;

            Volatile.Write(ref queueHead, head + 1);
        }

        /// <summary>The window was resized; applied before the next frame. Call on the UI thread.</summary>
        public void PostResize(int width, int height)
        {
            Interlocked.Exchange(ref pendingSize, ((long)width << 32) | (uint)height);
        }

        void Run()
        {
            try
            {
                host.BindToCurrentThread();

                long startTime = Stopwatch.GetTimestamp();
                long lastFrame = startTime;
                bool first = true;

                while (!stopRequested)
                {
                    long frameStart = Stopwatch.GetTimestamp();

                    float secondsElapsed = first ? 0 : (float)((double)(frameStart - lastFrame) / Stopwatch.Frequency);

                    lastFrame = frameStart;
                    first = false;

                    ApplyPending();

                    frame(secondsElapsed);

                    frameCount++;
                    runTicks = Stopwatch.GetTimestamp() - startTime;

                    long frameTicks = Stopwatch.GetTimestamp() - frameStart;

                    if (frameTicks < minFrameTicks)
                        Thread.Sleep((int)Math.Max(1, ((minFrameTicks - frameTicks) * 1000) / Stopwatch.Frequency));
                }
            }
            catch (Exception ex)
            {
                // Don't take the host process down; the editor stops updating
                Failed = true;

                Logger.Log("Editor render thread failed: " + ex);
            }
            finally
            {
                host.ReleaseFromCurrentThread();
            }
        }

        void ApplyPending()
        {
            long size = Interlocked.Exchange(ref pendingSize, -1);

            if (size != -1)
                host.Resize((int)(size >> 32), (int)(uint)size);

            int head = Volatile.Read(ref queueHead);
            int tail = queueTail;

            while (tail != head)
            {
                AudioPluginUILayout.ApplyInput(host, queue[tail & (QueueSize - 1)]);

                tail++;
            }

            Volatile.Write(ref queueTail, tail);

            if (inputDropped)
            {
                inputDropped = false;

                host.ReleaseAllInput();
            }
        }
    }
}
