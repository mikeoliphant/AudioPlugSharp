using System.Reflection;
using System.Runtime.Loader;

namespace AudioPlugSharpBridge.Interop;

/// <summary>
/// Every plugin INSTANCE gets its own collectible AssemblyLoadContext. The plugin assembly and all of its
/// private dependencies (UILayout, MonoGame, ...) are loaded fresh into it, so their static state is
/// per-instance.
///
/// Exception: a plugin whose CacheLoadContext is true (WPF plugins: WPF's resource lookup breaks when an assembly
/// is loaded more than once, dotnet/wpf#1700) gets one context per plugin file, shared by all its instances and
/// never unloaded.
///
/// Shared (one copy per process) and therefore NOT isolated:
///   - AudioPlugSharp (the public API) and AudioPlugSharpBridge, so every instance sees the same
///     IAudioPlugin type. Neither keeps per-plugin static state, except AudioPlugSharp's Logger.
///   - The .NET shared framework (System.*), resolved from the default context.
/// </summary>
internal sealed class InstanceLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ApiAssembly = typeof(IAudioPlugin).Assembly;
    private static readonly Assembly BridgeAssembly = typeof(InstanceLoadContext).Assembly;
    private static int _nextId;

    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>Shared by every instance of the plugin (CacheLoadContext); never unloaded.</summary>
    public bool Shared { get; set; }

    public InstanceLoadContext(string pluginPath)
        : base($"{Path.GetFileNameWithoutExtension(pluginPath)}#{Interlocked.Increment(ref _nextId)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (IsShared(name, ApiAssembly)) return ApiAssembly;
        if (IsShared(name, BridgeAssembly)) return BridgeAssembly;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null; // null -> default context (BCL)
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : 0;
    }

    private static bool IsShared(AssemblyName name, Assembly shared) =>
        string.Equals(name.Name, shared.GetName().Name, StringComparison.OrdinalIgnoreCase);
}

internal static class InstanceLoader
{
    private static readonly object TrackLock = new();
    private static readonly List<WeakReference> Contexts = new();
    private static readonly Dictionary<string, (InstanceLoadContext Context, Type Type)> SharedContexts =
        new(StringComparer.OrdinalIgnoreCase);

    public static (IAudioPlugin Plugin, InstanceLoadContext Context, Precompiler.Stats Precompiled) Load(string pluginPath)
    {
        var fullPath = Path.GetFullPath(pluginPath);

        (InstanceLoadContext Context, Type Type) shared;
        lock (TrackLock)
            if (!SharedContexts.TryGetValue(fullPath, out shared)) shared = default;
        if (shared.Context != null)
        {
            // Already loaded and precompiled for an earlier instance.
            return ((IAudioPlugin)Activator.CreateInstance(shared.Type!)!, shared.Context, default);
        }

        var context = new InstanceLoadContext(fullPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            var type = assembly.GetExportedTypes().FirstOrDefault(t =>
                           typeof(IAudioPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface &&
                           t.GetConstructor(Type.EmptyTypes) != null)
                       ?? throw new InvalidOperationException(
                           $"No public IAudioPlugin implementation with a parameterless constructor in {fullPath}");

            // Each context has its own copy of the code, so each one is precompiled separately.
            // Only the realtime closure is compiled; UI and other main-thread code JITs lazily.
            var stats = Precompiler.PrecompileRealtime(type, context);
            var plugin = (IAudioPlugin)Activator.CreateInstance(type)!;
            Track(context);
            if (plugin.CacheLoadContext)
            {
                context.Shared = true;
                lock (TrackLock)
                {
                    // Two instances created at once on different threads: keep the first context.
                    if (SharedContexts.TryGetValue(fullPath, out var first))
                    {
                        context.Unload();
                        return ((IAudioPlugin)Activator.CreateInstance(first.Type)!, first.Context, default);
                    }
                    SharedContexts[fullPath] = (context, type);
                }
            }
            return (plugin, context, stats);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static void Track(InstanceLoadContext context)
    {
        lock (TrackLock)
        {
            Contexts.RemoveAll(w => !w.IsAlive);
            Contexts.Add(new WeakReference(context));
        }
    }

    /// <summary>Plugin contexts not yet collected. Unloading finishes only after a GC.</summary>
    public static int LiveContextCount()
    {
        lock (TrackLock)
        {
            Contexts.RemoveAll(w => !w.IsAlive);
            return Contexts.Count;
        }
    }
}
