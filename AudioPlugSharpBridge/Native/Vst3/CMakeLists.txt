# The VST3 adapter, its test host and the installable VST3 bundles. Included from ../CMakeLists.txt, which
# provides aps_native_core, aps_warnings, APS_PLUGINS, APS_ARCH, APS_BIN_DIR/APS_OUT and APS_BRIDGE_DIR.

# ---------------------------------------------------------------------------
# VST3: only the interfaces (pluginterfaces, MIT since 3.8), no SDK helper classes. Fetched into a folder named
# "pluginterfaces" so the SDK's include paths (pluginterfaces/...) resolve.
# ---------------------------------------------------------------------------
set(VST3_ROOT "${CMAKE_BINARY_DIR}/_deps/vst3")
FetchContent_Declare(vst3_pluginterfaces
    GIT_REPOSITORY https://github.com/steinbergmedia/vst3_pluginterfaces.git
    GIT_TAG 4f547e8e102b47de4a8b8aaf343c73b700786372 # v3.8.1_build_84
    SOURCE_DIR "${VST3_ROOT}/pluginterfaces"
    SOURCE_SUBDIR do-not-add)
FetchContent_MakeAvailable(vst3_pluginterfaces)

# The bundle's architecture folder and the binary's suffix (VST3 bundle format).
if(WIN32)
    set(APS_VST3_ARCH_DIR "x86_64-win")
    if(APS_ARCH STREQUAL "arm64")
        set(APS_VST3_ARCH_DIR "arm64-win")
    endif()
    set(APS_VST3_SUFFIX ".vst3")
else()
    set(APS_VST3_ARCH_DIR "x86_64-linux")
    if(APS_ARCH STREQUAL "arm64")
        set(APS_VST3_ARCH_DIR "aarch64-linux")
    endif()
    set(APS_VST3_SUFFIX ".so")
endif()

# The generic VST3 adapter: one binary for every AudioPlugSharp plugin, shipped as
# <Plugin>.vst3/Contents/<arch>/<Plugin>.vst3 (.so on Linux) next to <Plugin>.dll and the bridge files.
add_library(AudioPlugSharpVst3 MODULE Vst3Adapter.cpp "${VST3_ROOT}/pluginterfaces/base/funknown.cpp")
target_include_directories(AudioPlugSharpVst3 PRIVATE "${VST3_ROOT}")
target_link_libraries(AudioPlugSharpVst3 PRIVATE aps_native_core)
set_target_properties(AudioPlugSharpVst3 PROPERTIES PREFIX "" SUFFIX "${APS_VST3_SUFFIX}" LIBRARY_OUTPUT_DIRECTORY "${APS_OUT}")
if(WIN32)
    target_link_libraries(AudioPlugSharpVst3 PRIVATE ole32) # funknown.cpp: CoCreateGuid
else()
    # The C++ runtime is linked statically (no dependency on the system's or the host's libstdc++/libgcc_s; any
    # compiler), and --exclude-libs,ALL plus the exports map keep its symbols, like nethost's, out of the dynamic
    # symbol table, so they can't interpose with the host's or other plugins' C++ runtimes.
    target_link_options(AudioPlugSharpVst3 PRIVATE -static-libstdc++ -static-libgcc "LINKER:--exclude-libs,ALL"
        "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
    set_property(TARGET AudioPlugSharpVst3 APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
endif()
aps_warnings(AudioPlugSharpVst3)
if(MSVC) # the SDK file uses sprintf/strcpy/sscanf (the property applies to every target that compiles it)
    set_source_files_properties("${VST3_ROOT}/pluginterfaces/base/funknown.cpp" PROPERTIES COMPILE_DEFINITIONS _CRT_SECURE_NO_WARNINGS)
endif()

# VST3 test host (the VST3 sibling of ClapTestHost; also opens editors).
add_executable(Vst3TestHost "${APS_BRIDGE_DIR}/Tests/Vst3TestHost/Vst3TestHost.cpp"
                            "${VST3_ROOT}/pluginterfaces/base/funknown.cpp"
                            "${APS_BRIDGE_DIR}/Tests/Common/TestWindow.h")
target_include_directories(Vst3TestHost PRIVATE "${VST3_ROOT}" "${APS_BRIDGE_DIR}/Tests/Common")
target_compile_definitions(Vst3TestHost PRIVATE APS_VST3_ARCH_DIR="${APS_VST3_ARCH_DIR}")
target_link_libraries(Vst3TestHost PRIVATE ${CMAKE_DL_LIBS})
if(WIN32)
    target_link_libraries(Vst3TestHost PRIVATE ole32)
else()
    target_link_libraries(Vst3TestHost PRIVATE X11::X11)
endif()
set_target_properties(Vst3TestHost PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${APS_OUT}")
aps_warnings(Vst3TestHost)

# Installable VST3 bundles: bin/vst3/<Plugin>.vst3/Contents/<arch>/ = the adapter + the plugin + the bridge.
set(_vst3_bundle_commands "")
foreach(_p IN LISTS APS_PLUGINS)
    set(_dir "${APS_BIN_DIR}/vst3/${_p}.vst3/Contents/${APS_VST3_ARCH_DIR}")
    list(APPEND _vst3_bundle_commands
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_dir}"
        COMMAND "${CMAKE_COMMAND}" -E copy_directory "${APS_BIN_DIR}/bridge" "${_dir}"
        COMMAND "${CMAKE_COMMAND}" -E copy_directory "${APS_BIN_DIR}/plugins/${_p}" "${_dir}"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:AudioPlugSharpVst3>"
                "${_dir}/${_p}${APS_VST3_SUFFIX}")
endforeach()
add_custom_target(vst3_bundles ALL ${_vst3_bundle_commands}
    DEPENDS AudioPlugSharpVst3 managed
    COMMENT "Assembling VST3 bundles in bin/vst3"
    VERBATIM)
