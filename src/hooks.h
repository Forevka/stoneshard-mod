#pragma once

namespace mod {

// Discovers the IDXGISwapChain vtable via a throwaway device/swap chain, then
// detours Present (index 8) and ResizeBuffers (index 13) with MinHook.
bool InstallHooks();

void RemoveHooks();

} // namespace mod
