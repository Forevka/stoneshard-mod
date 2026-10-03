---
title: Drawing and UI
description: Recipes for adding sprites and sounds, drawing into the game's GUI layer, reskinning sprites, drawing with the game's own UI scripts, and building an ImGui tab.
---

A mod has two places to put pixels. The **game's GUI layer** is the game's own screen: you draw into it
with GameMaker's `draw_*` builtins, your own sprites, or the game's UI scripts, and it looks like part of
the game. The **overlay tab** is a Dear ImGui window, toggled with INSERT, with one tab per mod: you
build it with the `UI.*` widgets in `OnGUI`, and it is for settings, tools and debugging.

## Add a sprite and a sound, and draw in the GUI layer {#sprite-and-sound}

Load files with `Content.AddSprite` and `Content.AddSound`, and register a draw handler with
`GameDraw.OnGui`. The handler runs once a frame inside a Draw GUI event, so coordinates are GUI pixels
(`GameDraw.GuiWidth` and `GuiHeight` are the layer's size). ContentDemo loads a spinning coin from an
8-frame PNG strip and a chime from an OGG file:

```csharp
_coin = Content.AddSprite("assets/coin.png", frames: 8, xOrigin: 24, yOrigin: 24);
_chime = Content.AddSound("assets/chime.ogg");
_drawing = GameDraw.OnGui(DrawBadge);
```

<small>Source: [managed/Mods/ContentDemo/ContentDemo.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L30-L32)</small>

```csharp
private void DrawBadge()
{
    if (!_badge || _coin == null) return;
    _spin += 0.25;
    double x = GameDraw.GuiWidth - 40, y = 40;
    _coin.Draw(x, y, frame: _spin);
    // A second, tinted and fading copy shows the rest of draw_sprite_ext.
    _coin.Draw(x - 56, y, frame: _spin + 4, xScale: 0.75, yScale: 0.75, colour: 0xFFC080,
               alpha: 0.5 + 0.5 * Math.Sin(_spin / 4));
}
```

<small>Source: [managed/Mods/ContentDemo/ContentDemo.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L38-L47)</small>

`Sprite.Draw(x, y, frame, xScale, yScale, rotation, colour, alpha)` is `draw_sprite_ext`. The frame wraps,
so a growing counter animates it, and `colour` is in GameMaker's 0xBBGGRR order. A sound plays with
`_chime.Play()` ([ContentDemo.cs line 57](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L57)).

**Where content files go.** A relative path is looked up in a folder named after the mod's dll, inside
`Mods` (for `ContentDemo.dll`, the folder `Mods\ContentDemo\`, so the example's file is
`Mods\ContentDemo\assets\coin.png`). It is also looked up next to the dll, but only for a mod that has a
folder to itself, never in the shared `Mods` root where another mod's file of the same name could be
picked up. An absolute path is used as given. Supported formats are PNG, JPEG or GIF for sprites (a
horizontal strip is cut into `frames` equal frames) and OGG Vorbis for sounds.

Gotchas:

- Content belongs to your mod. Unloading or hot reloading empties your sprites (their slot is reused), closes your sounds, and
  gives replaced sprites their original image back. Content files are not watched: reload the mod after
  changing one.
- A missing or unreadable file throws (`FileNotFoundException`, `ArgumentException` for a wrong
  extension, `GmlException` if the game cannot load it).
- A draw handler runs every frame, so keep it cheap. It may set colour, alpha, font, alignment and blend
  mode freely: the loader restores them afterwards, but do not rely on what they are when your handler
  starts.
- The loader borrows the Draw GUI event of some object with a live instance, and moves to another when a
  room change removes it. Handlers do not see which. `GameDraw.Carrier` names it, for diagnostics.

## Reskin one of the game's own sprites {#reskin}

Use `Content.ReplaceSprite(name, file, ...)`. Every place that draws that sprite shows your image, and
unloading the mod restores the original. ContentDemo's reskin button does this:

```csharp
private void Reskin(string name)
{
    try
    {
        _reskin?.Dispose();
        _reskin = Content.ReplaceSprite(name, "assets/coin.png", frames: 8, xOrigin: 24, yOrigin: 24);
        _status = $"{name} now shows the coin";
        Config.Set("reskin", name);
    }
    // A missing file or a bad name is the user's typo, not a reason to
    // disable the mod.
    catch (Exception ex) when (ex is GmlException or IOException or ArgumentException)
    {
        _reskin = null;
        _status = ex.Message;
    }
}
```

<small>Source: [managed/Mods/ContentDemo/ContentDemo.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L74-L90)</small>

Gotchas:

- Only the game's own sprites can be replaced. Naming a sprite another mod added throws `GmlException`.
- Dispose the returned `Sprite` to restore the original. If several mods replace one sprite, unloading
  them in any order restores what was there before each one.
- The new image can have a different frame count and origin from the original; the parameters are the
  new image's.

## Draw text and shapes with the `draw_*` builtins {#draw-builtins}

Inside a `GameDraw.OnGui` handler, call GameMaker's own drawing functions. The Console's inspector outlines
an instance and labels it:

```csharp
double x1 = (l - m.X) * m.Sx, y1 = (top - m.Y) * m.Sy, x2 = (rt - m.X) * m.Sx, y2 = (b - m.Y) * m.Sy;
Game.CallBuiltin("draw_set_alpha", 1);
Game.CallBuiltin("draw_set_colour", colour);
Game.CallBuiltin("draw_rectangle", x1 - 1, y1 - 1, x2 + 1, y2 + 1, true);
Game.CallBuiltin("draw_text", x1, Math.Max(0, y1 - 16), label);
```

<small>Source: [managed/Mods/Console/Inspector.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L470-L474)</small>

Gotchas:

- Coordinates are GUI pixels, not room coordinates. To outline something in the room, convert with the
  camera: read `view_get_camera`, `camera_get_view_x/y/width/height` and scale by the GUI size, as the
  inspector does ([Inspector.cs lines 455-459](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/Inspector.cs#L455-L459)).
- Colours are 0xBBGGRR integers, not RGB.
- `draw_text` uses whatever font the game last set. Set the font yourself if the look matters.

## Draw a game sprite at a position {#draw-sprite-ext}

With the interop, `Builtins.draw_sprite_ext` is typed. FastTravel draws the game's own cell-highlight
frame over a world-map cell the game left unhighlighted, scaled to the cell's size:

```csharp
if (cell is { } at && _verdict == WorldMap.Verdict.Allowed && _frameSprite >= 0 && !WorldMap.Revealed(at)
    && WorldMap.CellRect(at) is { } r)
{
    double k = r.Size / Builtins.sprite_get_width(_frameSprite).AsReal;
    Builtins.draw_sprite_ext(_frameSprite, 0, r.X, r.Y, k, k, 0, 16777215, 1);
}
```

<small>Source: [managed/Mods/FastTravel/FastTravelMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L149-L154)</small>

The sprite's index came from `Builtins.asset_get_index("s_highlight_globalmap")`; see
[Look up an asset](calling-the-game.md#assets-and-sounds). To draw one of your own sprites, `Sprite.Draw`
(above) is the same call with defaults.

## Draw with the game's own UI scripts {#game-ui}

When a mod's panel should look like the game's, call the scripts the game draws its own panels with.
FastTravel's banner is a Stoneshard world-map board plus a line of the game's colour text:

```csharp
public void DrawBanner(string text)
{
    double map = TextMap(text);
    var data = new DsMap(map);
    double w = data.Get("width").AsReal + 2 * Pad, h = data.Get("height").AsReal + 2 * Pad;
    double x = Math.Round((GameDraw.GuiWidth - w) / 2), y = 24;
    _banner = (x, y, w, h);
    Scripts.scr_globalmapDrawBoard.Call(x, y, w, h, BoardScale);
    Scripts.scr_colorTextDraw.Call(map, x + Pad, y + Pad, 0, 0, 0, 1);
}
```

<small>Source: [managed/Mods/FastTravel/MapUi.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/MapUi.cs#L157-L166)</small>

The game's colour text is a ds_map built once from a string with tags such as `~lg~green~/~`, then drawn
every frame. The mod keeps one per string and checks it still exists:

```csharp
private double TextMap(string text)
{
    if (_textMaps.TryGetValue(text, out var id) && Builtins.ds_exists(id, 1).AsBool) return id;
    if (_textMaps.Count > 32) Clear();
    var map = Builtins.ds_map_create();
    Scripts.scr_colorTextCreate.Call(map, text, White, 1600, 1);
    return _textMaps[text] = map.AsReal;
}
```

<small>Source: [managed/Mods/FastTravel/MapUi.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/MapUi.cs#L171-L178)</small>

Gotchas:

- Find the scripts a panel uses with the Console's `code` command or ScriptSpy (see
  [Finding hooks](../finding-hooks.md)). Their arguments are not documented; watch a real call.
- A ds_map you create is yours to destroy. FastTravel's `OnShutdown` calls `_ui.Clear()`, which destroys
  its maps ([FastTravelMod.cs line 49](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/FastTravel/FastTravelMod.cs#L49)).
- A script that can throw must not take the mod with it: draw handlers should catch `GmlException`
  (see [Robustness and testing](robustness-and-testing.md#catch-gmlexception)).

## Build an overlay tab with ImGui {#overlay-tab}

Override `CoreMod.OnGUI` and call the `UI.*` widgets. It runs while the overlay draws your tab, and `UI`
works only there: calling it from `OnUpdate` or a hook throws. DwarfBoost's tab has a slider that saves its value, a read-out, and
a status line:

```csharp
public override void OnGUI()
{
    if (_status.Length > 0) UI.TextColored(1f, 0.6f, 0.3f, _status);

    UI.Text("Gold income");
    if (UI.SliderFloat("gold x##gold", ref _goldMultiplier, 1f, 20f)) Config.Set("goldMultiplier", _goldMultiplier);
    UI.TextDisabled($"bonus gold granted this session: {_bonusGold:N0}");
    UI.Separator();
    // ...
}
```

<small>Source: [managed/Mods/DwarfBoost/DwarfBoost.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L119-L131)</small>

Widgets that edit a value take it by `ref` and return `true` on the frame it changed, which is when you
save it. ContentDemo shows a checkbox and a text input; ScriptSpy a collapsing header:

```csharp
if (UI.Checkbox("Show the coin badge (top right of the game)", ref _badge)) Config.Set("badge", _badge);
// ...
UI.InputText("sprite name##reskin", ref _target, 128);
if (UI.Button("Reskin")) Reskin(_target.Trim());
```

<small>Source: [managed/Mods/ContentDemo/ContentDemo.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L53) and [lines 61-62](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ContentDemo/ContentDemo.cs#L61-L62)</small>

```csharp
UI.PushId(w.Symbol);
if (UI.CollapsingHeader($"{w.Symbol}  ({w.Calls:N0} calls)"))
{
```

<small>Source: [managed/Mods/ScriptSpy/ScriptSpy.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/ScriptSpy/ScriptSpy.cs#L53-L55)</small>

The widget set is in `managed/CoreLoader/UI.cs`: text (`Text`, `TextColored`, `TextDisabled`,
`TextWrapped`), `Button`, `SmallButton`, `Checkbox`, `SliderFloat`, `SliderInt`, `InputText`,
`InputInt`, `InputFloat`, `InputDouble`, `Combo`, `Selectable`, `ProgressBar`, `Tooltip`, tab bars, tree
nodes, child regions, `Clipped` for long lists, and more.

## Keep the tab from crashing on live data: UI.Guarded {#guarded}

An exception in `OnGUI` disables the mod. If part of a tab reads the live game, it can fail for ordinary
reasons (an instance died since the last frame, the player is at the title screen). Wrap that part in
`UI.Guarded(draw, onError)`: on an exception it closes every scope `draw` opened, hands you the
exception, and the rest of the tab carries on. The Console wraps its globals view:

```csharp
UI.Guarded(() => { _freezer.Draw(); _globals.Draw(); },
    ex => UI.TextColored(1f, 0.5f, 0.45f, $"{ex.GetType().Name}: {ex.Message}"));
```

<small>Source: [managed/Mods/Console/ConsoleMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/ConsoleMod.cs#L109-L110)</small>

Reliquary guards each row separately, so one stale item costs a line of text, not the tab:

```csharp
foreach (var relic in _relics)
    UI.Guarded(() => DrawRelic(relic), ex => UI.TextColored(0.95f, 0.4f, 0.4f, $"{relic.Name}: {ex.Message}"));
```

<small>Source: [managed/Mods/Reliquary/ReliquaryMod.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Reliquary/ReliquaryMod.cs#L357-L358)</small>

## Give widgets stable ids {#stable-ids}

Dear ImGui identifies a widget by its label, so two widgets with the same label in one window are the
same widget, and a label that changes every frame is a new widget every frame (a tree node would collapse,
a text field would lose focus). Three tools:

- `"label##suffix"`: the text after `##` is part of the id but is not shown. `"XP x##xp"` is shown as
  `XP x`.
- `"shown text###id"`: with three hashes, **only** the text after `###` is the id, so the visible part can
  change. Use it for any label that carries a live value.
- `UI.PushId(x)` / `UI.PopId()`: prefix every id inside with `x`, so a loop can draw the same label
  for each row.

The Console's variable view puts the live value in the label and the variable's name in the id:

```csharp
// "###": the value is display only; the node's identity is the
// name, so a value that changes does not collapse it.
if (UI.TreeNode($"{name}  [{type}]  {text}###{name}"))
```

<small>Source: [managed/Mods/Console/VariableTable.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/VariableTable.cs#L152-L154)</small>

Cheats' enemy list needs one button and one selectable per row, each with its own id:

```csharp
if (UI.SmallButton($"Remove###rm{e.Key}")) remove = e;
UI.SameLine();
if (UI.Selectable($"{RowText(e)}###row{e.Key}", e.Key == _selectedKey, allowOverlap: true))
    _selectedKey = e.Key;
```

<small>Source: [managed/Mods/StoneshardCheats/Tabs/EnemiesTab.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Tabs/EnemiesTab.cs#L96-L99)</small>

DwarfBoost draws the same pair of buttons for gold, mithril and soul, and wraps each pair in
`UI.PushId(name)` and `UI.PopId()` so they do not collide
([DwarfBoost.cs lines 140-158](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/DwarfBoost/DwarfBoost.cs#L140-L158)).

## Close every scope you open {#scopes}

Some widgets open a scope that must be closed: `PushId`, `BeginChild`, `BeginTabBar`, `BeginTabItem`,
`TreeNode`, `BeginCombo`, `BeginDisabled`, `PushTextColor`. The loader tracks them. An end call that
does not match the most recent open scope throws `InvalidOperationException`, and a mod that leaves a
scope open when `OnGUI` returns is faulted, because an unbalanced ImGui stack would corrupt the whole
overlay. The rules, from the XML docs in `UI.cs`:

- `BeginChild` returns whether the region is visible, but **always** pair it with `EndChild`.
- `BeginTabBar`, `BeginTabItem`, `TreeNode` and `BeginCombo` open a scope **only** when they return
  `true`. Call the matching `End*` or `TreePop` only then.
- `PushId` and `PopId` always pair.

```csharp
UI.BeginChild("##roster", 230f);
foreach (var e in _roster)
{
    // ...
}
UI.EndChild();
```

<small>Source: [managed/Mods/StoneshardCheats/Tabs/EnemiesTab.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardCheats/Tabs/EnemiesTab.cs#L87-L101)</small>

```csharp
if (UI.TreeNode($"{name}  [{type}]  {text}###{name}"))
{
    try { DrawChildren(Get(name), 1); } catch (GmlException ex) { UI.TextDisabled(ex.Message); }
    UI.TreePop();
}
```

<small>Source: [managed/Mods/Console/VariableTable.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/Console/VariableTable.cs#L154-L158)</small>

Note that the `try` sits **inside** the scope, so the exception cannot skip `TreePop`. If you cannot
guarantee that, use `UI.Guarded`, which unwinds the scopes for you. The regression mods
`managed/Tests/WidgetProbe` (every widget, and scope unwind under faults) and `managed/Tests/FaultyGuiMod`
exercise these rules.
