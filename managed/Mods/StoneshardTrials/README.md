# Stoneshard Trials

A roguelike loop for **Stoneshard**, in the spirit of BG3's *Trials of Tav*. The mod loads only in
that game. Still young: the loop, rewards in crowns, per-character progress and a progression that
matches dungeons to the character work. A run is finite for now (an endless mode comes next).

## Use

1. Start a new **Adventure**. It opens in the Osbrook tavern, which is the hub: its innkeeper and
   the townsfolk trade as usual.
2. A banner at the top of the screen reads **Trial N - leave the tavern to start the next trial
   level**.
3. Leave by the tavern's street door. Instead of Osbrook's street, it opens onto a crypt, catacombs
   or bastion whose boss still lives, as dangerous as your character calls for (see *Progression*).
   The banner in the tavern tells you the danger that awaits.
4. Kill the dungeon's boss or named miniboss. A **Trial Ticket** goes straight into your bag (if the
   bag is full, it comes as soon as there is room). Each trial pays one ticket, and only in its own
   dungeon.
5. Use the ticket from its context menu (right-click, **Use**). It takes you back to the tavern, the
   innkeeper pays you for the trial, and the trial number goes up.

Rules during the trials:
- **Crowns are the reward.** A trial won pays 150 crowns for a tier 1 dungeon and 100 more per tier
  above that. Each trial already behind you adds a tenth. Spend them in the tavern and build the
  character however you like.
- **Experience comes only from kills.** Finding places, quests, books, crafting and traps give none.
  Kill experience is scaled by the *Kill experience* setting, which is meant to become the difficulty
  selector.
- **The world map stays shut** (the M key, the HUD button and paper maps). The tavern door is the
  only road, and travel mods such as FastTravel cannot skip the trials.
- **Progress is per character, and part of its save.** The run travels on the character, so loading
  an older save brings back that save's trial: a boss killed after the save is alive again, and
  crowns paid after it are gone along with the trial they paid for. A readable copy of each run is
  written to `Mods\StoneshardTrials\characters\<id>.json`.

The ticket only works inside a dungeon. Leaving a dungeon by its stairs puts you in the open world
as usual; walk back into the Osbrook tavern to carry on.

While the mod is on, it changes **every** save, not only a new one: an existing character's Osbrook
tavern door leads into the trials too. Turn it off (`enabled`) to play a normal campaign.

## Progression

A dungeon's danger is its tier (1-5, the game's skulls). The tier sets its enemies' tiers (1, 1-2,
2-3, 3-4, 4-5) and the character level the game recommends for it (1-5, 5-10, 10-15, 15-20).
Each trial asks for a tier:

1. **Level tier** = 1 + (level - 1) / 5, so levels 1, 6, 11 and 16 are tiers 1, 2, 3 and 4.
2. **Gear score**: the average of your five best worn items. An item scores its tier, plus 0.25
   (enchanted or cursed), 0.5 (rare, unique or treasure) or 0.75 (legendary). Missing items count 0.
3. **Power** = half level tier + half gear score.
4. **Wanted danger** = power + a quarter tier for every trial behind you, up to one. It is never more
   than one tier above your power, and never below 1 + (trial - 1) / 6.
5. **Tier**: rounded up once the fraction passes 0.4. Trial 1 with a fresh character is tier 1; level
   3 with two tier-1 and two tier-2 items on trial 3 is tier 2.

The trial takes the untouched dungeon nearest that tier (the lower one on a tie), and a different
kind from the last trial when it can. The tavern banner shows the danger the door will really lead
to. A world holds about 19 dungeons. Once every master is dead, the run is complete for good: the
tavern door leads to Osbrook, the world map opens, and experience comes from everything again.

## How it works

Everything was established on the running game with the test host and Script Spy:

- A door leaves in its alarm 7. It sets `global.floor_counter` (the next room's floor:
  `locationFloor` plus its own `dungeon_level_incr`) and `global.position_tag` (which door to arrive
  at), then calls `scr_smoothRoomChange(target, ...)` as itself. A floor above 0 is a dungeon.
- The tavern's street door (`o_Doors_all_small_exit`, `position_tag` `r_OSbrooktavern`) gets
  `dungeon_level_incr` 1 before its alarm reads it. The room change is then pointed at
  `r_dungeon_generate`, with `playerGridX/Y` set to the chosen dungeon's world cell. That is exactly
  what a dungeon's own entrance does.
- Dungeons come from `scr_glmap_getLocationBySubType`, their tier from `scr_globaldungeonTierGet`, and
  whether their boss lives from `scr_globaltile_dungeon_get("boss_alive", x, y)`.
- Arriving: a door's alarm also sets `global.position_tag`, and the new room puts the player at the
  door carrying that tag. A dungeon's entrance sets "NA", the tag its stairs up carry. So the
  redirected tavern door sets "NA" too. Even so, the first trial after a new game or a load put the
  player at the room's default spot (481, 507), inside a wall in the dark. So once the dungeon has
  loaded, a player more than 3 cells from its stairs up (`o_dungeon_stairs_up`) is moved to the free
  cell nearest them (`Arrival.cs`).
- The hub door is told by its exact object, its tag and its `target` being `r_Osbrook`. (The
  street's door into the tavern is an `o_Doors_all_small` tagged `r_tavern01inside1floor`.)
- `o_enemy`'s Destroy event is its death. When the dying unit has `isBoss` or `isMiniboss` and no HP
  left, inside the trial's dungeon, and the trial has not paid out yet, a ticket is given.
- The ticket is a paper map (`o_inv_map_osbrook`) tagged in its saved `data` map, with its own
  `idName` (so the game still offers **Use** once the Osbrook map has been studied), name and text.
  Its Use (`o_inv_map_Other_24`) is skipped. On the next frame the player goes to
  `r_taverninside1floor` with `floor_counter` 0 and `position_tag` `r_OSbrooktavern`, and only once
  that room change has started is the ticket destroyed.

- The world map opens through `scr_globalmapCreate`. The HUD's map button (`o_gui_button_map`, also
  the M key) is refused there and receives noone. A paper map's Use is skipped before it runs, since
  its own code expects the map it asked for.
- Every source of experience calls `scr_get_XP(amount)`. A kill is the call from `o_enemy`'s
  Destroy, with the dying enemy as self (and only when the player landed the blow). Every other call
  is skipped during the trials.
- The run is a player attribute, `trialsRun`, holding it as JSON (`scr_atr_set_simple`). The game
  saves it with the character. It is written only once the character's first trial starts, and it is
  re-read whenever it changes, for instance when a load restores it.

## Settings

In the pause menu, **MODS** (needs the ModMenu mod), or `Mods\StoneshardTrials.json`:

| Key | Default | |
|---|---|---|
| `enabled` | `true` | **Trials**: off turns the door, the map and experience back to the normal game |
| `xpScale` | `1` | **Kill experience**: x0 to x3 |
| `goldScale` | `1` | **Trial reward**: x0 to x3 |

The run lives in the save, so the files under `Mods\StoneshardTrials\characters` are for reading only.

## Test host

| Command | What it does |
|---|---|
| `tr.state` | the character's run (id, trial number, trials won, crowns earned), the current trial, tickets carried, hub or dungeon |
| `tr.pick` | the dungeon the next trial would take |
| `tr.assess [trial]` | level, gear score, power and the tier that trial would take |
| `tr.next <x> <y>` | the next trial takes that dungeon |
| `tr.dkeys <x> <y>` / `tr.dset <x> <y> <key> <value>` | read / write a dungeon's data |
| `tr.gear` | worn items with their tier and quality |
| `tr.give-ticket` | puts a Trial Ticket in the bag |
| `tr.return` | goes back to the tavern as a used ticket does (no ticket spent) |
| `tr.dungeons`, `tr.where`, `tr.inst`, `tr.call`, `tr.event`, `tr.goto`, `tr.tp` | looking at the live game |
| `tr.snap` / `tr.diff`, `tr.snapdiff`, `tr.gwatch`, `tr.dumpresult` | which globals a change or an event touches |

## Building

Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`): run the game
once with Lodestone, or `tools\setup-dev.ps1`, before building it.
