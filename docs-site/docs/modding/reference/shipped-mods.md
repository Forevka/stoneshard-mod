---
title: Shipped mods
description: Every mod, example and regression test in the repository, which game each targets, and which API features each is a good example of.
---

The repository ships a set of mods that double as worked examples. Each mod under `managed/Mods/`
has a README next to its source on GitHub; the links below go to it.

Source lives in
[`managed/Mods/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Mods),
[`managed/Examples/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Examples) and
[`managed/Tests/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests). Deploy one with
`tools\deploy-coreloader.ps1 -Mods <Name>`, or copy its dll into a running game's `Mods\` folder for
hot reload.

The **Game** column is what the mod declares: `any` means `[CoreModAnyGame]` (it uses nothing a
particular game defines), otherwise `[CoreModGame("<exe name>")]`. A mod written against a generated
[interop](../interop.md) is for that one game.

## Mods

| Mod | Game | What it does | Good example of |
|---|---|---|---|
| [Console](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/README.md) | any | In-game console. Evaluates GML-style expressions against the live game: `instance_number(o_enemy)`, `oSys.gold += 1e6`, `global.x`, `obj[2].hp = 1`, `scr_foo(1, "a")`. Also `find`, `objects`, `vars`, `globals`, `hook`/`unhook` for live call logging, and history. Its **Inspector** tab lets you click any instance in the game (`inspect`) and see its object and parents, every variable (edit with any GML expression, freeze, expand arrays and structs), and read-only code: what each of its events calls and which strings it uses, and who calls those. `code <fn>`, `callers <fn>` and `dump` do the same from the console. The **Objects** tab lists every object with its live instance count, pages through an object's instances with the same variable table, and searches every live instance for a variable name (`where <text>` in the console). The **Globals** tab lists, edits and freezes global variables. Freezes hold across any number of instances and globals until lifted (`frozen`, `unfreeze all`) | The whole untyped API; `UI` tabs; pick mode; `Values.Keep`/`Free`; `Code`; `GameDraw.OnGui`; a test host command. [Walkthrough](../walkthroughs/console.md) |
| [ScriptSpy](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ScriptSpy/README.md) | any | Hook any function by name and watch its arguments and results live, with the object each call ran as. Also logs them; `ScriptSpy.txt` lists watches to start with the game. Over the test host: `spy.watch <function> [variable]` (the variable of self is read before and after each call), `spy.read`, `spy.clear`, `spy.unwatch` | `Hooks.Before`/`After` by name; reading `HookCall` arguments without keeping them; test host commands. The tool for [finding hooks](../finding-hooks.md) |
| [SpeedControl](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/SpeedControl/README.md) | any | Run the game faster or slower (`game_set_speed`) | The smallest useful mod: one builtin, one slider |
| [ContentDemo](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/README.md) | any | Runtime content. It loads a spinning coin sprite from a PNG strip and draws it in the game's GUI layer, plays a chime from an OGG file, and can reskin any game sprite by name. Its files ship in `Mods/ContentDemo/assets` | `Content.AddSprite`/`ReplaceSprite`/`AddSound`; `GameDraw.OnGui`; content files next to the dll |
| [DwarfBoost](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/README.md) | Dwarf Eats Mountain | Gold income and unit damage multipliers, resource editor | A game-specific mod on the 2024.14 runtime without a generated interop |
| [StoneshardBoost](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardBoost/README.md) | Stoneshard | XP multiplier (every source goes through `scr_get_XP`), loot multiplier (re-runs `scr_loot`) | `[HookBefore]` with `SetArg`; `CallOriginal` to repeat an effect; one hook covering every source of a value |
| [StoneshardCheats](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/README.md) | Stoneshard | Stats, items (any weapon/armor at any rarity, a stat constructor, every inventory object), potions built from chosen effects, needs/vitals/XP/conditions/psyche, body parts, an enemy roster with Remove, and save import from another machine. Saves are backed up before the first cheat | A large tabbed UI; changing game state and building items through the game's own scripts; `cheats.*` test commands; `GlobalSuppressions.cs` for deliberate lifetime exceptions |
| [Reliquary](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Reliquary/README.md) | Stoneshard | Twenty-two artifacts with a toll (the design's Censer is not in yet), from the Stoneshard Reliquary design (riders, panic buttons, vessels, auras, grafts, scaling passives). The first six, one per family, written only against the generated interop: Stavebound Ember (staff hits add a random element), Gorgoneion (petrify everything in sight, you included), Wolf's Heart (fills with hostiles in sight, spent on Rage), Copper Ring of Faith (a worn ring whose prayer strips effects from foes nearby), Grafted Hand (+40% one-handed damage; the arm never heals again), Pilgrim's Millstone (resistances for dodge and energy). Relics live in vanilla carrier items tagged in their saved data map; hover one in the inventory and press U to activate. Test host: `reliq.*` | A mod written only against the generated interop (`Scripts.scr_atr_calc.After(...)` and similar refs); custom items carried in the game's own item data. The interop-only artifacts mod |
| [FastTravel](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/README.md) | Stoneshard | Fast travel from the world map: open it (M), switch the mode on with its own entry in the map's controls bar or [F] (`toggleKey`), and click any land you have been to or that borders it. Drawn with the game's own board, text and highlight, so it reads as part of the map. Blocked or walled-in arrivals are moved to the nearest reachable spot; only from the open world, never with enemies nearby. Test host: `ft.*` | Typed interop; adding an entry to an existing game UI; acting by calling the game's own scripts; `DsMap`; undoing on failure. [Walkthrough](../walkthroughs/fast-travel.md) |
| [StoneshardTrials](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardTrials/README.md) | Stoneshard | A roguelike loop in the spirit of *Trials of Tav* (proof of concept). The Osbrook tavern, where a new Adventure starts, is the hub; a banner says to leave it, and its street door opens onto a random crypt, catacombs or bastion whose boss still lives, harder every two trials. The boss leaves a Trial Ticket in your bag (a tagged paper map), and using it brings you back to the tavern, where the innkeeper pays crowns for the trial. During the trials the world map stays shut and experience comes only from kills (scaled by a setting). Progress is kept per character. Settings live in the pause menu's MODS window (ModMenu). Test host: `tr.*` | A game-wide system built from many hooks; per-character persistence; `ModSettings`; test host setup commands for a scripted run |
| [ModMenu](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/README.md) | Stoneshard | A **MODS** entry in the pause menu (Esc) that opens a window drawn with the game's own board, buttons and text, where every setting mods register through `ModSettings` can be changed; changes apply and save at once. Test host: `mm.*` | The front end for `ModSettings.Registered`; building a window from the game's own UI pieces |
| [TavernGames](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/TavernGames/README.md) | Stoneshard | Gambling with the locals. Talk to someone in a tavern (innkeepers, drunks, sellswords and the like) and ask for a game - a line added to the game's own conversation - to sit down to **Poker Dice**, **Twenty-One** (dealt from a deck, cards sliding out and turning over), **Thimblerig** (a five-level ladder: more cups, faster swaps, higher stakes), **Arm Wrestling** (a timing skill check against their strength; real Fatigue) or a **Drinking Contest** (every mug is real Drunkenness - confusion, vomiting, passing out) for crowns, on a table drawn with the game's own board, buttons, text and sounds. Opponents bet by temperament (a cautious innkeeper, a reckless drunk) and carry a limited purse that refills over turns. The stake leaves your purse when you commit it and comes back with the winnings; walking away folds. A small framework: `Table` runs the stake, purses, window and input, and a new game is one `MiniGame` class. Test host: `tg.*` | Adding an option to the game's own dialogue; a small reusable framework (`Table`, `MiniGame`); `Content` sprites and the game's own sounds |
| [StoneshardHarness](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/README.md) | Stoneshard | Development tool: plays the game over the test host without screenshots. `hx.*` commands describe the room, the player (vitals, hotbar, free moves), enemies, interactive things, the bag, trade and loot windows, the action log and conversations, with desktop-pixel positions; and act through the game's own scripts and events (move, walk, attack, talk, doors, containers, item and menu actions, waiting, dialogue, window buttons, keys), reporting what each action did. A real click is the fallback (`hx.click`; picking things up uses it). Clients: `tools\stoneshard.ps1`, `tools\stoneshard_harness.py`. See its README | A large `TestHost` surface; finding script paths by watching with ScriptSpy. See [test host](test-host.md#playing-stoneshard-the-harness) |

## Examples

| Mod | Game | What it shows |
|---|---|---|
| [HelloMod](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Examples/HelloMod) | any | The smallest useful mod, game-agnostic: it logs how many functions the game has and draws a small tab |
| [InteropExample](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Examples/InteropExample) | Dwarf Eats Mountain | A mod written against a generated interop: `<InteropGame>Dwarf_Eats_Mountain</InteropGame>` and `[CoreModGame("Dwarf Eats Mountain")]`, hooking `Scripts.dealDamage` and `Objects.oMiner.Step_0`, and reading `Objects.oSys.Vars.gold` |

Neither has a README; the source is short enough to read in one sitting. For the template these
mirror, see [your first mod](../first-mod.md).

## Regression mods

[`managed/Tests/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests) holds mods that
test the loader itself. They are not examples to copy, but each shows how to provoke a specific
behaviour. Deploy them after a risky loader change.

| Mod | Game | What it checks |
|---|---|---|
| [ValueProbe](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests/ValueProbe) | any | Value lifetime: creates about 2,000 strings a frame; the game's private memory must stay flat |
| [StructProbe](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests/StructProbe) | any | A struct kept from C# survives forced garbage collections, and its root is released afterwards; must log `PASSED` |
| [FaultyGuiMod](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests/FaultyGuiMod) | any | A UI fault: it throws inside a tab bar. The loader must unwind the scope and disable only this mod |
| [ObjectTypeProbe](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests/ObjectTypeProbe) | any | Object types: defines a base, a child and a collider type, spawns them, and checks names, parents, instance counts, collision queries and every event (inherited ones too) through the game's builtins; must log `PASSED`. Copy it in again while the game runs: the reloaded probe checks the old instances were destroyed and the same objects came back |
| [WidgetProbe](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests/WidgetProbe) | any | Every UI widget, and scope unwind under faults |
| [XpProbe](https://github.com/Forevka/stoneshard-mod/tree/main/managed/Tests/XpProbe) | Stoneshard | Hook argument handling, with StoneshardBoost at `xpMultiplier` 3: +300, then +600 with `CallOriginal` |

The loader's own analyzers have unit tests in
[`managed/CoreLoader.Analyzers.Tests/`](https://github.com/Forevka/stoneshard-mod/tree/main/managed/CoreLoader.Analyzers.Tests);
see [analyzers](analyzers.md).
