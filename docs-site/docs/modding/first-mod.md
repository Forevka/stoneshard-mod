---
title: Your first mod
description: Build a mod from the template, then read the HelloMod example line by line to see attributes, the lifecycle, logging, config and the overlay tab.
---

This walkthrough has two halves. First you build the mod the template generates and see it run. Then you read `HelloMod`, the repository's smallest example, from top to bottom, because it shows more of the API than the template does. Finish with the [try it](#try-it) section.

It assumes you followed [Getting started](getting-started.md) and have a game with Lodestone installed.

## The template's mod

Generate a mod against the game you installed into (this example uses Stoneshard):

```powershell
dotnet new coreloader-mod -n MyMod --gameDir "D:\Games\Stoneshard" --gameName StoneShard
dotnet build MyMod
```

The class the template writes:

```csharp
public sealed class MyModMod : CoreMod
{
    private long _frames;
    private float _multiplier = 1f;

    public override void OnInitialize()
    {
        _multiplier = Config.Get("multiplier", 1f);
        Log.Info($"MyMod loaded in {Game.Name} ({Game.Symbols.Count} GML functions)");
        // ...
    }

    public override void OnUpdate() => _frames++;

    public override void OnGUI()
    {
        UI.Text($"Frames: {_frames:N0}");
        if (UI.SliderFloat("multiplier", ref _multiplier, 1f, 10f)) Config.Set("multiplier", _multiplier);
    }
}
```

<small>Source: [managed/Templates/CoreLoaderMod/MyMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Templates/CoreLoaderMod/MyMod.cs#L17-L40)</small>

(The `// ...` stands for lines that only exist when you pass `--interop`: a commented-out example hook through `Scripts.*`.)

Start the game, press **INSERT**, and find the *MyMod* tab. It counts frames and has a slider. Move the slider, close the game, and look at `<game>\Mods\MyMod.json`: the slider's value is saved there.

## Reading HelloMod

`HelloMod` is deliberately game-agnostic: it only uses what every YYC game has, so the same dll runs unchanged in any of them. The whole file is 61 lines. Here it is in pieces.

### Assembly attributes

```csharp
using CoreLoader;

[assembly: CoreModInfo(typeof(HelloMod.HelloMod), "Hello Mod", "0.2.0", "Lodestone")]
[assembly: CoreModAnyGame]
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L1-L4)</small>

A mod is a class library, and the loader recognises it by assembly-level attributes:

- **`[CoreModInfo(modType, name, version, author)]`** marks the assembly as a mod and names its entry type. All four values are required: the loader logs an error and skips the mod if the type, name, version or author is empty, or if the type is not a non-abstract subclass of `CoreMod`. The name is also the title of the mod's overlay tab. The loader finds mod dlls by reading this attribute from the file's metadata, so a dependency dll sitting in `Mods\` is never mistaken for a mod.
- **`[CoreModGame("StoneShard")]`** or **`[CoreModAnyGame]`**: every mod says which games it is for, with exactly one of these.

#### Declaring the game

`[CoreModGame]` takes the game's exe name without `.exe`, compared ignoring case. The game's interop namespace works too (`Dwarf_Eats_Mountain` for `Dwarf Eats Mountain.exe`). List several for a mod that supports more: `CoreModGame("StoneShard", "Dwarf Eats Mountain")`. In any other game the mod is skipped, with a log line and a grey entry in the Loader tab. One `Mods\` folder can therefore serve several games.

`[CoreModAnyGame]` is for a mod that relies on nothing a particular game defines: no object, script or variable names of its own. The Console and SpeedControl are like that, and so is `HelloMod`.

A mod that declares neither, or both, or a `[CoreModGame]` that names no game, is **not loaded**. The log and the Loader tab say what to add, and the test host's `status` lists it under `notLoaded`. The reason for being strict: running code written for one game inside another corrupts that game in ways that are hard to trace, so the loader would rather make the author hear about it the first time the mod runs anywhere.

The analyzer reports the same mistakes while you build:

- **CL0004** (error): the assembly has `[CoreModInfo]` but neither `[CoreModGame]` nor `[CoreModAnyGame]`.
- **CL0005**: the mod is compiled against a generated `<Game>.Interop` but its `[CoreModGame]` does not name that game, or it declares `[CoreModAnyGame]`. An interop names one game's scripts, objects and assets, so the mod cannot work anywhere else.

See [analyzers](reference/analyzers.md) for the full list.

### The class and its lifecycle

```csharp
public sealed class HelloMod : CoreMod
{
    private long _frames;
    private string _filter = "";
    private string _lastResult = "";
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L12-L16)</small>

The loader creates one instance of the type named in `[CoreModInfo]` and drives four callbacks. **All of them run on the game thread**, the only thread GML may be called from. An exception from any callback is logged and disables that mod (see [faults](concepts.md#faults)); it never reaches the game.

| Callback | When it runs |
|---|---|
| `OnInitialize()` | Once, when mods start. That is after the game has loaded its assets, which in Stoneshard is about 16 seconds after launch; a game that never answers still starts its mods after about 30 seconds. Hooks declared with `[HookBefore]` and `[HookAfter]` attributes are attached immediately before it, so `OnInitialize` can rely on them being live |
| `OnUpdate()` | Every rendered frame (inside the D3D11 `Present` hook) |
| `OnGUI()` | Every frame the overlay is open **and** the mod's tab is selected. Only here may you call `UI.*` |
| `OnShutdown()` | When the mod is unloaded, hot-reloaded, or the game window closes |

Because the constructor runs before any of that, do not read the game in a constructor or field initializer. Use `OnInitialize`. Anything you register from a constructor still belongs to your mod (see [ownership](concepts.md#ownership)).

The base class also gives you three properties:

- `Log`: a `Logger` tagged with the mod's name (`Info`, `Warning`, `Error`), writing to `lodestone.log`.
- `Config`: a `ModConfig`, the mod's persistent settings.
- `Directory`: the folder the mod's dll was loaded from.

### OnInitialize and Log

```csharp
public override void OnInitialize()
{
    Log.Info($"hello from {Game.Name}: {Game.Symbols.Count} GML functions, " +
             $"{Game.Symbols.Count(s => s.IsScript)} scripts, " +
             $"{Game.Symbols.Count(s => s.IsObjectEvent)} object events");
}
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L18-L23)</small>

`Game` is the static facade over the running game. `Game.Name` is the exe name; `Game.Symbols` is every compiled GML function the loader found, each with a `Name`, an `Address`, and `IsScript` and `IsObjectEvent` flags. After startup this line appears in `lodestone.log` tagged `Hello Mod`.

### Config

`HelloMod` does not use `Config`, but the template does: `Config.Get("multiplier", 1f)` reads a value with a fallback and `Config.Set("multiplier", _multiplier)` writes one. Settings are a flat JSON object stored next to the mod's dll, named after it (`Mods\<dll name>.json`, or `Mods\X\X.json` for a mod in its own folder). Changes are written about a second after the last one (a dragged slider saves once, not every frame), when the mod reloads, and on shutdown, through a temporary file so a crash never leaves a truncated file. You can hand-edit the file while the game is closed; comments and trailing commas are accepted. A file that cannot be parsed is kept as `<name>.json.bad` and the mod starts with empty settings.

A mod that wants its settings to appear in the game's own menu uses `ModSettings`; see [settings and persistence](cookbook/settings-and-persistence.md).

### OnUpdate

```csharp
public override void OnUpdate() => _frames++;
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L25-L25)</small>

`OnUpdate` runs every frame whether or not the overlay is open, so keep it cheap. This is where per-frame work belongs: the game does not give you an update loop of its own, and the overlay's `OnGUI` only runs while your tab is visible.

### OnGUI and the overlay tab

```csharp
public override void OnGUI()
{
    UI.Text($"Frames seen by this mod: {_frames:N0}");
    UI.Separator();

    UI.Text("Symbol search");
    UI.InputText("filter##sym", ref _filter, 128);
    if (_filter.Length >= 3)
    {
        int shown = 0;
        foreach (var s in Game.Symbols)
        {
            if (!s.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            UI.TextDisabled($"{s.Name}  @0x{s.Address:X}");
            if (++shown == 25) { UI.TextDisabled("..."); break; }
        }
    }
    UI.Separator();
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L27-L44)</small>

`UI` is a static facade over Dear ImGui. It is immediate-mode: you describe the tab again every frame, and widgets return whether the user acted (`UI.Button` returns true on the frame it was clicked) or edit a value you pass by `ref` (`InputText`, `SliderFloat`). The text after `##` in a label is an ID that is not displayed.

Two rules from the runtime apply here:

- `UI.*` throws outside `OnGUI`.
- Widgets that open a scope (a tree node, a child region, a tab bar) must be closed by the matching `End`/`Pop` call. A mod that returns with a scope still open is faulted, since it would corrupt every tab drawn after it. Use `UI.Guarded(draw, onError)` around code that may throw. See [faults](concepts.md#faults).

### Calling the game

```csharp
// A builtin call goes through the real GML runtime - the round trip the
// rest of the API depends on.
if (UI.Button("irandom(100) via the game's runtime"))
{
    try { _lastResult = Game.CallBuiltin("irandom", 100).ToString(); }
    catch (GmlException ex) { _lastResult = "failed: " + ex.Message; }
}
if (_lastResult.Length > 0)
{
    UI.SameLine();
    UI.Text(_lastResult);
}
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L46-L57)</small>

`Game.CallBuiltin` calls any GML builtin by name and returns an `RValue`, the runtime's 16-byte value. A call the game rejects throws `GmlException` carrying the GML error's message, so a mod that is probing the game catches it and shows the message, instead of being disabled. The `RValue` is only valid for this frame; [value lifetime](concepts.md#values) explains why.

### OnShutdown

```csharp
public override void OnShutdown() => Log.Info($"bye after {_frames:N0} frames");
```

<small>Source: [managed/Examples/HelloMod/HelloMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Examples/HelloMod/HelloMod.cs#L60-L60)</small>

`OnShutdown` is where you undo what you changed in the game: restore a variable you froze, point instances away from a sprite you are about to unload. You do **not** need to unhook, free draw handlers or delete content: the loader tears down everything a mod registered after `OnShutdown` returns.

### Content files

A mod can add sprites and sounds. Content files go in a folder named after the mod, next to its dll (`Mods\MyMod\...`), and relative paths resolve there. A sketch (the `ContentDemo` mod, linked below, is the working version):

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

With the template, put the files in the project's `content\` folder and they are copied to `Mods\MyMod\...`. Sprites are PNG strips (or JPG/GIF), sounds are OGG. Content belongs to the mod that added it: unloading or hot-reloading the mod empties its sprites (the slot is reused), closes its sounds and gives replaced sprites their original image back. **Content files are not watched**, so reload the mod after changing one. The [ContentDemo mod](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L30-L32) in the repository loads its sprite, sound and GUI draw handler the same way.

## Try it

1. Build the mod and deploy it. The template's build already copies it into `<game>\Mods\`. For `HelloMod` from this repository, use `tools\deploy-coreloader.ps1 -GameDir "<game>" -Mods HelloMod`, or build it and copy `HelloMod.dll` into `Mods\`.
2. Start the game and wait for the mods to start. In `<game>\Lodestone\Logs\lodestone.log` look for:

    ```text
    loaded Hello Mod 0.2.0 by Lodestone (HelloMod.dll)
    ```

   followed, once the game has loaded its assets, by the `hello from ...` line from `OnInitialize`.
3. Press **INSERT**, open the *Hello Mod* tab, type `scr` in the filter and press the `irandom` button.
4. With the game still running, change something visible (the text of the button, or the `Log.Info` in `OnInitialize`) and run `dotnet build` again.
5. Watch the log. About a second after the build finishes you should see the new copy load first, then the old copy shut down and unload:

    ```text
    loaded Hello Mod 0.2.0 by Lodestone (HelloMod.dll)
    bye after 5,432 frames
    unloaded Hello Mod
    reloaded Hello Mod 0.2.0
    ```

   The tab in the overlay keeps working, with your change. The frame counter restarts, since it is a field of the new instance.

The messages are the ones `HelloMod.OnShutdown` and the loader's mod manager write (`unloaded`, `loaded`, `reloaded`); each log line also carries a timestamp and the source tag, which are left out here, and the frame count is whatever your session reached. If the new build cannot load yet (still being written, or locked by an antivirus), the running copy stays and the loader tries again.

## Next

- [Concepts](concepts.md): the game-thread, value-lifetime, ownership and fault rules you have already touched.
- [Interop](interop.md): replace `Game.CallBuiltin("irandom", 100)` and string symbol names with compiler-checked members.
- [Finding hooks](finding-hooks.md): how to learn what a game's scripts do.
- [Cookbook](cookbook/index.md): recipes for hooks, drawing, input and settings.
