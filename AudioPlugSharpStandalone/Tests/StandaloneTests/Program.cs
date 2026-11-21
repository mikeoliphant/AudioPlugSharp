using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StandaloneTests;

/// <summary>
/// StandaloneTests &lt;AudioPlugSharpStandalone executable&gt; &lt;UILayoutTestPlugin.dll&gt; [--captures &lt;dir&gt;]
///
/// Runs the plugin in the standalone host, in both editor modes (AUDIOPLUGSHARP_EDITOR_RENDER_THREAD=0 and 1), and
/// checks, by driving the window from outside: the window opens at the editor's size, with the status strip below the
/// editor; a drag on the gain slider
/// reaches the plugin; the editor follows a resize and (Windows) a display scale change; F11 makes it fullscreen and
/// back; closing exits cleanly and saves the state; it reopens at the saved size. Captures of the editor go to
/// --captures. Exits with 0 when everything passed.
///
/// The windows are real: moving the mouse or switching windows during a run can make a check fail.
/// </summary>
static class Program
{
    const string PluginName = "UILayout Test"; // the window title
    const int EditorWidth = 320, EditorHeight = 160;
    const int StripHeight = 36; // StatusStrip.Height
    const int PanelMinWidth = 520; // AudioSettingsPanel.MinWidth: the window widens to it while the panel is open
    const uint KeyF11 = 0x7A;

    static int failures;

    static int Main(string[] args)
    {
        if (args.Length != 2 && !(args.Length == 4 && args[2] == "--captures"))
        {
            Console.Error.WriteLine("Usage: StandaloneTests <AudioPlugSharpStandalone executable> <UILayoutTestPlugin.dll> [--captures <dir>]");
            return 2;
        }
        string host = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]);
        string? captures = args.Length == 4 ? Path.GetFullPath(args[3]) : null;
        if (captures != null) Directory.CreateDirectory(captures);

        WindowDriver driver = OperatingSystem.IsWindows() ? new WindowsDriver()
                            : OperatingSystem.IsLinux() ? new X11Driver()
                            : throw new PlatformNotSupportedException();

        foreach (string mode in new[] { "0", "1" })
        {
            Console.WriteLine($"== Standalone host, render thread {mode}");
            Run(driver, host, plugin, mode, captures);
        }

        Console.WriteLine(failures == 0 ? "PASSED" : $"FAILED ({failures} failures)");
        return failures == 0 ? 0 : 1;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"    [{(ok ? " ok " : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    static void Run(WindowDriver driver, string host, string plugin, string mode, string? captures)
    {
        // A folder of the test's own (--save-folder), not the plugin's real one in the user's application data.
        string saveFolder = Path.Combine(Path.GetTempPath(), $"AudioPlugSharpStandaloneTests-{Environment.ProcessId}-{mode}");
        string saveFile = Path.Combine(saveFolder, "SaveData");
        if (Directory.Exists(saveFolder)) Directory.Delete(saveFolder, recursive: true);

        void Capture(nint window, string name)
        {
            if (captures != null) driver.Capture(window, Path.Combine(captures, $"standalone-{name}-{mode}.bmp"));
        }

        Process process = Start(host, plugin, mode, saveFolder);
        try
        {
            if (!driver.Find(PluginName, out nint top, out nint editor))
            {
                Check(false, "the window opens, with the editor inside");
                return;
            }
            Check(true, "the window opens, with the editor inside");
            Thread.Sleep(1500); // the first frames

            double scale = driver.Scale(top);
            int Px(int units) => (int)Math.Round(units * scale);
            string Expect(int width, int height) => $"{Px(width)}x{Px(height)}";
            string ExpectWindow(int width, int height) => $"{Px(width)}x{Px(height) + Px(StripHeight)}";

            // The strip: the full width, below the editor.
            void CheckStrip(int width, int height, string when)
            {
                nint strip = driver.Children(top).FirstOrDefault(c => c != editor);
                string at = strip != 0 ? $"{driver.Size(strip)} at y {driver.Position(strip).Y}" : "none";
                Check(strip != 0 && driver.Size(strip) == Expect(width, StripHeight) && driver.Position(strip).Y == Px(height),
                    $"{when}: the status strip is below the editor ({at}, expected {Expect(width, StripHeight)} at y {Px(height)})");
                if (strip != 0) Capture(strip, $"strip-{when}");
            }

            Check(driver.Size(top) == ExpectWindow(EditorWidth, EditorHeight) && driver.Size(editor) == Expect(EditorWidth, EditorHeight),
                $"opens at the editor's size, with the strip (window {driver.Size(top)}, editor {driver.Size(editor)}, expected {ExpectWindow(EditorWidth, EditorHeight)} and {Expect(EditorWidth, EditorHeight)})");
            CheckStrip(EditorWidth, EditorHeight, "open");
            Capture(editor, "open");

            // The strip's Audio button (at its right end) opens the settings panel below the strip, in the strip's
            // window: the window grows downward (and widens to the panel's minimum) and the editor keeps its size. A
            // click elsewhere on the strip does nothing; the button again closes the panel.
            nint stripWindow = driver.Children(top).FirstOrDefault(c => c != editor);

            if (stripWindow != 0)
            {
                void Click(int x, int y)
                {
                    driver.MouseButton(stripWindow, x, y, down: true);
                    driver.MouseButton(stripWindow, x, y, down: false);
                    Thread.Sleep(600);
                }

                int StripWidth() => int.Parse(driver.Size(stripWindow).Split('x')[0]);

                Click(Px(20), Px(15));

                Check(driver.Size(top) == ExpectWindow(EditorWidth, EditorHeight), $"a click on the strip's text doesn't open the panel (window {driver.Size(top)})");

                Click(StripWidth() - Px(45), Px(15));

                int openHeight = int.Parse(driver.Size(stripWindow).Split('x')[1]);
                string openWindow = $"{Px(PanelMinWidth)}x{Px(EditorHeight) + openHeight}";

                Check(driver.Size(top) == openWindow && driver.Size(editor) == Expect(EditorWidth, EditorHeight) && openHeight > Px(StripHeight * 4),
                    $"the Audio button opens the settings panel (window {driver.Size(top)}, editor {driver.Size(editor)}, strip {driver.Size(stripWindow)}, expected {openWindow})");

                Capture(stripWindow, "settings");

                // The driver drop-down's list, with the mouse over its second row, and the buffer drop-down's list
                // (captured only), each closed again by a click outside it.
                driver.MouseButton(stripWindow, Px(300), Px(68), down: true);
                driver.MouseButton(stripWindow, Px(300), Px(68), down: false);
                Thread.Sleep(400);

                driver.MouseMove(stripWindow, Px(300), Px(130), buttonHeld: false);
                Thread.Sleep(300);

                Capture(stripWindow, "settings-dropdown");

                Click(Px(20), Px(15));

                driver.MouseButton(stripWindow, Px(300), Px(349), down: true);
                driver.MouseButton(stripWindow, Px(300), Px(349), down: false);
                Thread.Sleep(400);

                Capture(stripWindow, "settings-buffer");

                Click(Px(20), Px(15));

                // Widening the window while the panel is open widens the editor by as much; narrowing it back restores it.
                driver.SetClientSize(top, Px(PanelMinWidth) + Px(100), Px(EditorHeight) + openHeight);
                Thread.Sleep(600);

                Check(driver.Size(editor) == Expect(EditorWidth + 100, EditorHeight),
                    $"with the panel open, widening the window widens the editor (editor {driver.Size(editor)}, expected {Expect(EditorWidth + 100, EditorHeight)})");

                driver.SetClientSize(top, Px(PanelMinWidth), Px(EditorHeight) + openHeight);
                Thread.Sleep(600);

                Check(driver.Size(editor) == Expect(EditorWidth, EditorHeight),
                    $"and narrowing it back narrows the editor (editor {driver.Size(editor)}, expected {Expect(EditorWidth, EditorHeight)})");

                Click(StripWidth() - Px(45), Px(15));

                Check(driver.Size(top) == ExpectWindow(EditorWidth, EditorHeight) && driver.Size(editor) == Expect(EditorWidth, EditorHeight),
                    $"and another closes it (window {driver.Size(top)}, editor {driver.Size(editor)})");

                CheckStrip(EditorWidth, EditorHeight, "settings closed");
            }

            // A drag on the gain slider, from its handle (0 dB) to the left: one edit gesture, saved below.
            int y = Px(99);
            driver.MouseMove(editor, Px(196), y, buttonHeld: false);
            driver.MouseButton(editor, Px(196), y, down: true);
            foreach (int x in new[] { 180, 150, 120, 90, 70 }) driver.MouseMove(editor, Px(x), y, buttonHeld: true);
            driver.MouseButton(editor, Px(70), y, down: false);
            Thread.Sleep(300);
            Capture(editor, "dragged");

            driver.SetClientSize(top, Px(500), Px(300) + Px(StripHeight));
            Thread.Sleep(600);
            Check(driver.Size(editor) == Expect(500, 300), $"follows a resize (editor {driver.Size(editor)}, expected {Expect(500, 300)})");
            CheckStrip(500, 300, "resized");
            Capture(editor, "resized");

            if (driver.CanChangeScale)
            {
                driver.ChangeScale(top, 1.5);
                Thread.Sleep(600);
                Check(driver.Size(editor) == "750x450", $"keeps its size at another display scale (1.5: editor {driver.Size(editor)}, expected 750x450)");
                Capture(editor, "scaled");
                driver.ChangeScale(top, scale);
                Thread.Sleep(600);
                Check(driver.Size(editor) == Expect(500, 300), $"and back (editor {driver.Size(editor)}, expected {Expect(500, 300)})");
            }

            driver.Key(editor, KeyF11);
            Thread.Sleep(800);
            string screen = driver.ScreenSize(editor);
            Check(driver.IsTopLevel(editor) && driver.Size(editor) == screen, $"F11: fullscreen (editor {driver.Size(editor)}, screen {screen})");
            Capture(editor, "fullscreen");
            driver.Key(editor, KeyF11);
            Thread.Sleep(800);
            Check(driver.IsChildOf(editor, top) && driver.Size(editor) == Expect(500, 300),
                $"F11: back in the window (editor {driver.Size(editor)}, expected {Expect(500, 300)})");

            driver.Close(top);
            Check(process.WaitForExit(10000) && process.ExitCode == 0, "closes, and the program exits");

            string saved = File.Exists(saveFile) ? File.ReadAllText(saveFile) : "";
            Check(saved.Contains("<EditorWidth>500</EditorWidth>") && saved.Contains("<EditorHeight>300</EditorHeight>"),
                "the editor size is saved (500 x 300)");
            string gain = Regex.Match(saved, "<Value>([^<]*)</Value>").Groups[1].Value;
            Check(double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out double dB) && dB < -10,
                $"the drag reached the plugin (gain saved: {(gain == "" ? "none" : gain + " dB")})");

            process = Start(host, plugin, mode, saveFolder);
            bool found = driver.Find(PluginName, out top, out editor);
            Thread.Sleep(1500);
            Check(found && driver.Size(editor) == Expect(500, 300), $"reopens at the saved size (editor {(found ? driver.Size(editor) : "none")})");
            if (found) driver.Close(top);
            Check(process.WaitForExit(10000) && process.ExitCode == 0, "closes again");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            if (Directory.Exists(saveFolder)) Directory.Delete(saveFolder, recursive: true);
        }
    }

    static Process Start(string host, string plugin, string mode, string saveFolder)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        start.ArgumentList.Add("--save-folder");
        start.ArgumentList.Add(saveFolder);
        start.ArgumentList.Add(plugin);
        start.Environment["AUDIOPLUGSHARP_EDITOR_RENDER_THREAD"] = mode;
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {host}");
    }
}
