using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public enum EAudioChannelConfiguration
    {
        Mono,
        Stereo
    }

    public class AudioIOPort
    {
        public string Name { get; set; }
        public EAudioChannelConfiguration ChannelConfiguration { get; set; }
    }
}
