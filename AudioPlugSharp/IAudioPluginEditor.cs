using System;
using System.Collections.Generic;

namespace AudioPlugSharp
{
    /// <summary>
    /// The kind of native window handle passed to <see cref="IAudioPluginEditor.OpenEditor"/>.
    /// </summary>
    public enum EditorWindowType : int
    {
        None = 0,
        /// <summary>Windows: an HWND.</summary>
        Win32 = 1,
        /// <summary>Linux/BSD: an X11 Window id (XWayland under Wayland hosts).</summary>
        X11 = 2,
        /// <summary>macOS: an NSView pointer (reserved).</summary>
        Cocoa = 3,
    }

    /// <summary>
    /// A plugin's editor. The host side (a plugin format adapter, or a standalone host) owns the editor's window
    /// and drives the editor:
    ///
    /// - It creates a child window of <see cref="EditorWidth"/> x <see cref="EditorHeight"/> window pixels and
    ///   calls <see cref="OpenEditor"/> with it.
    /// - While the editor is open it calls <see cref="EditorIdle"/> about 60 times a second, and forwards input
    ///   to <see cref="EditorInput"/>.
    /// - <see cref="CloseEditor"/> ends it; the window still belongs to the host side.
    ///
    /// All calls are made on the host's UI thread. An editor that renders on a thread of its own must queue
    /// input itself. An editor may instead put a native child window of its own inside the given window (for
    /// example WPF on Windows); that window then receives its input directly, and idle and input calls can be
    /// ignored. Steady-state calls (idle, input) should not allocate.
    /// </summary>
    public interface IAudioPluginEditor
    {
        IAudioHost Host { get; }
        IReadOnlyList<AudioPluginParameter> Parameters { get; }
        IAudioPluginProcessor Processor { get; }

        /// <summary>Whether the plugin has an editor at all.</summary>
        bool HasUserInterface { get; }

        /// <summary>The editor's size in window pixels.</summary>
        uint EditorWidth { get; }
        uint EditorHeight { get; }

        /// <summary>Whether the editor can draw into a window of this type.</summary>
        bool IsEditorWindowTypeSupported(EditorWindowType windowType);

        /// <summary>Whether the host may resize the editor (via <see cref="SetEditorSize"/>).</summary>
        bool CanResizeEditor { get; }

        /// <summary>
        /// Start the editor in <paramref name="window"/>, which already has the editor's size.
        /// </summary>
        /// <param name="window">The native window (see <paramref name="windowType"/>).</param>
        /// <param name="windowType">The kind of handle.</param>
        /// <param name="scale">The host's UI scale (1.0 = 96 DPI). Sizes stay in window pixels.</param>
        void OpenEditor(IntPtr window, EditorWindowType windowType, double scale);

        /// <summary>Called regularly while the editor is open, on the UI thread.</summary>
        /// <param name="secondsElapsed">Time since the previous call (0 on the first one).</param>
        void EditorIdle(double secondsElapsed);

        /// <summary>An input event for the editor window. Returns true if the editor used it.</summary>
        /// <remarks>
        /// Unused key events are given back to the host where the format allows it (for example the space bar for
        /// the transport).
        /// </remarks>
        bool EditorInput(in EditorInputEvent inputEvent);

        /// <summary>
        /// The host resized the editor window (window pixels). Returns false if the size was not accepted;
        /// <see cref="EditorWidth"/>/<see cref="EditorHeight"/> then hold the size the editor will use.
        /// </summary>
        bool SetEditorSize(uint width, uint height);

        /// <summary>Stop the editor and release everything tied to the window.</summary>
        void CloseEditor();
    }
}
