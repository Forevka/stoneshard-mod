---
title: Getting started
description: Install the mod template, build a mod against a game's generated interop, and get it running with hot reload.
---

This page takes you from an installed loader to a mod that rebuilds into a running game. The next page, [Your first mod](first-mod.md), explains the code the template gives you.

## Prerequisites

- **Windows.** The loader is a Windows `version.dll`.
- **The .NET 10 SDK** to build mods. Players only need the .NET 10 runtime, and not even that when the release zip ships it privately in `Lodestone\dotnet\` (the release zips do, via `tools\package-release.ps1 -BundleRuntime`).
- **A YYC-compiled GameMaker game with Lodestone installed.** See the [tested games](../index.md#tested-games) and [INSTALL.md](https://github.com/Forevka/stoneshard-mod/blob/main/INSTALL.md) for installing from a release. To build the loader yourself, see [Working in this repository](#working-in-this-repository) below and [Contributing](../internals/contributing.md).

## What an install looks like

After installing, the game folder contains:

```text
<game>\version.dll                 the loader (a proxy for the system version.dll)
<game>\Lodestone\CoreLoader.dll    the .NET runtime side, plus its runtimeconfig
<game>\Lodestone\Interop\          generated per game (see below)
<game>\Mods\*.dll                  your mods; Mods\<Name>.json holds their settings
```

Two more things live under `Lodestone\`: `Analyzers\` (the compile-time checks the template uses) and `Logs\` (the log). A mod may also be a folder, `Mods\<Name>\<Name>.dll`, which is how mods with content files are laid out.

## The mod-author workflow

1. **Install Lodestone and start the game once.** On that first run the loader writes `Lodestone\Interop\<Game>.Interop\`, a buildable project with typed refs to everything the game has. [Interop](interop.md) describes what it contains. It is regenerated when the game exe changes, and after the variable harvester learns new variables (on the next launch, or immediately with **Regenerate interop now** in the overlay's Loader tab).
2. **Create the mod from the template.** The template references CoreLoader and the interop, and deploys every build into the game.
3. **Build.** With the game running, the build is hot-reloaded.

### Install the template

From a clone of the repository:

```powershell
dotnet new install managed\Templates\CoreLoaderMod
```

This registers the template `coreloader-mod` ("CoreLoader mod"). Its parameters:

| Parameter | Meaning |
|---|---|
| `--gameDir` | The game's folder: the one holding the exe, `version.dll` and `Lodestone\`. The project references `CoreLoader.dll` from its `Lodestone` folder and copies every build into its `Mods` folder |
| `--gameName` | The game's exe name without extension, such as `StoneShard`. Restricts the mod to that game. Leave it empty for a mod that runs in any game |
| `--interop` | The generated interop's namespace, the folder name in `Lodestone\Interop\<interop>.Interop`, such as `StoneShard` or `Dwarf_Eats_Mountain`. Leave it empty to write the mod without interop |
| `--modAuthor` | The author in `[CoreModInfo]` (default `Me`) |

Create a project in a folder named after it:

```powershell
dotnet new coreloader-mod -n MyMod --gameDir "<game folder>" --gameName "<Exe name>" --interop <Game_Interop_Namespace>
dotnet build MyMod
```

What the generated declaration is depends on the parameters: with `--gameName` the mod declares `[CoreModGame("<Exe name>")]`; with only `--interop` it declares the interop's game; with neither it declares `[CoreModAnyGame]`. [Your first mod](first-mod.md) covers the rules behind that.

### What the generated project does

The template's `MyMod.csproj` targets `net10.0` and does four things worth knowing:

- Sets `GameDir` and `CoreLoaderDir` (`$(GameDir)\Lodestone`), and references `CoreLoader.dll` from there with `<Private>false</Private>`. The loader supplies CoreLoader at runtime, so it is never shipped with your mod.
- With `--interop`, adds a `ProjectReference` to `Lodestone\Interop\<interop>.Interop\<interop>.Interop.csproj`.
- Loads the analyzer `Lodestone\Analyzers\CoreLoader.Analyzers.dll` when it exists. It runs in the compiler only (see [analyzers](reference/analyzers.md)).
- Defines a `DeployToGame` target after `Build` that copies the mod's dll and pdb, the dependencies built next to it (the interop dll, NuGet packages) and any content into `$(GameDir)\Mods`. Turn it off with `-p:DeployOnBuild=false`.

Files you put in the project's `content\` folder are copied to `Mods\MyMod\...`, where `Content.AddSprite("x.png")` and friends look for them.

### Build, deploy and hot reload

With the game closed, `dotnet build` copies the mod into `<game>\Mods\` and it loads at the next launch.

With the game running, the loader watches `Mods\`: a rebuilt mod is swapped in between frames. The old copy gets `OnShutdown`, its hooks and config are released, and the new copy is loaded and started. The Loader tab in the overlay also has **Reload** buttons, **Reload all**, and a checkbox to turn hot reload off. The details, including what happens when the new build cannot load yet, are in [Concepts](concepts.md#hot-reload).

The same applies to a mod that `tools\deploy-coreloader.ps1 -Mods` copies into a running game: it is reloaded at once, against the runtime already running. `-Live` defers only `version.dll` and the runtime to the next launch.

### The log and the overlay

The log is `<game>\Lodestone\Logs\lodestone.log`. The previous session's log is kept as `lodestone.prev.log`, so a crash's trail survives the next launch. An identical line repeated many times a second is written a few times, then summarised. Set `CORELOADER_DATA_DIR` to move the log (and `imgui.ini`) for a process.

The overlay opens with **INSERT**. Its **Loader** tab shows the game, the number of GML functions and builtins, whether the GML bridge is ready, how many functions are hooked, the interop status, and one row per mod with its state. A tab per running mod follows; that is where your `OnGUI` draws. A mod that threw shows as disabled with the reason.

## Working in this repository

Mods that ship in this repository do not use the template. A mod under `managed\Mods\<Name>\` is a two-line csproj, because `Mods\Directory.Build.props` wires CoreLoader, the analyzer and the output folder. Add a new one to the solution with `dotnet sln add`, and to the mods table in `managed\README.md`.

### Interop projects and Visual Studio

Projects here that use a game's interop, such as `Examples\InteropExample`, name the game with an MSBuild property:

```xml
<InteropGame>Dwarf_Eats_Mountain</InteropGame>
```

The build finds that game's generated interop by itself. It only needs to know where your games are. Run this once per machine:

```powershell
tools\setup-dev.ps1                                    # finds games with Lodestone installed in your Steam libraries
tools\setup-dev.ps1 -GameDir "D:\Games\Stoneshard"     # plus any other folder
```

It writes two files, both kept out of git:

- `managed\CoreLoader.user.props`, which lists the game folders in `CoreLoaderGameDirs`. Every build reads it, in Visual Studio and with `dotnet build`. You can edit it by hand; `CoreLoader.user.props.example` shows the format (separate folders with `;`).
- `managed\CoreLoader.Dev.sln`, which is `CoreLoader.sln` plus each game's generated `<Game>.Interop` project. Open this one to build interop-based projects and to *Go To Definition* straight into the generated source.

Without either file the solution still loads and builds: an interop-based project compiles nothing and gives a warning saying what to run. The environment variable `CORELOADER_GAME_DIRS` works in place of the props file (on a build machine, say), and `-p:InteropProject=<path>` still overrides everything. Re-run `setup-dev.ps1` after installing Lodestone into another game.

### Deploying from the repository

`tools\deploy-coreloader.ps1` installs the loader and chosen mods into a game folder:

```powershell
tools\deploy-coreloader.ps1 -GameDir "<game folder>" -Mods Console,ScriptSpy
```

| Switch | Effect |
|---|---|
| `-Mods A,B` | Copies those built mods into `<game>\Mods\` |
| `-CleanMods` | Removes other mod dlls first, so the folder holds exactly what you named |
| `-Live` | Installs into a **running** game. Windows will not let a loaded dll be overwritten, so each locked file is renamed to `*.old` (or `*.<timestamp>.old` while an earlier one is still locked) and the new file takes its name; it takes effect on the next launch. Leftover `.old` files are deleted on the next deploy that can |
| `-Configuration` | Build configuration to copy from (default `Release`) |

The runtime is copied before `version.dll`, so a failed deploy never pairs a new `version.dll` with an old runtime. Without `-Live`, a running game stops the deploy before it copies anything. `-Live` only defers the native and runtime files: mods copied into `Mods\` are hot-reloaded at once by the runtime that is already running, whatever its version. An install from before the rename (a `CoreLoader\` folder) is moved to `Lodestone\` by the next deploy, generated interop and logs included.

Build everything first:

```powershell
cmake --build build --config Release        # native loader: build\version.dll
dotnet build managed\CoreLoader.sln -c Release
```

`cmake` is not on PATH on a stock Visual Studio install; [Contributing](../internals/contributing.md) has the exact command.

## Next

- [Your first mod](first-mod.md) reads the template's code and the `HelloMod` example top to bottom.
- [Concepts](concepts.md) explains the rules the runtime enforces.
- [Interop](interop.md) explains the generated project you referenced.
