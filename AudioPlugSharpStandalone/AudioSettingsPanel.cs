using AudioIOSharp;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
using UILayout;

namespace AudioPlugSharpStandalone;

/// <summary>
/// The audio settings, shown above the status strip when it's opened (the strip stays below as the panel's status
/// bar), laid out as SpiceForge's audio dialog, in outlined sections: the driver, the device and its control panel, and
/// the input and output channels; the MIDI inputs; rate and buffer size; a test (a tone or the inputs looped back) with a level meter
/// per chosen channel; then Cancel/OK. OK and Test apply the settings (and save them).
///
/// Device work (looking for devices, reading one, applying, the test, the control panel) runs off the UI thread, one
/// step at a time, with a message saying what it waits for; each step's result is used on the UI thread, in
/// <see cref="Tick"/>. Meanwhile OK, Test and Control panel do nothing; Cancel still closes.
/// </summary>
public class AudioSettingsPanel : VerticalStack
{
    /// <summary>The narrowest the panel lays out well, in logical units: the window widens to it while it's open.</summary>
    public const int MinWidth = 520;

    const int RowHeight = 34, HeaderHeight = 28, ChannelsHeight = 4 * 29, MidiHeight = 3 * 29, LineHeight = 28, ButtonsHeight = 36;
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
    readonly CheckList<string> midiInputs = new(StringComparer.OrdinalIgnoreCase);
    readonly MeterRow inputMeters = new(), outputMeters = new();
    readonly TextBlock message;

    AudioDeviceInfo? info;
    AudioTestClient? testClient;
    readonly List<float> peaks = new();

    // The device work to do, in order, and the step running: what it waits for, and what to do with its task once done.
    readonly Queue<(Func<Task> Start, string Waiting, Action<Task> Then)> work = new();
    Task? running;
    string waiting = "";
    Action<Task>? then;

    // The last error, shown when nothing is running.
    string error = "";

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

        foreach (CheckList<string> list in new[] { inputs, outputs, midiInputs })
        {
            list.HorizontalAlignment = EHorizontalAlignment.Stretch;
            list.VerticalAlignment = EVerticalAlignment.Stretch;
            list.TextColor = Layout.Current.Theme.Text;
        }

        var channels = new HorizontalStack { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = ChannelsHeight, ChildrenEqualSize = true };
        channels.Children.Add(new InputFrame(inputs) { Margin = new LayoutPadding(0, 0, Spacing, 0) });
        channels.Children.Add(new InputFrame(outputs) { Margin = new LayoutPadding(0, 0, Spacing, 0) });

        Children.Add(Section(Row("Driver", driver), Row("Device", device, controlPanel), headers, channels));

        // The MIDI inputs: JACK's MIDI ports, or the system's for the other drivers.
        TextBlock midiHeader = Text("MIDI inputs", Layout.Current.Theme.MutedText);
        midiHeader.DesiredHeight = HeaderHeight;
        midiHeader.Margin = new LayoutPadding(LabelIndent, 0, 0, 0);

        midiInputs.EmptyText = "No MIDI inputs";

        var midiList = new InputFrame(midiInputs) { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = MidiHeight };

        Children.Add(Section(midiHeader, midiList));

        // The format.
        Children.Add(Section(Row("Rate", rate), Row("Buffer", buffer)));

        // The test, and the chosen channels' levels.
        var testRow = new HorizontalStack { DesiredHeight = RowHeight, ChildSpacing = Spacing };
        testRow.Children.Add(test);
        testRow.Children.Add(signal);

        Children.Add(Section(testRow, Row("In", inputMeters), Row("Out", outputMeters)));

        message = Text("", Layout.Current.Theme.Warning);
        message.DesiredHeight = LineHeight;
        Children.Add(message);

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

    // ---- device work, off the UI thread ----------------------------------------------------------------------------

    /// <summary>Whether device work is queued or running.</summary>
    public bool Busy => running != null || work.Count > 0;

    // Queues a step: start (on a pool thread: some drivers' calls block) shows the waiting message until it's done, then
    // next gets its task on the UI thread.
    void Run(Func<Task> start, string waitingFor, Action<Task> next)
    {
        work.Enqueue((() => Task.Run(start), waitingFor, next));

        StartNext();
    }

    void Run<T>(Func<Task<T>> start, string waitingFor, Action<Task<T>> next)
    {
        work.Enqueue((() => Task.Run(start), waitingFor, task => next((Task<T>)task)));

        StartNext();
    }

    void StartNext()
    {
        if (running == null && work.Count > 0)
        {
            (Func<Task> start, string waitingFor, Action<Task> next) = work.Dequeue();

            running = start();
            waiting = waitingFor;
            then = next;
        }

        ShowMessage();
    }

    // The step's error, if it failed (shown, after the prefix).
    bool Failed(Task task, string prefix = "")
    {
        if (!task.IsFaulted)
            return false;

        Exception e = task.Exception!.InnerException ?? task.Exception;

        error = prefix + e.Message;

        return true;
    }

    // What's running, or else the last error.
    void ShowMessage()
    {
        bool busy = running != null;
        string text = busy ? waiting + "..." : error;

        if (text == message.Text)
            return;

        message.Text = text;
        message.TextColor = busy ? Layout.Current.Theme.MutedText : Layout.Current.Theme.Warning;

        Layout.Current.UpdateLayout();
    }

    /// <summary>[UI thread] Carries on with device work that finished, and updates the meters. Call every frame, open
    /// or not (work started before the panel closed still finishes).</summary>
    public void Tick(double secondsElapsed)
    {
        if (running is { IsCompleted: true } done)
        {
            Action<Task> next = then!;

            running = null;
            then = null;

            next(done);

            StartNext();
        }

        if (testClient == null) return;

        float decay = (float)Math.Exp(-secondsElapsed / 0.25);

        testClient.TakeInputPeaks(peaks);
        inputMeters.Update(testClient.InputNames, peaks, decay);

        testClient.TakeOutputPeaks(peaks);
        outputMeters.Update(testClient.OutputNames, peaks, decay);
    }

    /// <summary>[UI thread] Runs the device work left to its end, waiting for it. Called when the window closes, before
    /// the engine stops.</summary>
    public void FinishWork()
    {
        while (running != null)
        {
            try
            {
                running.Wait();
            }
            catch (AggregateException)
            {
                // The step's own continuation sees it.
            }

            Tick(0);
        }
    }

    // ---- showing the settings ---------------------------------------------------------------------------------------

    /// <summary>Shows the configuration in use. Called when the panel opens.</summary>
    public void Refresh()
    {
        error = "";

        AudioConfig config = engine.Config;

        driver.SetItems(engine.Drivers.Select(d => (d.DriverName, d.DriverName)));
        driver.SelectValue(d => string.Equals(d, config.Driver, StringComparison.OrdinalIgnoreCase));

        ShowConfig(config);
    }

    // Another driver starts from what was last used with it, as at startup; a remembered device that's gone gives way to
    // the first.
    void DriverChanged()
    {
        error = "";

        AudioConfig config = engine.ConfigFor(driver.Value!);

        // MIDI inputs never chosen with this driver: the ones chosen now, where it has them too (the system's MIDI
        // inputs are the same for every driver but JACK).
        ShowConfig(config, config.MidiInputs == null ? new MidiChoice(midiInputs.Checked, ShowMissing: false) : null);
    }

    // The MIDI inputs to show checked, and whether the ones the device doesn't have are shown too (as not connected),
    // so a configuration's choice isn't dropped when a keyboard is unplugged.
    record MidiChoice(IReadOnlyList<string>? Chosen, bool ShowMissing);

    // The MIDI inputs checked now, kept as they are.
    MidiChoice CurrentMidi() => new(midiInputs.Checked, ShowMissing: true);

    void DeviceChanged()
    {
        error = "";

        LoadDeviceInfo(null, null, null, null, CurrentMidi());
    }

    // The driver's devices, then the chosen one's channels and formats. A result for a driver no longer chosen is
    // dropped (a newer one is on its way).
    void ShowConfig(AudioConfig config, MidiChoice? midi = null)
    {
        midi ??= new MidiChoice(config.MidiInputs, ShowMissing: true);

        string queried = driver.Value!;

        Run(() => engine.Driver(queried).Devices(), "Looking for devices", task =>
        {
            if (driver.Value != queried)
                return;

            IReadOnlyList<string> names = Failed(task) ? [] : task.Result;

            device.SetItems(names.Select(n => (n, n)));
            device.SelectValue(n => string.Equals(n, config.Device, StringComparison.OrdinalIgnoreCase));

            // Channels are kept only for the device they were chosen on.
            bool sameDevice = config.Device != null && string.Equals(device.Value, config.Device, StringComparison.OrdinalIgnoreCase);

            LoadDeviceInfo(sameDevice ? config.Inputs : null, sameDevice ? config.Outputs : null, config.SampleRate, config.BufferSize, midi);
        });
    }

    // The device's channels, rates and buffer sizes, with the given ones chosen (null: the device's defaults). A result
    // for a device no longer chosen is dropped.
    void LoadDeviceInfo(IReadOnlyList<string>? chosenInputs, IReadOnlyList<string>? chosenOutputs, int? wantedRate, int? wantedBuffer, MidiChoice midi)
    {
        string? queriedDriver = driver.Value, queriedDevice = device.Value;

        if (queriedDevice == null)
        {
            ShowDeviceInfo(null, chosenInputs, chosenOutputs, wantedRate, wantedBuffer, midi);

            return;
        }

        Run(() => engine.Driver(queriedDriver!).Info(queriedDevice), "Reading the device", task =>
        {
            if ((driver.Value != queriedDriver) || (device.Value != queriedDevice))
                return;

            // ASIO: another driver is running (OK still applies).
            AudioDeviceInfo? read = Failed(task) ? null : task.Result;

            ShowDeviceInfo(read, chosenInputs, chosenOutputs, wantedRate, wantedBuffer, midi);
        });
    }

    void ShowDeviceInfo(AudioDeviceInfo? read, IReadOnlyList<string>? chosenInputs, IReadOnlyList<string>? chosenOutputs, int? wantedRate, int? wantedBuffer,
        MidiChoice midi)
    {
        info = read;

        // A device's defaults: its first input, its first two outputs (as the engine takes a null list).
        inputs.SetItems(Channels(info?.Inputs), chosenInputs ?? info?.Inputs.Take(1) ?? []);
        outputs.SetItems(Channels(info?.Outputs), chosenOutputs ?? info?.Outputs.Take(2) ?? []);

        inputMeters.SetChannels(inputs.Checked);
        outputMeters.SetChannels(outputs.Checked);

        ShowMidiInputs(info?.MidiInputs ?? [], midi);

        controlPanel.Visible = info?.ControlPanel ?? false;

        if (info == null)
            rate.SetItems([("The device's", (int?)null)]);
        else
            rate.SetItems([($"The device's ({FormatRate(info.DefaultSampleRate)})", null), .. info.SampleRates.Select(r => (FormatRate(r), (int?)r))]);

        rate.SelectValue(r => r == wantedRate);

        ShowBufferSizes(wantedBuffer);
    }

    // The device's MIDI inputs, the chosen ones checked; chosen ones it doesn't have are shown as not connected, if asked
    // for, or else dropped.
    void ShowMidiInputs(IReadOnlyList<string> available, MidiChoice midi)
    {
        IReadOnlyList<string> chosen = midi.Chosen ?? [];

        var missing = chosen.Where(c => !available.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        IEnumerable<(string, string)> items = Channels(available);

        if (midi.ShowMissing)
            items = items.Concat(missing.Select(m => ($"{m} (not connected)", m)));

        midiInputs.SetItems(items, midi.ShowMissing ? chosen : chosen.Except(missing, StringComparer.OrdinalIgnoreCase));
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
        MidiInputs = midiInputs.Checked.Count > 0 ? midiInputs.Checked : null,
    };

    // Moves the engine to the settings shown, then (if it took) carries on with tookEffect, or else with failed.
    void Apply(Action tookEffect, Action? failed = null)
    {
        error = "";

        AudioConfig selected = Selected();

        Run(() => engine.SetConfig(selected), "Opening the device", task =>
        {
            if (Failed(task, "Not applied: "))
            {
                Logger.Log("Audio settings " + error);

                failed?.Invoke();

                return;
            }

            applied(task.Result);

            tookEffect();
        });
    }

    void Ok()
    {
        if (Busy)
            return;

        StopTest();

        Apply(close);
    }

    void Cancel()
    {
        StopTest();

        close();
    }

    void ShowControlPanel()
    {
        if (Busy)
            return;

        error = "";

        string? controlDriver = driver.Value, controlDevice = device.Value;

        Run(() => engine.ShowControlPanel(controlDriver, controlDevice), "Waiting for the control panel", task =>
        {
            if (Failed(task))
                return;

            // The driver may have changed its rate or buffer sizes.
            LoadDeviceInfo(inputs.Checked, outputs.Checked, rate.Value, buffer.Value, CurrentMidi());
        });
    }

    // ---- the test ---------------------------------------------------------------------------------------------------

    void ToggleTest()
    {
        if (testClient != null)
        {
            StopTest();

            return;
        }

        // The button stays up until the test runs.
        test.SetPressed(false);

        if (Busy)
            return;

        // With the settings above, applied first.
        Apply(StartTest);
    }

    void StartTest()
    {
        var client = new AudioTestClient(signal.Value);

        Run(() => engine.Attach(client), "Starting the test", task =>
        {
            if (Failed(task))
                return;

            testClient = client;
            test.Text = "Stop test";
            test.SetPressed(true);

            Layout.Current.UpdateLayout();

            // Closed while it started: the plugin goes back on the device.
            if (!Visible)
                StopTest();
        });
    }

    /// <summary>Stops the test, if it runs, and puts the plugin back on the device. Called when the panel closes.</summary>
    public void StopTest()
    {
        if (testClient == null) return;

        testClient.FadeOut(TimeSpan.FromMilliseconds(250));
        testClient = null;

        Run(() => engine.Attach(mainClient), "Stopping the test", task => Failed(task));

        test.Text = "Test";
        test.SetPressed(false);

        inputMeters.Clear();
        outputMeters.Clear();

        Layout.Current.UpdateLayout();
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
