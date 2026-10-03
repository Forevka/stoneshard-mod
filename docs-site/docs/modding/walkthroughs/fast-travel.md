---
title: "Walkthrough: the Fast Travel mod"
description: A guided read of Fast Travel, a Stoneshard mod written against the typed interop that adds a world-map feature drawn with the game's own UI pieces.
---

Fast Travel adds a mode to Stoneshard's world map: switch it on, click any land you have visited (or
that touches land you have), and the player is moved there the way a border crossing moves them.
The mod is four files, about 700 lines, and uses no game code of its own. Everything it does is a call
to something the game already has: its scripts, its objects' variables, its map data and its UI
drawing routines.

Where the [Console walkthrough](console.md) used only the untyped API, this one uses the generated
[interop](../interop.md): `Scripts.scr_globaltile_get_room`, `Objects.o_player.First`,
`Builtins.ds_grid_get`, `Assets.Sounds.snd_gui_enable_mode`. These are the same calls with names the
compiler checks.

| File | Role |
|---|---|
| `FastTravelMod.cs` | Wiring: attributes, hooks, the key, per-frame logic, test commands |
| `WorldMap.cs` | Reading the map: the grid, visited cells, the hovered cell, whether a cell can be entered |
| `Traveller.cs` | Acting: moving the player, then fixing a bad arrival |
| `MapUi.cs` | The mod's own entry in the map's controls bar and its banner, drawn with the game's pieces |

The sources are in
[`managed/Mods/FastTravel/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Mods/FastTravel).

## Wiring {#wiring}

### Attributes and the interop reference

```csharp
using CoreLoader;
using StoneShard;

[assembly: CoreModInfo(typeof(FastTravel.FastTravelMod), "Fast Travel", "1.0.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace FastTravel;
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L1-L7)</small>

`using StoneShard;` brings in the generated interop namespace (its name is the game's exe name made
safe for C#). The project file points the build at that interop:

```xml
<PropertyGroup>
  <AssemblyName>FastTravel</AssemblyName>
  <InteropGame>StoneShard</InteropGame>
</PropertyGroup>
```

<small>Source: [managed/Mods/FastTravel/FastTravel.csproj](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravel.csproj#L4-L7)</small>

A mod built on an interop is for that one game, so it declares `[CoreModGame("StoneShard")]` rather
than `CoreModAnyGame`; the analyzer rule [CL0005](../reference/analyzers.md#cl0005) reports the
mismatch at build time. Generate the interop first by running the game once with Lodestone, or with
`tools\setup-dev.ps1`.

### OnInitialize: one hook, one draw handler

```csharp
public override void OnInitialize()
{
    _key = Config.Get("toggleKey", "F").Trim().ToUpperInvariant();
    if (_key.Length != 1) _key = "F";
    _ui = new MapUi(_key);
    _traveller = new Traveller(Log);
    _frameSprite = Builtins.asset_get_index("s_highlight_globalmap").AsReal;

    Objects.o_globalmapControlsRender.Other_20.After(c =>
    {
        try { _ui.AppendEntry(c.Self); }
        catch (GmlException ex) { Log.Warning($"could not add the Fast Travel entry to the map: {ex.Message}"); }
    });
    GameDraw.OnGui(DrawGui);
    if (TestHost.Enabled) RegisterCommands();
    Log.Info($"ready: open the map and press [{_key}] or click Fast Travel");
}
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L31-L47)</small>

Things to notice:

- **The only setting is the key**, read with `Config.Get("toggleKey", "F")` from `Mods\FastTravel.json`
  and validated (a bad value falls back to `F`). A mod with more to configure would use
  [`ModSettings`](../cookbook/settings-and-persistence.md), as StoneshardTrials does.
- **`Objects.o_globalmapControlsRender.Other_20.After(...)`** is an `EventRef` hook: run after the
  map's controls bar handles its user event 10. That event is where the bar builds its entries, so
  "after it" is the moment to add one more. The `try`/`catch (GmlException)` matters: this handler
  runs inside the game's event, and a game error in the mod's addition should cost the entry, not
  fault the mod. See [hooks](../cookbook/hooks.md).
- **The handler receives `c.Self` as an `Instance`**, a raw pointer valid for this call only. It is
  passed straight into `AppendEntry` and never stored. The analyzer rule
  [CL0002](../reference/analyzers.md#cl0002) exists to catch the mistake of keeping it.
- **`GameDraw.OnGui(DrawGui)`** registers a draw handler for the game's GUI pass. The loader tears it
  down on unload, so `OnShutdown` has no draw cleanup.
- **`TestHost.Enabled`** gates registering test commands (see [below](#test-host)).

### The per-frame loop: a key, a click, and "closing the map turns the mode off"

```csharp
if (Builtins.keyboard_check_pressed(Builtins.ord(_key)).AsBool) SetMode(!_mode);
if (!Builtins.mouse_check_button_pressed(1).AsBool) return;
double mx = Builtins.device_mouse_x_to_gui(0).AsReal, my = Builtins.device_mouse_y_to_gui(0).AsReal;
if (_ui.EntryContains(mx, my))
{
    SetMode(!_mode);
    return;
}
// A click on a panel is the panel's; only one on the map itself picks a place.
if (_mode && !_ui.OnPanel(mx, my) && WorldMap.CellAt(mx, my) is { } cell) TryTravel(cell);
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L67-L76)</small>

Input is read with the game's own builtins, not with a separate input system. The mod polls in
`OnUpdate`; the click targets are tested against rectangles it computes itself (`EntryContains`,
`OnPanel`), because the game's buttons for this entry do not exist. Notice the ordering: a click on
the bar entry toggles the mode, a click on any other panel is ignored, and only a click on the map
itself travels. Without the `OnPanel` check, clicking the legend would travel.

Earlier in the same method, `if (!open)` turns the mode off when the map closes and clears the cached
visited set, so the mode never outlives one visit of the map.

### Sounds from the interop's `Assets`

```csharp
try
{
    var sound = Builtins.asset_get_index(on ? Assets.Sounds.snd_gui_enable_mode : Assets.Sounds.snd_gui_disable_mode);
    if (sound.AsReal >= 0) Builtins.audio_play_sound(sound, 5, false);
}
catch (GmlException) { }
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L86-L91)</small>

`Assets.Sounds.snd_gui_enable_mode` is a string constant holding the asset's name, so a renamed or
removed asset is a compile error instead of a silent miss. The mod plays the game's *own* mode-switch
sounds so its entry sounds like the others on the bar. The empty `catch` has a stated reason: it is
cosmetic, "never worth a fault".

### OnShutdown

```csharp
public override void OnShutdown() => _ui?.Clear();
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L49)</small>

`Clear` destroys the ds_maps the mod created for its colour text. The hook and the draw handler go
by themselves, because [everything a mod registers belongs to it](../concepts.md#ownership); the
ds_maps are game objects the loader does not know about, so the mod frees them. The `?.` covers a mod whose
`OnInitialize` never ran or failed before `_ui` was assigned.

## Reading state {#reading-state}

`WorldMap.cs` is documentation of the game's map, "read through the test host on the live game
rather than assumed", as its class comment says:

- the world is a grid of cells; the player's is `global.playerGridX`/`playerGridY`;
- a cell the player has entered is a key `"x_y"` in `global.locationsRoomsDataMap`;
- `scr_globaltile_get_room(x, y)` is the room a cell loads, `-1` off the world, `r_sea` for water.

Those are three different ways of reading game state, each used the obvious way.

### Globals

```csharp
public static (int X, int Y) PlayerCell =>
    ((int)Globals.Get("playerGridX").AsReal, (int)Globals.Get("playerGridY").AsReal);
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L22-L23)</small>

`Globals.Get` returns an `RValue`; `.AsReal` converts a number to a `double` (NaN for a string or
reference). Converting to C# data straight away is the safe habit: nothing is kept.

### A ds_map of visited rooms

```csharp
public static HashSet<(int X, int Y)> Visited()
{
    var set = new HashSet<(int, int)>();
    var rooms = new DsMap(Globals.Get("locationsRoomsDataMap"));
    if (rooms.Exists)
    {
        foreach (var (key, _) in rooms.Entries())
        {
            var parts = key.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)) set.Add((x, y));
        }
    }
    set.Add(PlayerCell);
    return set;
}
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L80-L94)</small>

The game's real state is in a `ds_map` whose id sits in a global. `DsMap` wraps the id; `Exists` guards
against a map that is not there yet (the title screen, a new game); `Entries()` enumerates key and
value pairs. The mod needs only the keys. The result is a plain C# `HashSet`, so the caller can hold
it for the whole map visit (and `FastTravelMod` does: `_visited`, reset when the map closes). See
[game state](../cookbook/game-state.md) for the `DsMap`/`DsList` API.

### A script as a function

```csharp
public static int RoomOf(int x, int y) => (int)Scripts.scr_globaltile_get_room.Call(x, y).AsReal;

/// <summary>Land the game can load: not off the world, not open sea.</summary>
public static bool IsLand(int room) =>
    room >= 0 && Builtins.room_get_name(room).ToString() != "r_sea";
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L96-L100)</small>

`Scripts.scr_globaltile_get_room` is a `ScriptRef`; `Call(x, y)` runs it as the current context (whatever the game last ran as; the call
is refused if there is none), which is fine for a pure lookup. This is the cheapest way to borrow game logic: the mod does not
reimplement which tile is sea, it asks. `ToString()` on an `RValue` goes through the runtime's own
string formatting, so it is only valid on the game thread (it is, here).

### Putting it together: the verdict

```csharp
public static Verdict Judge((int X, int Y) cell, HashSet<(int X, int Y)> visited)
{
    if (cell == PlayerCell) return Verdict.Here;
    bool near = visited.Contains(cell);
    for (int dx = -1; dx <= 1 && !near; dx++)
        for (int dy = -1; dy <= 1 && !near; dy++)
            near = visited.Contains((cell.X + dx, cell.Y + dy));
    if (!near) return Verdict.Unknown;
    return IsLand(RoomOf(cell.X, cell.Y)) ? Verdict.Allowed : Verdict.Water;
}
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L108-L117)</small>

The rule is pure C# over game data: visited, or touching visited, and land. The expensive call
(`RoomOf`) runs only after the cheap set test passes. `FastTravelMod.DrawMode` caches the verdict for
the hovered cell and recomputes it only when the cell changes, so hovering costs one script call per
cell, not per frame.

### Instance variables for layout

`WorldMap.Layout()` reads the map's own instance:

```csharp
if (Objects.o_globalmap.First is not { } map) return null;
var scale = map.Get("mapScale");
var width = map.Get("mapWidth");
if (!scale.IsNumber || !width.IsNumber || scale.AsReal <= 0) return null;
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L62-L65)</small>

`Objects.o_globalmap.First` is the object's first live instance as an `InstanceRef?`, and it is null
when the map is closed, which is what `WorldMap.IsOpen` tests on the controls bar object. `Get` reads a
variable by name; `IsNumber` guards against `undefined` before the map has set it up. The mod derives
which cell is under the mouse from the map's layout (`mapScale`, `mapWidth`, `mapOffsetX/Y`) instead
of from the game's own highlight, because the highlight exists only over cells the fog has lifted,
and the mod needs to judge cells next to those too. That reasoning is in the class comment.

## Acting: moving the player through game scripts {#acting}

The tempting implementation is to write the player's `x` and `y`. It would be wrong: the game would
not load the destination room. `Traveller.cs` instead does what a border crossing does, in the same
order, and its remarks record where that was observed on the live game.

```csharp
// The same three things a border crossing sets, in its order.
Scripts.scr_atr_set_simple.CallAs(player, "localX", ax);
Scripts.scr_atr_set_simple.CallAs(player, "localY", ay);
SetCell(cell);
_oldPlayer = IdKey(player.Id);
Scripts.scr_smoothRoomChange.CallAs(player, room, Builtins.array_create(1, 4), -1, false);
```

<small>Source: [managed/Mods/FastTravel/Traveller.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/Traveller.cs#L73-L78)</small>

- **`CallAs(player, ...)`** runs a script *as* an instance. Many game scripts read their caller's
  variables, so calling them from nowhere (`Call`) would fail or do the wrong thing. `player` is an
  `InstanceRef`; there is also an overload taking two `Instance` values (self and other), which the
  bar entry uses.
- **`SetCell`** writes `Globals.Set("playerGridX", ...)`, pointing the world map at the next cell,
  exactly like the crossing.
- **If the game refuses part-way**, the `catch (GmlException)` around this block puts the cell back
  (`SetCell(_from)`) and rethrows, so the map never believes the player moved when they did not.
- **`_oldPlayer`** records the player's id. The player instance is recreated by the room change, so
  "the id changed" is how `Tick()` knows the new room has its player.

### Refusing when it is not safe

```csharp
public string? Refusal(InstanceRef player)
{
    if (Busy) return "you are already on your way";
    // Only from the open world: leaving a building or a dungeon by any
    // other way than its door is not something the game ever does.
    if (Grids() is not { } g || g.W != RoomCells || g.H != RoomCells) return "step outside first";
    double range = Num(player, Objects.o_player.Vars.VSN, 8) * Cell;
    var (px, py) = (Num(player, "x"), Num(player, "y"));
    if (Objects.o_enemy.Object is { } enemies)
    {
        foreach (var e in enemies.Instances())
        {
            if (!e.Exists || Num(e, Objects.o_enemy.Vars.is_hostile) <= 0 || Num(e, Objects.o_enemy.Vars.HP) <= 0) continue;
            if (Math.Max(Math.Abs(Num(e, "x") - px), Math.Abs(Num(e, "y") - py)) <= range)
                return "there are enemies nearby";
        }
    }
    return null;
}
```

<small>Source: [managed/Mods/FastTravel/Traveller.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/Traveller.cs#L43-L61)</small>

Enemies come from the interop: `Objects.o_enemy.Object` is the `GmlObject` (so `Instances()` includes
child objects), and `Objects.o_enemy.Vars.is_hostile` and `.HP` are variable names the interop
harvested from live instances while the game was played, so a typo is a compile error. The `Vars`
class is the reason to prefer the interop over string literals for instance variables. `Num` is a
small helper that reads a variable and falls back when it is not a number.

The refusal returns a `string?` reason instead of throwing, so the caller can show it in the game's
own action log (the `Say` helper calls `Scripts.scr_actionsLogAddMessage.CallAs(p, text)`).

### Fixing a bad arrival

The room's own start code puts the player at `localX/localY` and "nothing checks that the spot is
free". After arriving, `Settle` reads the room's `wallgrid` and `posgrid` from `o_controller`
through `Builtins.ds_grid_get`, floods open ground from the room's edges, and if the player stands
on a blocked or walled-in cell, teleports them with the game's own script:

```csharp
Scripts.scr_invisible_teleport.CallAs(player, (cx + dx) * Cell + Cell / 2, (cy + dy) * Cell + Cell / 2);
_log.Info($"arrival cell {cx},{cy} was blocked or walled in; moved to {cx + dx},{cy + dy}");
```

<small>Source: [managed/Mods/FastTravel/Traveller.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/Traveller.cs#L180-L181)</small>

Using `scr_invisible_teleport` instead of writing `x`/`y` keeps the game's grids in step. Waiting for
the arrival is a small state machine driven by `Tick()` from `OnUpdate`: ten frames of settling after
the new player appears, and a timeout of 60 * 20 frames (`ArrivalTimeoutFrames`, about 20 seconds at 60 fps) that puts the map's cell back
if the room never changed. A mod that starts something the game finishes later needs both, because a
refusal inside the game is silent.

## UI drawn with the game's own pieces {#ui}

`MapUi.cs` does two things so the feature looks native: it adds an entry to the map's controls bar,
and it draws a banner. Both use the game's own text and board scripts rather than ImGui or raw
`draw_text`.

### An entry in an existing bar

The bar keeps its entries as a ds_list of "colour text" maps (`textMapsList`), each made by the game's
`scr_colorTextCreate`. The mod makes one the same way and appends it:

```csharp
var list = bar.Get(Objects.o_globalmapControlsRender.Vars.textMapsList);
if (!list.IsNumber) return;
// Made and measured before the bar is touched, so a refusal leaves it as the game built it.
var map = Builtins.ds_map_create();
double w;
try
{
    Scripts.scr_colorTextCreate.CallAs(bar, bar, map, EntryText, White, 1000, 1);
    w = new DsMap(map).Get("width").AsReal;
}
catch (GmlException)
{
    Builtins.ds_map_destroy(map);
    throw;
}
Builtins.ds_list_add(list, map);
// Owned by the bar's list from here on: destroyed with it, like its own.
Builtins.ds_list_mark_as_map(list, Builtins.ds_list_size(list).AsReal - 1);
```

<small>Source: [managed/Mods/FastTravel/MapUi.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/MapUi.cs#L56-L73)</small>

The order is a design decision: create and measure the new entry first, touch the bar only if that
worked, and destroy the map if it did not. `ds_list_mark_as_map` hands ownership to the bar's list,
so the game frees the map when it frees the list. The rest of `AppendEntry` grows the bar's `width`,
`contentWidth` and `surfaceWidth` by the entry plus its spacing and sets `surfaceRecreate` so the bar
redraws. The class remark records the measured layout (`142 + 144 + 89 + 128 = 503` for the four
vanilla entries) that the arithmetic was checked against.

`AppendEntry` receives the bar as an `Instance` and uses `bar.Get(...)` and `bar.Set(...)` on it
directly, which is why the hook handler passes `c.Self` through without storing it.

The entry text is `[~lg~F~/~] - Fast Travel`: `~lg~` and `~/~` are the game's own colour markup,
which `scr_colorTextCreate` understands.

### A banner on the game's board

```csharp
public void DrawBanner(string text)
{
    double map = TextMap(text);
    var data = new DsMap(map);
    double w = data.Get("width").AsReal + 2 * Pad, h = data.Get("height").AsReal + 2 * Pad;
    double x = Math.Round((GameDraw.GuiWidth - w) / 2), y = 24;
    _banner = (x, y, w, h);
    Scripts.scr_globalmapDrawBoard.Call(x, y, w, h, BoardScale);
    Scripts.scr_colorTextDraw.Call(map, x + Pad, y + Pad, 0, 0, 0, 1);
}
```

<small>Source: [managed/Mods/FastTravel/MapUi.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/MapUi.cs#L157-L166)</small>

This runs from the `GameDraw.OnGui` handler: `scr_globalmapDrawBoard` draws the same panel the controls
bar sits on, and `scr_colorTextDraw` draws the colour-text map on it. The banner is sized from the
text map's own measured `width` and `height`, centred with `GameDraw.GuiWidth`, and its rectangle is
remembered in `_banner` so a click on it is not treated as a click on the map.

Making a colour-text map costs script calls, so the maps are cached per string:

```csharp
private double TextMap(string text)
{
    if (_textMaps.TryGetValue(text, out var id) && Builtins.ds_exists(id, 1).AsBool) return id;
    if (_textMaps.Count > 32) Clear();
    var map = Builtins.ds_map_create();
    Scripts.scr_colorTextCreate.Call(map, text, White, 1600, 1);
    return _textMaps[text] = map.AsReal;
}
```

<small>Source: [managed/Mods/FastTravel/MapUi.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/MapUi.cs#L171-L178)</small>

The cache stores the map's id as a `double`, which is plain C# data, not an `RValue`. The
`ds_exists(id, 1)` check (type 1 is a map) guards against the game having destroyed it. The cache is
capped at 32 entries and destroyed by `Clear()` from `OnShutdown`.

## How you would find these names {#discovery}

None of the script or variable names above come from documentation. They come from the workflow in
[finding hooks](../finding-hooks.md):

1. Open the map with the Console and ScriptSpy mods loaded.
2. Search: the Console's `find globalmap` lists scripts, events and builtins whose names contain it
   (`scr_globalmapDrawBoard`, `scr_globaltile_get_room`); the Objects tab lists `o_globalmap...`
   objects and their live instances; `where playerGrid` finds the global that holds the cell.
3. Watch: `spy.watch` on a candidate (`scr_smoothRoomChange`) while doing the action once by hand
   shows its arguments, and with a variable name it shows what changes (`localX`, `playerGridX`).
4. Read: `code scr_smoothRoomChange` lists what it calls and its strings, and `callers` shows who calls
   it (the border-crossing object, which is where the sequence in `Traveller.Go` came from).

The harness mod can replay and verify an action over the test host, which is how `Traveller`'s
behaviour was confirmed on a live game ("live: arriving on a market stall or inside a house left the
player there", in the source remarks).

## Test host commands {#test-host}

```csharp
TestHost.Register("ft.state", _ => new
{
    open = WorldMap.IsOpen,
    mode = _mode,
    busy = _traveller.Busy,
    cell = new[] { WorldMap.PlayerCell.X, WorldMap.PlayerCell.Y },
    hovered = WorldMap.HoveredCell is { } h ? new[] { h.X, h.Y } : null,
    visited = WorldMap.Visited().Select(v => $"{v.X}_{v.Y}").OrderBy(s => s).ToArray(),
}, "ft.state: map open, mode, travelling, the player's cell, the hovered cell and every visited cell");
TestHost.Register("ft.mode", args =>
{
    if (args.Count > 0) SetMode(args[0].GetString() is "on" or "true" or "1", quiet: true);
    return _mode;
}, "ft.mode [on|off]: the fast travel mode");
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L179-L192)</small>

A handler may return any object; its public properties are serialised to JSON. `ft.judge <x> <y>`
and `ft.travel <x> <y> [arriveX arriveY]` go through exactly the code a click does (`ft.travel`
calls `TryTravel`, "the same checks"), so a test exercises the real path. The optional arrival point
exists to test the blocked-arrival fix by sending the player to a known-bad cell.

A test then reads like this:

```powershell
tools\coreloader.ps1 -Game Stoneshard ft.judge 10 12
tools\coreloader.ps1 -Game Stoneshard ft.travel 10 12
```

See the [test host reference](../reference/test-host.md) and
[testing](../cookbook/robustness-and-testing.md).

## What to take from this mod

- **Reuse the game.** Every capability here is a game script or builtin called with the right
  context. The mod contains the *rules* (what is allowed) and the *sequence* (in what order), not
  the mechanics.
- **Convert to C# data at once.** `PlayerCell`, `Visited()` and the cached verdict are plain C#
  types. Nothing frame-bound is stored.
- **Undo on failure.** `SetCell(_from)`, `ds_map_destroy` on a refused create, and the arrival
  timeout all restore a consistent state when the game says no.
- **Prefer the game's UI pieces for game UI.** Drawing with `scr_globalmapDrawBoard` and
  `scr_colorTextDraw` gives the right fonts, scaling and colour markup for free; the cost is
  measuring layout from the live bar, which the class remarks document.
- **Record where facts came from.** The remarks say "checked live" and give the numbers. In a mod that
  depends on undocumented behaviour, that is the only maintenance documentation there will be.
