---
slug: /
title: Lodestone
description: Lodestone (CoreLoader) is a mod loader that lets you write C# mods for any YYC-compiled GameMaker game.
---

Lodestone is a mod loader for GameMaker games compiled with YYC. You write mods in C#, and the loader runs them inside the game: they can hook any script or object event, call the game's own functions, read and write its variables, draw into its GUI layer and add their own tabs to an in-game overlay.

**Lodestone** is the name players see: the release zips, the install folder (`<game>\Lodestone\`), the log file and the overlay title. **CoreLoader** is the name in code: the `CoreLoader.dll` assembly, the `CoreLoader` namespace and the mod API. This documentation uses both in the same way.

## How it works

The loader is a `version.dll` placed next to the game's exe. Windows loads it in place of the system `version.dll`, and it forwards the real calls so the game does not notice.

On first launch it:

1. Finds the game's compiled GML (every script and object event is a native function in the exe) and the runtime helpers it needs, by pattern. Nothing is hardcoded per game, so a patch that moves addresses around costs nothing.
2. Hosts .NET 10 inside the game process.
3. Loads the C# mods from `<game>\Mods\`.
4. Generates a typed C# project from the running game (its scripts, objects, builtins and assets) that your mods can compile against.

A Dear ImGui overlay (press **INSERT**) shows the loader's status and one tab per mod.

## YYC games only

CoreLoader works with **YYC** builds, where the game's GML is compiled to native code in the exe. A **VM** build keeps its GML as bytecode in `data.win`, so there is nothing for the loader to find. In a VM game the loader stands down cleanly: it says so in its log (`not a YYC game`) and the game runs as usual. Mods still load, but they cannot touch the game.

## Tested games

| Game | Store | Build | GML functions (builtins) | Loader | Console mod | Notes |
|---|---|---|---|---|---|---|
| Stoneshard (Early Access) | Steam | YYC, older runtime | 34,167 (2,532) | yes | yes | The main target. The Stoneshard mods and the full `smoke-stoneshard.ps1` suite run here |
| Dwarf Eats Mountain Demo | Steam | YYC, runtime 2024.14 | 4,968 (2,861) | yes | yes | The second reference game, on the newer runtime |
| The Spike Cross | Steam | YYC | 48,206 | yes | yes | Steam relaunches it, which drops the test-host environment variable |
| The King is Watching 1.3.6 | GOG | YYC | 14,511 | yes | yes | Rebuilds its swap chain at startup; handled in the overlay |
| Zero Stress King | GOG | YYC | 1,833 | yes | yes | |
| Slime Trader | Steam | YYC | 1,478 | yes | yes | Steam relaunches it, which drops the test-host environment variable |
| Void War | GOG | **VM** | 0 | not supported | Loads, no game access | Detected as "not a YYC game"; the game runs unaffected |

"Yes" means the loader starts, proves its GML bridge with a live call, and generates interop. The overlay draws, and `tools\smoke-generic.ps1` passes all 29 of its checks in that game: builtin and script calls, globals, instance variables, object and event lookup, the Console mod's expressions and commands, hooking and unhooking a live event, and that game errors are reported without disabling a mod.

Tested with CoreLoader 0.4.0 plus the unreleased changes, on 2026-09-29. The two reference games are five years of GameMaker runtime apart, which is why the loader keeps [runtime differences](internals/runtime-differences.md) in view.

## Where to start

**Writing mods?** Read in this order:

1. [Getting started](modding/getting-started.md): install the template, build, deploy, hot reload.
2. [Your first mod](modding/first-mod.md): the template and the smallest example, line by line.
3. [Concepts](modding/concepts.md): the rules every mod lives by (game thread, value lifetime, ownership, faults).
4. [Finding hooks](modding/finding-hooks.md): how to work out what to hook in a game you have never seen the source of.
5. [The cookbook](modding/cookbook/index.md): short recipes for hooks, game state, drawing, input and settings.

**Working on the loader itself?** Start with the [architecture overview](internals/architecture.md), then [how the loader boots](internals/boot.md). [Contributing](internals/contributing.md) covers the build, the test games and the conventions.

**Want to play with mods?** The player guide is [INSTALL.md on GitHub](https://github.com/Forevka/stoneshard-mod/blob/main/INSTALL.md).
