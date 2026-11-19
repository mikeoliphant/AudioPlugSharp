cmake_minimum_required(VERSION 3.20)
project(AudioPlugSharpBridge LANGUAGES CXX)

# The native side of AudioPlugSharpBridge: the shared core (.NET runtime hosting, plugin factory, editor windows),
# the format adapters, and the test hosts. It also builds the managed bridge and the example plugins (dotnet CLI),
# so that the test hosts can run against them:
#   bin/bridge/AudioPlugSharpBridge.dll (+ .runtimeconfig.json) and AudioPlugSharp.dll
#   bin/plugins/<Plugin>/<Plugin>.dll          (examples and test plugins)
#   bin/clap/<Plugin>/<Plugin>.clap            (installable CLAP folder: adapter + plugin + bridge)
#   bin/BridgeTestHost[.exe], bin/ClapTestHost[.exe]
# The format adapters are built by their folders (Clap/), from the shared parts defined here.

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
# The .NET SDK (for the managed build) and the target RID.
# ---------------------------------------------------------------------------
find_program(DOTNET_EXE dotnet REQUIRED)

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

# ---------------------------------------------------------------------------
# nethost: lets the native core find the installed .NET runtime (get_hostfxr_path), linked statically. It comes
# from Microsoft's per-platform package runtime.<rid>.Microsoft.NETCore.DotNetAppHost, fetched by NuGet
# (NetHost/NetHost.csproj, restore only) so that nuget.config feeds, mirrors and the offline cache apply. Pinned
# here by version; NETHOST_DIR overrides it (a folder with nethost.h and the static library).
# ---------------------------------------------------------------------------
set(APS_NETHOST_VERSION 10.0.15)

set(NETHOST_DIR "" CACHE PATH "Directory with nethost.h and the static nethost library (empty: restore the pinned package)")
if(NETHOST_DIR)
    set(APS_NETHOST_DIR "${NETHOST_DIR}")
else()
    execute_process(
        COMMAND "${DOTNET_EXE}" restore "${CMAKE_CURRENT_SOURCE_DIR}/NetHost/NetHost.csproj"
                "-p:ApsRid=${APS_RID}" "-p:ApsNetHostVersion=${APS_NETHOST_VERSION}" --nologo -v:quiet
        RESULT_VARIABLE _restore_result OUTPUT_VARIABLE _restore_out ERROR_VARIABLE _restore_out)
    if(NOT _restore_result EQUAL 0)
        message(FATAL_ERROR "Restoring nethost ${APS_NETHOST_VERSION} for ${APS_RID} failed (or set NETHOST_DIR):\n${_restore_out}")
    endif()
    execute_process(COMMAND "${DOTNET_EXE}" nuget locals global-packages --list
                    OUTPUT_VARIABLE _packages_out RESULT_VARIABLE _packages_result)
    if(NOT _packages_result EQUAL 0 OR NOT _packages_out MATCHES "global-packages: *([^\r\n]+)")
        message(FATAL_ERROR "Could not find the NuGet global packages folder:\n${_packages_out}")
    endif()
    file(TO_CMAKE_PATH "${CMAKE_MATCH_1}" _packages)
    string(REGEX REPLACE "/+$" "" _packages "${_packages}")
    set(APS_NETHOST_DIR
        "${_packages}/runtime.${APS_RID}.microsoft.netcore.dotnetapphost/${APS_NETHOST_VERSION}/runtimes/${APS_RID}/native")
    if(NOT EXISTS "${APS_NETHOST_DIR}/nethost.h")
        message(FATAL_ERROR "nethost.h not found in ${APS_NETHOST_DIR} after the restore")
    endif()
endif()
message(STATUS "AudioPlugSharpBridge: RID ${APS_RID}, nethost from ${APS_NETHOST_DIR}")

# ---------------------------------------------------------------------------
# Managed projects (the bridge, the example plugins and the test plugins), built with the dotnet CLI.
# ---------------------------------------------------------------------------
set(APS_EXAMPLES SimpleExample MidiExample UILayoutExample)
if(WIN32)
    list(APPEND APS_EXAMPLES WPFExample) # net10.0-windows
endif()
set(APS_TEST_PLUGINS UILayoutTestPlugin)
set(APS_PLUGINS ${APS_EXAMPLES} ${APS_TEST_PLUGINS})
set(APS_DOTNET_CONFIG $<IF:$<CONFIG:Debug>,Debug,Release>)
set(_managed_commands
    COMMAND "${DOTNET_EXE}" build "${APS_BRIDGE_DIR}/AudioPlugSharpBridge.csproj" -c ${APS_DOTNET_CONFIG}
            "-p:AudioPlugSharpBridgeOutDir=${APS_BIN_DIR}/bridge" --nologo -v:minimal)
foreach(_ex IN LISTS APS_EXAMPLES)
    list(APPEND _managed_commands
        COMMAND "${DOTNET_EXE}" build "${APS_ROOT_DIR}/Examples/${_ex}/${_ex}.csproj" -c ${APS_DOTNET_CONFIG}
                "-p:OutDir=${APS_BIN_DIR}/plugins/${_ex}/" --nologo -v:minimal)
endforeach()
foreach(_tp IN LISTS APS_TEST_PLUGINS)
    list(APPEND _managed_commands
        COMMAND "${DOTNET_EXE}" build "${APS_BRIDGE_DIR}/Tests/Plugins/${_tp}/${_tp}.csproj" -c ${APS_DOTNET_CONFIG}
                "-p:OutDir=${APS_BIN_DIR}/plugins/${_tp}/" --nologo -v:minimal)
endforeach()
add_custom_target(managed ALL ${_managed_commands}
    WORKING_DIRECTORY "${APS_ROOT_DIR}"
    COMMENT "Building the managed bridge, the example plugins and the test plugins"
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
target_include_directories(aps_native_core PUBLIC Include PRIVATE Core "${APS_NETHOST_DIR}")
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
    target_link_libraries(aps_native_core PUBLIC "${APS_NETHOST_DIR}/libnethost.lib")
else()
    target_link_libraries(aps_native_core PUBLIC "${APS_NETHOST_DIR}/libnethost.a" ${CMAKE_DL_LIBS})
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

# ---------------------------------------------------------------------------
# The format adapters. Each folder builds its adapter, its test host and its installable bundles from the shared
# parts above (aps_native_core, aps_warnings, APS_PLUGINS, APS_BIN_DIR/APS_OUT).
# ---------------------------------------------------------------------------
include(FetchContent)
add_subdirectory(Clap)
