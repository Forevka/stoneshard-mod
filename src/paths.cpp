#include "paths.h"

#include <windows.h>
#include <filesystem>
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
            GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCSTR>(&Resolve), &self);
            char path[MAX_PATH * 4];
            const DWORD len = GetModuleFileNameA(self, path, sizeof(path));
            if (len > 0 && len < sizeof(path)) {
                const auto loader = std::filesystem::path(path).parent_path() / "CoreLoader";
                std::error_code ec;
                if (std::filesystem::is_directory(loader, ec)) g_dir = (loader / "Logs").string();
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
