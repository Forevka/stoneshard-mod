// .NET hosting through hostfxr, the same component `dotnet app.dll` uses.
//
// hostfxr is located by hand rather than through nethost.lib: nethost is one
// more static library to match against our static CRT, and the lookup it does
// is three well-documented steps (DOTNET_ROOT, the registry, Program Files).
// An app-local runtime under CoreLoader\dotnet wins over all of them, so a
// release can ship a private runtime and never depend on what the player has.

#include "host/dotnet_host.h"
#include "host/core_api.h"

#include "log.h"

#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <filesystem>
#include <string>
#include <vector>

#include "imgui.h"

namespace mod::host {
namespace {

namespace fs = std::filesystem;

// ---- the handful of hostfxr / coreclr_delegates declarations we need -------
using char_t = wchar_t;
using hostfxr_handle = void*;

struct hostfxr_initialize_parameters {
    std::size_t   size;
    const char_t* host_path;
    const char_t* dotnet_root;
};

enum hostfxr_delegate_type {
    hdt_com_activation,
    hdt_load_in_memory_assembly,
    hdt_winrt_activation,
    hdt_com_register,
    hdt_com_unregister,
    hdt_load_assembly_and_get_function_pointer,
    hdt_get_function_pointer,
};

using hostfxr_initialize_for_runtime_config_fn =
    std::int32_t(__cdecl*)(const char_t*, const hostfxr_initialize_parameters*, hostfxr_handle*);
using hostfxr_get_runtime_delegate_fn =
    std::int32_t(__cdecl*)(const hostfxr_handle, hostfxr_delegate_type, void**);
using hostfxr_close_fn = std::int32_t(__cdecl*)(const hostfxr_handle);
using hostfxr_error_writer_fn = void(__cdecl*)(const char_t*);
using hostfxr_set_error_writer_fn = hostfxr_error_writer_fn(__cdecl*)(hostfxr_error_writer_fn);

using load_assembly_and_get_function_pointer_fn =
    int(__stdcall*)(const char_t* assembly_path, const char_t* type_name,
                    const char_t* method_name, const char_t* delegate_type_name,
                    void* reserved, void** delegate);

const char_t* const kUnmanagedCallersOnly = reinterpret_cast<const char_t*>(-1);

using ManagedInitFn = std::int32_t(__stdcall*)(const CoreApi*, ManagedExports*);

// -----------------------------------------------------------------------------

// Start() runs on the init thread while Present is already firing on the game
// thread, so publication is explicit: g_exports is filled first, then g_running
// is released; readers acquire g_running before touching g_exports.
ManagedExports    g_exports{};
std::atomic<bool> g_running{false};

std::mutex  g_statusLock;
std::string g_status = "not started";

void SetStatus(std::string s) {
    std::lock_guard<std::mutex> lock(g_statusLock);
    g_status = std::move(s);
}

std::string GetStatus() {
    std::lock_guard<std::mutex> lock(g_statusLock);
    return g_status;
}

std::string Narrow(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                      nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<std::size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), s.data(), n,
                        nullptr, nullptr);
    return s;
}

fs::path Widen(const char* utf8) {
    const int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    std::wstring w(static_cast<std::size_t>(n > 0 ? n - 1 : 0), L'\0');
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w.data(), n);
    return fs::path(w);
}

void Fail(const std::string& why) {
    SetStatus(why);
    Logf("[!] host: %s", why.c_str());
}

void __cdecl ErrorWriter(const char_t* message) {
    Logf("[!] host: hostfxr: %s", Narrow(message ? message : L"").c_str());
}

// "10.0.10" > "9.0.20" > "10.0.0-rc.2..." - numeric fields first, and a
// pre-release sorts below its release.
std::vector<int> VersionKey(const std::wstring& name) {
    std::vector<int> key;
    std::size_t i = 0;
    while (i < name.size() && key.size() < 3) {
        int v = 0;
        bool any = false;
        while (i < name.size() && iswdigit(name[i])) { v = v * 10 + (name[i] - L'0'); ++i; any = true; }
        if (!any) break;
        key.push_back(v);
        if (i < name.size() && name[i] == L'.') ++i; else break;
    }
    key.resize(3, 0);
    key.push_back(name.find(L'-') == std::wstring::npos ? 1 : 0);
    return key;
}

fs::path NewestFxr(const fs::path& dotnetRoot) {
    std::error_code ec;
    const fs::path fxrDir = dotnetRoot / L"host" / L"fxr";
    if (!fs::is_directory(fxrDir, ec)) return {};

    fs::path best;
    std::vector<int> bestKey;
    for (const auto& e : fs::directory_iterator(fxrDir, ec)) {
        if (!e.is_directory()) continue;
        const fs::path dll = e.path() / L"hostfxr.dll";
        if (!fs::exists(dll, ec)) continue;
        auto key = VersionKey(e.path().filename().wstring());
        if (best.empty() || key > bestKey) { best = dll; bestKey = std::move(key); }
    }
    return best;
}

fs::path RegistryDotnetRoot() {
    wchar_t buf[MAX_PATH];
    DWORD   size = sizeof(buf);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64",
                     L"InstallLocation", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6432KEY, nullptr, buf,
                     &size) == ERROR_SUCCESS)
        return fs::path(buf);
    return {};
}

// Empty when unset. Sized from the first call: a value longer than any fixed
// buffer is reported by length only, with the buffer left unwritten.
fs::path EnvPath(const wchar_t* name) {
    const DWORD need = GetEnvironmentVariableW(name, nullptr, 0);
    if (need == 0) return {};
    std::wstring value(need, L'\0');
    const DWORD got = GetEnvironmentVariableW(name, value.data(), need);
    if (got == 0 || got >= need) return {};
    value.resize(got);
    return fs::path(value);
}

// Returns the hostfxr to load and, for an app-local runtime, the root to pass.
bool LocateHostfxr(const fs::path& loaderDir, fs::path& fxr, fs::path& appLocalRoot) {
    const fs::path local = loaderDir / L"dotnet";
    if (fxr = NewestFxr(local); !fxr.empty()) { appLocalRoot = local; return true; }

    std::vector<fs::path> roots;
    if (auto r = EnvPath(L"DOTNET_ROOT"); !r.empty()) roots.push_back(r);
    if (auto r = RegistryDotnetRoot(); !r.empty()) roots.push_back(r);
    if (auto r = EnvPath(L"ProgramFiles"); !r.empty()) roots.push_back(r / L"dotnet");

    for (const auto& root : roots)
        if (fxr = NewestFxr(root); !fxr.empty()) return true;
    return false;
}

} // namespace

bool Start() {
    const CoreApi* api = Api();
    const fs::path loaderDir = Widen(api->loader_dir());
    const fs::path assembly  = loaderDir / L"CoreLoader.dll";
    const fs::path config    = loaderDir / L"CoreLoader.runtimeconfig.json";

    std::error_code ec;
    if (!fs::exists(assembly, ec) || !fs::exists(config, ec)) {
        Fail("CoreLoader.dll / CoreLoader.runtimeconfig.json not found in " + Narrow(loaderDir.wstring()) +
             " - C# mods disabled");
        return false;
    }

    fs::path fxrPath, appLocalRoot;
    if (!LocateHostfxr(loaderDir, fxrPath, appLocalRoot)) {
        Fail("no .NET runtime found (install the .NET 10 runtime) - C# mods disabled");
        return false;
    }
    Logf("host: hostfxr %s%s", Narrow(fxrPath.wstring()).c_str(),
         appLocalRoot.empty() ? "" : " (app-local)");

    HMODULE fxr = LoadLibraryW(fxrPath.c_str());
    if (!fxr) { Fail("could not load hostfxr.dll"); return false; }

    auto init  = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config"));
    auto getDel = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(fxr, "hostfxr_get_runtime_delegate"));
    auto close = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(fxr, "hostfxr_close"));
    auto setErr = reinterpret_cast<hostfxr_set_error_writer_fn>(
        GetProcAddress(fxr, "hostfxr_set_error_writer"));
    if (!init || !getDel || !close) { Fail("hostfxr.dll is missing expected exports"); return false; }
    if (setErr) setErr(&ErrorWriter);

    hostfxr_initialize_parameters params{sizeof(params), nullptr, nullptr};
    const std::wstring rootStr = appLocalRoot.wstring();
    if (!appLocalRoot.empty()) params.dotnet_root = rootStr.c_str();

    hostfxr_handle ctx = nullptr;
    const std::int32_t rc = init(config.c_str(), &params, &ctx);
    // 1 and 2 are success codes: the runtime was already up / config differs.
    if (rc < 0 || rc > 2 || !ctx) {
        char buf[96];
        std::snprintf(buf, sizeof(buf), "runtime initialisation failed (0x%08X)", static_cast<unsigned>(rc));
        Fail(buf);
        if (ctx) close(ctx);
        return false;
    }

    void* loadFnRaw = nullptr;
    if (getDel(ctx, hdt_load_assembly_and_get_function_pointer, &loadFnRaw) != 0 || !loadFnRaw) {
        Fail("hostfxr did not provide load_assembly_and_get_function_pointer");
        close(ctx);
        return false;
    }
    close(ctx);   // the runtime stays loaded; only the init handle is released
    auto loadFn = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(loadFnRaw);

    void* initRaw = nullptr;
    const int lrc = loadFn(assembly.c_str(), L"CoreLoader.Runtime.Entry, CoreLoader", L"Init",
                           kUnmanagedCallersOnly, nullptr, &initRaw);
    if (lrc != 0 || !initRaw) {
        char buf[96];
        std::snprintf(buf, sizeof(buf), "could not bind CoreLoader.Runtime.Entry.Init (0x%08X)",
                      static_cast<unsigned>(lrc));
        Fail(buf);
        return false;
    }

    g_exports      = ManagedExports{};
    g_exports.size = sizeof(ManagedExports);
    const std::int32_t ok = reinterpret_cast<ManagedInitFn>(initRaw)(api, &g_exports);
    if (!ok || !g_exports.frame || !g_exports.gui || !g_exports.shutdown) {
        Fail("CoreLoader.Runtime.Entry.Init reported failure (see managed log lines above)");
        return false;
    }

    SetStatus("running");
    g_running.store(true, std::memory_order_release);
    Logf("host: managed runtime ready");
    return true;
}

// No SEH around these calls on purpose. Every managed entry point catches all
// exceptions itself, and GML calls are guarded natively where they happen;
// unwinding managed frames behind the CLR's back from out here would corrupt
// the thread's runtime state rather than contain anything.

void Frame() {
    if (g_running.load(std::memory_order_acquire)) g_exports.frame();
}

void DrawModsTab() {
    if (!g_running.load(std::memory_order_acquire)) {
        ImGui::TextColored(ImVec4(1.0f, 0.45f, 0.45f, 1.0f), "C# mods are not running.");
        const std::string status = GetStatus();
        ImGui::TextWrapped("%s", status.c_str());
        return;
    }
    g_exports.gui();
}

void Shutdown() {
    if (!g_running.exchange(false, std::memory_order_acq_rel)) return;
    g_exports.shutdown();
}

bool        Running() { return g_running.load(std::memory_order_acquire); }
std::string Status()  { return GetStatus(); }

} // namespace mod::host
