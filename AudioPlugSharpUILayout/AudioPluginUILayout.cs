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
    /// By default update, draw and present run in EditorIdle on the host's UI thread, without vsync (light
    /// editors). Heavy editors set UseEditorRenderThread: frames then run on a thread of the editor's own, with
    /// vsync. The editor is built on open and discarded on close.
    /// </summary>
    public abstract class AudioPluginUILayout : AudioPluginBase
    {
        MonoGameHost editorHost;
        EditorRenderThread renderThread;
        double editorScale = 1.0;

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
        /// Whether the editor renders on a thread of its own (render-thread mode) instead of on the host's UI
        /// thread. For editors whose frames are too heavy for the host's UI timer (which is coarse, and shared
        /// with the host's own UI work), or that want vsync. Set it before the editor opens, for example in the
        /// constructor.
        ///
        /// In render-thread mode EditorUpdate, the layout's update and drawing (and so all UI element code) run on
        /// the render thread. CreateEditorUI, EditorOpened and EditorClosing still run on the UI thread, while the
        /// render thread is not running.
        ///
        /// For testing and diagnosis the environment variable AUDIOPLUGSHARP_EDITOR_RENDER_THREAD overrides it
        /// ("1": render thread, "0": host UI thread).
        /// </summary>
        protected bool UseEditorRenderThread { get; set; }

        /// <summary>
        /// Whether the editor takes every key while it has keyboard focus, so that none of them work as host
        /// shortcuts (for example the space bar for the host's transport). For editors that are used like an
        /// application of their own. By default (false) the editor takes the keys EditorWantsKey names.
        /// </summary>
        protected bool EditorClaimsAllKeys { get; set; }

        /// <summary>
        /// Whether the editor uses a key, so that the host gives it to the editor instead of using it as a shortcut
        /// (hosts ask per key press; Reaper and others then keep the key from their own shortcuts). The editor sees
        /// key events either way. Default: every key with EditorClaimsAllKeys, otherwise the keys a key mapping of
        /// the layout's InputManager reads. Called on the UI thread; should not allocate.
        /// </summary>
        protected virtual bool EditorWantsKey(InputKey key, EditorModifiers modifiers)
        {
            if (EditorClaimsAllKeys)
                return true;

            MonoGameLayout layout = EditorLayout;

            if (layout == null)
                return false;

            try
            {
                // In render-thread mode the mappings belong to the render thread; they are normally only added while
                // the editor opens, so reading them here is safe in practice, and a failure just means "not used"
                return layout.InputManager.IsKeyMapped(key);
            }
            catch (Exception)
            {
                return false;
            }
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

            bool useRenderThread = UseEditorRenderThread;

            string renderThreadOverride = Environment.GetEnvironmentVariable("AUDIOPLUGSHARP_EDITOR_RENDER_THREAD");

            if (renderThreadOverride == "1")
                useRenderThread = true;
            else if (renderThreadOverride == "0")
                useRenderThread = false;

            editorScale = (scale > 0) ? scale : 1.0;

            // The window is the logical size x the UI scale; the layout works in logical units (MonoGameLayout.Scale)
            MonoGameHost host = new MonoGameHost(window, ToWindowPixels(EditorWidth, editorScale), ToWindowPixels(EditorHeight, editorScale), vsync: useRenderThread);

            try
            {
                MonoGameLayout layout = CreateEditorLayout();

                // DisplayScale: the layout's own Scale stays the plugin's (for example a user zoom setting)
                layout.DisplayScale = (float)editorScale;
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

            if (useRenderThread)
            {
                renderThread = new EditorRenderThread(host, RenderFrame, GetType().Name + " editor");

                renderThread.Start();
            }
        }

        void RenderFrame(float secondsElapsed)
        {
            EditorUpdate(secondsElapsed);

            editorHost.Tick(secondsElapsed);
        }

        /// <summary>
        /// Called every editor frame before the layout updates and draws, for example to bring controls up to date
        /// with parameter values the host changed. Runs on the UI thread, or on the render thread in render-thread
        /// mode (UseEditorRenderThread); should not allocate.
        /// </summary>
        protected virtual void EditorUpdate(float secondsElapsed)
        {
        }

        public override void EditorIdle(double secondsElapsed)
        {
            MonoGameHost host = editorHost;

            // In render-thread mode frames run on their own
            if ((host == null) || (renderThread != null))
                return;

            RenderFrame((float)secondsElapsed);
        }

        public override bool EditorInput(in EditorInputEvent inputEvent)
        {
            MonoGameHost host = editorHost;

            if (host == null)
                return false;

            if (renderThread != null)
            {
                renderThread.PostInput(inputEvent);

                // The answer for text depends on the focused element, known only on the render thread: assume used
                return inputEvent.Type switch
                {
                    EditorInputEventType.KeyDown or EditorInputEventType.KeyUp => EditorWantsKey((InputKey)inputEvent.Key, inputEvent.Modifiers),
                    EditorInputEventType.MouseLeave or EditorInputEventType.FocusGained => false,
                    _ => true,
                };
            }

            bool used = ApplyInput(host, inputEvent);

            switch (inputEvent.Type)
            {
                case EditorInputEventType.KeyDown:
                case EditorInputEventType.KeyUp:
                    return EditorWantsKey((InputKey)inputEvent.Key, inputEvent.Modifiers);

                case EditorInputEventType.Text:
                    return used || EditorClaimsAllKeys;

                default:
                    return used;
            }
        }

        /// <summary>
        /// Passes an input event to the host, on the host's thread. Returns whether the editor used it.
        /// </summary>
        internal static bool ApplyInput(MonoGameHost host, in EditorInputEvent inputEvent)
        {
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

                case EditorInputEventType.MouseLeave:
                    host.MouseLeave();

                    return false;

                case EditorInputEventType.KeyDown:
                case EditorInputEventType.KeyUp:
                    host.Key((Keys)inputEvent.Key, inputEvent.Type == EditorInputEventType.KeyDown);

                    // Keys are polled by UILayout, so there is no "handled" answer here (see EditorWantsKey)
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

            ResizeEditorHost(scaleChanged: false);

            return true;
        }

        public override void SetEditorScale(double scale)
        {
            editorScale = (scale > 0) ? scale : 1.0;

            ResizeEditorHost(scaleChanged: true);
        }

        /// <summary>
        /// The UI scale of the open editor (1 = 96 DPI).
        /// </summary>
        public double EditorScale
        {
            get { return editorScale; }
        }

        void ResizeEditorHost(bool scaleChanged)
        {
            int width = ToWindowPixels(EditorWidth, editorScale);
            int height = ToWindowPixels(EditorHeight, editorScale);

            if (renderThread != null)
            {
                if (scaleChanged)
                    renderThread.PostScale((float)editorScale);

                renderThread.PostResize(width, height);
            }
            else if (editorHost != null)
            {
                if (scaleChanged && (editorHost.Layout != null))
                    editorHost.Layout.DisplayScale = (float)editorScale;

                editorHost.Resize(width, height);
            }
        }

        public override void CloseEditor()
        {
            MonoGameHost host = editorHost;

            if (host == null)
                return;

            EditorRenderThread thread = renderThread;

            renderThread = null;

            if ((thread != null) && !thread.Stop())
            {
                // Still rendering: leave the host to it rather than pull the device out from under it (leaks)
                editorHost = null;

                return;
            }

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
