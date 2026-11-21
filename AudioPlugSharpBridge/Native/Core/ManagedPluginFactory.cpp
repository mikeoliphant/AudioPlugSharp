#include "aps/ManagedPluginFactory.h"
#include "DotnetRuntime.h"

namespace aps {
namespace {
constexpr const char* kBridgeName = "AudioPlugSharpBridge";
}

struct ManagedPluginFactory::Impl {
    DotnetRuntime runtime;
};

ManagedPluginFactory::ManagedPluginFactory() : impl_(std::make_unique<Impl>()) {}
ManagedPluginFactory::~ManagedPluginFactory() = default;

bool ManagedPluginFactory::initialize(const std::filesystem::path& bridgeDir, std::string& error) {
    const auto assembly = bridgeDir / (std::string(kBridgeName) + ".dll");
    const auto config = bridgeDir / (std::string(kBridgeName) + ".runtimeconfig.json");

    if (!impl_->runtime.initialize(config, error)) return false;

    const std::string entryType = std::string(kBridgeName) + ".NativeEntry, " + kBridgeName;
    const std::string diagType = std::string(kBridgeName) + ".Diagnostics, " + kBridgeName;

    createPlugin_ = reinterpret_cast<aps_create_plugin_fn>(
        impl_->runtime.getFunction(assembly, entryType, "CreatePlugin", error));
    if (!createPlugin_) return false;

    std::string ignored; // diagnostics are optional
    auto get = [&](const char* name) { return impl_->runtime.getFunction(assembly, diagType, name, ignored); };
    diagnostics_.startGcPressure = reinterpret_cast<aps_diag_start_gc_pressure_fn>(get("StartGcPressure"));
    diagnostics_.stopGcPressure = reinterpret_cast<aps_diag_stop_gc_pressure_fn>(get("StopGcPressure"));
    diagnostics_.gcCount = reinterpret_cast<aps_diag_gc_count_fn>(get("GcCount"));
    diagnostics_.gcPauseMs = reinterpret_cast<aps_diag_gc_pause_ms_fn>(get("GcTotalPauseMs"));
    diagnostics_.livePluginContexts = reinterpret_cast<aps_diag_live_plugin_contexts_fn>(get("LivePluginContexts"));
    diagnostics_.bridgeAllocatedBytes = reinterpret_cast<aps_diag_allocated_bytes_fn>(get("BridgeAllocatedBytes"));
    diagnostics_.pluginAllocatedBytes = reinterpret_cast<aps_diag_allocated_bytes_fn>(get("PluginAllocatedBytes"));
    diagnostics_.resetAllocationCounters = reinterpret_cast<aps_diag_reset_fn>(get("ResetAllocationCounters"));
    return true;
}

std::unique_ptr<PluginApi> ManagedPluginFactory::load(const std::filesystem::path& pluginAssembly, HostApi& host,
                                                      std::string& error) {
    if (!createPlugin_) {
        error = "factory not initialized";
        return nullptr;
    }

    // The managed side copies the vtable, so it only needs to live for the duration of the call.
    const aps_host_api hostApi = makeHostApi(host);

    const auto u8 = std::filesystem::absolute(pluginAssembly).u8string();
    aps_plugin_api api{};
    aps_result rc = createPlugin_(reinterpret_cast<const char*>(u8.c_str()), &hostApi, &api);
    if (rc != APS_OK) {
        error = "CreatePlugin failed with code " + std::to_string(rc);
        return nullptr;
    }
    if (api.abi_version != APS_ABI_VERSION || PluginApi::findMissing(api) != nullptr) {
        const char* missing = PluginApi::findMissing(api);
        error = std::string("plugin ABI mismatch (version ") + std::to_string(api.abi_version) + ", missing " +
                (missing ? missing : "nothing") + ")";
        if (api.destroy) api.destroy(api.self);
        return nullptr;
    }
    return std::make_unique<PluginApi>(api);
}

} // namespace aps
