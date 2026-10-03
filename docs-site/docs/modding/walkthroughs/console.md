---
title: "Walkthrough: the Console mod"
description: A guided read of the Console mod, which works in any YYC game because it uses only the untyped CoreLoader API.
---

The Console mod is an in-game console, an object and global browser, an instance inspector, and a
variable freezer. It is declared `[assembly: CoreModAnyGame]`, so it loads in every game, and it
contains no object, script or variable name from any of them. That is only possible because
everything it does goes through the untyped half of the API: `Game.CallBuiltin`, `Game.CallScript`,
`Globals`, `GmlObject`, `InstanceRef`, `Hooks.Before`/`After` by name, and `Code`.

Read it as the answer to "what can I build before the game has an [interop](../interop.md)?" Almost
everything. The interop adds typed names and compile-time checking; it does not add capabilities.

The mod is a handful of files in
[`managed/Mods/Console/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Mods/Console):

| File | Role |
|---|---|
| `ConsoleMod.cs` | The mod class, the console tab, command dispatch, `hook`, `code`, `callers` |
| `Expr.cs` | Lexer and evaluator for the GML-style expression language |
| `Inspector.cs` | Pick mode, the selected instance, the code view, the in-game outline |
| `ObjectsTab.cs`, `VariableTable.cs` | The Objects and Globals tabs and the editable variable table they share |
| `Freezer.cs` | Variables held at a value |

## Wiring: one mod, four tabs {#wiring}

`OnInitialize` builds the pieces and hands each the others it needs. The console, the Inspector, the
Objects tab and the Globals tab all share one `Evaluator` (so a value typed into any edit box is a
full expression) and one `Freezer` (so a freeze made in one tab is visible in all of them).

```csharp
public override void OnInitialize()
{
    _history.AddRange(Config.Get("history", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    Print($"Lodestone console - {Game.Name}. Type 'help'.", 0.6f, 0.8f, 1f);
    // The Objects tab needs the object table: built a slice per frame from
    // now, it is ready long before anyone opens the tab.
    ObjectTable.Start();
    _freezer = new Freezer(m => Print(m, 0.6f, 0.8f, 1f));
    _inspector = new Inspector(_eval, _freezer, Print, () => Path.Combine(Directory, "Console", "dumps"));
    _objects = new ObjectsTab(_inspector, _freezer);
    _globals = new VariableTable(_eval, _freezer, Print, globals: true);
    _gameDrawing = GameDraw.OnGui(_inspector.DrawGame);
    if (TestHost.Enabled)
        TestHost.Register("console", args => RunForTest(string.Join(' ', args.Select(a =>
                a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString() : a.GetRawText()))),
            "console <line>: runs a console line (expression or command), answers its output");
}
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L32-L48)</small>

Three decisions are visible here.

- **`ObjectTable.Start()` is called at once.** Listing every object in a big game costs one builtin
  call per asset index, thousands of calls. `Start()` spreads that over the next frames within a
  per-frame time budget, so the tab is ready long before anyone opens it. See
  [the object table](../cookbook/game-state.md) for the API.
- **History lives in `Config`.** `Config.Get`/`Config.Set` store JSON next to the mod dll
  (`Mods\Console.json`), and `OnShutdown` writes the last 100 lines back. That is the whole
  persistence story for a mod that needs none of [`ModSettings`](../cookbook/settings-and-persistence.md).
- **The test host command is registered only when the host is on.** `console <line>` runs a line
  through the same `Execute` the input box uses and answers the captured output, so a script can
  drive the console. A line the console reports as an error answers `ok:false`.

The tab itself is drawn from `OnGUI`, which dispatches to one method per tab and closes every scope
it opens:

```csharp
public override void OnGUI()
{
    if (!UI.BeginTabBar("##console_tabs")) return;
    if (UI.BeginTabItem("Console"))
    {
        DrawConsole();
        UI.EndTabItem();
    }
    if (UI.BeginTabItem("Inspector"))
    {
        _inspector?.Draw();
        UI.EndTabItem();
    }
    // ...
    UI.EndTabBar();
}
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L79-L103)</small>

UI scopes are tracked by the loader; a mod that returns with one open is [faulted](../concepts.md#faults).
Code that reads the live game while drawing, like the Globals tab, runs inside `UI.Guarded(draw, onError)`
so a game error shows up in the tab instead of faulting the mod:

```csharp
UI.Guarded(() => { _freezer.Draw(); _globals.Draw(); },
    ex => UI.TextColored(1f, 0.5f, 0.45f, $"{ex.GetType().Name}: {ex.Message}"));
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L109-L110)</small>

## Evaluating expressions against the live game {#expressions}

`Expr.cs` is a small recursive-descent parser: a lexer, then precedence levels `Or`, `And`,
`Compare`, `Sum`, `Product`, `Unary`, `Primary`. Nothing is compiled; every sub-expression is
evaluated immediately against the running game, so the only state is `ans`, the last result.

The interesting part is how a bare identifier gets a meaning without the mod knowing the game. A
function call tries a builtin first, then a script:

```csharp
private RValue Call(string name)
{
    Expect("(");
    var args = new List<RValue>();
    if (!IsOp(")"))
    {
        do { args.Add(Expr()); } while (IsOp(",") && Next().Text == ",");
    }
    Expect(")");

    if (Game.BuiltinArity(name) is { } arity)
    {
        if (arity >= 0 && arity != args.Count)
            throw new ConsoleError($"{name} takes {arity} argument{(arity == 1 ? "" : "s")} in this game, got {args.Count}");
        return Game.CallBuiltin(name, args.ToArray());
    }
    if (Game.FindSymbol("gml_Script_" + name) != 0 || Game.FindSymbol(name) != 0)
        return Game.CallScript(name, args.ToArray());
    throw new ConsoleError($"no builtin or script named '{name}' (try: find {name})");
}
```

<small>Source: [managed/Mods/Console/Expr.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Expr.cs#L394-L413)</small>

`Game.BuiltinArity` returns the argument count *this game's runtime registered* the builtin with (`-1`
for variadic, `null` if it does not exist). It matters because the runtime refuses a call with the
wrong count, and registries differ between runtime versions and from the manual. Checking first turns
a refusal into a message that names the number. `Game.FindSymbol` answers whether a compiled script
exists, with or without the `gml_Script_` prefix.

A bare name that is not a keyword is treated as an asset, and the console asks the game itself:

```csharp
// A bare name is an asset: objects, sprites, rooms, sounds, scripts.
var idx = Game.CallBuiltin("asset_get_index", name);
if (idx.IsNumber && idx.AsReal < 0)
    throw new ConsoleError($"'{name}' is not a known name, asset or function (try: find {name})");
return idx;
```

<small>Source: [managed/Mods/Console/Expr.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Expr.cs#L387-L391)</small>

So `instance_number(o_enemy)` is a builtin call whose argument is the result of `asset_get_index("o_enemy")`.
No object table is consulted. `idx.IsNumber` matters: newer runtimes return typed asset references
instead of numbers (see [runtime differences](../../internals/runtime-differences.md)), which
`IsNumber` is false for and which pass through unchanged.

### Places: `global.x`, `obj.var`, `obj[n].var`

Assignment needs something you can both read and write. The evaluator models that as a `Place`:

```csharp
private abstract record Place
{
    public abstract RValue Get();
    public abstract void Set(RValue v);
}

private sealed record GlobalPlace(string Name) : Place
{
    public override RValue Get() => Globals.Get(Name);
    public override void Set(RValue v) => Globals.Set(Name, v);
}

private sealed record InstancePlace(InstanceRef Inst, string Object, string Name) : Place
{
    public override RValue Get()
    {
        if (!Inst.Has(Name)) throw new ConsoleError($"{Object} has no variable '{Name}'");
        return Inst.Get(Name);
    }
    public override void Set(RValue v) => Inst.Set(Name, v);
}
```

<small>Source: [managed/Mods/Console/Expr.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Expr.cs#L157-L177)</small>

`Globals` and `InstanceRef` are thin wrappers over the game's own `variable_global_*` and
`variable_instance_*` builtins. An instance is held as an `InstanceRef`, which is its id, not a pointer,
so a destroyed instance stops `Exists` rather than dangling. `obj[n]` resolves through
`GmlObject.Find(name)` then `Instance(n)`:

```csharp
var o = GmlObject.Find(objectName) ?? throw new ConsoleError($"no object named '{objectName}'");
int count = o.InstanceCount;
```

<small>Source: [managed/Mods/Console/Expr.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Expr.cs#L237-L238)</small>

Places are resolved by backtracking (`Statement` tries a place, then falls back to an expression), and
the index expression in `obj[expr]` could run twice. The `_places` cache keyed by start token exists
for that reason, with a comment saying so: `obj[irandom(3)].hp` must roll once.

### Keeping `ans` alive

The last result outlives the frame, but strings and arrays from the game are pooled and released at
the end of the frame. The evaluator copies what it keeps:

```csharp
// `ans` outlives this frame, so it holds its own reference (a copy).
// Copy first, release the previous one after: v may BE the previous one
// (typing `ans`), and releasing first would free it before the copy.
var fresh = Values.CanCopy || v.IsNumber || v.IsUndefined ? Values.Copy(v) : RValue.Undefined;
var old = Ans;
Ans = fresh;
Values.Free(ref old);
return v;
```

<small>Source: [managed/Mods/Console/Expr.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Expr.cs#L126-L133)</small>

This is the [value lifetime rule](../concepts.md#values) in miniature: `Values.Copy` makes an
independently owned reference, `Values.Free(ref ...)` releases it, and the order matters when the new
value might be the old one. `Ans` is an `RValue` held in a field, which the CL0001 analyzer would
flag; the mod carries a `GlobalSuppressions.cs` with a justification for exactly these fields (see
[CL0001](../reference/analyzers.md#cl0001)).

### Nesting limits

`MaxDepth = 64` guards the parser because every nested expression costs managed stack frames on the
game thread, deep inside `Present`. A stack overflow cannot be caught; a line of 500 `(` must be an
error instead. Any recursive evaluator running on the game thread needs the same guard.

## The Objects and Globals tabs {#browsers}

Both tabs are views over the same untyped calls the evaluator uses.

`GmlObject.All()` is the object table (index, name, parent), and `GmlObject.InstanceCount` and
`Instance(n)` answer through `instance_number` and `instance_find`. The tab waits for the table and
shows a progress bar while it builds:

```csharp
if (!ObjectTable.Ready)
{
    ObjectTable.Start();
    UI.Text(ObjectTable.Status);
    UI.ProgressBar(ObjectTable.Progress);
    return;
}
```

<small>Source: [managed/Mods/Console/ObjectsTab.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ObjectsTab.cs#L50-L56)</small>

The "which object has a variable called gold?" search, `where <text>` in the console, is how you find
state in a game you have never seen. It does not scan every instance, which would be far too slow; it
looks at the first live instance of each object:

```csharp
public static List<(GmlObject Obj, string Name, RValue Value)> FindVariable(string text, int limit)
{
    var hits = new List<(GmlObject, string, RValue)>();
    var seen = new HashSet<long>();
    foreach (var o in GmlObject.All())
    {
        IEnumerable<string> names;
        InstanceRef inst;
        try
        {
            if (o.InstanceCount == 0) continue;
            inst = o.Instance(0);
            if (!seen.Add(inst.Id.Int64)) continue;
            names = inst.VariableNames();
        }
        catch (GmlException) { continue; }
        // ...
```

<small>Source: [managed/Mods/Console/ObjectsTab.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ObjectsTab.cs#L105-L131)</small>

Two details. `seen` is keyed by the id's raw bits (`Id.Int64`), because a parent's first instance is
often a child's too and an instance should be looked at once. And every read sits in
`catch (GmlException)`: an instance can be destroyed mid-search, or a getter can throw, and one bad
instance must cost one skipped hit, not the search.

The Globals tab and the `globals` command use `Globals.Names()` (the game's
`variable_instance_get_names` on the global scope) and `Globals.Get`, and skip values of kind `RValueKind.Object` (methods) unless you filter, since a
game with thousands of script-defined methods in `global` would drown the list.

`Format` renders a value by kind: strings quoted and truncated, arrays through `Gml.ArrayLength` and
`Gml.ArrayGet`, structs through `Gml.TypeOf` and `Gml.StructNames`, typed references as `ref <index>`.
It is depth-limited because GML arrays are references and can contain themselves.

## The Inspector: pick mode and an outline {#inspector}

### Picking

Pick mode lets the player click a thing in the game without the game reacting to the click:

```csharp
public void Pick()
{
    Input.ArmPick();
    _message = "click an instance in the game (right click cancels)";
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L46-L50)</small>

`Input.ArmPick()` makes the loader swallow the next click outside the overlay's windows. The mod
collects it from `OnUpdate`:

```csharp
if (Input.TryTakePick(out var click))
{
    if (click.RightButton) _message = "pick cancelled";
    else TakePick(click.RoomX, click.RoomY);
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L91-L95)</small>

The `PickClick` carries both window pixels and the game's own room coordinates (`device_mouse_x(0)` read
as the click was taken), which already account for views, scaling and letterboxing. The Inspector
needs room coordinates, because it asks the game what is *at* that point:
`instance_position_list`, then sorts the instances by collision-box area and depth, so the small thing
on top wins over the room-sized floor object under it. On a runtime without the list form it falls
back to `instance_position`, which returns one instance.

### Reading and editing what it found

`Select` keeps the id across frames with `Values.Keep`:

```csharp
public void Select(InstanceRef r, string? filter = null)
{
    Release(keepPicking: true);
    _target = new InstanceRef(Values.Keep(r.Id));
    _targetObject = ObjectName(r);
    string id = ConsoleMod.Format(r.Get("id"));
    _targetLabel = $"{_targetObject} (id {id})";
    _vars.Show(_target, $"{_targetObject} {id}", filter ?? "");
    SelectionVersion++;
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L61-L70)</small>

On older runtimes an instance id is a plain number and needs no keeping. On runtimes from 2024 it is
a typed reference, which `Values.Keep` handles identically; the mod does not branch on which. `Release`
frees it again. The object's name comes from `object_get_name(r.Get("object_index"))`, its
variables from `InstanceRef.VariableNames()`, and its parents from `GmlObject.Ancestors()`. Editing a
variable runs the typed text through the same `Evaluator`, then `InstanceRef.Set`.

### Drawing a highlight

The yellow outline around the selected instance is drawn in `GameDraw.OnGui`, the game's own GUI
layer, using only builtins:

```csharp
private static void Outline(InstanceRef r, ViewMap m, int colour, string label)
{
    double l = r.Get("bbox_left").AsReal, top = r.Get("bbox_top").AsReal;
    double rt = r.Get("bbox_right").AsReal, b = r.Get("bbox_bottom").AsReal;
    if (double.IsNaN(l) || rt < l) { l = rt = r.Get("x").AsReal; top = b = r.Get("y").AsReal; }
    double x1 = (l - m.X) * m.Sx, y1 = (top - m.Y) * m.Sy, x2 = (rt - m.X) * m.Sx, y2 = (b - m.Y) * m.Sy;
    Game.CallBuiltin("draw_set_alpha", 1);
    Game.CallBuiltin("draw_set_colour", colour);
    Game.CallBuiltin("draw_rectangle", x1 - 1, y1 - 1, x2 + 1, y2 + 1, true);
    Game.CallBuiltin("draw_text", x1, Math.Max(0, y1 - 16), label);
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L465-L475)</small>

The instance's bounding box is in *room* coordinates; the GUI layer is in *display* pixels. `Mapping`
converts through view 0's camera (`view_get_camera`, `camera_get_view_x`, `camera_get_view_width`, ...)
and `GameDraw.GuiWidth`. `GameDraw.OnGui` runs a handler once per frame inside the game's Draw GUI
pass, and the loader restores draw colour, alpha, font and alignment afterwards, so a handler can set them
freely.

The handler runs in the middle of the game's draw, where a throw would fault the mod, so it is
guarded by hand: a failure is shown once and drawing pauses for five seconds
(`_drawPausedUntil`) rather than disabling the console. See
[robustness](../cookbook/robustness-and-testing.md) for the pattern.

## The Freezer: re-applying values every frame {#freezer}

A "freeze" holds a variable at a value even though the game keeps changing it. It is the
Console's most direct use of the lifetime rules, because the frozen value outlives every frame.

```csharp
public void Freeze(InstanceRef? target, string name, RValue value, string label)
{
    Unfreeze(target, name);
    _entries.Add(new Entry
    {
        Target = target is { } t ? new InstanceRef(Values.Keep(t.Id)) : null,
        Name = name,
        Label = label,
        Key = KeyOf(target, name),
        Value = Values.Keep(value),
    });
}
```

<small>Source: [managed/Mods/Console/Freezer.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Freezer.cs#L47-L58)</small>

Both the instance id and the value are `Values.Keep`'d, and `Remove` releases both with
`Values.Free(ref ...)`. Calling `Keep` on a value the mod does not own (a pooled one) is safe: it
takes the value out of the pool or makes a copy, so freezing "this frame's value" works.

Every frame, after the game has had its turn, the freezer sets each variable back:

```csharp
public void Update()
{
    for (int i = _entries.Count - 1; i >= 0; i--)
    {
        var e = _entries[i];
        try
        {
            if (e.Target is { } t)
            {
                if (t.Exists) t.Set(e.Name, e.Value);
                else { Remove(i); _report($"unfroze {e.Label}: the instance is gone"); }
            }
            // variable_global_set would re-create a global the game has
            // removed; a freeze only holds values that still exist, and
            // stays listed in case the global comes back.
            else if (Globals.Exists(e.Name)) Globals.Set(e.Name, e.Value);
        }
        catch (GmlException ex) { Remove(i); _report($"unfroze {e.Label}: {ex.Message}"); }
    }
}
```

<small>Source: [managed/Mods/Console/Freezer.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Freezer.cs#L82-L101)</small>

"After the game had its turn" is the point of calling it from `OnUpdate`, which runs once per frame
at the Present hook, after the frame's Step events. The list is walked backwards so removing an entry
is safe. An instance that is gone, or a set the game refuses, drops the freeze and tells the user
instead of failing every frame.

The freeze key is `t.Id.Int64`, "the id's raw bits", because that identifies the instance whatever
the runtime hands out: a number on older runtimes, a typed reference on 2024+.

`OnShutdown` calls `_freezer?.Clear()`. A kept value that is never freed is a leak that survives the
mod's unload.

## Code views: what a function calls, and who calls it {#code}

YYC compiles GML to native code, so there is no source to show. `Code.Describe(symbol)` reads the
machine code and reports the scripts, events and builtins a function calls and the string literals it
references. The `code <fn>` command prints a `CodeInfo` (`Name`, `Address`, `Size`, `ArgumentCount`,
`Calls`, `Strings`), and the Inspector's Code tab turns each call into a button to follow. Together
with ScriptSpy and the `where` search, this is how you find a hook target in an unknown game; see
[finding hooks](../finding-hooks.md).

Finding callers means scanning every function in the game, which is too much for one frame.
`Code.FindCallers` takes a cursor and a millisecond budget (4 ms by default) and resumes where it
stopped:

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

`OnUpdate` calls this once per frame. The contract is: start the cursor at `0`, call until it comes
back `-1`. Note the enclosing `try` in `OnUpdate`, which catches a failure and cancels the search; a
background search must never fault the mod.

## Live call logging with `hook` {#hook}

`hook <script>` prints each call's arguments and result. It registers a `Before` and an `After` by
symbol name:

```csharp
long calls = 0;
// A stack, not one slot: the hooked script may call itself (or be
// reached again from inside), and each After must print its own call.
var pending = new Stack<(long N, string Args)>();
bool isEvent = symbol.StartsWith("gml_Object_", StringComparison.Ordinal);
// Formatting asks the game about the values (typeof, struct names); a
// failure there must cost one line of output, not fault the console.
var before = Hooks.Before(symbol, c =>
{
    calls++;
    string text;
    try { text = $"({string.Join(", ", Enumerable.Range(0, Math.Min(c.ArgCount, 8)).Select(i => Format(c.GetArg(i))))})"; }
    catch (Exception ex) { text = $"(arguments unreadable: {ex.Message})"; }
    pending.Push((calls, text));
});
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L326-L340)</small>

The hook arguments are *lent* by the game: the handler formats them to a C# string on the spot and
keeps only that, never the `RValue` (the CL0001 and CL0003 rules). The `HookHandle` pair is stored in
a dictionary so `unhook` and `OnShutdown` can `Dispose` them. Output is throttled after twenty calls,
because a hooked Step event can fire thousands of times a second.

## Test host commands

The mod registers one command, `console <line>` (see [wiring](#wiring)). Run from a script:

```powershell
tools\coreloader.ps1 -Game Dwarf console "instance_number(oMiner)"
```

That drives the whole mod headlessly, which is how `tools\smoke-generic.ps1` checks any YYC game: it
finds objects and variables with the console's `objects`, `vars` and `find` commands and never needs a
game-specific name. See the [test host reference](../reference/test-host.md).

## What to take from this mod

- **The untyped API is complete.** Builtins, scripts, globals, instance variables, hooks by symbol
  name, drawing and input all work with strings.
- **Ask the game, don't assume.** Argument counts come from `Game.BuiltinArity`, asset ids from
  `asset_get_index`, parents from the runtime, and `GmlException` is caught wherever the game can
  refuse.
- **Spread expensive work across frames** (`ObjectTable.Start`, `Code.FindCallers` with a cursor).
- **Anything kept across frames is owned** (`Values.Keep`/`Free`), and anything lent is copied to C#
  data on the spot.
- **Guard code that runs inside the game's own passes** (`UI.Guarded`, the draw handler's pause).

For a mod that does use the interop, and draws with the game's own UI pieces, read the
[Fast Travel walkthrough](fast-travel.md).
