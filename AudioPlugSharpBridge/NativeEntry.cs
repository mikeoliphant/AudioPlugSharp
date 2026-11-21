using System.Runtime;
using System.Runtime.InteropServices;
using AudioPlugSharpBridge.Interop;

namespace AudioPlugSharpBridge;

/// <summary>Entry point resolved by the native adapter through hostfxr. Not for plugin authors.</summary>
public static unsafe class NativeEntry
{
    private static readonly object InitLock = new();
    private static string? _initError;
    private static bool _initialized;

    /// <summary>
    /// aps_result CreatePlugin(const char* plugin_path, const aps_host_api* host, aps_plugin_api* out_plugin).
    /// Parameters are void* to keep the interop types internal.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int CreatePlugin(byte* pluginPathUtf8, void* hostApi, void* outPluginApi)
    {
        if (pluginPathUtf8 == null || hostApi == null || outPluginApi == null) return AbiResult.InvalidArgument;

        var hostVTable = (HostApiVTable*)hostApi;
        var missing = HostApiProxy.FindMissing(hostVTable);
        if (hostVTable->AbiVersion != AbiInfo.Version || missing != null)
        {
            Report($"host ABI mismatch: version {hostVTable->AbiVersion} (expected {AbiInfo.Version}), missing member: {missing ?? "none"}");
            return AbiResult.AbiMismatch;
        }

        var host = new ManagedHost(new HostApiProxy(hostVTable));
        try
        {
            if (!EnsureInitialized(out var initError))
            {
                host.Log(LogLevel.Error, $"AudioPlugSharpBridge initialization failed: {initError}");
                return AbiResult.AbiMismatch;
            }

            var path = Marshal.PtrToStringUTF8((nint)pluginPathUtf8)!;
            var (plugin, context, precompiled) = InstanceLoader.Load(path);
            if (precompiled.Considered > 0)
                host.Log(LogLevel.Debug,
                    $"Precompiled {precompiled.Compiled} realtime methods in {precompiled.Milliseconds:0.0} ms " +
                    $"(the plugin's load context has {Precompiler.CountMethods(context)} methods)");
            else if (context.Shared)
                host.Log(LogLevel.Debug, "Using the plugin's shared load context (CacheLoadContext)");

            var instance = new PluginInstance(plugin, context, host);
            try
            {
                instance.Initialize();
            }
            catch
            {
                instance.Unload();
                throw;
            }

            var handle = GCHandle.Alloc(instance);
            PluginApiThunks.Fill((PluginApiVTable*)outPluginApi, GCHandle.ToIntPtr(handle));
            return AbiResult.Ok;
        }
        catch (Exception e)
        {
            host.Log(LogLevel.Error, $"CreatePlugin failed: {e}");
            return AbiResult.Exception;
        }
    }

    private static void Report(string message) =>
        AbiUtf8.ReportUnhandled(nameof(CreatePlugin), new InvalidOperationException(message));

    private static bool EnsureInitialized(out string? error)
    {
        lock (InitLock)
        {
            if (!_initialized)
            {
                _initError = AbiLayout.Verify();
                if (_initError == null)
                {
                    // An exception on a thread of the plugin's own ends the DAW: at least it's in the log.
                    Logger.LogUnhandledExceptions();

                    // Avoid blocking gen2 collections while plugins are running.
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    // The shared assemblies are small; compile them completely.
                    Precompiler.PrecompileAll(typeof(NativeEntry).Assembly);
                    Precompiler.PrecompileAll(typeof(IAudioPlugin).Assembly);
                }
                _initialized = true;
            }
            error = _initError;
            return error == null;
        }
    }
}
