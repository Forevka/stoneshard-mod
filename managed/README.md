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

## API at a glance

| Area | What you get |
|---|---|
| `CoreMod` | `OnInitialize`, `OnUpdate` (every frame), `OnGUI` (the mod's own tab), `OnShutdown`, plus `Log`, `Config`, `Directory` |
| `Hooks` | `Before`/`After` on any `gml_Script_*` or `gml_Object_*`, or the `[HookBefore]`/`[HookAfter]` attributes. The `HookCall` passed to a handler exposes `Self`, `Other`, `GetArg`/`SetArg`, `Result`, `SkipOriginal()` and `CallOriginal()` |
| `Game` | `Name`, `Symbols`, `CallScript`, `CallEvent`, `CallBuiltin`, `BuiltinArity`, `CurrentSelf`, `RunOnGameThread` |
| `Globals`, `GmlObject`, `InstanceRef` | Read and write global and instance variables by name, list objects and live instances |
| `Gml` | `TypeOf`, arrays and structs through the runtime's own builtins |
| `UI` | ImGui widgets for your tab, including scrolling regions and a history-aware input line. Scopes are tracked, so a mistake can't corrupt the overlay |
| `RValue` | The runtime's 16-byte value, laid out identically. Converts implicitly from double, int, bool and string |
| `Values` | Lifetime of strings, arrays and structs: `Keep`, `Free`, `Copy` |

The rules the loader enforces:
- GML is only touched on the game thread. Every callback runs there; from anywhere else, use
  `Game.RunOnGameThread`.
- A mod that throws, or that leaves UI scopes open, is disabled until it is reloaded, and its hooks
  are removed. It never takes the game down.
- Hooks are shared: however many mods hook one function, it is detoured once, and the loader's own
  tools share the same detour. A function nobody hooks any more is detached again.
- **Values are released automatically.** Every string, array or struct the game hands you (call
  results, variable reads, `RValue.FromString`) goes into a per-frame pool and is released at the
  end of the frame. Using it within the frame is always safe and never leaks, including formatting a
  new string every frame. Only a value you keep in a field across frames needs `Values.Keep(v)`,
  and later `Values.Free(ref v)`.

## Mods in this repository

| Mod | Game | What it does |
|---|---|---|
| Console | any | In-game console. Evaluates GML-style expressions against the live game: `instance_number(o_enemy)`, `oSys.gold += 1e6`, `global.x`, `obj[2].hp = 1`, `scr_foo(1, "a")`. Also `find`, `objects`, `vars`, `globals`, `hook`/`unhook` for live call logging, and history |
| ScriptSpy | any | Hook any function by name and watch its arguments and results live. Also logs them; `ScriptSpy.txt` lists watches to start with the game |
| GlobalsEditor | any | Browse, edit and freeze global variables |
| InstanceInspector | any | Objects, live instances and their variables: edit and freeze them, and search every instance for a variable name |
| SpeedControl | any | Run the game faster or slower (`game_set_speed`) |
| DwarfBoost | Dwarf Eats Mountain | Gold income and unit damage multipliers, resource editor |
| StoneshardBoost | Stoneshard | XP multiplier (every source goes through `scr_get_XP`), loot multiplier (re-runs `scr_loot`) |
| HelloMod, InteropExample | any / DEM | Minimal examples |

`Tests/` holds regression mods for the loader itself:
- a UI-fault mod;
- a GC-safety variable probe;
- a reflection probe;
- an XP probe;
- a value-lifetime probe (memory stays flat while it creates about 3,000 strings a frame);
- a hook-coexistence probe (C# and the loader's own tools on the same event).

## Finding things to mod

1. Start the game with **ScriptSpy**, **GlobalsEditor** and **InstanceInspector**.
2. InstanceInspector's search, e.g. "gold" or "damage", finds where the state lives: in Dwarf Eats
   Mountain it found `oSys.gold` and each unit's `damage`.
3. ScriptSpy on a candidate function shows what its arguments mean at runtime.
4. For the full picture, `tools/re/ghidra/ExportGml.java` decompiles every script and event into
   one `.c` file each, named by symbol.
