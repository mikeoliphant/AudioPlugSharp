#include "DotnetRuntime.h"

#include "aps/NativeLog.h"

#include <nethost.h>
#include <hostfxr.h>
#include <coreclr_delegates.h>

#include <cstdio>
#include <cwchar>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace aps {
namespace {

using string_t = std::basic_string<char_t>;

void* loadLibrary(const char_t* path) {
#ifdef _WIN32
    return ::LoadLibraryW(path);
#else
    return ::dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
}

void* getExport(void* lib, const char* name) {
#ifdef _WIN32
    return reinterpret_cast<void*>(::GetProcAddress(static_cast<HMODULE>(lib), name));
#else
    return ::dlsym(lib, name);
#endif
}

// hostfxr's own messages (why the runtime didn't start, which framework is missing) go to the native log.
void HOSTFXR_CALLTYPE errorWriter(const char_t* message) {
#ifdef _WIN32
    const int length = static_cast<int>(std::wcslen(message));
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, message, length, nullptr, 0, nullptr, nullptr);

    std::string utf8(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, message, length, utf8.data(), bytes, nullptr, nullptr);

    nativeLog("hostfxr: " + utf8);
#else
    nativeLog(std::string("hostfxr: ") + message);
#endif
}

std::string hex(int32_t rc) {
    char buf[16];
    std::snprintf(buf, sizeof(buf), "0x%08x", static_cast<uint32_t>(rc));
    return buf;
}

string_t widen(const std::string& ascii) { return string_t(ascii.begin(), ascii.end()); }

} // namespace

bool DotnetRuntime::initialize(const std::filesystem::path& runtimeConfig, std::string& error) {
    if (loadAssemblyAndGetFunctionPointer_) return true;

    // 1. Find hostfxr using nethost (honours DOTNET_ROOT, then the default install locations).
    char_t buffer[4096];
    size_t size = sizeof(buffer) / sizeof(char_t);
    int rc = get_hostfxr_path(buffer, &size, nullptr);
    if (rc != 0) {
        error = "get_hostfxr_path failed (" + hex(rc) + ") - is the .NET runtime installed?";
        return false;
    }

    hostfxrLib_ = loadLibrary(buffer);
    if (!hostfxrLib_) {
        error = "failed to load hostfxr";
        return false;
    }

    auto initFn = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        getExport(hostfxrLib_, "hostfxr_initialize_for_runtime_config"));
    auto getDelegateFn = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        getExport(hostfxrLib_, "hostfxr_get_runtime_delegate"));
    auto closeFn = reinterpret_cast<hostfxr_close_fn>(getExport(hostfxrLib_, "hostfxr_close"));
    auto setErrorWriterFn = reinterpret_cast<hostfxr_set_error_writer_fn>(
        getExport(hostfxrLib_, "hostfxr_set_error_writer"));

    if (!initFn || !getDelegateFn || !closeFn) {
        error = "hostfxr is missing required exports";
        return false;
    }
    if (setErrorWriterFn) setErrorWriterFn(errorWriter);

    // 2. Initialize the runtime from the framework's runtimeconfig.json.
    //    0 = success, 1 = already initialized, 2 = already initialized w/ different properties.
    hostfxr_handle handle = nullptr;
    rc = initFn(runtimeConfig.c_str(), nullptr, &handle);
    if (rc < 0 || handle == nullptr) {
        error = "hostfxr_initialize_for_runtime_config failed (" + hex(rc) + ") for " +
                runtimeConfig.string();
        constexpr int32_t kFrameworkMissing = static_cast<int32_t>(0x80008096);
        if (rc == kFrameworkMissing) {
#ifdef _WIN32
            error += ": a required .NET framework is not installed. Install the .NET Desktop Runtime "
                     "(version 10 or later), which AudioPlugSharp plugins need on Windows.";
#else
            error += ": a required .NET framework is not installed. Install the .NET Runtime (version 10 or later).";
#endif
        }
        if (handle) closeFn(handle);
        return false;
    }

    // 3. Get the "load assembly and get function pointer" delegate.
    void* fn = nullptr;
    rc = getDelegateFn(handle, hdt_load_assembly_and_get_function_pointer, &fn);
    closeFn(handle);
    if (rc != 0 || fn == nullptr) {
        error = "hostfxr_get_runtime_delegate failed (" + hex(rc) + ")";
        return false;
    }

    loadAssemblyAndGetFunctionPointer_ = fn;
    return true;
}

void* DotnetRuntime::getFunction(const std::filesystem::path& assembly, const std::string& typeName,
                                 const std::string& methodName, std::string& error) {
    if (!loadAssemblyAndGetFunctionPointer_) {
        error = "runtime not initialized";
        return nullptr;
    }
    auto loadFn = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(
        loadAssemblyAndGetFunctionPointer_);

    const string_t type = widen(typeName);
    const string_t method = widen(methodName);
    void* fn = nullptr;
    int rc = loadFn(assembly.c_str(), type.c_str(), method.c_str(),
                    UNMANAGEDCALLERSONLY_METHOD, nullptr, &fn);
    if (rc != 0 || fn == nullptr) {
        error = "failed to resolve " + typeName + "." + methodName + " (" + hex(rc) + ")";
        return nullptr;
    }
    return fn;
}

} // namespace aps
