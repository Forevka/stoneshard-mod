# Stoneshard Trials

A roguelike loop for **Stoneshard**, in the spirit of BG3's *Trials of Tav*. The mod loads only in
that game. Still young: the loop, rewards in crowns, per-character progress and a progression that
matches dungeons to the character work, in finite or endless runs, with a difficulty setting.

## Use

1. Start a new **Adventure**. It opens in the Osbrook tavern, which is the hub: its innkeeper and
   the townsfolk trade as usual.
2. A banner at the top of the screen (below your status effects) reads **Trial N - leave the tavern
   to start the next trial level**.
3. Leave by the tavern's street door. Instead of Osbrook's street, it opens onto a crypt, catacombs
   or bastion whose boss still lives, as dangerous as your character calls for (see *Progression*).
   The banner in the tavern tells you the danger that awaits.
4. Kill the dungeon's boss or named miniboss. A **Trial Ticket** goes straight into your bag (if the
   bag is full, it comes once you make room; it is retried when the bag changes, and every half
   minute). Each trial pays one ticket, and only in its own dungeon.
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
as usual; walk back into the Osbrook tavern to carry on. There the trial is settled as if you had
used the ticket: a won trial is paid, one not won is given up, and any ticket you carry is taken
back. (A ticket used in a trial not won brings you back too, and gives that trial up.) Once every
master is dead, a finite run completes as soon as you are back in the tavern.

While the mod is on, it changes **every** save, not only a new one: an existing character's Osbrook
tavern door leads into the trials too. Turn it off (`enabled`) to play a normal campaign.

## Boons and traders

**Cards.** When you come back to the tavern after a won trial, a window lays out three cards (four
after a Second Look). Take one, or turn them all down. The offer is part of the run, so a reload
offers the same cards. A card is a reward, and a strong reward comes with a cost of about its weight,
shown in red; numbers grow with the won trial's danger tier (1-5). The set is the research
catalogue's (`.omc/research/trials-rewards-catalogue.md`) as approved: 51 of 53 entries (raw wound
statuses as a reward rejected; Stone Skin dropped because it roots the player).

Rewards:
- **Crowns and points:** Heavy Purse (500 crowns per level, x1.0-1.8 by tier), Hard-Won Skill
  (ability points), Tempered Body (attribute points), Trained Body (+1-3 to one attribute), Light
  Feet (evasion), Deep Reserves (energy), Hawk Eyes (vision), Streetwise (savvy and trap avoidance),
  Quick Study (+10-20% experience).
- **Lasting statuses:** Vampirism, Night Eyes, Clear Vision, Sturdiness, Blessing, the four
  Training regimens, Elusiveness, Exceptional Precision, Battle Rage, Energy Drain, Adrenaline,
  Unholy Pact; and swaps that trade one stat for another: Glass Cannon, Bulwark, Troll Blood, Quick
  Hands. Several carry their own cost in their numbers (Elusiveness costs health, Battle Rage
  raises damage taken).
- **Another Hero's Way** (tier 3+): a perk of another origin.
- **Things:** Alchemist's Gift (a potion of healing and regeneration, more at higher tiers, given
  identified), Forbidden Library (treatises of two trees; always closes a third), Tome of
  Experience (half a level), Armoury Drop (an enchanted, rare or legendary piece of the trial's
  tier, one higher from tier 4), Cursed Heirloom (a piece a tier higher, cursed), Field Kit
  (supplies).
- **The run:** Merchant's Favour (the traders' next two stocks a tier higher and rarer), Second
  Look (a fourth card next time), Blood Money (the next trial is half a tier or a tier harder and
  pays x2 or x2.5).

Costs: Tithe (25-50% of your crowns), Curse of Decay, Vampiric Corruption (for good), Enervation,
Weakness, Gluttony, Heart of Darkness, Eternal hangover, Mark of the Feast, Blood Hex, Pestilence,
Coughing, Weariness (fatigue gain), an Old Wound, Shattered Gear (a worn piece named on the card),
Forbidden Art (a skill tree closed for the run: greyed in the Abilities window, its skills cannot be
learned, its treatises make no sense) and Attribute Loss. Timed costs last some trials won (named
on the card) and end on their own, or when the run ends.

A status a boon keeps is unique to it: no two cards of a run, rewards or costs, use the same one.
Boon statuses are kept for good and a timed cost for its term: a cure or a dispel does not lift
them (they are back within a second). A custom status (Elusiveness, the swaps...) sits on one of the
game's own statuses, so if the game gives that status too, the boon's numbers replace it. Blood
Money's extra danger stops at the top of tier 5; its pay does not.

**Traders.** From the first won trial on, two traders stand by the innkeeper's counter: a smith
(weapons, armour, shields, tools) and a merchant (potions, medicine, scrolls, treatises, jewellery,
tools, valuables). They are real NPCs: talk to them and pick "Have anything for sale?". Nothing is
locked; crowns are the only price. Their stock is made for the last won trial's tier (the smith's
reaches one tier higher) after trials 1, 3, 5... and kept for two trials, never restocked by the
game's own timers. It is part of the world save, so it rolls back with the run.

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

The **difficulty** setting shifts the wanted danger: Easy -0.5, Normal 0, Hard +0.5, Brutal +1.

**Finite runs** (the default) take the untouched dungeon nearest that tier (the lower one on a tie),
and a different kind from the last trial when it can. The tavern banner shows the danger the door will really lead
to. A world holds about 19 dungeons. Once every master is dead, the run is complete for good: the
tavern door leads to Osbrook, the world map opens, and experience comes from everything again.

**Endless runs** (setting *Run*) take an untouched dungeon at exactly that tier if one is left;
otherwise they remake one at that tier, a won one first, and a different kind and dungeon from the
last trial when they can:
- its saved floors are forgotten, so it is generated afresh, full of enemies;
- its tier, the levels the game recommends for it, and its master are rewritten: a master of that
  kind and tier, from the game's own tables (a crypt at tier 3 gets an Undertaker, a Ritualist or an
  Armored Husk).

Only dungeons that began with one floor are remade, and they stay one floor. A cell's layout keeps
its master on the floor it was made with, so a remade two-floor dungeon would have no master on
floor 1. Crypts are never made tier 5, because the game has no tier-5 crypt layouts. When nothing
can be remade at the wanted tier, the nearest tier that can is used, and then any untouched dungeon.
An endless run never completes. A finished finite run takes the trials up again if *Run* is switched
to Endless.

### Harder trials

When the wanted danger runs past the dungeon's tier by more than the 0.4 the rounding allows, the
trial adds to it on arrival. This happens when a finite run has no dungeon of the wanted tier left,
or on Hard and Brutal:
- **extra enemies**: none while the wanted danger is within 0.4 of the dungeon's tier; past that,
  one, and one more per further 0.3 (up to 4); plus 1 on Hard and 2 on Brutal (6 at most). A fresh
  character's first trial on Normal adds nothing. They are copies of the dungeon's own toughest kind
  of enemy, spawned beside them, and they notice the player and fight like the natives;
- **an elite master** once the danger runs a whole tier past, and always on Brutal: half again its
  health, and "Elite" before its name (the master itself; a miniboss only where there is none). On a
  natural two-floor dungeon it becomes elite when the player reaches its floor. The run remembers it,
  so after a save and load the master is made elite again. The game loads it with its plain maximum
  health: an unhurt master gets the elite's full health back, and a wounded one keeps no more health
  than that plain maximum.

The action log says what was added.

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
  `r_taverninside1floor` with `floor_counter` 0 and `position_tag` `r_OSbrooktavern`. Only once that
  room change has started is the trial settled: paid if won, and every ticket the game has loaded is
  destroyed. Walking back into the tavern (world cell 32,10 with its street door present) settles it
  the same way.

- The world map opens through `scr_globalmapCreate`. The HUD's map button (`o_gui_button_map`, also
  the M key) is refused there and receives noone. A paper map's Use is skipped before it runs, since
  its own code expects the map it asked for.
- Every source of experience calls `scr_get_XP(amount)`. A kill is the call from `o_enemy`'s
  Destroy, with the dying enemy as self (and only when the player landed the blow). Every other call
  is skipped during the trials.
- A unit's health is rebuilt every turn: `scr_atr_calc`, run as the unit, resets `max_hp` from its
  base data and clamps `HP` to it. So around that call, for the elite only, its `HP` is noted, and
  afterwards `max_hp` is scaled and the `HP` put back. Its unbuffed maximum is `max_hp_cosnt`.
- A full bag makes `scr_inventory_add_item` throw the new map on the ground (an
  `o_loot_map_osbrook`) and return noone; the mod takes that map away again.
- The status effect icons are children of one `o_modificatorsMenu` at the top centre, in room space
  under `global.cameraGUI` (a 960x540 view). Its `guiHeight` grows by a row as the icons wrap, and
  the banner is drawn below it.
- Cards act through the game's own paths. Attribute and ability points are the `AP` and `SP` keys
  of the character's data (their names are swapped against the UI: `AP` is shown as SP), and
  bonus keys such as `bEVS` and `bMp` add to the derived stats; the game saves all of them. A
  lasting status is the buff instance, owned by the player, in its `buffs` list, with its own
  Alarm 2 run and then 99999 turns (some buffs set their own duration in that alarm). The game
  saves it but refills its numbers from the buff's defaults, and `night_vision` is a variable of
  the player instance; both are written again every second, which covers loads and the new player
  instance of every room. Potions are built as StoneshardCheats builds them (rewriting a fresh
  bottle's `atrdlist` in its Alarm 0). A closed tree is `disabled` on its `o_skill_category_*`,
  plus Before hooks on the skill icon's learn event (`o_skill_ico` Other_10) and on
  `scr_skill_branch_study`.
- The traders are `o_npc_smith_osbrook` and `o_npc_merchant_mannshire` (Osbrook's own merchant
  opens with his caravan-quest introduction), made with `instance_create_depth` and given their
  own `id_name`. Their stock is the entry of that name in the `npc_data` map of the kind's home
  tile (Osbrook 32,10, Mannshire 26,23); the trade window rolls it with `is_restock` from the
  trader's `Equipment_Tier_*`, rarity chances, `Stock_Size` and `selling_loot_category`. The roll
  runs the trader's own User Event 9, which rewrites that list, so an After hook on it puts the
  merchant's range back. Town NPCs walk their day's schedule, so the traders are put back on their
  spot, idle, every second the player is in the tavern. `scr_npc_restock` is skipped for them.
  Details: `.omc/research/trials-merchants.md`.
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
| `difficulty` | `1` | **Difficulty**: 0 Easy, 1 Normal, 2 Hard, 3 Brutal |
| `runMode` | `0` | **Run**: 0 Finite, 1 Endless |

The run lives in the save, so the files under `Mods\StoneshardTrials\characters` are for reading only.

## Test host

| Command | What it does |
|---|---|
| `tr.state` | the character's run (id, trial number, trials won, crowns earned), the current trial, tickets carried, hub or dungeon |
| `tr.pick` | the dungeon the next trial would take (ties are broken per run and trial, so the door agrees) |
| `tr.assess [trial]` | level, gear score, power and the tier that trial would take |
| `tr.next <x> <y>` | the next trial takes that dungeon |
| `tr.dkeys <x> <y>` / `tr.dset <x> <y> <key> <value>` | read / write a dungeon's data |
| `tr.rooms [filter]` / `tr.roomkeys <x_y>` | the saved locations, and the rooms one holds |
| `tr.gear` | worn items with their tier and quality |
| `tr.offer` | the cards on the table, whether their window is open, and the boons taken |
| `tr.deal [tier] [card ids...]` | puts cards on the table now (they show in the tavern) |
| `tr.take <n>` / `tr.discard` | takes the n-th card (0-based) / turns them all down |
| `tr.traders [restock]` | the tavern traders (where, tiers, stock rows); `restock` remakes their stock |
| `tr.give-ticket` | puts a Trial Ticket in the bag |
| `tr.return` | goes back to the tavern as a used ticket does (no ticket spent) |
| `tr.dungeons`, `tr.where`, `tr.inst`, `tr.call`, `tr.event`, `tr.goto`, `tr.tp` | looking at the live game |
| `tr.snap` / `tr.diff`, `tr.snapdiff`, `tr.gwatch`, `tr.dumpresult` | which globals a change or an event touches |

## Testing with agents

Trials can be played end to end by an agent, without screenshots, through the
**StoneshardHarness** mod (`tools\stoneshard.ps1`). The `tr.*` commands above set up situations.
For example, `tr.dset` with `boss_alive 0` on every dungeon makes the next endless trial remake one.

## Building

Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`): run the game
once with Lodestone, or `tools\setup-dev.ps1`, before building it.
