namespace AudioPlugSharpBridge.Interop;

/// <summary>
/// IAudioHost implemented in managed code (replaces the C++/CLI AudioPlugSharpHost). Transport state comes
/// from the current block, events are dispatched from the block's event array, output events go into the
/// block's output buffer, and only parameter edits call into native code (on the main thread; edits from other
/// threads are queued for it).
///
/// Everything used during Process is allocation-free.
/// </summary>
internal sealed unsafe class ManagedHost : IAudioHost
{
    private readonly HostApiProxy _native;
    private IAudioPluginProcessor _processor = null!;

    // Per-block state (audio thread). _data is only valid inside Process.
    private ProcessData* _data;
    private int _cursor;
    private bool[] _dispatched = new bool[1024];
    private int[] _paramStamp = [];
    private int _stamp;

    public ManagedHost(HostApiProxy native)
    {
        _native = native;
        _mainThreadId = Environment.CurrentManagedThreadId; // created by the adapter's main-thread Create call
    }

    /// <summary>Events that did not fit the host's output buffer (reported on the main thread).</summary>
    public long DroppedOutputEvents { get; private set; }

    /// <summary>Times the event bookkeeping had to grow on the audio thread (an allocation).</summary>
    public long EventBufferGrowths { get; private set; }

    public void Attach(IAudioPluginProcessor processor) => _processor = processor;

    /// <summary>[main] Sizes per-parameter bookkeeping; called after the plugin is initialized.</summary>
    public void Prepare()
    {
        var count = _processor.Parameters?.Count ?? 0;
        if (_paramStamp.Length != count) _paramStamp = new int[count];
    }

    // ---- IAudioHost: transport (set per block) --------------------------------------

    public double SampleRate { get; internal set; }
    public uint MaxAudioBufferSize { get; internal set; }
    public uint CurrentAudioBufferSize { get; private set; }
    public EAudioBitsPerSample BitsPerSample { get; internal set; }
    public double BPM { get; private set; }
    public long CurrentProjectSample { get; private set; }
    public bool IsPlaying { get; private set; }

    // ---- block lifecycle (audio thread) --------------------------------------------

    public void BeginBlock(ProcessData* data)
    {
        _data = data;
        _cursor = 0;
        CurrentAudioBufferSize = data->NumFrames;
        BPM = data->Tempo;
        IsPlaying = (data->TransportFlags & TransportFlags.Playing) != 0;
        CurrentProjectSample = data->ProjectSample;
        data->NumOutputEvents = 0;

        int n = (int)data->NumEvents;
        if (n > _dispatched.Length)
        {
            // Rare: more events in one block than ever before. Growing allocates; it is counted and reported.
            _dispatched = new bool[Math.Max(n, _dispatched.Length * 2)];
            EventBufferGrowths++;
        }
        else
        {
            Array.Clear(_dispatched, 0, n);
        }
    }

    public void EndBlock()
    {
        // Every parameter's clock moves on by the block, so a ramp that isn't finished carries on into the next block.
        var data = _data;
        var parameters = _processor.Parameters;
        int frames = (int)data->NumFrames;
        if (parameters != null)
            for (int i = 0; i < parameters.Count; i++)
                parameters[i].ResetParameterChange(frames);
        _data = null;
    }

    /// <summary>Events-only block (NumFrames == 0): deliver everything, regardless of offset.</summary>
    public void DispatchAll()
    {
        for (int i = 0; i < _data->NumEvents; i++)
            if (!_dispatched[i]) Dispatch(i);
    }

    // ---- IAudioHost: events -----------------------------------------------------------

    /// <summary>
    /// Dispatches the events due at the current position and returns the end of the next segment, i.e. the
    /// sample where more events are due (or the block size). Semantics match the C++/CLI bridge:
    /// MIDI-style events are dispatched at their own offset. Each parameter's next upcoming change point is
    /// dispatched at the start of the current segment (lookahead, so the plugin can interpolate towards it);
    /// it ends the segment one sample after itself only if it is the nearest upcoming event. As a result a
    /// parameter point can be delivered before a MIDI event that comes earlier in the block.
    /// </summary>
    public int ProcessEvents()
    {
        var data = _data;
        if (data == null) return (int)CurrentAudioBufferSize;

        int frames = (int)data->NumFrames;
        int end = frames;
        int count = (int)data->NumEvents;
        int paramCount = _paramStamp.Length;
        int stamp = ++_stamp;

        for (int i = 0; i < count; i++)
        {
            if (_dispatched[i]) continue;
            ref readonly var e = ref data->Events[i];
            int offset = (int)e.SampleOffset;

            if (e.Type == AudioEventType.ParamChange)
            {
                if (e.Index >= (uint)paramCount) { _dispatched[i] = true; continue; }
                if (offset <= _cursor) { Dispatch(i); continue; }
                if (_paramStamp[e.Index] == stamp) continue; // only the first upcoming point per parameter
                _paramStamp[e.Index] = stamp;
                Dispatch(i);
                end = Math.Min(end, offset + 1);
            }
            else if (offset <= _cursor)
            {
                Dispatch(i);
            }
            else
            {
                end = Math.Min(end, offset);
            }
        }

        _cursor = Math.Min(end, frames);
        return _cursor;
    }

    public void ProcessAllEvents()
    {
        if (_data == null) return;
        int frames = (int)_data->NumFrames;
        int next;
        do { next = ProcessEvents(); } while (next < frames);
    }

    private void Dispatch(int i)
    {
        _dispatched[i] = true;
        ref readonly var e = ref _data->Events[i];
        int offset = (int)e.SampleOffset;
        switch (e.Type)
        {
            case AudioEventType.NoteOn:
                _processor.HandleNoteOn(e.Channel, (int)e.Index, (float)e.Value, offset);
                break;
            case AudioEventType.NoteOff:
                _processor.HandleNoteOff(e.Channel, (int)e.Index, (float)e.Value, offset);
                break;
            case AudioEventType.PolyPressure:
                _processor.HandlePolyPressure(e.Channel, (int)e.Index, (float)e.Value, offset);
                break;
            case AudioEventType.ParamChange:
                _processor.HandleParameterChange(_processor.Parameters[(int)e.Index], e.Value, offset);
                break;
            case AudioEventType.ControlChange:
                // MIDI learn-style mappings (AudioPluginBase.AddMidiControllerMapping).
                if (_processor.GetParameterByMidiController(e.Index) is { } mapped)
                    _processor.HandleParameterChange(mapped, Math.Clamp(e.Value / 127.0, 0, 1), offset);
                break;
        }
    }

    public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset) =>
        Emit(AudioEventType.NoteOn, channel, noteNumber, velocity, sampleOffset);

    public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset) =>
        Emit(AudioEventType.NoteOff, channel, noteNumber, velocity, sampleOffset);

    public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset) =>
        Emit(AudioEventType.ControlChange, channel, ccNumber, ccValue, sampleOffset);

    public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset) =>
        Emit(AudioEventType.PolyPressure, channel, noteNumber, pressure, sampleOffset);

    private void Emit(AudioEventType type, int channel, int index, double value, int sampleOffset)
    {
        var data = _data;
        if (data == null || data->OutputEvents == null || data->NumOutputEvents >= data->OutputEventCapacity)
        {
            DroppedOutputEvents++;
            return;
        }
        data->OutputEvents[data->NumOutputEvents++] = new AudioEvent
        {
            Type = type,
            SampleOffset = (uint)Math.Max(0, sampleOffset),
            Index = (uint)index,
            Channel = channel,
            Value = value,
        };
    }

    // ---- IAudioHost: parameter edits (from the editor, any thread) ----------------------

    /// <summary>
    /// Edits are forwarded only once the plugin is initialized. AudioPluginBase.AddParameter sets each
    /// parameter's edit value, which would otherwise report an edit for every parameter during Initialize
    /// (the C++/CLI bridge dropped these too: its controller did not exist yet).
    /// </summary>
    public bool EditsEnabled { get; set; }

    // The adapter takes edits on its main thread only. An editor that renders on a thread of its own
    // (AudioPluginUILayout's render-thread mode) edits from there: those edits are queued, in order, and
    // forwarded by FlushEdits on the main thread (editor idle, editor close). Edits made on the main thread
    // first forward what is queued, so order holds across threads too. Preallocated; a lock (uncontended
    // apart from the moment of a flush) keeps it correct with several editing threads.
    private const int EditQueueSize = 1024;
    private enum EditKind : byte { Begin, Perform, End }
    private struct QueuedEdit { public EditKind Kind; public int Parameter; public double Value; }

    private readonly int _mainThreadId;
    private readonly QueuedEdit[] _editQueue = new QueuedEdit[EditQueueSize];
    private readonly object _editLock = new();
    private int _editCount;
    private long _droppedEdits, _reportedDroppedEdits;

    // Called continuously while a control is dragged, so no closures or other per-call allocation.
    public void BeginEdit(int parameter) => Edit(EditKind.Begin, parameter, 0);

    public void PerformEdit(int parameter, double normalizedValue) => Edit(EditKind.Perform, parameter, normalizedValue);

    public void EndEdit(int parameter) => Edit(EditKind.End, parameter, 0);

    private void Edit(EditKind kind, int parameter, double value)
    {
        if (!EditsEnabled) return;

        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            FlushEdits();
            Forward(kind, parameter, value);
            return;
        }

        lock (_editLock)
        {
            // A Perform for the same parameter as the previous queued one replaces it (the host only needs the
            // latest value), which also keeps a long drag from filling the queue while the main thread is busy.
            if (kind == EditKind.Perform && _editCount > 0)
            {
                ref var last = ref _editQueue[_editCount - 1];
                if (last.Kind == EditKind.Perform && last.Parameter == parameter)
                {
                    last.Value = value;
                    return;
                }
            }
            if (_editCount == EditQueueSize)
            {
                _droppedEdits++;
                return;
            }
            _editQueue[_editCount++] = new QueuedEdit { Kind = kind, Parameter = parameter, Value = value };
        }
    }

    /// <summary>
    /// [main] Forwards what other threads asked for: queued edits, then the latest cursor and fullscreen requests.
    /// </summary>
    public void FlushEditorRequests()
    {
        FlushEdits();
        int cursor = Interlocked.Exchange(ref _pendingCursorVisible, NoRequest);
        if (cursor != NoRequest) ApplyCursorVisible(cursor != 0);
        int fullscreen = Interlocked.Exchange(ref _pendingFullscreen, NoRequest);
        if (fullscreen != NoRequest) ApplyFullscreen(fullscreen != 0);
    }

    /// <summary>
    /// [main] The editor closed: forwards its last edits and drops its window requests (the adapter has already
    /// left fullscreen).
    /// </summary>
    public void EditorClosed()
    {
        Volatile.Write(ref _pendingCursorVisible, NoRequest);
        Volatile.Write(ref _pendingFullscreen, NoRequest);
        FlushEdits();
        IsEditorFullscreen = false;
    }

    private void FlushEdits()
    {
        if (Volatile.Read(ref _editCount) == 0 && _droppedEdits == _reportedDroppedEdits) return;

        lock (_editLock)
        {
            // Under the lock: an editing thread waits for the flush rather than reorder around it. The adapter's
            // edit calls are short (they don't call back into the plugin).
            for (int i = 0; i < _editCount; i++)
                Forward(_editQueue[i].Kind, _editQueue[i].Parameter, _editQueue[i].Value);
            _editCount = 0;

            if (_droppedEdits != _reportedDroppedEdits)
            {
                Logger.Log($"Parameter edit queue full: {_droppedEdits - _reportedDroppedEdits} edits dropped");
                _reportedDroppedEdits = _droppedEdits;
            }
        }
    }

    private void Forward(EditKind kind, int parameter, double value)
    {
        try
        {
            switch (kind)
            {
                case EditKind.Begin: _native.BeginEdit((uint)parameter); break;
                case EditKind.Perform: _native.PerformEdit((uint)parameter, value); break;
                default: _native.EndEdit((uint)parameter); break;
            }
        }
        catch (AbiCallException e) { EditFailed(e); }
    }

    // ---- IAudioHost: editor window services (any thread) ---------------------------------

    // The latest request from another thread (0/1), or NoRequest; applied by FlushEditorRequests.
    private const int NoRequest = -1;
    private int _pendingCursorVisible = NoRequest, _pendingFullscreen = NoRequest;
    private volatile bool _isEditorFullscreen;

    public bool IsEditorFullscreen
    {
        get => _isEditorFullscreen;
        private set => _isEditorFullscreen = value;
    }

    public void SetEditorCursorVisible(bool visible)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId) ApplyCursorVisible(visible);
        else Volatile.Write(ref _pendingCursorVisible, visible ? 1 : 0);
    }

    public void SetEditorFullscreen(bool fullscreen)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId) ApplyFullscreen(fullscreen);
        else Volatile.Write(ref _pendingFullscreen, fullscreen ? 1 : 0);
    }

    private void ApplyCursorVisible(bool visible)
    {
        try { _native.SetEditorCursorVisible(visible); }
        catch (AbiCallException) { /* not supported by this adapter or platform */ }
    }

    private void ApplyFullscreen(bool fullscreen)
    {
        try { IsEditorFullscreen = _native.SetEditorFullscreen(fullscreen); }
        catch (AbiCallException e) { Logger.Log("Editor fullscreen not available: " + e.Message); }
    }

    private static void EditFailed(AbiCallException e) => Logger.Log("Host rejected a parameter edit: " + e.Message);

    // ---- logging ------------------------------------------------------------------------

    /// <summary>[main] Writes to the native host's log (CLAP's host log, or stderr), and to AudioPlugSharp's log
    /// (from Info up; errors written at once, as the process may be about to end).</summary>
    public void Log(LogLevel level, string message)
    {
        if (level >= LogLevel.Info)
        {
            Logger.Log(level == LogLevel.Info ? message : $"{level}: {message}");

            if (level >= LogLevel.Error)
                Logger.Flush();
        }

        try { _native.Log(level, message); }
        catch (AbiCallException) { /* host has no usable log; nothing more to do */ }
    }
}
