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
        string PluginVersion { get; }
        ulong PluginID { get; }

        IAudioHost Host { get; set;  }

        IAudioPluginProcessor Processor { get; }
        IAudioPluginEditor Editor { get; }
    }
}
