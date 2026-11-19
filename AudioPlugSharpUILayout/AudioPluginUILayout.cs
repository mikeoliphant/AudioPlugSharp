using System;
using Microsoft.Xna.Framework.Input;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout
{
    /// <summary>
    /// A plugin whose editor is a UILayout MonoGame (DesktopGL) layout, drawn into the editor window the host
    /// provides (IAudioPluginEditor), on Windows and Linux (X11). Derive from it, set EditorWidth/EditorHeight and
    /// override CreateEditorUI.
    ///
    /// Update, draw and present run in EditorIdle on the host's UI thread, without vsync. The editor is built on
    /// open and discarded on close.
    /// </summary>
    public abstract class AudioPluginUILayout : AudioPluginBase
    {
        MonoGameHost editorHost;

        protected AudioPluginUILayout()
        {
            HasUserInterface = true;
        }

        /// <summary>
        /// The open editor's layout, or null while the editor is closed.
        /// </summary>
        protected MonoGameLayout EditorLayout
        {
            get { return editorHost?.Layout; }
        }

        /// <summary>
        /// Whether the editor is open.
        /// </summary>
        public bool IsEditorOpen
        {
            get { return editorHost != null; }
        }

        /// <summary>
        /// Build the editor's root element. Called on open, after the graphics device and default textures exist.
        /// </summary>
        protected abstract UIElement CreateEditorUI();

        /// <summary>
        /// Create the layout. Override to use a MonoGameLayout subclass.
        /// </summary>
        protected virtual MonoGameLayout CreateEditorLayout()
        {
            return new MonoGameLayout();
        }

        /// <summary>
        /// Called after the editor opened.
        /// </summary>
        protected virtual void EditorOpened()
        {
        }

        /// <summary>
        /// Called before the editor closes.
        /// </summary>
        protected virtual void EditorClosing()
        {
        }

        public override bool IsEditorWindowTypeSupported(EditorWindowType windowType)
        {
            return (windowType == EditorWindowType.Win32) || (windowType == EditorWindowType.X11);
        }

        public override void OpenEditor(IntPtr window, EditorWindowType windowType, double scale)
        {
            if (editorHost != null)
                CloseEditor();

            if (!IsEditorWindowTypeSupported(windowType))
                throw new NotSupportedException("Editor window type " + windowType + " is not supported");

            MonoGameHost host = new MonoGameHost(window, (int)EditorWidth, (int)EditorHeight);

            try
            {
                MonoGameLayout layout = CreateEditorLayout();

                layout.Scale = (float)((scale > 0) ? scale : 1.0);
                host.SetLayout(layout);
                layout.RootUIElement = CreateEditorUI();
                layout.UpdateLayout();
            }
            catch
            {
                // Keep the original exception: disposing calls the layout's Exiting, which may fail on a
                // half-built editor too
                try
                {
                    host.Dispose();
                }
                catch (Exception disposeException)
                {
                    Logger.Log("Editor cleanup after a failed open also failed: " + disposeException);
                }

                throw;
            }

            editorHost = host;

            EditorOpened();
        }

        /// <summary>
        /// Called every editor frame before the layout updates and draws, for example to bring controls up to date
        /// with parameter values the host changed. Runs on the UI thread; should not allocate.
        /// </summary>
        protected virtual void EditorUpdate(float secondsElapsed)
        {
        }

        public override void EditorIdle(double secondsElapsed)
        {
            MonoGameHost host = editorHost;

            if (host == null)
                return;

            EditorUpdate((float)secondsElapsed);

            host.Tick((float)secondsElapsed);
        }

        public override bool EditorInput(in EditorInputEvent inputEvent)
        {
            MonoGameHost host = editorHost;

            if (host == null)
                return false;

            switch (inputEvent.Type)
            {
                case EditorInputEventType.MouseMove:
                    host.MouseMove(inputEvent.X, inputEvent.Y);
                    return true;

                case EditorInputEventType.MouseDown:
                case EditorInputEventType.MouseUp:
                    host.MouseButton((int)inputEvent.Button, inputEvent.Type == EditorInputEventType.MouseDown, inputEvent.X, inputEvent.Y);
                    return true;

                case EditorInputEventType.MouseWheel:
                    host.MouseMove(inputEvent.X, inputEvent.Y);
                    host.MouseWheel(inputEvent.WheelDelta);
                    return true;

                case EditorInputEventType.KeyDown:
                case EditorInputEventType.KeyUp:
                    host.Key((Keys)inputEvent.Key, inputEvent.Type == EditorInputEventType.KeyDown);

                    // Keys are polled by UILayout, so there is no "handled" answer; let the host see them too.
                    return false;

                case EditorInputEventType.Text:
                    return TextInput(host, inputEvent.Character);

                case EditorInputEventType.FocusLost:
                    // Keys only: a drag in progress continues until its button is released. Mouse buttons the
                    // window loses track of (capture taken by another window) arrive as MouseUp from the host.
                    host.ReleaseAllKeys();
                    return true;

                default:
                    return false;
            }
        }

        static bool TextInput(MonoGameHost host, int codePoint)
        {
            // Like SDL text input (what MonoGameHost forwards): no control characters; Backspace/Enter are keys.
            if ((codePoint < 0x20) || (codePoint == 0x7F))
                return false;

            if (codePoint <= 0xFFFF)
                return host.TextInput((char)codePoint);

            if (codePoint > 0x10FFFF)
                return false;

            // Outside the BMP: UILayout takes UTF-16 code units.
            int v = codePoint - 0x10000;

            bool used = host.TextInput((char)(0xD800 + (v >> 10)));

            return host.TextInput((char)(0xDC00 + (v & 0x3FF))) || used;
        }

        public override bool SetEditorSize(uint width, uint height)
        {
            if (!base.SetEditorSize(width, height))
                return false;

            editorHost?.Resize((int)EditorWidth, (int)EditorHeight);

            return true;
        }

        public override void CloseEditor()
        {
            MonoGameHost host = editorHost;

            if (host == null)
                return;

            try
            {
                EditorClosing();
            }
            finally
            {
                editorHost = null;

                host.Dispose();
            }
        }
    }
}
