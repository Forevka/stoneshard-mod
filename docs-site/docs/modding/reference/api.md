---
title: API at a glance
description: Every public area of the CoreLoader API with its main members, and the cookbook page that shows each one in use.
---

Everything in this page lives in the `CoreLoader` namespace of `CoreLoader.dll`, which every mod
references. Public classes are `static` facades over the loader; each call that touches the game
checks that it is on the game thread and throws otherwise (see [the game thread](../concepts.md#game-thread)).

If the game has a generated [interop](../interop.md), `Scripts.*`, `Objects.*`, `Builtins.*` and
`Assets.*` give typed access to the same functionality as `Game.CallScript`, `GmlObject` and
`Game.CallBuiltin`; that part of the API is generated per game and is not listed here.

## Overview

| Area | What you get | Cookbook |
|---|---|---|
| [`CoreMod`](#coremod) | Lifecycle callbacks, `Log`, `Config`, `Directory` | [first mod](../first-mod.md) |
| [`Hooks`](#hooks) | Before/after hooks on scripts and object events, one-shot hooks | [hooks](../cookbook/hooks.md) |
| [`Game`](#game) | Calling scripts, events and builtins; symbols; queueing work onto the game thread | [calling the game](../cookbook/calling-the-game.md) |
| [`Globals`, `GmlObject`, `InstanceRef`](#variables-and-instances) | Global and instance variables, objects, live instances | [game state](../cookbook/game-state.md) |
| [`ObjectTable`](#objecttable) | The object table, built over a few frames | [game state](../cookbook/game-state.md) |
| [`ObjectTypes`](#objecttypes) | New objects defined at runtime, with events in C# | [new object types](../cookbook/object-types.md) |
| [`DsMap`, `DsList`](#dsmap-and-dslist) | ds_map and ds_list by id | [game state](../cookbook/game-state.md) |
| [`Gml`](#gml) | `typeof`, arrays and structs | [game state](../cookbook/game-state.md) |
| [`UI`](#ui) | ImGui widgets for the mod's overlay tab | [drawing and UI](../cookbook/drawing-and-ui.md) |
| [`RValue`](#rvalue) | The runtime's 16-byte value | [calling the game](../cookbook/calling-the-game.md) |
| [`Values`](#values) | Lifetime of strings, arrays and structs | [concepts](../concepts.md#values) |
| [`Content`](#content) | Sprites and sounds loaded at runtime | [drawing and UI](../cookbook/drawing-and-ui.md) |
| [`GameDraw`](#gamedraw) | Drawing into the game's own GUI layer | [drawing and UI](../cookbook/drawing-and-ui.md) |
| [`Input`](#input) | Pick mode | [input](../cookbook/input.md) |
| [`ModSettings`](#modsettings) | Settings a mod offers the player | [settings and persistence](../cookbook/settings-and-persistence.md) |
| [`ModConfig`](#modconfig) | The mod's JSON settings file | [settings and persistence](../cookbook/settings-and-persistence.md) |
| [`TestHost`](#testhost) | Commands for automated testing | [robustness and testing](../cookbook/robustness-and-testing.md) |
| [`Code`](#code) | Read-only views of compiled code | [finding hooks](../finding-hooks.md) |

## CoreMod {#coremod}

The base class of every mod. Declare the mod with assembly attributes, then subclass.

```csharp
[assembly: CoreModInfo(typeof(MyMod), "My Mod", "1.0.0", "Me")]
[assembly: CoreModGame("StoneShard")]     // or [assembly: CoreModAnyGame]
```

| Member | Notes |
|---|---|
| `virtual void OnInitialize()` | Once, on the game thread, after the game has loaded its assets |
| `virtual void OnUpdate()` | Every frame, at the Present hook |
| `virtual void OnGUI()` | Inside the mod's own overlay tab; use `UI.*` |
| `virtual void OnShutdown()` | Unload, hot reload or game exit |
| `Logger Log` | Writes to `lodestone.log`, tagged with the mod's name |
| `ModConfig Config` | `<dll folder>\<dll name>.json`, see [ModConfig](#modconfig) |
| `string Directory` | The folder the mod's dll is in; content files go in a folder named after the dll, or next to it |
| `CoreModInfoAttribute Info` | `ModType`, `Name`, `Version`, `Author` |

Attributes:

- `[assembly: CoreModInfo(Type modType, string name, string version, string author)]` is required.
- `[assembly: CoreModGame(params string[] games)]` names the exe (without `.exe`, compared ignoring
  case, or the interop namespace). In any other game the mod is skipped.
- `[assembly: CoreModAnyGame]` for a mod that uses nothing game-specific.

Exactly one of the last two is required; see [CL0004](analyzers.md#cl0004) and
[troubleshooting](troubleshooting.md#not-loaded).

`Logger` has `Info(string)`, `Warning(string)`, `Error(string)` and `Error(string, Exception)`.
`GmlException` is what any call the game refuses throws; its message is the game's own error text.

## Hooks {#hooks}

Hooks are shared: however many mods hook a function, it is detoured once, and a function nobody hooks
any more is detached again.

| Member | Notes |
|---|---|
| `Hooks.Before(string symbol, HookHandler handler)` | Returns a `HookHandle` (`Dispose()` removes it). May change arguments or skip the original |
| `Hooks.After(string symbol, HookHandler handler)` | May read or replace the result |
| `Hooks.NextBefore(symbol, match, action, timeout, onTimeout)` | Runs `action` once, before the next call `match` accepts (`null` accepts any). Returns a `NextCall` |
| `Hooks.NextAfter(...)` | The same, after the call |
| `Hooks.NativeHookCount` | Distinct functions the loader has detoured |
| `[HookBefore("symbol")]`, `[HookAfter("symbol")]` | On a mod's methods: attached at start, removed on unload |
| `ScriptRef.Before/After`, `EventRef.Before/After` | The same, from the interop's refs |

`symbol` is `gml_Script_<name>`, `gml_Object_<object>_<Event>_<n>`, `gml_RoomCC_*`, `gml_GlobalScript_*`, or a bare script name.

`HookCall` (the handler's argument, **valid only inside the handler**):

| Member | Notes |
|---|---|
| `Symbol` | The resolved full symbol, e.g. `gml_Script_scr_get_XP` even if you hooked `scr_get_XP` |
| `Self`, `Other` | `Instance` values |
| `IsAfter`, `OriginalSkipped` | Phase and whether a Before handler skipped the original |
| `ArgCount`, `GetArg(i)`, `SetArg(i, value)` | `SetArg` changes what the original and later handlers see, not the caller's variables |
| `Result` | Get or set; scripts only (object events have none) |
| `CallOriginal()` | Runs the original again with the (modified) arguments; no handler sees the extra call |
| `SkipOriginal()` | Before handlers only |

`NextCall` has `Symbol`, `IsPending` and `Dispose()` to cancel. Hook arguments and results are lent
by the game: never `Values.Free` them ([CL0003](analyzers.md#cl0003)) and never keep the `HookCall`
([CL0002](analyzers.md#cl0002)). See [hook arguments](../concepts.md#hook-arguments).

## Game {#game}

| Member | Notes |
|---|---|
| `Name`, `Directory`, `LoaderDirectory` | Exe name without extension, game folder, the `Lodestone` folder |
| `IsGmlReady`, `IsAbiProven`, `BuiltinCount` | Loader state |
| `Symbols` | Every compiled GML function: `GmlSymbol(Name, Address)`, with `IsScript` and `IsObjectEvent` |
| `Builtins` | Every builtin this runtime registers, sorted |
| `FindSymbol(name)` | Address, or `0` |
| `CallScript(name, params RValue[] args)` | `name` is `scr_foo` or `gml_Script_scr_foo` |
| `CallScriptAs(Instance self, Instance other, name, args)` | As an instance |
| `CallScriptAs(InstanceRef self, name, args)` | As the instance an id names |
| `CallEvent(name, Instance self, Instance other = default)` | Runs an object event |
| `CallBuiltin(name, params RValue[] args)`, `CallBuiltinAs(Instance self, ...)` | Builtins |
| `BuiltinArity(name)` | The count this runtime registered it with: `-1` variadic, `null` unknown |
| `CurrentSelf` | The instance the game is running code as; only meaningful inside a hook or event |
| `CanResolveInstances` | Whether `InstanceRef.Resolve` works in this runtime |
| `RunOnGameThread(Action)` | Queue work to the start of the next frame, as the calling mod; the call meant for other threads (disposing a hook handle, a `NextCall` or a `GameDraw.OnGui` registration also marshals to the game thread) |

`Instance` is a raw pointer (`Pointer`, `IsNull`, `Get(name)`, `Set(name, value)`, indexer). It dangles
once the instance is destroyed; hold an `InstanceRef` instead ([CL0002](analyzers.md#cl0002)).

A call the game rejects throws `GmlException` with the GML error's message, for example
`call to scr_x failed: Variable ... not set before reading it. (in gml_Script_scr_x, line 12)`.

## Variables and instances {#variables-and-instances}

**`Globals`**: `Get(name)`, `Set(name, value)`, `Exists(name)`, `Names()`.

**`GmlObject`** (a `record struct(int Index, string Name)`):

| Member | Notes |
|---|---|
| `GmlObject.Find(name)` | By name; `null` if there is none. Misses are remembered for 2 s |
| `GmlObject.All()`, `FromIndex(i)` | From the cached [object table](#objecttable) |
| `Parent`, `Ancestors()`, `IsA(name)`, `Children()` | The hierarchy |
| `InstanceCount` | Live instances, children included |
| `Instance(n)`, `Instances()` | `InstanceRef` values |

**`InstanceRef`** (a `record struct(RValue Id)`): safe to hold across frames, because a destroyed
instance stops existing instead of dangling.

| Member | Notes |
|---|---|
| `Exists` | |
| `Get(name)`, `Set(name, value)`, `Has(name)`, indexer | Instance variables |
| `VariableNames()` | |
| `Resolve()` | The live `Instance`, or `null` if gone or if this runtime's id lookup could not be proven |
| `CallScript(name, args)` | Runs a script as this instance |

## ObjectTable {#objecttable}

The object table (index, name, parent), read once and cached.

| Member | Notes |
|---|---|
| `Start()` | Builds it over the coming frames, within a per-frame time budget. Later calls do nothing |
| `Ready`, `Progress`, `Status` | For a progress bar |
| `Complete()` | Finishes it now, in one frame |

`Parent` and `Ancestors()` work before it is ready; `Children()` finishes the table on the spot.

```csharp
public override void OnInitialize() => ObjectTable.Start();

public override void OnGUI()
{
    if (!ObjectTable.Ready) { UI.ProgressBar(ObjectTable.Progress, 0f, ObjectTable.Status); return; }
    var food = GmlObject.Find("o_inv_food_parent")!.Value;
    foreach (var o in food.Children()) UI.Text(o.Name);
    bool isFood = GmlObject.Find("o_inv_acorn")?.IsA("o_inv_food_parent") == true;
}
```


## ObjectTypes {#objecttypes}

New GameMaker objects, defined at runtime, whose events run C#. Recipes: [New object types](../cookbook/object-types.md).

| `ObjectTypes` | |
|---|---|
| `Available`, `Status` | Whether objects can be defined in this game: `"available"`, `"not proven yet (...)"` or `"unavailable: ..."` |
| `Define(name, parent?, sprite?, visible, persistent, solid, mask?)` | A new object, or the one already defined under that name this session (after a hot reload, the same index). Throws when unavailable, when the name is a game asset, or when another loaded mod defined it |
| `Find(name)` | A type defined this session, or null |
| `Flush()` | Makes this frame's definitions take effect now; needed before creating instances of a fresh type other than through `ObjectType.Create` |

| `ObjectType` | |
|---|---|
| `Name`, `Index`, `Object` | The object's name, index (what builtins take) and `GmlObject` |
| `On(GameEvent, handler)` | Implements an event (replacing the parent's and an earlier handler); returns the type |
| `Create(x, y, depth = 0)` | An instance, as an `InstanceRef`; its Create event has run |
| `InstanceCount`, `Instances()` | Live instances, including those of types inheriting from it |
| `DestroyAll()` | Destroys them (without their Destroy event where the runtime allows it) |

| `ObjectEventCall` (what a handler gets) | |
|---|---|
| `Self`, `Other` | The instance the event runs for, and GML's `other` |
| `Type`, `Event` | The handler's type and event |
| `CallInherited()` | Runs the parent's code for this event (`event_inherited()`); false when it has none |

`GameEvent(Type, Subtype)` names an event: `Create`, `Destroy`, `Step`, `BeginStep`, `EndStep`,
`Alarm(n)`, `Collision(obj)`, `Keyboard(key)`, `KeyPress(key)`, `KeyRelease(key)`, `Mouse(n)`,
`Other(n)`, `User(n)`, `GameStart`, `GameEnd`, `RoomStart`, `RoomEnd`, `AnimationEnd`, `AsyncHttp`,
`AsyncNetworking`, `AsyncSaveLoad`, `AsyncSystem`, `Draw`, `DrawGui`, `DrawBegin`, `DrawEnd`,
`DrawGuiBegin`, `DrawGuiEnd`, `PreDraw`, `PostDraw`, `CleanUp`. Its `ToString()` is the compiled name
(`Step_0`).

```csharp
var trap = ObjectTypes.Define("o_mymod_trap", parent: "o_enemy", sprite: "s_trap")
    .On(GameEvent.Create, e => { e.CallInherited(); e.Self.Set("armed", true); })
    .On(GameEvent.Collision(GmlObject.Find("o_player")!.Value), e => Spring(e.Self, e.Other));
trap.Create(x, y);
```

## DsMap and DsList {#dsmap-and-dslist}

Many games keep their real state in ds_maps and ds_lists and hand out only the id. `DsMap` and
`DsList` wrap that id and go through the game's own builtins. The struct is safe to keep across frames
(a destroyed map stops `Exists`ing), but strings read out of one are pooled like any other value.

| `DsMap` | |
|---|---|
| `Exists`, `Count` | |
| `Get(key)`, `Set(key, value)`, `Has(key)`, `Remove(key)`, `Clear()`, indexer | `Set` is `ds_map_replace`: adds or replaces |
| `Keys()`, `Entries()` | `Entries()` is `(string Key, RValue Value)` pairs |
| `ToJson()` | `json_encode` of the map |

| `DsList` | |
|---|---|
| `Exists`, `Count` | |
| `At(i)`, `Set(i, value)`, indexer | |
| `Add(value)`, `Insert(i, value)`, `RemoveAt(i)`, `Clear()` | |
| `Items()` | |

```csharp
var data = new DsMap(item.Get("data"));
if (data.Exists)
{
    foreach (var (key, value) in data.Entries()) Log.Info($"{key} = {value}");
    data.Set("Durability", 100);                  // ds_map_replace: adds or replaces
    var effects = new DsList(data.Get("effects"));  // nested: edit it in place,
    effects.Add("good_pt_rage");                    // never write its id back with Set
}
```


:::warning
A nested list or map stored with `Set` is stored as its bare id: the parent no longer knows it is
nested, and `json_encode` writes it as a number. Edit a nested structure in place with
`new DsList(map.Get("key"))` instead of writing its id back.
:::

## Gml {#gml}

| Member | Notes |
|---|---|
| `TypeOf(value)` | The game's `typeof` |
| `ArrayLength(array)`, `ArrayGet(array, i)` | |
| `ToStringList(array)` | A GML array of strings as a list; anything else gives an empty list |
| `StructNames(struct)`, `StructGet(struct, name)`, `StructSet(struct, name, value)` | |

## UI {#ui}

Widgets for the mod's overlay tab, drawn from `OnGUI`. Scopes (`Begin*`/`End*`, `Push*`/`Pop*`) are
tracked: a mod that leaves one open is [faulted](../concepts.md#faults).

| Kind | Members |
|---|---|
| Text | `Text`, `TextColored(r, g, b, text)`, `TextDisabled`, `TextWrapped`, `SeparatorText` |
| Buttons | `Button(label)`, `Button(label, width, height)`, `SmallButton`, `Selectable(label, selected)` |
| Inputs | `Checkbox(label, ref bool)`, `SliderFloat`, `SliderInt`, `InputInt`, `InputFloat`, `InputDouble`, `InputText`, `InputTextEnter`, `InputTextWithHint`, `Combo`, `BeginCombo`/`EndCombo` |
| History line | `InputLine(label, ref value, history, ref cursor)`, `KeyPressed(UI.Key)` |
| Layout | `SameLine()`, `SameLine(offsetX, spacing)`, `Separator`, `Spacing`, `SetNextItemWidth`, `ProgressBar(fraction, width, overlay)` |
| Scopes | `BeginTabBar`/`EndTabBar`, `BeginTabItem`/`EndTabItem`, `BeginChild`/`EndChild`, `TreeNode`/`TreePop`, `PushId`/`PopId`, `BeginDisabled`/`EndDisabled`, `PushTextColor`/`PopTextColor` |
| Other | `CollapsingHeader`, `Tooltip`, `Clipped(count, row, itemHeight)`, `SetClipboard`, `FocusNext`, `ScrollHere`, `AtBottom`, `ItemDeactivatedAfterEdit` |
| Safety | `Guarded(draw, onError)`: runs `draw`; if it throws, closes any scopes it left open and calls `onError` |

Use `###stableId` in a label that carries live values (`$"Frozen ({n})###frozen"`) so the widget keeps
its identity when the text changes. `UI.Key` is `Tab`, `Left`, `Right`, `Up`, `Down`, `Enter`, `Escape`.

## RValue {#rvalue}

The runtime's 16-byte value, laid out identically so it passes to and from the game without
conversion.

**Kinds** (`RValueKind`, from `RValue.Kind`):

| Kind | Value | Notes |
|---|---|---|
| `Real` | 0 | A double |
| `String` | 1 | Pooled: valid for the frame |
| `Array` | 2 | Pooled |
| `Pointer` | 3 | |
| `Undefined` | 5 | |
| `Object` | 6 | A struct or method; pooled |
| `Int32` | 7 | |
| `Int64` | 10 | |
| `Bool` | 13 | |
| `Reference` | 15 | A typed instance or asset reference on newer runtimes |
| `Unset` | `0x00FFFFFF` | Treated as undefined |

**Creating**: `RValue.Undefined`, `RValue.FromReal(double)`, `RValue.FromBool(bool)`,
`RValue.FromString(string)` (game thread only; pooled like any value), and implicit conversions from
`double`, `int`, `bool` and `string`, so `Game.CallBuiltin("instance_create_depth", 100, 200, 0, idx)`
works as written.

**Reading**:

| Member | Notes |
|---|---|
| `IsNumber` | `Real`, `Int32`, `Int64` or `Bool` |
| `IsUndefined` | `Undefined` or `Unset` |
| `AsReal` | A double; strings and references read as `NaN` |
| `AsBool` | `IsNumber && AsReal > 0.5` |
| `ToString()` | Text through the runtime's own formatting; game thread only |
| `Kind`, `Real`, `Int32`, `Int64`, `Pointer`, `Flags` | The raw fields. `Reference` ids are in the low half: `Int32`, or `Int64 & 0xFFFFFFFF` |

A reference is not a number, so `IsNumber` is false for it. Code that works on both old and 2024+
runtimes compares ids through their raw bits (`Int64`) or a helper rather than `AsReal`; see
[runtime differences](../../internals/runtime-differences.md).

## Values {#values}

Lifetime of strings, arrays and structs. Anything the game hands you goes into a per-frame pool and
is released at the end of the frame; only a value you keep across frames needs ownership.

| Member | Notes |
|---|---|
| `Values.Keep(value)` | A reference that survives the frame; you must `Free` it. A pooled value is taken out of the pool; one you do not own (a hook argument) is copied |
| `Values.Free(ref value)` | Releases a kept or copied value, and sets it to undefined. Never free what the game lends |
| `Values.Copy(value)` | An independently owned second reference. Numbers are returned unchanged |
| `Values.CanFree`, `Values.CanCopy` | Whether this runtime's helpers were found |
| `Values.Pending`, `Values.RootedStructs` | Diagnostics: this frame's pool, and kept structs |

Structs are garbage-collected rather than reference-counted, and the collector cannot see a pointer
held in C#, so `Keep` also roots a kept struct in the global `__coreloader_roots`, and `Free` takes it
out. See [value lifetime](../concepts.md#values).

## Content {#content}

Sprites and sounds loaded at runtime. Paths are relative to `Mods\<ModName>\` next to the mod's dll.
Content belongs to the mod that added it and is removed when the mod unloads.

| Member | Notes |
|---|---|
| `Content.AddSprite(file, frames = 1, xOrigin = 0, yOrigin = 0, removeBackground = false, smooth = false)` | A new sprite from a PNG strip |
| `Content.ReplaceSprite(spriteName, file, frames, xOrigin, yOrigin, removeBackground, smooth)` | A reskin of one of the game's own sprites; undone on unload |
| `Content.AddSound(file)` | A sound from an OGG file |
| `Content.ResolvePath(file)` | Where a relative path resolves |

`Sprite`: `Id`, `Index`, `File`, `Replaces`, `IsReleased`, `Frames`, `Width`, `Height`,
`SetOrigin(x, y)`, `Draw(x, y, frame = 0, xScale = 1, yScale = 1, rotation = 0, colour = 0xFFFFFF, alpha = 1)`,
`Dispose()`.
`Sound`: `Id`, `Index`, `File`, `IsReleased`, `IsPlaying`, `Play(loop = false, priority = 0)`, `Stop()`,
`Dispose()`.

Content files are not watched: reload the mod after changing one.

## GameDraw {#gamedraw}

| Member | Notes |
|---|---|
| `GameDraw.OnGui(Action draw)` | Runs `draw` once a frame inside the game's Draw GUI pass. Returns an `IDisposable`; the handler also stops when the mod unloads |
| `GuiWidth`, `GuiHeight` | Size of the GUI layer handlers draw on |
| `Carrier` | The Draw GUI event currently carrying the handlers (diagnostics) |

Draw with `draw_*` builtins through `Game.CallBuiltin` or the interop's `Builtins`, and with your
`Sprite.Draw`. Draw colour, alpha, font, alignment and blend mode are restored after the handlers.

## Input {#input}

Pick mode: let the player click on something in the game without the game reacting.

| Member | Notes |
|---|---|
| `Input.ArmPick()` | The next left or right click outside the overlay's windows is swallowed |
| `Input.TryTakePick(out PickClick click)` | Takes it once; `false` until a click arrives |
| `Input.IsPicking`, `Input.CancelPick()` | |

`PickClick(X, Y, Width, Height, RightButton, RoomX, RoomY)`: `X`/`Y` in client pixels, `RoomX`/`RoomY`
in room coordinates (`device_mouse_x(0)` read as the click was taken, `NaN` if unreadable). By
convention a right click means cancel. One pick at a time, owned by the mod that armed it.

## ModSettings {#modsettings}

Settings a mod offers the player, bound to keys of its `Config`. The loader only keeps the list; a
front end draws it (the ModMenu mod's game-styled window in Stoneshard).

| Member | Notes |
|---|---|
| `ModSettings.Toggle(mod, key, label, description, defaultValue, changed = null)` | Returns a `Setting` |
| `ModSettings.Slider(mod, key, label, description, defaultValue, min, max, step, format = null, changed = null)` | `format` turns the value into text (`x1.5`, `150%`) |
| `ModSettings.Choice(mod, key, label, description, defaultIndex, options, changed = null)` | The value is the chosen index |
| `ModSettings.Registered`, `ModSettings.Version` | The list, and a number that changes with it |

`Setting`: `ModName`, `ModId`, `Kind` (`Toggle`, `Slider`, `Choice`), `Key`, `Label`, `Description`,
`DefaultBool`, `DefaultNumber`, `Min`, `Max`, `Step`, `Options`, `Format`, `IsRegistered`, `GetBool()`,
`GetNumber()`, `ValueText()`, `SetBool()`, `SetNumber()`, `Nudge(direction)`, `Reset()`.
Registrations go when the mod unloads or faults.

## ModConfig {#modconfig}

`CoreMod.Config`: a JSON file named after the dll, next to it (`Mods\<dll name>.json`, or `Mods\X\X.json` for a mod in its own folder), written after a short delay and on unload
through a temporary file, so a crash never leaves a truncated file. Hand-editing it while the game is
closed works.

| Member | Notes |
|---|---|
| `Get(key, fallback)` | Overloads for `double`, `float`, `int`, `bool`, `string` |
| `Set(key, value)` | The same overloads |
| `Save()` | Forces a write |
| `Path` | Where the file is |

## TestHost {#testhost}

Development only: see the [test host reference](test-host.md).

| Member | Notes |
|---|---|
| `TestHost.Enabled` | `CORELOADER_TEST=1`, or a `Lodestone\testhost.enable` file |
| `TestHost.Register(name, handler, help = "")` | `handler` is `Func<IReadOnlyList<JsonElement>, object?>`; belongs to the registering mod |
| `TestHost.PipeName`, `TestHost.Frame` | |
| `TestHost.ToRValue(JsonElement)` | Converts an argument |

## Code {#code}

Read-only views of compiled code. YYC compiles GML to native code, so there is no source, but the
call graph and the strings go a long way towards knowing what to hook. Game thread only.

| Member | Notes |
|---|---|
| `Code.Describe(fn)` | `CodeInfo(Name, Address, Size, ArgumentCount, Calls, Strings)` or `null`. Cached |
| `Code.FindCallers(fn, ref cursor, millisecondBudget = 4)` | Start `cursor` at `0` and call once a frame until it comes back `-1` |
| `Code.BuiltinAddress(name)` | The native function behind a builtin, or `0` |

## The rules the loader enforces

These are explained, with their reasons, in [concepts](../concepts.md); in one line each:

- GML is only touched on the game thread. From anywhere else use `Game.RunOnGameThread` ([game thread](../concepts.md#game-thread)).
- A mod that throws, or that leaves UI scopes open, is disabled until it is reloaded, and its hooks are removed ([faults](../concepts.md#faults)).
- Everything a mod registers (hooks, draw handlers, content, picks, kept structs, queued actions, test commands, settings) belongs to it and goes when it unloads ([ownership](../concepts.md#ownership)).
- Values from the game are released at the end of the frame; keep one with `Values.Keep` ([values](../concepts.md#values)).
- A script with mod hooks on it is called with private copies of its arguments, so `SetArg` cannot compound ([hook arguments](../concepts.md#hook-arguments)).
- Mods start once the game has loaded its assets; Stoneshard loads them seconds after its first frame.
