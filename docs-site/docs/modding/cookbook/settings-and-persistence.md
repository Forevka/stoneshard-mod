---
title: Settings and persistence
description: Recipes for saving a mod's settings, offering them in the game's own menu, reading files from the mod folder, storing data in the save, and surviving a hot reload.
---

A mod has four places to keep something, and they differ in how long it lives and who can see it:

| Where | Lives until | Use it for |
|---|---|---|
| `Config` (a JSON file next to the mod) | the player deletes it | settings the player chooses |
| Files in the mod folder | the player deletes them | data you ship, or logs and exports you write |
| A game variable that the game saves | the save is deleted, and **rolls back with it** | per-character state that must match the save |
| A game global of your own | the game exits | state that must survive a hot reload |

## Save a setting {#config}

`CoreMod.Config` is a flat key-value store. Read with `Config.Get(key, fallback)`; the fallback's type picks
the overload (`double`, `float`, `int`, `bool` or `string`). Write with `Config.Set(key, value)`.
StoneshardBoost reads its two multipliers at start, and saves the XP one when the overlay slider moves:

```csharp
public override void OnInitialize()
{
    _xpMultiplier = Config.Get("xpMultiplier", 1f);
    _lootMultiplier = Config.Get("lootMultiplier", 1f);
    Log.Info($"XP x{_xpMultiplier}, loot x{_lootMultiplier}");
}
```

<small>Source: [managed/Mods/StoneshardBoost/StoneshardBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardBoost/StoneshardBoost.cs#L33-L38)</small>

```csharp
if (UI.SliderFloat("XP x##xp", ref _xpMultiplier, 1f, 10f)) Config.Set("xpMultiplier", _xpMultiplier);
```

<small>Source: [managed/Mods/StoneshardBoost/StoneshardBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardBoost/StoneshardBoost.cs#L72)</small>

The file is named after the dll (`<dll name>.json`) and sits next to it, and `Config.Path` gives the full path. Details
that matter:

- **Saves are batched.** A change is written about a second after it was made, so a dragged slider saves
  once and not every frame, and again on shutdown. The write goes through a temporary file, so a crash
  never leaves a truncated file. `Config.Save()` writes pending changes immediately.
- **The player can edit it** while the game is closed (comments and trailing commas are accepted). Treat
  every `Get` as input that may be missing or the wrong type: the fallback is what you get back.
- **Any stored number reads through any numeric overload.** `Get("n", 0)` rounds it to an `int`,
  `Get("n", 0.0)` returns the `double`.

Gotchas:

- Setting a value equal to the stored one does nothing, so calling `Set` from a widget's return value is
  fine. A value that really changes every frame would rewrite the file every second, so save on change.
- The file is named after the dll file and sits in its folder, so two dlls with the same file name in the same folder share a config.

## Offer settings in the game's own menu {#mod-settings}

Register controls with `ModSettings.Toggle`, `Slider` and `Choice` in `OnInitialize`. Each is bound to a
key of your `Config`, with a label and a description, and returns a `Setting` you read when you need
it (`GetBool`, `GetNumber`; `SetBool`, `SetNumber`, `Nudge`, `Reset` and `ValueText` also exist). The
loader only keeps the list; a front end draws it. In Stoneshard that is the ModMenu mod: an Esc-menu
entry named **MODS** that opens a window drawn with the game's own board and buttons. The Trials mod
registers its options like this:

```csharp
_enabledSetting = ModSettings.Toggle(this, "enabled", "Trials",
    "Off: the tavern door leads to Osbrook and the world map opens again.", true);
_xpScale = ModSettings.Slider(this, "xpScale", "Kill experience",
    "Experience from kills, the only source during the trials.", 1, 0, 3, 0.25, v => $"x{v:0.##}");
_goldScale = ModSettings.Slider(this, "goldScale", "Trial reward",
    "Crowns paid for each trial won.", 1, 0, 3, 0.25, v => $"x{v:0.##}");
_difficulty = ModSettings.Choice(this, "difficulty", "Difficulty",
    "Easy: half a tier lower. Hard: half higher, +1 enemy. Brutal: a tier higher, +2, elite master.",
    1, Difficulties);
```

<small>Source: [managed/Mods/StoneshardTrials/TrialsMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/TrialsMod.cs#L69-L77)</small>

The arguments are `(mod, key, label, description, default, ...)`, then for a slider `min, max, step` and an
optional formatter, and for a choice the default index and the option names. The ModMenu author notes show
the three kinds together:

```csharp
var xp = ModSettings.Slider(this, "xpScale", "Kill experience", "Experience from slain enemies.",
                            1, 0, 3, 0.25, v => $"x{v:0.##}");
ModSettings.Toggle(this, "enabled", "Trials", "Off: the mod stands aside.", true);
ModSettings.Choice(this, "mode", "Mode", "", 0, new[] { "Easy", "Normal", "Hard" });
double now = xp.GetNumber();   // read when you need it; the window writes the config
```

<small>Source: [managed/Mods/ModMenu/README.md](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/README.md#L22-L28)</small>

Read the setting where you use it (`_enabledSetting.GetBool()` in the Trials mod), not once at start: the
player can change it at any time and the window writes the new value to your config.

Gotchas:

- A mod that registers settings needs nothing else from ModMenu. Without it installed, the settings still
  work from the mod's `Mods\<Mod>.json`, and in any other game the list is not drawn. Register
  them once, for every game.
- Registrations belong to your mod: they go when it unloads or faults, and `ModSettings.Version` changes
  when the list does, for a front end that caches it.
- `ModSettings` is game-thread only.

## Read a file from the mod folder {#mod-folder}

`CoreMod.Directory` is the folder the mod's dll was loaded from. Resolve your own files against it. ScriptSpy
reads a list of functions to watch from a text file next to its dll, so watches can start with the game:

```csharp
// Watches listed in ScriptSpy.txt next to the dll (one symbol per line)
// start with the game - handy for functions that only run at startup.
var file = Path.Combine(Directory, "ScriptSpy.txt");
if (!File.Exists(file)) return;
foreach (var line in File.ReadAllLines(file))
{
    var s = line.Trim();
    if (s.Length > 0 && !s.StartsWith('#')) AddWatch(s);
}
```

<small>Source: [managed/Mods/ScriptSpy/ScriptSpy.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ScriptSpy/ScriptSpy.cs#L88-L96)</small>

Gotchas:

- `Directory` here is the `CoreMod` property. Inside a mod class it shadows `System.IO.Directory`; write
  `System.IO.Directory.CreateDirectory(...)` if you need that one.
- Sprites and sounds have their own rule: `Content.AddSprite("assets/x.png")` looks in a folder named
  after your dll. See [Drawing and UI](drawing-and-ui.md#sprite-and-sound).
- The Trials mod writes under `Mods/StoneshardTrials/characters/`, a folder named after the mod.
  Keep to a folder of your own, not the shared `Mods` root.
- A file read or written on the game thread blocks the frame. For anything large, use a `Task` (see
  [Robustness and testing](robustness-and-testing.md#background-work)).

## Keep per-character data inside the save {#save-data}

State that must match a save (a run's progress, say) should live **in** the save, so loading an earlier
save rolls it back with the game. The Trials mod keeps its run on the player character as an attribute
holding JSON. The code's own comment gives the reason:

> A run is kept on its character, as a player attribute holding the run as JSON, which the game saves
> with the character. So it rolls back with the save: a reload from before a boss fell finds that boss
> alive and the trial not yet won.

It reads the attribute with the game's `scr_atr` and writes it with `scr_atr_set_simple`, both run as the
player:

```csharp
public static string Stored(InstanceRef player)
{
    var v = Scripts.scr_atr.CallAs(player, Attribute);
    return v.Kind == RValueKind.String ? v.ToString() : "";
}
```

<small>Source: [managed/Mods/StoneshardTrials/Run.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Run.cs#L107-L111)</small>

```csharp
string stored = JsonSerializer.Serialize(run, Compact);
Scripts.scr_atr_set_simple.CallAs(player, Attribute, stored);
Mirror(run);
```

<small>Source: [managed/Mods/StoneshardTrials/Run.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Run.cs#L136-L138)</small>

`Mirror` also writes a readable copy to disk, for the player to look at, not for loading. The disk copy
cannot be the source of truth: a reload would then disagree with the save.

Gotchas:

- This is Stoneshard's attribute system. Another game has its own place that its save covers; find it
  with the Console and the [Finding hooks](../finding-hooks.md) techniques.
- A string attribute is only as safe as your parser. Treat a missing or damaged value as "no run yet":
  `RunStore.Read` catches `JsonException` and starts over.
- If a save already holds a value from an older build of your mod, read it tolerantly. Players keep old
  saves.

:::danger
Writing to a save from outside the game (the files under `%LOCALAPPDATA%\StoneShard`) can corrupt it.
Back up the folder first, and prefer going through the game's own scripts, as above.
:::

## Survive a hot reload {#hot-reload}

A hot reload loads a new copy of your mod and unloads the old one, so your classes (and their static
fields) start again from their initial values. When state must survive (here, the room the player is in, which the game does not
let a mod read), keep it in a game global rather than a field. The StoneshardHarness mod does:

```csharp
// Kept in a game global rather than a field, so a hot reload of the mod
// (a new copy of this class) still knows the room.
private const string RoomGlobal = "__harness_room";

/// <summary>The room last changed to, by index; -1 until one is seen.</summary>
public static int TrackedRoom
{
    get => Globals.Get(RoomGlobal) is { IsNumber: true } v ? (int)v.AsReal : -1;
    set => Globals.Set(RoomGlobal, value);
}
```

<small>Source: [managed/Mods/StoneshardHarness/World.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/World.cs#L20-L29)</small>

Gotchas:

- A global lasts until the game exits, so it does not survive a restart. Use `Config` or the save for
  that.
- Choose a name unlikely to clash (`__yourmod_thing`), and check `Globals.Exists` or the type on read,
  as `IsNumber` does above.
- Hot reload loads the new build first. If it cannot load yet, the running copy stays. The old copy's
  `OnShutdown` runs when it is replaced.

## Clean up in OnShutdown {#on-shutdown}

`OnShutdown` runs on unload, hot reload and game exit. The loader releases everything that is registered
through its API (hooks, draw handlers, content, picks, settings, test commands), so most mods need no
cleanup. Use it for what the loader cannot see: a value you changed in the game and must put back.
SpeedControl restores the game's own frame rate:

```csharp
public override void OnShutdown()
{
    if (_active) Apply(_baseFps);
}
```

<small>Source: [managed/Mods/SpeedControl/SpeedControl.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/SpeedControl/SpeedControl.cs#L60-L63)</small>

FastTravel destroys the ds_maps it created for text
([FastTravelMod.cs line 49](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L49)), and ModMenu cancels its pick and clears its canvas
([ModMenuMod.cs lines 63-67](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L63-L67)).

The rule of thumb: if you changed a game value that would stay changed once your mod is gone, restore
it here. See [Ownership](../concepts.md#ownership) for what the loader releases for you.
