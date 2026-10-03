---
title: Calling the game
description: Recipes for calling the game's scripts, builtins and object events, looking up assets and playing sounds, and running code inside the game's own event.
---

Everything the game can do from GML, a mod can ask it to do. There are three kinds of callee: compiled
**scripts** (`scr_*` and friends), **builtins** (GameMaker's own functions such as `instance_create_depth`),
and **object events** (the compiled Create, Step, Alarm bodies). Each has a typed form from the generated
interop (see [Interop](../interop.md)) and an untyped form that works in any game. A call the game
rejects throws `GmlException` with the GML error's message, so catch it where the call can legitimately fail
(see [Robustness and testing](robustness-and-testing.md#catch-gmlexception)).

All calls must come from the [game thread](../concepts.md#game-thread). A string, array or struct a call
returns is pooled and released at the end of the frame ([Values](../concepts.md#values)).

## Call a script {#call-a-script}

Use `Scripts.<name>.Call(args)` from the interop. It returns an `RValue`. FastTravel asks the game which
room a world-map cell loads:

```csharp
public static int RoomOf(int x, int y) => (int)Scripts.scr_globaltile_get_room.Call(x, y).AsReal;
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L96)</small>

A script that reads `self` needs an instance to run as. `CallAs(self, args)` takes an `InstanceRef` (it
resolves it first), or an `Instance` pair for `self` and `other`. FastTravel writes a line to the game's
action log as the player:

```csharp
if (Objects.o_player.First is { } p) Scripts.scr_actionsLogAddMessage.CallAs(p, text);
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L172)</small>

Without the interop, call by name with `Game.CallScript("scr_name", args)`, or run as an instance with
`Game.CallScriptAs(self, other, "scr_name", args)`. The name may be the full symbol
(`gml_Script_scr_foo`) or the script name. The Trials mod runs a potion script as the bottle:

```csharp
Game.CallScriptAs(bottle, bottle, "scr_potion_set_param");
```

<small>Source: [managed/Mods/StoneshardTrials/Cards/Effects.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Cards/Effects.cs#L248)</small>

Where the generator could read how many arguments a script uses, it gives the ref a typed `Invoke`, so
a call with the wrong number of arguments is a compile error. The interop example calls `key_to_string`,
which reads exactly one argument:

```csharp
if (UI.Button("Scripts.key_to_string.Invoke(32)"))
    _status = $"key 32 is {Scripts.key_to_string.Invoke(32)}";
```

<small>Source: [managed/Examples/InteropExample/InteropExample.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/InteropExample/InteropExample.cs#L45-L46)</small>

`Invoke` exists only for scripts with 1 to 8 guarded arguments; `Call(params RValue[])` works on every
script, including ones that index `argument[i]` dynamically.

Gotchas:

- **Self matters.** A script that reads `self` fails when it has none. Inside a hook the loader has no
  current instance to run a script as and refuses the call, so the Trials mod runs its world queries as
  the player:

  ```csharp
  // Inside a hook the loader has no current instance to run a script as and
  // refuses the call, so world queries run as the player.
  private static RValue Run(ScriptRef script, params RValue[] args) =>
      Player is { } p ? script.CallAs(p, args) : script.Call(args);
  ```

  <small>Source: [managed/Mods/StoneshardTrials/World.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/World.cs#L100-L103)</small>

- `CallAs(InstanceRef, ...)` throws if the instance no longer exists, or if this runtime's instance lookup
  is unavailable (`Game.CanResolveInstances`).
- `ScriptRef.Exists` tells you whether the running game has the script, which is how a mod written for
  one version copes with another.
- Some scripts only work inside the event they were written for. See
  [Run code inside the game's own event](#inside-the-event).

## Call a builtin {#call-a-builtin}

Use `Builtins.<name>(args)` from the interop. The wrapper's signature carries the argument count this
game's runtime registers. The interop example rolls a die:

```csharp
if (UI.Button("Builtins.irandom(6) + 1"))
    _status = $"rolled {Builtins.irandom(6).AsReal + 1}";
```

<small>Source: [managed/Examples/InteropExample/InteropExample.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/InteropExample/InteropExample.cs#L35-L36)</small>

Without the interop, use `Game.CallBuiltin("name", args)`. A builtin's argument count can differ
between runtimes (the manual says one, some registries say two) and a call with the wrong count is
refused. `Game.BuiltinArity("name")` tells you what the running game expects, or `null` if the builtin
does not exist. SpeedControl reads the frame rate whichever way the game registered it:

```csharp
private double ReadFps()
{
    try
    {
        var arity = Game.BuiltinArity("game_get_speed");
        return (arity == 2
            ? Game.CallBuiltin("game_get_speed", GamespeedFps, 0)
            : Game.CallBuiltin("game_get_speed", GamespeedFps)).AsReal;
    }
    catch (GmlException ex)
    {
        _error = ex.Message;
        return 0;
    }
}
```

<small>Source: [managed/Mods/SpeedControl/SpeedControl.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/SpeedControl/SpeedControl.cs#L67-L81)</small>

and sets it with a plain call:

```csharp
Game.CallBuiltin("game_set_speed", fps, GamespeedFps);
```

<small>Source: [managed/Mods/SpeedControl/SpeedControl.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/SpeedControl/SpeedControl.cs#L87)</small>

Gotchas:

- The result is an `RValue`. Convert with `.AsReal`, `.AsBool` or `.ToString()`, and check `IsNumber` or
  `Kind` first when the type is in doubt.
- A builtin that runs "as" an instance (a few read `self`) has `Game.CallBuiltinAs(instance, name, args)`.
- Builtins with variable-name arguments (`variable_instance_set` and friends) are handled by the
  loader: it swaps a pooled name for the permanent string of that name, because the runtime keeps a
  pointer to a created variable's name.

## Run an object event directly {#run-an-event}

Use `Game.CallEvent(symbol, self)` (or `Objects.<obj>.<Event>.Call(self)` from the interop) to run an
event body as an instance. Look the symbol up first with `Game.FindSymbol`, which returns 0 when the game
has no such event. The Trials mod gives the player a status effect by making the status object and then
running the object's own Alarm 2, so the buff sets itself up as the game made it:

```csharp
string alarm = $"gml_Object_{objectName}_Alarm_2";
if (Game.FindSymbol(alarm) == 0) throw new InvalidOperationException($"{objectName} has no Alarm 2");
```

<small>Source: [managed/Mods/StoneshardTrials/Cards/Effects.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Cards/Effects.cs#L87-L88)</small>

```csharp
if (buff.Resolve() is { } inst) Game.CallEvent(alarm, inst);
```

<small>Source: [managed/Mods/StoneshardTrials/Cards/Effects.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Cards/Effects.cs#L97)</small>

Gotchas:

- `CallEvent` takes an `Instance`, not an `InstanceRef`: turn a ref into one with `Resolve()`, which is
  `null` if the instance is gone.
- Everything the event does happens, including drawing and sounds. Run only events you have read with
  `Code.Describe` or the Console's `code` command (see
  [Robustness and testing](robustness-and-testing.md#introspection)).

## Look up an asset and play a sound {#assets-and-sounds}

Assets (sprites, sounds, rooms, objects) are looked up by name with the `asset_get_index` builtin. The
interop also gives you the names as constants under `Assets.Sprites`, `Assets.Rooms` and `Assets.Sounds`,
so a rename in a game update is a compile error. FastTravel plays the game's own sounds for switching a
mode on and off:

```csharp
try
{
    var sound = Builtins.asset_get_index(on ? Assets.Sounds.snd_gui_enable_mode : Assets.Sounds.snd_gui_disable_mode);
    if (sound.AsReal >= 0) Builtins.audio_play_sound(sound, 5, false);
}
catch (GmlException) { }
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L86-L91)</small>

Without the interop, the call is `Game.CallBuiltin("asset_get_index", "snd_gui_enable_mode")`. The same
lookup gives a sprite for drawing; FastTravel reads the game's hover frame once at start:

```csharp
_frameSprite = Builtins.asset_get_index("s_highlight_globalmap").AsReal;
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L37)</small>

Gotchas:

- An asset that does not exist comes back as a negative number, as the `>= 0` check above handles.
  Runtimes differ: older ones return a plain index, 2024 and later a typed asset reference, whose
  `AsReal` is `NaN`, so `AsReal >= 0` would reject every valid asset there. FastTravel can use it because
  it is Stoneshard-only (the older runtime); a mod for several games should read the index from the
  reference (`Int32` for a `Reference`, `AsReal` for a number). Pass the
  returned `RValue` straight back into other builtins, as the first snippet does, rather than rebuilding
  it from a number.
- Sound is cosmetic, so FastTravel swallows `GmlException` rather than let a missing sound disable the mod.
- To play a sound of your own, add it with `Content.AddSound`; see
  [Drawing and UI](drawing-and-ui.md#sprite-and-sound).

## Run code inside the game's own event {#inside-the-event}

Some scripts only work from inside the event they were written for, and throw when a mod calls them from
its tab. Arm a one-shot request with `Hooks.NextAfter` (or `Hooks.NextBefore`), make the game run the event,
and do the work there. A sketch, where `x`, `y` and `bottleIndex` stand for values you already have:

```csharp
var request = Hooks.NextAfter("gml_Object_o_bottle_Alarm_0",
    match: c => !c.OriginalSkipped,   // which call is yours; null accepts the first one
    action: c => Game.CallScriptAs(c.Self, c.Self, "scr_bottle_refresh"),
    timeout: TimeSpan.FromSeconds(2),
    onTimeout: () => Log.Warning("the bottle never rolled"));
Game.CallBuiltin("instance_create_depth", x, y, 0, bottleIndex);
// request.IsPending, request.Dispose() to cancel
```


The action runs at most once; the request is disarmed before it runs. It belongs to your mod and goes
when the mod unloads. The timeout is checked every frame, so `onTimeout` runs on time even if the event
never fires; it runs between frames, never inside the game's call. An exception from `match`, `action` or
`onTimeout` faults the mod like any hook, so catch inside them to report a failure yourself.

For a real use, the Trials mod rewrites a potion's effects inside the bottle's Alarm 0; see
[Run something once inside the next matching call](hooks.md#next-call).
