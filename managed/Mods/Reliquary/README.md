# Reliquary

Artifacts that ask for something back, for **Stoneshard**. The mod loads only in that game. Each
relic gives a strong boon and takes a toll, from the Stoneshard Reliquary design's six families:
riders, panic buttons, vessels, auras, grafts and scaling passives.

## Relics

Twenty-two are in:

- **Stavebound Ember**, **Gorgoneion**, **Wolf's Heart**, **Copper Ring of Faith**, **Grafted Hand of
  the Hanged Man**, **Pilgrim's Millstone**;
- **Faceless Mirror**, **Split Quiver**, **Cinder Rosary**, **Echoing Bell**, **Debtor's Knot**,
  **Weeping Candle**;
- **Pallbearer's Coin**, **Vessel of Borrowed Years**, **Reliquary of Saint Mardun**,
  **Usurer's Scale**, **Sundered Gate**;
- **Lodestone Idol**, **Surveyor's Chain**, **Iron Lung**, **Oath-Stone of the Deep Road**,
  **The Sated Worm**.

The design's twenty-third, the **Censer of the Drowned Choir**, is not registered yet: none of the
game's statuses it tried actually stops abilities.

## Use

- A relic is a vanilla item (a valuable, or a real ring) tagged in its saved data, with its own icon
  and a tooltip that gives its boon, its toll and a live status line.
- Relics work from the **bag**; the Copper Ring works when **worn**. Only the first copy of each
  counts.
- **Activate** one by hovering it in the inventory and pressing the activate key (default **U**).
- The overlay (**INSERT**), *Reliquary* tab lists every relic with a *Give* button and, for carried
  ones, *Activate*.
- Effects show in the game's own action log.

## Settings (`Mods\Reliquary.json`)

| Key | Default | |
|---|---|---|
| `activateKey` | `U` | the key that activates the hovered relic |

## Test host

| Command | What it does |
|---|---|
| `reliq.list` | every relic `{id, name, family}` |
| `reliq.give <id>` | puts a new relic in the bag |
| `reliq.state` | turns seen, the wielded weapon, effects on the character, every relic item |
| `reliq.activate <id>` | activates the carried relic |
| `reliq.hostiles` | hostiles in the player's vision |

A few relics add their own (`reliq.cinder.*`, `reliq.bell.*`, `reliq.lode.*`, `reliq.coin.*`,
`reliq.gate.*`); `list-commands` shows them.

## Building

Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`): run the game
once with Lodestone, or `tools\setup-dev.ps1`, before building it. Placeholder icons ship in
`Mods/Reliquary/assets`.
