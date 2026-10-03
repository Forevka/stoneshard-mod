---
title: Analyzers
description: The five CoreLoader compiler rules (CL0001 to CL0005), what each flags and why, with examples and how to suppress them.
---

Mods are compiled with `CoreLoader.Analyzers`, a Roslyn analyzer that reports the lifetime mistakes
the runtime can only catch as a crash, and a mod that does not say which game it is for. It runs in the
compiler only; the game never loads it.

- Projects under `Mods/`, `Tests/` and `Examples/` in the repository get it from their
  `Directory.Build.props`.
- Template mods get it from `<game>\Lodestone\Analyzers\`, where `tools\deploy-coreloader.ps1`
  installs it.

Each diagnostic's help link points at the section for its rule on this page.

| Rule | Severity | Reports |
|---|---|---|
| [CL0001](#cl0001) | warning | A game value (`RValue`) kept past the frame |
| [CL0002](#cl0002) | warning | An `Instance` or `HookCall` kept past its call |
| [CL0003](#cl0003) | warning | `Values.Free` on a value the game lent |
| [CL0004](#cl0004) | **error** | A mod that does not say which game it is for |
| [CL0005](#cl0005) | warning | A mod built on one game's interop that declares other games |

The first three are about [value lifetime](../concepts.md#values); the last two are about
game declarations and are checked once per compilation.

## CL0001: A game value is kept in a field {#cl0001}

**What it flags.** A field or auto-property whose type is, or contains, `RValue`: `RValue`, `RValue?`,
`RValue[]`, `List<RValue>`, a tuple with an `RValue`, a positional `record` parameter. It also flags a
lambda that captures an `RValue` local when the lambda is *kept*: assigned to a field or property,
queued with `Game.RunOnGameThread`, `Task.Run`/`StartNew`/`ContinueWith`, `ThreadPool.QueueUserWorkItem`,
added to a collection held in a field, or registered with `Hooks.Before`/`After`/`NextBefore`/
`NextAfter`, `TestHost.Register` or `GameDraw.OnGui`.

**Why.** Strings, arrays and structs from the game are pooled and released at the end of the frame.
An `RValue` in a field next frame points at freed memory. A captured local lives exactly as long as
the lambda, which is the same problem. The type cannot tell a number (safe) from a string (not), so the
rule flags both and you decide.

A minimal sketch of the mistake and the two fixes:

```csharp
// Bad: a string result kept in a field.
private RValue _name;
void Capture() => _name = Game.CallBuiltin("object_get_name", 5);

// Good: keep C# data.
private string _name = "";
void Capture() => _name = Game.CallBuiltin("object_get_name", 5).ToString();

// Good: own the value, and release it when you are done (suppress CL0001 on the field, see below).
private RValue _name;
void Capture() { Values.Free(ref _name); _name = Values.Keep(Game.CallBuiltin("object_get_name", 5)); }
public override void OnShutdown() => Values.Free(ref _name);
```

A captured value in a kept lambda is the same mistake:

```csharp
// Bad: v is a pooled string by the time the lambda runs.
var v = c.GetArg(0);
Game.RunOnGameThread(() => Log.Info(v.ToString()));

// Good: copy to C# data first.
var text = c.GetArg(0).ToString();
Game.RunOnGameThread(() => Log.Info(text));
```

**What it does not flag.** Instance fields of a `ref struct` (it cannot outlive the call that made it;
a `static` field in one is still flagged). Computed properties (`RValue Now => Game.CallBuiltin("x")`
stores nothing). A lambda that is used on the spot (`array.Select(x => ... v ...)`), and a lambda's own
parameters (`Hooks.Before("scr_x", call => call.GetArg(0))`).

**How to suppress.** Keeping a value on purpose (a number, or one owned with `Values.Keep` and freed
later) is fine. Suppress that one member, with a reason, in a `GlobalSuppressions.cs`:

```csharp
[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member",
    Target = "~F:MyMod.MyMod._frozen", Justification = "Values.Keep'd, freed in OnShutdown.")]
```


The Console mod does this for the value its evaluator keeps as `ans`:

```csharp
[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~P:CoreConsole.Evaluator.Ans",
    Justification = "ans holds its own reference: Evaluator.Run stores a Values.Copy and frees the previous one, and Release frees it on shutdown.")]
```

<small>Source: [managed/Mods/Console/GlobalSuppressions.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/GlobalSuppressions.cs#L6-L7)</small>

For a single declaration, `#pragma warning disable CL0001` with a comment also works (the analyzer's
own tests check this, in `Suppression_IsHonoured`). The Target name uses Roslyn's documentation-ID
form: `~F:` for a field, `~P:` for a property, `Namespace.Type.Member`.

## CL0002: A per-call game handle outlives its call {#cl0002}

**What it flags.** A field or auto-property of type `Instance` or `HookCall` (or a collection or
nullable of them, so `List<Instance>` and `Instance?` count). And a kept lambda (the same list as
[CL0001](#cl0001)) that captures an `Instance` or a `HookCall` from the enclosing method.

**Why.**

- `Instance` is a raw `CInstance` pointer. It dangles once the instance is destroyed, and a stale
  pointer is a crash, not an exception. Hold an `InstanceRef` (an id): a destroyed instance
  stops `Exists`ing.
- `HookCall` points into the hooked call's frame and is valid only inside its handler.

```csharp
// Bad
private Instance _player;
void H(HookCall c) { _player = c.Self; }

// Good
private InstanceRef _player;
void H(HookCall c) { _player = new InstanceRef(c.Self.Get("id")); }
```

Capturing a `HookCall` in a handler registered from inside another handler is the lambda form of the
mistake:

```csharp
// Bad: the inner handler runs on a later call; c is long gone.
Hooks.Before("scr_x", other => { var n = c.ArgCount; });

// Good: copy what you need.
var n = c.ArgCount;
Hooks.Before("scr_x", other => { /* use n */ });
```

**What it does not flag.** The lambda's own `HookCall` parameter (`Hooks.Before(name, call => ...)`),
a local you copy data out of before the lambda, and instance fields of a `ref struct`.

**How to suppress.** Rarely right. If you hold an `Instance` for a single frame in a field and clear
it before the frame ends, suppress that member with a `SuppressMessage` for `CL0002`, written like the CL0001 example above.

## CL0003: Freeing a value the game lent {#cl0003}

**What it flags.** `Values.Free(ref x)` where the local `x` was assigned, anywhere in its method, from
`HookCall.GetArg(...)` or `HookCall.Result`.

**Why.** A hook's arguments and result belong to the caller. `Values.Free` releases a reference; if it
is the caller's, the game then uses freed memory and crashes or corrupts state later, far from the
mistake.

```csharp
// Bad
[HookBefore("scr_x")]
void H(HookCall c) { var a = c.GetArg(0); Values.Free(ref a); }

// Good: nothing to free; read what you need.
[HookBefore("scr_x")]
void H(HookCall c) { double d = c.GetArg(0).AsReal; }

// Good: you own a copy, so freeing it is correct.
[HookBefore("scr_x")]
void H(HookCall c) { var a = Values.Copy(c.GetArg(0)); /* ... */ Values.Free(ref a); }
```

`Values.Keep(...)`, `Values.Copy(...)` and `RValue.FromString(...)` return values you own, and
freeing those is clean. See [hook arguments](../concepts.md#hook-arguments).

**How to suppress.** Do not: a flagged free is a bug. If the analyzer is wrong (a local is reused for
an owned value on another path), copy to a new local with a different name.

## CL0004: A mod does not say which game it is for {#cl0004}

**What it flags.** An assembly with `[assembly: CoreModInfo(...)]` and one of:

- neither `[CoreModGame]` nor `[CoreModAnyGame]`;
- both of them;
- a `[CoreModGame]` that names no game (`CoreModGame()`, `CoreModGame(null)`, or only blank names).

This is the only rule with **error** severity, because it is the same set of cases the loader refuses
at runtime. The analyzer reports it on the `[CoreModInfo]` attribute.

**Why.** Code written for one game's objects and scripts must not run inside another. Rather than
guessing, the loader refuses a mod that does not say, so its author hears about it the first time it
runs. The analyzer moves that failure to build time.

```csharp
// Bad: refused at load, an error at build.
[assembly: CoreModInfo(typeof(MyMod), "My Mod", "1.0.0", "Me")]

// Good: for particular games, by exe name without .exe.
[assembly: CoreModInfo(typeof(MyMod), "My Mod", "1.0.0", "Me")]
[assembly: CoreModGame("StoneShard")]

// Good: for a mod that uses nothing game-specific.
[assembly: CoreModAnyGame]
```

`[CoreModGame("StoneShard", "Dwarf Eats Mountain")]` lists several. The exact test cases are in
[`GameDeclarationAnalyzerTests.cs`](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader.Analyzers.Tests/GameDeclarationAnalyzerTests.cs).

A class library that has no `[CoreModInfo]` (shared helpers) is not a mod and is not reported.

**How to suppress.** You do not; add the declaration. The loader will refuse the mod regardless.

## CL0005: A mod built on one game's interop declares other games {#cl0005}

**What it flags.** A mod that compiles against a generated `<Game>.Interop` assembly (one that also
holds a `<Game>.Scripts` class), when its `[CoreModGame]` does not name that game, or when it declares
`[CoreModAnyGame]`. The name match ignores case and treats every non-alphanumeric character as `_`, so
`"Dwarf Eats Mountain"` matches the interop `Dwarf_Eats_Mountain`.

**Why.** A generated interop names one game's scripts, objects and assets. The mod fails in any other
game, so claiming that it works anywhere is wrong. This rule is a warning, not an error: the mod
still loads, but the declaration is a lie.

```csharp
// Bad: compiles against StoneShard.Interop, claims Dwarf Eats Mountain.
[assembly: CoreModGame("Dwarf Eats Mountain")]

// Bad: an interop mod cannot be for any game.
[assembly: CoreModAnyGame]

// Good
[assembly: CoreModGame("StoneShard")]
```

**What it does not flag.** An assembly named `*.Interop` that is not a game interop (no `Scripts`
class in the namespace); a mod that names the interop's game among several; a mod without a valid
declaration (it gets CL0004 only).

**How to suppress.** If the mod really is written to work in several games, split the game-specific
part from the shared part: the shared mod is `CoreModAnyGame` and does not reference the interop. Or
declare every game the interop covers.

## Making an analyzer help link work

Every rule's `helpLinkUri` is `https://lodestone.forevka.dev/modding/reference/analyzers#clNNNN`,
which is why the headings above carry explicit ids (`{#cl0001}` and so on) and must not be renamed.
