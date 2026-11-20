using AudioIOSharp;
using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// AudioPlugSharpStandalone: runs any AudioPlugSharp plugin from its build output as an application.
/// <code>
/// AudioPlugSharpStandalone [options] &lt;plugin.dll&gt;
///   --save-folder &lt;folder&gt;   where the plugin's state and audio settings are kept (default: a folder named after
///                             the plugin in the user's application data)
///   --audio &lt;driver&gt;         None (the internal clock), ASIO or JACK
///   --audio-device &lt;name&gt;    the driver's device
///   --sample-rate &lt;hz&gt;       the device's sample rate
///   --buffer-size &lt;frames&gt;   the device's buffer size
/// AudioPlugSharpStandalone --list-audio
/// </code>
/// Audio choices are saved, and used from then on.
/// </summary>
static class Program
{
    const string Usage = """
        Usage: AudioPlugSharpStandalone [options] <plugin.dll>
          --save-folder <folder>   where the plugin's state and audio settings are kept
          --audio <driver>         None (the internal clock), ASIO or JACK
          --audio-device <name>    the driver's device
          --sample-rate <hz>       the device's sample rate
          --buffer-size <frames>   the device's buffer size
        AudioPlugSharpStandalone --list-audio   lists the audio drivers and their devices
        """;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (UsageException)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    static int Run(string[] args)
    {
        string? pluginPath = null, saveFolder = null, driver = null, device = null;
        int? sampleRate = null, bufferSize = null;
        bool listAudio = false;
        for (int i = 0; i < args.Length; i++)
        {
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--save-folder": saveFolder = Value() ?? throw Fail(); break;
                case "--audio": driver = Value() ?? throw Fail(); break;
                case "--audio-device": device = Value() ?? throw Fail(); break;
                case "--sample-rate": sampleRate = Number(Value()); break;
                case "--buffer-size": bufferSize = Number(Value()); break;
                case "--list-audio": listAudio = true; break;
                default:
                    if (pluginPath != null || args[i].StartsWith("--"))
                        throw Fail();
                    pluginPath = args[i];
                    break;
            }
        }

        if (listAudio)
            return ListAudio();
        if (pluginPath == null)
            throw Fail();

        IAudioPlugin plugin;
        try
        {
            plugin = PluginLoader.Load(pluginPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not load {pluginPath}: {e.Message}");
            return 1;
        }

        var host = new StandaloneHost(plugin)
        {
            AudioDriver = driver,
            AudioDevice = device,
            AudioSampleRate = sampleRate,
            AudioBufferSize = bufferSize,
        };
        if (saveFolder != null) host.SaveFolder = Path.GetFullPath(saveFolder);
        try
        {
            host.Run();
        }
        finally
        {
            Logger.FlushAndShutdown(); // the log is written on a thread of its own: its last lines, before the process ends
        }
        return 0;
    }

    static int Number(string? value) => int.TryParse(value, out int n) && n > 0 ? n : throw Fail();

    static UsageException Fail() => new();

    class UsageException : Exception;

    // Every driver's devices, with their channels, rates and buffer sizes.
    static int ListAudio()
    {
        using var engine = new AudioEngine(new AudioConfig(), AudioEngine.PlatformDrivers("AudioPlugSharpStandalone"));
        foreach (AudioDriver driver in engine.Drivers)
        {
            Console.WriteLine($"{driver.DriverName}:");
            foreach (string device in driver.Devices().GetAwaiter().GetResult())
            {
                try
                {
                    AudioDeviceInfo info = driver.Info(device).GetAwaiter().GetResult();
                    Console.WriteLine($"  {device}");
                    Console.WriteLine($"    inputs:       {(info.Inputs.Count > 0 ? string.Join(", ", info.Inputs) : "(none)")}");
                    Console.WriteLine($"    outputs:      {(info.Outputs.Count > 0 ? string.Join(", ", info.Outputs) : "(none)")}");
                    Console.WriteLine($"    sample rates: {string.Join(", ", info.SampleRates)} (now {info.DefaultSampleRate})");
                    Console.WriteLine($"    buffer sizes: {string.Join(", ", info.BufferSizes)} (preferred {info.DefaultBufferSize})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {device}: {ex.Message}");
                }
            }
        }
        return 0;
    }
}
