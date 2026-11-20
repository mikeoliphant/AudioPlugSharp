using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// AudioPlugSharpStandalone [--save-folder &lt;folder&gt;] &lt;plugin.dll&gt;: runs any AudioPlugSharp plugin from its build
/// output as an application. The plugin's state is kept in --save-folder, by default in a folder named after the
/// plugin in the user's application data (see <see cref="StandaloneHost.SaveFolder"/>).
/// </summary>
static class Program
{
    const string Usage = "Usage: AudioPlugSharpStandalone [--save-folder <folder>] <plugin.dll>";

    [STAThread]
    static int Main(string[] args)
    {
        string? pluginPath = null, saveFolder = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--save-folder" && i + 1 < args.Length)
                saveFolder = args[++i];
            else if (pluginPath == null && !args[i].StartsWith("--"))
                pluginPath = args[i];
            else
                return Fail(Usage);
        }
        if (pluginPath == null) return Fail(Usage);

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

        var host = new StandaloneHost(plugin);
        if (saveFolder != null) host.SaveFolder = Path.GetFullPath(saveFolder);
        host.Run();
        return 0;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
