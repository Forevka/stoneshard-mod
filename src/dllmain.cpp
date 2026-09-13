#include <windows.h>

#include "assets.h"
#include "gml.h"
#include "hooks.h"
#include "log.h"
#include "potions.h"
#include "tracer.h"
#include "symbols.h"

namespace {

DWORD WINAPI InitThread(LPVOID) {
    mod::LogInit();
    mod::Logf("=== Stoneshard PoC mod ===");
    mod::Logf("loaded into pid %lu", GetCurrentProcessId());

    // The exe's .data relocations are applied before imports are resolved, so
    // the YYC registration rows are already valid by the time we get here.
    if (mod::sym::Scan()) {
        // Pure memory reading, so it is safe off the game thread. The first
        // actual GML call happens later, from inside the Present hook.
        mod::gml::Init();
    }

    // Item/object names come from the shipped data file, so the picker keeps
    // working when a patch adds or renames items.
    mod::assets::Load();

    // Our version.dll may be resolved before d3d11/dxgi during the exe's import
    // walk, so wait for them rather than assuming an order. ~10s ceiling.
    bool ready = false;
    for (int i = 0; i < 200; ++i) {
        if (GetModuleHandleW(L"d3d11.dll") && GetModuleHandleW(L"dxgi.dll")) {
            ready = true;
            break;
        }
        Sleep(50);
    }

    if (!ready) {
        mod::Logf("[!] d3d11.dll/dxgi.dll never appeared - aborting init");
        return 0;
    }
    mod::Logf("d3d11.dll and dxgi.dll present");

    if (!mod::InstallHooks())
        mod::Logf("[!] hook installation failed - overlay will not appear");

    // Needs MinHook, which InstallHooks initialises. Recording the game's own
    // weapon spawns is what lets the Items tab hand out gear at all.
    mod::gml::InstallWeaponRecorder();
    mod::gml::InstallPlayerTracker();

    // Same idea for potions: the bottle's own alarm event is where the game
    // hands over a real bottle instance, which is what rolling one needs.
    mod::potions::InstallRecorder();

    // Resolves the shared prologue helper; the hook itself is only installed
    // while a recording is armed.
    mod::tracer::Init();

    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        // Keep the loader lock free: do all real work on our own thread.
        if (HANDLE t = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr))
            CloseHandle(t);
        break;

    case DLL_PROCESS_DETACH:
        mod::RemoveHooks();
        mod::LogShutdown();
        break;

    default:
        break;
    }
    return TRUE;
}
