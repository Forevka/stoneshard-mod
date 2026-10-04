// The in-game overlay: ImGui lifecycle on the game's D3D11 swap chain, the
// CoreLoader window (Mods, Symbols and Status tabs), pick mode, the per-frame
// tick that runs the self-tests and the managed runtime, and the
// window-procedure hook.
//
// The WndProc hook lives here rather than in hooks.cpp because it is tightly
// coupled to the ImGui context it feeds.

#include "overlay.h"
#include "gml.h"
#include "builtins.h"
#include "log.h"
#include "objtypes.h"
#include "paths.h"
#include "symbols.h"
#include "host/dotnet_host.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "imgui.h"
#include "imgui_internal.h"
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

// Pick mode: written by the window thread, read by the game thread (the same
// thread in practice, but nothing here relies on it).
std::atomic<bool> g_pickArmed{false};
std::atomic<bool> g_pickReady{false};
std::atomic<UINT> g_pickSwallowUp{0};   // the button-up message still owed to us, or 0
std::atomic<int>  g_pickX{0}, g_pickY{0}, g_pickW{0}, g_pickH{0}, g_pickButton{0};

// True when the message was a pick click and must not reach the game.
bool HandlePick(HWND hwnd, UINT msg, LPARAM lParam) {
    const bool left  = msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK;
    const bool right = msg == WM_RBUTTONDOWN || msg == WM_RBUTTONDBLCLK;
    // Only the release of the very button we swallowed is ours; the mouse was
    // captured on the press, so it arrives here even outside the window.
    if (const UINT owed = g_pickSwallowUp.load(); owed != 0 && msg == owed) {
        g_pickSwallowUp = 0;
        if (GetCapture() == hwnd) ReleaseCapture();
        return true;
    }
    if (!(left || right) || !g_pickArmed.load()) return false;
    if (g_visible && g_initialised && ImGui::GetIO().WantCaptureMouse) return false;   // a click on the overlay

    RECT rc{};
    GetClientRect(hwnd, &rc);
    g_pickX = static_cast<short>(LOWORD(lParam));
    g_pickY = static_cast<short>(HIWORD(lParam));
    g_pickW = rc.right - rc.left;
    g_pickH = rc.bottom - rc.top;
    g_pickButton = right ? 1 : 0;
    g_pickArmed = false;
    g_pickSwallowUp = right ? WM_RBUTTONUP : WM_LBUTTONUP;
    SetCapture(hwnd);
    g_pickReady = true;
    return true;
}

bool IsReleaseMessage(UINT msg) {
    return msg == WM_KEYUP || msg == WM_SYSKEYUP || msg == WM_LBUTTONUP || msg == WM_RBUTTONUP ||
           msg == WM_MBUTTONUP || msg == WM_XBUTTONUP;
}

LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // The last point where mods can still run on the game thread; DllMain's
    // detach is under the loader lock, where calling into .NET is unsafe.
    if (msg == WM_DESTROY) host::Shutdown();

    if (msg == WM_KEYDOWN && wParam == VK_INSERT) {
        // Bit 30: the key was already down - auto-repeat must not flicker it.
        if ((lParam & (1 << 30)) == 0) {
            g_visible = !g_visible;
            Logf("overlay %s", g_visible ? "shown" : "hidden");
        }
        return 0;   // swallow, so the game never sees the toggle key
    }

    if (HandlePick(hwnd, msg, lParam)) return 0;

    if (g_visible && g_initialised) {
        ImGui_ImplWin32_WndProcHandler(hwnd, msg, wParam, lParam);

        // Releases always reach the game: a key or button pressed in the game
        // and let go over the overlay would otherwise stay held down for it.
        if (IsReleaseMessage(msg)) return CallWindowProcW(g_prevWndProc, hwnd, msg, wParam, lParam);

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

// Some games (The King is Watching) throw their swap chain away and build a new
// one, on a new device, after the first frames. Our backend's device and
// context then belong to the dead chain: the ImGui DX11 backend moves to the
// device of the chain being presented now.
bool FollowDevice(IDXGISwapChain* swapChain) {
    ID3D11Device* device = nullptr;
    if (FAILED(swapChain->GetDevice(IID_PPV_ARGS(&device))) || !device) return false;
    if (device == g_device) { device->Release(); return true; }

    Logf("swap chain moved to device %p (was %p); overlay follows", device, g_device);
    OverlayInvalidate();
    ImGui_ImplDX11_Shutdown();
    if (g_context) g_context->Release();
    if (g_device) g_device->Release();
    g_device = device;   // keeps GetDevice's reference
    g_device->GetImmediateContext(&g_context);
    if (!ImGui_ImplDX11_Init(g_device, g_context)) {
        Logf("[!] ImGui DX11 backend re-init failed; overlay disabled");
        g_initFailed = true;
        // No NewFrame runs again, so ImGui's capture flags would stay frozen
        // and the WndProc could keep eating the game's input.
        g_visible = false;
        return false;
    }
    return true;
}

bool EnsureInitialised(IDXGISwapChain* swapChain) {
    if (g_initialised) return !g_initFailed && FollowDevice(swapChain);
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

    // Keep imgui.ini in the loader's data dir, NOT the game folder.
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
    {
        char exe[MAX_PATH] = {};
        GetModuleFileNameA(nullptr, exe, MAX_PATH);
        const char* name = std::strrchr(exe, '\\');
        ImGui::Text("Host                    : %s (GameMaker YYC, D3D11)", name ? name + 1 : exe);
    }

    ImGui::Separator();

    if (sym::Healthy()) {
        ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                           "Symbol resolver OK - %zu functions", sym::Count());
    } else {
        ImGui::TextColored(ImVec4(0.95f, 0.35f, 0.35f, 1.0f),
                           "Symbol resolver FAILED: %s", sym::HealthMessage());
        if (std::strncmp(sym::HealthMessage(), "not a YYC game", 14) != 0)
            ImGui::TextWrapped("Execution features are disabled. This usually means a game "
                               "update changed the YYC table layout.");
    }

    if (gml::Ready()) {
        if (gml::AbiProven())
            ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                               "GML bridge OK - string round-trip proven "
                               "(game calls: see the builtins self-test in the log)");
        else
            ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                               "GML bridge resolved - string round-trip pending/failed");
    } else {
        ImGui::TextColored(ImVec4(0.95f, 0.35f, 0.35f, 1.0f),
                           "GML bridge unavailable: %s", gml::Status());
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
    static char                     query[128] = "";
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

// The overlay window's name. The part after ### is its identity in imgui.ini:
// it was "Lodestone", and an ini written before the placement below could
// hold a position ImGui had pulled to a small startup window's edge (see
// below), so the window starts afresh under a new identity.
constexpr const char* kMainWindow = "Lodestone###LodestoneMain";

// Where the overlay window belongs: its saved place, or wherever the user last
// moved it. ImGui pulls a window back on screen by itself and keeps the pulled
// position, so a game that opens a small window and only then goes fullscreen
// (Stoneshard starts at 970x540) left the overlay stuck at that small window's
// edge. So the place is kept here and the window is put there every frame,
// pulled in only as far as the current display needs. Whenever the user moves
// it (a drag, a resize from the left or top, a keyboard move) ImGui is left
// alone, and the position it ends up at becomes the place.
ImVec2 g_wantedPos{0.0f, 0.0f};
bool g_wantedKnown = false;
// No saved place and never moved: it keeps to the top-right corner, out of the
// way of the HUD most games draw at the top left and the top centre.
bool g_anchorRight = false;
// Where the window was at the end of the last frame, and whether this frame's
// position was set here (and to what).
ImVec2 g_lastEndPos{-1.0f, -1.0f};
bool g_placed = false;
ImVec2 g_placedPos{0.0f, 0.0f};

void AdoptPosition(ImVec2 pos) {
    g_wantedPos = pos;
    g_anchorRight = false;
}

// Whether the user is acting on the window: moving it (also by a child
// region's empty space, which moves the root), resizing it, or keyboard-moving it.
bool UserActsOn(const ImGuiWindow* window) {
    const ImGuiContext& g = *ImGui::GetCurrentContext();
    auto root = [](const ImGuiWindow* w) { return w ? w->RootWindow : nullptr; };
    return root(g.MovingWindow) == window || root(g.ActiveIdWindow) == window ||
           root(g.NavWindowingTarget) == window;
}

void PlaceMainWindow() {
    constexpr float kMargin = 4.0f;
    ImGuiIO& io = ImGui::GetIO();
    g_placed = false;
    if (!g_wantedKnown) {
        g_wantedKnown = true;
        if (const ImGuiWindowSettings* s = ImGui::FindWindowSettingsByID(ImHashStr(kMainWindow)))
            g_wantedPos = ImVec2(static_cast<float>(s->Pos.x), static_cast<float>(s->Pos.y));
        else
            g_anchorRight = true;
    }
    ImGuiWindow* window = ImGui::FindWindowByName(kMainWindow);
    // Moved since the last frame ended (a drag or keyboard move happens before Begin).
    if (window && g_lastEndPos.x >= 0.0f && (window->Pos.x != g_lastEndPos.x || window->Pos.y != g_lastEndPos.y))
        AdoptPosition(window->Pos);
    if (window && UserActsOn(window)) return;
    const ImVec2 size = window ? window->Size : ImVec2(660.0f, 480.0f);
    ImVec2 pos = g_anchorRight ? ImVec2(io.DisplaySize.x - size.x - kMargin, kMargin) : g_wantedPos;
    // As ImGui itself clamps: the window keeps a corner of DisplayWindowPadding on screen.
    const ImVec2 pad = ImGui::GetStyle().DisplayWindowPadding;
    pos.x = ImClamp(pos.x, pad.x - size.x, ImMax(pad.x - size.x, io.DisplaySize.x - pad.x));
    pos.y = ImClamp(pos.y, 0.0f, ImMax(0.0f, io.DisplaySize.y - pad.y));
    ImGui::SetNextWindowPos(pos, ImGuiCond_Always);
    g_placed = true;
    g_placedPos = pos;
}

// After Begin: a resize from the left or top edge moves the window inside
// Begin, after the position set above; that move is the user's too.
void NoteMainWindow() {
    const ImVec2 pos = ImGui::GetWindowPos();
    if (!g_placed && (pos.x != g_lastEndPos.x || pos.y != g_lastEndPos.y))
        AdoptPosition(pos);
    else if (g_placed && (pos.x != g_placedPos.x || pos.y != g_placedPos.y))
        AdoptPosition(pos);
    g_lastEndPos = pos;
}

void DrawUI() {
    ImGui::SetNextWindowSize(ImVec2(660.0f, 480.0f), ImGuiCond_FirstUseEver);
    PlaceMainWindow();

    ImGui::Begin(kMainWindow);
    NoteMainWindow();
    // Ordered by who wants them: mods first, the tooling that dissects the
    // game and the loader's own health last.
    if (ImGui::BeginTabBar("##tabs")) {
        if (ImGui::BeginTabItem("Mods"))    { host::DrawModsTab();   ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Symbols")) { DrawSymbolsTab();      ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Status"))  { DrawStatusTab();       ImGui::EndTabItem(); }
        ImGui::EndTabBar();
    }

    ImGui::End();
}

} // namespace

void OverlayRender(IDXGISwapChain* swapChain) {
    // A GML call made during this frame (by a mod) can present
    // again from inside it; ImGui's frame and the mods' frame are already
    // open, so the nested Present only lets the game draw.
    static thread_local bool inRender = false;
    if (inRender) return;
    inRender = true;
    struct Leave { ~Leave() { inRender = false; } } leave;

    // Present runs on the game's main thread: from here on, API calls that
    // touch GML are refused from any other thread. Recorded before anything
    // else, and whether or not the overlay itself can start.
    gml::NoteGameThread();

    // The overlay is optional; the game tick below is not. Mods keep running
    // in a game whose swap chain our ImGui backend cannot draw on.
    bool overlay = EnsureInitialised(swapChain);
    if (overlay && !g_rtv) {
        CreateRenderTarget(swapChain);
        overlay = g_rtv != nullptr;
    }
    if (overlay) {
        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();
    }

    ++g_frameCount;

    // We are on the game's render thread here, which is where GML must be
    // called from. Give the game a moment to run its own code first so the
    // borrowed `self` instance is populated.
    if (g_frameCount > 120) {
        gml::AbiSelfTest();
        // Runs once the registry resolves and the game has run some GML.
        builtins::SelfTest();
        // Proves the id -> instance lookup once an instance is at hand.
        gml::VerifyInstanceLookup();
        // Locates and proves what defining object types needs (once).
        objtypes::Verify();
    }

    // Before any mod runs: prove the value free/copy helpers on a probe string
    // (game thread, once). Until then the managed side treats them as absent.
    gml::VerifyValueLifetime();

    // C# mods: initialised on their first frame, then ticked every frame.
    host::Frame();

    // Objects or events the mods defined this frame join the runner's
    // per-event lists before the next frame's Step.
    objtypes::Flush();

    if (overlay) {
        // The game hides the OS cursor, so ImGui has to draw its own while visible.
        ImGui::GetIO().MouseDrawCursor = g_visible;

        if (g_visible)
            DrawUI();

        // Render/NewFrame must stay balanced even when nothing is drawn.
        ImGui::Render();
    }

    // Everything that needed a live instance this frame has run; the next
    // frame's events will supply a fresh one (only matters on runtimes without
    // a current-self global, where the observed instance is the only source).
    gml::ClearObservedSelf();

    // A mod's GML call can resize the game's buffers mid-frame, which drops
    // our view (OverlayInvalidate); this frame's overlay is then skipped.
    if (!overlay || !g_rtv) return;

    // The DX11 backend restores shaders, buffers and viewports but not the
    // output-merger targets: the game's own are put back by hand, so a game
    // that binds its target once keeps drawing where it expects.
    ID3D11RenderTargetView* gameRtv = nullptr;
    ID3D11DepthStencilView* gameDsv = nullptr;
    g_context->OMGetRenderTargets(1, &gameRtv, &gameDsv);
    g_context->OMSetRenderTargets(1, &g_rtv, nullptr);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    g_context->OMSetRenderTargets(1, &gameRtv, gameDsv);
    if (gameRtv) gameRtv->Release();
    if (gameDsv) gameDsv->Release();

    // No reference to the back buffer outlives the frame. A view kept across
    // frames holds the swap chain alive after the game releases it, and DXGI
    // then refuses the game a new chain for the same window (E_ACCESSDENIED
    // from CreateSwapChain*). One view per frame costs next to nothing.
    // (A resize from inside the frame may have dropped it already.)
    if (g_rtv) { g_rtv->Release(); g_rtv = nullptr; }
}

void OverlayInvalidate() {
    if (g_rtv) {
        // Our view may still be bound; a bound reference to the backbuffer
        // makes ResizeBuffers fail.
        if (g_context) {
            ID3D11RenderTargetView* bound = nullptr;
            g_context->OMGetRenderTargets(1, &bound, nullptr);
            if (bound == g_rtv) g_context->OMSetRenderTargets(0, nullptr, nullptr);
            if (bound) bound->Release();
        }
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

void OverlaySetPick(bool armed) {
    g_pickArmed = armed;
    if (!armed) g_pickReady = false;
}

bool OverlayTakePick(int* x, int* y, int* width, int* height, int* button) {
    if (!g_pickReady.exchange(false)) return false;
    if (x) *x = g_pickX;
    if (y) *y = g_pickY;
    if (width) *width = g_pickW;
    if (height) *height = g_pickH;
    if (button) *button = g_pickButton;
    return true;
}

} // namespace mod
