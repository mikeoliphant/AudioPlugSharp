// DotnetRuntime.h - minimal cross-platform wrapper around nethost + hostfxr.
#pragma once

#include <filesystem>
#include <string>

namespace aps {

class DotnetRuntime {
public:
    // Locates hostfxr (via nethost) and initializes the runtime from a .runtimeconfig.json.
    // The runtime can only be started once per process; later calls with a compatible
    // config attach to the already-running runtime.
    bool initialize(const std::filesystem::path& runtimeConfig, std::string& error);

    // Returns a native-callable pointer to a static [UnmanagedCallersOnly] C# method.
    // typeName is assembly-qualified, e.g. "Ns.Type, AssemblyName".
    void* getFunction(const std::filesystem::path& assembly, const std::string& typeName,
                      const std::string& methodName, std::string& error);

private:
    void* hostfxrLib_ = nullptr;
    void* loadAssemblyAndGetFunctionPointer_ = nullptr;
};

} // namespace aps
