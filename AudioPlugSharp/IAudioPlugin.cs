using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioPlugin
    {
        string Company { get; }
        string Website { get; }
        string Contact { get; }
        string PluginName { get; }
        string PluginCategory { get; }
        string PluginVerstion { get; }
        string ProcessorGuid { get; }
        string ControllerGuid { get; }

        IAudioProcessor CreateProcessor();
        IAudioController CreateController();
    }
}
