cmake_minimum_required(VERSION 3.21)

# Static MSVC runtime so the DLL has no VC++ redist dependency inside the game process.
cmake_policy(SET CMP0091 NEW)

project(coreloader LANGUAGES C CXX)   # C is needed for MinHook

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Installing into a game is tools/deploy-coreloader.ps1's job (-Live while the
# game runs); the build never touches a game folder.

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
add_library(coreloader SHARED
    src/dllmain.cpp
    src/proxy.cpp
    src/hooks.cpp
    src/overlay.cpp
    src/log.cpp
    src/paths.cpp
    src/symbols.cpp
    src/gml.cpp
    src/builtins.cpp
    src/hookengine.cpp
    src/objtypes.cpp
    src/host/core_api.cpp
    src/host/dotnet_host.cpp
    ${IMGUI_SOURCES}
    ${MINHOOK_SOURCES}
)

target_include_directories(coreloader PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${imgui_SOURCE_DIR}
    ${imgui_SOURCE_DIR}/backends
    ${minhook_SOURCE_DIR}/include
)

target_link_libraries(coreloader PRIVATE d3d11 dxgi)

# Log + imgui.ini live here, NEVER in the game folder.
target_compile_definitions(coreloader PRIVATE
    MOD_DATA_DIR="${CMAKE_CURRENT_SOURCE_DIR}"
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

# The .def file maps the real version.dll export names onto our Proxy_* functions,
# which sidesteps the dllimport/dllexport redefinition conflict with the SDK headers.
target_link_options(coreloader PRIVATE
    "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/src/version.def"
)

# Must be named exactly version.dll to be picked up ahead of the system one.
set_target_properties(coreloader PROPERTIES
    OUTPUT_NAME "version"
    PREFIX ""
)
