---
title: Concepts
description: The rules every CoreLoader mod lives by - the game thread, value lifetime, ownership, faults, hook arguments and hot reload.
---

The loader runs your C# inside a game that was never designed to host it. A handful of rules make that safe, and each one exists for a concrete reason. This page states the rules, the reason, and what the loader does when a mod breaks one. The analyzers that ship with the loader catch the lifetime mistakes at build time.

## Game thread {#game-thread}

GML is single-threaded. **Every callback runs on the game thread**: `OnInitialize`, `OnUpdate`, `OnGUI`, `OnShutdown`, hook handlers, draw handlers, test-host commands and queued actions. In practice that is the render thread the overlay runs on, captured on the first frame.

From any other thread (a `Task`, a timer, a file watcher) the loader refuses every call that touches the game: builtins, scripts, strings, value free and copy. That includes calls from native plugins. The refusal is logged, and managed entry points throw an `InvalidOperationException`:

```csharp
public static void EnsureGameThread()
{
    if (!OnGameThread)
        throw new InvalidOperationException(
            "GML can only be touched on the game thread - from OnUpdate, OnGUI or a hook. " +
            "Use Game.RunOnGameThread to get there from another thread.");
}
```

<small>Source: [managed/CoreLoader/Loader.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Loader.cs#L18-L24)</small>

The public facades (`Game`, `Globals`, `Values`, `Hooks`, `Content` and the rest) call `Loader.EnsureGameThread()` first, so a misuse fails at the call site with that message instead of corrupting the game.

To get from another thread to the game thread, queue an action:

```csharp
Task.Run(async () =>
{
    var data = await Download();                         // any thread
    Game.RunOnGameThread(() => Globals.Set("gold", data.Gold));
});
```

That sketch is illustrative; `Download` and `data` stand for your own code. `Game.RunOnGameThread(Action)` runs the action at the start of the next frame, as the mod that queued it. If the action queues itself again ("check again next frame"), the second run happens next frame, not in an endless loop that never lets `Present` return. An action from a mod that has been unloaded or faulted in the meantime is dropped.

## Value lifetime {#values}

A GML value is an `RValue`: a 16-byte struct holding a number, or a pointer to a string, an array or a struct. The pointers are reference-counted (strings, arrays) or garbage-collected (structs) by the game, and your C# code cannot see either mechanism.

So the loader manages it for you. **Every string, array or struct the game hands you** (call results, variable reads, `RValue.FromString`, `HookCall.CallOriginal`) is placed in a **per-frame pool and released at the end of the frame**. This has a useful consequence: using a value within the frame is always safe and never leaks. Read it, pass it to another call, assign it to a game variable (the game then takes its own reference), format a new string every frame. The loader's value-lifetime probe creates about 2,000 strings a frame and its private memory stays flat.

Only a value you keep **across frames** needs your attention:

```csharp
public static RValue Keep(RValue v)
{
    if (!HoldsReference(v)) return v;
    // Nothing is ever released on a runtime without a free helper: every
    // value already outlives the frame, and there is no copy to make.
    if (!CanFree) return v;
    if (TakeFromPool(v))
    {
        if (v.Kind == RValueKind.Object) Root(v);
        return v;
    }
    return Copy(v);
}
```

<small>Source: [managed/CoreLoader/Values.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Values.cs#L46-L58)</small>

The rules:

- **`Values.Keep(v)`** returns a reference that survives the frame. A pooled value is taken out of the pool; a value your mod does not own (a hook argument, a result slot) gets an independent copy. You then own it.
- **`Values.Free(ref v)`** releases a value you kept (or `Values.Copy`'d), and sets it to undefined. Call it when you are done, typically in `OnShutdown`.
- **Never free what the game lends you.** A hook's arguments and result belong to the caller; freeing one releases the caller's reference and the game crashes later, somewhere unrelated.
- Numbers and booleans need none of this. The simplest way to keep a value across frames is to keep the C# data instead (`AsReal`, `ToString()`).

### Kept structs are rooted

Structs are garbage-collected, not reference-counted. A struct pointer held only in C# is invisible to the collector, which frees the struct as soon as GML stops using it. So `Values.Keep` on a struct also pushes it into a GML array held in the global `__coreloader_roots`, where the collector sees it, and `Free` takes it out again. The runtime keeps its own list of who rooted what: a mod that unloads without freeing loses its roots, and roots wiped with the game's globals (a game resetting them, or `game_restart`) are pushed again.

`Values.RootedStructs` reports how many are currently rooted; it is a diagnostic.

### Analyzers CL0001 to CL0003

The mistakes the runtime can only catch as a crash are reported by the compiler instead:

| Rule | Reports |
|---|---|
| [CL0001](reference/analyzers.md#cl0001) | A field or auto-property holding an `RValue` (or an array, collection or tuple of them), or a stored, queued or registered lambda capturing one |
| [CL0002](reference/analyzers.md#cl0002) | A field or auto-property holding an `Instance` or a `HookCall`, or a stored, queued or registered lambda capturing one. Hold an `InstanceRef` instead of an `Instance` |
| [CL0003](reference/analyzers.md#cl0003) | `Values.Free` on a local read from `HookCall.GetArg` or `HookCall.Result` |

Keeping a value on purpose (a number, or one owned with `Values.Keep`) is fine; suppress the warning on that member with a reason. [Analyzers](reference/analyzers.md) shows how.

## Ownership {#ownership}

Everything a mod registers **belongs to it** and is torn down when the mod unloads, faults or hot-reloads. That covers:

- hooks (`Hooks.Before`/`After`, the `[HookBefore]`/`[HookAfter]` attributes, `Scripts.x.Before`)
- one-shot requests (`Hooks.NextBefore`/`NextAfter`)
- draw handlers (`GameDraw.OnGui`)
- content: sprites, reskins, sounds
- a pick-mode request in progress (`Input`)
- structs kept with `Values.Keep`
- queued actions (`Game.RunOnGameThread`)
- test-host commands and `ModSettings` registrations

You never write cleanup code for these. The teardown is one function:

```csharp
private static void RemoveRegistrations(LoadedMod m)
{
    Hooks.RemoveOwner(m);
    GameDraw.RemoveOwner(m);
    Input.RemoveOwner(m);
    // After OnShutdown, which is where a mod points instances away from
    // its sprites before they go.
    Content.RemoveOwner(m);
    Values.RemoveOwner(m);
    TestHost.RemoveOwner(m);
    ModSettings.RemoveOwner(m);
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L273-L284)</small>

How the loader knows whose a registration is: inside a callback, there is a "current mod". Outside one (a constructor, a `Task`, a timer) the owner is read from the **load context of the code being registered**, since every mod is loaded in its own context. So a hook registered from a background task still goes with the mod that wrote the lambda.

Content has one extra rule. An added sprite is **emptied rather than deleted** when its mod goes, so anything still showing it draws nothing instead of crashing, and its slot is reused. If several mods replace one sprite, unloading them in any order restores what was there before each one; when a mod hot-reloads, its reskin goes back on top of the stack.

### Hooks are shared

However many mods hook one function, it is detoured once, and the loader's own tools (ScriptSpy, the Console's `hook` command) share the same detour. A function nobody hooks any more is detached again, so it runs at full speed.

## Faults {#faults}

An exception that escapes a mod's callback does not reach the game. The loader catches it, logs it, and **disables that mod until it is reloaded**:

```csharp
public static void Fault(LoadedMod m, string reason, Exception? ex = null)
{
    if (m.State == ModState.Faulted) return;
    m.State = ModState.Faulted;
    m.Fault = reason;
    Hooks.RemoveOwner(m);
    // Its drawing and sounds stop, and a pick it armed is cancelled; its
    // sprites stay until it unloads, as instances may still show them.
    GameDraw.RemoveOwner(m);
    Content.StopSounds(m);
    Input.RemoveOwner(m);
    // Its settings' callbacks are its code too.
    ModSettings.RemoveOwner(m);
    if (ex != null) m.Instance.Log.Error($"{reason} - the mod is disabled until it is reloaded", ex);
    else m.Instance.Log.Error($"{reason} - the mod is disabled until it is reloaded");
}
```

<small>Source: [managed/CoreLoader/Runtime/ModManager.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/ModManager.cs#L523-L538)</small>

Other mods keep running. The faulted mod's overlay tab shows `Disabled - <reason>`, and its **Reload** button (or a rebuild, with hot reload on) brings it back. A fault comes from any callback: `OnInitialize`, `OnUpdate`, `OnGUI`, a hook handler, a queued action, a timeout handler.

### GmlException

A call into the game that the game itself rejects throws `GmlException`, with the GML error's message: for example `call to scr_x failed: Variable ... not set before reading it. (in gml_Script_scr_x, line 12)`. Nothing is faulted by the loader at that point; the exception is yours to handle. A mod that probes the live game should catch it and report the failure itself. Left uncaught in a callback, it faults the mod like any other exception.

### UI scope leaks

Widgets that open a scope (`BeginTabItem`, tree nodes, child regions, disabled blocks, text colours, clippers) are tracked. The mod may only close what it opened. If `OnGUI` returns with scopes still open, the loader closes them (so ImGui never sees an unbalanced `End()`) **and faults the mod**, because the leftovers would corrupt every tab drawn after it:

```csharp
int leaked = UI.UnwindTo(mark);
if (leaked > 0 && m.State != ModState.Faulted)
    ModManager.Fault(m, $"OnGUI left {leaked} UI scope(s) open (missing End/Pop)");
```

<small>Source: [managed/CoreLoader/Runtime/Entry.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/Entry.cs#L222-L224)</small>

### UI.Guarded

Reading the live game inside `OnGUI` can throw: an instance disappears between frames, a variable was never set. Wrap that part in `UI.Guarded`, which closes whatever scopes the draw code opened, hands you the exception, and lets the rest of the tab carry on without disabling the mod:

```csharp
public static void Guarded(Action draw, Action<Exception> onError)
{
    Guard();
    int mark = Mark;
    try
    {
        draw();
    }
    catch (Exception ex)
    {
        UnwindTo(mark);
        onError(ex);
    }
}
```

<small>Source: [managed/CoreLoader/UI.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/UI.cs#L88-L101)</small>

Use `###stableId` in labels that carry live values (`"HP: 42###hp"`), so ImGui keeps the widget's identity as the text changes.

## Hook arguments {#hook-arguments}

A script with mod hooks on it is called with **private copies of its arguments**. `HookCall.SetArg` changes what the original and later handlers see, and nothing else: not the caller's variables, and not the constants the compiler passes literals from.

The reason: YYC passes a literal argument, as in `scr_get_XP(10)`, from a constant the compiled code reads again on every call. If `SetArg` wrote into that slot, a multiplier hook would turn the `10` into `20` for good, and the next call would double it again. With private copies, a multiplier cannot compound.

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

This is StoneshardBoost's XP multiplier. Every XP source in Stoneshard goes through `scr_get_XP`, and the multiplier is applied to each call's argument. It reads the argument into a C# `double` and writes a new number back, so nothing is kept across the handler.

(A runtime without the value helpers has no copies; there the slot is the caller's and what was in it is not released.)

A `HookCall` itself is valid **only inside its handler**. It wraps a pointer to the native call frame, which is gone once the handler returns, so never keep one in a field and never capture it in a lambda that outlives the handler (analyzer CL0002). Copy out what you need: a number with `c.GetArg(0).AsReal`, or, to find an instance again later, hold an `InstanceRef` (the instance's id, which stays safe after the instance is destroyed) rather than the `Instance` that `c.Self` returns.

The members of `HookCall` are `Symbol`, `Self`, `Other`, `IsAfter`, `OriginalSkipped`, `ArgCount`, `GetArg`, `SetArg`, `Result`, `CallOriginal()` and `SkipOriginal()`. `CallOriginal()` re-runs the original script with this call's possibly modified arguments, and no hook handler (this one included) sees the extra call, so repeating an effect cannot recurse. `SkipOriginal()` is for Before handlers; in that case you set `Result` to what the caller should receive. See the [hooks cookbook](cookbook/hooks.md) for patterns.

Hooks accept a short script name (`scr_get_XP`, resolved as `gml_Script_scr_get_XP`) or a full symbol (`gml_Object_o_player_Step_0`). Only `gml_Script_*`, `gml_Object_*`, `gml_RoomCC_*` and `gml_GlobalScript_*` functions can be hooked.

## Declaring the game

Every mod declares the games it is for with `[CoreModGame("<exe name>")]` or `[CoreModAnyGame]`, and the loader refuses a mod that does neither. The reasons and the exact matching rules are on [Your first mod](first-mod.md#declaring-the-game). The analyzer enforces it at build time (CL0004, CL0005).

## Hot reload {#hot-reload}

The loader watches `Mods\*.dll` (and `Mods\<Name>\<Name>.dll`). A change is applied after the files have been quiet for about 700 ms, because a build writes in bursts (dll, then pdb, then the dll again). The swap happens between frames, where no mod code is on the stack. The sequence for a rebuilt mod:

1. The running copy's settings are saved, so the new copy reads them.
2. The **new copy is loaded first.** If it cannot load yet (the build is still being written, or an antivirus holds the file), the running copy stays and the file is looked at again shortly: up to three retries, then the loader gives up and logs it. A mod does not disappear.
3. The old copy gets `OnShutdown`, and everything it registered is torn down (see [ownership](#ownership)).
4. The new copy starts: attribute hooks first, then `OnInitialize`.

Assemblies are loaded from memory, never by path, so the files stay unlocked and a build can overwrite a mod while the game runs. Each mod has its own load context, so two mods can ship different versions of the same dependency, and the old copy's types can be collected. `CoreLoader.dll` always resolves to the one copy already running, or the mod's `CoreMod` would be a different type from the loader's.

A few consequences to design for:

- **Fields start fresh.** The new instance is a new object. Keep anything that must survive a reload in `Config` or in the game.
- **Dependencies count.** If a dependency next to the mod changes (a rebuilt `<Game>.Interop.dll`, say), every mod that loaded an assembly by that name is reloaded.
- **A new build that is for another game, or refused,** unloads the running copy; that is a final verdict, not a half-written file.
- **Content files are not watched.** Reload the mod after changing a PNG or OGG.
- Turn hot reload off with the checkbox in the Loader tab.

## Startup order

Mods are discovered and loaded (constructed) when the runtime starts, but their callbacks wait: **mods start once the game has loaded its assets.** Some games, Stoneshard among them, load sprites, sounds and rooms seconds after their first frame, and a sprite added before that would take a slot the game is about to fill. A game that never answers still starts its mods after about 30 seconds, and a game without a working GML bridge starts them at once. Until then a mod's tab says "Starts once the game has loaded its assets".
