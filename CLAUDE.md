# CoreLoader: working notes for Claude

CoreLoader is a mod loader for **any YYC-compiled GameMaker game**. A `version.dll` proxy (native
C++, `src/`) finds the game's compiled GML and runtime helpers by pattern, hosts .NET 10, and
loads C# mods (`managed/`). It began as a Stoneshard-only native cheat mod; those tools are now
the `StoneshardCheats` C# mod, and `src/` holds no game-specific code.

- Player-facing brand: **Lodestone** (install folder `<game>\Lodestone\`, `lodestone.log`, overlay title,
  release zips). The code, assembly and API stay CoreLoader. Native: `paths::kInstallFolder`.
- Releases: `tools\package-release.ps1` + `.github/workflows/release.yml` (draft release on a `v*` tag;
  interop mods uploaded locally with `-Mods FastTravel,Reliquary -NoLoader -Upload v<ver>`).
- User-facing guide: the documentation site in `docs-site/` (Docusaurus; mod authoring, cookbook, API, loader internals), published to GitHub Pages by `.github/workflows/docs.yml`. `README.md` is the project overview; `managed/README.md` is a stub pointing at the site.
- History: `CHANGELOG.md` (the loader: native, API, tools, test mods) and `managed/Mods/CHANGELOG.md` (the shipped
  mods). **Update the right one with every user-visible change** (see Versioning).

## Layout

| Path | What |
|---|---|
| `src/` | Native loader. `dllmain.cpp` (init thread), `symbols.cpp` (gml_* table), `gml.cpp` (runtime bridge: strings, calls, value free/copy, self-tests), `builtins.cpp` (builtin registry), `hookengine.cpp` (thunk detours for managed hooks and the loader's own self observers), `objtypes.cpp` (objects defined at runtime: located, proven, then built like the runner's own), `overlay.cpp` (ImGui, WndProc, pick mode, per-frame tick), `host/` (.NET hosting, `core_api.h/.cpp` = the C ABI), `hooks.cpp` (D3D11 Present hook), `proxy.cpp`, `paths.cpp`, `log.cpp` |
| `managed/CoreLoader/` | The runtime mods reference: `Game`, `Hooks`, `Values`, `RValue`, `Globals`/`GmlObject`/`InstanceRef`, `ObjectTable`, `DsMap`/`DsList`, `UI`, `Content`, `GameDraw`, `Input`, `Code`, `ModConfig`, `ModSettings`, `ObjectTypes`; `Runtime/` = entry points, mod manager (hot reload), interop generator |
| `managed/Mods/` | Shipped mods (Console + Inspector/Objects/Globals, ScriptSpy, SpeedControl, ContentDemo, DwarfBoost, StoneshardBoost, StoneshardCheats, Reliquary - the interop-only artifacts mod, FastTravel - world-map fast travel drawn with the game's own UI pieces, TavernGames - dice/card minigames against tavern NPCs on a small `MiniGame` framework, StoneshardTrials - roguelike tavern-hub/random-dungeon loop, ModMenu - the Esc-menu MODS window for `ModSettings`, StoneshardHarness - `hx.*` test-host commands to play Stoneshard without screenshots, clients `tools\stoneshard.ps1` / `tools\stoneshard_harness.py`) |
| `managed/Tests/` | Regression mods: ValueProbe, StructProbe, XpProbe, FaultyGuiMod, WidgetProbe (every UI widget, and scope unwind under faults), ObjectTypeProbe (objects defined at runtime: events, inheritance, collisions, hot-reload teardown) |
| `managed/Examples/`, `managed/Templates/CoreLoaderMod/` | HelloMod, InteropExample; the `dotnet new coreloader-mod` template |
| `managed/CoreLoader.Analyzers/` (+ `.Tests`) | Roslyn analyzer every mod compiles with: CL0001-CL0003 lifetime rules, CL0004-CL0005 game declaration (see `docs-site/docs/modding/reference/analyzers.md`) |
| `tools/` | `deploy-coreloader.ps1`, `setup-dev.ps1`, `run-game.ps1`, `game-saves.ps1`, `coreloader.ps1` (test-host client), `stoneshard.ps1` / `stoneshard_harness.py` (StoneshardHarness clients), `smoke-*.ps1`, `checkcksum.py`; `re/` = static RE toolkit over any game's exe (`relib.py` and friends, `--exe <game.exe>` or `RE_GAME_EXE`; `ghidra/ExportGml.java`) |
| `docs-site/` | Docusaurus documentation site: `docs/modding/` (guide, cookbook, walkthroughs, reference) and `docs/internals/` (how the loader works). `npm start` / `npm run build`; a broken link fails the build. Update it with every user-visible API change |

## Build, deploy, run

Windows only. **Use PowerShell for anything with a path.** `cmake` is not on PATH; use the VS one:

```powershell
# native (build\version.dll)
$vs='C:\Program Files\Microsoft Visual Studio\18\Community'
cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul && `"$vs\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`" --build build --config Release"
# managed (managed\bin\Release\...)
dotnet build managed\CoreLoader.sln -c Release
# install (game closed); -CleanMods removes other mod dlls first
tools\deploy-coreloader.ps1 -GameDir "<game>" -Mods Console,DwarfBoost -CleanMods
# into a RUNNING game: locked files are renamed to *.old and the new ones take effect on next launch
tools\deploy-coreloader.ps1 -GameDir "<game>" -Live
# one-time per machine: lets interop-based projects build and navigate in VS (opens CoreLoader.Dev.sln)
tools\setup-dev.ps1 -GameDir "<extra game folder>"
```

Test games:
- **Dwarf Eats Mountain Demo** (runtime 2024.14): `D:\SteamLibrary\steamapps\common\Dwarf Eats Mountain Demo`.
- **Stoneshard** (older runtime): `D:\torrent\Stoneshard (Early Access)\Stoneshard`.
- Extra YYC games, for breadth (`run-game.ps1 -GameDir <dir> [-Exe <name>] -TestHost`, then
  `tools\smoke-generic.ps1 -GameDir <dir>`): `D:\torrent\Zero Stress King`,
  `D:\torrent\The.King.is.Watching.v1.3.6` (`-Exe 'The King is Watching.exe'`; it rebuilds its swap
  chain at startup), and Steam's `TheSpikeCross` and `Slime Trader` (Steam relaunches them, so the
  test host comes on through `Lodestone\testhost.enable`). `D:\torrent\Void.War.Build.25426981\...` is
  VM-compiled: the loader must stand down cleanly there ("not a YYC game").

Log: `<game>\Lodestone\Logs\lodestone.log`; the previous run's is `lodestone.prev.log`.
`CORELOADER_DATA_DIR` moves the log (and imgui.ini) per process; `SSMOD_DATA_DIR` is its old name,
still read as a fallback for one release.

**Validate every change in both games.** Their runtimes differ:
- 2024 has static string initialisers.
- 2024 uses 24-byte builtin rows instead of 80-byte inline names.
- 2024 has no current-self global; self comes from observed events.
- 2024 returns typed refs where the older runtime returns numbers.
- In 2024, `COPY_RValue` is inlined, and only its `__Post` half is callable.

Testing notes:
- **Stoneshard waits ~16 s** for its assets before mods start.
- `tools\run-game.ps1 -Game Stoneshard|Dwarf [-Deploy -Mods A,B -CleanMods] [-WaitFor <regex>] [-TimeoutSec N]`
  kills the game, deploys, relaunches, and waits for this run's log to say `mod(s) loaded` (mods
  start later: wait for `starting mods` or your own line). `-Stop` only kills it.
- `$b = tools\game-saves.ps1 backup -Game Stoneshard` (prints the backup path); afterwards
  `tools\game-saves.ps1 restore -From $b` and `verify -From $b` (exit 1 on any difference). Restore
  deletes only inside `character_N` folders (ones missing from the backup, and files the backup's
  copy lacks), and refuses a folder without its marker or without characters unless `-Force`.
  Backups go to `.omc\save-backups` (or `$env:CORELOADER_BACKUP_ROOT`).
- **Drive the game through the test host, not the mouse.** `tools\run-game.ps1 ... -TestHost` starts the
  game with `CORELOADER_TEST=1`; then `tools\coreloader.ps1 -Game Stoneshard|Dwarf <cmd> [args]` (exit 0
  ok, 1 command failed, 2 game unreachable, 3 no answer in time), or dot-source it for `Invoke-CoreLoader` / `Wait-CoreLoader`.
  `list-commands` lists everything: core (`status`, `log`, `reload`, `call`, `builtin`, `global-*`,
  `instance-*`, `object-count`, `wait-frames`), `console <line>`, and `cheats.*`. Poll for anything that
  takes time; the host never blocks a frame. Protocol: `docs-site/docs/modding/reference/test-host.md`.
- **Stoneshard mods are tested by agents through the StoneshardHarness mod, not screenshots.**
  Deploy it with the mod under test, and play through `tools\stoneshard.ps1`:
  - observe: `state`, `player`, `enemies`, `objects`, `inventory`, `log`, `dialogue`;
  - act: `goto`, `attack`, `interact`, `use`, `wait`, `say`, `press`;
  - the same commands as `hx.*` over the test host, and in `tools\stoneshard_harness.py`.

  It walks, fights, trades, loots and talks through the game's own scripts. Only picking things up
  off the ground falls back to a click. Full reference: `managed/Mods/StoneshardHarness/README.md`.
  Test on a **new Adventure character** (title -> Play -> New Game -> Adventure). Never press
  Continue, which loads the user's latest real character. Mods add their own setup commands
  (StoneshardTrials: `tr.*`).
- Smoke tests: `tools\smoke-dwarf.ps1` (core + Console) and `tools\smoke-stoneshard.ps1` (every `cheats.*`
  command, with read-backs; needs a loaded save; never let the game save afterwards). PASS/FAIL per check.
- To load a save: first **back up `%LOCALAPPDATA%\StoneShard`** (`game-saves.ps1 backup`). Then at 1920x1080: title, space, Play (1660,552), Continue (1660,552), and wait ~30 s. **Kill the game without saving** afterwards.
- UI automation is Python + pyautogui (`C:\Python314\python.exe`). ImGui needs a slow click: mouse down, ~150 ms, mouse up. Screenshot, read the image, act.
- Regression mods to deploy after risky changes:
  - ValueProbe: private memory must stay flat.
  - StructProbe: must log `PASSED`.
  - XpProbe with StoneshardBoost at `xpMultiplier` 3: +300, then +600 with CallOriginal.
  - ObjectTypeProbe: must log `PASSED` in both games, and again after copying it in while the game runs.

## Creating a mod

A mod is a class library referencing CoreLoader. It either lives in `managed/Mods/<Name>/` (a
two-line csproj, since `Mods/Directory.Build.props` wires CoreLoader and the output) or comes from the
template (`dotnet new coreloader-mod`).

```csharp
[assembly: CoreModInfo(typeof(MyMod.Main), "My Mod", "1.0.0", "Author")]
[assembly: CoreModGame("StoneShard")]           // required: these games (exe name), or [assembly: CoreModAnyGame]
public sealed class Main : CoreMod
{
    public override void OnInitialize() { }      // game thread, once the game's assets are loaded
    public override void OnUpdate() { }          // every frame (Present)
    public override void OnGUI() { }             // inside the mod's overlay tab (UI.*)
    public override void OnShutdown() { }        // unload, hot reload, or game exit
    [HookBefore("scr_get_XP")] void Xp(HookCall c) => c.SetArg(0, c.GetArg(0).AsReal * 2);
}
```

Rules the runtime enforces, and that mods must respect:
- **Game thread only.** Every callback runs there. From a Task or timer, use `Game.RunOnGameThread`.
  Native API calls from other threads are refused.
- **Values:**
  - Strings and arrays from the game are pooled and released at the end of the frame.
  - Keep one across frames with `Values.Keep`, and later `Values.Free`.
  - Never free what the game lends you (hook arguments or result).
  - Kept structs are rooted in the global `__coreloader_roots`.
- **Ownership:** hooks, draw handlers, content, picks, kept structs and queued actions belong to the
  registering mod (from its load context, even in constructors or Tasks). They are torn down on
  unload, fault or hot reload.
- **UI:**
  - Scopes are tracked. A mod that leaves one open is faulted.
  - Use `UI.Guarded(draw, onError)` around code that reads the live game.
  - Use `###stableId` for labels that carry live values.
- **Faults:** an exception in a callback disables only that mod, until it is reloaded. It never
  takes down the game. Mods reading the live game should still catch and show errors themselves.
- **Content files** (PNG/OGG) go in `Mods/<ModName>/`. `Content.AddSprite/ReplaceSprite/AddSound`,
  `GameDraw.OnGui` draws into the game's GUI layer.
- **Interop:** a mod written against a game's generated interop sets `<InteropGame>Game_Name</InteropGame>`
  (repo projects), or references the interop csproj (template). Generated at
  `<game>\Lodestone\Interop\<Game>.Interop\`.

Deploy a repo mod with `tools\deploy-coreloader.ps1 -Mods <Name>`, or copy the dll into a running
game's `Mods\` for **hot reload**. Add it to `managed/CoreLoader.sln` (`dotnet sln add`) and to
the mods table in `docs-site/docs/modding/reference/shipped-mods.md`.

## Extending CoreLoader

**Adding a CoreApi entry** (a native capability the managed side calls):
1. Append the field at the **end** of `struct CoreApi` in `src/host/core_api.h`. Never reorder
   or remove fields. Bump `kCoreApiVersion` and note it in the version comment.
2. Implement it in `core_api.cpp`, and assign it in `Build()`. Anything that touches GML starts
   with `GameThreadOnly("name")`.
3. Mirror it at the end of `managed/CoreLoader/Native/CoreApi.cs`, and bump `ExpectedVersion`.
4. Wrap it in a public API (e.g. `UI.cs`). A widget that opens a scope must be tracked: add a `Scope`
   value, `CloseNative` case, `Open.Add`, and a `Close(...)`-based end method.
5. Rebuild **both** sides. A version mismatch makes the managed runtime refuse to start (by design).

**Native pattern discovery** (finding a runtime helper):
- Vote across many functions, then validate the winner structurally, then prove it by behaviour
  (see `VerifyValueLifetime`).
- Fail closed: a missing helper means the feature is unavailable. It is never a guess.
- Native code stays game-agnostic. Anything game-specific is a C# mod gated with `[CoreModGame]`.

**Calling into the game natively:**
- Use `gml::CallAs` / `CallEvent` / `builtins::Call`. They are SEH-guarded.
- Strings handed to the game must be permanent: use `gml::Intern`, static storage, or the per-name
  cache. The runtime keeps pointers to them.
- Never let a C++/GML exception unwind through .NET frames. The guards handle this; keep
  `gml::ManagedScope` around any new managed entry point.

**Managed APIs:**
- Public classes are `static` facades over `Loader.Api`. Call `Loader.EnsureGameThread()` at the top.
- Values returned from builtins go through `Values.Track`.
- Register ownership via `ModManager.OwnerOf(delegate)` or `ModManager.Current`, and add a
  `RemoveOwner` to `ModManager.RemoveRegistrations`.

## Conventions

- Comments explain *why*, in full sentences, as the surrounding code does. Match the density of the
  file you edit.
- Commits: Conventional Commits with scope `NOTICKET, <area>`, made with the `commit` skill
  (`python C:\Users\forevkassh\.claude\skills\commit\scripts\commit.py -m ... -m ...`).
  The user lets Claude commit. **No AI attribution anywhere** (commit messages, PRs, code, docs).
  Push only when asked. Branch: `feat/managed-mod-loader`.
- Non-trivial work gets an independent review pass (a code-reviewer subagent) before it is called
  done. Fix what it finds, re-test in both games, then commit.
- CRLF warnings from git are expected (files are LF in the index).

## Versioning

- Semantic versioning. The loader's version is `<Version>` in
  `managed/CoreLoader/CoreLoader.csproj`, shown in the log and Loader tab.
- While on 0.x: breaking API or ABI changes bump the **minor** version, fixes bump the patch.
  Bumping `kCoreApiVersion` is breaking for anything built against the old table.
- Every change that users or mod authors can see goes under `## [Unreleased]` in `CHANGELOG.md` (loader) or
  `managed/Mods/CHANGELOG.md` (a shipped mod); the release workflow puts both in the release notes.
  A release moves it under a version heading and bumps the csproj `<Version>`.
