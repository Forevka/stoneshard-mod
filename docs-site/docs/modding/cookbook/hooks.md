---
title: Hooks
description: Recipes for running your code before or after a game script or object event, changing arguments and results, and hooking by name at runtime.
---

A hook runs your handler before or after a compiled GML script or object event. The `HookCall` your
handler receives is only valid while the handler runs; do not keep it. Every recipe below is a hook, so
the rules in [Hook arguments](../concepts.md#hook-arguments) and [Faults](../concepts.md#faults) apply
to all of them: an exception in a handler disables your mod, so catch what can fail (a `GmlException`
from a game call, say) inside the handler.

Where the generated interop is available (see [Interop](../interop.md)), `Scripts.<name>.Before(...)`
and `Objects.<object>.<Event>.After(...)` are the compiler-checked form. The `[HookBefore("name")]`
attribute and `Hooks.Before("name", ...)` take a string and work without the interop. Finding which
script to hook is its own topic: see [Finding hooks](../finding-hooks.md).

## Change a script's argument before it runs {#change-an-argument}

Use `[HookBefore]` (or `Scripts.x.Before`) and `HookCall.SetArg`. Stoneshard sends every source of
experience through `scr_get_XP(amount)`, so scaling its first argument scales all of them, including the
"+N XP" in the action log.

```csharp
[HookBefore("scr_get_XP")]
private void ScaleXp(HookCall c)
{
    _xpCalls++;
    if (_xpMultiplier == 1f || c.ArgCount < 1) return;
    var amount = c.GetArg(0);
    if (!amount.IsNumber || amount.AsReal <= 0) return;

    double scaled = Math.Round(amount.AsReal * _xpMultiplier);
    _xpBonus += scaled - amount.AsReal;
    c.SetArg(0, scaled);
}
```

<small>Source: [managed/Mods/StoneshardBoost/StoneshardBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardBoost/StoneshardBoost.cs#L40-L51)</small>

Gotchas:

- Check `ArgCount` before `GetArg`. Reading past the arguments the caller passed throws
  `ArgumentOutOfRangeException`, which faults your mod.
- `SetArg` changes what the original and later handlers see, and nothing else: not the caller's variable.
  A multiplier therefore cannot compound across calls.
- A string passed without the `gml_` prefix is looked up as a script (`gml_Script_` is prepended).

## Run the original script again {#run-the-original-again}

Use `[HookAfter]` and `HookCall.CallOriginal()`. The call re-runs the script with this call's self, other
and (possibly modified) arguments, and no hook handler sees it, so repeating an effect cannot recurse.
Stoneshard's `scr_loot` places one item per call and has no count parameter, so "more loot" means running
it again.

```csharp
[HookAfter("scr_loot")]
private void RepeatLoot(HookCall c)
{
    _lootCalls++;
    if (_lootMultiplier <= 1f || c.OriginalSkipped) return;

    double extra = _lootMultiplier - 1f;
    int runs = (int)Math.Floor(extra);
    if (_rng.NextDouble() < extra - runs) runs++;
    for (int i = 0; i < runs; i++)
    {
        c.CallOriginal();
        _lootExtra++;
    }
}
```

<small>Source: [managed/Mods/StoneshardBoost/StoneshardBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardBoost/StoneshardBoost.cs#L53-L67)</small>

Gotchas:

- Test `c.OriginalSkipped` first. If another mod's Before handler skipped the original, there was no
  effect to repeat.
- `CallOriginal` is for scripts only; object events take no arguments and have no result.
- The returned `RValue` is a plain copy. If it holds a string, array or struct, nothing releases that
  reference, so read what you need from it in the same call.

## Cancel a script and return your own value {#cancel-a-script}

Use a Before handler that calls `SkipOriginal()` and, for a script, sets `Result`. The caller receives
what you put in `Result`, as if the script had returned it. Stoneshard's `scr_skill_branch_study` returns
a bool; the Trials mod refuses it for a locked skill tree:

```csharp
Scripts.scr_skill_branch_study.Before(c => Guard("tree lock", () =>
{
    if (CurrentRun() is not { Boons.Count: > 0 } run || !Trees.IsLockedTreatise(c.Self, Catalog.LockedTrees(run))) return;
    c.SkipOriginal();
    c.Result = false;
    World.Say("~r~The treatise makes no sense to you~/~: that art is closed for these trials.");
}));
```

<small>Source: [managed/Mods/StoneshardTrials/TrialsMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/TrialsMod.cs#L95-L101)</small>

The same works with a string-keyed hook. TavernGames answers the game's dialogue lookup for its own key
and lets every other call through:

```csharp
// dialogue_get_string runs for every line of every conversation: only a
// string argument equal to our key is ours.
private void Answer(HookCall c)
{
    if (!IsKey(c, Key)) return;
    c.SkipOriginal();
    c.Result = _line.Length > 0 ? _line : _lines[0];
}
```

<small>Source: [managed/Mods/TavernGames/DialogueOption.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/TavernGames/DialogueOption.cs#L128-L135)</small>

An object event has no result, but it can be skipped the same way. This hook stops a skill icon's
User Event 0 from running:

```csharp
Hooks.Before("gml_Object_o_skill_ico_Other_10", c => Guard("tree lock", () =>
{
    if (CurrentRun() is not { Boons.Count: > 0 } run || !Trees.IsLockedSkill(c.Self, Catalog.LockedTrees(run))) return;
    c.SkipOriginal();
    World.Say("~r~That art is closed to you~/~ for these trials.");
}));
```

<small>Source: [managed/Mods/StoneshardTrials/TrialsMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/TrialsMod.cs#L89-L94)</small>

Gotchas:

- `SkipOriginal()` throws `InvalidOperationException` in an After handler. The original has already run.
- Setting `Result` on an object event throws too ("object events have no result").
- Skipping a script means everything it does is skipped: later After handlers still run and see
  `OriginalSkipped == true`.

## Change what a script returned {#change-the-result}

Use an After handler and read or replace `c.Result`. The result is the original's return value, and what
you store is what the caller gets. TavernGames adds a conversation option by editing the array that
`scr_dialogue_sort_options` returns:

```csharp
if (Speaker(c.Self) is not { } npc || !_willPlay(npc)) return;
var options = c.Result;
if (Gml.TypeOf(options) != "array") return;
int n = Gml.ArrayLength(options);
var keys = Enumerable.Range(0, n).Select(i => Gml.ArrayGet(options, i)).Select(v => v.Kind == RValueKind.String ? v.ToString() : "").ToList();
// Only on the conversation's main menu, the one that offers a way out.
if (!keys.Contains(LeaveKey) || keys.Contains(Key)) return;
_line = _lines[Random.Shared.Next(_lines.Length)];
Builtins.array_insert(options, keys.IndexOf(LeaveKey), Key);
```

<small>Source: [managed/Mods/TavernGames/DialogueOption.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/TavernGames/DialogueOption.cs#L112-L120)</small>

It is installed with `Scripts.scr_dialogue_sort_options.After(AddOption);`
([DialogueOption.cs line 67](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/TavernGames/DialogueOption.cs#L67)).
The handler edits the array in place with a builtin. To return a different value, assign `c.Result`
instead. The array is a pooled value; see [Values](../concepts.md#values).

Gotchas:

- Check the result's type (`Gml.TypeOf`) before treating it as an array, as the source does.
- The whole body in the source sits in a `try` that catches `GmlException`, because the game calls
  `array_insert` on its own data and a bad frame must not cost the mod.

## Run code when an instance is destroyed {#instance-destroyed}

Hook the object's Destroy event with `Before`. In a Before handler the instance still has all its
variables, so you can read its health, flags or position. Stoneshard's `o_enemy` Destroy event is the
death of an enemy: the loot is dropped and the dungeon's `boss_alive` is cleared there.

```csharp
Objects.o_enemy.Destroy_0.Before(c => Guard("enemy death", () => OnEnemyDestroyed(c)));
```

<small>Source: [managed/Mods/StoneshardTrials/TrialsMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/TrialsMod.cs#L84)</small>

```csharp
// o_enemy's Destroy is its death: it drops the loot and clears the dungeon's
// boss_alive. A unit unloaded with its room still has its HP. Only the trial's
// own dungeon pays, and only once: a miniboss and a boss do not make two.
private void OnEnemyDestroyed(HookCall c)
{
    if (!_enabled || CurrentRun() is not { Won: false } run) return;
    var self = c.Self;
    if (self.IsNull) return;
    bool boss = World.Truthy(self.Get("isBoss")) || World.Truthy(self.Get("isMiniboss"));
    var hp = self.Get("HP");
    if (!boss || !hp.IsNumber || hp.AsReal > 0 || !InTrial) return;
    // ...
}
```

<small>Source: [managed/Mods/StoneshardTrials/TrialsMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/TrialsMod.cs#L521-L537)</small>

Gotchas:

- A Destroy event also runs when a room is unloaded and its instances go with it. Check the state you
  care about (here `HP <= 0`) rather than assuming the instance died.
- `c.Self` is the instance the event runs as; check `IsNull` before reading from it.
- The mod's `Guard` wrapper catches exceptions and logs them, so one bad read does not fault the mod.
  See [Robustness and testing](robustness-and-testing.md#guard).

## Run code when an instance is created {#instance-created}

Hook the object's Create event with `After`, so the game's own initialisation has already filled in the
instance's variables. ModMenu adds a button to Stoneshard's pause menu this way: the menu is an
`o_close_panel` that builds its buttons in its Create event.

```csharp
Objects.o_close_panel.Create_0.After(c => Guard("menu", () => AddMenuButton(c.Self)));
```

<small>Source: [managed/Mods/ModMenu/ModMenuMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L40)</small>

Gotchas:

- Create events run once per instance, including instances that exist before your mod loads. A hook
  installed in `OnInitialize` does not see those. Iterate the live instances once if you need them (see
  [Iterate every instance of an object](game-state.md#iterate-instances)).
- `c.Self` is an `Instance` (a pointer valid during the handler). To keep it, store `new InstanceRef(c.Self.Get("id"))`
  and resolve it later; see [Game state](game-state.md#singleton).

## Run code every step of an object {#every-step}

Hook the object's Step event. Stoneshard's `o_controller` exists for the whole session, so its Step
events are a reliable per-frame place that also runs at a known point in the frame. Step_1 is Begin
Step, which runs before the game reads any key, so ModMenu uses it to swallow the keyboard (see
[Input](input.md#swallow-keys)):

```csharp
Objects.o_controller.Step_1.Before(_ =>
{
    if (!_open) return;
    try
    {
        if (Builtins.keyboard_check_pressed(27).AsBool) _escPressed = true;
        Builtins.io_clear();
    }
    catch (GmlException) { }
});
```

<small>Source: [managed/Mods/ModMenu/ModMenuMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L48-L57)</small>

The interop example does the same on a Dwarf Eats Mountain unit, counting its Step events:

```csharp
Objects.oMiner.Step_0.Before(_ => _unitSteps++);
```

<small>Source: [managed/Examples/InteropExample/InteropExample.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/InteropExample/InteropExample.cs#L25)</small>

:::tip
If you only need "once a frame" and not "at this point of the frame", override `CoreMod.OnUpdate`
instead. A Step hook on an object with hundreds of instances runs hundreds of times a frame.
:::

## Hook a user event {#user-events}

User events (`Other_10` to `Other_25`, User Event 0 to 15) are how GameMaker objects get called by
other code, so they are often the cleanest hook point. FastTravel appends its entry to Stoneshard's
world-map controls bar after the bar's own event has drawn it:

```csharp
Objects.o_globalmapControlsRender.Other_20.After(c =>
{
    try { _ui.AppendEntry(c.Self); }
    catch (GmlException ex) { Log.Warning($"could not add the Fast Travel entry to the map: {ex.Message}"); }
});
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L39-L43)</small>

ModMenu hooks `o_ingame_menu_button.Other_10` the same way to react to a click on a menu button
([ModMenuMod.cs line 41](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L41)).

## Event names {#event-names}

An object event's symbol is `gml_Object_<object>_<Event>_<n>`, and the generated interop exposes it as
`Objects.<object>.<Event>_<n>`. The loader splits a symbol at the last event keyword that is followed by
a suffix (`InteropGenerator.SplitEvent`), because object names can themselves contain underscores and
event-like words. The keywords it knows are `PreCreate`, `Create`, `Destroy`, `CleanUp`, `Step`, `Alarm`,
`Draw`, `Mouse`, `KeyPress`, `KeyRelease`, `Keyboard`, `Collision`, `Other`, `Gesture` and `Async`.

| GameMaker event | EventRef member | Symbol |
|---|---|---|
| Create | `Create_0` | `gml_Object_o_x_Create_0` |
| Destroy | `Destroy_0` | `gml_Object_o_x_Destroy_0` |
| Step | `Step_0` | `gml_Object_o_x_Step_0` |
| Begin Step | `Step_1` | `gml_Object_o_x_Step_1` |
| End Step | `Step_2` | `gml_Object_o_x_Step_2` |
| Alarm N | `Alarm_N` (`Alarm_0` to `Alarm_11`) | `gml_Object_o_x_Alarm_N` |
| Draw | `Draw_0` | `gml_Object_o_x_Draw_0` |
| Draw GUI | `Draw_64` | `gml_Object_o_x_Draw_64` |
| Draw Begin / Draw End | `Draw_72` / `Draw_73` | `gml_Object_o_x_Draw_72`, `_73` |
| Room Start / Room End / Animation End | `Other_4` / `Other_5` / `Other_7` | `gml_Object_o_x_Other_4`, `_5`, `_7` |
| User Event N | `Other_(10+N)`, so `Other_10` to `Other_25` | `gml_Object_o_x_Other_10` ... |
| Mouse, key, collision, async | `Mouse_N`, `KeyPress_N`, `Collision_<suffix>`, `Async_N` | as listed by the game |

The numbers are GameMaker's own event sub-types. The ones above occur in Stoneshard's symbol list. Only
the events an object actually has exist as symbols: `Objects.o_x.Step_2` compiles only if the game
defines an End Step for `o_x`, and a hook on one the game never compiled throws `GmlException` when
installed (the symbol "does not exist"). To see what an object has, search the symbol list in the
overlay's Console, or read `Lodestone\Interop\<Game>.Interop\Objects.g.cs`. If two events of one object
collapse to the same C# name, the generator keeps the first and drops the rest.

:::note
Hooks on Step and Draw events are the expensive ones: a hooked Step event can run thousands of times a
frame. Keep the handler small and bail out early.
:::

## Hook a function chosen at runtime, and unhook it later {#hook-by-name}

Use `Hooks.Before` and `Hooks.After` with a string. Each returns a `HookHandle`; dispose it to unhook.
When the last handler on a function is disposed, the loader detaches the detour and the function runs at
full speed again. ScriptSpy lets you type any function name and hooks it:

```csharp
var before = Hooks.Before(symbol, c => OnBefore(w!, c));
var after = Hooks.After(symbol, c => OnAfter(w!, c));
```

<small>Source: [managed/Mods/ScriptSpy/ScriptSpy.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ScriptSpy/ScriptSpy.cs#L156-L157)</small>

```csharp
if (UI.Button("Unhook"))
{
    w.Before.Dispose();
    w.After.Dispose();
    _watches.Remove(w);
}
```

<small>Source: [managed/Mods/ScriptSpy/ScriptSpy.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ScriptSpy/ScriptSpy.cs#L60-L65)</small>

`Hooks.Before` throws `GmlException` if the name is not in the game, so wrap a user-typed name in a
`try`. ScriptSpy does, and shows the message in its tab. The accepted symbols start with `gml_Script_`,
`gml_Object_`, `gml_RoomCC_` or `gml_GlobalScript_`; a name with no `gml_` prefix is tried as a script.
Anything else is refused.

Hooks are shared: however many mods hook one function, it is detoured once. Everything you register
belongs to your mod and is removed when it unloads, so disposing handles is only needed when you want to
stop earlier. See [Ownership](../concepts.md#ownership).

## Run something once inside the next matching call {#next-call}

Use `Hooks.NextAfter` (or `Hooks.NextBefore`, which runs before the original). Some scripts only work
from inside the event they were written for and throw when called from a tab or from `OnUpdate`. Arm a
one-shot request, make the game run that event, and do the work inside it. The request is disarmed
before your action runs, so it fires at most once; it has a timeout, and `onTimeout` runs between frames
if the call never comes.

Stoneshard rolls a potion's effects in `o_inv_bottle`'s Alarm 0, a step after the bottle is made. The
Trials mod arms the request first, then gives the bottle:

```csharp
var request = Hooks.NextAfter(BottleAlarm,
    c => !c.Self.IsNull && !c.OriginalSkipped && (aimed < 0 || World.IdKey(c.Self.Get("id")) == aimed),
    c =>
    {
        try { log.Info($"potion: {Rewrite(c.Self, tags)}"); }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException) { log.Warning($"potion: {ex.Message}"); }
    },
    BottleTimeout, () => log.Warning("potion: the bottle never rolled; it stays a plain potion"));
```

<small>Source: [managed/Mods/StoneshardTrials/Cards/Effects.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Cards/Effects.cs#L210-L217)</small>

Gotchas:

- Arm the request before you make the game run the event. The alarm may come on the next step or sooner.
- `match` picks which call is yours (null accepts the first). Here it compares the bottle's id so a
  loot bottle rolling in the same step is not rewritten.
- `match`, `action` and `onTimeout` run as hook handlers. An exception from any of them faults the mod,
  so catch inside them, as above.
- Dispose the returned `NextCall` to cancel, and check `IsPending`. If you cannot single out the right
  call, cancel the request instead of letting it rewrite whichever call comes next.

For the same technique used to call a script that must run inside an event, see
[Calling the game](calling-the-game.md#inside-the-event).

## Attribute, typed ref or string: which form to use {#attribute-or-typed}

All three install the same hook; pick by what you have and where the handler lives. The lines below
are quoted from two mods and placed side by side:

```csharp
[HookBefore("scr_get_XP")]                                                  // attribute, string symbol
Scripts.scr_get_XP.Before(c => Guard("experience", () => OnExperience(c))); // typed, from the interop
Hooks.Before("gml_Object_o_skill_ico_Other_10", c => Guard("tree lock", () => { /* ... */ }));
```

<small>Source: [managed/Mods/StoneshardBoost/StoneshardBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardBoost/StoneshardBoost.cs#L40) and [managed/Mods/StoneshardTrials/TrialsMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/TrialsMod.cs#L87-L89)</small>

- **Attribute** (`[HookBefore]`, `[HookAfter]`): the method must be `void M(HookCall call)`. Static methods
  work anywhere in the mod's assembly; instance methods only on the mod class itself. The loader
  attaches them for you, and they go away with the mod.
- **Typed** (`Scripts.x.Before`, `Objects.o.Event.After`): a typo is a compile error, and a script the
  game dropped in an update fails with its name in the message.
- **String** (`Hooks.Before`): works in any game with no generated interop, and is the only form for a
  name you only know at runtime.

An `EventRef` also has `Call(self)` to run the event yourself, and a `ScriptRef` has `Call`/`CallAs`
(see [Calling the game](calling-the-game.md)).
