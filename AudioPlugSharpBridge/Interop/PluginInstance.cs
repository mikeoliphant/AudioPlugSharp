using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AudioPlugSharpBridge.Interop;

/// <summary>
/// One plugin instance: implements the ABI contract on top of the AudioPlugSharp API. The native side holds
/// a GCHandle to this (see the generated PluginApiThunks).
/// </summary>
internal sealed unsafe class PluginInstance : IPluginContract, IAbiErrorHandler, IDisposable
{
    private readonly IAudioPlugin _plugin;
    private readonly IAudioPluginProcessor _processor;
    private InstanceLoadContext? _context;

    private SampleFormat _format;
    private uint _maxFrames;
    private bool _isSetUp, _isActive;
    private PortScratch[] _inputScratch = [], _outputScratch = [];
    private Exception? _deferredException;

    // Editor state (main thread).
    private EditorWindowType _editorWindowType; // None while closed

    // Allocation accounting on the audio thread. Bridge = everything in Process outside the plugin's code.
    private long _bridgeBytes, _pluginBytes, _blocks;
    private int _gc0, _gc1, _gc2;
    private TimeSpan _gcPause;

    public PluginInstance(IAudioPlugin plugin, InstanceLoadContext context, ManagedHost host)
    {
        _plugin = plugin;
        _processor = plugin.Processor;
        _context = context;
        Host = host;
    }

    public ManagedHost Host { get; }

    /// <summary>[main] Initialization at creation, in the same order as the C++/CLI bridge.</summary>
    public void Initialize()
    {
        _plugin.Host = Host;
        Host.Attach(_processor);
        _processor.Initialize();
        Host.Prepare();
        Host.EditsEnabled = true;
    }

    // ---- IPluginContract: description ---------------------------------------------

    public PluginInfo GetInfo() => new()
    {
        Company = _plugin.Company,
        Website = _plugin.Website,
        Contact = _plugin.Contact,
        Name = _plugin.PluginName,
        Category = _plugin.PluginCategory,
        Version = _plugin.PluginVersion,
        PluginId = _plugin.PluginID,
        SampleFormats = (SampleFormat)(uint)_processor.SampleFormatsSupported,
        NumInputPorts = (uint)(_processor.InputPorts?.Length ?? 0),
        NumOutputPorts = (uint)(_processor.OutputPorts?.Length ?? 0),
    };

    public PortInfo GetInputPort(uint index) => Port(_processor.InputPorts, index);

    public PortInfo GetOutputPort(uint index) => Port(_processor.OutputPorts, index);

    private static PortInfo Port(AudioIOPort[]? ports, uint index)
    {
        if (ports == null || index >= ports.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return new PortInfo { Name = ports[index].Name, NumChannels = ports[index].NumChannels };
    }

    // ---- IPluginContract: parameters ----------------------------------------------

    public uint GetParamCount() => (uint)(_processor.Parameters?.Count ?? 0);

    public ParamInfo GetParamInfo(uint index)
    {
        var p = Parameter(index);
        return new ParamInfo
        {
            Index = index,
            Key = p.ID,
            Name = p.Name,
            DefaultNormalized = Math.Clamp(p.NormalizedDefaultValue, 0, 1),
        };
    }

    public double GetParamNormalized(uint index) => Parameter(index).NormalizedEditValue;

    public void SetParamNormalized(uint index, double value) => Parameter(index).SetNormalizedValueFromHost(Math.Clamp(value, 0, 1));

    public ParamText FormatParam(uint index, double normalized)
    {
        var p = Parameter(index);
        return new ParamText { Text = p.FormatValue(p.GetValueDenormalized(Math.Clamp(normalized, 0, 1))) };
    }

    public int GetMidiControllerParam(uint controller) =>
        _processor.GetParameterByMidiController(controller) is { } p ? p.ParameterIndex : -1;

    private AudioPluginParameter Parameter(uint index)
    {
        var parameters = _processor.Parameters;
        if (parameters == null || index >= parameters.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return parameters[(int)index];
    }

    // ---- IPluginContract: lifecycle and state -------------------------------------------

    public void SetupProcessing(double sampleRate, uint maxFrames, SampleFormat format)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (maxFrames == 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (format is not (SampleFormat.Float32 or SampleFormat.Float64))
            throw new ArgumentOutOfRangeException(nameof(format), "exactly one sample format");
        if (((uint)_processor.SampleFormatsSupported & (uint)format) == 0)
            throw new ArgumentOutOfRangeException(nameof(format), $"plugin does not support {format}");
        if (_isActive) SetActive(false);

        var bits = (EAudioBitsPerSample)(uint)format;
        Host.SampleRate = sampleRate;
        Host.BitsPerSample = bits;
        Host.MaxAudioBufferSize = maxFrames;
        _processor.InitializeProcessing();
        _processor.SetMaxAudioBufferSize(maxFrames, bits);

        _format = format;
        _maxFrames = maxFrames;
        FreeScratch();
        _inputScratch = AllocScratch(_processor.InputPorts);
        _outputScratch = AllocScratch(_processor.OutputPorts);
        _isSetUp = true;

        WarmUp();
    }

    public void SetActive(bool active)
    {
        if (active == _isActive) return;
        if (active)
        {
            if (!_isSetUp) throw new InvalidOperationException("SetupProcessing must be called before SetActive(true)");
            _bridgeBytes = _pluginBytes = _blocks = 0;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _gcPause = GC.GetTotalPauseDuration();
            _processor.Start();
            _isActive = true;
        }
        else
        {
            _isActive = false;
            _processor.Stop();
            ReportDiagnostics();
        }
    }

    public byte[] SaveState() => _processor.SaveState() ?? [];

    /// <summary>
    /// Restoring sets every parameter's edit value, which would report an edit per parameter to the host.
    /// Hosts read values back after loading state (and could record such edits as automation), so they are
    /// suppressed here.
    /// </summary>
    public void LoadState(ReadOnlySpan<byte> data)
    {
        var editsWereEnabled = Host.EditsEnabled;
        Host.EditsEnabled = false;
        try { _processor.RestoreState(data.ToArray()); }
        finally { Host.EditsEnabled = editsWereEnabled; }
    }

    // ---- IPluginContract: editor (main thread) ------------------------------------------
    //
    // The adapter owns the editor's window and drives the plugin's IAudioPluginEditor: open, idle, input,
    // resize, close.

    public EditorInfo GetEditorInfo()
    {
        var editor = _plugin.Editor;
        if (editor == null || !editor.HasUserInterface) return default;

        var info = new EditorInfo
        {
            Width = editor.EditorWidth,
            Height = editor.EditorHeight,
            Flags = EditorFlags.HasEditor | EditorFlags.Embedded,
        };
        if (editor.CanResizeEditor) info.Flags |= EditorFlags.Resizable;
        foreach (var type in (ReadOnlySpan<EditorWindowType>)[EditorWindowType.Win32, EditorWindowType.X11, EditorWindowType.Cocoa])
            if (editor.IsEditorWindowTypeSupported((global::AudioPlugSharp.EditorWindowType)type))
                info.WindowTypes |= 1u << (int)type;
        return info;
    }

    public void OpenEditor(nint window, EditorWindowType windowType, double scale)
    {
        if (window == 0) throw new ArgumentNullException(nameof(window));
        var info = GetEditorInfo();
        if ((info.Flags & EditorFlags.HasEditor) == 0) throw new NotSupportedException("the plugin has no editor");
        if ((int)windowType <= 0 || (int)windowType >= 32 || (info.WindowTypes & (1u << (int)windowType)) == 0)
            throw new NotSupportedException($"window type {windowType} is not supported by this editor");
        if (_editorWindowType != EditorWindowType.None) CloseEditor();

        using var guard = EditorCallGuard.Enter();
        try { _plugin.Editor.OpenEditor(window, (global::AudioPlugSharp.EditorWindowType)windowType, scale > 0 ? scale : 1.0); }
        catch (Exception e) { throw new PluginCodeException(e); }
        _editorWindowType = windowType;
    }

    public void EditorIdle(double secondsElapsed)
    {
        if (_editorWindowType == EditorWindowType.None) return;
        Host.FlushEditorRequests(); // edits and window requests from an editor's own render thread
        // A frame inside another editor's call (the UI thread pumped messages there) would draw while that
        // editor is half set up, with its OpenGL context current: skip it.
        if (EditorCallGuard.IsActive) return;
        using var guard = EditorCallGuard.Enter();
        try { _plugin.Editor.EditorIdle(Math.Max(secondsElapsed, 0)); }
        catch (Exception e) { throw new PluginCodeException(e); }
    }

    public bool EditorInput(in EditorInputEvent inputEvent)
    {
        if (_editorWindowType == EditorWindowType.None) return false;
        // Same layout (checked in the static constructor), so no copy or conversion.
        try { return _plugin.Editor.EditorInput(in Unsafe.As<EditorInputEvent, global::AudioPlugSharp.EditorInputEvent>(ref Unsafe.AsRef(in inputEvent))); }
        catch (Exception e) { throw new PluginCodeException(e); }
    }

    public bool SetEditorSize(uint width, uint height)
    {
        if (width == 0 || height == 0) throw new ArgumentOutOfRangeException(nameof(width));
        var editor = _plugin.Editor;
        if (editor == null) return false;
        using var guard = EditorCallGuard.Enter();
        try { return editor.SetEditorSize(width, height); }
        catch (Exception e) { throw new PluginCodeException(e); }
    }

    public void SetEditorScale(double scale)
    {
        if (_editorWindowType == EditorWindowType.None) return;
        if (!(scale > 0) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        using var guard = EditorCallGuard.Enter();
        try { _plugin.Editor.SetEditorScale(scale); }
        catch (Exception e) { throw new PluginCodeException(e); }
    }

    public void CloseEditor()
    {
        if (_editorWindowType == EditorWindowType.None) return;
        _editorWindowType = EditorWindowType.None;
        using var guard = EditorCallGuard.Enter();
        try { _plugin.Editor.CloseEditor(); }
        catch (Exception e) { throw new PluginCodeException(e); }
        finally { Host.EditorClosed(); } // the last edits of an editor's own render thread (stopped now)
    }

    /// <summary>
    /// Tracks editor calls in progress (open, idle, resize, close) across every plugin instance in the process,
    /// including other modules' bridge copies, which share the runtime and therefore AppContext. The UI thread can
    /// pump messages inside such a call (for example while the CLR waits on an STA thread), and an editor frame
    /// that runs there would find another editor's OpenGL context half set up.
    /// </summary>
    private readonly struct EditorCallGuard : IDisposable
    {
        private const string Key = "AudioPlugSharpBridge.EditorCallDepth";
        private static readonly StrongBox<int> Depth = SharedDepth();

        private static StrongBox<int> SharedDepth()
        {
            lock (AppDomain.CurrentDomain) // one per process, whichever bridge copy runs this
            {
                if (AppContext.GetData(Key) is StrongBox<int> existing) return existing;
                var depth = new StrongBox<int>();
                AppContext.SetData(Key, depth);
                return depth;
            }
        }

        public static bool IsActive => Volatile.Read(ref Depth.Value) > 0;

        public static EditorCallGuard Enter()
        {
            Interlocked.Increment(ref Depth.Value);
            return default;
        }

        public void Dispose() => Interlocked.Decrement(ref Depth.Value);
    }

    /// <summary>
    /// An exception thrown by the plugin's own code. Wrapped so that the generated thunks log it (they map a bare
    /// ArgumentException or NotSupportedException to a result code for the caller, without logging).
    /// </summary>
    private sealed class PluginCodeException(Exception inner) : Exception(inner.Message, inner);

    static PluginInstance()
    {
        // EditorInput reinterprets the contract struct as the AudioPlugSharp one.
        if (sizeof(EditorInputEvent) != sizeof(global::AudioPlugSharp.EditorInputEvent)
            || sizeof(EditorInputEvent) != 8 * sizeof(int))
            throw new InvalidOperationException("EditorInputEvent layout differs between the contract and AudioPlugSharp");
    }

    // ---- IPluginContract: processing (audio thread) ---------------------------------

    public void Process(in ProcessData data)
    {
        // `data` refers to native (or stack) memory, never the GC heap, so taking its address is safe.
        var d = (ProcessData*)Unsafe.AsPointer(ref Unsafe.AsRef(in data));
        long start = GC.GetAllocatedBytesForCurrentThread();
        long inPlugin = 0;
        try
        {
            if (!_isSetUp) throw new InvalidOperationException("Process called before SetupProcessing");
            Host.BeginBlock(d);
            if (d->NumFrames == 0)
            {
                // Events only (e.g. a parameter flush while stopped). The event handlers are plugin code.
                long pluginStart = GC.GetAllocatedBytesForCurrentThread();
                Host.DispatchAll();
                inPlugin = GC.GetAllocatedBytesForCurrentThread() - pluginStart;
            }
            else
            {
                if (d->SampleFormat != _format) throw new ArgumentException("sample format differs from SetupProcessing");
                if (d->NumFrames > _maxFrames) throw new ArgumentException("more frames than SetupProcessing allowed");

                BindPorts(_processor.InputPorts, d->Inputs, d->NumInputPorts, _inputScratch, d->NumFrames, clear: true);
                BindPorts(_processor.OutputPorts, d->Outputs, d->NumOutputPorts, _outputScratch, d->NumFrames, clear: false);

                // Everything the plugin does, including the event handlers it triggers via ProcessEvents.
                long pluginStart = GC.GetAllocatedBytesForCurrentThread();
                _processor.PreProcess();
                _processor.Process();
                _processor.PostProcess();
                inPlugin = GC.GetAllocatedBytesForCurrentThread() - pluginStart;
            }
            Host.EndBlock();
        }
        catch (Exception e)
        {
            Silence(d);
            Interlocked.CompareExchange(ref _deferredException, e, null);
            return; // exception paths allocate; they are reported separately, not counted
        }

        long total = GC.GetAllocatedBytesForCurrentThread() - start;
        _pluginBytes += inPlugin;
        _bridgeBytes += total - inPlugin;
        _blocks++;
        AllocationCounters.Add(total - inPlugin, inPlugin);
    }

    private void BindPorts(AudioIOPort[]? ports, AudioPortBuffers* buffers, uint count, PortScratch[] scratch,
        uint frames, bool clear)
    {
        if (ports == null) return;
        for (int i = 0; i < ports.Length; i++)
        {
            var port = ports[i];
            void** channels;
            if (i < count && buffers[i].Channels != null && buffers[i].NumChannels >= port.NumChannels)
            {
                channels = buffers[i].Channels;
            }
            else
            {
                // The host gave us fewer ports or channels than the plugin has: use silent scratch buffers.
                channels = scratch[i].Channels;
                if (clear) scratch[i].Clear(frames);
            }
            port.SetCurrentBufferSize(frames);
            port.SetAudioBufferPtrs((IntPtr)channels);
        }
    }

    private void Silence(ProcessData* d)
    {
        try
        {
            if (d == null || d->Outputs == null || d->NumFrames == 0) return;
            nuint bytes = d->NumFrames * (nuint)(d->SampleFormat == SampleFormat.Float64 ? 8 : 4);
            for (int p = 0; p < d->NumOutputPorts; p++)
                for (int c = 0; c < d->Outputs[p].NumChannels; c++)
                    if (d->Outputs[p].Channels[c] != null) NativeMemory.Clear(d->Outputs[p].Channels[c], bytes);
        }
        catch { /* best effort */ }
    }

    // ---- warm-up ----------------------------------------------------------------------

    /// <summary>
    /// Runs the full native-to-managed process path on silent scratch buffers, on the main thread, so the
    /// reverse-P/Invoke stub, the thunk, generic instantiations and static constructors are ready before
    /// the audio thread calls in. Uses a no-op parameter change (the current value) rather than notes, so the
    /// plugin's state is not changed.
    /// </summary>
    private void WarmUp()
    {
        int inPorts = _inputScratch.Length, outPorts = _outputScratch.Length;
        var inputs = stackalloc AudioPortBuffers[Math.Max(inPorts, 1)];
        var outputs = stackalloc AudioPortBuffers[Math.Max(outPorts, 1)];
        for (int i = 0; i < inPorts; i++) inputs[i] = _inputScratch[i].AsBuffers();
        for (int i = 0; i < outPorts; i++) outputs[i] = _outputScratch[i].AsBuffers();

        var events = stackalloc AudioEvent[1];
        uint numEvents = 0;
        if (_processor.Parameters is { Count: > 0 } parameters)
        {
            events[0] = new AudioEvent { Type = AudioEventType.ParamChange, Index = 0, Value = parameters[0].NormalizedProcessValue };
            numEvents = 1;
        }
        var outEvents = stackalloc AudioEvent[16];

        var data = new ProcessData
        {
            Inputs = inputs,
            Outputs = outputs,
            Events = events,
            OutputEvents = outEvents,
            NumInputPorts = (uint)inPorts,
            NumOutputPorts = (uint)outPorts,
            NumFrames = _maxFrames,
            SampleFormat = _format,
            OutputEventCapacity = 16,
            Tempo = 120,
        };

        var handle = GCHandle.Alloc(this);
        try
        {
            delegate* unmanaged<void*, ProcessData*, int> process = &PluginApiThunks.Process;
            void* self = (void*)GCHandle.ToIntPtr(handle);
            for (int i = 0; i < 16; i++)
            {
                data.NumEvents = i % 4 == 0 ? numEvents : 0;
                process(self, &data);
            }
            // The events-only path, too.
            data.NumFrames = 0;
            data.NumEvents = numEvents;
            process(self, &data);
        }
        finally
        {
            handle.Free();
        }
        ReportDeferredException();
    }

    // ---- diagnostics / errors -----------------------------------------------------------

    private void ReportDiagnostics()
    {
        ReportDeferredException();
        if (_blocks == 0) return;
        var pause = GC.GetTotalPauseDuration() - _gcPause;
        var summary =
            $"{_plugin.PluginName}: {_blocks} blocks; audio-thread allocations: bridge {_bridgeBytes} B, plugin {_pluginBytes} B; " +
            $"GCs while active: gen0 {GC.CollectionCount(0) - _gc0}, gen1 {GC.CollectionCount(1) - _gc1}, " +
            $"gen2 {GC.CollectionCount(2) - _gc2}, pause {pause.TotalMilliseconds:0.0} ms";
        Host.Log(LogLevel.Info, summary);
        // Also in AudioPlugSharp's log file (%APPDATA%/AudioPlugSharp on Windows): many hosts don't show plugin logs.
        Logger.Log(summary);
        if (Host.DroppedOutputEvents > 0) Host.Log(LogLevel.Warning, $"{Host.DroppedOutputEvents} output events dropped (host buffer full)");
        if (Host.EventBufferGrowths > 0) Host.Log(LogLevel.Warning, $"event bookkeeping grew {Host.EventBufferGrowths} time(s) on the audio thread");
    }

    private void ReportDeferredException()
    {
        var e = Interlocked.Exchange(ref _deferredException, null);
        if (e != null) Host.Log(LogLevel.Error, $"Exception in Process (outputs were silenced): {e}");
    }

    public void OnAbiException(string member, Exception exception)
    {
        if (exception is PluginCodeException { InnerException: { } inner }) exception = inner;
        if (member == nameof(Process))
            Interlocked.CompareExchange(ref _deferredException, exception, null); // audio thread: report later
        else
            Host.Log(LogLevel.Error, $"Exception in {member}: {exception}");
    }

    /// <summary>Called by the generated Destroy thunk.</summary>
    public void Dispose()
    {
        try
        {
            try { CloseEditor(); }
            catch (Exception e) { Host.Log(LogLevel.Error, $"Closing the editor failed: {e}"); }
            if (_isActive) SetActive(false);
        }
        finally
        {
            FreeScratch();
            Unload();
        }
    }

    /// <summary>[main] Starts unloading the instance's load context (unless shared); a later GC collects it.</summary>
    public void Unload()
    {
        if (_context is { Shared: false }) _context.Unload();
        _context = null;
    }

    // ---- scratch buffers for ports the host did not provide ------------------------------

    private PortScratch[] AllocScratch(AudioIOPort[]? ports)
    {
        if (ports == null) return [];
        var result = new PortScratch[ports.Length];
        int sampleSize = _format == SampleFormat.Float64 ? 8 : 4;
        for (int i = 0; i < ports.Length; i++) result[i] = new PortScratch(ports[i].NumChannels, _maxFrames, sampleSize);
        return result;
    }

    private void FreeScratch()
    {
        foreach (var s in _inputScratch) s.Free();
        foreach (var s in _outputScratch) s.Free();
        _inputScratch = _outputScratch = [];
    }

    private sealed class PortScratch
    {
        private readonly uint _numChannels;
        private readonly int _sampleSize;
        private void* _memory;

        public PortScratch(uint numChannels, uint maxFrames, int sampleSize)
        {
            _numChannels = numChannels;
            _sampleSize = sampleSize;
            uint channels = Math.Max(numChannels, 1);
            _memory = NativeMemory.AllocZeroed((nuint)(channels * (ulong)sizeof(void*) + channels * (ulong)maxFrames * (ulong)sampleSize));
            Channels = (void**)_memory;
            byte* data = (byte*)_memory + channels * (nuint)sizeof(void*);
            for (uint c = 0; c < channels; c++) Channels[c] = data + c * maxFrames * (nuint)sampleSize;
        }

        public void** Channels { get; private set; }

        public AudioPortBuffers AsBuffers() => new() { Channels = Channels, NumChannels = _numChannels };

        public void Clear(uint frames)
        {
            for (uint c = 0; c < _numChannels; c++) NativeMemory.Clear(Channels[c], frames * (nuint)_sampleSize);
        }

        public void Free()
        {
            if (_memory != null) NativeMemory.Free(_memory);
            _memory = null;
            Channels = null;
        }
    }
}

/// <summary>Process-wide audio-thread allocation totals, read by the diagnostics entry points.</summary>
internal static class AllocationCounters
{
    private static long _bridge, _plugin;

    public static void Add(long bridge, long plugin)
    {
        if (bridge != 0) Interlocked.Add(ref _bridge, bridge);
        if (plugin != 0) Interlocked.Add(ref _plugin, plugin);
    }

    public static long Bridge => Interlocked.Read(ref _bridge);
    public static long Plugin => Interlocked.Read(ref _plugin);

    public static void Reset()
    {
        Interlocked.Exchange(ref _bridge, 0);
        Interlocked.Exchange(ref _plugin, 0);
    }
}
