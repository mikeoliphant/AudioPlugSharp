using AudioIOSharp;
using AudioPlugSharpWindowing;
using Microsoft.Xna.Framework;
using UILayout;

namespace AudioPlugSharpStandalone;

/// <summary>One thing the status strip shows. A warning is drawn in amber.</summary>
public readonly record struct StatusItem(string Text, bool Warning = false);

/// <summary>
/// The strip along the bottom of the standalone's window: the audio device and how it's running. A child window below
/// the editor's, with a UILayout layout of the host's own: the plugin's UILayout, if it has one, is another copy (the
/// plugin's load context), so neither changes the other's fonts, colors or settings. Items are shown in priority
/// order, as many as fit the width. Runs on the UI thread.
/// </summary>
public class StatusStrip : IDisposable
{
    /// <summary>The strip's height in logical units (the default font is 26 high).</summary>
    public const int Height = 30;

    static readonly UIColor Background = new(36, 38, 44), TextColor = new(196, 200, 208), WarningColor = new(255, 180, 60);
    const float Spacing = 18, Margin = 8;

    readonly NativeWindow window;
    readonly MonoGameHost host;
    readonly MonoGameLayout layout;
    readonly HorizontalStack row;
    readonly AudioSettingsPanel? panel;
    IReadOnlyList<StatusItem> items = [];
    float fittedWidth = -1;
    bool disposed, open;
    bool? pendingOpen;

    /// <summary>Creates the strip's window in <paramref name="parent"/>'s client area (pixels). With a panel (made
    /// once the strip's layout exists), clicking the strip opens it below the strip.</summary>
    public StatusStrip(NativeWindow parent, int y, int width, int height, double scale, Func<StatusStrip, AudioSettingsPanel>? createPanel = null)
    {
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

            row = new HorizontalStack
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
            root.Children.Add(stripRow);

            if (createPanel != null)
            {
                panel = createPanel(this);
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

    /// <summary>The height the strip's window needs, in logical units: the strip, and the panel when it's open.</summary>
    public int TotalHeight => Height + (open ? AudioSettingsPanel.PanelHeight : 0);

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

        fittedWidth = -1;
    }

    /// <summary>What to show, most important first.</summary>
    public void Show(IReadOnlyList<StatusItem> newItems)
    {
        if (newItems.SequenceEqual(items)) return;

        items = newItems;
        fittedWidth = -1;
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
                panel!.Visible = open;
                if (!open) host.ReleaseAllInput();

                layout.UpdateLayout();

                OpenChanged?.Invoke();
            }
        }

        float width = window.ClientWidth / layout.TotalScale - 2 * Margin;
        if (width != fittedWidth) Fit(width);

        host.Tick((float)secondsElapsed);
    }

    // As many items as fit, in order; the rest are left out.
    void Fit(float width)
    {
        fittedWidth = width;
        row.Children.Clear();

        float used = 0;

        foreach (StatusItem item in items)
        {
            layout.DefaultFont.MeasureString(item.Text, out float itemWidth, out _);
            float needed = (row.Children.Count > 0 ? Spacing : 0) + itemWidth;

            if (used + needed > width) break;

            used += needed;
            row.Children.Add(new TextBlock(item.Text) { TextColor = item.Warning ? WarningColor : TextColor });
        }

        layout.UpdateLayout();
    }

    // A click on the strip opens or closes the panel; while it's open, the mouse is the panel's.
    bool OnInput(in WindowInputEvent e)
    {
        bool onStrip = e.Y < Height * layout.TotalScale;

        if (e.Type == WindowInputEventType.MouseDown && e.Button == MouseButton.Left && (onStrip || !open))
        {
            SetOpen(!open);
            Clicked?.Invoke();

            return true;
        }

        if (!open) return e.Type is WindowInputEventType.MouseMove or WindowInputEventType.MouseUp;

        switch (e.Type)
        {
            case WindowInputEventType.MouseMove:
                host.MouseMove(e.X, e.Y);

                return true;

            case WindowInputEventType.MouseDown:
            case WindowInputEventType.MouseUp:
                host.MouseButton((int)e.Button, e.Type == WindowInputEventType.MouseDown, e.X, e.Y);

                return true;

            case WindowInputEventType.MouseWheel:
                host.MouseMove(e.X, e.Y);
                host.MouseWheel(e.WheelDelta);

                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

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
