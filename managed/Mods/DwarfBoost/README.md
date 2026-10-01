# Dwarf Boost

Tweaks for **Dwarf Eats Mountain**. The mod loads only in that game.

- **Gold income multiplier.** Each frame's rise in gold (on the game's `oSys` controller) is scaled.
  Spending is left alone, so purchases cost what they say.
- **Unit damage multiplier.** Every dwarf unit (all children of `parDwarf`) has its `damage` scaled
  on top of what the game last computed. When an upgrade makes the game write a new value, that
  becomes the new base, so the bonus never compounds.
- **Resource editor** for gold, mithril and soul.

## Use

Overlay (**INSERT**), *Dwarf Boost* tab: sliders for the two multipliers (1x to 20x), and buttons that add to or multiply each resource.

## Settings (`Mods\DwarfBoost.json`)

| Key | Default | |
|---|---|---|
| `goldMultiplier` | `1` | gold income multiplier |
| `damageMultiplier` | `1` | unit damage multiplier |
