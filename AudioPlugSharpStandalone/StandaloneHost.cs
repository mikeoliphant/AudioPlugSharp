using AudioPlugSharp;
using AudioPlugSharpWindowing;

namespace AudioPlugSharpStandalone;

/// <summary>
/// Runs a plugin as an application. The host owns the plugin, its audio and its window: it opens the plugin's
/// editor in a window of its own and drives it as the DAW adapters do (IAudioPluginEditor: open, idle, input,
/// resize, scale, close), so the same editor code runs in a DAW and standalone.
///
/// <code>
/// [STAThread]
/// static void Main() => new StandaloneHost(new MyPlugin()).Run();
/// </code>
///
/// The plugin's state is restored when it starts and saved when it closes (<see cref="SaveFolder"/>). Audio is a
/// dummy device for now: silent input, output discarded, at <see cref="SampleRate"/> in blocks of
/// <see cref="BlockSize"/>.
/// </summary>
public class StandaloneHost : IAudioHost
{
    readonly IAudioPlugin plugin;
    readonly IAudioPluginProcessor processor;
    readonly IAudioPluginEditor? editor;

    PortBuffers? inputBuffers, outputBuffers;
    DummyAudioDevice? audio;
    Exception? processFailure;

    public StandaloneHost(IAudioPlugin plugin)
    {
        this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        processor = plugin.Processor;
        editor = plugin.Editor;
        SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), plugin.PluginName);
    }

    public IAudioPlugin Plugin => plugin;

    /// <summary>Where the plugin's state is kept between runs (as by AudioPlugSharp 0.7's standalone hosts).</summary>
    public string SaveFolder { get; set; }

    /// <summary>The block size audio is processed in. Set before <see cref="Run"/>.</summary>
    public uint BlockSize { get; set; } = 256;

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
        MaxAudioBufferSize = BlockSize;
        processor.InitializeProcessing();
        processor.SetMaxAudioBufferSize(MaxAudioBufferSize, BitsPerSample);
        inputBuffers = new PortBuffers(processor.InputPorts, MaxAudioBufferSize, BitsPerSample);
        outputBuffers = new PortBuffers(processor.OutputPorts, MaxAudioBufferSize, BitsPerSample);

        processor.Start();
        audio = new DummyAudioDevice(SampleRate, MaxAudioBufferSize, ProcessBlock);
        audio.Start();
        try
        {
            RunWindow();
        }
        finally
        {
            audio.Stop();
            processor.Stop();
            SaveState();
            inputBuffers.Dispose();
            outputBuffers.Dispose();
            if (processFailure != null) Logger.Log("Processing failed: " + processFailure);
        }
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

    /// <summary>The sample rate audio is processed at. Set before <see cref="Run"/>.</summary>
    public double SampleRate { get; set; } = 48000;
    public uint MaxAudioBufferSize { get; private set; }
    public uint CurrentAudioBufferSize { get; private set; }
    public EAudioBitsPerSample BitsPerSample { get; private set; }
    public double BPM => 120;
    public long CurrentProjectSample { get; private set; }
    public bool IsPlaying => false;

    // ---- audio thread -------------------------------------------------------------------------------------------

    bool blockEventsDispatched;

    void ProcessBlock(uint frames)
    {
        CurrentAudioBufferSize = frames;
        blockEventsDispatched = false;
        changedCount = 0;

        inputBuffers!.Bind(frames, clear: true);
        outputBuffers!.Bind(frames, clear: false);
        try
        {
            processor.PreProcess();
            processor.Process();
            processor.PostProcess();
        }
        catch (Exception e)
        {
            processFailure ??= e; // logged when the host stops; logging here would allocate on every block
            outputBuffers.Clear(frames);
        }

        // As in a DAW: parameters that changed in this block end their interpolation.
        var parameters = processor.Parameters;
        for (int i = 0; i < changedCount; i++)
            parameters[changed[i]].ResetParameterChange();

        CurrentProjectSample += frames;
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

    NativeWindow? window, editorWindow;
    bool editorOpen;
    double scale = 1;
    int uiThreadId;

    void RunWindow()
    {
        uiThreadId = Environment.CurrentManagedThreadId;
        NativeWindowType? type = NativeWindow.PlatformWindowType;

        if (editor == null || !editor.HasUserInterface || type == null || !editor.IsEditorWindowTypeSupported((EditorWindowType)type.Value))
        {
            // Nothing to show but the window itself, which keeps the plugin running until it is closed.
            window = NativeWindow.CreateTopLevel(plugin.PluginName, 400, 100, resizable: false);
            window.Closed += WindowLoop.Quit;
            window.Show();
            WindowLoop.Run();
            window.Dispose();
            return;
        }

        window = NativeWindow.CreateTopLevel(plugin.PluginName, (int)editor.EditorWidth, (int)editor.EditorHeight, editor.CanResizeEditor);
        scale = window.Scale;
        window.SetClientSize(Pixels(editor.EditorWidth), Pixels(editor.EditorHeight));

        editorWindow = window.CreateChild(0, 0, window.ClientWidth, window.ClientHeight);
        editor.OpenEditor(editorWindow.Handle, (EditorWindowType)editorWindow.WindowType, scale);
        editorOpen = true;
        editorWindow.KeepControl(); // after the editor wrapped the window (SDL)

        editorWindow.Input += EditorInput;
        editorWindow.Resized += EditorWindowResized;
        window.Resized += (width, height) => editorWindow.SetClientSize(width, height); // stored while fullscreen
        window.ScaleChanged += ScaleChanged;
        window.CloseRequested += () =>
        {
            CloseEditor(); // while its window still exists
            return true;
        };
        window.Closed += WindowLoop.Quit;

        using (WindowLoop.StartTimer(TimeSpan.FromSeconds(1 / 60.0), EditorIdle))
        {
            window.Show();
            editorWindow.Focus();
            try
            {
                WindowLoop.Run();
            }
            finally
            {
                CloseEditor();
                window.Dispose();
            }
        }
    }

    int Pixels(uint units) => Math.Max(1, (int)Math.Round(units * scale));

    uint Units(int pixels) => (uint)Math.Max(1, Math.Round(pixels / scale));

    void EditorIdle(double secondsElapsed)
    {
        if (!editorOpen) return;
        ApplyWindowRequests();
        editor!.EditorIdle(secondsElapsed);
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
            window!.SetClientSize(Pixels(editor.EditorWidth), Pixels(editor.EditorHeight)); // the size it took instead
    }

    // The main window moved to a monitor with another scale: the editor redraws at it, at the same logical size.
    void ScaleChanged(double newScale)
    {
        if (newScale == scale) return;
        scale = newScale;
        if (!editorOpen) return;
        editor!.SetEditorScale(scale);
        window!.SetClientSize(Pixels(editor.EditorWidth), Pixels(editor.EditorHeight));
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
