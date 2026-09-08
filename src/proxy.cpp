// Pass-through implementations for every export of the real version.dll.
//
// The game (and anything else in the process) links against these; we forward
// each call to C:\Windows\System32\version.dll. Export names are attached by
// src/version.def, so nothing here collides with the SDK's own declarations.

#include <windows.h>
#include <atomic>

namespace {

std::atomic<HMODULE> g_realDll{nullptr};

// Load the genuine DLL by ABSOLUTE system path. A bare LoadLibraryW(L"version.dll")
// would find *us* first (we sit next to the exe) and recurse.
HMODULE RealDll() {
    if (HMODULE cached = g_realDll.load(std::memory_order_acquire))
        return cached;

    wchar_t path[MAX_PATH]{};
    const UINT len = GetSystemDirectoryW(path, MAX_PATH);
    if (len == 0 || len > MAX_PATH - 16)
        return nullptr;
    wcscat_s(path, L"\\version.dll");

    HMODULE loaded = LoadLibraryW(path);

    // Benign race: if another thread beat us here, drop our extra reference.
    HMODULE expected = nullptr;
    if (!g_realDll.compare_exchange_strong(expected, loaded)) {
        if (loaded) FreeLibrary(loaded);
        return expected;
    }
    return loaded;
}

FARPROC ResolveReal(const char* name) {
    HMODULE dll = RealDll();
    return dll ? GetProcAddress(dll, name) : nullptr;
}

} // namespace

// Each thunk resolves its target once, then forwards. Signature is declared
// inline so the compiler checks argument passing for us.
#define PROXY_FN(ret, name, params, args)                                    \
    extern "C" ret WINAPI Proxy_##name params {                              \
        using fn_t = ret(WINAPI*) params;                                    \
        static fn_t fn = nullptr;                                            \
        if (!fn) fn = reinterpret_cast<fn_t>(ResolveReal(#name));            \
        if (!fn) { SetLastError(ERROR_PROC_NOT_FOUND); return (ret)0; }      \
        return fn args;                                                      \
    }

PROXY_FN(BOOL, GetFileVersionInfoA,
         (LPCSTR f, DWORD h, DWORD len, LPVOID data), (f, h, len, data))
PROXY_FN(BOOL, GetFileVersionInfoW,
         (LPCWSTR f, DWORD h, DWORD len, LPVOID data), (f, h, len, data))

PROXY_FN(BOOL, GetFileVersionInfoExA,
         (DWORD flags, LPCSTR f, DWORD h, DWORD len, LPVOID data),
         (flags, f, h, len, data))
PROXY_FN(BOOL, GetFileVersionInfoExW,
         (DWORD flags, LPCWSTR f, DWORD h, DWORD len, LPVOID data),
         (flags, f, h, len, data))

PROXY_FN(DWORD, GetFileVersionInfoSizeA, (LPCSTR f, LPDWORD h), (f, h))
PROXY_FN(DWORD, GetFileVersionInfoSizeW, (LPCWSTR f, LPDWORD h), (f, h))

PROXY_FN(DWORD, GetFileVersionInfoSizeExA,
         (DWORD flags, LPCSTR f, LPDWORD h), (flags, f, h))
PROXY_FN(DWORD, GetFileVersionInfoSizeExW,
         (DWORD flags, LPCWSTR f, LPDWORD h), (flags, f, h))

PROXY_FN(BOOL, VerQueryValueA,
         (LPCVOID block, LPCSTR sub, LPVOID* buf, PUINT len), (block, sub, buf, len))
PROXY_FN(BOOL, VerQueryValueW,
         (LPCVOID block, LPCWSTR sub, LPVOID* buf, PUINT len), (block, sub, buf, len))

PROXY_FN(DWORD, VerFindFileA,
         (DWORD flags, LPCSTR file, LPCSTR win, LPCSTR app,
          LPSTR cur, PUINT curLen, LPSTR dest, PUINT destLen),
         (flags, file, win, app, cur, curLen, dest, destLen))
PROXY_FN(DWORD, VerFindFileW,
         (DWORD flags, LPCWSTR file, LPCWSTR win, LPCWSTR app,
          LPWSTR cur, PUINT curLen, LPWSTR dest, PUINT destLen),
         (flags, file, win, app, cur, curLen, dest, destLen))

PROXY_FN(DWORD, VerInstallFileA,
         (DWORD flags, LPCSTR src, LPCSTR dst, LPCSTR srcDir, LPCSTR dstDir,
          LPCSTR curDir, LPSTR tmp, PUINT tmpLen),
         (flags, src, dst, srcDir, dstDir, curDir, tmp, tmpLen))
PROXY_FN(DWORD, VerInstallFileW,
         (DWORD flags, LPCWSTR src, LPCWSTR dst, LPCWSTR srcDir, LPCWSTR dstDir,
          LPCWSTR curDir, LPWSTR tmp, PUINT tmpLen),
         (flags, src, dst, srcDir, dstDir, curDir, tmp, tmpLen))

// Undocumented, so there is no public signature to match. On x64 the first four
// integer/pointer arguments always arrive in RCX/RDX/R8/R9, so a four-slot
// pass-through forwards it correctly regardless of the true parameter types.
PROXY_FN(UINT_PTR, GetFileVersionInfoByHandle,
         (UINT_PTR a, UINT_PTR b, UINT_PTR c, UINT_PTR d), (a, b, c, d))

#undef PROXY_FN
