# AudioPlugSharp
Easily create VST3 audio plugins in C#

Very much still a work in progress, but already quite functional.

AudioPlugSharp provides a C++/CLI bridge to load managed audio plugins into VST hosts. User interfaces can be created with built-in support for WPF. Windows Forms interfaces are also possible.

Framework support is .NET Core only. By default, it is configured to use .NET 5.0.

See the [SimpleExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/SimpleExample/SimpleExamplePlugin.cs) and [WPFExample](https://github.com/mikeoliphant/AudioPlugSharp/blob/master/WPFExample/WPFExamplePlugin.cs) projects for example usage.
