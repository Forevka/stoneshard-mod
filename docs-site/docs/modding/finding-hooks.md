---
title: Finding hooks
description: How to work out which script, event or variable to hook in a game you have no source for, using the Console, ScriptSpy and the generated codemap.
---

A YYC game ships no source. To mod it you need three answers: where does the state live (which object or global holds the gold, the health, the inventory), which function changes it, and what do that function's arguments mean. This page is the workflow for finding them with the tools Lodestone ships, ending with two worked examples.

The short version:

1. Start the game with **Console** and **ScriptSpy** installed.
2. Search the Console's **Objects** tab (or `where <name>` in the console), for example "gold" or "damage", to find where the state lives.
3. Put ScriptSpy on a candidate function to see what its arguments mean at runtime.
4. If you need more, read what a function calls with `code`, and who calls it with `callers`.

Both tools are `[CoreModAnyGame]` mods, so they work in any YYC game. Deploy them with `tools\deploy-coreloader.ps1 -GameDir "<game>" -Mods Console,ScriptSpy`, or, as in [Getting started](getting-started.md), copy their dlls into `Mods\`.

## Find where the state lives

Most GameMaker games keep state in a few controller objects (an `oSys`, an `o_controller`) and in globals. The Console's **Objects** and **Globals** tabs find them.

### The Console

The Console mod is a tab in the overlay (**INSERT**), with *Inspector*, *Objects* and *Globals* tabs beside it. It evaluates GML-style expressions against the running game:

- builtins and scripts: `instance_number(o_enemy)`, `scr_some_script(1, "a")` (the `gml_Script_` prefix is optional);
- globals and instance variables, read or write: `global.gold = 500`, `oSys.gold *= 2`, `o_enemy[3].hp = 1`;
- numbers, strings and `+ - * / % == < && || !`; bare names resolve as assets, and `ans` is the last result.

The commands that matter for exploration:

| Command | What it does |
|---|---|
| `find <text>` | Scripts, events and builtins whose name contains the text (at least two characters) |
| `objects [filter]` | Objects with live instances; with a filter, all matching objects |
| `vars <obj>[n]` | Every variable of an instance, e.g. `vars oSys` or `vars o_enemy 2` |
| `globals [filter]` | Global variables and their values |
| `where <text>` | Live instances that have a variable named like the text |
| `code <function>` | What a script or event calls, and the strings it uses (read-only) |
| `callers <function>` | Every script and event that calls it (scans all code) |
| `hook <script>`, `unhook <script\|all>`, `hooks` | Print each call's arguments and result |
| `inspect`, `inspect <obj> [n]` | Pick an instance by clicking it in the game, or name one |
| `dump [<obj> [n]]` | Write the inspected instance to `Mods\Console\dumps` |
| `frozen`, `unfreeze all` | Variables held by a freeze |

Up and Down in the input line walk the history, which is kept between sessions.

A typical first move: you want to change the player's gold, so run `where gold`. It lists every live instance with a variable named like "gold", for example `oSys` in Dwarf Eats Mountain. Then `vars oSys` shows all its variables and their current values, and `oSys.gold += 1000` proves you have the right one by changing the number in the game.

### Objects and Globals tabs

The **Objects** tab lists every object with its live instance count and pages through an object's instances with a variable table. It also searches every live instance for a variable name, the same as `where <text>`. The **Globals** tab lists, edits and freezes global variables. Use it when the state is in a global rather than an object (`global.gold`).

A **freeze** holds a variable at a value every frame, across any number of instances. It is the quickest experiment there is: freeze a variable and play. If the game behaves as if you changed the thing you expected, you found the state. `frozen` lists active freezes and `unfreeze all` lifts them.

### Inspector and pick mode

Run `inspect` (or use the Inspector tab) and click any instance in the game. The click goes to the loader, not the game. This is **pick mode**; mods can use it too through `Input.ArmPick()` and `Input.TryTakePick(out click)`. The Inspector then shows:

- the instance's object and its parents, which tells you which family it belongs to (in Dwarf Eats Mountain every unit is a child of `parDwarf`);
- every variable, with **editing** (type any expression to set it), **freezing**, and expansion of arrays and structs;
- read-only **code**: what each of its events calls and which strings they use, and who calls those.

The parents matter for modding: objects in one family usually share variables and scripts, so knowing the parent tells you whether one change can cover all of them.

## Watch a function with ScriptSpy

Once you have a candidate function (from `find`, from the Inspector's code view, or from a name you guessed), **ScriptSpy** shows what it does at runtime. It hooks the function by name and records each call: the arguments going in, the result coming out, and the object each call ran as.

In the overlay, open the *Script Spy* tab, type a function name (`scr_get_XP`, `gml_Object_o_player_Step_0`) and press **Watch**, or search with **find** and click a result. Each watch keeps its recent calls (they are also written to the log) and can be paused, cleared or unhooked.

What to look for:

- **How many arguments, and what values.** Do the thing in the game once, then read the calls. If a function is called with one number that is bigger after a harder fight, it probably takes an amount.
- **Who is self.** Each call shows the object it ran as, which tells you whether the function acts for one object or for all.
- **What it returns.** The recorded result tells you whether you want an `After` hook that replaces `Result`.
- **What it changes.** Over the test host a watch can also track a **variable of self**, read before and after each call, to see what a script changes: `spy.watch <function> <variable>`.

A function that only runs while the game loads is too early to watch from the tab. List one symbol per line in `ScriptSpy.txt` next to the dll, and ScriptSpy starts those watches with the game.

The test-host commands are in the table below; they let a script, or an agent, drive the same workflow:

| Command | What it does |
|---|---|
| `spy.watch <function> [variable]` | Starts watching (and reading that variable of self around each call) |
| `spy.read [function]` | Every watch (or one) with its call count and recent calls |
| `spy.clear` | Forgets the recorded calls |
| `spy.unwatch <function\|all>` | Stops watching |

Because hooks are shared, watching a function does not disturb a mod that hooks it too. Unwatch when done; a function nobody hooks runs at full speed again.

The Console's `hook <script>` does a lighter version of the same thing: it prints each call's arguments and result into the console.

## Browse the codemap {#browse-the-codemap}

The generated [interop](interop.md) holds a map of the whole game. `codemap.json` in `<game>\Lodestone\Interop\<Game>.Interop\` lists every function with its address, every script with the number of arguments it reads, every object with its events and harvested variables, the builtins, and the sprite, room and sound names. Open it in an editor with search, or load it in a script.

The same data is in the generated C#: open `Scripts.g.cs` or `Objects.g.cs` in your IDE (with `CoreLoader.Dev.sln`, *Go To Definition* on `Scripts.dealDamage` lands there) and use your editor's search. IntelliSense on `Scripts.` and `Objects.` is a way of browsing the game: type `Scripts.deal` and read the completions.

Event names tell you a lot. An object with `Step_0`, `Draw_0` and `Alarm_0` is something that updates and draws itself. A script named `scr_get_XP` that is called by many events is a choke point worth hooking.

## The offline route

Sometimes you need to know what a function does, not only what it is called with. The live tools above are enough for almost everything; the offline reverse-engineering toolkit in `tools\re\` is for the rest. It reads the game's exe without running it: who calls whom, which strings a function references, the builtin table, assets from `data.win`, and a Ghidra script (`tools\re\ghidra\ExportGml.java`) that decompiles every script and event into one `.c` file each, named by symbol.

Treat decompiled output as a map for you, not as source to port: build the mod from the generated interop and what the live tools show you. See the [reverse-engineering toolkit](../internals/re-toolkit.md) for the commands.

## Worked example: how the Stoneshard harness was found

The StoneshardHarness mod plays Stoneshard through the test host without screenshots: it walks, fights, trades, loots and talks through the game's own scripts. Every script path it uses was found the same way: **watch candidate scripts and events with ScriptSpy (`spy.watch`), do the action once with a real click, read the spy, then replay those calls.**

1. **Guess candidates.** `find move`, or `callers` on a function you already know, gives a short list of scripts and events that sound right.
2. **Watch them.** `spy.watch <candidate>` for each one (and for the events of the objects involved).
3. **Do the action once, with a real click.** Click a floor tile and read the spy. For walking, the script that ran was `scr_player_move`, as the player.
4. **Replay.** Call the script yourself with the same arguments and check that the same thing happens. That is exactly what `hx.move <dx> <dy>` does: it calls `scr_player_move(x, y)` as the player, which is what a floor click runs. `hx.attack` clicks the enemy through its `Mouse_4` event, found the same way, with `scr_mouse_on_unit` answering that enemy while it runs.

The results of that process are recorded in the mod's source (the remarks in `Act.cs`, `Bag.cs`, `Dialogue.cs`, `Gui.cs`, `Grid.cs` and `Screen.cs` say what each path does) and in its [README](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/README.md#how-it-was-found). Facts that took watching to learn:

- Walkability is `astar_get_cell(o_controller.cleangrid, x, y)` (0 walkable, -1 not). It knows the void outside a room and furniture such as a counter, which `wallgrid` leaves at 0.
- The current room cannot be read (GML's `room` is not reachable), so the harness tracks it from `scr_smoothRoomChange`.
- The GUI is not the Draw GUI layer but instances in room space under `global.cameraGUI`, whose view sits at (-5000, -5000).

One warning from the same experience: **running an event by guess can stop the game** with a GML "Code Error". It happened three times while this was built (the floor cursor's user events on a corpse, and a loot item's pick-up events without the state its click sets up). Watch what the game does first, and replay the whole call, not a part of it.

## Worked example: Dwarf Eats Mountain

The DwarfBoost mod needed the game's economy and its units' damage. The source says it all: *the game keeps its economy on the `oSys` controller (gold, mithril, soul) and every unit (miners, flamers, harpoons, cannons, all children of `parDwarf`) carries its own `damage`, recomputed by the game from `baseDamage` and its modifiers whenever an upgrade changes them. Found with the Console's object search; nothing here is hardcoded to an address.*

In practice:

1. `where gold` in the Console, or "gold" in the Objects tab search, finds `oSys`. `vars oSys` shows `gold`, `mithril` and `soul`. `oSys.gold += 1000` confirms it.
2. "damage" in the same search finds a `damage` variable on many instances. Inspecting a unit shows its parent `parDwarf`, and that each unit carries its own `damage`, which the game recomputes from `baseDamage` and its modifiers when an upgrade changes them.
3. The mod therefore does not hook a script at all. It reads `oSys.gold` every frame and scales the rises (leaving drops alone, so purchases cost what they say), and for each unit it remembers the value it wrote, so that when the game writes a new one (an upgrade), that becomes the new base and the multiplier never compounds.

The mod's header is quoted in [DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L8-L23). Reading and writing state, instead of hooking, is often the simplest route when the state is easy to find and the game recomputes it on its own schedule. When it is not (an XP amount that is added and forgotten), you hook the function, as StoneshardBoost does with `scr_get_XP`: see [concepts](concepts.md#hook-arguments) and the [hooks cookbook](cookbook/hooks.md).
