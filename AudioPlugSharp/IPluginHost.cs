using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioPlugSharp
{
    public interface IPluginHost
    {
        double SampleRate { get; }
        double BPM { get; }
    }
}
