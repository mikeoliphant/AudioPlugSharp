using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioPluginEditor
    {
        uint EditorWidth { get; }
        uint EditorHeight { get; }
        IAudioPluginProcessor Processor { get; }

        void InitializeEditor();
        bool ShowEditor(IntPtr parentWindow);
    }
}
