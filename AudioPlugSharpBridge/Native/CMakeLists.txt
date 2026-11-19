cmake_minimum_required(VERSION 3.20)
project(AudioPlugSharpBridge LANGUAGES CXX)

# The native side of AudioPlugSharpBridge: the shared core (.NET runtime hosting, plugin factory, editor windows),
# the format adapters, and the test hosts. It also builds the managed bridge and the example plugins (dotnet CLI),
# so that the test hosts can run against them:
#   bin/bridge/AudioPlugSharpBridge.dll (+ .runtimeconfig.json) and AudioPlugSharp.dll
#   bin/plugins/<Example>/<Example>.dll
#   bin/BridgeTestHost[.exe]

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
# Plugin binaries export only their entry point: two AudioPlugSharp plugins (possibly different versions) in
# one process must never bind to each other's copies of the native core.
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
# Static CRT on Windows: plugin DLLs must not depend on DLLs next to them (the default DLL search does not
# look there) or on the Visual C++ redistributable. This also matches libnethost.lib.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(APS_BRIDGE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/..")
set(APS_ROOT_DIR "${APS_BRIDGE_DIR}/..")
set(APS_BIN_DIR "${CMAKE_BINARY_DIR}/bin")
# "$<0:>" stops multi-config generators from appending /Release etc.
set(APS_OUT "${APS_BIN_DIR}$<0:>")

# ---------------------------------------------------------------------------
# Locate the .NET SDK and the nethost library for the target RID.
# ---------------------------------------------------------------------------
find_program(DOTNET_EXE dotnet REQUIRED)
file(REAL_PATH "${DOTNET_EXE}" DOTNET_EXE_REAL)
get_filename_component(DOTNET_ROOT "${DOTNET_EXE_REAL}" DIRECTORY)

if(WIN32)
    set(APS_OS win)
elseif(APPLE)
    set(APS_OS osx)
else()
    set(APS_OS linux)
endif()

string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" APS_PROC)
if(APPLE AND CMAKE_OSX_ARCHITECTURES)
    set(APS_PROC "${CMAKE_OSX_ARCHITECTURES}")
endif()
if(APS_PROC MATCHES "^(amd64|x86_64|x64)$")
    set(APS_ARCH x64)
elseif(APS_PROC MATCHES "^(arm64|aarch64)$")
    set(APS_ARCH arm64)
else()
    message(FATAL_ERROR "Unsupported architecture: ${CMAKE_SYSTEM_PROCESSOR}")
endif()
set(APS_RID "${APS_OS}-${APS_ARCH}")

set(NETHOST_DIR "" CACHE PATH "Directory containing nethost.h and the nethost library (auto-detected if empty)")
if(NOT NETHOST_DIR)
    # Microsoft builds name the pack after the portable RID (linux-x64); distro-built .NET (e.g. Ubuntu's
    # packages) uses a distro RID such as ubuntu.26.04-x64. Accept either on Linux.
    file(GLOB _all_candidates LIST_DIRECTORIES true
         "${DOTNET_ROOT}/packs/Microsoft.NETCore.App.Host.*/*/runtimes/*/native")
    set(_nethost_candidates "")
    foreach(_cand IN LISTS _all_candidates)
        if(NOT _cand MATCHES "/(10\\.[^/]*)/runtimes/([^/]+)/native$")
            continue()
        endif()
        set(_rid "${CMAKE_MATCH_2}")
        if(_rid STREQUAL APS_RID)
            list(APPEND _nethost_candidates "${_cand}")
        elseif(APS_OS STREQUAL "linux" AND _rid MATCHES "-${APS_ARCH}$"
               AND NOT _rid MATCHES "^(win|osx|linux-musl|linux-bionic|android|ios|tvos|maccatalyst|browser|freebsd)")
            list(APPEND _nethost_candidates "${_cand}")
        endif()
    endforeach()
    if(NOT _nethost_candidates)
        message(FATAL_ERROR "Could not find the .NET 10 apphost pack for ${APS_RID} under ${DOTNET_ROOT}/packs. "
                            "Install the .NET 10 SDK (or your distro's apphost pack) or set NETHOST_DIR.")
    endif()
    list(SORT _nethost_candidates COMPARE NATURAL ORDER DESCENDING)
    list(GET _nethost_candidates 0 _nethost_dir)
    set(NETHOST_DIR "${_nethost_dir}" CACHE PATH "" FORCE)
endif()
message(STATUS "AudioPlugSharpBridge: RID ${APS_RID}, nethost from ${NETHOST_DIR}")

# ---------------------------------------------------------------------------
# Managed projects (the bridge and the example plugins), built with the dotnet CLI.
# ---------------------------------------------------------------------------
set(APS_EXAMPLES SimpleExample MidiExample UILayoutExample)
if(WIN32)
    list(APPEND APS_EXAMPLES WPFExample) # net10.0-windows
endif()
set(APS_DOTNET_CONFIG $<IF:$<CONFIG:Debug>,Debug,Release>)
set(_managed_commands
    COMMAND "${DOTNET_EXE}" build "${APS_BRIDGE_DIR}/AudioPlugSharpBridge.csproj" -c ${APS_DOTNET_CONFIG}
            "-p:AudioPlugSharpBridgeOutDir=${APS_BIN_DIR}/bridge" --nologo -v:minimal)
foreach(_ex IN LISTS APS_EXAMPLES)
    list(APPEND _managed_commands
        COMMAND "${DOTNET_EXE}" build "${APS_ROOT_DIR}/Examples/${_ex}/${_ex}.csproj" -c ${APS_DOTNET_CONFIG}
                "-p:OutDir=${APS_BIN_DIR}/plugins/${_ex}/" --nologo -v:minimal)
endforeach()
add_custom_target(managed ALL ${_managed_commands}
    WORKING_DIRECTORY "${APS_ROOT_DIR}"
    COMMENT "Building the managed bridge and the example plugins"
    VERBATIM)

# ---------------------------------------------------------------------------
# Native core: runtime boot, plugin factory and editor windows, shared by the adapters and the test hosts.
# ---------------------------------------------------------------------------
find_package(Threads REQUIRED)

add_library(aps_native_core STATIC
    Core/ManagedPluginFactory.cpp
    Core/DotnetRuntime.cpp
    Core/DotnetRuntime.h
    Include/aps/ManagedPluginFactory.h
    Include/aps/EditorWindow.h
    Include/aps/abi_support.hpp
    # Generated by Tools/AbiGen during the managed build (committed).
    Include/aps/generated/abi.h
    Include/aps/generated/abi.hpp)
target_include_directories(aps_native_core PUBLIC Include PRIVATE Core "${NETHOST_DIR}")
target_link_libraries(aps_native_core PUBLIC Threads::Threads)
add_dependencies(aps_native_core managed) # the managed build regenerates the ABI headers
if(WIN32)
    target_sources(aps_native_core PRIVATE Core/EditorWindowWin32.cpp)
elseif(UNIX AND NOT APPLE)
    find_package(X11 REQUIRED) # Debian/Ubuntu: libx11-dev
    target_sources(aps_native_core PRIVATE Core/EditorWindowX11.cpp)
    target_link_libraries(aps_native_core PUBLIC X11::X11)
else()
    target_sources(aps_native_core PRIVATE Core/EditorWindowNone.cpp)
endif()
if(WIN32)
    target_compile_definitions(aps_native_core PRIVATE NETHOST_USE_AS_STATIC UNICODE _UNICODE)
    target_link_libraries(aps_native_core PUBLIC "${NETHOST_DIR}/libnethost.lib")
else()
    target_link_libraries(aps_native_core PUBLIC "${NETHOST_DIR}/libnethost.a" ${CMAKE_DL_LIBS})
endif()

function(aps_warnings target)
    if(MSVC)
        target_compile_options(${target} PRIVATE /W4 /permissive-)
    else()
        target_compile_options(${target} PRIVATE -Wall -Wextra)
    endif()
endfunction()
aps_warnings(aps_native_core)

# ---------------------------------------------------------------------------
# BridgeTestHost: loads the example plugins through the bridge (functional demo, isolation, stress,
# allocation check on the audio path).
# ---------------------------------------------------------------------------
add_executable(BridgeTestHost "${APS_BRIDGE_DIR}/Tests/BridgeTestHost/BridgeTestHost.cpp")
target_link_libraries(BridgeTestHost PRIVATE aps_native_core)
set_target_properties(BridgeTestHost PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${APS_OUT}")
if(WIN32)
    target_link_libraries(BridgeTestHost PRIVATE winmm)
endif()
aps_warnings(BridgeTestHost)
