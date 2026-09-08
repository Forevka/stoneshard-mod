#include "hooks.h"
#include "log.h"
#include "overlay.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <MinHook.h>

namespace mod {
namespace {

using Present_t = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using ResizeBuffers_t = HRESULT(STDMETHODCALLTYPE*)(
    IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

Present_t       g_originalPresent       = nullptr;
ResizeBuffers_t g_originalResizeBuffers = nullptr;
bool            g_installed             = false;

// IDXGISwapChain vtable layout: IUnknown(0-2), IDXGIObject(3-6),
// IDXGIDeviceSubObject(7), then IDXGISwapChain::Present at 8.
constexpr int kPresentIndex       = 8;
constexpr int kResizeBuffersIndex = 13;

HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain* swapChain,
                                        UINT syncInterval, UINT flags) {
    OverlayRender(swapChain);
    return g_originalPresent(swapChain, syncInterval, flags);
}

HRESULT STDMETHODCALLTYPE HookedResizeBuffers(IDXGISwapChain* swapChain,
                                              UINT bufferCount, UINT width,
                                              UINT height, DXGI_FORMAT format,
                                              UINT flags) {
    // Must drop our render target view first or the resize fails with
    // DXGI_ERROR_INVALID_CALL (outstanding backbuffer reference).
    OverlayInvalidate();
    return g_originalResizeBuffers(swapChain, bufferCount, width, height, format, flags);
}

// Builds a temporary device + swap chain purely to read the DXGI vtable. The
// vtable lives in dxgi.dll's read-only data and is shared process-wide, so the
// addresses stay valid after everything here is released.
bool ResolveSwapChainVTable(void** outPresent, void** outResizeBuffers) {
    const wchar_t* kClassName = L"SSModVTableProbe";

    WNDCLASSEXW wc{};
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = DefWindowProcW;
    wc.hInstance     = GetModuleHandleW(nullptr);
    wc.lpszClassName = kClassName;
    RegisterClassExW(&wc);   // failure is fine if the class already exists

    HWND hwnd = CreateWindowExW(0, kClassName, L"", WS_OVERLAPPEDWINDOW,
                                0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
    if (!hwnd) {
        Logf("[!] probe window creation failed (%lu)", GetLastError());
        UnregisterClassW(kClassName, wc.hInstance);
        return false;
    }

    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount                        = 1;
    sd.BufferDesc.Width                   = 64;
    sd.BufferDesc.Height                  = 64;
    sd.BufferDesc.Format                  = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator   = 60;
    sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.BufferUsage                        = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow                       = hwnd;
    sd.SampleDesc.Count                   = 1;
    sd.Windowed                           = TRUE;
    sd.SwapEffect                         = DXGI_SWAP_EFFECT_DISCARD;

    D3D_FEATURE_LEVEL levels[]  = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0};
    D3D_FEATURE_LEVEL obtained  = {};
    IDXGISwapChain*      swap   = nullptr;
    ID3D11Device*        device = nullptr;
    ID3D11DeviceContext* ctx    = nullptr;

    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, ARRAYSIZE(levels),
        D3D11_SDK_VERSION, &sd, &swap, &device, &obtained, &ctx);

    if (FAILED(hr)) {
        Logf("hardware probe failed (0x%08lX), retrying with WARP", hr);
        hr = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, levels, ARRAYSIZE(levels),
            D3D11_SDK_VERSION, &sd, &swap, &device, &obtained, &ctx);
    }

    bool ok = false;
    if (SUCCEEDED(hr) && swap) {
        void** vtable    = *reinterpret_cast<void***>(swap);
        *outPresent      = vtable[kPresentIndex];
        *outResizeBuffers = vtable[kResizeBuffersIndex];
        Logf("vtable=%p  Present=%p  ResizeBuffers=%p",
             vtable, *outPresent, *outResizeBuffers);
        ok = true;
    } else {
        Logf("[!] D3D11CreateDeviceAndSwapChain failed (0x%08lX)", hr);
    }

    if (ctx)    ctx->Release();
    if (device) device->Release();
    if (swap)   swap->Release();
    DestroyWindow(hwnd);
    UnregisterClassW(kClassName, wc.hInstance);
    return ok;
}

} // namespace

bool InstallHooks() {
    if (g_installed) return true;

    void* presentAddr = nullptr;
    void* resizeAddr  = nullptr;
    if (!ResolveSwapChainVTable(&presentAddr, &resizeAddr))
        return false;

    if (MH_Initialize() != MH_OK) {
        Logf("[!] MH_Initialize failed");
        return false;
    }

    if (MH_CreateHook(presentAddr, &HookedPresent,
                      reinterpret_cast<void**>(&g_originalPresent)) != MH_OK) {
        Logf("[!] MH_CreateHook(Present) failed");
        return false;
    }
    if (MH_CreateHook(resizeAddr, &HookedResizeBuffers,
                      reinterpret_cast<void**>(&g_originalResizeBuffers)) != MH_OK) {
        Logf("[!] MH_CreateHook(ResizeBuffers) failed");
        return false;
    }

    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK) {
        Logf("[!] MH_EnableHook failed");
        return false;
    }

    g_installed = true;
    Logf("hooks installed; waiting for the game's first Present");
    return true;
}

void RemoveHooks() {
    if (!g_installed) return;
    MH_DisableHook(MH_ALL_HOOKS);
    OverlayShutdown();
    MH_Uninitialize();
    g_installed = false;
    Logf("hooks removed");
}

} // namespace mod
