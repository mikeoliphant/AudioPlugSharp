// NativeLog.h - the native side's log, for what happens outside managed code: above all startup failures (the
// .NET runtime or the plugin assembly not loading), which happen before there is a managed log to write to.
//
// Lines go to native.log in AudioPlugSharp's log folder (where the managed Logger writes its logs: %APPDATA% on
// Windows, $XDG_CONFIG_HOME or ~/.config on Linux), flushed one by one so a crash loses nothing, and to stderr. The
// file is created on the first line only: most sessions have none. Each process starts with a header line (time,
// executable, process id); a file over 1 MB is started afresh by the next process that writes.
//
// Messages from managed code (the bridge, the plugin) go to the managed log, not here.
#pragma once

#include <exception>
#include <string>
#include <string_view>

namespace aps {

/// Writes a line (thread-safe; never throws).
void nativeLog(std::string_view message) noexcept;

/// Runs f, turning a C++ exception into a line in the native log and the given failure result: for entry points
/// the host calls while loading a plugin, so an exception there fails the load instead of the host.
template <typename R, typename F>
R guarded(const char* where, R failed, F&& f) noexcept {
    try {
        return f();
    } catch (const std::exception& e) {
        nativeLog(std::string(where) + ": " + e.what());
    } catch (...) {
        nativeLog(std::string(where) + ": unknown exception");
    }

    return failed;
}

} // namespace aps
