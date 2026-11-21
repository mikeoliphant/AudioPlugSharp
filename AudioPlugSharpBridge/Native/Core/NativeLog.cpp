#include "aps/NativeLog.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <filesystem>
#include <mutex>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <share.h>
#else
#include <unistd.h>
#endif

namespace aps {
namespace {

namespace fs = std::filesystem;

constexpr std::uintmax_t kMaxSize = 1024 * 1024;

std::mutex logMutex;
std::FILE* logFile = nullptr;
bool opened = false; // tried (the file may still be missing: then stderr only)

// AudioPlugSharp's log folder, as .NET's ApplicationData folder gives it to the managed Logger.
fs::path logFolder() {
#ifdef _WIN32
    wchar_t appData[MAX_PATH];
    const DWORD length = GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH);

    return (length > 0 && length < MAX_PATH) ? fs::path(appData) / L"AudioPlugSharp" : fs::path();
#else
    const char* config = std::getenv("XDG_CONFIG_HOME");
    const char* home = std::getenv("HOME");

    if (config && *config) return fs::path(config) / "AudioPlugSharp";

    return home ? fs::path(home) / ".config" / "AudioPlugSharp" : fs::path();
#endif
}

std::string executable() {
#ifdef _WIN32
    wchar_t path[MAX_PATH];
    const int length = static_cast<int>(GetModuleFileNameW(nullptr, path, MAX_PATH));
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, path, length, nullptr, 0, nullptr, nullptr);

    std::string utf8(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, path, length, utf8.data(), bytes, nullptr, nullptr);

    return utf8;
#else
    std::error_code ec;

    return fs::read_symlink("/proc/self/exe", ec).string();
#endif
}

long processId() {
#ifdef _WIN32
    return static_cast<long>(GetCurrentProcessId());
#else
    return static_cast<long>(getpid());
#endif
}

std::string timestamp() {
    const auto now = std::chrono::system_clock::now();
    const std::time_t seconds = std::chrono::system_clock::to_time_t(now);
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()).count() % 1000;

    std::tm local{};
#ifdef _WIN32
    localtime_s(&local, &seconds);
#else
    localtime_r(&seconds, &local);
#endif

    char text[32];
    std::snprintf(text, sizeof(text), "%04d/%02d/%02d %02d:%02d:%02d.%03d", local.tm_year + 1900, local.tm_mon + 1,
                  local.tm_mday, local.tm_hour, local.tm_min, local.tm_sec, static_cast<int>(ms));

    return text;
}

// Opens native.log for this process's first line (caller holds logMutex).
void openLocked() {
    opened = true;

    const fs::path folder = logFolder();

    if (folder.empty()) return;

    std::error_code ec;
    fs::create_directories(folder, ec);

    const fs::path file = folder / "native.log";
    const std::uintmax_t size = fs::file_size(file, ec);
    const bool restart = !ec && size > kMaxSize;

#ifdef _WIN32
    // Shared, so the log can be read while a host has it open.
    logFile = _wfsopen(file.c_str(), restart ? L"w" : L"a", _SH_DENYNO);
#else
    logFile = std::fopen(file.c_str(), restart ? "w" : "a");
#endif

    if (!logFile) return;

    std::fprintf(logFile, "[%s] ---- %s (process %ld)\n", timestamp().c_str(), executable().c_str(), processId());
}

} // namespace

void nativeLog(std::string_view message) noexcept {
    try {
        std::lock_guard lock(logMutex);

        if (!opened) openLocked();

        const std::string line = "[" + timestamp() + "] " + std::string(message) + "\n";

        std::fputs(("[AudioPlugSharp] " + std::string(message) + "\n").c_str(), stderr);

        if (logFile) {
            std::fputs(line.c_str(), logFile);
            std::fflush(logFile);
        }
    } catch (...) {
        // Nowhere left to report it.
    }
}

} // namespace aps
