// ManagedPluginFactory.h - boots the .NET runtime and creates AudioPlugSharp plugin instances.
// The plugin/host interfaces themselves are generated: see aps/generated/abi.hpp.
#pragma once

#include "generated/abi.hpp"

#include <filesystem>
#include <memory>
#include <string>

extern "C" {
// Managed entry points resolved through hostfxr from AudioPlugSharpBridge.dll. They are bootstrap
// functions rather than interface members, so they are declared here by hand.

// AudioPlugSharpBridge.NativeEntry.CreatePlugin: loads the plugin assembly into a NEW collectible
// load context (one per instance), instantiates the first public IAudioPlugin implementation,
// initializes it and fills *out_plugin. The returned aps_plugin_api's destroy() unloads the context.
typedef aps_result(APS_CALL* aps_create_plugin_fn)(const char* plugin_path, const aps_host_api* host,
                                                   aps_plugin_api* out_plugin);

// AudioPlugSharpBridge.Diagnostics.* - testing only.
typedef void(APS_CALL* aps_diag_start_gc_pressure_fn)(int32_t megabytes_per_second);
typedef void(APS_CALL* aps_diag_stop_gc_pressure_fn)(void);
typedef int32_t(APS_CALL* aps_diag_gc_count_fn)(int32_t generation);
typedef double(APS_CALL* aps_diag_gc_pause_ms_fn)(void);
typedef int32_t(APS_CALL* aps_diag_live_plugin_contexts_fn)(void); // forces GCs
typedef int64_t(APS_CALL* aps_diag_allocated_bytes_fn)(void);
typedef void(APS_CALL* aps_diag_reset_fn)(void);
}

namespace aps {

struct PluginDiagnostics {
    aps_diag_start_gc_pressure_fn startGcPressure = nullptr;
    aps_diag_stop_gc_pressure_fn stopGcPressure = nullptr;
    aps_diag_gc_count_fn gcCount = nullptr;
    aps_diag_gc_pause_ms_fn gcPauseMs = nullptr;
    aps_diag_live_plugin_contexts_fn livePluginContexts = nullptr;
    // Bytes allocated on audio threads, process-wide: inside the bridge (must be 0) and inside plugin code.
    aps_diag_allocated_bytes_fn bridgeAllocatedBytes = nullptr;
    aps_diag_allocated_bytes_fn pluginAllocatedBytes = nullptr;
    aps_diag_reset_fn resetAllocationCounters = nullptr;
};

class ManagedPluginFactory {
public:
    ManagedPluginFactory();
    ~ManagedPluginFactory();
    ManagedPluginFactory(const ManagedPluginFactory&) = delete;
    ManagedPluginFactory& operator=(const ManagedPluginFactory&) = delete;

    // bridgeDir contains AudioPlugSharpBridge.dll (+ .runtimeconfig.json) and AudioPlugSharp.dll.
    bool initialize(const std::filesystem::path& bridgeDir, std::string& error);

    // Each call creates a fully isolated instance (own load context, own statics).
    // `host` must outlive the returned plugin.
    std::unique_ptr<PluginApi> load(const std::filesystem::path& pluginAssembly, HostApi& host,
                                    std::string& error);

    const PluginDiagnostics& diagnostics() const { return diagnostics_; }

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
    aps_create_plugin_fn createPlugin_ = nullptr;
    PluginDiagnostics diagnostics_;
};

} // namespace aps
