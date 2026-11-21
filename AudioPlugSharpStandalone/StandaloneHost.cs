using System.Diagnostics;
using AudioIOSharp;
using AudioPlugSharp;
using AudioPlugSharpWindowing;

namespace AudioPlugSharpStandalone;

/// <summary>
/// Runs a plugin as an application. The host owns the plugin, its audio and its window: it opens the plugin's
/// editor in a window of its own and drives it as the DAW adapters do (IAudioPluginEditor: open, idle, input,
/// resize, scale, close), so the same editor code runs in a DAW and standalone.
///
/// An application of its own for a plugin built into it:
/// <code>
/// [STAThread]
/// static void Main() => StandaloneHost.ForPlugin&lt;MyPlugin&gt;().Run();
/// </code>
/// The plugin always runs in a load context of its own (<see cref="PluginLoader"/>), as in a DAW: the host's status
/// bar has its own UILayout, and UILayout supports one layout per copy.
///
/// The plugin's state is restored when it starts and saved when it closes (<see cref="SaveFolder"/>). Audio runs
/// through AudioIOSharp: ASIO, JACK, or its internal clock (no device, the default). The audio configuration is saved
/// in the save folder too, with the last one used with each driver; <see cref="AudioDriver"/>,
/// <see cref="AudioDevice"/>, <see cref="AudioSampleRate"/> and <see cref="AudioBufferSize"/> change it for a run (and
/// from then on).
/// </summary>
public class StandaloneHost : IAudioHost
{
    readonly IAudioPlugin plugin;
    readonly IAudioPluginProcessor processor;
    readonly IAudioPluginEditor? editor;

    AudioEngine? engine;
    PortBinding? inputBinding, outputBinding;
    bool processing;
    Exception? processFailure;
    // Since the device last started or changed its format: blocks and the frames in them, and the dropouts in the first
    // second (a device can resync as it starts, before anything is heard) and after it.
    long blocks, frames, startupDropouts, dropouts;
    const double StartupSeconds = 1;
    // The time spent processing (Stopwatch ticks, never reset), for the plugin's load in the status strip.
    long busyTicks;

    // What went on while processing, recorded on the audio thread without allocating and logged only when processing
    // stops, so finding out about dropouts can't cause more: each dropout's position, with the garbage collections and GC
    // pause so far (process-wide, so they show whether a collection came with it), and the plugin's allocations.
    const int MaxRecordedDropouts = 64;
    readonly long[] dropoutFrames = new long[MaxRecordedDropouts];
    readonly int[] dropoutLost = new int[MaxRecordedDropouts];
    readonly int[] dropoutCollections = new int[MaxRecordedDropouts * 3];
    readonly long[] dropoutPauseTicks = new long[MaxRecordedDropouts];
    int recordedDropouts;
    long pluginAllocatedBytes;
    readonly int[] startCollections = new int[3];
    long startPauseTicks;
    DateTime startTime;

    /// <summary>A host for a plugin built into the application (see the class summary).</summary>
    public static StandaloneHost ForPlugin<TPlugin>() where TPlugin : IAudioPlugin, new()
    {
        return new StandaloneHost(PluginLoader.Load(typeof(TPlugin)));
    }

    // The plugin must come from PluginLoader (a load context of its own).
    internal StandaloneHost(IAudioPlugin plugin)
    {
        this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        processor = plugin.Processor;
        editor = plugin.Editor;
        SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), plugin.PluginName);
    }

    public IAudioPlugin Plugin => plugin;

    /// <summary>Where the plugin's state is kept between runs (as by AudioPlugSharp 0.7's standalone hosts), and the
    /// audio settings.</summary>
    public string SaveFolder { get; set; }

    /// <summary>The audio driver to use instead of the saved one (None, ASIO, JACK). Another driver starts from the
    /// configuration last used with it. Set before <see cref="Run"/>.</summary>
    public string? AudioDriver { get; set; }

    /// <summary>The device to use instead of the saved one. Set before <see cref="Run"/>.</summary>
    public string? AudioDevice { get; set; }

    /// <summary>The sample rate to use instead of the saved one. Set before <see cref="Run"/>.</summary>
    public int? AudioSampleRate { get; set; }

    /// <summary>The buffer size to use instead of the saved one. Set before <see cref="Run"/>.</summary>
    public int? AudioBufferSize { get; set; }

    /// <summary>The audio engine, while <see cref="Run"/> runs.</summary>
    public AudioEngine? Engine => engine;

    /// <summary>
    /// Runs the plugin until its window is closed, then saves its state. Call on the thread that should own the
    /// window (the application's main thread, marked [STAThread] on Windows).
    /// </summary>
    public void Run()
    {
        plugin.Host = this;
        processor.Initialize();
        PrepareParameters();
        editsEnabled = true;
        LoadState();
        BitsPerSample = (processor.SampleFormatsSupported & EAudioBitsPerSample.Bits32) != 0 ? EAudioBitsPerSample.Bits32 : EAudioBitsPerSample.Bits64;

        settings = AudioSettings.Load(SaveFolder);
        engine = new AudioEngine(StartupConfig(settings), AudioEngine.PlatformDrivers(plugin.PluginName), settings.For);
        engine.Log += (level, message) => Logger.Log($"Audio: {message}");
        engine.StateChanged += () => audioStateChanged = true; // shown in the status strip, on the UI thread
        // The engine went back to no device after the device failed: that's the configuration from now on.
        engine.ConfigChanged += config =>
        {
            settings.Remember(config);
            settings.Save(SaveFolder);
        };
        var client = mainClient = new Client(this);
        try
        {
            // The device opens while the window does (an ASIO driver can take a second or more); its configuration is
            // saved once it's open (Idle).
            startup = Task.Run(async () =>
            {
                await engine.Start();
                await engine.Attach(client);
            });

            RunWindow();
        }
        finally
        {
            // The engine's work doesn't need this thread, so waiting here is safe.
            FinishStartup(wait: true);

            engine.Detach(client).GetAwaiter().GetResult();
            settings.Remember(engine.Config);
            engine.Dispose();
            if (processing)
            {
                processor.Stop();
                processing = false;
                ReportProcessing();
            }
            SaveState();
            settings.Save(SaveFolder);
            inputBinding?.Dispose();
            outputBinding?.Dispose();
            if (processFailure != null) Logger.Log("Processing failed: " + processFailure);
        }
    }

    AudioSettings settings = new();
    IAudioClient? mainClient;

    // Opening the device at startup, until it's done.
    Task? startup;

    // [UI thread] Once the device opened at startup (or failed to), saves the configuration in use. With wait, waits
    // for it first (when the window has closed).
    void FinishStartup(bool wait)
    {
        if (startup == null || (!wait && !startup.IsCompleted))
            return;

        try
        {
            startup.GetAwaiter().GetResult();

            settings.Remember(engine!.Config);
            settings.Save(SaveFolder);
        }
        catch (Exception e)
        {
            Logger.Log("Audio didn't start: " + e);
        }

        startup = null;
    }

    // The saved configuration, with this run's choices on top. Another driver starts from what was last used with it.
    AudioConfig StartupConfig(AudioSettings settings)
    {
        AudioConfig c = settings.Config;
        if (AudioDriver != null && !string.Equals(AudioDriver, c.Driver, StringComparison.OrdinalIgnoreCase))
            c = settings.For(AudioDriver) is AudioConfig last ? last with { Driver = AudioDriver } : new AudioConfig { Driver = AudioDriver };
        return c with
        {
            Device = AudioDevice ?? c.Device,
            SampleRate = AudioSampleRate ?? c.SampleRate,
            BufferSize = AudioBufferSize ?? c.BufferSize,
        };
    }

    // ---- state --------------------------------------------------------------------------------------------------

    string SaveFile => Path.Combine(SaveFolder, "SaveData");

    void LoadState()
    {
        if (!File.Exists(SaveFile)) return;
        editsEnabled = false; // restoring sets every parameter's edit value; the processor gets the values directly
        try
        {
            processor.RestoreState(File.ReadAllBytes(SaveFile));
        }
        catch (Exception e)
        {
            Logger.Log("Could not load the saved state: " + e);
        }
        finally
        {
            editsEnabled = true;
        }
    }

    void SaveState()
    {
        try
        {
            byte[] data = processor.SaveState() ?? [];
            Directory.CreateDirectory(SaveFolder);
            File.WriteAllBytes(SaveFile, data);
        }
        catch (Exception e)
        {
            Logger.Log("Could not save the state: " + e);
        }
    }

    // ---- IAudioHost: transport ----------------------------------------------------------------------------------

    public double SampleRate { get; private set; } = ClockDriver.DefaultSampleRate;
    public uint MaxAudioBufferSize { get; private set; }
    public uint CurrentAudioBufferSize { get; private set; }
    public EAudioBitsPerSample BitsPerSample { get; private set; }
    public double BPM => 120;
    public long CurrentProjectSample { get; private set; }

    /// <summary>Dropouts while running, since the device last started or changed its format (after its first second;
    /// see <see cref="StartupDropouts"/>): the device's resyncs and callbacks that overran (ASIO), JACK's xruns, and the
    /// internal clock falling behind.</summary>
    public long Dropouts => Interlocked.Read(ref dropouts);

    /// <summary>Dropouts in the first second after the device started or changed its format: a device can resync as it
    /// starts, before anything is heard.</summary>
    public long StartupDropouts => Interlocked.Read(ref startupDropouts);
    public bool IsPlaying => false;

    // ---- the plugin on the device -------------------------------------------------------------------------------

    /// <summary>The plugin as the audio engine's client.</summary>
    class Client(StandaloneHost host) : IAudioClient
    {
        public void Prepare(AudioFormat format) => host.Prepare(format);

        public void Process(in AudioBuffers buffers) => host.ProcessBlock(buffers);
    }

    // The device opened, or its format changed (the engine calls this while nothing processes): the plugin is set up
    // for the device's rate and buffer size, and its ports for the device's channels.
    void Prepare(AudioFormat format)
    {
        if (processing)
        {
            processor.Stop();
            processing = false;
            ReportProcessing();
        }
        SampleRate = format.SampleRate;
        MaxAudioBufferSize = (uint)Math.Max(format.MaxFrames, 1);
        processor.InitializeProcessing();
        processor.SetMaxAudioBufferSize(MaxAudioBufferSize, BitsPerSample);
        inputBinding?.Dispose();
        outputBinding?.Dispose();
        inputBinding = new PortBinding(processor.InputPorts, input: true, MaxAudioBufferSize, BitsPerSample);
        outputBinding = new PortBinding(processor.OutputPorts, input: false, MaxAudioBufferSize, BitsPerSample);
        processor.Start();
        processing = true;
        startTime = DateTime.Now;
        for (int g = 0; g < 3; g++)
            startCollections[g] = GC.CollectionCount(g);
        startPauseTicks = GC.GetTotalPauseDuration().Ticks;
        Logger.Log($"Audio: {format.SampleRate} Hz, {format.MaxFrames} frames, inputs [{string.Join(", ", format.Inputs)}], outputs [{string.Join(", ", format.Outputs)}]");
    }

    // ---- audio thread -------------------------------------------------------------------------------------------

    bool blockEventsDispatched;

    void ProcessBlock(in AudioBuffers buffers)
    {
        int frames = buffers.Frames;
        if (!processing || frames > MaxAudioBufferSize || inputBinding == null || outputBinding == null)
        {
            buffers.ClearOutputs();
            return;
        }
        long blockStart = Stopwatch.GetTimestamp();
        CurrentAudioBufferSize = (uint)frames;
        blockEventsDispatched = false;
        changedCount = 0;

        inputBinding.Bind(buffers, frames);
        outputBinding.Bind(buffers, frames);
        try
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            processor.PreProcess();
            processor.Process();
            processor.PostProcess();
            pluginAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            outputBinding.Finish(buffers, frames);
        }
        catch (Exception e)
        {
            processFailure ??= e; // logged when the host stops; logging here would allocate on every block
            buffers.ClearOutputs();
        }

        // As in a DAW: parameters that changed in this block end their interpolation.
        var parameters = processor.Parameters;
        for (int i = 0; i < changedCount; i++)
            parameters[changed[i]].ResetParameterChange();

        CurrentProjectSample += frames;
        Volatile.Write(ref busyTicks, busyTicks + Stopwatch.GetTimestamp() - blockStart);
        int lost = buffers.Xruns + (buffers.Skipped > 0 ? 1 : 0);
        if (lost > 0)
        {
            Interlocked.Add(ref this.frames < StartupSeconds * SampleRate ? ref startupDropouts : ref dropouts, lost);
            if (recordedDropouts < MaxRecordedDropouts)
            {
                int r = recordedDropouts++;
                dropoutFrames[r] = this.frames;
                dropoutLost[r] = lost;
                for (int g = 0; g < 3; g++)
                    dropoutCollections[r * 3 + g] = GC.CollectionCount(g);
                dropoutPauseTicks[r] = GC.GetTotalPauseDuration().Ticks;
            }
        }
        blocks++;
        Volatile.Write(ref this.frames, this.frames + frames);
    }

    // Processing stopped (the device closed or changed its format): how it went, in the log.
    // Times are from when processing started (logged), so they line up with the plugin's own log lines.
    void ReportProcessing()
    {
        Logger.Log($"Audio: started {startTime:HH:mm:ss.fff}: {blocks} blocks at {SampleRate} Hz ({frames / SampleRate:F1} s), " +
                   $"{Interlocked.Read(ref dropouts)} dropout(s) while running, {Interlocked.Read(ref startupDropouts)} while starting");

        var collections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - startCollections[g]).ToArray();
        double pause = TimeSpan.FromTicks(GC.GetTotalPauseDuration().Ticks - startPauseTicks).TotalMilliseconds;
        Logger.Log($"Audio: GCs while processing: gen0 {collections[0]}, gen1 {collections[1]}, gen2 {collections[2]} " +
                   $"(total pause {pause:F1} ms); the plugin allocated {pluginAllocatedBytes:N0} bytes on the audio thread");

        if (recordedDropouts > 0)
        {
            var lines = new List<string>();
            int[] previous = startCollections;
            long previousPause = startPauseTicks;
            for (int r = 0; r < recordedDropouts; r++)
            {
                var gens = new List<string>();
                for (int g = 0; g < 3; g++)
                {
                    int since = dropoutCollections[r * 3 + g] - previous[g];
                    if (since > 0)
                        gens.Add($"gen{g} {since}");
                }
                double sincePause = TimeSpan.FromTicks(dropoutPauseTicks[r] - previousPause).TotalMilliseconds;
                string what = dropoutFrames[r] < StartupSeconds * SampleRate ? ", starting" : "";
                string gc = gens.Count > 0 ? $"GCs since the last: {string.Join(", ", gens)}, {sincePause:F1} ms pause" : "no GC since the last";
                lines.Add($"{dropoutFrames[r] / SampleRate:F2} s ({dropoutLost[r]}{what}; {gc})");
                previous = dropoutCollections[(r * 3)..(r * 3 + 3)];
                previousPause = dropoutPauseTicks[r];
            }
            long total = Interlocked.Read(ref dropouts) + Interlocked.Read(ref startupDropouts);
            long unrecorded = total - dropoutLost.Take(recordedDropouts).Sum();
            Logger.Log($"Audio: dropouts at {string.Join("; ", lines)}{(unrecorded > 0 ? $"; and {unrecorded} more" : "")}");
        }

        blocks = frames = 0;
        Interlocked.Exchange(ref dropouts, 0);
        Interlocked.Exchange(ref startupDropouts, 0);
        recordedDropouts = 0;
        pluginAllocatedBytes = 0;
    }

    public int ProcessEvents()
    {
        if (!blockEventsDispatched)
        {
            blockEventsDispatched = true;
            DispatchParameterChanges();
        }
        return (int)CurrentAudioBufferSize;
    }

    public void ProcessAllEvents() => ProcessEvents();

    // No MIDI output yet.
    public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset) { }
    public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset) { }
    public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset) { }
    public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset) { }

    // ---- parameter edits (editor, any thread) -> processor (audio thread) ---------------------------------------
    //
    // Without a DAW there is no automation to record: an edit goes straight to the processor, as a change at the end
    // of the next block (the plugin ramps to it across the block, as with a DAW's echo of the edit). The latest value
    // per parameter wins. Lock-free and allocation-free: a value and a flag per parameter.

    volatile bool editsEnabled;
    double[] pendingValue = [];
    int[] pending = [];
    int anyPending;
    int[] changed = [];
    int changedCount;

    void PrepareParameters()
    {
        int count = processor.Parameters?.Count ?? 0;
        pendingValue = new double[count];
        pending = new int[count];
        changed = new int[count];
    }

    public void BeginEdit(int parameter) { }

    public void PerformEdit(int parameter, double normalizedValue)
    {
        if (!editsEnabled || (uint)parameter >= (uint)pending.Length) return;
        Volatile.Write(ref pendingValue[parameter], normalizedValue);
        Volatile.Write(ref pending[parameter], 1);
        Volatile.Write(ref anyPending, 1);
    }

    public void EndEdit(int parameter) { }

    void DispatchParameterChanges()
    {
        if (Interlocked.Exchange(ref anyPending, 0) == 0) return;
        var parameters = processor.Parameters;
        int last = (int)CurrentAudioBufferSize - 1;
        for (int i = 0; i < pending.Length; i++)
        {
            if (Interlocked.Exchange(ref pending[i], 0) == 0) continue;
            processor.HandleParameterChange(parameters[i], Volatile.Read(ref pendingValue[i]), Math.Max(last, 0));
            changed[changedCount++] = i;
        }
    }

    // ---- window and editor (UI thread) --------------------------------------------------------------------------
    //
    // The window's client area is the editor with the status strip below it, the full width. The editor keeps the
    // size it asks for; the window is sized around the two. Without an editor (or editor support), the window is the
    // strip alone. If the strip can't be created (no OpenGL), the window is the editor alone.

    NativeWindow? window, editorWindow;
    StatusStrip? strip;
    bool editorOpen;
    double scale = 1;
    int uiThreadId;
    const uint NoEditorWidth = 400;

    void RunWindow()
    {
        uiThreadId = Environment.CurrentManagedThreadId;
        NativeWindowType? type = NativeWindow.PlatformWindowType;
        bool showEditor = editor != null && editor.HasUserInterface && type != null && editor.IsEditorWindowTypeSupported((EditorWindowType)type.Value);
        uint width = showEditor ? editor!.EditorWidth : NoEditorWidth, height = showEditor ? editor!.EditorHeight : 0;

        window = NativeWindow.CreateTopLevel(plugin.PluginName, (int)width, (int)height + StatusStrip.Height, showEditor && editor!.CanResizeEditor);

        scale = window.Scale;
        window.SetClientSize(Pixels(width), Pixels(height) + Pixels(StatusStrip.Height));

        // Before the editor's window: a window gives keyboard focus to the child created last, which should be the
        // editor's.
        try
        {
            strip = new StatusStrip(window, Pixels(height), window.ClientWidth, Pixels(StatusStrip.Height), scale,
                s => new AudioSettingsPanel(engine!, mainClient!, AudioConfigApplied, () => s.SetOpen(false)));

            strip.Clicked += () =>
            {
                if (editorOpen) editorWindow!.Focus(); // keys stay the editor's
            };

            strip.OpenChanged += PanelOpenChanged;
        }
        catch (Exception e)
        {
            Logger.Log("No status strip: " + e.Message);

            if (!showEditor) height = 100;

            window.SetClientSize(Pixels(width), Pixels(height));
        }

        if (showEditor)
        {
            editorWindow = window.CreateChild(0, 0, window.ClientWidth, Pixels(height));
            editor!.OpenEditor(editorWindow.Handle, (EditorWindowType)editorWindow.WindowType, scale);
            editorOpen = true;

            editorWindow.KeepControl(); // after the editor wrapped the window (SDL)

            editorWindow.Input += EditorInput;
            editorWindow.Resized += EditorWindowResized;
        }

        window.Resized += ArrangeChildren;
        window.ScaleChanged += ScaleChanged;
        window.CloseRequested += () =>
        {
            CloseEditor(); // while their windows still exist
            CloseStrip();

            return true;
        };
        window.Closed += WindowLoop.Quit;

        using (WindowLoop.StartTimer(TimeSpan.FromSeconds(1 / 60.0), Idle))
        {
            window.Show();
            editorWindow?.Focus();
            try
            {
                WindowLoop.Run();
            }
            finally
            {
                CloseEditor();
                CloseStrip();
                window.Dispose();
            }
        }
    }

    int Pixels(uint units) => Math.Max(1, (int)Math.Round(units * scale));

    int Pixels(int units) => Pixels((uint)units);

    uint Units(int pixels) => (uint)Math.Max(1, Math.Round(pixels / scale));

    int StripPixels => strip != null ? Pixels(strip.TotalHeight) : 0;

    // [UI thread] The settings panel moved the engine to a configuration: it's the one to start with next time.
    void AudioConfigApplied(AudioConfig config)
    {
        settings.Remember(config);
        settings.Save(SaveFolder);
    }

    bool PanelOpen => strip?.IsOpen == true;

    // The window's client area for the editor at its size (or the strip alone), and the strip. The settings panel needs
    // a minimum width: the window widens to it while the panel is open, and the editor keeps its size (the reverse of
    // ArrangeChildren).
    void SizeWindowToContent()
    {
        int width = editorOpen ? Pixels(editor!.EditorWidth) : Pixels(NoEditorWidth);

        if (PanelOpen)
        {
            int panelWidth = Pixels(AudioSettingsPanel.MinWidth);

            if (!editorOpen) width = Math.Max(width, panelWidth);
            else if (width >= editorWidthAtOpen) width = Math.Max(panelWidth, editorWidthAtOpen) + width - editorWidthAtOpen;
        }

        int height = editorOpen ? Pixels(editor!.EditorHeight) : 0;

        window!.SetClientSize(width, height + StripPixels);
    }

    // The editor's size (pixels) when the settings panel opened.
    int editorWidthAtOpen;
    int editorHeightAtOpen;

    // The panel opened or closed: the window grows downward for it (and widens to its minimum), and back.
    void PanelOpenChanged()
    {
        if (PanelOpen && editorWindow != null)
        {
            editorWidthAtOpen = editorWindow.ClientWidth;
            editorHeightAtOpen = editorWindow.ClientHeight;
        }

        SizeWindowToContent();
    }

    // The window's client area changed (the user resized it, or SizeWindowToContent): the editor takes all but the
    // strip at the bottom. While the editor is fullscreen its size is stored for when it comes back.
    //
    // While the settings panel is open, the window can be wider than the editor (the panel's minimum width): then the
    // editor keeps the width it had, the window's width beyond that and the panel's minimum goes to the editor, and a
    // window narrower than the editor narrows it. A window too short for the editor and the panel (Windows keeps a
    // window about as tall as the screen at most) cuts off the panel's bottom, not the editor.
    void ArrangeChildren(int width, int height)
    {
        int editorHeight = Math.Max(1, height - StripPixels);
        int editorWidth = width;

        if (PanelOpen && editorOpen)
        {
            int extra = width - Math.Max(Pixels(AudioSettingsPanel.MinWidth), editorWidthAtOpen);

            editorWidth = extra >= 0 ? editorWidthAtOpen + extra : Math.Min(width, editorWidthAtOpen);

            editorHeight = Math.Max(editorHeight, Math.Min(editorHeightAtOpen, height));
        }

        editorWindow?.SetClientSize(editorWidth, editorHeight);
        strip?.SetBounds(editorWindow != null ? editorHeight : 0, width, StripPixels);
    }

    void Idle(double secondsElapsed)
    {
        FinishStartup(wait: false);

        if (editorOpen)
        {
            ApplyWindowRequests();
            editor!.EditorIdle(secondsElapsed);
        }

        UpdateStatus(secondsElapsed);
    }

    bool EditorInput(in WindowInputEvent e)
    {
        if (!editorOpen) return false;
        var inputEvent = new EditorInputEvent
        {
            Type = (EditorInputEventType)e.Type,
            Modifiers = (EditorModifiers)e.Modifiers,
            X = e.X,
            Y = e.Y,
            Button = (EditorMouseButton)e.Button,
            WheelDelta = e.WheelDelta,
            Key = e.Key,
            Character = e.Character,
        };
        return editor!.EditorInput(in inputEvent);
    }

    // The editor's window changed size: the user resized the main window, or the editor went fullscreen or back.
    void EditorWindowResized(int width, int height)
    {
        if (!editorOpen || !editor!.CanResizeEditor) return;
        uint units = Units(width), unitsHigh = Units(height);
        if (units == editor.EditorWidth && unitsHigh == editor.EditorHeight) return;
        if (!editor.SetEditorSize(units, unitsHigh) && !editorWindow!.IsFullscreen)
            SizeWindowToContent(); // the size it took instead
    }

    // The main window moved to a monitor with another scale: the editor and strip redraw at it, at the same logical
    // size.
    void ScaleChanged(double newScale)
    {
        if (newScale == scale) return;

        scale = newScale;

        if (editorOpen) editor!.SetEditorScale(scale);
        strip?.SetScale(scale);

        SizeWindowToContent();
    }

    void CloseEditor()
    {
        if (!editorOpen) return;
        editorWindow!.SetFullscreen(false);
        editorWindow.SetCursorVisible(true);
        editorOpen = false;
        editor!.CloseEditor();
        Volatile.Write(ref pendingCursorVisible, NoRequest);
        Volatile.Write(ref pendingFullscreen, NoRequest);
        IsEditorFullscreen = false;
    }

    void CloseStrip()
    {
        strip?.Dispose();
        strip = null;
    }

    // ---- status strip (UI thread) -------------------------------------------------------------------------------

    volatile bool audioStateChanged = true;
    AudioState? audioState;
    double sinceStatus;
    long statusBusyTicks, statusFrames;
    double loadPercent = double.NaN;
    const double StatusInterval = 0.5;

    // The engine's state when it changes; the load and dropouts twice a second.
    void UpdateStatus(double secondsElapsed)
    {
        if (strip == null || engine == null) return;

        bool changed = false;

        if (audioStateChanged || audioState == null)
        {
            audioStateChanged = false;
            audioState = engine.State;
            changed = true;
        }

        sinceStatus += secondsElapsed;

        if (sinceStatus >= StatusInterval)
        {
            sinceStatus = 0;

            long busy = Volatile.Read(ref busyTicks), processed = Volatile.Read(ref frames);
            long busyDelta = busy - statusBusyTicks, framesDelta = processed - statusFrames;
            statusBusyTicks = busy;
            statusFrames = processed;

            // The time processing took, as a share of the time the audio covered. Frames start again from 0 when the
            // device restarts: that interval is skipped.
            if (framesDelta > 0 && SampleRate > 0)
                loadPercent = busyDelta / (double)Stopwatch.Frequency / (framesDelta / SampleRate) * 100;

            changed = true;
        }

        if (changed)
            strip.Show(StatusStrip.Describe(audioState, engine.Config, processing ? loadPercent : double.NaN, Interlocked.Read(ref dropouts)));

        strip.Tick(secondsElapsed);
    }

    // ---- IAudioHost: editor window services (any thread) --------------------------------------------------------

    const int NoRequest = -1;
    int pendingCursorVisible = NoRequest, pendingFullscreen = NoRequest;
    volatile bool isEditorFullscreen;

    public bool IsEditorFullscreen
    {
        get => isEditorFullscreen;
        private set => isEditorFullscreen = value;
    }

    public void SetEditorCursorVisible(bool visible)
    {
        Volatile.Write(ref pendingCursorVisible, visible ? 1 : 0);
        if (Environment.CurrentManagedThreadId == uiThreadId) ApplyWindowRequests();
    }

    public void SetEditorFullscreen(bool fullscreen)
    {
        Volatile.Write(ref pendingFullscreen, fullscreen ? 1 : 0);
        if (Environment.CurrentManagedThreadId == uiThreadId) ApplyWindowRequests();
    }

    // [UI thread] The latest cursor and fullscreen requests (an editor rendering on its own thread asks from there).
    void ApplyWindowRequests()
    {
        if (!editorOpen) return;
        int cursor = Interlocked.Exchange(ref pendingCursorVisible, NoRequest);
        if (cursor != NoRequest) editorWindow!.SetCursorVisible(cursor != 0);
        int fullscreen = Interlocked.Exchange(ref pendingFullscreen, NoRequest);
        if (fullscreen != NoRequest && (fullscreen == 0 || editor!.CanResizeEditor))
            IsEditorFullscreen = editorWindow!.SetFullscreen(fullscreen != 0);
    }
}
