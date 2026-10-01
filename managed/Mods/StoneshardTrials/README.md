# Stoneshard Trials

A roguelike loop for **Stoneshard**, in the spirit of BG3's *Trials of Tav*. The mod loads only in
that game. This is a proof of concept: the loop works, the rewards between trials come later.

## Use

1. Start a new **Adventure**. It opens in the Osbrook tavern, which is the hub: its innkeeper and
   the townsfolk trade as usual.
2. A banner at the top of the screen reads **Trial N - leave the tavern to start the next trial
   level**.
3. Leave by the tavern's street door. Instead of Osbrook's street, it opens onto a random crypt,
   catacombs or bastion whose boss still lives. Trials 1 and 2 pick tier 1 dungeons, and every two
   trials after that allow one tier more.
4. Kill the dungeon's boss or named miniboss. A **Trial Ticket** goes straight into your bag (if the
   bag is full, it comes as soon as there is room). Each trial pays one ticket, and only in its own
   dungeon.
5. Use the ticket from its context menu (right-click, **Use**). It takes you back to the tavern and
   the trial number goes up.

The ticket only works inside a dungeon. Leaving a dungeon by its stairs puts you in the open world
as usual; walk back into the Osbrook tavern to carry on.

While the mod is on, it changes **every** save, not only a new one: an existing character's Osbrook
tavern door leads into the trials too. Turn it off (`enabled`) to play a normal campaign.

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
- The hub door is told by its exact object, its tag and its `target` being `r_Osbrook`. (The
  street's door into the tavern is an `o_Doors_all_small` tagged `r_tavern01inside1floor`.)
- `o_enemy`'s Destroy event is its death. When the dying unit has `isBoss` or `isMiniboss` and no HP
  left, inside the trial's dungeon, and the trial has not paid out yet, a ticket is given.
- The ticket is a paper map (`o_inv_map_osbrook`) tagged in its saved `data` map, with its own
  `idName` (so the game still offers **Use** once the Osbrook map has been studied), name and text.
  Its Use (`o_inv_map_Other_24`) is skipped. On the next frame the player goes to
  `r_taverninside1floor` with `floor_counter` 0 and `position_tag` `r_OSbrooktavern`, and only once
  that room change has started is the ticket destroyed.

## Settings (`Mods\StoneshardTrials.json`)

| Key | Default | |
|---|---|---|
| `enabled` | `true` | turns the trials off: the door leads to Osbrook again |
| `level` | `1` | the trial number; written by the mod |
| `trial` | empty | the trial under way and whether it is won; written by the mod |

The trial number is kept per installation, not per character: delete the file to start over.

## Test host

| Command | What it does |
|---|---|
| `tr.state` | trial number, current trial, tickets carried, hub or dungeon |
| `tr.pick` | the dungeon the next trial would take |
| `tr.give-ticket` | puts a Trial Ticket in the bag |
| `tr.return` | goes back to the tavern as a used ticket does (no ticket spent, no trial counted) |
| `tr.dungeons`, `tr.where`, `tr.inst`, `tr.call`, `tr.event`, `tr.goto` | looking at the live game |
| `tr.snap` / `tr.diff`, `tr.snapdiff`, `tr.gwatch`, `tr.dumpresult` | which globals a change or an event touches |

## Building

Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`): run the game
once with Lodestone, or `tools\setup-dev.ps1`, before building it.
