# AudioPlugSharp
Easily create VST3 audio plugins in C#

AudioPlugSharp provides a C++/CLI bridge to load managed audio plugins into VST hosts. User interfaces can be created with built-in support for WPF. Windows Forms interfaces are also possible.

# Current Release

NuGet packages are available:

[AudioPlugSharp](https://www.nuget.org/packages/AudioPlugSharp) (Needed for all plugins)

[AudioPlugSharpVst3](https://www.nuget.org/packages/AudioPlugSharp) (Need for all VST3 plugins)

[AudioPlugSharpWPF](https://www.nuget.org/packages/AudioPlugSharpWPF) (Also needed for WPF user interfaces)

[AudioPlugSharpHost](https://www.nuget.org/packages/AudioPlugSharpHost) (For createing stand-alone Windows applictions)

[AudioPlugSharpJack](https://www.nuget.org/packages/AudioPlugSharpJack) (For createing cross-platform Jack Audio clients)

# Plugin Project Setup and Deployment

## NuGet Packages
Using the NuGet packages is the recommended way to build a project using AudioPlugSharp.

If you use the AudioPlugSharp/AudioPlugSharpWPF NuGet packages, copying the appropriate files to your output folder will be handled for you. You simply need to copy the files from the output folder to a folder on your VST plugin path (usually "**C:\Program Files\Common Files\VST3**").

## Direct Referecing
If you are referencing AudioPlugSharp manually, your plugin project will need an assembly dependency on **AudioPlugSharp.dll** (and **AudioPlugSharpWPF.dll** if you are using it).

For deployment, you need to copy **"AudioPlugSharpVst.vst3"** to your output folder, and rename it to be **"YourPluginDllNameBridge.vst3"**. So if your plugin dll is called **"MyPlugin.dll"**, then you would rename **"AudioPlugSharpVst.vst3"** to **"MyPluginBridge.vst3"**. You also need to copy **"AudioPlugSharpVst.runtimeconfig.json"** (or **"wpf.runtimeconfig.json"** if you are using WPF in your plugin) to your output folder as **"YourPluginDllNameBridge.runtimeconfig.json"**. You also need to copy **"Ijwhost.dll"** to your output folder.
  
These steps can be done using a Post-build event. Have a look at the included sample plugins for examples - keep in mind you may need to change the source folder of the "copy" commands depending on where your copy of AudioPlugSharp is.

# Examples

See the [SimpleExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/SimpleExample/SimpleExamplePlugin.cs), [MidiExample](https://github.com/mikeoliphant/AudioPlugSharp/tree/master/MidiExample) and [WPFExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/WPFExample/WPFExamplePlugin.cs) projects for example usage.

Here are some examples of some larger projects using AudioPlugSharp:

[ChartPlayer](https://github.com/mikeoliphant/ChartPlayer), an app for playing along to song charts synchronized to music recordings.

[Stompbox](https://github.com/mikeoliphant/StompboxUI), guitar amplifier and effects simulation.

[LiveSPICE VST](https://github.com/dsharlet/LiveSPICE/tree/master/LiveSPICEVst), real time SPICE simulation for audio signals.

# Creating a Stand-Alone Application

The [AudioPlugSharpHost library](https://github.com/mikeoliphant/AudioPlugSharp/tree/master/AudioPlugSharpHost) can be used to easily create a stand-alone Windows application for your plugin.

# AudioPlugSharp Building Instructions

You will need to have CMake (https://cmake.org) installed.

From a shell, run the following:

```bash
git clone --recursive https://github.com/mikeoliphant/AudioPlugSharp
cd AudioPlugSharp
cd vstbuild
cmake.exe -G "Visual Studio 17 2022" -A x64 ../vst3sdk -DSMTG_CREATE_PLUGIN_LINK=0
```

Then you can load the solution in Visual Studio and build. **Note that you will need to change the last line above if you have a different version of Visual Studio.**
