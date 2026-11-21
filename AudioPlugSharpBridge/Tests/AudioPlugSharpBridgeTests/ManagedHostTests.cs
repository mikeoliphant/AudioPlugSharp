using System.Runtime.InteropServices;
using AudioPlugSharp;
using AudioPlugSharpBridge;
using AudioPlugSharpBridge.Interop;
using Xunit;

namespace AudioPlugSharpBridgeTests;

/// <summary>Records the events the host dispatches to it.</summary>
public sealed class RecordingPlugin : AudioPluginBase
{
    public List<string> Calls { get; } = new();

    public override void Initialize()
    {
        base.Initialize();
        AddParameter(new AudioPluginParameter { ID = "a", Name = "A" });
        AddParameter(new AudioPluginParameter { ID = "b", Name = "B" });
        AddMidiControllerMapping(GetParameter("b"), 7);
    }

    public override void HandleNoteOn(int channel, int noteNumber, float velocity, int sampleOffset) =>
        Calls.Add($"on {noteNumber}@{sampleOffset}");

    public override void HandleNoteOff(int channel, int noteNumber, float velocity, int sampleOffset) =>
        Calls.Add($"off {noteNumber}@{sampleOffset}");

    public override void HandleParameterChange(AudioPluginParameter parameter, double newNormalizedValue, int sampleOffset)
    {
        Calls.Add($"{parameter.ID}={newNormalizedValue:0.##}@{sampleOffset}");
        base.HandleParameterChange(parameter, newNormalizedValue, sampleOffset);
    }
}

public sealed unsafe class ManagedHostTests : IDisposable
{
    private readonly HostApiVTable* _vtable;
    private readonly ManagedHost _host;
    private readonly RecordingPlugin _plugin = new();
    private AudioEvent* _events;
    private AudioEvent* _outEvents;

    public ManagedHostTests()
    {
        // A host that provides nothing: edits and logging fail quietly.
        _vtable = (HostApiVTable*)NativeMemory.AllocZeroed((nuint)sizeof(HostApiVTable));
        _host = new ManagedHost(new HostApiProxy(_vtable));
        _plugin.Host = _host;
        _host.Attach(_plugin);
        _plugin.Initialize();
        _host.Prepare();
        _host.EditsEnabled = true;
    }

    public void Dispose()
    {
        NativeMemory.Free(_vtable);
        NativeMemory.Free(_events);
        NativeMemory.Free(_outEvents);
    }

    private static AudioEvent Note(AudioEventType type, uint offset, uint note) =>
        new() { Type = type, SampleOffset = offset, Index = note, Value = 0.8 };

    private static AudioEvent Param(uint offset, uint index, double value) =>
        new() { Type = AudioEventType.ParamChange, SampleOffset = offset, Index = index, Value = value };

    private ProcessData Block(uint frames, params AudioEvent[] events)
    {
        NativeMemory.Free(_events);
        _events = (AudioEvent*)NativeMemory.Alloc((nuint)Math.Max(events.Length, 1), (nuint)sizeof(AudioEvent));
        for (int i = 0; i < events.Length; i++) _events[i] = events[i];
        if (_outEvents == null) _outEvents = (AudioEvent*)NativeMemory.AllocZeroed(2, (nuint)sizeof(AudioEvent));
        return new ProcessData
        {
            NumFrames = frames,
            Events = _events,
            NumEvents = (uint)events.Length,
            OutputEvents = _outEvents,
            OutputEventCapacity = 2,
        };
    }

    private List<int> Segments(ProcessData* data)
    {
        var ends = new List<int>();
        int next;
        do { next = _host.ProcessEvents(); ends.Add(next); } while (next < data->NumFrames);
        return ends;
    }

    [Fact]
    public void MidiEvents_SplitTheBlockAtTheirOffsets()
    {
        var data = Block(128, Note(AudioEventType.NoteOn, 0, 60), Note(AudioEventType.NoteOn, 64, 62), Note(AudioEventType.NoteOff, 100, 60));
        _host.BeginBlock(&data);
        Assert.Equal([64, 100, 128], Segments(&data));
        Assert.Equal(["on 60@0", "on 62@64", "off 60@100"], _plugin.Calls);
        _host.EndBlock();
    }

    [Fact]
    public void ParamPoints_AreDispatchedAtTheStartOfTheSegmentEndingAtThem()
    {
        // Upstream semantics (C++/CLI AudioPlugSharpHost::ProcessEvents): each parameter's next upcoming point
        // is dispatched at the start of the current segment so the plugin can interpolate towards it, and a
        // point ends the segment one sample after it only if it is the nearest upcoming event.
        //   call 1 (pos 0):  a@32 dispatched; segment ends at 33
        //   call 2 (pos 33): a@96 dispatched (lookahead); the note at 50 ends the segment
        //   call 3 (pos 50): note dispatched; nothing left, segment ends at the block end
        var data = Block(128, Param(32, 0, 0.2), Note(AudioEventType.NoteOn, 50, 60), Param(96, 0, 0.9));
        _host.BeginBlock(&data);
        Assert.Equal([33, 50, 128], Segments(&data));
        Assert.Equal(["a=0.2@32", "a=0.9@96", "on 60@50"], _plugin.Calls);
        _host.EndBlock();
    }

    [Fact]
    public void ParamPointAtZero_IsDispatchedImmediately()
    {
        var data = Block(64, Param(0, 1, 0.5));
        _host.BeginBlock(&data);
        Assert.Equal([64], Segments(&data));
        Assert.Equal(["b=0.5@0"], _plugin.Calls);
        _host.EndBlock();
    }

    [Fact]
    public void ProcessAllEvents_DeliversEverything()
    {
        var data = Block(128, Note(AudioEventType.NoteOn, 10, 60), Param(20, 0, 0.3), Note(AudioEventType.NoteOff, 127, 60));
        _host.BeginBlock(&data);
        _host.ProcessAllEvents();
        // The parameter point is lookahead: it is delivered at the start of the first segment, before the note.
        Assert.Equal(["a=0.3@20", "on 60@10", "off 60@127"], _plugin.Calls);
        _host.EndBlock();
    }

    [Fact]
    public void ControlChange_UsesTheMidiControllerMapping()
    {
        var data = Block(64, new AudioEvent { Type = AudioEventType.ControlChange, Index = 7, Value = 127 },
            new AudioEvent { Type = AudioEventType.ControlChange, Index = 8, Value = 64 });
        _host.BeginBlock(&data);
        _host.ProcessAllEvents();
        Assert.Equal(["b=1@0"], _plugin.Calls); // CC 8 has no mapping
        _host.EndBlock();
    }

    [Fact]
    public void EventsOnlyBlock_DeliversEverythingRegardlessOfOffset()
    {
        var data = Block(0, Param(40, 0, 0.6), Note(AudioEventType.NoteOn, 80, 61));
        _host.BeginBlock(&data);
        _host.DispatchAll();
        Assert.Equal(["a=0.6@40", "on 61@80"], _plugin.Calls);
        _host.EndBlock();
    }

    [Fact]
    public void EndBlock_ResetsParameterInterpolation()
    {
        var data = Block(128, Param(64, 0, 1.0));
        _host.BeginBlock(&data);
        _host.ProcessAllEvents();
        Assert.True(_plugin.GetParameter("a").NeedInterpolationUpdate);
        _host.EndBlock();
        Assert.False(_plugin.GetParameter("a").NeedInterpolationUpdate);
    }

    [Fact]
    public void OutputEvents_FillTheBufferAndCountOverflow()
    {
        var data = Block(64);
        _host.BeginBlock(&data);
        _host.SendNoteOn(1, 60, 0.5f, 3);
        _host.SendCC(1, 74, 100, 5);
        _host.SendNoteOff(1, 60, 0f, 9); // capacity is 2
        Assert.Equal(2u, data.NumOutputEvents);
        Assert.Equal(AudioEventType.NoteOn, _outEvents[0].Type);
        Assert.Equal(3u, _outEvents[0].SampleOffset);
        Assert.Equal(AudioEventType.ControlChange, _outEvents[1].Type);
        Assert.Equal(100, _outEvents[1].Value);
        Assert.Equal(1, _host.DroppedOutputEvents);
        _host.EndBlock();
    }

    [Fact]
    public void ProcessEvents_DoesNotAllocate()
    {
        var data = Block(128, Param(32, 0, 0.2), Note(AudioEventType.NoteOn, 50, 60), Param(96, 1, 0.9),
            new AudioEvent { Type = AudioEventType.ControlChange, Index = 7, Value = 10 });
        // First pass compiles everything (and lets the recording plugin's list grow).
        for (int i = 0; i < 3; i++)
        {
            _host.BeginBlock(&data);
            _host.ProcessAllEvents();
            _host.EndBlock();
        }
        _plugin.Calls.Clear();
        _plugin.Calls.Capacity = 64;

        // Measure only the host's own work: a plugin that ignores events allocates nothing.
        var silent = new SilentPlugin();
        silent.Host = _host;
        _host.Attach(silent);
        silent.Initialize();
        _host.Prepare();
        for (int i = 0; i < 3; i++) { _host.BeginBlock(&data); _host.ProcessAllEvents(); _host.EndBlock(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _host.BeginBlock(&data);
            _host.ProcessAllEvents();
            _host.EndBlock();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class SilentPlugin : AudioPluginBase
    {
        public override void Initialize()
        {
            base.Initialize();
            AddParameter(new AudioPluginParameter { ID = "a", Name = "A" });
            AddParameter(new AudioPluginParameter { ID = "b", Name = "B" });
            AddMidiControllerMapping(GetParameter("b"), 7);
        }
    }
}
