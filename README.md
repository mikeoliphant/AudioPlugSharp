# AudioPlugSharp
Easily create CLAP and VST3 audio plugins in C#

AudioPlugSharp lets you write audio plugins in C# and use them in DAWs on Windows and Linux, as CLAP and VST3 plugins. User interfaces can be created with [UILayout](https://github.com/mikeoliphant/UILayout) (Windows and Linux) or WPF (Windows only) - see [Plugin Editors](Docs/Editors.md).

# Current Release

**Note:** the packages on NuGet are for the previous version (0.7), which made Windows-only VST3 plugins. This version isn't on NuGet yet (**AudioPlugSharpBridge** isn't there at all): to use it now, build it from this repository - see [Building Instructions](#audioplugsharp-building-instructions).

NuGet packages are available:

[AudioPlugSharp](https://www.nuget.org/packages/AudioPlugSharp) (Needed for all plugins)

[AudioPlugSharpBridge](https://www.nuget.org/packages/AudioPlugSharpBridge) (Needed to load your plugin in a DAW - it creates the CLAP and VST3 plugin files)

[AudioPlugSharpWPF](https://www.nuget.org/packages/AudioPlugSharpWPF) (Also needed for WPF user interfaces)

For UILayout user interfaces, reference the **AudioPlugSharpUILayout** project from this repository (it is not on NuGet yet).

# Plugin Project Setup and Deployment

## NuGet Packages
Add the **AudioPlugSharpBridge** package to your plugin project (it brings in AudioPlugSharp). When you build, it creates your plugin in the output folder, ready to install:

- **clap/MyPlugin** - copy this folder to your CLAP plugin folder (**C:\Program Files\Common Files\CLAP** on Windows, **~/.clap** on Linux)
- **vst3/MyPlugin.vst3** - copy this to your VST3 plugin folder (**C:\Program Files\Common Files\VST3** on Windows, **~/.vst3** on Linux)

The plugin is created for the platform you build on. To create Linux plugins on Windows (or the other way around), add for example **-p:AudioPlugSharpBridgeRuntimeIdentifier=linux-x64** (or **linux-arm64**, **win-x64**) to your build.

## Requirements
Plugins need the .NET 10 runtime (or newer) to be installed. On Windows, install the **.NET Desktop Runtime**, which also covers WPF.

On Linux, plugins work on Ubuntu 22.04 or newer (or similarly recent distributions), on x64 and arm64. To install the .NET runtime on Ubuntu:

```bash
sudo apt update
sudo apt install dotnet-runtime-10.0
```

On Ubuntu 22.04, first add the .NET backports repository with ```sudo add-apt-repository ppa:dotnet/backports```.

## Logs
AudioPlugSharp writes its logs to **%APPDATA%\AudioPlugSharp** on Windows (**~/.config/AudioPlugSharp** on Linux): one file per session, keeping the last ten. If a plugin doesn't load at all - for example because the .NET runtime is missing - look in **native.log** there.

## Upgrading From Earlier Versions
Plugins written for version 0.7 need some changes, mostly in their user interface code: the DAW now provides the window your editor draws into. The C++/CLI bridge (**AudioPlugSharpVst3**) is replaced by **AudioPlugSharpBridge**. See the [migration notes](Docs/Migration-0.8.md) for details.

# Examples

The [Examples](Examples) folder has a project for each of these:

- **[SimpleExample](Examples/SimpleExample/SimpleExamplePlugin.cs)**: a stereo gain with no editor of its own. The simplest plugin.
- **[MidiExample](Examples/MidiExample/MidiExamplePlugin.cs)**: a simple sine-wave synthesizer, playing MIDI notes.
- **[ParameterEditorExample](Examples/ParameterEditorExample/ParameterEditorExamplePlugin.cs)**: a stereo drive whose editor is generated automatically from its parameters.
- **[UILayoutExample](Examples/UILayoutExample/UILayoutExamplePlugin.cs)**: a stereo gain and mute with an editor of its own built with UILayout.
- **[WPFExample](Examples/WPFExample/WPFExamplePlugin.cs)**: a stereo gain and pan with a WPF editor. Windows only.

Here are some examples of some larger projects using AudioPlugSharp:

[ChartPlayer](https://github.com/mikeoliphant/ChartPlayer), an app for playing along to song charts synchronized to music recordings.

[Stompbox](https://github.com/mikeoliphant/StompboxUI), guitar amplifier and effects simulation.


# Creating a Stand-Alone Application

**AudioPlugSharpStandalone** runs your plugin as an application, on Windows and Linux, with its editor and a status bar showing the audio device and how busy the plugin is. Build it from this repository, then start it with your plugin's DLL from its build output:

```bash
dotnet build AudioPlugSharpStandalone -c Release
AudioPlugSharpStandalone/bin/Release/net10.0/AudioPlugSharpStandalone path/to/MyPlugin.dll
```

Audio goes through ASIO (Windows), ALSA (Linux) or JACK (where it is installed). Click **Audio** in the status bar to choose the driver, device, channels, sample rate and buffer size, and to test them with a tone or by looping the inputs back. Your choices and the plugin's state are saved, in a folder named after the plugin in your application data folder.

Options:

- **--save-folder <folder>** - keep the plugin's state and audio settings somewhere else
- **--audio <driver>**, **--audio-device <name>**, **--sample-rate <hz>**, **--buffer-size <frames>** - choose the audio device from the command line
- **--list-audio** - list the audio drivers and their devices

To make an application of your own for your plugin, create a console project that references **AudioPlugSharpStandalone** and your plugin project, with this as its program:

```csharp
using AudioPlugSharpStandalone;

class Program
{
    [STAThread]
    static void Main() => StandaloneHost.ForPlugin<MyPlugin>().Run();
}
```

Also add `<TieredCompilation>false</TieredCompilation>` to the project's properties, as AudioPlugSharpStandalone does: it avoids dropouts at small buffer sizes.

MIDI is not supported yet.

# AudioPlugSharp Building Instructions

You will need the .NET 10 SDK, CMake (https://cmake.org) and a C++ compiler (Visual Studio on Windows; GCC or Clang and the X11 development files, such as **libx11-dev**, on Linux). On Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0 cmake g++ libx11-dev
```

From a shell, run the following:

```bash
git clone --recursive https://github.com/mikeoliphant/AudioPlugSharp
cd AudioPlugSharp
cmake -S AudioPlugSharpBridge/Native -B build
cmake --build build --config Release
```

This builds everything, including CLAP and VST3 versions of the examples in **build/bin/clap** and **build/bin/vst3**. You can also load **AudioPlugSharp.sln** in Visual Studio to work on the C# code.
