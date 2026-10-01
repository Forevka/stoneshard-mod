# Stoneshard Boost

Experience and loot multipliers for **Stoneshard**, built on hooks. The mod loads only in that game.

- **XP multiplier.** Every source of experience (kills, books, traps, quest rewards, discovering
  places) goes through `scr_get_XP(amount)`. The mod scales that amount before the script runs, so
  all of them are scaled, including the "+N XP" in the action log.
- **Loot multiplier.** `scr_loot` places one item per call, so more loot means running it again: the
  mod re-runs the game's own call. A fractional multiplier such as 1.5 adds the extra roll half the
  time.

## Use

Overlay (**INSERT**), *Stoneshard Boost* tab: the two multipliers.

## Settings (`Mods\StoneshardBoost.json`)

| Key | Default | |
|---|---|---|
| `xpMultiplier` | `1` | experience multiplier |
| `lootMultiplier` | `1` | loot rolls per drop |
