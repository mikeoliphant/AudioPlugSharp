# AudioPlugSharp
Easily create CLAP and VST3 audio plugins in C#

AudioPlugSharp lets you write audio plugins in C# and use them in DAWs on Windows and Linux, as CLAP and VST3 plugins. User interfaces can be created with [UILayout](https://github.com/mikeoliphant/UILayout) (Windows and Linux) or WPF (Windows only).

# Current Release

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

## Upgrading From Earlier Versions
Plugins written for version 0.7 need some changes, mostly in their user interface code: the DAW now provides the window your editor draws into. The C++/CLI bridge (**AudioPlugSharpVst3**) is replaced by **AudioPlugSharpBridge**, and the post-build copy steps are no longer needed.

# Examples

See the [SimpleExample](Examples/SimpleExample/SimpleExamplePlugin.cs), [MidiExample](Examples/MidiExample/MidiExamplePlugin.cs), [UILayoutExample](Examples/UILayoutExample/UILayoutExamplePlugin.cs) and [WPFExample](Examples/WPFExample/WPFExamplePlugin.cs) projects for example usage.

Here are some examples of some larger projects using AudioPlugSharp:

[ChartPlayer](https://github.com/mikeoliphant/ChartPlayer), an app for playing along to song charts synchronized to music recordings.

[Stompbox](https://github.com/mikeoliphant/StompboxUI), guitar amplifier and effects simulation.


# Creating a Stand-Alone Application

Stand-alone applications are being reworked for the cross-platform version. Until then, use version 0.7 and its [AudioPlugSharpHost](https://www.nuget.org/packages/AudioPlugSharpHost) (Windows) or [AudioPlugSharpJack](https://www.nuget.org/packages/AudioPlugSharpJack) (Jack Audio) packages.

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
