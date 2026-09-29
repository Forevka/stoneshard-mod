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

// Pick mode, for tools that let the user click on something in the game. While
// armed, the next left or right click outside the overlay's own windows is
// swallowed (the game never sees it) and kept for OverlayTakePick: the
// position in client pixels, the client size, and the button (0 left, 1 right).
void OverlaySetPick(bool armed);
bool OverlayTakePick(int* x, int* y, int* width, int* height, int* button);

} // namespace mod
