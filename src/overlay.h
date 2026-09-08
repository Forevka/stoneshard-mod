#pragma once

struct IDXGISwapChain;

namespace mod {

// Called from the Present hook, every frame. Lazily performs ImGui/D3D init on
// the first call, using the device and window owned by the game's swap chain.
void OverlayRender(IDXGISwapChain* swapChain);

// Called from the ResizeBuffers hook, before the original runs. Drops the
// render target view so the resize is not blocked by an outstanding reference.
void OverlayInvalidate();

void OverlayShutdown();

} // namespace mod
