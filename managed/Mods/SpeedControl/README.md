# Speed Control

Runs **any** GameMaker game faster or slower by changing its target frame rate with
`game_set_speed`, the same knob the game itself uses. The chosen speed is re-applied if the game
resets it, which many do on a room change.

## Use

Overlay (**INSERT**), *Speed Control* tab: a slider from 0.25x to 10x, preset buttons, and *Reset*.

## Settings (`Mods\SpeedControl.json`)

| Key | Default | |
|---|---|---|
| `multiplier` | `1` | the game's speed, as a multiple of its own frame rate |
