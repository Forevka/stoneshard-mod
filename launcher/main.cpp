// lodestone_installer - a tiny native Win32 installer for the Lodestone mod
// loader. It discovers the YYC GameMaker game it was placed next to, shows the
// game's name and install state, and installs/uninstalls the loader by
// registering it as a native extension inside the game's data.win. Once
// installed, the game loads the mods however it is started (its own .exe, Steam,
// a shortcut), so there is no "start the game" button here - only Install/Uninstall.
//
// The one operation that must run on .NET - editing data.win via UndertaleModLib
// - is delegated to LodestonePatcher.exe, spawned as a separate process. Keeping
// that in its own process is deliberate: it quarantines UndertaleModLib's GPL-3.0
// licence to that small tool, and keeps all the .NET dependency weight out of
// this launcher, which needs no .NET at all.
//
// This file is part of CoreLoader/Lodestone and is under the project's own
// licence (see the repository root). It is NOT GPL: it only invokes the GPL
// LodestonePatcher as a separate program.

#include <windows.h>
#include <shlobj.h>
#include <string>
#include <vector>
#include <cstdint>
#include <cstring>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")

namespace {

// ---- small helpers ---------------------------------------------------------

std::wstring ExeDir() {
    wchar_t buf[MAX_PATH]{};
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    std::wstring p(buf);
    const size_t slash = p.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : p.substr(0, slash);
}

bool FileExists(const std::wstring& p) {
    const DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

bool DirExists(const std::wstring& p) {
    const DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

std::wstring Join(const std::wstring& a, const std::wstring& b) {
    if (a.empty()) return b;
    const wchar_t last = a.back();
    return (last == L'\\' || last == L'/') ? a + b : a + L"\\" + b;
}

// Read a little-endian uint32 from a byte buffer at an offset, bounds-checked.
bool ReadU32(const std::vector<uint8_t>& buf, size_t off, uint32_t& out) {
    if (off + 4 > buf.size()) return false;
    out = (uint32_t)buf[off] | ((uint32_t)buf[off + 1] << 8) |
          ((uint32_t)buf[off + 2] << 16) | ((uint32_t)buf[off + 3] << 24);
    return true;
}

// Read `count` bytes from an absolute file offset. data.win strings live in the
// STRG chunk near the END of the file (tens of MB in for a big game), so the UI
// reads only the few regions it needs by offset rather than the whole file.
std::vector<uint8_t> ReadBytesAt(const std::wstring& path, uint64_t off, uint32_t count) {
    std::vector<uint8_t> out;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return out;
    LARGE_INTEGER li; li.QuadPart = (LONGLONG)off;
    if (SetFilePointerEx(h, li, nullptr, FILE_BEGIN)) {
        out.resize(count);
        DWORD read = 0; size_t done = 0;
        while (done < count) {
            DWORD want = (count - done > 0x100000u) ? 0x100000u : (DWORD)(count - done);
            if (!ReadFile(h, out.data() + done, want, &read, nullptr) || read == 0) break;
            done += read;
        }
        out.resize(done);
    }
    CloseHandle(h);
    return out;
}

// Walk the FORM chunk table (8-byte headers only, skipping the payloads) and
// return the payload offset+size of a named chunk, e.g. "GEN8" or "STRG".
bool FindChunk(const std::wstring& path, const char name[4], uint64_t& outOff, uint32_t& outSize) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    bool found = false;
    uint8_t hdr[8]; DWORD read = 0;
    if (ReadFile(h, hdr, 8, &read, nullptr) && read == 8 && memcmp(hdr, "FORM", 4) == 0) {
        uint64_t pos = 8;
        for (;;) {
            LARGE_INTEGER li; li.QuadPart = (LONGLONG)pos;
            if (!SetFilePointerEx(h, li, nullptr, FILE_BEGIN)) break;
            if (!ReadFile(h, hdr, 8, &read, nullptr) || read != 8) break;
            const uint32_t sz = (uint32_t)hdr[4] | ((uint32_t)hdr[5] << 8) |
                                ((uint32_t)hdr[6] << 16) | ((uint32_t)hdr[7] << 24);
            if (memcmp(hdr, name, 4) == 0) { outOff = pos + 8; outSize = sz; found = true; break; }
            pos += 8 + (uint64_t)sz;
        }
    }
    CloseHandle(h);
    return found;
}

// ---- data.win parsing (read-only, just enough for the UI) ------------------

// Find the game display name by walking FORM chunks to GEN8 and resolving the
// game-name string pointer (absolute file offset; length is the u32 before it).
// This mirrors the layout we validated across GM 2022.x and 2024.x. Returns an
// empty string if anything does not line up - the UI simply shows the folder.
std::wstring ReadGameName(const std::wstring& dataWin) {
    uint64_t gen8Off = 0; uint32_t gen8Size = 0;
    if (!FindChunk(dataWin, "GEN8", gen8Off, gen8Size) || gen8Size < 44) return L"";

    // GEN8: u8 debug, u8 bytecode, u16 unknown, u32 filename, u32 config, u32 lastObj,
    // u32 lastTile, u32 gameId, 16-byte guid, u32 gameName... -> name pointer at +40.
    std::vector<uint8_t> hdr = ReadBytesAt(dataWin, gen8Off, 44);
    uint32_t strPtr = 0;
    if (!ReadU32(hdr, 40, strPtr) || strPtr < 4) return L"";

    // The pointer is an absolute file offset into STRG (near the end of the file);
    // the string's length is the u32 just before the characters.
    std::vector<uint8_t> lenB = ReadBytesAt(dataWin, strPtr - 4, 4);
    uint32_t len = 0;
    if (!ReadU32(lenB, 0, len) || len == 0 || len > 256) return L"";
    std::vector<uint8_t> s = ReadBytesAt(dataWin, strPtr, len);
    if (s.size() < len) return L"";

    int wlen = MultiByteToWideChar(CP_UTF8, 0, (const char*)s.data(), (int)len, nullptr, 0);
    if (wlen <= 0) return L"";
    std::wstring out(wlen, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, (const char*)s.data(), (int)len, out.data(), wlen);
    return out;
}

// ---- process launching -----------------------------------------------------

// Run a command line, hidden, to completion. Captures stdout+stderr so install
// failures can show the patcher's own message. Returns false if it could not
// even be started.
bool RunCapture(const std::wstring& cmdline, DWORD& exitCode, std::wstring& output) {
    SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE};
    HANDLE rd = nullptr, wr = nullptr;
    if (!CreatePipe(&rd, &wr, &sa, 0)) return false;
    SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput = wr;
    si.hStdError = wr;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);

    PROCESS_INFORMATION pi{};
    std::wstring mutableCmd = cmdline;  // CreateProcessW may modify the buffer.
    BOOL ok = CreateProcessW(nullptr, mutableCmd.data(), nullptr, nullptr, TRUE,
                             CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    CloseHandle(wr);  // our copy; child keeps its own
    if (!ok) { CloseHandle(rd); return false; }

    std::string raw;
    char chunk[4096];
    DWORD got = 0;
    while (ReadFile(rd, chunk, sizeof(chunk), &got, nullptr) && got > 0)
        raw.append(chunk, got);
    CloseHandle(rd);

    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    int wlen = MultiByteToWideChar(CP_UTF8, 0, raw.c_str(), (int)raw.size(), nullptr, 0);
    output.assign(wlen, L'\0');
    if (wlen > 0)
        MultiByteToWideChar(CP_UTF8, 0, raw.c_str(), (int)raw.size(), output.data(), wlen);
    return true;
}

// ---- recursive copy / delete ----------------------------------------------

bool CopyTree(const std::wstring& src, const std::wstring& dst) {
    if (!DirExists(src)) return false;
    CreateDirectoryW(dst.c_str(), nullptr);
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(Join(src, L"*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return true;
    bool ok = true;
    do {
        const std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        const std::wstring s = Join(src, name), d = Join(dst, name);
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            ok = CopyTree(s, d) && ok;
        else
            ok = (CopyFileW(s.c_str(), d.c_str(), FALSE) != 0) && ok;
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return ok;
}

void DeleteTree(const std::wstring& dir) {
    if (!DirExists(dir)) return;
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(Join(dir, L"*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            const std::wstring name = fd.cFileName;
            if (name == L"." || name == L"..") continue;
            const std::wstring p = Join(dir, name);
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                DeleteTree(p);
            else
                DeleteFileW(p.c_str());
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

// ---- discovery -------------------------------------------------------------

struct Target {
    std::wstring gameDir;
    std::wstring dataWin;   // full path, empty if none
    std::wstring name;      // display name, may be empty
};

std::wstring FindDataWin(const std::wstring& dir) {
    const std::wstring direct = Join(dir, L"data.win");
    if (FileExists(direct)) return direct;
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(Join(dir, L"*.win").c_str(), &fd);
    std::wstring found;
    if (h != INVALID_HANDLE_VALUE) {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) found = Join(dir, fd.cFileName);
        FindClose(h);
    }
    return found;
}

// Folder-picker fallback when the installer is not sitting in a game folder.
std::wstring BrowseForGame(HWND owner) {
    BROWSEINFOW bi{};
    bi.hwndOwner = owner;
    bi.lpszTitle = L"Select the game folder (the one with data.win)";
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
    LPITEMIDLIST pidl = SHBrowseForFolderW(&bi);
    if (!pidl) return L"";
    wchar_t path[MAX_PATH]{};
    const bool ok = SHGetPathFromIDListW(pidl, path);
    CoTaskMemFree(pidl);
    return ok ? std::wstring(path) : std::wstring();
}

Target Discover(HWND owner) {
    Target t;
    t.gameDir = ExeDir();
    t.dataWin = FindDataWin(t.gameDir);
    if (t.dataWin.empty()) {
        const std::wstring picked = BrowseForGame(owner);
        if (!picked.empty()) {
            t.gameDir = picked;
            t.dataWin = FindDataWin(t.gameDir);
        }
    }
    if (!t.dataWin.empty())
        t.name = ReadGameName(t.dataWin);
    return t;
}

// Authoritative install check: is our extension actually registered in data.win?
// DLL presence alone is misleading - a Steam "verify integrity" or a game update
// restores data.win (dropping the extension) but leaves coreloader_ext.dll on disk,
// so a presence check would wrongly report "installed" on an unmodded game. The
// extension name only appears in data.win if we put it there. EXTN sits near the
// start of the file, so a capped read finds it cheaply even on 100 MB+ files.
bool ExtensionPresent(const std::wstring& dataWin) {
    if (dataWin.empty()) return false;
    // The extension's name string lives in STRG like every other string, so scan
    // that chunk (a couple of MB) for our unique name rather than the whole file.
    uint64_t strgOff = 0; uint32_t strgSize = 0;
    if (!FindChunk(dataWin, "STRG", strgOff, strgSize)) return false;
    std::vector<uint8_t> buf = ReadBytesAt(dataWin, strgOff, strgSize);
    static const char needle[] = "CoreLoaderExt";
    const size_t nlen = sizeof(needle) - 1;
    if (buf.size() < nlen) return false;
    for (size_t i = 0; i + nlen <= buf.size(); ++i)
        if (memcmp(buf.data() + i, needle, nlen) == 0) return true;
    return false;
}

// ---- install / uninstall orchestration ------------------------------------

std::wstring Quote(const std::wstring& s) { return L"\"" + s + L"\""; }

bool DoInstall(HWND hwnd, const Target& t, std::wstring& message) {
    const std::wstring launcherDir = ExeDir();
    const std::wstring payload = Join(launcherDir, L"payload");
    const std::wstring patcher = Join(Join(launcherDir, L"patcher"), L"LodestonePatcher.exe");

    if (!FileExists(patcher)) { message = L"Missing patcher\\LodestonePatcher.exe next to the launcher."; return false; }
    if (!FileExists(Join(payload, L"coreloader_ext.dll"))) { message = L"Missing payload\\coreloader_ext.dll next to the launcher."; return false; }

    // Warn about a leftover version.dll proxy, which would load the loader twice.
    if (FileExists(Join(t.gameDir, L"version.dll"))) {
        if (MessageBoxW(hwnd,
                L"A version.dll is already in the game folder. On this install route the "
                L"loader is loaded as a GameMaker extension, so a version.dll proxy would "
                L"load it a second time.\n\nRename it aside and continue?",
                L"Lodestone", MB_YESNO | MB_ICONWARNING) == IDYES) {
            MoveFileExW(Join(t.gameDir, L"version.dll").c_str(),
                        Join(t.gameDir, L"version.dll.disabled-by-lodestone").c_str(),
                        MOVEFILE_REPLACE_EXISTING);
        }
    }

    // Back up data.win once, keeping the pristine original for uninstall. A failure
    // here (read-only folder, no space) must stop the install before anything changes.
    const std::wstring backup = t.dataWin + L".lodestone-bak";
    if (!FileExists(backup) && !CopyFileW(t.dataWin.c_str(), backup.c_str(), TRUE)) {
        message = L"Could not back up data.win. Is the game folder writable?";
        return false;
    }

    // Stage the payload into the game folder. Check each copy: if staging fails the
    // extension must not be registered, or the game would reference a missing DLL.
    if (!CopyFileW(Join(payload, L"coreloader_ext.dll").c_str(), Join(t.gameDir, L"coreloader_ext.dll").c_str(), FALSE)) {
        message = L"Could not copy coreloader_ext.dll into the game folder. Is the game running, or the folder read-only?";
        return false;
    }
    if (DirExists(Join(payload, L"Lodestone")) && !CopyTree(Join(payload, L"Lodestone"), Join(t.gameDir, L"Lodestone"))) {
        message = L"Could not copy the Lodestone runtime into the game folder.";
        return false;
    }
    if (DirExists(Join(payload, L"Mods")) && !CopyTree(Join(payload, L"Mods"), Join(t.gameDir, L"Mods"))) {
        message = L"Could not copy mods into the game folder.";
        return false;
    }

    // Register the extension in data.win (only after the DLL is in place above).
    const std::wstring cmd = Quote(patcher) + L" install --game " + Quote(t.gameDir);
    DWORD exitCode = 0; std::wstring out;
    if (!RunCapture(cmd, exitCode, out)) { message = L"Could not start LodestonePatcher. Is .NET 10 installed?"; return false; }
    if (exitCode != 0) { message = L"Patcher failed:\n" + out; return false; }

    message = L"Mod support added. Start the game normally (its own .exe, Steam, or a shortcut) and "
              L"Lodestone loads automatically. Log: Lodestone\\Logs\\lodestone.log.";
    return true;
}

bool DoUninstall(HWND hwnd, const Target& t, std::wstring& message) {
    (void)hwnd;
    const std::wstring patcher = Join(Join(ExeDir(), L"patcher"), L"LodestonePatcher.exe");

    // Remove the extension from data.win first. The staged DLL is deleted only once
    // this succeeds: deleting it while the EXTN entry still points at it would leave
    // the game failing to load a missing extension.
    if (!FileExists(patcher) || t.dataWin.empty()) {
        message = L"LodestonePatcher or data.win is missing; cannot remove the extension cleanly.";
        return false;
    }
    const std::wstring cmd = Quote(patcher) + L" uninstall --game " + Quote(t.gameDir);
    DWORD exitCode = 0; std::wstring out;
    if (!RunCapture(cmd, exitCode, out)) {
        message = L"Could not start LodestonePatcher (is .NET 10 installed?). The loader DLL was left in "
                  L"place, so the game still starts; nothing was changed.";
        return false;
    }
    if (exitCode != 0) { message = L"Patcher failed:\n" + out; return false; }

    // Remove the staged loader so nothing loads it. Leave Lodestone\ (logs,
    // settings) and Mods\ (which may hold the user's own mods) in place.
    DeleteFileW(Join(t.gameDir, L"coreloader_ext.dll").c_str());

    message = L"Uninstalled. The game will launch unmodified. (Lodestone settings and mods were left in place.)";
    return true;
}

// ---- window ----------------------------------------------------------------

enum { ID_INSTALL = 101, ID_UNINSTALL, ID_EXIT };

Target g_target;
HWND g_info = nullptr, g_install = nullptr, g_uninstall = nullptr;

void RefreshUi(HWND hwnd) {
    const bool haveGame = !g_target.dataWin.empty();
    const bool installed = haveGame && ExtensionPresent(g_target.dataWin);

    std::wstring info;
    if (!haveGame) {
        info = L"No GameMaker game found.\r\nPut this installer in the game folder (the one "
               L"with data.win) and reopen it.";
    } else {
        info  = L"Game:   " + (g_target.name.empty() ? L"(unknown)" : g_target.name) + L"\r\n";
        info += L"Folder: " + g_target.gameDir + L"\r\n";
        info += installed
            ? L"Status: Mod support added - you can start the game now."
            : L"Status: not installed - click Install to add mod support.";
    }
    SetWindowTextW(g_info, info.c_str());

    ShowWindow(g_install,   (haveGame && !installed) ? SW_SHOW : SW_HIDE);
    ShowWindow(g_uninstall, (haveGame &&  installed) ? SW_SHOW : SW_HIDE);
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE: {
        g_info = CreateWindowW(L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                               16, 16, 452, 90, hwnd, nullptr, nullptr, nullptr);
        auto mkButton = [&](const wchar_t* text, int id, int x) {
            return CreateWindowW(L"BUTTON", text, WS_CHILD | BS_PUSHBUTTON,
                                 x, 120, 108, 32, hwnd, (HMENU)(INT_PTR)id, nullptr, nullptr);
        };
        g_install   = mkButton(L"Install", ID_INSTALL, 16);
        g_uninstall = mkButton(L"Uninstall", ID_UNINSTALL, 16);
        mkButton(L"Exit", ID_EXIT, 360);
        // A readable default font instead of the ancient system one.
        HFONT font = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
        for (HWND c : {g_info, g_install, g_uninstall, GetDlgItem(hwnd, ID_EXIT)})
            if (c) SendMessageW(c, WM_SETFONT, (WPARAM)font, TRUE);
        RefreshUi(hwnd);
        return 0;
    }
    case WM_COMMAND: {
        const int id = LOWORD(wParam);
        if (id == ID_EXIT) { DestroyWindow(hwnd); return 0; }
        if (id == ID_INSTALL || id == ID_UNINSTALL) {
            std::wstring message;
            const bool ok = (id == ID_INSTALL) ? DoInstall(hwnd, g_target, message)
                                               : DoUninstall(hwnd, g_target, message);
            MessageBoxW(hwnd, message.c_str(), L"Lodestone",
                        ok ? MB_ICONINFORMATION : MB_ICONERROR);
            RefreshUi(hwnd);
            return 0;
        }
        return 0;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

} // namespace

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR, int) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    wc.lpszClassName = L"LodestoneLauncher";
    RegisterClassW(&wc);

    // Discover before creating the window so the first paint is already correct.
    g_target = Discover(nullptr);

    HWND hwnd = CreateWindowW(wc.lpszClassName, L"Lodestone Installer",
                              WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                              CW_USEDEFAULT, CW_USEDEFAULT, 500, 210,
                              nullptr, nullptr, hInst, nullptr);
    if (!hwnd) return 1;
    ShowWindow(hwnd, SW_SHOWNORMAL);
    UpdateWindow(hwnd);

    MSG m{};
    while (GetMessageW(&m, nullptr, 0, 0) > 0) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    CoUninitialize();
    return 0;
}
