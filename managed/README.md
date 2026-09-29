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
2. Install into a game: `tools\deploy-coreloader.ps1 -GameDir "<game folder>" -Mods ScriptSpy,GlobalsEditor`.

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
     live instances as you play (2,020 in the first minute of Dwarf Eats Mountain);
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
   released. The Loader tab also has Reload buttons.

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

## API at a glance

| Area | What you get |
|---|---|
| `CoreMod` | `OnInitialize`, `OnUpdate` (every frame), `OnGUI` (the mod's own tab), `OnShutdown`, plus `Log`, `Config`, `Directory` |
| `Hooks` | `Before`/`After` on any `gml_Script_*` or `gml_Object_*`, or the `[HookBefore]`/`[HookAfter]` attributes. The `HookCall` passed to a handler exposes `Self`, `Other`, `GetArg`/`SetArg`, `Result`, `SkipOriginal()` and `CallOriginal()` |
| `Game` | `Name`, `Symbols`, `CallScript`, `CallScriptAs` (as an `Instance`, or as the instance an `InstanceRef` names), `CallEvent`, `CallBuiltin`, `BuiltinArity`, `CurrentSelf`, `CanResolveInstances`, `RunOnGameThread`. A call the game rejects throws `GmlException` with the GML error's message, e.g. `call to scr_x failed: Variable ... not set before reading it. (in gml_Script_scr_x, line 12)` |
| `Globals`, `GmlObject`, `InstanceRef` | Read and write global and instance variables by name, list objects and live instances. `InstanceRef.Resolve()` turns an id into the live `Instance` (null if it is gone, or if this runtime's id lookup could not be proven), and `InstanceRef.CallScript` runs a script as it |
| `Gml` | `TypeOf`, arrays and structs through the runtime's own builtins |
| `UI` | ImGui widgets for your tab: text, buttons, inputs, sliders, combos, selectable rows, progress bars, disabled blocks, text colour, tooltips, scrolling regions, clipped long lists (`UI.Clipped`) and a history-aware input line. Scopes are tracked, so a mistake can't corrupt the overlay |
| `RValue` | The runtime's 16-byte value, laid out identically. Converts implicitly from double, int, bool and string |
| `Values` | Lifetime of strings, arrays and structs: `Keep`, `Free`, `Copy` |
| `Content` | New sprites from PNG (`AddSprite`), reskins of the game's own sprites (`ReplaceSprite`) and sounds from OGG (`AddSound`), loaded at runtime. `Sprite.Draw`, `Sound.Play`/`Stop` |
| `GameDraw` | `OnGui(handler)`: draw into the game's own GUI layer each frame with `draw_*` builtins and your sprites |
| `Input` | Pick mode: `ArmPick()`, then `TryTakePick(out click)` gives the next click outside the overlay (the game never sees it), in window pixels and room coordinates |
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

## Mods in this repository

| Mod | Game | What it does |
|---|---|---|
| Console | any | In-game console. Evaluates GML-style expressions against the live game: `instance_number(o_enemy)`, `oSys.gold += 1e6`, `global.x`, `obj[2].hp = 1`, `scr_foo(1, "a")`. Also `find`, `objects`, `vars`, `globals`, `hook`/`unhook` for live call logging, and history. Its **Inspector** tab lets you click any instance in the game (`inspect`) and see its object and parents, every variable (edit with any GML expression, freeze, expand arrays and structs), and read-only code: what each of its events calls and which strings it uses, and who calls those. `code <fn>`, `callers <fn>` and `dump` do the same from the console |
| ScriptSpy | any | Hook any function by name and watch its arguments and results live. Also logs them; `ScriptSpy.txt` lists watches to start with the game |
| GlobalsEditor | any | Browse, edit and freeze global variables |
| InstanceInspector | any | Objects, live instances and their variables: edit and freeze them, and search every instance for a variable name |
| SpeedControl | any | Run the game faster or slower (`game_set_speed`) |
| ContentDemo | any | Runtime content. It loads a spinning coin sprite from a PNG strip and draws it in the game's GUI layer, plays a chime from an OGG file, and can reskin any game sprite by name. Its files ship in `Mods/ContentDemo/assets` |
| DwarfBoost | Dwarf Eats Mountain | Gold income and unit damage multipliers, resource editor |
| StoneshardBoost | Stoneshard | XP multiplier (every source goes through `scr_get_XP`), loot multiplier (re-runs `scr_loot`) |
| StoneshardCheats | Stoneshard | Stats, items (any weapon/armor at any rarity, a stat constructor, every inventory object), potions built from chosen effects, needs/vitals/XP/conditions/psyche, body parts, an enemy roster with Remove, and save import from another machine. Saves are backed up before the first cheat |
| HelloMod, InteropExample | any / DEM | Minimal examples |

`Tests/` holds regression mods for the loader itself:
- a UI-fault mod;
- a GC-safety variable probe;
- a reflection probe;
- an XP probe;
- a value-lifetime probe (memory stays flat while it creates about 3,000 strings a frame);
- a struct probe (a struct kept from C# survives forced garbage collections, and its root is released afterwards);
- a hook-coexistence probe (C# and the loader's own tools on the same event).

## Finding things to mod

1. Start the game with **ScriptSpy**, **GlobalsEditor** and **InstanceInspector**.
2. InstanceInspector's search, e.g. "gold" or "damage", finds where the state lives: in Dwarf Eats
   Mountain it found `oSys.gold` and each unit's `damage`.
3. ScriptSpy on a candidate function shows what its arguments mean at runtime.
4. For the full picture, `tools/re/ghidra/ExportGml.java` decompiles every script and event into
   one `.c` file each, named by symbol.
