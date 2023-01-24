# AudioPlugSharp
Easily create VST3 audio plugins in C#

AudioPlugSharp provides a C++/CLI bridge to load managed audio plugins into VST hosts. User interfaces can be created with built-in support for WPF. Windows Forms interfaces are also possible.

Framework support is .NET Core only. By default, it is configured to use .NET 6.0.

See the [SimpleExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/SimpleExample/SimpleExamplePlugin.cs) and [WPFExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/WPFExample/WPFExamplePlugin.cs) projects for example usage.

# Current Release

NuGet packages are available:

[AudioPlugSharp](https://www.nuget.org/packages/AudioPlugSharp) (Needed for all plugins)

[AudioPlugSharpWPF](https://www.nuget.org/packages/AudioPlugSharpWPF)  (Also needed for WPF user interfaces)

# Plugin Project Setup and Deployment

Your plugin project will need an assembly dependency on **AudioPlugSharp.dll** (and **AudioPlugSharpWPF.dll** if you are using it).

For deployment, you need to copy **"AudioPlugSharpVst.vst3"** to your output folder, and rename it to be **"YourPluginDllNameBridge.vst3"**. So if your plugin dll is called **"MyPlugin.dll"**, then you would rename **"AudioPlugSharpVst.vst3"** to **"MyPluginBridge.vst3"**. You also need to copy **"AudioPlugSharpVst.runtimeconfig.json"** (or **"wpf.runtimeconfig.json"** if you are using WPF in your plugin) to your output folder as **"YourPluginDllNameBridge.runtimeconfig.json"**. You also need to copy **"Ijwhost.dll"** to your output folder.
  
These steps can be done using a Post-build event. Have a look at the included sample plugins for examples - keep in mind you may need to change the source folder of the "copy" commands depending on where your copy of AudioPlugSharp is.

# Examples

For an example of how to create your own plugin in its own solution, have a look at the LiveSpice VST plugin here:

https://github.com/dsharlet/LiveSPICE/tree/master/LiveSPICEVst

# AudioPlugSharp Building Instructions

You will need to have CMake (https://cmake.org) installed.

From a shell, run the following:

```bash
git clone --recursive https://github.com/mikeoliphant/AudioPlugSharp
cd AudioPlugSharp
mkdir vstbuild
cd vstbuild
cmake.exe -G "Visual Studio 17 2022" -A x64 ../vst3sdk
```

Then you can load the solution in Visual Studio and build. **Note that you will need to change the last line above if you have a different version of Visual Studio.**
