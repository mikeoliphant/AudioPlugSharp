using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace AudioPlugSharp
{
    public interface IAudioPluginEditor
    {
        IAudioHost Host { get; }
        uint EditorWidth { get; }
        uint EditorHeight { get; }
        public IReadOnlyList<AudioPluginParameter> Parameters { get; }
        IAudioPluginProcessor Processor { get; }

        bool HasUserInterface { get; }

        double GetDpiScale();
        void InitializeEditor();
        void ResizeEditor(uint newWidth, uint newHeight);
        void ShowEditor(IntPtr parentWindow);
        void HideEditor();
    }
}
