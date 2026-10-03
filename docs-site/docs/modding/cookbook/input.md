---
title: Input
description: Recipes for reading keys and the mouse, taking a click the game never sees, and keeping keyboard input away from the game while your own window is open.
---

There are two ways to see input. You can ask the game, with its own `keyboard_check_*` and
`mouse_check_*` builtins, which is what the game's code sees too. Or you can take it **before** the
game, with the loader's pick mode, which swallows a click so the game never reacts to it. Use the first
for hotkeys and the second for "click something" tools and for windows that must own the mouse.

## Detect a key press {#key-press}

Call `keyboard_check_pressed` from `OnUpdate`, which runs once a frame, so a press fires once. `ord` turns
a character into the key code the builtin expects. FastTravel's hotkey, and its mouse click, are checked
like this:

```csharp
if (Builtins.keyboard_check_pressed(Builtins.ord(_key)).AsBool) SetMode(!_mode);
if (!Builtins.mouse_check_button_pressed(1).AsBool) return;
double mx = Builtins.device_mouse_x_to_gui(0).AsReal, my = Builtins.device_mouse_y_to_gui(0).AsReal;
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L67-L69)</small>

The key itself is a setting. The mod reads it once, from its config, and falls back to `F` if the value is
not a single character:

```csharp
_key = Config.Get("toggleKey", "F").Trim().ToUpperInvariant();
if (_key.Length != 1) _key = "F";
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L33-L34)</small>

Without the interop, use `Game.CallBuiltin("keyboard_check_pressed", Game.CallBuiltin("ord", "F"))`. Key
codes are GameMaker's: ModMenu tests `keyboard_check_pressed(27)` for Esc and TavernGames `32` for space.

Gotchas:

- `keyboard_check_pressed` is true for the one frame the key went down. `keyboard_check` is true while
  it is held, so use that for movement and `_pressed` for toggles.
- The game sees the same key. If your hotkey is already bound in the game, both react. Pick an unused key
  and make it configurable, as FastTravel does.
- While the overlay wants the keyboard (a text box has focus), it does not reach the game, and so does not
  reach your `keyboard_check_*` call either. See [The overlay and your input](#overlay-input).
- INSERT is the loader's own key: it toggles the overlay and the game never sees it.

## Read the mouse in GUI coordinates {#mouse-gui}

The game has several coordinate spaces, and the one you want depends on what you draw or hit-test.
`device_mouse_x_to_gui(0)` and `device_mouse_y_to_gui(0)` give the mouse in **GUI** pixels, the same
space a `GameDraw.OnGui` handler draws in, so a hit test against something you drew compares like with
like. `device_mouse_x(0)` and `device_mouse_y(0)` give **room** coordinates. FastTravel's draw handler
reads the GUI position every frame to find the map cell under the mouse:

```csharp
double mx = Builtins.device_mouse_x_to_gui(0).AsReal, my = Builtins.device_mouse_y_to_gui(0).AsReal;
var cell = _ui.OnPanel(mx, my) ? null : WorldMap.CellAt(mx, my);
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L140-L141)</small>

Gotchas:

- Letterboxing, scaling and views are the game's, so convert with the game's own builtins rather than
  dividing by the window size yourself.
- A click on one of the game's own panels is also a click on whatever is under it. FastTravel checks the
  panels first (`OnPanel`) and only treats a click as a map click if it is not on one:
  [FastTravelMod.cs lines 75-76](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L75-L76).

## Take a click the game never sees {#take-a-click}

Use pick mode: `Input.ArmPick()`, then poll `Input.TryTakePick(out var click)` each frame. The next left or
right click **outside the overlay's windows** is swallowed (the press and its release), so the game never
reacts to it, and is reported to you once. The Console's inspector uses it to select an instance:

```csharp
public void Pick()
{
    Input.ArmPick();
    _message = "click an instance in the game (right click cancels)";
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L46-L50)</small>

```csharp
if (Input.TryTakePick(out var click))
{
    if (click.RightButton) _message = "pick cancelled";
    else TakePick(click.RoomX, click.RoomY);
}
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L91-L95)</small>

The `PickClick` carries the click's window pixels (`X`, `Y`, with the client `Width` and `Height`), whether
it was `RightButton`, and `RoomX`/`RoomY`: where the game's own mouse was in the room, read as the click
was taken. Right-click as "cancel" is a convention, not a rule.

A **modal** window re-arms the pick after every click so no click ever reaches the game, even one another
mod armed a pick for. ModMenu's window does this in `OnUpdate`:

```csharp
// Modal: while the window is up every click is its own, even one another
// mod armed a pick for (that pick is taken over and lost).
if (!Input.IsPicking) Input.ArmPick();
if (!Input.TryTakePick(out var click)) return;
Input.ArmPick();
```

<small>Source: [managed/Mods/ModMenu/ModMenuMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ModMenu/ModMenuMod.cs#L146-L150)</small>

Gotchas:

- **One pick at a time.** A pick is owned by the mod that armed it. Another mod arming takes it over, and
  `TryTakePick` then returns `false` for you. `Input.IsPicking` tells you whether it is still yours.
- Always disarm: call `Input.CancelPick()` when you stop (ModMenu does so in `OnShutdown`, and when its
  window closes). A pick left armed swallows the player's next click for nothing. The loader also
  disarms the pick of a mod that unloads or faults.
- Pick mode swallows mouse **buttons** only. The game still sees mouse movement, so hover effects keep
  working.
- Pick mode works at the window's message loop, below GameMaker's input handling, so `io_clear` does not
  affect it.
- A click on the overlay's own windows is not taken: the overlay owns it.

## Swallow the keyboard while your window is open {#swallow-keys}

Pick mode covers clicks. For keys, clear the game's input before it reads any: hook a Step event that runs
early, and call `io_clear`. Stoneshard's `o_controller` Begin Step (`Step_1`) runs before any key is read,
so a hook on it is early enough. ModMenu, TavernGames' table and the Trials card window all do this
while they are open:

```csharp
public void CaptureKeys() =>
    Objects.o_controller.Step_1.Before(_ =>
    {
        if (!IsOpen) return;
        try
        {
            if (Builtins.keyboard_check_pressed(27).AsBool) _escPressed = true;
            if (Builtins.keyboard_check_pressed(32).AsBool) _actionPressed = true;
            Builtins.io_clear();
        }
        catch (GmlException) { }
    });
```

<small>Source: [managed/Mods/TavernGames/Framework/Table.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/TavernGames/Framework/Table.cs#L87-L98)</small>

Note the order: the handler **reads** the keys it wants (`Esc` to close, `Space` to act) and then clears
the rest. Without the read, `io_clear` would erase your own input too, and without the clear, Esc would
also open the game's pause menu.

Gotchas:

- Return at once when your window is closed, as `if (!IsOpen) return;` does. The hook runs every frame
  for the whole session.
- Pick one early point in the frame, and use it everywhere. Hooking a later event is too late: the game
  has already read the key.
- `io_clear` is the game's own builtin and clears the keyboard **and** mouse state for the frame. Combine
  it with pick mode if the window also takes clicks.
- For the hook mechanics themselves, see [Run code every step of an object](hooks.md#every-step).

## The overlay and your input {#overlay-input}

The loader's overlay (toggled with INSERT) sits in front of the game and takes input first. While it is
visible and Dear ImGui says it wants the mouse (the pointer is over an overlay window) or the keyboard (a
text box has focus), those mouse or keyboard messages go to the overlay and **not** to the game. Key and
button **releases** always reach the game, so a key held down in the game and released over the overlay
does not stay stuck.

What this means for a mod:

- Your `OnGUI` widgets get input like any ImGui widget; you do nothing special.
- A hotkey you read with `keyboard_check_pressed` stops working while the player is typing in one of the
  overlay's text boxes. That is intended.
- Pick mode ignores clicks on the overlay, so a mod's "click something in the game" tool never fires from
  clicking its own buttons.
- Test mods with the overlay closed. The game's input only behaves as the player will see it when the
  overlay is hidden.
