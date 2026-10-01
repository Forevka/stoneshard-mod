# Fast Travel

Fast travel from **Stoneshard**'s world map. The mod loads only in that game.

## Use

1. Open the world map (**M**).
2. Switch the mode on with its own entry in the map's controls bar, **[F] - Fast Travel**, or the key
   (`toggleKey`).
3. Click a place: any land you have been to, or right next to where you have been. A banner in the
   game's own style says what a click on the hovered cell would do.

Travel works the way a border crossing does. It starts only from the open world, not inside a
building or dungeon, and is refused with enemies nearby. A blocked or walled-in arrival is moved to
the nearest free spot joined to the room's edges. Closing the map turns the mode off.

## Settings (`Mods\FastTravel.json`)

| Key | Default | |
|---|---|---|
| `toggleKey` | `F` | the key that switches the mode on the map |

## Test host

| Command | What it does |
|---|---|
| `ft.state` | map open, mode, travelling, the player's cell, the hovered cell, visited cells |
| `ft.mode [on\|off]` | reads or sets the mode |
| `ft.judge <x> <y>` | whether that cell can be travelled to |
| `ft.travel <x> <y> [arriveX arriveY]` | travels as a click would, with the same checks |

## Building

Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`): run the game
once with Lodestone, or `tools\setup-dev.ps1`, before building it.
