# AudioPlugSharp
Easily create VST3 audio plugins in C#

Very much still a work in progress, but already quite functional.

AudioPlugSharp provides a C++/CLI bridge to load managed audio plugins into VST hosts. User interfaces can be created with built-in support for WPF. Windows Forms interfaces are also possible.

Framework support is .NET Core only. By default, it is configured to use .NET 5.0.

See the [SimpleExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/SimpleExample/SimpleExamplePlugin.cs) and [WPFExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/WPFExample/WPFExamplePlugin.cs) projects for example usage.

# Building Instructions

You will need to have CMake (https://cmake.org) installed.

From a shell, run the following:

```bash
git clone --recursive https://github.com/mikeoliphant/AudioPlugSharp
cd AudioPlugSharp
mkdir vstbuild
cd vstbuild
cmake.exe -G "Visual Studio 16 2019" -A x64 ../vst3sdk
```

Then you can load the solution in Visual Studio and build.
