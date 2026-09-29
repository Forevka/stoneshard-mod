# CoreLoader: C# mods for any YYC GameMaker game

CoreLoader lets you mod GameMaker games compiled with YYC using C#, in the style of MelonLoader. It is a
`version.dll` placed next to the game exe, and it needs no per-game setup. On first launch it finds
the game's scripts, object events and builtins, then starts .NET inside the game and loads C# mods
from `Mods\`.

It has been verified on two games, five years of GameMaker runtime apart:

| | Stoneshard | Dwarf Eats Mountain (runtime 2024.14) |
|---|---|---|
| GML functions found | 34,167 | 4,968 |
| Builtins | 2,532 | 2,861 |
| Interop generated | 260 ms | 80 ms |

## Installing

1. Build: `cmake --build build` (native loader) and `dotnet build managed\CoreLoader.sln -c Release`.
2. Install into a game: `tools\deploy-coreloader.ps1 -GameDir "<game folder>" -Mods Console,ScriptSpy`.

The game folder ends up with:

```
<game>\version.dll                 the loader (a proxy for the system version.dll)
<game>\CoreLoader\CoreLoader.dll   the .NET runtime side, plus its runtimeconfig
<game>\CoreLoader\Interop\         generated per game (see below)
<game>\Mods\*.dll                  your mods; Mods\<Name>.json holds their settings
```

Players need the .NET 10 runtime. It can also be shipped privately in `CoreLoader\dotnet\`. The
overlay (**INSERT**) has a *Mods* tab that shows the loader's status and one tab per mod.

The log is `CoreLoader\Logs\coreloader.log`. The previous session's log is kept as
`coreloader.prev.log`, so a crash's trail survives the next launch. An identical line repeated many
times a second is written a few times, then summarised.

## The mod-author workflow

1. Install CoreLoader and start the game once.
2. CoreLoader writes `CoreLoader\Interop\<Game>.Interop\`, a buildable project that contains:
   - `Scripts.*`: a ref for every script, to call or hook. Where the argument count can be read from
     the compiled code (366 of 486 scripts in Dwarf Eats Mountain), the ref has a typed `Invoke`, e.g.
     `Scripts.dealDamage` is a `ScriptRef6`. The rest keep `Call(params)`;
   - `Objects.<object>.<Event>_<n>`: an `EventRef` for every object event, plus `Objects.<object>.First`
     (its first live instance) and `Objects.<object>.Vars.<name>`, the variable names harvested from
     live instances as you play (2,020 in the first minute of Dwarf Eats Mountain). `InstanceVars`
     holds GameMaker's built-in ones (`x`, `y`, `object_index`, ...), which every harvested `Vars` class repeats.
     A script ref runs as an instance with `CallAs(instance, other, args)`, or `CallAs(instanceRef, args)`
     for one held by id;
   - `Builtins.*`: typed wrappers that use the argument counts this game's runtime actually registers;
   - `Assets.*`: sprite, room and sound names;
   - `codemap.json`: all of the above, plus addresses, argument counts and variables, for tools.

   It is regenerated when the game exe changes, and after the harvester learns new variables (on
   the next launch, or immediately with *Regenerate interop now* in the Loader tab).
3. Create the mod from the template, which references CoreLoader and the interop and deploys every
   build into the game:

   ```
   dotnet new install managed\Templates\CoreLoaderMod
   dotnet new coreloader-mod -n MyMod --gameDir "<game folder>" --gameName "<Exe name>" --interop <Game_Interop_Namespace>
   dotnet build MyMod
   ```

   With the game running, the build is **hot-reloaded**: CoreLoader watches `Mods\`, and a rebuilt
   mod is swapped in between frames. The old copy gets `OnShutdown`, and its hooks and config are
   released. The Loader tab also has Reload buttons. The same goes for a mod that
   `tools\deploy-coreloader.ps1 -Mods` copies into a running game: it is reloaded at once, against the
   runtime already running. `-Live` defers only `version.dll` and the runtime to the next launch.

### Working in this repository with Visual Studio

Projects here that use a game's interop, such as `Examples\InteropExample`, name the game with
`<InteropGame>Dwarf_Eats_Mountain</InteropGame>`. The build finds that game's generated interop by
itself. It just needs to know where your games are:

```
tools\setup-dev.ps1                                    # finds games with CoreLoader in your Steam libraries
tools\setup-dev.ps1 -GameDir "D:\Games\Stoneshard"     # plus any other folder
```

This writes two files, both kept out of git:
- `managed\CoreLoader.user.props`, which lists the game folders. Every build reads it, in Visual
  Studio and with `dotnet build`. You can edit it by hand; `CoreLoader.user.props.example` shows
  the format.
- `managed\CoreLoader.Dev.sln`, which is `CoreLoader.sln` plus each game's generated `<Game>.Interop`
  project. Open this one to build interop-based projects and to *Go To Definition* straight into the
  generated source.

Without either file the solution still loads and builds: an interop-based project compiles nothing
and gives a warning saying what to run. The environment variable `CORELOADER_GAME_DIRS` works in
place of the props file (on a build machine, say), and `-p:InteropProject=<path>` still overrides
everything.

## A mod

```csharp
using CoreLoader;

[assembly: CoreModInfo(typeof(MyMod), "My Mod", "1.0.0", "Me")]
[assembly: CoreModGame("StoneShard")]           // optional: only load in this game

public sealed class MyMod : CoreMod
{
    private float _xp;

    public override void OnInitialize() => _xp = Config.Get("xp", 2f);

    [HookBefore("scr_get_XP")]                   // or Scripts.scr_get_XP.Before(...)
    private void DoubleXp(HookCall c) => c.SetArg(0, c.GetArg(0).AsReal * _xp);

    public override void OnGUI()
    {
        if (UI.SliderFloat("XP x", ref _xp, 1, 10)) Config.Set("xp", _xp);
    }
}
```

Build it against `CoreLoader.dll`: the `Mods/` projects here inherit that setup from
`Mods/Directory.Build.props`. Then drop the dll into `<game>\Mods\`.

Content files go in a folder named after the mod, next to its dll (`Mods\MyMod\...`). Relative paths
resolve there:

```csharp
public override void OnInitialize()
{
    var coin  = Content.AddSprite("assets/coin.png", frames: 8, xOrigin: 24, yOrigin: 24);
    var chime = Content.AddSound("assets/chime.ogg");
    Content.ReplaceSprite("spr_player", "assets/hero.png");   // a reskin, undone on unload
    GameDraw.OnGui(() => coin.Draw(GameDraw.GuiWidth - 40, 40, frame: Environment.TickCount64 / 100));
    chime.Play();
}
```

## Analyzers

Mods are compiled with `CoreLoader.Analyzers`, which reports the lifetime mistakes the runtime can
only catch as a crash. The `Mods/`, `Tests/` and `Examples/` projects here get it from their
`Directory.Build.props`. Template mods get it from `<game>\CoreLoader\Analyzers\`, where
`tools\deploy-coreloader.ps1` installs it. It runs in the compiler only; the game never loads it.

| Rule | Reports | Why |
|---|---|---|
| CL0001 | A field or auto-property that holds an `RValue` (or an array, collection or tuple of them), or a stored, queued or registered lambda that captures one | Strings, arrays and structs from the game are pooled and released at the end of the frame. Keep C# data (`AsReal`, `AsString`), or own the value with `Values.Keep` and release it with `Values.Free` |
| CL0002 | A field or auto-property that holds an `Instance` or a `HookCall`, or a lambda that captures one and is stored, queued or registered (`Game.RunOnGameThread`, `Task.Run`, a field, a collection, `Hooks.Before`/`After`/`NextBefore`/`NextAfter`, `TestHost.Register`, `GameDraw.OnGui`) | An `Instance` is a raw pointer that dangles once the instance is destroyed: hold an `InstanceRef`. A `HookCall` is valid only inside its handler |
| CL0003 | `Values.Free` on a local read from `HookCall.GetArg` or `HookCall.Result` | The game lends hook arguments and results; freeing one releases the caller's reference |

Instance fields of a `ref struct` are exempt: it cannot outlive the call that made it. Keeping a
value on purpose (a number, or one owned with `Values.Keep`) is fine: suppress the warning on that
member with a reason, e.g. in `GlobalSuppressions.cs`:

```csharp
[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member",
    Target = "~F:MyMod.MyMod._frozen", Justification = "Values.Keep'd, freed in OnShutdown.")]
```

## API at a glance

| Area | What you get |
|---|---|
| `CoreMod` | `OnInitialize`, `OnUpdate` (every frame), `OnGUI` (the mod's own tab), `OnShutdown`, plus `Log`, `Config`, `Directory` |
| `Hooks` | `Before`/`After` on any `gml_Script_*` or `gml_Object_*`, or the `[HookBefore]`/`[HookAfter]` attributes. The `HookCall` passed to a handler exposes `Self`, `Other`, `GetArg`/`SetArg`, `Result`, `SkipOriginal()` and `CallOriginal()`. `NextBefore`/`NextAfter` run code once, inside the next matching call, with a timeout |
| `Game` | `Name`, `Symbols`, `CallScript`, `CallScriptAs` (as an `Instance`, or as the instance an `InstanceRef` names), `CallEvent`, `CallBuiltin`, `BuiltinArity`, `CurrentSelf`, `CanResolveInstances`, `RunOnGameThread`. A call the game rejects throws `GmlException` with the GML error's message, e.g. `call to scr_x failed: Variable ... not set before reading it. (in gml_Script_scr_x, line 12)` |
| `Globals`, `GmlObject`, `InstanceRef` | Read and write global and instance variables by name, list objects and live instances. `GmlObject.Parent`, `Ancestors()`, `IsA(name)`, `Children()` walk the object hierarchy. `InstanceRef.Resolve()` turns an id into the live `Instance` (null if it is gone, or if this runtime's id lookup could not be proven), and `InstanceRef.CallScript` runs a script as it |
| `ObjectTable` | The object table (index, name, parent), read once over a few frames and cached: `Start()`, `Ready`, `Progress`, `Status`, `Complete()` |
| `DsMap`, `DsList` | ds_map and ds_list by id: `Exists`, `Count`, `Get`/`Set`/`Has`/`Remove`, `Entries()`, `ToJson()`; `At`, `Add`, `Insert`, `RemoveAt`, `Clear`, `Items()` |
| `Gml` | `TypeOf`, arrays and structs through the runtime's own builtins |
| `UI` | ImGui widgets for your tab: text, buttons, inputs, sliders, combos, selectable rows, progress bars, disabled blocks, text colour, tooltips, scrolling regions, clipped long lists (`UI.Clipped`) and a history-aware input line. Scopes are tracked, so a mistake can't corrupt the overlay |
| `RValue` | The runtime's 16-byte value, laid out identically. Converts implicitly from double, int, bool and string |
| `Values` | Lifetime of strings, arrays and structs: `Keep`, `Free`, `Copy` |
| `Content` | New sprites from PNG (`AddSprite`), reskins of the game's own sprites (`ReplaceSprite`) and sounds from OGG (`AddSound`), loaded at runtime. `Sprite.Draw`, `Sound.Play`/`Stop` |
| `GameDraw` | `OnGui(handler)`: draw into the game's own GUI layer each frame with `draw_*` builtins and your sprites |
| `Input` | Pick mode: `ArmPick()`, then `TryTakePick(out click)` gives the next click outside the overlay (the game never sees it), in window pixels and room coordinates |
| `TestHost` | Development only: `Enabled`, and `Register(name, handler, help)` for commands a test script sends over the test host's pipe (see [Test host](#test-host)) |
| `Code` | Read-only views of compiled code: `Describe(fn)` lists the scripts, events and builtins a function calls and the strings it uses; `FindCallers(fn)` finds what calls it |

The rules the loader enforces:
- GML is only touched on the game thread. Every callback runs there; from anywhere else, use
  `Game.RunOnGameThread`.
- A mod that throws, or that leaves UI scopes open, is disabled until it is reloaded, and its hooks
  are removed. It never takes the game down.
- Hooks are shared: however many mods hook one function, it is detoured once, and the loader's own
  tools share the same detour. A function nobody hooks any more is detached again.
- Mods start once the game has loaded its assets. Some games (Stoneshard) load them seconds after
  their first frame.
- Content belongs to the mod that added it. Unloading or hot reloading the mod deletes its sprites,
  closes its sounds and gives replaced sprites their original image back. If several mods replace
  one sprite, unloading them in any order restores what was there before each one. When a mod
  hot-reloads, its reskin goes back on top of the stack. An added sprite is emptied rather than
  deleted, so anything still showing it draws nothing instead of crashing, and its slot is reused.
  Content files are not watched: reload the mod after changing one.
- **Values are released automatically.** Every string, array or struct the game hands you (call
  results, variable reads, `RValue.FromString`) goes into a per-frame pool and is released at the
  end of the frame. Using it within the frame is always safe and never leaks, including formatting a
  new string every frame. Only a value you keep in a field across frames needs `Values.Keep(v)`, and
  later `Values.Free(ref v)`. Structs are garbage-collected rather than reference-counted, and the
  collector can't see a pointer held in C#. So `Keep` also roots a kept struct in a GML array (the
  global `__coreloader_roots`), and `Free` takes it out again.
- A script with mod hooks on it is called with private copies of its arguments. `SetArg` changes
  what the original and later handlers see, and nothing else: not the caller's variables, and not
  the constants the compiler passes literals from. So a multiplier can't compound.
- Everything a mod registers belongs to it, including registrations made in its constructor or from
  a background task, and goes when it unloads. That covers hooks, draw handlers, content, a pick in
  progress, kept structs and queued actions. A hot reload loads the new build first; if it can't
  load yet, the running copy stays.
- GML is single-threaded. From another thread, the loader refuses every call that touches it
  (builtins, scripts, strings, value free/copy), including calls from native plugins, and logs the
  refusal.

### ds_maps and ds_lists

Many games keep their real state in ds_maps and ds_lists and hand out only the id. `DsMap` and
`DsList` wrap that id and go through the game's own builtins. The struct is safe to keep across
frames (a destroyed map just stops `Exists`ing), but strings read out of one are pooled like any
other value.

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

### Running code inside the game's own event

Some scripts only work from inside the event they were written for, and throw when a mod calls
them from its tab. Arm a one-shot request, make the game run the event, and do the work there:

```csharp
var request = Hooks.NextAfter("gml_Object_o_bottle_Alarm_0",
    match: c => !c.OriginalSkipped,   // which call is yours; null accepts the first one
    action: c => Game.CallScriptAs(c.Self, c.Self, "scr_bottle_refresh"),
    timeout: TimeSpan.FromSeconds(2),
    onTimeout: () => Log.Warning("the bottle never rolled"));
Game.CallBuiltin("instance_create_depth", x, y, 0, bottleIndex);
// request.IsPending, request.Dispose() to cancel
```

The action runs at most once; the request is disarmed before it runs. It belongs to your mod and
goes when the mod unloads. The timeout is checked every frame, so `onTimeout` runs on time even if
the event never fires; it runs between frames, never inside the game's call. An exception from `match`, `action` or `onTimeout` faults the mod like any
hook, so catch inside them to report a failure yourself.

### The object table and hierarchy

`GmlObject.All()` needs one builtin call per asset index, thousands in a big game. The first
call does that walk and caches it for the session. `ObjectTable.Start()` builds the whole table
(parents included) over a few frames instead, and `ObjectTable.Ready`/`Progress`/`Status` report
how far it got, which suits a progress bar:

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

`Parent` and `Ancestors()` work before the table is ready, by asking the runtime directly.
`Children()` needs every object's parent, so before `Ready` it finishes the table on the spot, in
one frame.

## Test host

For automated testing, the loader can take commands over a named pipe: a script (or an agent) drives
the game and reads its state back without clicking through the overlay.

**Development only.** It is off unless the game is started with the environment variable
`CORELOADER_TEST=1`, and the log says which way it went (`test host ON` / `test host off`). The pipe,
`\\.\pipe\coreloader-<pid>`, admits only the user running the game, but anything running as that user
can then call any script and write any variable. Never set the variable for normal play.

Steam relaunches some games through `steam.exe`, and the variable does not survive that. For those,
a file `CoreLoader\testhost.enable` in the game folder turns the host on too. `run-game.ps1 -TestHost`
writes it, and a launch without `-TestHost`, or `-Stop`, removes it.

```powershell
tools\run-game.ps1 -Game Stoneshard -TestHost          # launches with CORELOADER_TEST=1
tools\run-game.ps1 -GameDir "D:\Games\Other" -TestHost # any other game, by folder
tools\smoke-generic.ps1 -GameDir "D:\Games\Other"      # core + Console checks for any YYC game
tools\coreloader.ps1 -Game Stoneshard status           # one command, result printed as JSON
tools\coreloader.ps1 -Game Stoneshard call scr_atr STR
. tools\coreloader.ps1 -Game Dwarf                     # or, from a script:
Invoke-CoreLoader builtin string_upper "abc"           #   returns the result, throws on failure
Wait-CoreLoader { (Invoke-CoreLoader object-count o_enemy) -gt 0 } -TimeoutSec 30
```

The protocol is one JSON object per line each way:
`{"id":1,"cmd":"call","args":["scr_foo",1,"a"]}` is answered with `{"id":1,"ok":true,"result":...}`
or `{"id":1,"ok":false,"error":"..."}`. The pipe's name is also written to
`CoreLoader\Logs\testhost.pipe`. Arguments are numbers, strings, booleans or null. Results are
numbers, strings, booleans, null (undefined) and arrays. An instance or asset reference becomes its id
as a number, which can be passed back. Any other value becomes its GML string.

Commands run on the game thread at the start of a frame, never on the pipe's thread. Nothing blocks
the frame: `wait-frames n` answers n frames later, and a longer wait is polled from the client
(`Wait-CoreLoader`).

| Command | Does |
|---|---|
| `ping`, `status`, `mods`, `log [n]` | Liveness, game and bridge state, frame count, each mod's state and fault, the last n log lines |
| `reload <mod\|all>` | Reloads a mod, as the Loader tab does |
| `call <script> [args]`, `builtin <name> [args]` | Calls a script or builtin. `"as":"current"` runs it as the instance the game last ran; `"as":<instance id>` runs it as that instance (needs `Game.CanResolveInstances`) |
| `global-get <name>`, `global-set <name> <value>` | Global variables (set answers the value read back) |
| `instance-get <object> <n> <var>`, `instance-set <object> <n> <var> <value>` | Instance variables of an object's n-th live instance, or of an instance id (`<id> <var>`) |
| `object-count <object>` | Live instances, children included |
| `wait-frames [n]`, `list-commands` | Wait n frames; every command with its help, including mods' |

A mod adds its own commands, usually only when the host is on:

```csharp
public override void OnInitialize()
{
    if (TestHost.Enabled)
        TestHost.Register("mymod.gold", args => { Globals.Set("gold", args[0].GetDouble()); return Globals.Get("gold"); },
            "mymod.gold <n>: sets the gold, answers it read back");
}
```

A command belongs to the mod that registered it and goes when the mod unloads or hot-reloads. A
handler that throws answers `ok:false` with the message and does **not** fault the mod: a test feeds
commands bad input on purpose. A faulted mod's commands answer with its fault. The Console mod adds
`console <line>`, and StoneshardCheats adds `cheats.*` (see `list-commands`).
`tools\smoke-stoneshard.ps1` and `tools\smoke-dwarf.ps1` exercise them against a running game.

## Mods in this repository

| Mod | Game | What it does |
|---|---|---|
| Console | any | In-game console. Evaluates GML-style expressions against the live game: `instance_number(o_enemy)`, `oSys.gold += 1e6`, `global.x`, `obj[2].hp = 1`, `scr_foo(1, "a")`. Also `find`, `objects`, `vars`, `globals`, `hook`/`unhook` for live call logging, and history. Its **Inspector** tab lets you click any instance in the game (`inspect`) and see its object and parents, every variable (edit with any GML expression, freeze, expand arrays and structs), and read-only code: what each of its events calls and which strings it uses, and who calls those. `code <fn>`, `callers <fn>` and `dump` do the same from the console. The **Objects** tab lists every object with its live instance count, pages through an object's instances with the same variable table, and searches every live instance for a variable name (`where <text>` in the console). The **Globals** tab lists, edits and freezes global variables. Freezes hold across any number of instances and globals until lifted (`frozen`, `unfreeze all`) |
| ScriptSpy | any | Hook any function by name and watch its arguments and results live, with the object each call ran as. Also logs them; `ScriptSpy.txt` lists watches to start with the game. Over the test host: `spy.watch <function> [variable]` (the variable of self is read before and after each call), `spy.read`, `spy.clear`, `spy.unwatch` |
| SpeedControl | any | Run the game faster or slower (`game_set_speed`) |
| ContentDemo | any | Runtime content. It loads a spinning coin sprite from a PNG strip and draws it in the game's GUI layer, plays a chime from an OGG file, and can reskin any game sprite by name. Its files ship in `Mods/ContentDemo/assets` |
| DwarfBoost | Dwarf Eats Mountain | Gold income and unit damage multipliers, resource editor |
| StoneshardBoost | Stoneshard | XP multiplier (every source goes through `scr_get_XP`), loot multiplier (re-runs `scr_loot`) |
| StoneshardCheats | Stoneshard | Stats, items (any weapon/armor at any rarity, a stat constructor, every inventory object), potions built from chosen effects, needs/vitals/XP/conditions/psyche, body parts, an enemy roster with Remove, and save import from another machine. Saves are backed up before the first cheat |
| Reliquary | Stoneshard | Twenty-two artifacts with a toll (the design's Censer is not in yet), from the Stoneshard Reliquary design (riders, panic buttons, vessels, auras, grafts, scaling passives). The first six, one per family, written only against the generated interop: Stavebound Ember (staff hits add a random element), Gorgoneion (petrify everything in sight, you included), Wolf's Heart (fills with hostiles in sight, spent on Rage), Copper Ring of Faith (a worn ring whose prayer strips effects from foes nearby), Grafted Hand (+40% one-handed damage; the arm never heals again), Pilgrim's Millstone (resistances for dodge and energy). Relics live in vanilla carrier items tagged in their saved data map; hover one in the inventory and press U to activate. Test host: `reliq.*` |
| HelloMod, InteropExample | any / DEM | Minimal examples |

`Tests/` holds regression mods for the loader itself:
- a UI-fault mod;
- an XP probe;
- a value-lifetime probe (memory stays flat while it creates about 3,000 strings a frame);
- a struct probe (a struct kept from C# survives forced garbage collections, and its root is released afterwards).

## Finding things to mod

1. Start the game with **Console** and **ScriptSpy**.
2. The Console's **Objects** tab search (or `where <name>` in the console), e.g. "gold" or "damage",
   finds where the state lives: in Dwarf Eats Mountain it found `oSys.gold` and each unit's `damage`.
   Its **Globals** tab covers state kept in globals.
3. ScriptSpy on a candidate function shows what its arguments mean at runtime.
4. For the full picture, `tools/re/ghidra/ExportGml.java` decompiles every script and event into
   one `.c` file each, named by symbol.
