---
title: Troubleshooting
description: For mod authors, where the logs are and what the loader's messages mean when a mod does not load, faults, or does not hot reload.
---

This page is for people writing mods. Players' problems (INSERT does nothing, antivirus, crash
reports) are in the
[INSTALL.md troubleshooting section](https://github.com/Forevka/stoneshard-mod/blob/main/INSTALL.md#troubleshooting).

## Where the logs are {#logs}

| File | What |
|---|---|
| `<game>\Lodestone\Logs\lodestone.log` | This run |
| `<game>\Lodestone\Logs\lodestone.prev.log` | The previous run, kept so a crash's trail survives the next launch |
| `<game>\Lodestone\Logs\testhost.pipe` | The test host's pipe name, when it is on |

An identical line repeated many times a second is written a few times, then summarised.

Set the environment variable `CORELOADER_DATA_DIR` to move the log (and `imgui.ini`) for one process,
which is useful when running two copies of a game. `SSMOD_DATA_DIR` is its old name, still read as a
fallback.

Every mod log line is tagged with the mod's name (`Log.Info`, `Log.Warning`, `Log.Error` from inside a
mod). The first lines of a run tell you the loader came up:

```text
Lodestone <version> on .NET <version>, game '<exe name>', <n> GML functions
```

If you started the test host, there is also `test host ON`. From a script, `tools\coreloader.ps1 -Game <name> log 50` returns the last lines without opening the file ([test host](test-host.md)).

Mods start only after the game has loaded its assets. The log says `waiting for the game's assets before starting mods` and later `starting mods after <n> s`. Stoneshard takes about 16 s; a game that
never answers starts its mods after about 30 s. Until then a mod's tab says "Starts once the game has
loaded its assets", which is not a fault.

## A mod is faulted {#faulted}

An exception in any mod callback (`OnInitialize`, `OnUpdate`, `OnGUI`, `OnShutdown`, a hook handler)
disables **only that mod**, until it is reloaded. Its hooks, draw handlers and picks are removed. The
game carries on. See [faults](../concepts.md#faults).

How it shows:

- In the overlay's **Loader** tab, the mod is listed in red as
  `<name> <version> by <author> - disabled: <reason>`, and its own tab shows `Disabled - <reason>`.
- In the log, one error line, with the exception:

  ```text
  <callback>: <ExceptionType>: <message> - the mod is disabled until it is reloaded
  ```

  where `<callback>` is `OnInitialize`, `OnGUI`, `hook attributes` or similar.

- Over the test host, `status` and `mods` list the mod's state and fault.

The special case is a UI scope left open:

```text
OnGUI left 1 UI scope(s) open (missing End/Pop)
```

Every `UI.Begin*`/`Push*` needs its `End*`/`Pop*`, also on the early-return and exception paths. If
the code between them can throw, wrap it in `UI.Guarded(draw, onError)`, which closes any scope it
left open.

To recover, fix the code and rebuild (a [hot reload](#hot-reload) restarts the mod), press **Reload**
next to the mod in the Loader tab, or run `tools\coreloader.ps1 -Game <name> reload <mod>`.

A handler registered with `TestHost.Register` that throws answers `ok:false` and does **not** fault
the mod, by design.

## "not a YYC game" {#not-a-yyc-game}

```text
[!] symbols: not a YYC game: data.win holds VM bytecode, and Lodestone needs YYC-compiled code (GML features are off)
```

Lodestone finds a game's scripts and events in the compiled executable. A game built with GameMaker's
VM export keeps its code as bytecode in `data.win`, so there is nothing to find. The loader stands
down cleanly: the game runs, the overlay opens, but GML features are off. This is expected (and
tested) for VM-compiled games; there is no workaround, because there is no native code to hook.

The sibling message means the game *is* compiled but the pattern search failed:

```text
[!] symbols: only <n> symbols resolved (expected >= <m>) - table shape changed?
```

That is a loader bug for that runtime version; see [the runtime bridge](../../internals/runtime-bridge.md)
and [GML functions](../../internals/gml-functions.md) for how symbols are found.

## The managed runtime refuses to start {#version-mismatch}

The native loader (`version.dll`) and the managed runtime (`Lodestone\CoreLoader.dll`) talk through a
table of function pointers, `CoreApi`. Its shape is versioned: `kCoreApiVersion` in `src/host/core_api.h`
on the native side and `ExpectedVersion` in `managed/CoreLoader/Native/CoreApi.cs` on the managed side.
`Entry.Init` compares them and returns failure if they differ, or if the table is smaller than the
managed side expects:

```text
CoreLoader.Runtime.Entry.Init reported failure (see managed log lines above)
```

This is by design: calling through a field the other side does not have would corrupt the game. It
happens when `version.dll` and `CoreLoader.dll` come from different builds, usually after a partial
deploy. Rebuild **both** sides and redeploy together:

```powershell
tools\deploy-coreloader.ps1 -GameDir "<game>" -Mods Console
```

With a running game use `-Live`: locked files are renamed to `*.old` and the new ones take effect on
the next launch. Nothing a mod does can cause this; it is only relevant if you work on the loader.

## Hot reload does not pick up my build {#hot-reload}

How it works is in [concepts](../concepts.md#hot-reload). In practice:

- **Wait about 700 ms after the last write.** A change is applied once the files have been quiet that
  long. A build that writes the dll in bursts therefore reloads once.
- **The new copy is loaded before the old one is dropped.** If it cannot load yet (still being
  written, or an antivirus is scanning it), the old copy keeps running:

  ```text
  could not load the new <file>.dll yet; keeping the running copy and trying again
  ```

  It retries up to three times, then:

  ```text
  giving up on the new <file>.dll; the running copy stays
  ```

  Touch or rebuild the file again, or use the Loader tab's **Reload** button.
- **The new build is for another game, or refused.** Then the old copy is unloaded (the verdict is
  final) and the log says `unloaded <file>.dll: its new build is not loaded here`; see
  [below](#not-loaded).
- **Hot reload is off.** The Loader tab has a checkbox. If the loader could not watch the folder at
  all, the log says `hot reload unavailable (cannot watch <dir>): <reason>`.
- **A file locked by the game.** Mod assemblies are loaded from memory, so a mod's dll is not locked
  and a build can overwrite it while the game runs. The loader's own files (`version.dll`,
  `CoreLoader.dll`) are locked: `tools\deploy-coreloader.ps1 -Live` renames them to `*.old`, and the
  new ones take effect on the next launch. A rebuilt `CoreLoader.dll` is never hot reloaded.
- **Content files are not watched.** Reload the mod after changing a PNG or OGG.
- **Fields start fresh** after a reload. Anything that must survive goes in `Config`.

## A mod does not load, or is "skipped" {#not-loaded}

Every mod declares which games it is for ([CL0004](analyzers.md#cl0004)). The log and the Loader tab
tell you which case you are in.

**Skipped for another game.** Quiet by design, since one `Mods\` folder may serve several games:

```text
skipping <name>: it is for <games>, not <exe name>
```

Shown grey as `<name> (<file>) - skipped: ...`. The game name is compared with the exe name without
`.exe`, ignoring case, or with the interop namespace (`Dwarf_Eats_Mountain` for
`Dwarf Eats Mountain.exe`).

**Refused.** The mod does not say which game it is for, so the loader will not load it. Logged as an
error and shown red as `<name> (<file>) - not loaded: ...`. Three messages:

```text
<name> does not say which game it is for. Add [assembly: CoreModGame("<exe name>")] (the game's exe name; list several if it supports more) for a mod written for particular games, or [assembly: CoreModAnyGame] for one that works in any game
<name> carries both [CoreModGame] and [CoreModAnyGame]; keep the one that is true
<name>'s [CoreModGame] names no game; name at least one exe, or use [CoreModAnyGame] instead
```

The analyzer reports the same cases at build time as an error, CL0004. The test host's `status` lists
both kinds under `notLoaded`.

Other load failures, from `ModManager`:

```text
<file>: [CoreModInfo] needs a mod type, name, version and author
<file>: <Type> must be a non-abstract subclass of CoreMod
failed to load <file>
```

The last one is followed by the exception, for example a missing dependency or a mod built against a
different `CoreLoader.dll`.

A dll in `Mods\` with no `[CoreModInfo]` is not a mod (it is treated as a dependency) and is ignored
without a line in the log.

## A game call throws `GmlException` {#gml-exception}

A script or builtin the game rejects throws `GmlException` with the game's own error text:

```text
call to scr_x failed: Variable ... not set before reading it. (in gml_Script_scr_x, line 12)
```

The most common cause is calling a script in a state where the game has not set up what it reads:
at the title screen there is no player, no `global.` variables from a save, no instances of most
objects. A mod that loads with the game must treat everything it reads as maybe-absent:

- `Objects.o_player.First` and `GmlObject.Find(...)` return `null` when there is none. Check it.
- `Globals.Exists(name)` before `Globals.Get`.
- `InstanceRef.Exists` before reading from a held instance.
- Catch `GmlException` where a failure should cost one action, not the mod
  (see [robustness](../cookbook/robustness-and-testing.md)).
- Do not run scripts that need an event's context from your own tab. Some work only inside the event
  they were written for; arm `Hooks.NextAfter` and make the game run the event.

Related messages: `<name> does not exist in <game>` (a script or event name that is not compiled in;
check with `Game.FindSymbol` or the Console's `find`), and `could not hook <symbol> (see the loader log)` (the loader refused the detour; the native side logs why, for example that the address is not the
start of a `gml_*` function).

The harness README records an extreme case: running an event by guess can stop the game with a GML
"Code Error". Prefer calling scripts to running events, and prove a path by watching the real thing
with ScriptSpy first.

## Values freed at the end of the frame {#values-freed}

Symptom: a string, array or struct you read works in the frame you read it, and later comes back as
garbage or crashes the game.

Cause: strings, arrays and structs from the game go into a per-frame pool and are released at the end
of the frame. Keeping one in a field, or capturing one in a lambda that runs later, keeps a dangling
reference. This is exactly what [CL0001](analyzers.md#cl0001) flags at build time, so look for that
warning first. Fixes: store C# data (`AsReal`, `ToString()`), or own the value with `Values.Keep` and
release it with `Values.Free`. The reverse mistake, `Values.Free` on a hook argument or result, is
[CL0003](analyzers.md#cl0003). See [value lifetime](../concepts.md#values).

A related symptom, a raw `Instance` that crashes after an object is destroyed, is
[CL0002](analyzers.md#cl0002): hold an `InstanceRef`.

## Interop does not build or is out of date {#interop}

- **"interop generation failed"** in the log: the generator threw; the exception follows. The
  interop is not updated.
- A mod project that references a game's interop and builds nothing, with a warning saying what to run:
  there is no generated interop on this machine yet. Run the game once with Lodestone, or
  `tools\setup-dev.ps1`.
- The interop is regenerated when the game exe changes, and after the harvester learns new variables
  (on the next launch, or immediately with **Regenerate interop now** in the Loader tab). A rebuilt
  `<Game>.Interop.dll` reloads every mod that loaded it.

## Antivirus flags `version.dll` {#antivirus}

A mod loader works by being loaded by the game in place of a Windows file with the same name, which
is what antivirus programs are suspicious of. Restore the file and add an exception for the game
folder. Players' guidance is in the
[INSTALL.md troubleshooting section](https://github.com/Forevka/stoneshard-mod/blob/main/INSTALL.md#troubleshooting).
For development, exclude `<game>\Lodestone\` and your `managed\bin` folder too, since a scanner holding
a freshly built dll is the usual cause of the "could not load the new ... yet" retries above.

## Reporting a problem

Attach `Lodestone\Logs\lodestone.log`. If the game crashed, attach `lodestone.prev.log` too: that is
the log from the run before.
