# AudioPlugSharpBridge

The bridge is what connects an AudioPlugSharp plugin to a DAW. A small native **format adapter** (CLAP or VST3) is what the DAW loads. It starts .NET and hands everything over to the managed bridge (**AudioPlugSharpBridge.dll**), which loads the plugin and implements **IAudioHost** for it.

It replaces 0.7's C++/CLI VST3 bridge, which only worked on Windows. The goals were:

- CLAP and VST3 (and possibly LV2 later) on Windows x64, Linux x64 and Linux arm64, designed so macOS can follow
- the plugin API (**IAudioPlugin**, **AudioPluginBase**, ports, parameters) mostly unchanged
- realtime safety on the audio thread

# Structure

```
 DAW
  |  CLAP / VST3, and the editor's parent window
  v
 format adapter (C++, one binary per format and platform)    Native/Clap, Native/Vst3
 shared native core: .NET hosting, editor window, input      Native/Core
  |  generated C ABI
  v
 managed bridge (C#, one copy per process)                   Interop/, NativeEntry.cs
  |  the AudioPlugSharp API
  v
 the plugin, in a load context of its own per instance
```

**Adapters only translate.** Everything that is the same in every format is written once, in C#: event dispatch (**ProcessEvents**), output events, parameter mapping and normalization, state, editor handling. An adapter maps its format's calls and events onto the ABI and does nothing else. That keeps each new format small, and the plugin behaves the same in all of them.

# Hosting .NET

The adapter boots the runtime through hostfxr, using the bridge's runtime config, and calls an `[UnmanagedCallersOnly]` entry point. The adapter finds its own location and loads the plugin assembly next to it: **MyPlugin.clap** loads **MyPlugin.dll**.

- **One runtime per process,** shared by every AudioPlugSharp plugin in the DAW. The runtime config rolls forward to the newest installed major version (.NET 10 at minimum), so plugins built against different versions can share it. If another vendor's .NET plugin boots the runtime first, its settings win. That limitation is accepted.
- **One collectible load context per plugin instance.** Plugin statics are never shared, so instances can't interfere with each other, and closing an instance unloads its code. AudioPlugSharp and the bridge resolve to one shared copy, so types match across instances. The .NET framework and native libraries aren't isolated: the OS loads native libraries once per process. Each instance JITs its own copy of the plugin and its dependencies. Static event subscriptions on shared types, threads that are never stopped, and caches holding plugin types keep a context alive after it should have unloaded.

# The ABI

The adapters and the bridge talk through a flat C ABI: tables of function pointers and blittable structs. There's no COM and no C++/CLI, so it works the same on every platform and compiler. A C# contract (**Contract/**) defines it, and [AbiGen](Tools/AbiGen/README.md) generates both sides from it, so they can't drift apart.

The ABI is internal: it connects only the adapters and the bridge, which always ship together. Plugin compatibility depends only on the public C# API.

# Realtime

- **No marshalling on the audio thread.** The host's `float**`/`double**` buffers are passed straight to the plugin's ports, and events arrive as one sorted array per block.
- **No allocation in the bridge's audio path.** Tests measure it and fail on anything other than 0 bytes. Output events go into preallocated buffers. Exceptions are caught, the outputs are silenced, and the error is logged later on the main thread.
- **No JIT on the audio thread.** Tiered compilation is off. When a plugin is activated, the plugin's `[Realtime]` methods and everything they call are compiled within the instance's load context. The real process path is then run on silent buffers to warm it up.
- **GC:** workstation GC with concurrent collection, `RetainVM` and `SustainedLowLatency`. The GC isn't tuned beyond that. All plugins share one GC heap, and a collection triggered by any thread can pause the audio thread. So the policy is to not create garbage where it matters. The framework itself is allocation-free in steady state (audio path as a hard rule, editor frames as a goal), because plugin authors can't see or avoid its allocations.

# Editors

The DAW provides a parent window. The native core creates the editor's own child window inside it and gives the managed editor its handle (**IAudioPluginEditor.OpenEditor**). The core translates input natively and forwards it (**EditorInput**), and calls **EditorIdle** on a timer.

- **Why a child window of our own:** it gets its own window procedure on Windows, and its own X connection and event mask on Linux. Our input then never mixes with the DAW's or another plugin's, and we can resize it, change its cursor, take it fullscreen and destroy it without touching the DAW's window.
- **Why the editor doesn't run its own window and event loop:** 0.7's UILayout editors ran a MonoGame **Game** on a thread of their own. On Linux, MonoGame uses SDL, which has one event queue for the whole process, so several editors in one DAW would consume each other's events. Embedded editors only use SDL for their OpenGL context, and input comes from the core.
- Editors can still render on a thread of their own. The UILayout editor handles that on the managed side; the contract doesn't change.

# Formats

- **CLAP** is closest to the ABI. Parameter IDs are a hash of each parameter's key, so they stay stable when parameters are added.
- **VST3** uses only the SDK's interface headers (MIT licensed), not its helper classes or build system. The plugin is a single component (processor and controller in one object), because both halves need the same managed instance. It keeps 0.7's processor class ID, parameter IDs and state bytes, so projects saved with 0.7 plugins still load.

# Packaging

The **AudioPlugSharpBridge** package holds the bridge, the adapters for every platform (under **native/**, not **runtimes/**, which NuGet would copy into every plugin's output) and MSBuild targets. After each build, the targets assemble the plugin's CLAP and VST3 bundles: the plugin's output, the bridge, and the adapter renamed after the plugin.

On Linux, the adapters link the C++ runtime statically and are built on Ubuntu 22.04, so the only requirement is glibc 2.35 or newer.

# Tests

- **AudioPlugSharpBridgeTests:** event dispatch, parameter gestures and interpolation, audio ports
- **AbiGenTests:** the generator
- **BridgeTestHost**, **ClapTestHost**, **Vst3TestHost:** native hosts that load real plugins through the adapters. They check processing (including zero allocation), parameters, state and editors, with **Tests/Plugins** as the test plugins.

CI runs all of them on Windows x64, Linux x64 and Linux arm64.
