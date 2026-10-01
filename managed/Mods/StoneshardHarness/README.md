# Stoneshard Harness

Lets a script or an agent play **Stoneshard** without screenshots. Over the test host it describes
what the player sees, as JSON with desktop-pixel positions, and acts through the game's own scripts
and events. A real mouse click is the fallback, used for one action only (picking things up off the
ground). The mod loads only in Stoneshard, and does nothing unless the game runs with the test host
on (`CORELOADER_TEST=1`). It is a development tool, not something to play with.

```powershell
tools\run-game.ps1 -Game Stoneshard -TestHost -Deploy -Mods Console,ScriptSpy,StoneshardHarness -CleanMods
tools\stoneshard.ps1 state            # room, turn, open windows, the player's vitals, gold
tools\stoneshard.ps1 enemies          # what can be fought, nearest first
tools\stoneshard.ps1 attack 401593 20 # clicks it once per turn until it dies (or 20 turns)
tools\stoneshard.ps1 -Json objects    # the raw JSON the mod answers
```

`tools\stoneshard.ps1` prints compact tables, and for an action it waits until the game has settled
and prints what happened. `tools\stoneshard_harness.py` is the same in Python (a module, and a
command that prints JSON). The `hx.*` commands also work straight through `tools\coreloader.ps1`.

## Looking

| Command | Answers |
|---|---|
| `hx.state` | One call per turn: the room, world cell, floor, whether in a dungeon, the turn counter, whether it is the player's turn, open windows (`inventory`, `dialogue`, `trade`, `loot`, `reward`, `pause`, `world-map`, `dead`, `context-menu`...), the player (HP, MP, level, XP, room and grid position, screen pixel), gold, enemies in sight, and whether an action is running |
| `hx.player` | Position (room, grid, screen), vitals (HP, MP, hunger, thirst, fatigue, pain, sanity, morale, intoxication, immunity, vision), the weapon (ranged or not), worn items, the hotbar (key, skill, cooldown, energy, ready) and the 8 moves with what blocks each |
| `hx.enemies [all]` | Hostile or potentially hostile units in sight (`all`: every live one, birds included), nearest first: id, object, name, faction, tier, level, HP, aware, state, distance, position, and what the player can do (`melee`, `shoot`, `approach`) |
| `hx.npcs [all]` | People and animals in sight |
| `hx.objects [reach] [all]` | Doors and stairs (with their exit text), the area's edge (nearest tile only), containers, things on the ground, NPCs and corpses within `reach` tiles (12), with their position and what `hx.interact` does with them |
| `hx.inventory` | The bag and worn items (name, kind, stack, charges, price, slot; screen pixels while it is open), and the other side of an open trade, loot, reward or stash window |
| `hx.log [n]` | The last lines of the action log, as plain text |
| `hx.dialogue` | The open conversation: speaker, line, and the options (number, key, text, enabled, screen) |
| `hx.buttons` | Window buttons on screen (CONFIRM, CANCEL, the main menu's...) |
| `hx.screen <gx> <gy>` | A grid cell as desktop pixels; `hx.screen room <x> <y>` a room position; `hx.screen at <sx> <sy>` the room position and cell under a pixel |

Positions: `x`/`y` are room pixels, `gx`/`gy` the 26-pixel grid cell, `sx`/`sy` the desktop pixel to
click it at (the middle of its sprite). HP is the `HP` variable, which damage lowers at once
(`scr_atr("HP")` lags behind); `maxHp` is the base maximum, and the bar's maximum can be lower.

## Acting

Each action answers `{seq}` at once and runs over the next frames; `hx.result [seq]` answers
`done:false` until the game has settled (the player's turn is back and nothing moves), then what
happened: turns taken, the player's HP, MP, gold and cell before and after, the target's HP before
and after and whether it died, the new action-log lines, open windows and the room. One action runs
at a time; `hx.cancel` stops tracking one.

| Command | Does | Through |
|---|---|---|
| `hx.move <dx> <dy>` | One step | `scr_player_move(x, y)` as the player, what a floor click runs |
| `hx.goto <gx> <gy>` | Walk to a cell, pathfinding as a click does | the same |
| `hx.attack <id> [turns]` | Click the enemy once per turn (a step, a swing or a shot, as the game decides) until it dies; stops when the player drops below a quarter of their health | the enemy's `Mouse_4`, with `scr_mouse_on_unit` answering that enemy while it runs |
| `hx.interact <id>` | Talk to an NPC; go through a door, stairs or the area's edge; open a container; pick something up | an NPC's `Mouse_4`; for doors and containers the floor cursor's part of a click (`o_floor_target.target_id`, then `scr_player_move` as the cursor), after which the game walks there and uses it; picking up walks there by script and then clicks (the fallback) |
| `hx.interact <id> <action>` | A context-menu action on an object (a barrel offers `Open`) | as `hx.use` |
| `hx.use <itemId> [action]` | An item's context-menu action (`Use`/`Eat`, `Equip`, `Drop`, a trader's `Buy`, `Move` to take an item out of an open container...), the first by default | the item's `Mouse_5` (right click) builds the menu; the button's `Mouse_4` presses it |
| `hx.actions <id>` | Reads an item's or object's context menu (opens and closes it); the actions are in the result's `extra` | as above |
| `hx.wait [turns]` | Skip turns | `scr_skip_turn` as the player, one turn at a time, each confirmed by the turn counter |
| `hx.say <n\|key\|text>` | Pick a conversation option (also ones scrolled out of view) | the option button's `Mouse_4` |
| `hx.press <id\|text>` | Press a window button | the user event its `event` variable names (CONFIRM's is 0) |
| `hx.key <key>` | A key (`i`, `esc`, `space`, `1`-`9`, `f1`...); needs no focus | `keyboard_key_press`/`keyboard_key_release` |
| `hx.click <sx> <sy> [right]` | A real mouse click at desktop pixels: the fallback | `SendInput`; refused unless the game owns the foreground window |
| `hx.close` | Close a context menu, else press Esc | |

`hx.press`, `hx.key`, `hx.say` and `hx.click` also work without a player (the title screen, the
intro): a new Adventure can be started and played through to the first turn without a mouse.

## How it was found

Every script path above was found the same way: watch candidate scripts and events with ScriptSpy
(`spy.watch`), do the action once with a real click, read the spy, then replay those calls. The
remarks in `Act.cs`, `Bag.cs`, `Dialogue.cs`, `Gui.cs`, `Grid.cs` and `Screen.cs` record what each
one does. Facts worth knowing:

- **Screen mapping.** The world is drawn through view 0's camera (960x540) onto the application
  surface; the GUI is not the Draw GUI layer but instances in room space under `global.cameraGUI`,
  whose view sits at (-5000, -5000).
- **Walkability** is `astar_get_cell(o_controller.cleangrid, x, y)` (0 walkable, -1 not): it knows the
  void outside a room and furniture such as a counter, which `wallgrid` leaves at 0. `posgrid` names
  whatever a tile holds, including a hanging sign over several tiles, so only units count as blocking.
- **Visibility** is `is_visible()` run as a unit; for anything else,
  `scr_checkFogVisibleCoordinates(gx, gy)`.
- **The current room** cannot be read (GML's `room` is not reachable), so it is tracked from
  `scr_smoothRoomChange` and kept in the global `__harness_room` (cleared at the menus). It is
  unknown (`null`) until the first room change after a new game or a load.
- **Doors, containers and the area's edge** are used when the player arrives next to them; the
  harness starts that walk the way the click does. A quest can refuse: at the start of an Adventure
  Osbrook's edge says "cantExit" until the elder has been met.
- **Running an event by guess can stop the game** with a GML "Code Error". It happened three times
  while this was built: the floor cursor's user events on a corpse, and a loot item's pick-up events
  without the state its click sets up. Hence the pick-up fallback, and why objects without a proven
  path are only walked up to.

## Known gaps

- Picking things up off the ground clicks (the game must be in the foreground).
- Corpses have no action: what an enemy drops lies on the ground around it (`hx.objects`).
- Casting a hotbar skill on a target is not scripted (`hx.key <n>` selects it; aiming needs a click).
- The room is unknown until the first room change after a new game or a load.
- `maxHp` is the base maximum; the health bar's maximum can be lower.
