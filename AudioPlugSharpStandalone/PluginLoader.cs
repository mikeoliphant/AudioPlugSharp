using System.Reflection;
using System.Runtime.Loader;
using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// Loads a plugin, as the DAW bridge does: the plugin assembly and its dependencies (UILayout, MonoGame, SDL, ...) load
/// into a context of their own, resolved through the plugin's .deps.json (a plugin project gets one from the
/// AudioPlugSharpBridge package, or with EnableDynamicLoading), or the application's for a plugin built into it.
/// AudioPlugSharp is shared with the host, so the plugin's types are the host's.
/// </summary>
public static class PluginLoader
{
    /// <summary>
    /// Creates the plugin in <paramref name="pluginPath"/>: its public IAudioPlugin implementation with a
    /// parameterless constructor.
    /// </summary>
    public static IAudioPlugin Load(string pluginPath)
    {
        string fullPath = Path.GetFullPath(pluginPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Plugin not found", fullPath);

        var context = new PluginLoadContext(fullPath);
        Assembly assembly = context.LoadFromAssemblyPath(fullPath);
        Type type = assembly.GetExportedTypes().FirstOrDefault(t =>
                        typeof(IAudioPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface &&
                        t.GetConstructor(Type.EmptyTypes) != null)
                    ?? throw new InvalidOperationException($"No public IAudioPlugin implementation with a parameterless constructor in {fullPath}");
        return (IAudioPlugin)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Creates a plugin built into the application, in a context of its own as for a plugin loaded by path: its
    /// assembly and dependencies resolve through the application's .deps.json, so it gets its own copies of them (the
    /// host's status bar has its own UILayout, and UILayout supports one layout per copy).
    /// </summary>
    public static IAudioPlugin Load(Type pluginType)
    {
        if (!typeof(IAudioPlugin).IsAssignableFrom(pluginType) || (pluginType.GetConstructor(Type.EmptyTypes) == null))
            throw new ArgumentException($"{pluginType} isn't an IAudioPlugin with a parameterless constructor", nameof(pluginType));

        string application = Assembly.GetEntryAssembly()?.Location ?? "";

        if (application == "")
            throw new InvalidOperationException("The application's assembly has no file (single-file publishing isn't supported)");

        AssemblyName assemblyName = pluginType.Assembly.GetName();

        var context = new PluginLoadContext(application, assemblyName.Name!);
        Assembly assembly = context.LoadFromAssemblyName(assemblyName);
        Type type = assembly.GetType(pluginType.FullName!, throwOnError: true)!;

        return (IAudioPlugin)Activator.CreateInstance(type)!;
    }

    class PluginLoadContext : AssemblyLoadContext
    {
        static readonly Assembly ApiAssembly = typeof(IAudioPlugin).Assembly;
        readonly AssemblyDependencyResolver resolver;

        // Resolves through the .deps.json of the assembly at resolverPath: the plugin's own, or the application's.
        public PluginLoadContext(string resolverPath, string? name = null)
            : base(name ?? Path.GetFileNameWithoutExtension(resolverPath))
        {
            resolver = new AssemblyDependencyResolver(resolverPath);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (string.Equals(name.Name, ApiAssembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return ApiAssembly;
            string? path = resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null; // null: the default context (the framework)
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : 0;
        }
    }
}
