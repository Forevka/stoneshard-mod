#include "paths.h"

#include <windows.h>
#include <filesystem>
#include <iterator>
#include <mutex>

namespace mod::paths {
namespace {

std::string    g_dir;
bool           g_overridden = false;
std::once_flag g_once;

// Called from the init thread (via LogInit) and later from the game thread
// (via remote::Poll), so the resolution itself has to be safe to race.
void Resolve() {
    std::call_once(g_once, [] {
        char        buf[MAX_PATH * 4];
        const DWORD n = GetEnvironmentVariableA("SSMOD_DATA_DIR", buf, sizeof(buf));
        if (n > 0 && n < sizeof(buf)) {
            g_dir        = buf;
            g_overridden = true;
            while (!g_dir.empty() && (g_dir.back() == '\\' || g_dir.back() == '/'))
                g_dir.pop_back();
        }
        // An installed CoreLoader (a CoreLoader\ folder next to this dll) keeps
        // everything it writes under CoreLoader\Logs - contained, and on the
        // player's own machine rather than at a path compiled in on ours.
        if (g_dir.empty()) {
            HMODULE self = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&Resolve), &self);
            // Wide on purpose: an install under a folder name the ANSI code
            // page cannot represent (Cyrillic, CJK) must still be found.
            wchar_t path[MAX_PATH * 4];
            const DWORD len = GetModuleFileNameW(self, path, static_cast<DWORD>(std::size(path)));
            if (len > 0 && len < std::size(path)) {
                const auto loader = std::filesystem::path(path).parent_path() / L"CoreLoader";
                std::error_code ec;
                if (std::filesystem::is_directory(loader, ec)) {
                    // The rest of the mod opens files through narrow paths
                    // (fopen). A path with characters outside the ANSI code
                    // page cannot be narrowed faithfully, so the folder is
                    // created first and its 8.3 short form - plain ASCII - is
                    // what gets used.
                    const auto logs = loader / L"Logs";
                    std::filesystem::create_directories(logs, ec);
                    wchar_t shortPath[MAX_PATH * 4];
                    DWORD n = GetShortPathNameW(logs.c_str(), shortPath, static_cast<DWORD>(std::size(shortPath)));
                    const wchar_t* use = (n > 0 && n < std::size(shortPath)) ? shortPath : logs.c_str();
                    char narrow[MAX_PATH * 4];
                    BOOL lossy = FALSE;
                    const int m = WideCharToMultiByte(CP_ACP, 0, use, -1, narrow, sizeof(narrow), nullptr, &lossy);
                    if (m > 0 && !lossy) g_dir = narrow;
                }
            }
        }
        // A development build run straight from the build tree.
        if (g_dir.empty()) g_dir = MOD_DATA_DIR;

        std::error_code ec;
        std::filesystem::create_directories(g_dir, ec);   // failure surfaces later, as a
                                                          // failed fopen; nothing can log yet
    });
}

} // namespace

const std::string& DataDir() { Resolve(); return g_dir; }

std::string File(const char* name) { return DataDir() + "\\" + name; }

bool Overridden() { Resolve(); return g_overridden; }

} // namespace mod::paths
