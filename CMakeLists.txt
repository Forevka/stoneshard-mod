cmake_minimum_required(VERSION 3.21)

# Static MSVC runtime so the DLL has no VC++ redist dependency inside the game process.
cmake_policy(SET CMP0091 NEW)

project(stoneshard_mod LANGUAGES C CXX)   # C is needed for MinHook

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Where the built version.dll gets deployed by the explicit `deploy` target.
# Override on the command line for your own install:
#     cmake -B build -DSTONESHARD_DIR="C:/path/to/Stoneshard"
# or set the STONESHARD_DIR environment variable before configuring.
if(DEFINED ENV{STONESHARD_DIR})
    set(_ss_default "$ENV{STONESHARD_DIR}")
else()
    set(_ss_default "C:/Program Files (x86)/Steam/steamapps/common/Stoneshard")
endif()
set(STONESHARD_DIR "${_ss_default}" CACHE PATH "Stoneshard install directory")

# ---------------------------------------------------------------- dependencies
include(FetchContent)
set(FETCHCONTENT_BASE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/_deps")

# Dear ImGui ships no CMakeLists, so MakeAvailable just populates the source tree.
FetchContent_Declare(imgui
    GIT_REPOSITORY https://github.com/ocornut/imgui.git
    GIT_TAG        v1.91.5
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(imgui)

# MinHook ships only MSVC project files, no CMakeLists, so MakeAvailable just
# populates the tree and we compile its handful of C sources ourselves.
FetchContent_Declare(minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG        v1.3.3
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(minhook)


# zlib. Save files are zlib(<json><md5><NUL>), and the md5 is salted with the
# save's own FOLDER PATH - so a save copied into a different character slot
# fails its own checksum. Re-signing an imported save therefore means
# decompressing it, recomputing the hash against the new path, and
# recompressing. MD5 itself comes from bcrypt, which ships with Windows.
FetchContent_Declare(zlib
    GIT_REPOSITORY https://github.com/madler/zlib.git
    GIT_TAG        v1.3.1
    GIT_SHALLOW    TRUE
)
set(ZLIB_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(zlib)

set(MINHOOK_SOURCES
    ${minhook_SOURCE_DIR}/src/buffer.c
    ${minhook_SOURCE_DIR}/src/hook.c
    ${minhook_SOURCE_DIR}/src/trampoline.c
    ${minhook_SOURCE_DIR}/src/hde/hde32.c
    ${minhook_SOURCE_DIR}/src/hde/hde64.c
)

set(IMGUI_SOURCES
    ${imgui_SOURCE_DIR}/imgui.cpp
    ${imgui_SOURCE_DIR}/imgui_draw.cpp
    ${imgui_SOURCE_DIR}/imgui_tables.cpp
    ${imgui_SOURCE_DIR}/imgui_widgets.cpp
    ${imgui_SOURCE_DIR}/backends/imgui_impl_win32.cpp
    ${imgui_SOURCE_DIR}/backends/imgui_impl_dx11.cpp
)

# ---------------------------------------------------------------------- target
add_library(stoneshard_mod SHARED
    src/dllmain.cpp
    src/proxy.cpp
    src/hooks.cpp
    src/overlay.cpp
    src/log.cpp
    src/paths.cpp
    src/symbols.cpp
    src/gml.cpp
    src/builtins.cpp
    src/console.cpp
    src/cheats.cpp
    src/assets.cpp
    src/items.cpp
    src/potions.cpp
    src/character.cpp
    src/body.cpp
    src/gamespeed.cpp
    src/rewrite.cpp
    src/loot.cpp
    src/enemies.cpp
    src/savemigrate.cpp
    src/tracer.cpp
    src/remote.cpp
    src/inspector.cpp
    src/savebackup.cpp
    src/hookengine.cpp
    src/host/core_api.cpp
    src/host/dotnet_host.cpp
    ${IMGUI_SOURCES}
    ${MINHOOK_SOURCES}
)

target_include_directories(stoneshard_mod PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${imgui_SOURCE_DIR}
    ${imgui_SOURCE_DIR}/backends
    ${minhook_SOURCE_DIR}/include
    ${zlib_SOURCE_DIR}
    ${zlib_BINARY_DIR}
)

target_link_libraries(stoneshard_mod PRIVATE d3d11 dxgi bcrypt ole32 shell32 zlibstatic)

# Log + imgui.ini live here, NEVER in the game folder.
target_compile_definitions(stoneshard_mod PRIVATE
    MOD_DATA_DIR="${CMAKE_CURRENT_SOURCE_DIR}"
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

# The .def file maps the real version.dll export names onto our Proxy_* functions,
# which sidesteps the dllimport/dllexport redefinition conflict with the SDK headers.
target_link_options(stoneshard_mod PRIVATE
    "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/src/version.def"
)

# Must be named exactly version.dll to be picked up ahead of the system one.
set_target_properties(stoneshard_mod PROPERTIES
    OUTPUT_NAME "version"
    PREFIX ""
)

# ---------------------------------------------------------------------- deploy
# Deliberately a separate target, not a post-build step: the game folder is only
# ever touched when you explicitly ask for it.
add_custom_target(deploy
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:stoneshard_mod>
            "${STONESHARD_DIR}/version.dll"
    DEPENDS stoneshard_mod
    COMMENT "Deploying version.dll -> ${STONESHARD_DIR}"
    VERBATIM
)
# `deploy` fails while the game is running: Windows will not overwrite a loaded
# DLL. `deploy-live` goes through tools/deploy.ps1, which moves the live file
# aside instead - allowed even when locked - so a build can be staged at any
# time and picked up on the next launch.
add_custom_target(deploy-live
    COMMAND powershell -NoProfile -ExecutionPolicy Bypass
            -File "${CMAKE_CURRENT_SOURCE_DIR}/tools/deploy.ps1"
            -Source "$<TARGET_FILE:stoneshard_mod>"
            -GameDir "${STONESHARD_DIR}"
    DEPENDS stoneshard_mod
    COMMENT "Staging version.dll -> ${STONESHARD_DIR} (safe while the game runs)"
    VERBATIM
)
