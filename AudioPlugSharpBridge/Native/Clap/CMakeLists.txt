# The CLAP adapter, its test host and the installable CLAP folders. Included from ../CMakeLists.txt, which
# provides aps_native_core, aps_warnings, APS_PLUGINS, APS_BIN_DIR/APS_OUT and APS_BRIDGE_DIR.

# ---------------------------------------------------------------------------
# CLAP. Header-only, pinned to the 1.2.10 release commit.
# ---------------------------------------------------------------------------
FetchContent_Declare(clap
    GIT_REPOSITORY https://github.com/free-audio/clap.git
    GIT_TAG 195b42a004144fab0b3cf95e9c067187d15365b7 # 1.2.10
    SOURCE_SUBDIR do-not-add) # headers only; don't build CLAP's own CMake project
FetchContent_MakeAvailable(clap)

# The generic CLAP adapter: one binary for every AudioPlugSharp plugin. Shipped as <Plugin>.clap next to
# <Plugin>.dll and the bridge files; it finds everything relative to its own path.
add_library(AudioPlugSharpClap MODULE ClapAdapter.cpp)
target_include_directories(AudioPlugSharpClap PRIVATE "${clap_SOURCE_DIR}/include")
target_link_libraries(AudioPlugSharpClap PRIVATE aps_native_core)
set_target_properties(AudioPlugSharpClap PROPERTIES PREFIX "" SUFFIX ".clap" LIBRARY_OUTPUT_DIRECTORY "${APS_OUT}")
if(NOT WIN32 AND NOT APPLE)
    # Export exactly clap_entry: also hides static-library symbols (libnethost.a) and std:: template
    # instances, which libstdc++ gives default visibility.
    target_link_options(AudioPlugSharpClap PRIVATE "LINKER:--exclude-libs,ALL"
        "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
    set_property(TARGET AudioPlugSharpClap APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
endif()
aps_warnings(AudioPlugSharpClap)

# CLAP test host (loads .clap files, exercises the whole lifecycle; the GUI test opens real windows).
add_executable(ClapTestHost "${APS_BRIDGE_DIR}/Tests/ClapTestHost/ClapTestHost.cpp"
                            "${APS_BRIDGE_DIR}/Tests/Common/TestWindow.h")
target_include_directories(ClapTestHost PRIVATE "${clap_SOURCE_DIR}/include" "${APS_BRIDGE_DIR}/Tests/Common")
target_link_libraries(ClapTestHost PRIVATE ${CMAKE_DL_LIBS})
if(UNIX AND NOT APPLE)
    target_link_libraries(ClapTestHost PRIVATE X11::X11) # the GUI test's window
endif()
set_target_properties(ClapTestHost PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${APS_OUT}")
aps_warnings(ClapTestHost)

# Installable CLAP folders: bin/clap/<Plugin>/ = <Plugin>.clap + the plugin + the bridge.
set(_bundle_commands "")
foreach(_p IN LISTS APS_PLUGINS)
    list(APPEND _bundle_commands
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${APS_BIN_DIR}/clap/${_p}"
        COMMAND "${CMAKE_COMMAND}" -E copy_directory "${APS_BIN_DIR}/bridge" "${APS_BIN_DIR}/clap/${_p}"
        COMMAND "${CMAKE_COMMAND}" -E copy_directory "${APS_BIN_DIR}/plugins/${_p}" "${APS_BIN_DIR}/clap/${_p}"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:AudioPlugSharpClap>"
                "${APS_BIN_DIR}/clap/${_p}/${_p}.clap")
endforeach()
add_custom_target(clap_bundles ALL ${_bundle_commands}
    DEPENDS AudioPlugSharpClap managed
    COMMENT "Assembling CLAP plugin folders in bin/clap"
    VERBATIM)
