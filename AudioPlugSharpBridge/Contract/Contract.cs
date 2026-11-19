// The native/managed ABI contract: the single source of truth for the boundary between the native
// format adapters (CLAP, VST3, LV2, test hosts) and AudioPlugSharpBridge. Tools/AbiGen generates the C
// header, the C++ wrappers and the C# interop code from this file. See DESIGN.md §5 and §6.
//
// Rules: append members at the end only (new ones must be [OptionalMember]); any other change requires
// bumping AbiConfig.Version. Every interface method needs [MainThread] or [AudioThread].
using AudioPlugSharpBridge;

// The contract is a schema: many fields are only written by generated code or by native code.
#pragma warning disable CS0649

[assembly: AbiConfig(Version = 2, CPrefix = "aps", CppNamespace = "aps", CSharpNamespace = "AudioPlugSharpBridge.Interop")]

namespace AudioPlugSharpBridge;

/// <summary>Severity of a log message.</summary>
internal enum LogLevel : int
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Sample formats. Values match AudioPlugSharp's EAudioBitsPerSample.</summary>
[Flags]
internal enum SampleFormat : uint
{
    None = 0,
    Float32 = 1,
    Float64 = 2,
}

/// <summary>Kind of a realtime event.</summary>
internal enum AudioEventType : uint
{
    /// <summary>Index = note number, Value = velocity (0-1).</summary>
    NoteOn = 1,
    /// <summary>Index = note number, Value = velocity (0-1).</summary>
    NoteOff = 2,
    /// <summary>Index = note number, Value = pressure (0-1).</summary>
    PolyPressure = 3,
    /// <summary>Index = controller number, Value = controller value (0-127).</summary>
    ControlChange = 4,
    /// <summary>Index = parameter index, Value = normalized value (0-1). Input only.</summary>
    ParamChange = 5,
}

/// <summary>Transport state bits.</summary>
[Flags]
internal enum TransportFlags : uint
{
    None = 0,
    Playing = 1,
}

/// <summary>A sample-accurate event, in either direction.</summary>
[AbiStruct]
internal struct AudioEvent
{
    public AudioEventType Type;
    /// <summary>Offset within the block. Input events are sorted by offset.</summary>
    public uint SampleOffset;
    /// <summary>Note number, controller number or parameter index, depending on Type.</summary>
    public uint Index;
    /// <summary>MIDI channel (0-15); 0 for parameter changes.</summary>
    public int Channel;
    /// <summary>Velocity, pressure, controller value or normalized parameter value, depending on Type.</summary>
    public double Value;
}

/// <summary>The channel buffers of one audio port (bus).</summary>
[AbiStruct]
internal unsafe struct AudioPortBuffers
{
    /// <summary>[NumChannels][NumFrames] of float or double, per ProcessData.SampleFormat.</summary>
    public void** Channels;
    public uint NumChannels;
    public uint Reserved;
}

/// <summary>One block of audio and events, passed to Process.</summary>
[AbiStruct]
internal unsafe struct ProcessData
{
    /// <summary>One entry per input port. Buffers may alias the outputs.</summary>
    [Const] public AudioPortBuffers* Inputs;
    /// <summary>One entry per output port.</summary>
    public AudioPortBuffers* Outputs;
    /// <summary>Input events, sorted by SampleOffset. May be null when NumEvents is 0.</summary>
    [Const] public AudioEvent* Events;
    /// <summary>Space for events the plugin sends (notes, CC). The plugin sets NumOutputEvents.</summary>
    public AudioEvent* OutputEvents;
    public uint NumInputPorts;
    public uint NumOutputPorts;
    /// <summary>0 means "events only": no audio is processed, events are still delivered.</summary>
    public uint NumFrames;
    /// <summary>Must match the format passed to SetupProcessing.</summary>
    public SampleFormat SampleFormat;
    public uint NumEvents;
    public uint OutputEventCapacity;
    /// <summary>Written by the plugin.</summary>
    public uint NumOutputEvents;
    public TransportFlags TransportFlags;
    /// <summary>Position of the first sample of the block in the project timeline.</summary>
    public long ProjectSample;
    /// <summary>Tempo in BPM.</summary>
    public double Tempo;
}

/// <summary>Static plugin description.</summary>
[AbiRecord]
internal struct PluginInfo
{
    [FixedString(64)] public string Company;
    [FixedString(256)] public string Website;
    [FixedString(128)] public string Contact;
    [FixedString(64)] public string Name;
    /// <summary>For example "Fx" or "Instrument|Synth".</summary>
    [FixedString(128)] public string Category;
    [FixedString(64)] public string Version;
    /// <summary>Unique 64-bit id (AudioPlugSharp's PluginID).</summary>
    public ulong PluginId;
    public SampleFormat SampleFormats;
    public uint NumInputPorts;
    public uint NumOutputPorts;
    public uint Reserved;
}

/// <summary>Description of one audio port (bus).</summary>
[AbiRecord]
internal struct PortInfo
{
    [FixedString(64)] public string Name;
    public uint NumChannels;
}

/// <summary>Description of one parameter. Values cross the ABI normalized (0-1).</summary>
[AbiRecord]
internal struct ParamInfo
{
    /// <summary>Position in the plugin's parameter list.</summary>
    public uint Index;
    /// <summary>Reserved, 0.</summary>
    public uint Flags;
    /// <summary>The plugin's stable string id (AudioPluginParameter.ID).</summary>
    [FixedString(64)] public string Key;
    [FixedString(64)] public string Name;
    public double DefaultNormalized;
}

/// <summary>A parameter value formatted for display.</summary>
[AbiRecord]
internal struct ParamText
{
    [FixedString(128)] public string Text;
}

/// <summary>Static host description.</summary>
[AbiRecord]
internal struct HostInfo
{
    [FixedString(64)] public string Name;
}

/// <summary>Kind of native window handle for an embedded editor. Values match AudioPlugSharp.EditorWindowType.</summary>
internal enum EditorWindowType : int
{
    None = 0,
    /// <summary>HWND.</summary>
    Win32 = 1,
    /// <summary>X11 Window id.</summary>
    X11 = 2,
    /// <summary>NSView* (reserved).</summary>
    Cocoa = 3,
}

/// <summary>What kind of editor a plugin has.</summary>
[Flags]
internal enum EditorFlags : uint
{
    None = 0,
    /// <summary>The plugin has an editor.</summary>
    HasEditor = 1,
    /// <summary>
    /// The editor runs in the adapter's window and takes idle calls and input events (IAudioPluginEditor). Always
    /// set with HasEditor since the legacy ShowEditor path was removed; kept so the ABI does not change.
    /// </summary>
    Embedded = 2,
    /// <summary>The host may resize the editor (SetEditorSize).</summary>
    Resizable = 4,
}

/// <summary>Kind of an editor input event. Values match AudioPlugSharp.EditorInputEventType.</summary>
internal enum EditorInputEventType : int
{
    None = 0,
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseWheel = 4,
    MouseLeave = 5,
    KeyDown = 6,
    KeyUp = 7,
    Text = 8,
    FocusGained = 9,
    FocusLost = 10,
}

/// <summary>Editor description: size in window pixels, kind, and supported window types.</summary>
[AbiStruct]
internal struct EditorInfo
{
    /// <summary>The editor's size in logical units (1 unit = 1 pixel at scale 1, 96 DPI); window pixels = units x scale.</summary>
    public uint Width;
    public uint Height;
    public EditorFlags Flags;
    /// <summary>Bit (1 &lt;&lt; EditorWindowType) for every supported window type.</summary>
    public uint WindowTypes;
}

/// <summary>
/// An input event for an embedded editor. Same layout as AudioPlugSharp.EditorInputEvent; see there for the
/// field meanings (window pixels, Windows virtual-key codes, UTF-32 text).
/// </summary>
[AbiStruct]
internal struct EditorInputEvent
{
    public EditorInputEventType Type;
    /// <summary>Bits: 1 Shift, 2 Control, 4 Alt, 8 Meta.</summary>
    public int Modifiers;
    public int X;
    public int Y;
    /// <summary>0 left, 1 right, 2 middle.</summary>
    public int Button;
    /// <summary>120 per notch, positive away from the user.</summary>
    public int WheelDelta;
    /// <summary>Windows virtual-key code (left/right-specific for Shift, Control and Alt).</summary>
    public int Key;
    /// <summary>Unicode code point.</summary>
    public int Character;
}

/// <summary>An AudioPlugSharp plugin instance. Implemented in C#, called by the native adapter.</summary>
[ManagedApi("plugin")]
internal interface IPluginContract
{
    /// <summary>Static description of the plugin.</summary>
    [MainThread] PluginInfo GetInfo();

    [MainThread] PortInfo GetInputPort(uint index);

    [MainThread] PortInfo GetOutputPort(uint index);

    [MainThread] uint GetParamCount();

    [MainThread] ParamInfo GetParamInfo(uint index);

    /// <summary>Current (edit) value of a parameter, normalized.</summary>
    [MainThread] double GetParamNormalized(uint index);

    /// <summary>The host changed a parameter (automation read on the main thread, generic UI, ...).</summary>
    [MainThread] void SetParamNormalized(uint index, double value);

    /// <summary>Display text for a normalized value of a parameter.</summary>
    [MainThread] ParamText FormatParam(uint index, double normalized);

    /// <summary>Prepare for processing. Also warms up (JIT-compiles) the process path.</summary>
    [MainThread] void SetupProcessing(double sampleRate, uint maxFrames, SampleFormat format);

    /// <summary>Start (true) or stop (false) processing.</summary>
    [MainThread] void SetActive(bool active);

    /// <summary>Serialized plugin state.</summary>
    [MainThread] byte[] SaveState();

    [MainThread] void LoadState(ReadOnlySpan<byte> data);

    /// <summary>Process one block. Only valid between SetActive(true) and SetActive(false).</summary>
    [AudioThread] void Process(in ProcessData data);

    // ---- editor (appended in ABI version 2; all optional). Main (UI) thread only. ----

    /// <summary>The plugin's editor, if any.</summary>
    [MainThread, OptionalMember] EditorInfo GetEditorInfo();

    /// <summary>
    /// Open the editor in <paramref name="window"/>, a child window the adapter created with the editor's size in
    /// window pixels (logical size x <paramref name="scale"/>). <paramref name="scale"/> is the UI scale (1 = 96 DPI),
    /// from the host or the OS.
    /// </summary>
    [MainThread, OptionalMember] void OpenEditor(nint window, EditorWindowType windowType, double scale);

    /// <summary>Called about 60 times a second while the editor is open (embedded editors draw here).</summary>
    [MainThread, OptionalMember] void EditorIdle(double secondsElapsed);

    /// <summary>An input event for an embedded editor. Returns whether the editor used it.</summary>
    [MainThread, OptionalMember] bool EditorInput(in EditorInputEvent inputEvent);

    /// <summary>
    /// The host resized the editor (logical units). Returns whether the size was accepted; GetEditorInfo then has the
    /// editor's size.
    /// </summary>
    [MainThread, OptionalMember] bool SetEditorSize(uint width, uint height);

    /// <summary>Close the editor. The window still belongs to the adapter.</summary>
    [MainThread, OptionalMember] void CloseEditor();

    // ---- appended in ABI version 2 (optional) ----

    /// <summary>
    /// The parameter index a MIDI controller number (0-127) is mapped to (AudioPluginBase.AddMidiControllerMapping),
    /// or -1. For formats where the host turns CCs into parameter changes (VST3 IMidiMapping).
    /// </summary>
    [MainThread, OptionalMember] int GetMidiControllerParam(uint controller);

    // ---- appended in ABI version 2 (optional) ----

    /// <summary>
    /// The UI scale of the open editor changed (the host's scale, or the window moved to a monitor with another DPI).
    /// The adapter resizes the window to the editor's logical size x <paramref name="scale"/>.
    /// </summary>
    [MainThread, OptionalMember] void SetEditorScale(double scale);
}

/// <summary>Services of the native adapter. Implemented natively, called from C#.</summary>
[NativeApi("host")]
internal interface IHostContract
{
    /// <summary>Static description of the host.</summary>
    [MainThread] HostInfo GetInfo();

    /// <summary>Write a log message.</summary>
    [MainThread] void Log(LogLevel level, string message);

    /// <summary>The plugin starts changing a parameter (e.g. mouse down on a control).</summary>
    [MainThread] void BeginEdit(uint paramIndex);

    /// <summary>The plugin changed a parameter (normalized value).</summary>
    [MainThread] void PerformEdit(uint paramIndex, double normalized);

    /// <summary>The plugin finished changing a parameter.</summary>
    [MainThread] void EndEdit(uint paramIndex);

    // ---- editor window services (appended in ABI version 2; optional) ----

    /// <summary>Show or hide the mouse cursor while it is over the open editor's window.</summary>
    [MainThread, OptionalMember] void SetEditorCursorVisible(bool visible);

    /// <summary>
    /// Move the open editor's window out of the host's window into a borderless window covering its monitor (true),
    /// or back (false). The adapter resizes the editor to match (SetEditorSize), and keeps reporting the windowed
    /// size to the host. Returns whether the editor is fullscreen afterwards.
    /// </summary>
    [MainThread, OptionalMember] bool SetEditorFullscreen(bool fullscreen);
}
