---
title: Game state
description: Recipes for reading and writing the game's globals, instances, ds_maps, ds_lists, arrays, structs and object hierarchy from a mod.
---

GameMaker keeps its state in globals, in the variables of live instances, and in data structures it
hands out by id. CoreLoader reaches all of it through the game's own builtins, so every read and write
here is a call into the game. That means two rules apply to everything on this page: call from the
[game thread](../concepts.md#game-thread), and expect a `GmlException` when the thing you ask about does
not exist yet (at the title screen, say). See [Robustness and testing](robustness-and-testing.md#catch-gmlexception).

## Read and write a global variable {#globals}

Use `Globals.Get`, `Globals.Set` and `Globals.Exists`. They are `variable_global_get`, `variable_global_set`
and `variable_global_exists` underneath. Stoneshard's world map keeps the player's grid cell in two
globals:

```csharp
public static (int X, int Y) PlayerCell =>
    ((int)Globals.Get("playerGridX").AsReal, (int)Globals.Get("playerGridY").AsReal);
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L22-L23)</small>

```csharp
private static void SetCell((int X, int Y) cell)
{
    Globals.Set("playerGridX", cell.X);
    Globals.Set("playerGridY", cell.Y);
}
```

<small>Source: [managed/Mods/FastTravel/Traveller.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/Traveller.cs#L92-L96)</small>

`Globals.Get` returns an `RValue`. Check `IsNumber` (or `Kind`) before `AsReal`, because a global the game
has not set yet reads as undefined. The StoneshardHarness mod does exactly that when it keeps a value in
a game global of its own:

```csharp
public static int TrackedRoom
{
    get => Globals.Get(RoomGlobal) is { IsNumber: true } v ? (int)v.AsReal : -1;
    set => Globals.Set(RoomGlobal, value);
}
```

<small>Source: [managed/Mods/StoneshardHarness/World.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/World.cs#L25-L29)</small>

`Globals.Names()` lists every global, which is how you discover what the game has.

## Find a singleton instance and edit its variables {#singleton}

Many games keep their state on one controller object. Find the object by name with `GmlObject.Find`, take
its first live instance with `Instance(0)`, and read and write variables on the resulting `InstanceRef`.
Dwarf Eats Mountain keeps gold, mithril and soul on `oSys`:

```csharp
private InstanceRef? Sys()
{
    var o = GmlObject.Find("oSys");
    return o is { } obj && obj.InstanceCount > 0 ? obj.Instance(0) : null;
}
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L43-L47)</small>

```csharp
double gold = sys.Get("gold").AsReal;
// ...
sys.Set("gold", gold);
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L73) and [line 82](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L82)</small>

With the generated interop, the lookup collapses to `Objects.oSys.First`, and the variable names are
constants under `Objects.oSys.Vars`, harvested from live instances while the game ran:

```csharp
if (Objects.oSys.First is { } sys)
    UI.Text($"gold (oSys.gold): {sys[Objects.oSys.Vars.gold].AsReal:N0}");
```

<small>Source: [managed/Examples/InteropExample/InteropExample.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/InteropExample/InteropExample.cs#L40-L41)</small>

`InstanceRef` also has `Has(name)` to test for a variable, `VariableNames()` to list them, and an indexer
(`sys["gold"]`) that is the same as `Get` and `Set`.

Gotchas:

- `GmlObject.Find` is cached per name, and a miss is remembered for two seconds, so polling an object
  that does not exist yet every frame stays cheap.
- An `InstanceRef` is safe to keep across frames: a destroyed instance stops `Exists`ing instead of
  dangling. An `Instance` (the pointer you get as `HookCall.Self`) is not; it is only valid during the
  callback. To turn a ref into an `Instance` for a call that needs one, use `Resolve()` each frame and
  expect `null`.
- A variable the instance does not have reads as undefined; check with `Has` first.

## Iterate every instance of an object {#iterate-instances}

`GmlObject.Instances()` yields a ref for each live instance, **including instances of child objects**.
Dwarf Eats Mountain's units (miners, flamers, harpoons, cannons) are all children of `parDwarf`, so one
loop over the parent covers every unit:

```csharp
if (GmlObject.Find("parDwarf") is not { } units) return;
var seen = new HashSet<long>();
foreach (var unit in units.Instances())
{
    if (!unit.Has("damage")) continue;
    long key = unit.Id.Int64;
    seen.Add(key);
    double current = unit.Get("damage").AsReal;
    if (double.IsNaN(current)) continue;
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L91-L99)</small>

The typed form is the same loop over `Objects.o_enemy.Object`, with variable names from `Vars`:

```csharp
if (Objects.o_enemy.Object is { } enemies)
{
    foreach (var e in enemies.Instances())
    {
        if (!e.Exists || Num(e, Objects.o_enemy.Vars.is_hostile) <= 0 || Num(e, Objects.o_enemy.Vars.HP) <= 0) continue;
        if (Math.Max(Math.Abs(Num(e, "x") - px), Math.Abs(Num(e, "y") - py)) <= range)
            return "there are enemies nearby";
    }
}
```

<small>Source: [managed/Mods/FastTravel/Traveller.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/Traveller.cs#L51-L59)</small>

Gotchas:

- Because children are included, an exact-object test is sometimes needed. Stoneshard's `o_inventory`
  list also returns other containers, so the Trials mod compares `object_index` with the object it wants:

  ```csharp
  if (Objects.o_inventory.Object is not { } obj) return null;
  foreach (var r in obj.Instances())
      if (Builtins.object_get_name(r.Get("object_index")).ToString() == Objects.o_inventory.Name) return r;
  ```

  <small>Source: [managed/Mods/StoneshardTrials/World.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/World.cs#L94-L96)</small>

- `Instances()` is lazy and asks the game for the n-th instance as you go. If your loop creates or destroys
  instances, take a `ToList()` first (see the next recipe).
- Each `Get` is a call into the game. For hundreds of instances, read what you need once per pass and
  throttle the pass (see [Throttle OnUpdate](robustness-and-testing.md#throttle)).

## Spawn and destroy an instance {#spawn-and-destroy}

Use the `instance_create_depth` and `instance_destroy` builtins, and wrap the new id in an `InstanceRef`
to set variables on it. The Trials mod spawns tavern traders:

```csharp
var obj = GmlObject.Find(t.Object) ?? throw new InvalidOperationException($"no {t.Object}");
var npc = new InstanceRef(Builtins.instance_create_depth(t.X, t.Y, -t.Y, obj.Index));
if (!npc.Exists) return null;
// Our own stock key, before anything reads the town trader's.
npc.Set("id_name", t.Key);
```

<small>Source: [managed/Mods/StoneshardTrials/Merchants.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Merchants.cs#L182-L186)</small>

and removes a duplicate one:

```csharp
foreach (var n in npcs.Instances().ToList())
{
    if (n.Get("id_name") is not { Kind: RValueKind.String } k || k.ToString() != key) continue;
    if (first == null) first = n;
    else Builtins.instance_destroy(n.Id);
}
```

<small>Source: [managed/Mods/StoneshardTrials/Merchants.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Merchants.cs#L171-L176)</small>

Without the interop, the same two calls are `Game.CallBuiltin("instance_create_depth", x, y, depth, obj.Index)`
and `Game.CallBuiltin("instance_destroy", id)`.

Gotchas:

- Check `Exists` right after creating: a refused spawn leaves you with an id that names nothing.
- A new instance has run only its Create event. A game that reads variables another event sets may need
  you to run that event; see [Run an object event directly](calling-the-game.md#run-an-event).

## Read a ds_map {#ds-map}

Many games keep their real state in ds_maps and ds_lists and hand out only the id. `DsMap` wraps that id
(`Exists`, `Count`, `Get`, `Set`, `Has`, `Remove`, `Keys()`, `Entries()`, `ToJson()`). The struct is safe to
keep across frames; a destroyed map stops `Exists`ing. Strings read out of one are pooled like any
other value (see [Values](../concepts.md#values)).

Stoneshard saves each visited room's state in a map keyed by `"x_y"`; FastTravel reads the keys to know
where the player has been:

```csharp
var rooms = new DsMap(Globals.Get("locationsRoomsDataMap"));
if (rooms.Exists)
{
    foreach (var (key, _) in rooms.Entries())
    {
        var parts = key.Split('_');
        if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)) set.Add((x, y));
    }
}
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L83-L91)</small>

Other structures have no wrapper but are one builtin call away. Stoneshard's fog of war is a ds_grid:

```csharp
var fog = Globals.Get("globalmapFogGrid");
return fog.IsNumber && Builtins.ds_grid_get(fog, cell.X, cell.Y).AsReal > 0;
```

<small>Source: [managed/Mods/FastTravel/WorldMap.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/WorldMap.cs#L73-L74)</small>

## Edit a ds_list inside a ds_map {#ds-list}

`DsList` wraps a list id (`Count`, `At`, `Set`, `Add`, `Insert`, `RemoveAt`, `Clear`, `Items()`). When a
list lives inside a map, edit the list **in place** and never write its id back with `Set`: the map
already holds the reference. The Trials mod rewrites a potion's effect list this way:

```csharp
var data = new DsMap(bottle.Get("data"));
if (!data.Exists) throw new InvalidOperationException("the bottle has no data map");
// A ds_list held in the map: edited in place, never replaced.
var list = new DsList(data.Get("atrdlist"));
if (!list.Exists) throw new InvalidOperationException("the bottle has no atrdlist");
list.Clear();
foreach (var tag in tags) list.Add(tag);
Game.CallScriptAs(bottle, bottle, "scr_potion_set_param");
// A new potion shows "?" until identified (its data's identified); a gift comes known.
data.Set("identified", 1);
```

<small>Source: [managed/Mods/StoneshardTrials/Cards/Effects.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/Cards/Effects.cs#L241-L250)</small>

Always test `Exists` after wrapping: a missing variable reads as undefined, and an id that is not a map
or list does not exist. `DsMap.Set` is `ds_map_replace`, so it adds or replaces.

## Read arrays and structs {#arrays-and-structs}

GML arrays and structs are values, not ids. Use the `Gml` helpers: `ArrayLength`, `ArrayGet`, `StructGet`,
`StructSet`, `StructNames`, and `TypeOf` to tell them apart. The Trials mod reads Stoneshard's list of
dungeons, an array of structs:

```csharp
var arr = Run(Scripts.scr_glmap_getLocationBySubType, kind);
int n = Gml.ArrayLength(arr);
for (int i = 0; i < n; i++)
{
    var s = Gml.ArrayGet(arr, i);
    int x = (int)Gml.StructGet(s, "x").AsReal, y = (int)Gml.StructGet(s, "y").AsReal;
```

<small>Source: [managed/Mods/StoneshardTrials/World.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/World.cs#L71-L76)</small>

The Console's dump walks a value of unknown shape with `TypeOf`, `ArrayGet` and `StructNames`:

```csharp
if (depth < 3 && v.Kind == RValueKind.Array)
{
    int n = Gml.ArrayLength(v);
    sb.AppendLine($"{pad}{name} = [array of {n}]");
    for (int i = 0; i < n && i < 100; i++) DumpValue(sb, $"[{i}]", Gml.ArrayGet(v, i), depth + 1);
}
else if (depth < 3 && type == "struct")
{
    sb.AppendLine($"{pad}{name} = {{struct}}");
    foreach (var m in Gml.StructNames(v).Take(100)) DumpValue(sb, m, Gml.StructGet(v, m), depth + 1);
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L384-L394)</small>

Gotchas:

- Arrays and structs the game hands you are pooled and released at the end of the frame. To keep one
  across frames, use `Values.Keep` and later `Values.Free`; see [Values](../concepts.md#values).
- Cap loops over structures you do not know, as `Take(100)` does above: a game's data can be huge.

## Walk the object table and hierarchy {#object-table}

`GmlObject.All()` needs one builtin call per asset index, thousands in a big game. The first call does
that walk and caches it for the session. `ObjectTable.Start()` builds the whole table (parents included)
over a few frames instead, and `ObjectTable.Ready`, `Progress` and `Status` report how far it got, which
suits a progress bar:

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


`Parent`, `Ancestors()` and `IsA(name)` work before the table is ready, by asking the runtime directly.
`Children()` needs every object's parent, so before `Ready` it finishes the table on the spot, in one
frame, and `ObjectTable.Complete()` does that on demand. The StoneshardCheats catalogue calls
`ObjectTable.Start()`, then waits for `Ready` over later frames before reading `GmlObject.All()`
([Catalogue.cs lines 119-147](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Catalogue.cs#L119-L147)).

## Scale a value without compounding it {#base-value}

If you multiply a game value every frame, the value grows without bound. Remember what the game last
computed, and recognise your own write when you see it. DwarfBoost scales each unit's `damage`, which
the game recomputes from `baseDamage` whenever an upgrade changes it. Each instance remembers the value
the mod wrote:

```csharp
// instance id -> (the game's own damage, what we wrote, the unit's baseDamage then)
private readonly Dictionary<long, (double Base, double Written, double BaseDamage)> _damage = new();
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L33-L34)</small>

```csharp
double baseDamage = unit.Has("baseDamage") ? unit.Get("baseDamage").AsReal : double.NaN;
bool ours = _damage.TryGetValue(key, out var d)
            && Math.Abs(current - d.Written) < 1e-6
            && (double.IsNaN(baseDamage) || baseDamage.Equals(d.BaseDamage));

double gameValue = ours
    ? d.Base          // still our own value: the game has not recomputed
    : current;        // new instance or the game recomputed: that is the base now

double want = gameValue * _damageMultiplier;
if (Math.Abs(want - current) > 1e-6) unit.Set("damage", want);
_damage[key] = (gameValue, want, baseDamage);
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L103-L114)</small>

The same trick applies to income: DwarfBoost multiplies only the frame's *rise* in gold, so spending is
untouched, and resets its last reading when it edits gold itself so its own edit is not counted as income
([DwarfBoost.cs lines 70-87](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L70-L87)).

If a script's argument is what you want to scale, you do not need any of this: `HookCall.SetArg` changes
only what the original receives (see [Change a script's argument](hooks.md#change-an-argument)).
