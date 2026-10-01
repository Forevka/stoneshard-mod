# Content Demo

Shows runtime content in **any** YYC GameMaker game, loaded from files: an example to copy from.

- A **spinning coin**, from the PNG strip `assets/coin.png` (8 frames), drawn in the game's own GUI
  layer, top right.
- A **chime**, from `assets/chime.ogg`, played from the overlay.
- A **reskin**: any of the game's sprites can be replaced by the coin, by name. Unloading the mod
  puts the original back.

## Use

Overlay (**INSERT**), *Content Demo* tab: show or hide the coin, play the chime, and type a sprite
name to reskin (or restore it).

## Settings (`Mods\ContentDemo.json`)

| Key | Default | |
|---|---|---|
| `badge` | `true` | draw the spinning coin |
| `reskin` | empty | a game sprite to reskin with the coin at start |

## Files

`assets/coin.png` and `assets/chime.ogg` ship in `Mods/ContentDemo/assets`.
