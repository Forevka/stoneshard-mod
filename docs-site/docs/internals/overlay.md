---
title: Overlay and the frame tick
description: How Lodestone hooks D3D11 Present to draw its ImGui overlay and to run the per-frame tick that drives self-tests and the managed runtime.
---

The loader needs two things every frame: a moment on the game's main thread to call GML, and a place to draw its window. The D3D11 swap chain's `Present` provides both. This page covers how that hook is found (`src/hooks.cpp`), what runs inside it (`src/overlay.cpp`), and the managed frame stages it drives (`managed/CoreLoader/Runtime/Entry.cs`).

## Finding Present without the game's swap chain {#finding-present}

At startup the game's swap chain does not exist yet, and the loader has no pointer to it anyway. `IDXGISwapChain` is a COM interface, though, and every swap chain created by `dxgi.dll` shares one vtable in that DLL's read-only data. So the init thread builds a throwaway swap chain of its own and reads the vtable from it:

1. Register a window class and create a hidden 64x64 probe window.
2. Call `D3D11CreateDeviceAndSwapChain` on a hardware device, falling back to WARP if that fails.
3. Read entries 8 (`Present`) and 13 (`ResizeBuffers`) from the swap chain's vtable.
4. Release the device, context, swap chain and window. The addresses stay valid after that, because the vtable belongs to `dxgi.dll`, not to the objects.

```cpp
// IDXGISwapChain vtable layout: IUnknown(0-2), IDXGIObject(3-6),
// IDXGIDeviceSubObject(7), then IDXGISwapChain::Present at 8.
constexpr int kPresentIndex       = 8;
constexpr int kResizeBuffersIndex = 13;
```

<small>Source: [src/hooks.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/hooks.cpp#L21-L24)</small>

After `Present` (8) the interface declares `GetBuffer`, `SetFullscreenState`, `GetFullscreenState` and `GetDesc`, which puts `ResizeBuffers` at 13. `InstallHooks` then initialises MinHook, creates both detours and enables them. `d3d11` and `dxgi` are static imports of the proxy DLL, so they are already loaded when the init thread runs. If installation fails, the log says `hook installation failed - overlay will not appear`. Without the `Present` hook there is no frame tick either, so in that case mods never start.

## The two hooks {#present-hooks}

```cpp
HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain* swapChain,
                                        UINT syncInterval, UINT flags) {
    OverlayRender(swapChain);
    return g_originalPresent(swapChain, syncInterval, flags);
}
```

<small>Source: [src/hooks.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/hooks.cpp#L26-L30)</small>

Everything the loader does per frame happens in `OverlayRender`, before the game's frame is presented, so the overlay is drawn on top of the finished frame.

`HookedResizeBuffers` calls `OverlayInvalidate()` before forwarding. `ResizeBuffers` fails with `DXGI_ERROR_INVALID_CALL` while anything still references a back buffer, and the overlay's render-target view is such a reference. `OverlayInvalidate` unbinds the view if it is still bound and releases it. The next frame creates a new one.

## Initialising the overlay {#initialisation}

`EnsureInitialised` runs on the first `Present`:

- It gets the device and immediate context from the swap chain, and the game window from the swap chain description.
- It creates the ImGui context and stores `imgui.ini` in the loader's data directory (`paths::File("imgui.ini")`), not in the game folder.
- It initialises the ImGui Win32 and DX11 backends and subclasses the game window with `SetWindowLongPtrW(GWLP_WNDPROC, HookedWndProc)`.

If any step fails, the overlay is disabled for the session with a log line. The frame tick still runs, because mods do not depend on the overlay being drawable.

### Games that replace their swap chain {#follow-device}

Some games throw away their swap chain after the first frames and build a new one on a new device. The King is Watching, one of the breadth-test games, does this at startup. The ImGui DX11 backend would then keep drawing with a dead device. On every later frame, `EnsureInitialised` calls `FollowDevice`, which compares the presented chain's device with the one the backend holds:

```cpp
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
```

<small>Source: [src/overlay.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/overlay.cpp#L145-L159)</small>

If the re-init fails, the overlay also hides itself. Otherwise ImGui's last "wants the mouse" verdict would stay frozen, and the window procedure would keep swallowing the game's input.

## The window procedure {#wndproc}

`HookedWndProc` sees every message before the game does. Its rules, in order:

| Message | What happens | Why |
|---|---|---|
| `WM_DESTROY` | `host::Shutdown()`, then on to the game | The last point where mods can run on the game thread. `DllMain` detach runs under the loader lock, where calling into .NET is unsafe. |
| `WM_KEYDOWN` `VK_INSERT` | Toggle the overlay (ignoring auto-repeat), swallow the key | The game never sees the toggle key. |
| A click while pick mode is armed | Record it and swallow it, including the matching button-up | See [pick mode](#pick-mode). |
| Key and button releases, overlay visible | Fed to ImGui **and** always passed to the game | A key pressed in the game and released over the overlay would otherwise stay held down for the game. |
| Mouse messages while ImGui wants the mouse, key and char messages while it wants the keyboard | Fed to ImGui, swallowed | Typing in an overlay text box must not move the character. |
| Everything else | Passed to the game | |

```cpp
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
```

<small>Source: [src/overlay.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/overlay.cpp#L106-L119)</small>

### Pick mode {#pick-mode}

`Input.ArmPick()` lets a mod take one click in the game world, for example to inspect whatever is under the cursor. The window procedure handles it natively:

- When armed, the next left or right button-down outside the overlay's windows is recorded (position, client size, button) and swallowed. The mouse is captured so the matching button-up arrives even outside the window.
- That one button-up is swallowed too. Every other message is untouched, and the game keeps seeing mouse movement, so its own idea of the mouse position matches the click.
- `Input.TryTakePick` collects the click through the CoreApi once, and only for the mod that armed it.

## The per-frame tick {#frame-tick}

`OverlayRender` is the loader's heartbeat. Its order matters:

1. **Re-entrancy guard.** A GML call a mod makes during the frame can itself present (a `screen_refresh`, a script that draws and flips). The nested `Present` returns at once and lets only the game draw, because ImGui's frame and the mods' frame are already open.
2. **`gml::NoteGameThread()`.** `Present` runs on the game's main thread. Recording it first means API calls that touch GML are refused from any other thread from now on, whether or not the overlay can start.
3. **Overlay setup.** `EnsureInitialised`, then a fresh render-target view, then the ImGui `NewFrame` calls. Each is skipped if the overlay is unavailable.
4. **Self-tests, staged readiness, and the managed frame:**

```cpp
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
}

// Before any mod runs: prove the value free/copy helpers on a probe string
// (game thread, once). Until then the managed side treats them as absent.
gml::VerifyValueLifetime();

// C# mods: initialised on their first frame, then ticked every frame.
host::Frame();
```

<small>Source: [src/overlay.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/overlay.cpp#L353-L371)</small>

The self-tests are described in [runtime bridge](./runtime-bridge.md) and [builtins](./builtins.md), and the readiness stages in [boot](./boot.md).

5. **Draw.** If the overlay is visible, `DrawUI()` builds the Lodestone window. `ImGui::Render()` is always called, so `NewFrame` and `Render` stay balanced. While the overlay is visible, ImGui draws its own cursor, because the game hides the OS one.
6. **`gml::ClearObservedSelf()`.** Everything that needed a live instance this frame has run. On 2024 runtimes the next frame's events supply a fresh one (see [hook engine](./hook-engine.md)).
7. **Render the draw data** (next section) and release the render-target view.

### Render targets {#render-targets}

```cpp
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
```

<small>Source: [src/overlay.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/overlay.cpp#L396-L410)</small>

Two decisions show up here:

- **Save and restore the game's targets.** The ImGui DX11 backend restores shaders, buffers and viewports, but not the output-merger render targets. A game that binds its target once and relies on it staying bound would otherwise draw into the overlay's view on the next frame.
- **A new view every frame.** Keeping one view across frames looks cheaper, but it holds a reference to the back buffer and so keeps the swap chain alive after the game releases it. A game that recreates its swap chain for the same window then gets `E_ACCESSDENIED` from DXGI. Creating one view per frame costs next to nothing and rules this out.

If a mod's GML call resized the buffers mid-frame, `OverlayInvalidate` has already dropped the view, and that frame's overlay is skipped.

## The Lodestone window {#window}

`DrawUI` draws one ImGui window titled "Lodestone" with three tabs, ordered by who wants them:

| Tab | Drawn by | Contents |
|---|---|---|
| Mods | `host::DrawModsTab()` then managed `Entry.Gui` | A Loader tab (game, symbol and builtin counts, bridge state, hook count, interop status, hot reload toggle, Reload buttons, mods not loaded and why), then one tab per mod with its `OnGUI`. If the .NET runtime is not running, the reason. |
| Symbols | `DrawSymbolsTab` | Filter over every resolved `gml_*` function with its address. It re-filters only when the query changes, capped at 2000 matches, because scanning tens of thousands of names every frame would cost more than the rest of the overlay. |
| Status | `DrawStatusTab` | Frames through the hook, back-buffer size, symbol resolver health (or "not a YYC game"), GML bridge state, builtin registry and its self-test result, and the log. INSERT toggles the overlay. |

The managed `Gui` pass tracks UI scopes. A mod whose `OnGUI` leaves a scope open is faulted, and whatever is still open is closed before ImGui sees `End()`, so one mod cannot corrupt the tabs drawn after it. See [faults](../modding/concepts.md#faults).

## Managed frame stages {#managed-frame}

`host::Frame()` calls the managed `Entry.Frame` under a `ManagedScope`, skipping it on re-entry (see [.NET host](./dotnet-host.md)). `Entry.Frame` runs a fixed list of stages:

```csharp
try
{
    Stage("initialise", EnsureModsInitialised);
    // Rebuilt mod dlls are swapped in here, between frames, where no
    // mod code is on the stack.
    if (_initialisedMods) Stage("hot reload", ModManager.PollChanges);
    Stage("queued actions", () => Game.DrainPending(Log));
    Stage("test host", TestHost.Tick);
    Stage("hook request timeouts", Hooks.TickRequests);
    Stage("object table", ObjectTable.Tick);
    Stage("interop", InteropGenerator.Tick);
    Stage("variable harvest", VarHarvest.Tick);
    Stage("updates", () => ModManager.ForEach(nameof(CoreMod.OnUpdate), mod => mod.OnUpdate()));
    Stage("game drawing", GameDraw.Tick);
    Stage("settings", ModConfig.FlushSettled);
}
finally
{
    try { Values.Drain(); } catch (Exception ex) { Log.Error("releasing the frame's values failed", ex); }
    _inFrame = false;
}
```

<small>Source: [managed/CoreLoader/Runtime/Entry.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/Entry.cs#L115-L135)</small>

- **Each stage is isolated.** `Stage` catches and logs, so one failing stage (a half-written DLL during hot reload, a full disk while saving settings) does not cost every mod its `OnUpdate`.
- **`initialise`** waits for the game's assets before starting mods, then attaches attribute hooks and calls each mod's `OnInitialize` (see [boot](./boot.md)).
- **`hot reload`** runs between frames, where no mod code is on the stack (see [managed runtime](./managed-runtime.md)).
- **`Values.Drain()` in `finally`.** Strings and arrays the game handed out during the frame are pooled and released here, whatever happened above. A stage that threw must not leak the frame's values. See [values](../modding/concepts.md#values).

The `Gui` pass, which runs inside `DrawUI` later in the same frame, drains the pool again in its own `finally`, after closing any UI scopes left open.
