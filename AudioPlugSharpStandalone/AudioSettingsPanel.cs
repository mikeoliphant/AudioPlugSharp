using AudioIOSharp;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpStandalone;

/// <summary>
/// The audio settings, shown above the status strip when it's opened (the strip stays below as the panel's status
/// bar), laid out as SpiceForge's audio dialog, in outlined sections: the driver, the device and its control panel, and
/// the input and output channels; rate and buffer size; a test (a tone or the inputs looped back) with a level meter
/// per chosen channel; then Cancel/OK. OK and Test apply the settings (and save them). Device queries and applying
/// wait for the engine on the UI thread (the engine's work doesn't need this thread).
/// </summary>
public class AudioSettingsPanel : VerticalStack
{
    /// <summary>The narrowest the panel lays out well, in logical units: the window widens to it while it's open.</summary>
    public const int MinWidth = 520;

    const int RowHeight = 34, HeaderHeight = 28, ChannelsHeight = 4 * 29, LineHeight = 28, ButtonsHeight = 36;
    const int Spacing = 8, LabelWidth = 90, LabelIndent = 6, MeterWidth = 40, MeterHeight = 10;

    readonly AudioEngine engine;
    readonly IAudioClient mainClient;
    readonly Action<AudioConfig> applied;
    readonly Action close;

    readonly DropDown<string> driver = new(), device = new();
    readonly DropDown<int?> rate = new(), buffer = new();
    readonly DropDown<AudioTestSignal> signal = new();
    readonly FrameButton controlPanel, test;
    readonly CheckList<string> inputs = new(StringComparer.OrdinalIgnoreCase), outputs = new(StringComparer.OrdinalIgnoreCase);
    readonly MeterRow inputMeters = new(), outputMeters = new();
    readonly TextBlock error;

    AudioDeviceInfo? info;
    AudioTestClient? testClient;
    readonly List<float> peaks = new();

    /// <param name="mainClient">What runs on the device when the test doesn't: the plugin.</param>
    /// <param name="applied">A configuration is now in use (to save).</param>
    /// <param name="close">Closes the panel (Cancel, and OK once applied).</param>
    public AudioSettingsPanel(AudioEngine engine, IAudioClient mainClient, Action<AudioConfig> applied, Action close)
    {
        this.engine = engine;
        this.mainClient = mainClient;
        this.applied = applied;
        this.close = close;

        HorizontalAlignment = EHorizontalAlignment.Stretch;
        BackgroundColor = Layout.Current.Theme.Background; // darker than the status strip above it
        Padding = new LayoutPadding(Spacing, Spacing, Spacing, Spacing);
        ChildSpacing = Spacing;

        controlPanel = Button("Control panel", ShowControlPanel);
        // A toggle: pressed while the test runs.
        test = Button("Test", ToggleTest);
        test.IsToggleButton = true;

        driver.SelectAction = DriverChanged;
        device.SelectAction = DeviceChanged;
        rate.SelectAction = () => ShowBufferSizes(buffer.Value);

        inputs.CheckAction = () => inputMeters.SetChannels(inputs.Checked);
        outputs.CheckAction = () => outputMeters.SetChannels(outputs.Checked);

        signal.SetItems([("Tone, 440 Hz", AudioTestSignal.Tone), ("Inputs, looped back", AudioTestSignal.Inputs)]);
        signal.HorizontalAlignment = EHorizontalAlignment.Left;

        // The device and its channels.
        var headers = new HorizontalStack { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = HeaderHeight, ChildrenEqualSize = true };

        foreach (string header in new[] { "Inputs", "Outputs" })
        {
            TextBlock text = Text(header, Layout.Current.Theme.MutedText);
            text.Margin = new LayoutPadding(LabelIndent, 0, 0, 0);
            headers.Children.Add(text);
        }

        foreach (CheckList<string> list in new[] { inputs, outputs })
        {
            list.HorizontalAlignment = EHorizontalAlignment.Stretch;
            list.VerticalAlignment = EVerticalAlignment.Stretch;
            list.TextColor = Layout.Current.Theme.Text;
        }

        var channels = new HorizontalStack { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = ChannelsHeight, ChildrenEqualSize = true };
        channels.Children.Add(new InputFrame(inputs) { Margin = new LayoutPadding(0, 0, Spacing, 0) });
        channels.Children.Add(new InputFrame(outputs) { Margin = new LayoutPadding(0, 0, Spacing, 0) });

        Children.Add(Section(Row("Driver", driver), Row("Device", device, controlPanel), headers, channels));

        // The format.
        Children.Add(Section(Row("Rate", rate), Row("Buffer", buffer)));

        // The test, and the chosen channels' levels.
        var testRow = new HorizontalStack { DesiredHeight = RowHeight, ChildSpacing = Spacing };
        testRow.Children.Add(test);
        testRow.Children.Add(signal);

        Children.Add(Section(testRow, Row("In", inputMeters), Row("Out", outputMeters)));

        error = Text("", Layout.Current.Theme.Warning);
        error.DesiredHeight = LineHeight;
        Children.Add(error);

        var buttons = new HorizontalStack { HorizontalAlignment = EHorizontalAlignment.Right, DesiredHeight = ButtonsHeight, ChildSpacing = Spacing };
        buttons.Children.Add(Button("Cancel", Cancel));
        buttons.Children.Add(new FrameButton("OK", Ok) { Primary = true });
        Children.Add(buttons);
    }

    // An outlined group of rows.
    static Section Section(params UIElement[] rows)
    {
        var stack = new VerticalStack { HorizontalAlignment = EHorizontalAlignment.Stretch, ChildSpacing = 4 };

        foreach (UIElement row in rows)
            stack.Children.Add(row);

        return new Section(stack);
    }

    // A label, then the content taking the rest of the row (but what's beside it).
    static HorizontalStack Row(string label, UIElement content, UIElement? side = null)
    {
        var row = new HorizontalStack { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = RowHeight, ChildSpacing = Spacing };

        TextBlock text = Text(label, Layout.Current.Theme.Text);
        text.DesiredWidth = LabelWidth - Spacing - LabelIndent;
        text.Margin = new LayoutPadding(LabelIndent, 0, 0, 0);
        row.Children.Add(text);

        if (content.HorizontalAlignment != EHorizontalAlignment.Left) content.HorizontalAlignment = EHorizontalAlignment.Stretch;
        row.Children.Add(content);

        if (side != null) row.Children.Add(side);

        return row;
    }

    static TextBlock Text(string text, UIColor color) =>
        new(text) { TextColor = color, VerticalAlignment = EVerticalAlignment.Center };

    static FrameButton Button(string text, Action click) => new(text, click);

    // ---- showing the settings ---------------------------------------------------------------------------------------

    /// <summary>Shows the configuration in use. Called when the panel opens.</summary>
    public void Refresh()
    {
        error.Text = "";

        AudioConfig config = engine.Config;

        driver.SetItems(engine.Drivers.Select(d => (d.DriverName, d.DriverName)));
        driver.SelectValue(d => string.Equals(d, config.Driver, StringComparison.OrdinalIgnoreCase));

        ShowConfig(config);
    }

    // Another driver starts from what was last used with it, as at startup; a remembered device that's gone gives way to
    // the first.
    void DriverChanged()
    {
        error.Text = "";

        ShowConfig(engine.ConfigFor(driver.Value!));
    }

    void DeviceChanged()
    {
        error.Text = "";

        LoadDeviceInfo(null, null, null, null);
    }

    void ShowConfig(AudioConfig config)
    {
        IReadOnlyList<string> names = [];

        try
        {
            names = engine.Driver(driver.Value!).Devices().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error.Text = e.Message;
        }

        device.SetItems(names.Select(n => (n, n)));
        device.SelectValue(n => string.Equals(n, config.Device, StringComparison.OrdinalIgnoreCase));

        // Channels are kept only for the device they were chosen on.
        bool sameDevice = config.Device != null && string.Equals(device.Value, config.Device, StringComparison.OrdinalIgnoreCase);

        LoadDeviceInfo(sameDevice ? config.Inputs : null, sameDevice ? config.Outputs : null, config.SampleRate, config.BufferSize);
    }

    // The device's channels, rates and buffer sizes, with the given ones chosen (null: the device's defaults).
    void LoadDeviceInfo(IReadOnlyList<string>? chosenInputs, IReadOnlyList<string>? chosenOutputs, int? wantedRate, int? wantedBuffer)
    {
        info = null;

        if (device.Value != null)
        {
            try
            {
                info = engine.Driver(driver.Value!).Info(device.Value).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                error.Text = e.Message; // ASIO: another driver is running; OK still applies
            }
        }

        // A device's defaults: its first input, its first two outputs (as the engine takes a null list).
        inputs.SetItems(Channels(info?.Inputs), chosenInputs ?? info?.Inputs.Take(1) ?? []);
        outputs.SetItems(Channels(info?.Outputs), chosenOutputs ?? info?.Outputs.Take(2) ?? []);

        inputMeters.SetChannels(inputs.Checked);
        outputMeters.SetChannels(outputs.Checked);

        controlPanel.Visible = info?.ControlPanel ?? false;

        if (info == null)
            rate.SetItems([("The device's", (int?)null)]);
        else
            rate.SetItems([($"The device's ({FormatRate(info.DefaultSampleRate)})", null), .. info.SampleRates.Select(r => (FormatRate(r), (int?)r))]);

        rate.SelectValue(r => r == wantedRate);

        ShowBufferSizes(wantedBuffer);
    }

    // A device's channels as check list items (the name is the value).
    static IEnumerable<(string, string)> Channels(IReadOnlyList<string>? names) => (names ?? []).Select(n => (n, n));

    // The buffer sizes, in samples and milliseconds at the chosen rate.
    void ShowBufferSizes(int? wanted)
    {
        double sampleRate = rate.Value ?? info?.DefaultSampleRate ?? 48000;
        string Ms(int samples) => $"{samples / sampleRate * 1000:0.0} ms";

        if (info == null)
            buffer.SetItems([("The device's", (int?)null)]);
        else
            buffer.SetItems([($"The device's ({info.DefaultBufferSize}, {Ms(info.DefaultBufferSize)})", null), .. info.BufferSizes.Select(b => ($"{b} samples ({Ms(b)})", (int?)b))]);

        buffer.SelectValue(b => b == wanted);

        Layout.Current.UpdateLayout();
    }

    static string FormatRate(double hz) => hz % 1000 == 0 ? $"{hz / 1000} kHz" : $"{hz / 1000:0.0} kHz";

    // ---- applying ---------------------------------------------------------------------------------------------------

    AudioConfig Selected() => new()
    {
        Driver = driver.Value ?? ClockDriver.Name,
        Device = device.Value,
        // A device without channels (the clock) keeps the defaults rather than saving empty lists.
        Inputs = info != null && info.Inputs.Count > 0 ? inputs.Checked : null,
        Outputs = info != null && info.Outputs.Count > 0 ? outputs.Checked : null,
        SampleRate = rate.Value,
        BufferSize = buffer.Value,
    };

    // Moves the engine to the settings shown. Returns whether it took.
    bool Apply()
    {
        error.Text = "";

        try
        {
            AudioConfig config = engine.SetConfig(Selected()).GetAwaiter().GetResult();

            applied(config);

            return true;
        }
        catch (Exception e)
        {
            Logger.Log("Audio settings not applied: " + e.Message);

            error.Text = "Not applied: " + e.Message;

            Layout.Current.UpdateLayout();

            return false;
        }
    }

    void Ok()
    {
        StopTest();

        if (Apply()) close();
    }

    void Cancel()
    {
        StopTest();

        close();
    }

    void ShowControlPanel()
    {
        try
        {
            engine.ShowControlPanel(driver.Value, device.Value).GetAwaiter().GetResult();

            // The driver may have changed its rate or buffer sizes.
            LoadDeviceInfo(inputs.Checked, outputs.Checked, rate.Value, buffer.Value);
        }
        catch (Exception e)
        {
            error.Text = e.Message;

            Layout.Current.UpdateLayout();
        }
    }

    // ---- the test ---------------------------------------------------------------------------------------------------

    void ToggleTest()
    {
        if (testClient != null)
        {
            StopTest();

            return;
        }

        // With the settings above, applied first.
        if (!Apply())
        {
            test.SetPressed(false);

            return;
        }

        var client = new AudioTestClient(signal.Value);

        try
        {
            engine.Attach(client).GetAwaiter().GetResult();

            testClient = client;
            test.Text = "Stop test";
            test.SetPressed(true);
        }
        catch (Exception e)
        {
            error.Text = e.Message;

            test.SetPressed(false);
        }

        Layout.Current.UpdateLayout();
    }

    /// <summary>Stops the test, if it runs, and puts the plugin back on the device. Called when the panel closes.</summary>
    public void StopTest()
    {
        if (testClient == null) return;

        testClient.FadeOut(TimeSpan.FromMilliseconds(250));
        testClient = null;

        try
        {
            engine.Attach(mainClient).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error.Text = e.Message;
        }

        test.Text = "Test";
        test.SetPressed(false);

        inputMeters.Clear();
        outputMeters.Clear();

        Layout.Current.UpdateLayout();
    }

    /// <summary>[UI thread] Updates the meters. Call every frame while the panel is open.</summary>
    public void Tick(double secondsElapsed)
    {
        if (testClient == null) return;

        float decay = (float)Math.Exp(-secondsElapsed / 0.25);

        testClient.TakeInputPeaks(peaks);
        inputMeters.Update(testClient.InputNames, peaks, decay);

        testClient.TakeOutputPeaks(peaks);
        outputMeters.Update(testClient.OutputNames, peaks, decay);
    }

    /// <summary>A level meter per chosen channel. Shown always; levels while the test runs, on the channels it opened.</summary>
    class MeterRow : HorizontalStack
    {
        List<string> names = [];

        public MeterRow()
        {
            ChildSpacing = 3;
            VerticalAlignment = EVerticalAlignment.Center;
            HorizontalAlignment = EHorizontalAlignment.Left;
        }

        public void SetChannels(List<string> channels)
        {
            names = channels;

            Children.Clear();

            foreach (string name in names)
                Children.Add(new LevelMeter { DesiredWidth = MeterWidth, DesiredHeight = MeterHeight });

            Layout.Current.UpdateLayout();
        }

        public void Clear()
        {
            foreach (LevelMeter meter in Children.Cast<LevelMeter>())
                meter.Level = 0;
        }

        // Each meter shows its channel's peak since the last frame, or its last level decayed, whichever is higher.
        // Channels the device didn't open (chosen since the test started) stay at 0.
        public void Update(IReadOnlyList<string> open, List<float> peaks, float decay)
        {
            for (int i = 0; i < names.Count; i++)
            {
                var meter = (LevelMeter)Children[i];

                int index = IndexOf(open, names[i]);
                float peak = index >= 0 && index < peaks.Count ? peaks[index] : 0;

                meter.Level = Math.Max(meter.Level * decay, peak);
            }
        }

        static int IndexOf(IReadOnlyList<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return i;

            return -1;
        }
    }
}
