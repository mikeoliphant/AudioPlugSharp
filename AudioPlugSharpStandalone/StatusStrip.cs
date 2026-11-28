using AudioIOSharp;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
using AudioPlugSharpWindowing;
using Microsoft.Xna.Framework;
using UILayout;

namespace AudioPlugSharpStandalone;

/// <summary>One thing the status strip shows. A warning is drawn in amber.</summary>
public readonly record struct StatusItem(string Text, bool Warning = false);

/// <summary>
/// The strip along the bottom of the standalone's window: the audio device and how it's running, and an Audio button at
/// the right that opens the audio settings below the strip (the window grows downward). A child window below the
/// editor's, with a UILayout layout of the host's own: the plugin's UILayout, if it has one, is another copy (the
/// plugin's load context), so neither changes the other's fonts, colors or settings. Items are shown in priority
/// order, as many as fit beside the button, the first that doesn't fit cut short (see <see cref="StatusRow"/>). Runs
/// on the UI thread.
/// <para>For a plugin without an editor of its own, the strip's window also shows an editor generated from the
/// plugin's parameters (a <see cref="ParameterPanel"/>) above the strip: one layout for both, since a copy of UILayout
/// has one layout.</para>
/// </summary>
public class StatusStrip : IDisposable
{
    /// <summary>The strip's height in logical units (the default font is 26 high).</summary>
    public const int Height = 36;

    static readonly UIColor Background = new(36, 38, 44), TextColor = new(196, 200, 208), WarningColor = new(255, 180, 60);
    const float Spacing = 18, Margin = 8;

    // The Audio button's room above and below it in the strip.
    const int ButtonInset = 3;
    const int DividerHeight = 1;

    readonly NativeWindow window;
    readonly MonoGameHost host;
    readonly MonoGameLayout layout;
    readonly StatusRow row;
    readonly AudioSettingsPanel? panel;
    readonly ParameterPanel? parameterPanel;
    readonly TextButton? audioButton;
    readonly UIElement? divider;
    IReadOnlyList<StatusItem> items = [];
    readonly int panelHeight;
    bool disposed, open;
    bool? pendingOpen;

    /// <summary>Creates the strip's window in <paramref name="parent"/>'s client area (pixels). With a panel (made
    /// once the strip's layout exists), the strip has an Audio button that opens the panel below it, set off from the
    /// strip by a divider line. With parameters, a generated editor for them is above the strip (<see cref="EditorSize"/>
    /// gives its size).</summary>
    public StatusStrip(NativeWindow parent, int y, int width, int height, double scale, Func<StatusStrip, AudioSettingsPanel>? createPanel = null,
        IReadOnlyList<AudioPluginParameter>? parameters = null)
    {
        if ((parameters != null) && (parameters.Count > 0))
        {
            EditorSize(parameters, out uint editorWidth, out uint editorHeight);

            EditorWidth = (int)editorWidth;
            EditorHeight = (int)editorHeight;
        }

        window = parent.CreateChild(0, y, width, height);

        try
        {
            host = new MonoGameHost(window.Handle, window.ClientWidth, window.ClientHeight) { ClearColor = new Color(Background.R, Background.G, Background.B) };
        }
        catch
        {
            window.Dispose();
            throw;
        }

        try
        {
            layout = new MonoGameLayout { DisplayScale = (float)scale };
            host.SetLayout(layout);

            AudioTextures.Load();

            row = new StatusRow
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Center,
                ChildSpacing = Spacing,
            };

            var stripRow = new Dock
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                DesiredHeight = Height,
                Padding = new LayoutPadding(Margin, 0),
            };
            stripRow.Children.Add(row);

            var root = new VerticalStack
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                BackgroundColor = Background,
            };

            if (EditorHeight > 0)
            {
                // EditorHeight high (its cells and padding, until the window is resized: SetEditorHeight)
                parameterPanel = new ParameterPanel(parameters!)
                {
                    VerticalAlignment = EVerticalAlignment.Top,
                };

                SetPanelHeight();

                root.Children.Add(parameterPanel);
            }

            root.Children.Add(stripRow);

            if (createPanel != null)
            {
                // A toggle: pressed while the panel is open.
                audioButton = new TextButton("Audio", () => SetOpen(audioButton!.IsPressed))
                {
                    IsToggleButton = true,
                    HorizontalAlignment = EHorizontalAlignment.Right,
                    DesiredHeight = Height - 2 * ButtonInset,
                };
                stripRow.Children.Add(audioButton);

                // The row ends where the button begins, so the items fit in the room beside it.
                audioButton.GetSize(out float buttonWidth, out _);
                row.Margin = new LayoutPadding(0, 0, buttonWidth + Spacing, 0);

                divider = new UIElement
                {
                    HorizontalAlignment = EHorizontalAlignment.Stretch,
                    DesiredHeight = DividerHeight,
                    BackgroundColor = Layout.Current.Theme.Border,
                    Visible = false,
                };
                root.Children.Add(divider);

                panel = createPanel(this);

                panel.GetSize(out _, out float measured);
                panelHeight = DividerHeight + (int)Math.Ceiling(measured);

                panel.Visible = false;
                root.Children.Add(panel);
            }

            layout.RootUIElement = root;
            layout.UpdateLayout();
        }
        catch
        {
            host.Dispose();
            window.Dispose();
            throw;
        }

        window.KeepControl(); // after SDL wrapped the window
        window.Resized += host.Resize;
        window.Input += OnInput;
    }

    /// <summary>The strip was clicked.</summary>
    public event Action? Clicked;

    /// <summary>The panel opened or closed: the strip's window needs <see cref="TotalHeight"/> now.</summary>
    public event Action? OpenChanged;

    /// <summary>Whether the settings panel is open.</summary>
    public bool IsOpen => open;

    /// <summary>The settings panel, if the strip has one.</summary>
    public AudioSettingsPanel? Panel => panel;

    /// <summary>The height the strip's window needs, in logical units: the generated editor (if any), the strip, and
    /// the panel when it's open.</summary>
    public int TotalHeight => EditorHeight + HeightBelowEditor;

    /// <summary>The height below the generated editor, in logical units: the strip, and the panel when it's open.</summary>
    public int HeightBelowEditor => Height + (open ? panelHeight : 0);

    /// <summary>Whether the strip's window shows an editor generated from the parameters.</summary>
    public bool HasEditor => parameterPanel != null;

    /// <summary>The generated editor's size in logical units, or 0 without one: its height is the one it was given last
    /// (<see cref="SetEditorHeight"/>), its width the one it was made for.</summary>
    public int EditorWidth { get; }
    public int EditorHeight { get; private set; }

    /// <summary>The generated editor's height, in logical units (at least 1): the window was resized. Its width is the
    /// window's.</summary>
    public void SetEditorHeight(int height)
    {
        if ((parameterPanel == null) || (height == EditorHeight))
            return;

        EditorHeight = Math.Max(1, height);

        SetPanelHeight();

        layout.UpdateLayout();
    }

    // The generated editor EditorHeight high: its desired height doesn't include its padding, which UILayout adds
    void SetPanelHeight()
    {
        float padding = parameterPanel!.Padding.Top + parameterPanel.Padding.Bottom;

        parameterPanel.DesiredHeight = Math.Max(1, EditorHeight - padding);
    }

    /// <summary>The size of the editor generated for the parameters, in logical units.</summary>
    public static void EditorSize(IReadOnlyList<AudioPluginParameter> parameters, out uint width, out uint height) =>
        ParameterPanel.GetSize(parameters.Count, 0, out width, out height);

    /// <summary>Opens or closes the panel, at the start of the next tick (not while the layout handles input).</summary>
    public void SetOpen(bool value)
    {
        if (panel != null) pendingOpen = value;
    }

    /// <summary>Moves and resizes the strip in its parent (pixels).</summary>
    public void SetBounds(int y, int width, int height)
    {
        window.SetPosition(0, y);
        window.SetClientSize(Math.Max(width, 1), Math.Max(height, 1));
    }

    /// <summary>The window moved to a monitor with another display scale.</summary>
    public void SetScale(double scale)
    {
        layout.DisplayScale = (float)scale;
        host.Resize(window.ClientWidth, window.ClientHeight); // lays out again at the new scale
    }

    /// <summary>What to show, most important first.</summary>
    public void Show(IReadOnlyList<StatusItem> newItems)
    {
        if (newItems.SequenceEqual(items)) return;

        items = newItems;

        // All of them: the row decides, as it's laid out, which fit (and so follows resizes on its own).
        row.Children.Clear();

        foreach (StatusItem item in items)
            row.Children.Add(new FittedTextBlock { Text = item.Text, TextColor = item.Warning ? WarningColor : TextColor });

        layout.UpdateLayout();
    }

    /// <summary>Draws the strip. Call regularly.</summary>
    public void Tick(double secondsElapsed)
    {
        if (disposed) return;

        if (pendingOpen is bool next)
        {
            pendingOpen = null;

            if (next != open)
            {
                open = next;

                if (open) panel!.Refresh();
                else panel!.StopTest();

                panel.Visible = divider!.Visible = open;
                audioButton!.SetPressed(open);
                if (!open) host.ReleaseAllInput();

                layout.UpdateLayout();

                OpenChanged?.Invoke();
            }
        }

        // Open or not: device work started while it was open finishes.
        panel?.Tick(secondsElapsed);

        // The generated editor follows the parameters' values (restored state, the plugin changing them)
        parameterPanel?.Update();

        host.Tick((float)secondsElapsed);
    }

    // The mouse is the layout's (the Audio button, the panel).
    bool OnInput(in WindowInputEvent e)
    {
        switch (e.Type)
        {
            case WindowInputEventType.MouseMove:
                host.MouseMove(e.X, e.Y);

                return true;

            case WindowInputEventType.MouseDown:
            case WindowInputEventType.MouseUp:
                host.MouseButton((int)e.Button, e.Type == WindowInputEventType.MouseDown, e.X, e.Y);

                // On the strip itself (not the generated editor or the panel): the host gives the keys back to the
                // editor.
                if (e.Type == WindowInputEventType.MouseDown && e.Y >= EditorHeight * layout.TotalScale &&
                    e.Y < (EditorHeight + Height) * layout.TotalScale) Clicked?.Invoke();

                return true;

            case WindowInputEventType.MouseWheel:
                host.MouseMove(e.X, e.Y);
                host.MouseWheel(e.WheelDelta);

                return true;

            case WindowInputEventType.MouseLeave:
                host.MouseLeave();

                return false;
        }

        return false;
    }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        // The plugin back on the device, and nothing left to run after the engine stops.
        panel?.StopTest();
        panel?.FinishWork();

        host.Dispose();
        window.Dispose();
    }

    /// <summary>
    /// The audio engine's state as status items, most important first: a warning when the configured device isn't the
    /// one running, the device, its rate, buffer size and latency, the plugin's load (percent of each block's time; NaN
    /// if not known yet) and dropouts.
    /// </summary>
    public static List<StatusItem> Describe(AudioState state, AudioConfig config, double loadPercent, long dropouts)
    {
        var result = new List<StatusItem>();
        bool onClock = state.Driver == ClockDriver.Name;

        if (state.Fallback)
            result.Add(new StatusItem($"{config.Driver} device unavailable: on the internal clock", Warning: true));
        else if (onClock && state.Error != null)
            result.Add(new StatusItem("Audio device stopped: on the internal clock", Warning: true));

        if (onClock)
        {
            if (!state.Fallback && state.Error == null) result.Add(new StatusItem("No audio device"));
        }
        else
        {
            result.Add(new StatusItem(state.Device != null ? $"{state.Driver}: {state.Device}" : state.Driver));
        }

        double rate = state.Format?.SampleRate ?? 0;

        if (rate > 0)
        {
            result.Add(new StatusItem($"{rate / 1000:0.#} kHz"));

            if (state.BufferSize > 0)
                result.Add(new StatusItem($"{state.BufferSize / rate * 1000:0.0} ms buffer"));

            int latency = state.InputLatency + state.OutputLatency;
            if (!onClock && latency > 0)
                result.Add(new StatusItem($"latency {latency / rate * 1000:0.0} ms"));
        }

        if (state.Running && !double.IsNaN(loadPercent))
            result.Add(new StatusItem($"load {loadPercent:0}%", Warning: loadPercent >= 80));
        if (dropouts > 0)
            result.Add(new StatusItem(dropouts == 1 ? "1 dropout" : $"{dropouts} dropouts", Warning: true));

        return result;
    }
}

/// <summary>
/// The strip's items in priority order: those that fit at their full width, then the first that doesn't, cut short with
/// "..." in the room left (a <see cref="FittedTextBlock"/> does the cutting); the rest are hidden. Decided as the row is
/// laid out, so it follows resizes and new items without being asked.
/// </summary>
class StatusRow : HorizontalStack
{
    public override void UpdateContentLayout()
    {
        float used = 0;
        bool full = false;

        foreach (UIElement child in children)
        {
            child.GetSize(out float width, out _);

            float needed = (used > 0 ? ChildSpacing : 0) + width;
            bool overflows = !full && (used + needed > ContentBounds.Width);

            child.Visible = !full;

            // Stretched, the cut item takes the room left: the stack gives it its width less the overflow.
            child.HorizontalAlignment = overflows ? EHorizontalAlignment.Stretch : EHorizontalAlignment.Left;

            full |= overflows;
            used += needed;
        }

        base.UpdateContentLayout();
    }
}
