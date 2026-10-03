---
title: Managed runtime
description: How CoreLoader.dll discovers, loads, isolates and hot-reloads mods, pools GML values, and generates the per-game interop.
---

`CoreLoader.dll` is the managed half of Lodestone. The native host binds its `Entry` class (see
[Hosting .NET](./dotnet-host.md)); from there `managed/CoreLoader/Runtime/` does the rest:

| File | What it does |
|---|---|
| `Runtime/Entry.cs` | The five `[UnmanagedCallersOnly]` entry points: `Init`, `HookDispatch`, `Frame`, `Gui`, `Shutdown`. |
| `Runtime/ModManager.cs` | Discovery, load contexts, the mod list, faults, hot reload. |
| `Runtime/InteropGenerator.cs` | Writes `<game>\Lodestone\Interop\<Game>.Interop\` from the live game. |
| `Runtime/CodeScan.cs` | Reads a script's argument count out of its machine code. |
| `Runtime/VarHarvest.cs` | Collects instance-variable names from live instances, across sessions. |
| `ObjectTable.cs` | The game's object table (index, name, parent), built a slice per frame. |
| `Values.cs` | The per-frame pool that releases GML values the game hands to mods. |

Every frame, `Entry.Frame` runs a fixed list of stages (initialise, hot reload, queued actions, test host,
hook request timeouts, object table, interop, variable harvest, mod updates, game drawing, settings) and drains the value pool in a
`finally`. [Overlay](./overlay.md) covers that loop; this page covers what the stages do.

## Discovery {#discovery}

`ModManager.DiscoverAndLoad` runs inside `Entry.Init`, on the host's start-up thread. It looks in
`<game>\Mods\` for two shapes: a dll directly in the folder, and `Mods\<Name>\<Name>.dll` (a mod in its own
folder with its content files and dependencies).

```csharp
private static List<string> Candidates(string dir)
{
    var list = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).ToList();
    foreach (var sub in Directory.GetDirectories(dir))
    {
        var main = System.IO.Path.Combine(sub, System.IO.Path.GetFileName(sub) + ".dll");
        if (File.Exists(main)) list.Add(main);
    }
    return list.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L147-L156)</small>

Not every dll in the folder is a mod. A mod's dependencies (the generated `<Game>.Interop.dll`, a NuGet
package) can sit next to it. Loading those as if they were mods would lock them into a load context of their
own. So each candidate is first checked through metadata only, with `PEReader`, for an assembly-level
`[CoreModInfo]`:

```csharp
// Checks for the attribute through metadata only, so a dependency dll in the
// Mods folder is never loaded (and never locked into a context) by accident.
private static bool HasModInfo(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
    if (!pe.HasMetadata) return false;
    var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
    foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
    {
        // ...
        if (md.GetString(type.Name) == nameof(CoreModInfoAttribute) &&
            md.GetString(type.Namespace) == typeof(CoreModInfoAttribute).Namespace)
            return true;
    }
    return false;
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L466-L486)</small>

### Which game a mod is for {#game-check}

After the assembly is loaded, `CheckGame` reads `[CoreModGame]` and `[CoreModAnyGame]`. One mods folder may
serve several games, so the outcome has two flavours:

- **Skipped** quietly: the mod names other games. Logged as `skipping <mod>: it is for X, not Y`.
- **Refused** with an error: the mod names no game at all, carries both attributes, or has a
  `[CoreModGame]` with no names. A mod that does not say which game it is for is refused everywhere, so its
  author hears about it the first time they run it.

A game matches by exe name, or by the interop namespace (`Dwarf_Eats_Mountain` for "Dwarf Eats Mountain"),
which is what an interop-based mod knows the game by. Skipped and refused mods are listed in the Loader tab and
in the test host's status, since a log line alone is easy to miss.

### Constructing the mod {#construction}

`TryLoad` then checks `[CoreModInfo]` (a mod type, name, version and author are all required) and that the
type is a non-abstract `CoreMod`. The `LoadedMod` record is created **before** the constructor runs, and is
`ModManager.Current` while it runs:

```csharp
// The record exists before the mod's constructor runs, and is the
// current mod while it does: whatever a constructor or field
// initialiser registers belongs to this mod and goes with it.
var loaded = new LoadedMod { Path = path, Context = ctx, Generation = ++_generation };
ctx.Owner = loaded;
var previous = Current;
Current = loaded;
try
{
    var mod = (CoreMod)Activator.CreateInstance(info.ModType)!;
    // ...
    loaded.Instance = mod;
}
catch
{
    // Nothing of a half-built mod may outlive it, nor its context.
    RemoveRegistrations(loaded);
    ctx.Unload();
    throw;
}
finally
{
    Current = previous;
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L232-L260)</small>

`Current` is how the loader attributes registrations (hooks, draw handlers, content, settings) to a mod, so
it can tear them down on fault or reload (see [ownership](../modding/concepts.md#ownership)). For code that
runs outside a callback (a `Task`, a timer), `ModManager.OwnerOf(delegate)` reads the owner from the load
context of the delegate's own assembly instead.

A constructed mod is in state `Loaded`. Its `OnInitialize` waits for the game: the first frames call
`EnsureModsInitialised`, which waits until the game has loaded its assets (or 30 seconds pass; see
[Boot sequence](./boot.md)), then calls `ModManager.Initialize` for each waiting mod. `Initialize` attaches the
mod's attribute hooks first, so `OnInitialize` can rely on them, then calls `OnInitialize` and marks the mod
`Running`.

## Load contexts {#load-context}

Each mod gets its own `ModLoadContext`, a collectible `AssemblyLoadContext`. That serves two purposes:
two mods can ship different versions of the same dependency, and a mod can be unloaded and loaded again.

```csharp
protected override Assembly? Load(AssemblyName name)
{
    if (string.Equals(name.Name, Self.GetName().Name, StringComparison.OrdinalIgnoreCase))
        return Self;
    var path = _resolver.ResolveAssemblyToPath(name);
    if (path != null) return LoadUnlocked(path);

    // Mods are often deployed as bare dlls without their deps.json; a
    // dependency (such as a generated <Game>.Interop.dll) next to the mod
    // is still the right one.
    var local = System.IO.Path.Combine(_directory, name.Name + ".dll");
    return File.Exists(local) ? LoadUnlocked(local) : null;
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L66-L78)</small>

Three rules in that resolver:

- **CoreLoader resolves to the running copy.** If a mod's context loaded its own `CoreLoader.dll`, the mod's
  `CoreMod` would be a different type from the loader's, and nothing would match.
- **The deps.json resolver comes first**, through `AssemblyDependencyResolver` on the mod's main assembly.
- **A sibling dll is the fallback**, for mods copied without their `.deps.json`.

Every assembly is loaded from bytes, never by path. `LoadUnlocked` reads the dll (and its `.pdb`, when there
is one, so stack traces keep file and line numbers) into memory and calls `LoadFromStream`. The files on disk
stay unlocked, so a build can overwrite a mod while the game runs, which is what hot reload needs.

## Hot reload {#hot-reload}

A `FileSystemWatcher` on the mods folder (`*.dll`, subfolders included) records the time of every change,
create, delete and rename (both names of a rename). It only records: the watcher's events arrive on a thread
pool thread, and mods may only be touched on the game thread.

Each frame, `ModManager.PollChanges` picks up paths that have been quiet for 700 ms. A build writes in bursts
(the dll, then the pdb, then the dll again), and reloading on the first write would load half a file. For each
settled path:

- **A loaded mod's dll** is reloaded.
- **A new dll with `[CoreModInfo]`** is loaded and initialised.
- **A dll that cannot be read yet** (still being written, or not a PE file yet) is put back to be looked at
  again.
- **Any other dll** is treated as a dependency: every mod whose load context loaded an assembly of that name
  is reloaded. Rebuilding `<Game>.Interop.dll` reloads the mods built on it.

This runs as the "hot reload" stage at the start of `Entry.Frame`, between frames, where no mod code is on the
stack. The Loader tab's Reload buttons queue their work with `Game.RunOnGameThread` for the same reason: a
reload from inside the GUI pass would unload the code that is drawing.

`Reload` loads the new copy **before** it unloads the old one:

```csharp
// The new copy is loaded first: if the build is still being written, or
// an antivirus holds the file, the running copy stays - and the file is
// looked at again shortly - instead of the mod simply disappearing.
// The running copy's settings are written first, so the new one reads them.
try { m.Instance.Config.Save(); } catch (Exception ex) { Log.Warning($"saving {m.Instance.Info.Name}'s settings: {ex.Message}"); }
var fresh = TryLoad(path);
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L342-L347)</small>

If the new copy fails to load, the file is rescheduled up to three times, after which the loader gives up and
the running copy stays. If the new build is for another game, or refused, that is a final verdict, and the
running copy is unloaded with it. Otherwise the old copy is unloaded and the new one takes its place in the
list. It is initialised at once if mods have already started, or waits with the rest if the game is still
loading.

Unloading runs in a fixed order:

1. `OnShutdown`, only if the mod reached `Running`.
2. `RemoveRegistrations`: hooks, game drawing, input, content (sprites and sounds), kept values, test host
   commands, settings. Content goes after `OnShutdown`, which is where a mod points instances away from its
   sprites.
3. The mod's settings are saved.
4. In a `finally`: the mod leaves the list and its load context is unloaded, whatever failed above.

## Fault isolation {#faults}

Every call into a mod goes through `ModManager.Invoke`, which sets `Current` and catches everything:

```csharp
public static void Invoke(LoadedMod m, string callback, Action<CoreMod> action)
{
    var previous = Current;
    Current = m;
    try
    {
        action(m.Instance);
    }
    catch (Exception ex)
    {
        Fault(m, $"{callback}: {ex.GetType().Name}: {ex.Message}", ex);
    }
    finally
    {
        Current = previous;
    }
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L505-L521)</small>

`Fault` marks the mod `Faulted` and removes what would keep running its code: hooks, game drawing, sounds, an
armed pick, settings callbacks. Its sprites stay until it unloads, because instances in the game may still
show them. A faulted mod stays in the list, with the reason shown in its tab and in the Loader tab, until it is
reloaded.

The same rule holds in the other places mod code runs:

- **Hook handlers.** `Hooks.Dispatch` runs each subscriber as its owning mod and faults only that mod when
  its handler throws
  ([Hooks.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Hooks.cs#L392-L416)).
- **OnGUI.** `Entry.DrawModTab` records the UI scope depth before `OnGUI` and closes anything left open after
  it. A mod that leaves a scope open is faulted, since the unclosed scope would corrupt every tab drawn after it.
- **Frame stages.** Each stage of `Entry.Frame` is wrapped on its own, so one failing loader stage does not
  cost every mod its update.

The player-facing rules are on [Concepts](../modding/concepts.md#faults).

## The value pool {#values-pool}

GML strings, arrays and structs hold a reference that someone must release. The managed side never asks mods
to do that for values used within a frame. Every value the game hands over passes through `Values.Track`
(call results in `Game`, variable reads, `RValue.FromString`, `HookCall.CallOriginal`), which adds it to a pool:

```csharp
internal static RValue Track(RValue v)
{
    if (CanFree && HoldsReference(v)) Pool.Add(v);
    return v;
}
```

<small>Source: [managed/CoreLoader/Values.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Values.cs#L33-L37)</small>

`Values.Drain` releases the pool through `CoreApi.value_free` in the `finally` of both `Entry.Frame` and
`Entry.Gui`. `Values.Keep` takes a value out of the pool (or copies a value the mod does not own, such as a
hook argument), and `Values.Free` releases it later.

`CanFree` and `CanCopy` come from `Values.Probe`, which runs once, on the first frame, before mods start: it
calls `value_free` and `value_copy` on an undefined value, and each returns 0 when the native side did not find
and verify that helper (see [Runtime bridge](./runtime-bridge.md)). Without a free helper nothing is pooled,
because nothing could be released.

Structs are different: they are garbage-collected, not reference-counted. A pointer held in C# is invisible to
the GML collector, which would free the struct as soon as GML stopped using it. So `Keep` on a struct also
pushes it into a GML array held in the global `__coreloader_roots`, where the collector sees it. The managed
side remembers which mod rooted what, so a mod that unloads without freeing loses its roots, and if the game
wipes its globals (`game_restart`) the array is created again with every tracked root.

The rules this gives mod authors are on [Concepts](../modding/concepts.md#values).

## Interop generator {#interop-generator}

The interop is a C# project generated from the running game, so mods can write `Scripts.scr_get_XP` instead of
a string (see [Interop](../modding/interop.md)). Everything comes from the live game: the symbol table, the
builtin registry and the runtime's own asset enumeration. That is why it works for any YYC game.

**When it runs.** `InteropGenerator.Tick` runs every frame. It does nothing without a GML bridge. It waits at
least 120 frames, until builtins are resolved, and until an asset answers (the same check mods' start makes),
or until 1800 frames have passed. It then compares a stamp:

```csharp
private static string Stamp()
{
    var exe = Environment.ProcessPath ?? "";
    var info = new FileInfo(exe);
    return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Game.Symbols.Count}|{Game.BuiltinCount}|" +
           $"{typeof(InteropGenerator).Assembly.GetName().Version}|f{Format}";
}
```

<small>Source: [managed/CoreLoader/Runtime/InteropGenerator.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/InteropGenerator.cs#L162-L168)</small>

If `.stamp` in the output folder matches, the interop is up to date and nothing runs. A new game build, a new
loader version, or a bump of `Format` (when the generated code changes shape) regenerates it.
`VarHarvest` deletes the stamp when it learns new variable names, and the Loader tab's "Regenerate interop now"
button forces a run.

**Collecting without a hitch.** A big game needs tens of thousands of builtin calls to enumerate its assets.
`Collect()` is an iterator over the game-thread work, and `RunSlice` advances it for at most 4 ms per frame.
The steps are: the symbol table, the object table (waiting on `ObjectTable` rather than scanning again),
builtin names and arities, then sprites, rooms and sounds by walking `*_exists` / `*_get_name` until 64
indices in a row do not exist. Sprites and sounds that mods added are left out. The snapshot is then handed to
`Task.Run`: scanning code for argument counts and writing several MB of source need no GML, so they run off
the game thread.

**Output**, in `<game>\Lodestone\Interop\<Game>.Interop\`:

| File | Contents |
|---|---|
| `<Game>.Interop.csproj` | A `net10.0` project referencing `..\..\CoreLoader.dll`. |
| `Scripts.g.cs` | A `ScriptRef` per script; `ScriptRef1`..`ScriptRef8` where the argument count was read from code. |
| `Objects.g.cs` | `InstanceVars`, and per object its `Name`, `Object`, `First`, harvested `Vars`, and an `EventRef` per event. |
| `Builtins.g.cs` | A typed wrapper per builtin, with this runtime's registered arity (`params` for variadic). |
| `Assets.g.cs` | `Assets.Sprites`, `Assets.Rooms`, `Assets.Sounds` name constants. |
| `codemap.json` | The same data, with function addresses, for tools. |
| `.stamp` | Written only after a complete run. |

**Degraded runs.** A run that gave up waiting for assets, found an empty object table, or had an asset scan
stopped by an error is marked degraded. It still writes its files, but deletes `.stamp`, so the next launch
tries again instead of keeping a partial map. The Loader tab then shows "partial - will retry next launch".

### Reading argument counts from code {#codescan}

A GML script takes `argc` and an array of argument pointers; its signature says nothing about how many it
reads. But YYC compiles every read of `argument[j]` into a guard on `argc` followed by a load from the
argument array: `argc > j ? args[j] : undefined`. `CodeScan` looks for that shape:

- a guard: `cmp r32, imm8` (`83 F8+r ib`, optionally REX-prefixed) or `test r32, r32` on one register;
- then a conditional jump: `jl`/`jge`/`jle`/`jg`, rel8 (`7C`..`7F`) or rel32 (`0F 8C`..`0F 8F`);
- then, within 12 bytes, `mov r64, [base]` or `mov r64, [base + disp8]` with a REX.W prefix (`48`, `49`,
  `4C`, `4D`, then `8B`), where `disp8 / 8` is the slot index.

A load counts when its slot is the guard's immediate `k` or `k - 1` (so a guard written as `cmp argc, j` or as
`cmp argc, j + 1` both match) and is below 16. The largest slot plus one is the argument count. An illustrative
match, a script reading its third argument:

```nasm
83 F9 02          cmp   ecx, 2          ; guard: k = 2
7E 0A             jle   short .undef    ; argc <= 2 -> argument is undefined
48 8B 47 10       mov   rax, [rdi+10h]  ; args[2]: disp 0x10 / 8 = slot 2
                                        ; slot == k -> best = 2, argument count = 3
```

The scan stops at the next function's start (from the sorted symbol table) or after 6000 bytes, so a small
script never inherits its neighbour's arguments. Scripts that index `argument[i]` dynamically show no guards
and report 0, which the generator reads as "unknown" and types as plain `ScriptRef`. The class comment records
the scripts it was checked against on both runtimes (`scr_approach` 3, `scr_atr_set` 2, `dealDamage` 6,
`selectDrops` 4, `key_to_string` 1). The scanner itself is in
[managed/CoreLoader/Runtime/CodeScan.cs, lines 38-96](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/CodeScan.cs#L38-L96).

### Harvesting instance variables {#var-harvest}

A YYC exe does not record which variables an object has: they come into being as its code assigns them. The
only reliable source is live instances. `VarHarvest.Tick` starts after mods do, walks the object table, and
for each object with a live instance reads the variable names of its first instance. It works by time, not by
count, at most 1.5 ms per frame, because one object with hundreds of variables costs as much as a hundred
objects with a few. A full pass repeats about once a minute (every 3600 frames).

The names accumulate across sessions in `<game>\Lodestone\Interop\<Game>.vars.json`. When a pass learns
something new, the file is written (through a `.tmp` and a move) and the interop is marked stale, so the next
launch emits `Objects.<obj>.Vars.<name>` for it.

## The object table {#object-table}

GameMaker has no "list the objects" builtin. `ObjectTable` finds them by calling `object_exists` and
`object_get_name` for every asset index from 0 until 64 indices in a row do not exist, then
`object_get_parent` for each object it found. That is thousands of builtin calls, so `Tick` spends at most
4 ms per frame on it. Objects never change while the game runs, so the table is built once and cached.

It does not start before the game has its assets: a table read too early comes back empty. Before mods start,
it makes the same asset check mods' start makes. A scan that does find nothing is not cached, so a later call
scans again.

`object_get_parent` returns a plain number on the older runtime and a typed reference on 2024+ runtimes, so
`AssetIndex` accepts both:

```csharp
internal static int AssetIndex(RValue v) =>
    v.IsNumber ? (int)v.AsReal : v.Kind == RValueKind.Reference ? (int)(v.Int64 & 0xFFFFFFFF) : -1;
```

<small>Source: [managed/CoreLoader/ObjectTable.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/ObjectTable.cs#L125-L126)</small>

`GmlObject.All` and `GmlObject.Children` work at any time; when the table is not there yet they finish the
part they need on the spot, in one frame. The interop generator and the variable harvester both wait for the
shared table instead, so the session scans the objects once. See
[Runtime differences](./runtime-differences.md) for the number-versus-reference split.
