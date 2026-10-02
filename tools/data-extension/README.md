# data.win extension route (proof of concept)

Loads the Lodestone/CoreLoader native DLL by registering it as a **GameMaker
extension inside the game's `data.win`**, so the game's own extension loader
`LoadLibrary`s it at start-up. This is the same mechanism the test games already
use for their bundled native extensions:

- Stoneshard: `Steamworks_x64.dll`, `FastAstar`, … (8 extensions)
- Dwarf Eats Mountain Demo: `ImGui_GM`

No `version.dll` proxy, no process injection, no patched `.exe`. The game loads
the DLL through its sanctioned extension system — the proper, documented way to
add native code to a GameMaker game. A side effect of using the engine's own
loader instead of a proxy DLL is that the proxy-DLL heuristic that flags
`version.dll`-style loaders never applies.

## How it works

1. The patcher adds one extension to `data.win`:
   - a single file entry pointing at our DLL (`coreloader_ext.dll`), `Kind = Dll`;
   - one declared function (`coreloader_extension_probe`) bound to an exported
     symbol in our DLL.
2. At start-up the GameMaker runner initialises the extension and
   `GetProcAddress`-resolves that function, which first requires it to
   `LoadLibrary` our DLL.
3. That load fires `DllMain(DLL_PROCESS_ATTACH)`, which already boots the whole
   loader on its own thread — see `src/dllmain.cpp`. Nothing else is needed;
   `coreloader_extension_probe` is never called, it only has to exist so the
   resolve succeeds (`src/extension_entry.cpp`).

Editing `data.win` by hand is unsafe: strings and data are referenced by
absolute file offsets throughout the file, so inserting bytes shifts everything
after the `EXTN` chunk and corrupts the file. The patcher therefore drives
**UndertaleModTool's** headless CLI, which re-serialises `data.win` correctly
for the game's GameMaker version.

## Requirements

- **UndertaleModTool** (open-source GameMaker editor) — download a release from
  <https://github.com/UnderminersTeam/UndertaleModTool/releases> and note the
  path to `UndertaleModCli.exe` inside it.
- The **extension-enabled loader build**. Rebuild the native DLL after pulling
  these changes (adds `src/extension_entry.cpp`'s export):

  ```powershell
  $vs='C:\Program Files\Microsoft Visual Studio\18\Community'
  cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul && `"$vs\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`" --build build --config Release"
  ```

  The managed side still deploys the usual way (`tools\deploy-coreloader.ps1`);
  only the *loading* of `version.dll` is replaced by this route. The DLL copied
  into the game is the same `build\version.dll`, just under a different name.

## Usage

Install (on a disposable copy of the game first):

```powershell
cd tools\data-extension
.\Install-Extension.ps1 `
    -GameDir "D:\Games\StoneshardCopy\Stoneshard" `
    -Dll ..\..\build\version.dll `
    -UtmtCli "C:\Tools\UndertaleModTool"
```

Then launch the game and check `<GameDir>\Lodestone\Logs\lodestone.log` for the
`=== Lodestone ===` banner.

Revert (restores `data.win` from the backup the script made and removes the DLL):

```powershell
.\Install-Extension.ps1 -GameDir "D:\Games\StoneshardCopy\Stoneshard" -Revert
```

### Options

| Parameter        | Default                      | Meaning |
|------------------|------------------------------|---------|
| `-GameDir`       | *(required)*                 | Game folder (exe + data.win). |
| `-Dll`           | *(required to install)*      | Built loader DLL to stage. |
| `-UtmtCli`       | *(required to install)*      | `UndertaleModCli.exe` or its folder. |
| `-ExtensionName` | `CoreLoaderExt`              | Name recorded in data.win. |
| `-DllName`       | `coreloader_ext.dll`         | Filename in the game folder / data.win. |
| `-FuncName`      | `coreloader_extension_probe` | Exported symbol to bind. |
| `-DataWin`       | `<GameDir>\data.win`         | Explicit path if not at the default. |
| `-Revert`        | —                            | Undo. |

## Limitations / notes

- **Back up saves first** and test on a throwaway copy. The script backs up
  `data.win` to `data.win.lodestone-bak`, but it does not touch saves.
- **Steam "Verify integrity of game files"** restores the original `data.win`
  and so removes the extension; re-run the installer afterwards. (This also means
  a Steam game *update* reverts it — an installer/launch step would need to
  re-apply it for a shipped product. For the PoC, re-running is enough.)
- **GameMaker 2022.6+** stores one 16-byte product-id GUID per extension at the
  tail of `EXTN`. The script adds a matching GUID when (and only when) the game
  already keeps one per extension, so both a 2022.6+ game (Stoneshard) and a
  2024.x game (Dwarf Eats Mountain) round-trip correctly, while older games
  without product ids are left untouched. Verified by reloading the patched file.
- The declared function's calling-convention/kind is cloned from an existing
  extension function in the same `data.win`, so it matches the game's GameMaker
  build rather than relying on a hardcoded constant.
- This is a loading route only; everything above `DllMain` (mods, overlay, test
  host) is unchanged.
