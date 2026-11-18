using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using AudioPlugSharp;

[assembly: System.Windows.ThemeInfo(
        System.Windows.ResourceDictionaryLocation.None,
        System.Windows.ResourceDictionaryLocation.SourceAssembly
    )]

namespace AudioPlugSharpWPF
{
    /// <summary>
    /// A plugin whose editor is a WPF UserControl (Windows only). The control is hosted in an HwndSource: a child
    /// window of the editor window the host provides, which takes its input directly from Windows, so the idle and
    /// input calls of IAudioPluginEditor are not needed. WPF runs on the host's UI thread, which must be STA (as
    /// DAWs' UI threads are).
    /// </summary>
    public class AudioPluginWPF : AudioPluginBase
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        HwndSource editorSource;

        public UserControl EditorView { get; set; }

        public AudioPluginWPF()
        {
            // WPF requires a shared assembly load context: https://github.com/dotnet/wpf/issues/1700
            CacheLoadContext = true;
        }

        public virtual UserControl GetEditorView()
        {
            return new EditorView();
        }

        public override bool IsEditorWindowTypeSupported(EditorWindowType windowType)
        {
            return windowType == EditorWindowType.Win32;
        }

        public override void OpenEditor(IntPtr window, EditorWindowType windowType, double scale)
        {
            if (!IsEditorWindowTypeSupported(windowType))
                throw new NotSupportedException("WPF editors need a Win32 window");

            CloseEditor();

            Logger.Log("Open editor. Thread ID is: " + System.Threading.Thread.CurrentThread.ManagedThreadId);

            if (EditorView == null)
                EditorView = GetEditorView();

            EditorView.DataContext = Processor;

            var parameters = new HwndSourceParameters("AudioPlugSharpWPF")
            {
                ParentWindow = window,
                WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                PositionX = 0,
                PositionY = 0,
                Width = (int)EditorWidth,
                Height = (int)EditorHeight,
            };

            editorSource = new HwndSource(parameters);
            editorSource.RootVisual = EditorView;
        }

        public override bool SetEditorSize(uint width, uint height)
        {
            if (!base.SetEditorSize(width, height))
                return false;

            if (editorSource != null)
                SetWindowPos(editorSource.Handle, IntPtr.Zero, 0, 0, (int)EditorWidth, (int)EditorHeight, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

            return true;
        }

        public override void CloseEditor()
        {
            if (editorSource == null)
                return;

            // Detach the view so it can be shown again in the next window.
            editorSource.RootVisual = null;
            editorSource.Dispose();
            editorSource = null;
        }
    }
}
