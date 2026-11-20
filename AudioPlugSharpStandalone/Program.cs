using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// AudioPlugSharpStandalone &lt;plugin.dll&gt;: runs any AudioPlugSharp plugin from its build output as an application.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: AudioPlugSharpStandalone <plugin.dll>");
            return 2;
        }

        IAudioPlugin plugin;
        try
        {
            plugin = PluginLoader.Load(args[0]);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not load {args[0]}: {e.Message}");
            return 1;
        }

        new StandaloneHost(plugin).Run();
        return 0;
    }
}
