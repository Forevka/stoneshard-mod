# Mod Settings

A **MODS** entry in **Stoneshard**'s pause menu (Esc). It opens a window drawn with the game's own
board, buttons and text, listing every setting that mods offer through CoreLoader's `ModSettings`.
The mod loads only in Stoneshard.

## Use

1. Press **Esc** in game and click **MODS** (below SAVE & EXIT).
2. Pick a mod on the left. Its settings are on the right:
   - toggles show **ON**/**OFF**;
   - numbers and choices have **<** and **>**. A right click on **>** steps back.
3. Changes apply and save at once. **DEFAULTS** resets the mod shown. **CLOSE** or **Esc** returns
   to the game.

While the window is open, the keyboard and mouse belong to it.

## For mod authors

Register settings in `OnInitialize`. They are bound to keys of your mod's `Config`:

```csharp
var xp = ModSettings.Slider(this, "xpScale", "Kill experience", "Experience from slain enemies.",
                            1, 0, 3, 0.25, v => $"x{v:0.##}");
ModSettings.Toggle(this, "enabled", "Trials", "Off: the mod stands aside.", true);
ModSettings.Choice(this, "mode", "Mode", "", 0, new[] { "Easy", "Normal", "Hard" });
double now = xp.GetNumber();   // read when you need it; the window writes the config
```

A mod that registers settings needs nothing else from this one. Without Mod Settings installed,
its settings still work from its `Mods\<Mod>.json`.

## How it works

- The pause menu is an `o_close_panel`. Its Create event makes the buttons with
  `scr_guiCreateInteractive(panel, o_ingame_menu_button, depth, 480, y)`, 28 apart, each with a
  `text` and an `event`.
- **MODS** is one more of those, made right after the menu's own buttons. Its event is 0
  (CONTINUE, which closes the menu), and the window opens after it.
- While the window is open, the keyboard is cleared at `o_controller`'s Begin Step, and clicks are
  taken through the loader's pick mode. This is the same as TavernGames' table.

## Test host

| Command | What it does |
|---|---|
| `mm.state` | whether the window is open, the mod shown, every registered setting and its value |
| `mm.open` / `mm.close` | opens or closes the window |
| `mm.click <id> [right]` | as a click: `mod:<name>`, `dec:<key>`, `inc:<key>`, `toggle:<key>`, `defaults`, `close`, `prev`, `next` |

## Building

Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`).
