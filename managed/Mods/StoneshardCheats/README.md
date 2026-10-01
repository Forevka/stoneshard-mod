# Stoneshard Cheats

Cheats and character tools for **Stoneshard**. The mod loads only in that game. Every action runs
as the player, through the game's own scripts, and the **saves are backed up before the first
cheat** of a session.

## Tabs

Overlay (**INSERT**), *Stoneshard Cheats*:

| Tab | What it does |
|---|---|
| **Stats** | attributes, health, gold and XP |
| **Items** | any weapon or armour at any rarity, a stat constructor, every inventory object |
| **Potions** | potions built from chosen effects |
| **Character** | needs, vitals, XP, conditions and psyche (sanity, morale) |
| **Body** | the condition of each body part |
| **Enemies** | the enemies on the floor, with a Remove button |
| **Saves** | import a save made on another machine |

## Test host

`cheats.*` commands, with read-backs, used by `tools\smoke-stoneshard.ps1` (a loaded save needed):

| Command | |
|---|---|
| `cheats.player` | the player's basics |
| `cheats.atr-get` / `cheats.atr-set` | read or write an attribute |
| `cheats.hp`, `cheats.gold-get`, `cheats.gold`, `cheats.xp` | health, gold, experience |
| `cheats.conditions`, `cheats.condition`, `cheats.buffs` | list or apply conditions and effects |
| `cheats.psy-get` / `cheats.psy-set` | psyche |
| `cheats.body` / `cheats.body-set` | body parts |
| `cheats.items`, `cheats.item-give`, `cheats.item-inventory`, `cheats.object-give` | items |
| `cheats.potion-effects`, `cheats.potion`, `cheats.potion-result` | potions |
| `cheats.enemies`, `cheats.enemy-remove` | enemies |

`list-commands` over the test host prints each one's arguments.

## Notes

Never let the game save after a test run you did not mean to keep: back up first with
`tools\game-saves.ps1`.
