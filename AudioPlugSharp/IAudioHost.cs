using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioPlugSharp
{
    public interface IAudioHost
    {
        double SampleRate { get; }
        UInt32 MaxAudioBufferSize { get; }
        UInt32 CurrentAudioBufferSize { get; }
        EAudioBitsPerSample BitsPerSample { get; }
        double BPM { get; }
        Int64 CurrentProjectSample { get; }
        bool IsPlaying { get; }

        void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset);
        void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset);
        void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset);
        void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset);

        void ProcessAllEvents();
        int ProcessEvents();

        void BeginEdit(int parameter);
        void PerformEdit(int parameter, double normalizedValue);
        void EndEdit(int parameter);

        // Editor window services, for editors drawn into the window the host side provides (IAudioPluginEditor).
        // They may be called from any thread (for example an editor's own render thread); the host side applies
        // them on its UI thread, so the effect can come a frame later. Hosts that don't support them ignore them.

        /// <summary>
        /// Show or hide the mouse cursor while it is over the editor's window (for example while playing back).
        /// </summary>
        void SetEditorCursorVisible(bool visible)
        {
        }

        /// <summary>
        /// Move the editor's window into a borderless window covering its monitor (true), or back into the host's
        /// window (false). The editor is resized to match (<see cref="IAudioPluginEditor.SetEditorSize"/>), so it
        /// must be resizable (<see cref="IAudioPluginEditor.CanResizeEditor"/>). Closing the editor leaves
        /// fullscreen.
        /// </summary>
        void SetEditorFullscreen(bool fullscreen)
        {
        }

        /// <summary>Whether the editor's window is fullscreen (see <see cref="SetEditorFullscreen"/>).</summary>
        bool IsEditorFullscreen
        {
            get { return false; }
        }
    }
}
