using AudioIOSharp;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpStandalone;

/// <summary>
/// The audio settings, shown below the status strip when it's opened: driver, device, sample rate and buffer size,
/// each stepped through with buttons, and Apply. A first, minimal version: no channels, no test tone, no styling.
/// Device queries and Apply wait for the engine on the UI thread (the engine's work doesn't need this thread).
/// </summary>
public class AudioSettingsPanel : VerticalStack
{
    /// <summary>The panel's height in logical units.</summary>
    public const int PanelHeight = 6 * RowHeight + 2 * Spacing;

    const int RowHeight = 34, Spacing = 8, LabelWidth = 110, ButtonWidth = 34;
    static readonly UIColor TextColor = new(196, 200, 208), WarningColor = new(255, 180, 60);

    readonly AudioEngine engine;
    readonly Action<AudioConfig> applied;
    readonly TextBlock driverText, deviceText, rateText, bufferText, message;
    readonly TextButton controlPanelButton;

    // The choices for the selected driver and device, and what's selected. An empty rate or buffer list means the
    // device's default (its options aren't known).
    AudioConfig baseConfig = new();
    IReadOnlyList<string> drivers = [];
    IReadOnlyList<string> devices = [];
    IReadOnlyList<int> rates = [], buffers = [];
    int driverIndex, deviceIndex, rateIndex = -1, bufferIndex = -1;

    /// <param name="applied">A configuration is now in use (to save).</param>
    /// <param name="close">The panel's Close button.</param>
    public AudioSettingsPanel(AudioEngine engine, Action<AudioConfig> applied, Action close)
    {
        this.engine = engine;
        this.applied = applied;

        HorizontalAlignment = EHorizontalAlignment.Stretch;
        DesiredHeight = PanelHeight;
        Padding = new LayoutPadding(Spacing);

        Children.Add(Row("Driver", () => StepDriver(-1), () => StepDriver(1), out driverText));
        Children.Add(Row("Device", () => StepDevice(-1), () => StepDevice(1), out deviceText));
        Children.Add(Row("Rate", () => Step(ref rateIndex, rates.Count, -1), () => Step(ref rateIndex, rates.Count, 1), out rateText));
        Children.Add(Row("Buffer", () => Step(ref bufferIndex, buffers.Count, -1), () => Step(ref bufferIndex, buffers.Count, 1), out bufferText));

        var buttons = new HorizontalStack { DesiredHeight = RowHeight, ChildSpacing = Spacing };
        buttons.Children.Add(Button("Apply", Apply));

        controlPanelButton = Button("Control panel", ShowControlPanel);
        buttons.Children.Add(controlPanelButton);

        buttons.Children.Add(Button("Close", close));

        Children.Add(buttons);

        message = new TextBlock { TextColor = WarningColor, DesiredHeight = RowHeight, VerticalAlignment = EVerticalAlignment.Center };
        Children.Add(message);
    }

    static HorizontalStack Row(string label, Action previous, Action next, out TextBlock value)
    {
        var row = new HorizontalStack { DesiredHeight = RowHeight, ChildSpacing = 4 };

        row.Children.Add(new TextBlock(label) { TextColor = TextColor, DesiredWidth = LabelWidth, VerticalAlignment = EVerticalAlignment.Center });
        row.Children.Add(Button("<", previous, ButtonWidth));
        row.Children.Add(Button(">", next, ButtonWidth));

        value = new TextBlock { TextColor = TextColor, VerticalAlignment = EVerticalAlignment.Center, Margin = new LayoutPadding(4, 0) };
        row.Children.Add(value);

        return row;
    }

    static TextButton Button(string text, Action click, float width = 0)
    {
        var button = new TextButton(text) { ClickAction = click, DesiredHeight = RowHeight - 4, VerticalAlignment = EVerticalAlignment.Center };

        if (width > 0) button.DesiredWidth = width;

        return button;
    }

    /// <summary>Shows the configuration in use. Called when the panel opens.</summary>
    public void Refresh()
    {
        message.Text = "";
        drivers = engine.Drivers.Select(d => d.DriverName).ToList();

        SelectDriver(engine.Config);
    }

    void StepDriver(int step)
    {
        if (drivers.Count == 0) return;

        int index = (driverIndex + step + drivers.Count) % drivers.Count;
        message.Text = "";

        SelectDriver(engine.ConfigFor(drivers[index])); // the configuration last used with it
    }

    void StepDevice(int step)
    {
        if (devices.Count == 0) return;

        deviceIndex = (deviceIndex + step + devices.Count) % devices.Count;
        message.Text = "";

        LoadDeviceInfo(baseConfig with { Device = devices[deviceIndex] });
    }

    void Step(ref int index, int count, int step)
    {
        if (count == 0) return;

        index = (index + step + count) % count;

        Show();
    }

    void SelectDriver(AudioConfig config)
    {
        driverIndex = Math.Max(0, IndexOf(drivers, config.Driver));
        AudioDriver driver = engine.Driver(drivers[driverIndex]);

        try
        {
            devices = driver.Devices().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            devices = [];
            message.Text = e.Message;
        }

        deviceIndex = Math.Max(0, config.Device != null ? IndexOf(devices, config.Device) : 0);

        LoadDeviceInfo(config with { Driver = driver.DriverName, Device = devices.Count > 0 ? devices[deviceIndex] : config.Device });
    }

    // The device's rates and buffer sizes, with the configuration's selected (or the device's defaults).
    void LoadDeviceInfo(AudioConfig config)
    {
        baseConfig = config;
        rates = buffers = [];
        rateIndex = bufferIndex = -1;
        controlPanelButton.Visible = false;

        if (config.Device != null)
        {
            try
            {
                AudioDeviceInfo info = engine.Driver(config.Driver).Info(config.Device).GetAwaiter().GetResult();

                rates = info.SampleRates;
                buffers = info.BufferSizes;
                rateIndex = IndexOf(rates, config.SampleRate ?? info.DefaultSampleRate);
                bufferIndex = IndexOf(buffers, config.BufferSize ?? info.DefaultBufferSize);

                if (rateIndex < 0 && rates.Count > 0) rateIndex = 0;
                if (bufferIndex < 0 && buffers.Count > 0) bufferIndex = 0;

                controlPanelButton.Visible = info.ControlPanel;
            }
            catch (Exception e)
            {
                message.Text = e.Message; // ASIO: another driver is running; Apply still works
            }
        }

        Show();
    }

    void Show()
    {
        driverText.Text = drivers.Count > 0 ? drivers[driverIndex] : "";
        deviceText.Text = devices.Count > 0 ? devices[deviceIndex] : "(none)";
        rateText.Text = rateIndex >= 0 ? $"{rates[rateIndex]} Hz" : "(default)";
        bufferText.Text = bufferIndex >= 0 ? $"{buffers[bufferIndex]} frames" : "(default)";

        Layout.Current.UpdateLayout();
    }

    AudioConfig Selected()
    {
        AudioConfig c = baseConfig;

        // Channels are kept only for the device they were chosen on.
        bool sameDevice = string.Equals(c.Driver, engine.Config.Driver, StringComparison.OrdinalIgnoreCase) && c.Device == engine.Config.Device;

        return c with
        {
            Inputs = sameDevice ? c.Inputs : null,
            Outputs = sameDevice ? c.Outputs : null,
            SampleRate = rateIndex >= 0 ? rates[rateIndex] : null,
            BufferSize = bufferIndex >= 0 ? buffers[bufferIndex] : null,
        };
    }

    void Apply()
    {
        try
        {
            AudioConfig now = engine.SetConfig(Selected()).GetAwaiter().GetResult();

            applied(now);

            Refresh();
        }
        catch (Exception e)
        {
            Logger.Log("Audio settings not applied: " + e.Message);

            Refresh();

            message.Text = "Not applied: " + e.Message;

            Layout.Current.UpdateLayout();
        }
    }

    void ShowControlPanel()
    {
        try
        {
            engine.ShowControlPanel(baseConfig.Driver, baseConfig.Device).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            message.Text = e.Message;

            Layout.Current.UpdateLayout();
        }
    }

    static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }

    static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;

        return -1;
    }
}
