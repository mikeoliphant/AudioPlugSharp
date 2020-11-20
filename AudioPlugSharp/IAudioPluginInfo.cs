using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioPluginInfo
    {
        string Company { get; }
        string Website { get; }
        string Contact { get; }
        string PluginName { get; }
        string PluginCategory { get; }
        string PluginVerstion { get; }
        string ProcessorGuid { get; }
        string ControllerGuid { get; }
    }
}
