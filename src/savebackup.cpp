#include "savebackup.h"
#include "log.h"

#include <windows.h>
#include <shlobj.h>
#include <filesystem>
#include <string>

namespace mod::backup {
namespace {

bool        g_done = false;
std::string g_result = "not run";

std::filesystem::path SaveDir() {
    wchar_t* local = nullptr;
    std::filesystem::path p;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)) && local) {
        p = std::filesystem::path(local) / L"StoneShard" / L"characters_v1";
        CoTaskMemFree(local);
    }
    return p;
}

std::string Timestamp() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%04u%02u%02u-%02u%02u%02u",
                  st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

} // namespace

bool        Done()       { return g_done; }
const char* LastResult() { return g_result.c_str(); }

void EnsureBackupOnce() {
    if (g_done) return;
    g_done = true;   // one attempt per session either way

    std::error_code ec;
    const auto src = SaveDir();
    if (src.empty() || !std::filesystem::exists(src, ec)) {
        g_result = "no save directory found - nothing backed up";
        Logf("backup: %s", g_result.c_str());
        return;
    }

    const auto dst = std::filesystem::path(MOD_DATA_DIR) / "save-backups" / Timestamp();
    std::filesystem::create_directories(dst, ec);
    std::filesystem::copy(src, dst,
                          std::filesystem::copy_options::recursive |
                          std::filesystem::copy_options::overwrite_existing,
                          ec);

    if (ec) {
        g_result = "backup FAILED: " + ec.message();
        Logf("[!] backup: %s", g_result.c_str());
    } else {
        g_result = "saves backed up to " + dst.string();
        Logf("backup: %s", g_result.c_str());
    }
}

} // namespace mod::backup
