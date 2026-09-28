// ImGui lifecycle, the Hello World window, and the window-procedure hook.
//
// The WndProc hook lives here rather than in hooks.cpp because it is tightly
// coupled to the ImGui context it feeds.

#include "overlay.h"
#include "cheats.h"
#include "console.h"
#include "gml.h"
#include "builtins.h"
#include "inspector.h"
#include "tracer.h"
#include "remote.h"
#include "gamespeed.h"
#include "loot.h"
#include "enemies.h"
#include "rewrite.h"
#include "savemigrate.h"
#include "log.h"
#include "paths.h"
#include "symbols.h"
#include "host/dotnet_host.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <string>
#include <vector>

#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(
    HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace mod {
namespace {

bool                  g_initialised = false;
bool                  g_initFailed  = false;
bool                  g_visible     = true;
unsigned long long    g_frameCount  = 0;

ID3D11Device*         g_device  = nullptr;
ID3D11DeviceContext*  g_context = nullptr;
ID3D11RenderTargetView* g_rtv   = nullptr;
HWND                  g_hwnd    = nullptr;
WNDPROC               g_prevWndProc = nullptr;

UINT g_width  = 0;
UINT g_height = 0;

LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // The trace lines up keystrokes with what the game ran in response.
    if (msg == WM_KEYDOWN) tracer::NoteKey(static_cast<int>(wParam));

    // The last point where mods can still run on the game thread; DllMain's
    // detach is under the loader lock, where calling into .NET is unsafe.
    if (msg == WM_DESTROY) host::Shutdown();

    if (msg == WM_KEYDOWN && wParam == VK_INSERT) {
        g_visible = !g_visible;
        Logf("overlay %s", g_visible ? "shown" : "hidden");
        return 0;   // swallow, so the game never sees the toggle key
    }

    if (g_visible && g_initialised) {
        ImGui_ImplWin32_WndProcHandler(hwnd, msg, wParam, lParam);

        const ImGuiIO& io = ImGui::GetIO();
        if (io.WantCaptureMouse && msg >= WM_MOUSEFIRST && msg <= WM_MOUSELAST)
            return 1;
        if (io.WantCaptureKeyboard &&
            ((msg >= WM_KEYFIRST && msg <= WM_KEYLAST) || msg == WM_CHAR))
            return 1;
    }

    return CallWindowProcW(g_prevWndProc, hwnd, msg, wParam, lParam);
}

void CreateRenderTarget(IDXGISwapChain* swapChain) {
    ID3D11Texture2D* backBuffer = nullptr;
    if (SUCCEEDED(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) && backBuffer) {
        g_device->CreateRenderTargetView(backBuffer, nullptr, &g_rtv);
        D3D11_TEXTURE2D_DESC td{};
        backBuffer->GetDesc(&td);
        g_width  = td.Width;
        g_height = td.Height;
        backBuffer->Release();
    }
}

bool EnsureInitialised(IDXGISwapChain* swapChain) {
    if (g_initialised) return true;
    if (g_initFailed)  return false;

    if (FAILED(swapChain->GetDevice(IID_PPV_ARGS(&g_device))) || !g_device) {
        Logf("[!] GetDevice failed; overlay disabled");
        g_initFailed = true;
        return false;
    }
    g_device->GetImmediateContext(&g_context);

    DXGI_SWAP_CHAIN_DESC desc{};
    swapChain->GetDesc(&desc);
    g_hwnd = desc.OutputWindow;
    Logf("swapchain device=%p context=%p hwnd=%p", g_device, g_context, g_hwnd);

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();

    // Keep imgui.ini in the mod's data dir, NOT the game folder - and one dir
    // per process, so two instances do not fight over one layout file.
    static std::string iniPath = paths::File("imgui.ini");
    io.IniFilename = iniPath.c_str();

    ImGui::StyleColorsDark();

    if (!ImGui_ImplWin32_Init(g_hwnd) || !ImGui_ImplDX11_Init(g_device, g_context)) {
        Logf("[!] ImGui backend init failed; overlay disabled");
        g_initFailed = true;
        return false;
    }

    g_prevWndProc = reinterpret_cast<WNDPROC>(
        SetWindowLongPtrW(g_hwnd, GWLP_WNDPROC,
                          reinterpret_cast<LONG_PTR>(HookedWndProc)));
    Logf("wndproc hooked (original=%p)", g_prevWndProc);

    g_initialised = true;
    Logf("=== overlay ready ===");
    return true;
}

void DrawStatusTab() {
    // These can only advance if our Present hook is really running.
    ImGui::Text("Frames through our hook : %llu", g_frameCount);
    ImGui::Text("Backbuffer              : %u x %u", g_width, g_height);
    ImGui::Text("Overlay framerate       : %.1f FPS", ImGui::GetIO().Framerate);
    ImGui::Text("Host                    : StoneShard.exe (GameMaker YYC, D3D11)");

    ImGui::Separator();

    if (sym::Healthy()) {
        ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                           "Symbol resolver OK - %zu functions, %zu console commands",
                           sym::Count(), sym::ConsoleCommands().size());
    } else {
        ImGui::TextColored(ImVec4(0.95f, 0.35f, 0.35f, 1.0f),
                           "Symbol resolver FAILED: %s", sym::HealthMessage());
        ImGui::TextWrapped("Execution features are disabled. This usually means a game "
                           "update changed the YYC table layout.");
    }

    if (gml::Ready()) {
        if (gml::AbiProven())
            ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                               "GML bridge OK - ABI proven by live call");
        else
            ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                               "GML bridge resolved - ABI self-test pending/failed");
    } else {
        ImGui::TextColored(ImVec4(0.95f, 0.35f, 0.35f, 1.0f),
                           "GML bridge unavailable: %s", gml::Status());
    }

    // Gear giving needs a spawn context. The player tracker supplies one after a
    // little movement; a recorded game call is the fallback. Worth showing, since
    // otherwise a greyed-out button looks broken.
    {
        double px = 0.0, py = 0.0;
        const bool ok = gml::PlayerPosition(px, py) || gml::WeaponRecord().valid;
        if (ok)
            ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                               "Weapon spawn context OK - gear giving enabled");
        else
            ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                               "Locating player - load a save to enable gear giving");
    }

    {
        // The builtin registry is what makes instance fields readable by name.
        if (builtins::Ready()) {
            ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                               "Builtin registry OK - %zu functions (reflection available)",
                               builtins::Count());
            ImGui::TextDisabled("self-test: %s", builtins::SelfTestReport());
        } else {
            ImGui::TextDisabled("Builtin registry: %s", builtins::Status());
        }
    }

    ImGui::Separator();
    ImGui::TextDisabled("INSERT toggles this overlay");

    if (ImGui::CollapsingHeader("Log")) {
        ImGui::BeginChild("##log", ImVec2(0.0f, 200.0f), true,
                          ImGuiWindowFlags_HorizontalScrollbar);
        for (const std::string& line : LogSnapshot())
            ImGui::TextUnformatted(line.c_str());
        if (ImGui::GetScrollY() >= ImGui::GetScrollMaxY() - 1.0f)
            ImGui::SetScrollHereY(1.0f);
        ImGui::EndChild();
    }
}

void DrawSymbolsTab() {
    static char                     query[128] = "scr_console_";
    static std::string              lastQuery;
    static std::vector<const sym::Entry*> results;
    static bool                     primed = false;

    ImGui::TextDisabled("%zu functions resolved from the live image, by name.",
                        sym::Count());
    ImGui::SetNextItemWidth(-1.0f);
    ImGui::InputTextWithHint("##symsearch", "filter symbols...", query, sizeof(query));

    // Re-filter only when the query changes; scanning 34k names every frame
    // would cost more than the rest of the overlay combined.
    if (!primed || lastQuery != query) {
        results   = sym::Search(query, 2000);
        lastQuery = query;
        primed    = true;
    }

    ImGui::Text("%zu match%s%s", results.size(), results.size() == 1 ? "" : "es",
                results.size() >= 2000 ? " (capped)" : "");

    ImGui::BeginChild("##symlist", ImVec2(0.0f, 320.0f), true,
                      ImGuiWindowFlags_HorizontalScrollbar);
    ImGuiListClipper clipper;
    clipper.Begin(static_cast<int>(results.size()));
    while (clipper.Step()) {
        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; ++i) {
            const sym::Entry* e = results[static_cast<std::size_t>(i)];
            ImGui::Text("%p", e->func);
            ImGui::SameLine();
            ImGui::TextUnformatted(e->name);
        }
    }
    ImGui::EndChild();
}

void DrawTracerPanel() {
    ImGui::TextWrapped(
        "Records which game functions run, by detouring the single helper that every "
        "compiled script calls on entry - about 9,260 functions covered by one hook. The "
        "hook is only installed while recording, so this costs nothing when idle.");

    if (!tracer::Ready()) {
        ImGui::TextColored(ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "Tracer unavailable: %s", tracer::Status());
        return;
    }
    ImGui::TextDisabled("helper resolved at %p", tracer::HelperAddress());
    ImGui::Separator();

    static int  duration = 3000;
    static bool stacks   = false;
    static char filter[64] = "";

    ImGui::SetNextItemWidth(160.0f);
    ImGui::InputInt("duration (ms)", &duration, 500, 1000);
    if (duration < 200)   duration = 200;
    if (duration > 30000) duration = 30000;

    ImGui::SetNextItemWidth(260.0f);
    ImGui::InputTextWithHint("filter", "substring, empty = everything", filter, sizeof(filter));

    ImGui::Checkbox("capture call stacks", &stacks);
    ImGui::SameLine();
    ImGui::TextDisabled("much slower per call - leave off unless needed");

    ImGui::Spacing();
    if (!tracer::Recording()) {
        if (ImGui::Button("Start recording", ImVec2(200.0f, 0.0f))) {
            tracer::Options o;
            o.durationMs = duration;
            o.stacks     = stacks;
            std::snprintf(o.filter, sizeof(o.filter), "%s", filter);
            tracer::StartRecording(o);
        }
    } else {
        ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(0.45f, 0.90f, 0.45f, 1.0f));
        ImGui::Text("RECORDING - %d ms left", tracer::RemainingMs());
        ImGui::PopStyleColor();
        if (ImGui::Button("Stop now", ImVec2(200.0f, 0.0f))) tracer::StopRecording();
    }

    ImGui::Spacing();
    ImGui::Text("records: %llu    dropped: %llu", tracer::Recorded(), tracer::Dropped());
    if (!tracer::LastFile().empty())
        ImGui::TextWrapped("file: %s", tracer::LastFile().c_str());
    if (tracer::Dropped())
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                           "Ring buffer overflowed - narrow the filter or shorten the burst.");
}

void DrawBreakpointPanel() {
    static char symbol[128] = "scr_dialogue_reward_add_item";
    static bool skip        = false;
    static int  limit       = 20;

    ImGui::TextWrapped(
        "Traps a function and reports its arguments, instance context and a resolved call "
        "stack. It does NOT halt the game: the overlay renders on the game's own thread, so "
        "blocking here would freeze the UI too - a hang rather than a pause.");

    ImGui::SetNextItemWidth(340.0f);
    ImGui::InputText("symbol", symbol, sizeof(symbol));
    ImGui::SetNextItemWidth(120.0f);
    ImGui::InputInt("hit limit", &limit);
    if (limit < 1) limit = 1;

    ImGui::Checkbox("skip the original call", &skip);
    ImGui::SameLine();
    ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                       "suppresses real behaviour - can corrupt state");

    const auto& bp = tracer::Breakpoint();
    if (!bp.active) {
        if (ImGui::Button("Arm breakpoint", ImVec2(200.0f, 0.0f)))
            tracer::SetBreakpoint(symbol, skip, limit);
    } else {
        ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                           "ARMED on %s - %u hit(s)", bp.symbol.c_str(), bp.hits);
        if (ImGui::Button("Clear", ImVec2(200.0f, 0.0f))) tracer::ClearBreakpoint();
    }

    if (!bp.lastReport.empty()) {
        ImGui::Spacing();
        ImGui::SeparatorText("Last hit");
        ImGui::BeginChild("##bpout", ImVec2(0.0f, 220.0f), true,
                          ImGuiWindowFlags_HorizontalScrollbar);
        ImGui::TextUnformatted(bp.lastReport.c_str());
        ImGui::EndChild();
    }
}

void DrawDebugTab() {
    if (ImGui::BeginTabBar("##debugtabs")) {
        if (ImGui::BeginTabItem("Tracer"))    { DrawTracerPanel();               ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Inspector")) { inspector::DrawInspectorTab();   ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Breakpoints")) { DrawBreakpointPanel();     ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Rewrite"))   { rewrite::DrawRewriteTab();   ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Symbols"))   { DrawSymbolsTab();            ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Status"))    { DrawStatusTab();             ImGui::EndTabItem(); }
        ImGui::EndTabBar();
    }
}

void DrawUI() {
    ImGui::SetNextWindowSize(ImVec2(660.0f, 480.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowPos(ImVec2(40.0f, 40.0f), ImGuiCond_FirstUseEver);

    ImGui::Begin("CoreLoader");

    // The native Stoneshard tools predate the generic loader and name that
    // game's own scripts and objects; in any other game they would only show
    // errors, so they appear only where they apply. Its console scripts are
    // the fingerprint.
    static const bool stoneshard = sym::Find("gml_Script_scr_console_sethp") != nullptr;

    // Ordered by who wants them: mods first, the game-specific tools next, the
    // tooling that dissects the game last.
    if (ImGui::BeginTabBar("##tabs")) {
        if (ImGui::BeginTabItem("Mods"))    { host::DrawModsTab();          ImGui::EndTabItem(); }
        if (stoneshard) {
            if (ImGui::BeginTabItem("Cheats"))  { cheats::DrawCheatsTab();      ImGui::EndTabItem(); }
            if (ImGui::BeginTabItem("Enemies")) { enemies::DrawEnemiesTab();   ImGui::EndTabItem(); }
            if (ImGui::BeginTabItem("Loot"))    { loot::DrawLootTab();          ImGui::EndTabItem(); }
            if (ImGui::BeginTabItem("Speed"))   { gamespeed::DrawSpeedTab();    ImGui::EndTabItem(); }
            if (ImGui::BeginTabItem("Saves"))   { savemigrate::DrawSavesTab();  ImGui::EndTabItem(); }
            if (ImGui::BeginTabItem("Console")) { console::DrawConsoleTab();    ImGui::EndTabItem(); }
        }
        if (ImGui::BeginTabItem("Debug"))   { DrawDebugTab();               ImGui::EndTabItem(); }
        ImGui::EndTabBar();
    }

    ImGui::End();
}

} // namespace

void OverlayRender(IDXGISwapChain* swapChain) {
    if (!EnsureInitialised(swapChain)) return;

    if (!g_rtv) {
        CreateRenderTarget(swapChain);
        if (!g_rtv) return;
    }

    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();

    ++g_frameCount;

    tracer::Tick();
    // Re-assert the chosen game speed if the engine has moved it back.
    gamespeed::Tick();
    // Retires tracked enemy instances once their room stops stepping.
    enemies::Tick();

    remote::Poll();

    // We are on the game's render thread here, which is where GML must be
    // called from. Give the game a moment to run its own code first so the
    // borrowed `self` instance is populated.
    if (g_frameCount > 120) {
        gml::AbiSelfTest();
        // Phase A runs as soon as the registry resolves; phase B waits for a
        // character, so this keeps being called until one is loaded.
        builtins::SelfTest();
    }

    // Before any mod runs: prove the value free/copy helpers on a probe string
    // (game thread, once). Until then the managed side treats them as absent.
    gml::VerifyValueLifetime();

    // C# mods: initialised on their first frame, then ticked every frame.
    host::Frame();

    // The game hides the OS cursor, so ImGui has to draw its own while visible.
    ImGui::GetIO().MouseDrawCursor = g_visible;

    if (g_visible)
        DrawUI();

    // Render/NewFrame must stay balanced even when nothing is drawn.
    ImGui::Render();

    // Everything that needed a live instance this frame has run; the next
    // frame's events will supply a fresh one (only matters on runtimes without
    // a current-self global, where the observed instance is the only source).
    gml::ClearObservedSelf();

    g_context->OMSetRenderTargets(1, &g_rtv, nullptr);
    // The DX11 backend snapshots and restores the full pipeline state around
    // this call, so GameMaker's own render state is left untouched.
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
}

void OverlayInvalidate() {
    if (g_rtv) {
        g_rtv->Release();
        g_rtv = nullptr;
    }
}

void OverlayShutdown() {
    if (!g_initialised) return;
    g_initialised = false;

    if (g_hwnd && g_prevWndProc) {
        SetWindowLongPtrW(g_hwnd, GWLP_WNDPROC,
                          reinterpret_cast<LONG_PTR>(g_prevWndProc));
        g_prevWndProc = nullptr;
    }

    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();

    OverlayInvalidate();
    if (g_context) { g_context->Release(); g_context = nullptr; }
    if (g_device)  { g_device->Release();  g_device  = nullptr; }

    Logf("overlay shut down");
}

} // namespace mod
