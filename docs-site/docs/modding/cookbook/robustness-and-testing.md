---
title: Robustness and testing
description: Recipes for surviving a game that is not ready, keeping per-frame work cheap, working in the background, inspecting compiled code, and testing a mod through the test host.
---

A mod runs inside someone else's game, on the game's thread, against state that is half-built at the
title screen and changes under you every frame. Two rules in the loader shape everything on this page:
an exception that escapes a callback **disables your mod until it is reloaded** (see
[Faults](../concepts.md#faults)), and every call into the game can throw a `GmlException`. So the job is
to catch the exceptions you can predict, close to where they happen, and let only real bugs fault the mod.

## Catch GmlException where the game may not be ready {#catch-gmlexception}

`GmlException` is what a call into the game throws when the game rejects it: a variable that is not set,
an instance that is gone, a script that does not exist. At the title screen most of the game's objects do
not exist yet, so a mod that reads one every frame will hit it. DwarfBoost reads `oSys` in `OnUpdate`
and keeps the message for its tab instead of faulting:

```csharp
public override void OnUpdate()
{
    if (++_tick % 6 != 0) return;   // ten times a second is plenty
    try
    {
        ApplyGold();
        if (_tick % 30 == 0) ApplyDamage();
        if (_tick % 600 == 0 && _damage.Count > 0)
        {
            var sample = _damage.First().Value;
            Log.Info($"gold {_lastGold:N0} (bonus so far {_bonusGold:N0}); {_damage.Count} units, " +
                     $"e.g. damage {sample.Base:0.##} -> {sample.Written:0.##}");
        }
        _status = "";
    }
    catch (GmlException ex)
    {
        _status = ex.Message;   // e.g. at the title screen, before oSys exists
    }
}
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L49-L68)</small>

The message is the game's own, for example
`call to scr_x failed: Variable ... not set before reading it. (in gml_Script_scr_x, line 12)`, which
makes it a good thing to show the player or write to the log.

Gotchas:

- Catch `GmlException`, not `Exception`. A broad catch hides your own bugs, and `NullReferenceException`
  in your code is a bug to see, not to swallow.
- Cosmetic calls (a sound, an overlay line) are worth a bare `catch (GmlException) { }`, as FastTravel's
  `Say` does. A call whose failure changes behaviour deserves a log line.
- Prefer checking over catching where a check exists: `GmlObject.InstanceCount > 0`, `InstanceRef.Exists`,
  `Has("variable")`, `ScriptRef.Exists`, `DsMap.Exists`.
- Several calls are involved when one fails part-way. FastTravel's `Go` puts the map back when the room
  change throws, then rethrows, so a failed journey never leaves the game believing it moved
  ([Traveller.cs lines 71-85](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/Traveller.cs#L71-L85)).

## Wrap callbacks in a Guard {#guard}

When a mod has many callbacks (several hooks, a draw handler, an update), a small helper that catches,
logs once and carries on keeps one bad frame from disabling the whole mod. ModMenu's:

```csharp
private void Guard(string what, Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        if (ex.Message != _lastError) Log.Warning($"{what}: {ex.Message}");
        _lastError = ex.Message;
    }
}
```

<small>Source: [managed/Mods/ModMenu/ModMenuMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L69-L77)</small>

It logs a message only when it differs from the last one, so a failure that repeats every frame produces a
line, not a flood. It is used around each hook body:

```csharp
Objects.o_close_panel.Create_0.After(c => Guard("menu", () => AddMenuButton(c.Self)));
```

<small>Source: [managed/Mods/ModMenu/ModMenuMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L40)</small>

The drawing equivalent catches `GmlException` and de-duplicates the same way: see `Draw()` in
[ModMenuMod.cs lines 181-190](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L181-L190).
For the overlay tab, `UI.Guarded` plays this role and also closes scopes you left open (see
[Drawing and UI](drawing-and-ui.md#guarded)).

Gotchas:

- A Guard that swallows everything also hides a bug that breaks the mod for good. Log, do not be silent.
- Do not guard what must fault: a mod in an unknown state is better off disabled than limping.

## Throttle OnUpdate {#throttle}

`OnUpdate` runs every frame, and every read of the game is a call. Do the expensive work less often, and
cache what cannot change. DwarfBoost does its work every sixth frame, and the walk over every unit every
thirtieth (the first line of the snippet in [the first recipe](#catch-gmlexception)):

```csharp
if (++_tick % 6 != 0) return;   // ten times a second is plenty
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L51)</small>

FastTravel recomputes its verdict for the map cell under the mouse only when the cell changes, and reads
the set of visited cells once per visit to the map:

```csharp
var cell = _ui.OnPanel(mx, my) ? null : WorldMap.CellAt(mx, my);
if (cell != _judged)
{
    _judged = cell;
    _verdict = cell is { } c ? WorldMap.Judge(c, Visited) : WorldMap.Verdict.Unknown;
}
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L141-L146)</small>

Gotchas:

- A hooked Step or Draw event can run thousands of times a frame. Return early; test the cheapest
  condition first.
- A frame-count throttle runs at a different real rate when the game runs faster or slower (SpeedControl
  changes the frame rate). If real time matters, compare `Environment.TickCount64`.
- Long jobs belong in the background, or spread over frames (next recipe, and `Code.FindCallers` below).

## Do background work and come back to the game thread {#background-work}

The game is single-threaded. From another thread the loader refuses every call that touches it (builtins,
scripts, strings, value free and copy) and logs the refusal. So long work (reading a file, scanning a
folder, parsing a big JSON) goes in a `Task`, and the result comes back with `Game.RunOnGameThread`, which
runs your action at the start of the next frame, as your mod. An action dropped because the mod unloaded
first is not an error.

The StoneshardCheats catalogue starts a disk read in a task, then checks it every frame from the game
thread by queueing the check again:

```csharp
public static void Start()
{
    if (_phase != Phase.Idle) return;
    _phase = Phase.Objects;
    ObjectTable.Start();
    Status = ObjectTable.Status;
    Clock.Restart();
    _disk = Task.Run(ReadDisk);
    Game.RunOnGameThread(Step);
}
```

<small>Source: [managed/Mods/StoneshardCheats/Catalogue.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Catalogue.cs#L119-L128)</small>

```csharp
Game.RunOnGameThread(Step);
```

<small>Source: [managed/Mods/StoneshardCheats/Catalogue.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Catalogue.cs#L162)</small>

An action that queues itself again runs next frame, not in an endless loop within one. The Saves tab shows
the other direction: the task does the file work, then hands its result to the game thread to report:

```csharp
Task.Run(() =>
{
    bool ok;
    string status;
    try { (ok, status) = SaveMigration.Import(found, Target, Actions.Log.Info); }
    catch (Exception ex) { (ok, status) = (false, $"Import failed: {ex.Message}"); }
    lock (_gate) { _status = status; _working = false; }
    Actions.Log.Info($"saves: {status}");
    Game.RunOnGameThread(() => Actions.Report(status, ok));
});
```

<small>Source: [managed/Mods/StoneshardCheats/Tabs/SavesTab.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Tabs/SavesTab.cs#L150-L159)</small>

Gotchas:

- **A task must never touch the game.** Not `Builtins`, not `Globals`, not even `RValue.FromString`. Pass
  plain C# data out and act on it back on the game thread.
- A task's exceptions go nowhere unless you catch them. The task above catches everything and turns it
  into a status, as every background task should.
- Share state between the task and the game thread under a lock, as `_gate` does.
- A task that outlives the mod keeps running. Keep what it needs in the task and check a cancel flag.
- A modal Windows dialog opened on the game thread freezes the game behind its own dialog. Open
  it on a thread of its own and poll for the answer, as the Saves tab's file picker does
  ([SavesTab.cs lines 164-174](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Tabs/SavesTab.cs#L164-L174)).

## Inspect compiled code {#introspection}

The game is compiled to native code, so there is no source to read, but the loader can show what a
function calls and which strings it uses, and who calls it. `Code.Describe(symbol)` returns a `CodeInfo`
with `Name`, `Address`, `Size`, `ArgumentCount`, `Calls` (scripts, events and builtins, in first-call order)
and `Strings`; it is `null` if there is no such function. The Console's `code` command prints it:

```csharp
var info = CoreLoader.Code.Describe(symbol);
if (info == null)
{
    nint b = CoreLoader.Code.BuiltinAddress(symbol);
    if (b == 0) throw new ConsoleError($"{symbol} is not a compiled function or a builtin");
    Print($"builtin {symbol}: native code at 0x{b:X}, {Game.BuiltinArity(symbol)} argument(s) " +
          "(use 'callers' to see which scripts and events call it)", 0.6f, 0.85f, 1f);
    return;
}
Print($"{info.Name}  0x{info.Address:X}, up to {info.Size:N0} bytes" +
      (info.ArgumentCount > 0 ? $", reads {info.ArgumentCount} argument(s)" : ""), 0.6f, 0.85f, 1f);
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L373-L383)</small>

`Code.FindCallers(symbol, ref cursor)` answers "who calls this?". It scans every function in the game, so
it works for a few milliseconds (default 4) per call and resumes from the `cursor` you pass back. Start
the cursor at 0 and call it once a frame until it comes back as -1:

```csharp
private void AdvanceCallers()
{
    if (_callersOf == null) return;
    _callersFound.AddRange(CoreLoader.Code.FindCallers(_callersOf, ref _callersCursor));
    if (_callersCursor >= 0) return;
    Print($"{_callersFound.Count} caller(s) of {_callersOf}:", 0.6f, 0.85f, 1f);
    foreach (var c in _callersFound.Take(100)) Print("  " + c);
    if (_callersFound.Count > 100) Print($"  ... and {_callersFound.Count - 100} more");
    _callersOf = null;
}
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L410-L419)</small>

Gotchas:

- Results are what the machine code references directly. A function the game picks at runtime (by a
  computed name or index) may not show up.
- Both calls are game-thread only.
- This is how hook points are found: see [Finding hooks](../finding-hooks.md).

## Expose test commands to the test host {#test-host}

For automated testing, the loader can take commands over a named pipe, so a script or an agent can drive
the game without a mouse. It is on when `CORELOADER_TEST=1` is set, or when a `testhost.enable` marker file
is in the loader's folder (`tools\run-game.ps1 -TestHost` handles both). A mod adds its own commands with
`TestHost.Register(name, handler, help)`, registering only when `TestHost.Enabled`:

```csharp
if (TestHost.Enabled) RegisterCommands();
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L45)</small>

```csharp
TestHost.Register("ft.mode", args =>
{
    if (args.Count > 0) SetMode(args[0].GetString() is "on" or "true" or "1", quiet: true);
    return _mode;
}, "ft.mode [on|off]: the fast travel mode");
TestHost.Register("ft.judge", args =>
    WorldMap.Judge((args[0].GetInt32(), args[1].GetInt32()), WorldMap.Visited()).ToString(),
    "ft.judge <x> <y>: whether that cell can be travelled to");
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L188-L195)</small>

Drive it with `tools\coreloader.ps1 -Game Stoneshard ft.mode on`. The handler runs on the game thread with
the request's JSON arguments and returns anything that can be serialised: a bool, number, string,
`RValue`, `InstanceRef`, a collection, or an object whose public properties are written out. The full
protocol, the built-in commands and the client scripts are in the
[test host reference](../reference/test-host.md).

Gotchas:

- Expose **state** (`ft.state`, `mm.state`) as well as actions. A test needs to read back what happened,
  not guess from the screen.
- A handler that throws answers `ok:false` with the message and does **not** fault the mod. A test run
  feeds commands bad input on purpose.
- Namespace your commands (`ft.`, `mm.`): a name taken by another mod, or by a built-in, is refused when
  you register it.
- The command goes when the mod unloads or hot-reloads. In a session without the test host, `Register`
  does nothing.
- A command that takes a while should be polled rather than waited on: the host never blocks a frame.

## Keep regression mods for the risky parts {#regression-mods}

Some bugs only show up as a slow leak or a collected struct, and only after a long time. The repository
keeps small probe mods under `managed/Tests/` that stress one thing and report:

| Mod | What it checks |
|---|---|
| `ValueProbe` | makes about 2,000 fresh GML strings a frame and throws them away; the game's private memory must stay flat |
| `StructProbe` | a struct kept with `Values.Keep` across frames while the collector runs; must log `PASSED` |
| `XpProbe` | grants 100 XP through the game's `scr_get_XP` and logs the gain; with StoneshardBoost at `xpMultiplier` 3 it must be 300 |
| `FaultyGuiMod` | opens a UI scope and throws; that mod must show as disabled while every other tab still draws |
| `WidgetProbe` | every UI widget, and each scope under a fault inside `UI.Guarded` |

The idea is portable: when a feature of your mod is hard to see (a hook that rewrites a value, code that
keeps a value across frames), write a small mod that exercises it, logs a clear pass or fail line, and
deploy it after risky changes. `StructProbe` is a short example to copy
([StructProbe.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Tests/StructProbe/StructProbe.cs)).
For a Stoneshard mod, the StoneshardHarness mod plays the game through its own scripts; see the
[shipped mods](../reference/shipped-mods.md).
