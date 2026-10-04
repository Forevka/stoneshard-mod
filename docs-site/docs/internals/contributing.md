---
title: Contributing
description: Building, deploying and testing Lodestone, the rules for extending the native and managed halves, and how changes are versioned and released.
---

Lodestone is Windows only: the loader is a 64-bit Windows DLL and the games it loads into are Windows builds.
Run the commands on this page from the repository root, in PowerShell.

## Repository layout {#layout}

| Path | What |
|---|---|
| `src/` | The native loader (`version.dll`). See [Architecture](./architecture.md) for a file-by-file map. |
| `managed/CoreLoader/` | The managed runtime mods reference: the public API, plus `Runtime/` (entry points, mod manager, interop generator). |
| `managed/Mods/` | The shipped mods. |
| `managed/Tests/` | Regression mods: ValueProbe, StructProbe, XpProbe, FaultyGuiMod, WidgetProbe. |
| `managed/Examples/`, `managed/Templates/CoreLoaderMod/` | Example mods and the `dotnet new coreloader-mod` template. |
| `managed/CoreLoader.Analyzers/` (and `.Tests`) | The Roslyn analyzer every mod compiles with. See [Analyzers](../modding/reference/analyzers.md). |
| `tools/` | Deploy, run, test-host and save-backup scripts; `tools/re/`, the offline reverse-engineering toolkit (see [How we found what to hook](./re-toolkit.md)). |
| `docs-site/` | This documentation site. |
| `CHANGELOG.md` | Every user-visible change to the loader (native, API, tools, test mods). |
| `managed/Mods/CHANGELOG.md` | Every user-visible change to the shipped mods. |

## Building {#building}

### Native {#build-native}

The native half is a CMake project built with Visual Studio's C++ toolchain. CMake ships inside Visual Studio and
is usually not on `PATH`, so run it through the developer environment. Adjust `$vs` to your Visual Studio install:

```powershell
$vs='C:\Program Files\Microsoft Visual Studio\18\Community'
$cmake="$vs\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
# once: configure a 64-bit build tree
cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul && `"$cmake`" -S . -B build -A x64"
# every build: produces build\version.dll
cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul && `"$cmake`" --build build --config Release"
```

### Managed {#build-managed}

```powershell
dotnet build managed\CoreLoader.sln -c Release
dotnet test managed\CoreLoader.Analyzers.Tests -c Release --no-build
```

The output lands under `managed\bin\Release\`. Mods written against a game's generated interop (FastTravel,
Reliquary) only build on a machine where that game has run with Lodestone installed. Run
`tools\setup-dev.ps1` once per machine (optionally with `-GameDir <folder>`): it points the build at your game
folders and writes `managed\CoreLoader.Dev.sln`, which also contains each game's interop project so Go To
Definition works in Visual Studio.

## Deploying {#deploying}

```powershell
# install the loader and some mods into a closed game
tools\deploy-coreloader.ps1 -GameDir "D:\Games\Stoneshard" -Mods Console,ScriptSpy
# remove other mod dlls first
tools\deploy-coreloader.ps1 -GameDir "D:\Games\Stoneshard" -Mods Console -CleanMods
# into a running game
tools\deploy-coreloader.ps1 -GameDir "D:\Games\Stoneshard" -Live
```

The script copies `build\version.dll` next to the game exe, the managed runtime into `<game>\Lodestone\`, and the
named mods into `<game>\Mods\`. A `version.dll` already there that is not Lodestone's is kept as `version.dll.bak`.

A running game locks `version.dll` and the runtime. Windows will not overwrite a loaded DLL, but it will rename
one, so `-Live` moves each locked file aside to `<name>.old` and puts the new build in its place. The running game
keeps executing the old image; the next launch picks up the new one. Mods are different: the runtime hot-reloads
a mod dll the moment it lands in `Mods\`, with or without `-Live`.

### Running a test game {#run-game}

`tools\run-game.ps1` stops the game, optionally deploys, relaunches it, and waits until this run's log reports
`mod(s) loaded`:

```powershell
tools\run-game.ps1 -GameDir "D:\Games\Some Game" -Deploy -Mods Console -CleanMods
tools\run-game.ps1 -GameDir "D:\Games\Some Game" -WaitFor 'StructProbe.*PASSED' -TimeoutSec 180
tools\run-game.ps1 -GameDir "D:\Games\Some Game" -TestHost
tools\run-game.ps1 -GameDir "D:\Games\Some Game" -Stop
```

Pass `-Exe <name>` when the folder holds more than one exe. Mods start later than `mod(s) loaded`, so wait for
`starting mods` or a line your mod logs. The scripts also accept `-Game Stoneshard|Dwarf`, a shortcut for the
maintainer's install folders; use `-GameDir` on your machine.

The log is `<game>\Lodestone\Logs\lodestone.log`, and the previous run's is `lodestone.prev.log`. Setting
`CORELOADER_DATA_DIR` moves the log and `imgui.ini` for one process.

## Test games {#test-games}

| Game | Why |
|---|---|
| Stoneshard | The older GameMaker runtime, and a large game (about 34,000 GML functions). Waits about 16 s for its assets before mods start. |
| Dwarf Eats Mountain Demo | Runtime 2024.14, the newer runtime family. |
| Other YYC games | Breadth. Run them with `run-game.ps1 -GameDir <dir> -TestHost`, then `tools\smoke-generic.ps1 -GameDir <dir>`, which knows no game-specific names. Games that Steam relaunches lose the environment variable; `-TestHost` also writes `Lodestone\testhost.enable` for them. |
| A VM-compiled game | The loader must stand down cleanly, logging "not a YYC game". |

### Validate every change in both runtimes {#both-runtimes}

The two runtime families differ in ways that break code which works in one of them:

- 2024 has static string initialisers.
- 2024 uses 24-byte builtin rows instead of 80-byte rows with inline names.
- 2024 has no current-self global; `self` comes from observed events.
- 2024 returns typed references where the older runtime returns numbers.
- In 2024, `COPY_RValue` is inlined, and only its `__Post` half is callable.

[Runtime differences](./runtime-differences.md) explains each one. Test every change in Stoneshard and in Dwarf
Eats Mountain.

## Testing {#testing}

### The test host {#test-host}

Drive the game through the test host rather than the mouse. `run-game.ps1 -TestHost` starts the game with
`CORELOADER_TEST=1`; then:

```powershell
tools\coreloader.ps1 -GameDir "D:\Games\Some Game" status
tools\coreloader.ps1 -GameDir "D:\Games\Some Game" builtin string_upper abc
tools\coreloader.ps1 -GameDir "D:\Games\Some Game" list-commands
```

Exit codes: 0 ok, 1 the command failed, 2 the game is unreachable, 3 no answer in time. Dot-source the script
for `Invoke-CoreLoader` and `Wait-CoreLoader`. The host never blocks a frame, so poll for anything that takes
time. See [Test host](../modding/reference/test-host.md) for the protocol and commands.

Stoneshard mods are tested through the StoneshardHarness mod (`tools\stoneshard.ps1`,
`tools\stoneshard_harness.py`), which walks, fights, trades and talks through the game's own scripts. Test on a
new Adventure character; never press Continue, which loads your latest real character.

### Regression mods {#regression-mods}

Deploy these after risky changes:

| Mod | Pass condition |
|---|---|
| ValueProbe | Private memory stays flat (no value leak). |
| StructProbe | The log says `PASSED`. |
| XpProbe | With StoneshardBoost at `xpMultiplier` 3: +300, then +600 with `CallOriginal`. |
| FaultyGuiMod | It throws with its own tab bar open. It shows as disabled, the game keeps running, and every tab after it still draws. |
| WidgetProbe | Every UI widget draws, and scopes unwind under faults. |

### Smoke tests {#smoke-tests}

`tools\smoke-dwarf.ps1` (core and Console commands), `tools\smoke-stoneshard.ps1` (every `cheats.*` command with
read-backs; needs a loaded save) and `tools\smoke-generic.ps1` (any YYC game). Each prints PASS or FAIL per
check.

### Save backups {#save-backups}

:::danger
Tests that load a Stoneshard save can overwrite it. Back up first, and kill the game without saving afterwards.
:::

```powershell
$b = tools\game-saves.ps1 backup -Game Stoneshard   # prints the backup path
tools\game-saves.ps1 restore -From $b
tools\game-saves.ps1 verify -From $b                # exit 1 on any difference
```

Restore deletes only inside `character_N` folders, and refuses a folder without its marker or without characters
unless `-Force`. Backups go to `-BackupRoot` if you pass it, else `$env:CORELOADER_BACKUP_ROOT`, else a
git-ignored folder inside the repository.

## Extending the native loader {#extending-native}

### Adding a CoreApi entry {#adding-coreapi-entry}

A new native capability for the managed side is a new field in the `CoreApi` table:

1. Append the field at the **end** of `struct CoreApi` in `src/host/core_api.h`. Never reorder or remove fields.
   Bump `kCoreApiVersion` and note the change in its comment.
2. Implement it in `core_api.cpp` and assign it in `Build()`. Anything that touches GML starts with
   `GameThreadOnly("name")`, which refuses and logs any call made off the game thread (see
   [.NET host](./dotnet-host.md#game-thread-only)).
3. Mirror it at the end of `managed/CoreLoader/Native/CoreApi.cs`, and bump `ExpectedVersion`.
4. Wrap it in a public API (for example in `UI.cs`). A widget that opens a scope must be tracked: add a `Scope`
   value, a `CloseNative` case, `Open.Add`, and a `Close(...)`-based end method.
5. Rebuild **both** halves. A version mismatch makes the managed runtime refuse to start, by design: calling
   through a field the other side does not have would corrupt the game instead of failing.

### Native pattern discovery {#pattern-discovery}

When the loader needs a new runtime helper:

- **Vote, validate, prove.** Vote across many functions, validate the winner structurally, then prove it by
  behaviour before use (see `VerifyValueLifetime` in `src/gml.cpp`).
- **Fail closed.** A missing helper means the feature is unavailable. It is never a guess.
- **Stay game-agnostic.** No game names, addresses or offsets in `src/`. Anything game-specific is a C# mod
  gated with `[CoreModGame]`.

[Architecture](./architecture.md) explains why.

### Calling into the game natively {#calling-natively}

- Use `gml::CallAs`, `gml::CallEvent` or `builtins::Call`. They are guarded against SEH faults. Object events
  have a smaller signature than scripts; calling one as a script corrupts the stack, so events always go through
  `CallEvent`.
- Strings handed to the game must be permanent: use `gml::Intern`, static storage, or the per-name cache. The
  runtime keeps pointers to the characters.
- Never let a C++ or GML exception unwind through .NET frames; it terminates the process. The guards handle
  this. Keep a `gml::ManagedScope` around any new managed entry point, so calls made under it catch.

## Extending the managed API {#extending-managed}

- Public classes are `static` facades over `Loader.Api`. Call `Loader.EnsureGameThread()` at the top of anything
  that touches GML.
- Values returned from builtins go through `Values.Track`, so they join the per-frame pool and are released at
  the end of the frame.
- Anything a mod registers (hooks, draw handlers, content, picks, kept values, queued actions) belongs to that
  mod. Find the owner with `ModManager.OwnerOf(delegate)` or `ModManager.Current`, and add a `RemoveOwner` call
  to `ModManager.RemoveRegistrations` so it is torn down on unload, fault and hot reload.
- A new shipped mod goes in `managed/Mods/<Name>/` with a two-line csproj (`Mods/Directory.Build.props` wires
  the rest), is added to `managed/CoreLoader.sln` with `dotnet sln add`, and gets a row in the [shipped mods table](../modding/reference/shipped-mods.md).

## Code conventions {#conventions}

- Comments explain *why*, in full sentences. Match the comment density of the file you edit.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/) with the scope
  `NOTICKET, <area>`, for example `fix(NOTICKET, trials): settle trials left by the stairs`.
- Non-trivial changes get an independent review before they are merged; fix what it finds and re-test in both
  runtimes.
- Git warns about CRLF line endings; that is expected (files are LF in the index).

## Versioning {#versioning}

The loader follows semantic versioning. Its version is `<Version>` in `managed/CoreLoader/CoreLoader.csproj`,
shown in the log and on the overlay's Loader tab.

- While on 0.x, a breaking API or ABI change bumps the **minor** version; a fix bumps the patch.
- Bumping `kCoreApiVersion` is breaking for anything built against the old table.
- Every change users or mod authors can see goes under `## [Unreleased]`: in `CHANGELOG.md` for the loader, in
  `managed/Mods/CHANGELOG.md` for a shipped mod. A release moves both sections under its version heading and
  bumps the csproj `<Version>`.

## Releasing {#releasing}

Pushing a tag `v<Version>` that matches the csproj runs `.github/workflows/release.yml`. It builds the native
loader, the managed runtime and the mods, runs the analyzer tests, packs the zips with
`tools\package-release.ps1 -BundleRuntime`, and creates a **draft** release whose notes are the matching section
of `CHANGELOG.md`, followed by the same version's section of `managed/Mods/CHANGELOG.md` under **Mods**. A tag that does not match `<Version>` fails the workflow.

Mods built against a generated interop cannot be built on the CI runner, which has no game. Add them from a
machine with Stoneshard installed, then publish the draft:

```powershell
dotnet build managed\CoreLoader.sln -c Release
tools\package-release.ps1 -Mods FastTravel,Reliquary -NoLoader -Upload v<Version>
```

## Working on these docs {#docs}

The site lives in `docs-site/` and is built with Docusaurus (Node 20 or later):

```powershell
cd docs-site
npm install
npm start
```

Pages are Markdown parsed as MDX: escape `<` and `{` in prose, or wrap them in backticks. Quoted code carries a
source link with line anchors, so update the anchors when the code moves.
