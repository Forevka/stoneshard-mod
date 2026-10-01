# Sprite brief: Arm Wrestling and Drinking Contest

Art for two tavern minigames in a mod for **Stoneshard**, a dark, gritty medieval pixel-art RPG. The
sprites are drawn in the game's own GUI, over its dark slate panels, beside the game's own icons and
portraits. They must look as if the game's artists made them.

Each section below gives the **required size in pixels**. The mod's code depends on those sizes and on
the pivot points, so the final PNG files must match them exactly (see "Delivering the files").

---

## Art direction (applies to every sprite)

**Style**
- True pixel art at 1x, with hard pixel edges. No anti-aliasing, no gradients, no blur, no glow, no
  painterly or 3D-rendered look.
- A **1-pixel outline in very dark brown** (`#1E1612`) around every silhouette, like Stoneshard's item
  and character sprites. Never pure black.
- **Light from the upper left.** Each material uses a small ramp of 3 to 4 shades: highlight, base,
  shadow, and a deep shadow next to the outline. Shading is done in clusters, with no dithering noise.
- Muted, earthy and slightly desaturated, like worn medieval things: brown wood, dull brass, tanned skin
  and rough wool. Colour goes only where it carries meaning, such as foam, or a bloodshot knuckle.
- Seen from the side, a little from above, the way Stoneshard draws items on a table.
- **Transparent background** (PNG with alpha). Nothing is drawn outside the object: no floor, no
  shadow blob, unless the section asks for one.

**Palette** (stay close; a few extra in-between shades are fine)

| Use | Hex |
|---|---|
| Outline | `#1E1612` |
| Skin: highlight / base / shadow / deep | `#E6B894` `#CE9874` `#A47054` `#7A4E3A` |
| Wood: highlight / base / shadow | `#A47040` `#7A4E2C` `#54321C` |
| Brass / iron bands | `#C49C48` `#8A6A30` |
| Player's sleeve (green wool) | `#5E7A48` `#466838` `#304A28` |
| Opponent's sleeve (brown leather) | `#8A6440` `#76543A` `#54392A` |
| Ale foam | `#F0EAD6` `#D8CCAC` |
| Ale (visible at the rim) | `#C8862C` `#8E5A1C` |

**Where it is seen:** on a dark panel of about `#1A1C24`, scaled up **2x with nearest-neighbour** (so
each pixel becomes a 2x2 block). Silhouettes must read at that size, so keep them bold and simple.

---

## Arm Wrestling

The scene is built from two separate pieces: each forearm stands on its elbow on the table and is
**rotated in code** to reach the locked hands, and a single sprite of the two clasped hands sits on top
of where the forearms meet.

### 1. `arm.png`: two forearms, 2 frames in one horizontal strip

- **File size: 24 x 46 px.** Two frames of **12 x 46 px**, side by side, with no gap.
  - **Frame 0** (left, x 0-11): **the player's** forearm, **green wool** sleeve.
  - **Frame 1** (right, x 12-23): **the opponent's** forearm, **brown leather** sleeve.
- **Orientation: straight up.** The elbow is at the bottom and the wrist at the top, centred
  horizontally.
- **The pivot is fixed at pixel (6, 43)** in each frame, which is the middle of the elbow. The code
  rotates the arm around this point, so the elbow must sit there. Rows 44-45 are only the underside of
  the elbow or sleeve.
- **Rows 0 to about 33: the bare forearm.** It is a little thinner at the wrist (top, about 6 px wide)
  and wider towards the elbow (about 9-10 px). It is muscular, with a highlight down the left side and
  shadow down the right. There is no hand: the forearm stops at the wrist, where the clasped hands are
  drawn over it.
- **Rows about 34 to 45: the rolled-up sleeve** bunched around the elbow. It is chunkier than the arm,
  with one or two fold lines. Green wool for frame 0, darker brown leather with a stitched seam for
  frame 1.
- The code **stretches the arm lengthwise**, from about 0.6x (the losing arm, near the table) to 1.4x. Avoid details that look wrong when
  stretched vertically, such as round tattoos or circles. Vertical muscle lines and hair are fine.
- Optional character: a scar or a few dark hairs on the opponent's forearm. Keep the two arms clearly
  different at a glance, mainly by sleeve colour.

**Prompt idea:** *"Pixel art sprite sheet, 2 frames, 12x46 each: a muscular human forearm standing
vertically, elbow at the bottom and wrist at the top, no hand; frame 1 with a rolled-up green wool
sleeve at the elbow, frame 2 with a rolled-up brown leather sleeve; dark medieval RPG style like
Stoneshard; 1px dark brown outline, light from the upper left, muted earthy palette, transparent
background, no anti-aliasing."*

### 2. `fists.png`: the locked hands

- **File size: 18 x 14 px**, one frame.
- **The pivot is its centre, pixel (9, 7)**, which is placed exactly where both forearms end.
- The picture is **two hands gripping each other in an arm-wrestling clasp**, seen from the side:
  - the player's hand comes from the lower left, the opponent's from the lower right;
  - the knuckles of both fists sit on top, with thumbs locked over each other;
  - the wrists are cut off at the bottom-left and bottom-right edges, where the forearms join.
- The hands are tense: white-knuckle highlights, and a slightly redder tone at the knuckles
  (`#D08070`, at most one or two pixels).
- It must look right while the forearms swing up to 50 degrees either way. The sprite itself does not
  rotate, so keep it compact and nearly symmetrical.

**Prompt idea:** *"Pixel art, 18x14: two clenched hands gripping each other in an arm-wrestling
clasp, seen from the side, knuckles up, wrists cut off at the bottom corners; tense, white knuckles;
dark medieval pixel-art style, 1px dark brown outline, muted skin tones, transparent background, no
anti-aliasing."*

### Optional extras for Arm Wrestling (the code draws these as flat shapes today)

These would need a small code change to use. Make them only if you want the extra polish.

- **`tabletop.png`, about 320 x 12 px:** the edge of a heavy tavern table seen from the side, with dark
  oak planks, a worn top edge, a couple of knots, a ring stain, and one iron nail head. It is the
  surface the elbows rest on.
- **`tug_meter.png`, 180 x 8 px frame plus a 4 x 14 px marker:** a narrow bar set in dark iron with
  riveted ends. The left half has a faint red tint (the player losing), the right half a faint green
  tint (the player winning). The marker is a small brass pin.

---

## Drinking Contest

### 3. `mug.png`: a tankard, 2 frames in one horizontal strip

- **File size: 36 x 20 px.** Two frames of **18 x 20 px**, side by side.
  - **Frame 0: full.** Ale foam heaped over the rim and slightly overflowing, with one drip down the side.
  - **Frame 1: empty.** The same tankard, with a dark wet inside visible at the rim and a last fleck of
    foam clinging to the inner wall.
- The drawing origin is the **top-left** corner. Both frames must line up exactly (same body, same
  handle), so switching from full to empty only changes the top.
- The subject is a **wooden tavern tankard**: staves of dark wood held by **two brass or iron hoops**,
  with the handle on the **right**. It is chunky and slightly tapered, wider at the base.
- Pixel budget: the body is about 13 px wide and 15 px tall, plus 4-5 px of handle, and the foam takes
  the top 5-6 rows.
- In the game it is drawn at 2x (the cup in hand) and at about 1.2x (a row of empties), so the empty
  frame must still read at the small size. Give it a clear dark mouth.

**Prompt idea:** *"Pixel art sprite sheet, 2 frames, 18x20 each: a wooden medieval tavern tankard
with two brass hoops and a handle on the right; frame 1 full with frothy ale foam overflowing the rim
and a drip, frame 2 empty with a dark wet interior; dark medieval RPG pixel art like Stoneshard, 1px
dark brown outline, light from the upper left, muted palette, transparent background, no
anti-aliasing."*

### Optional extras for the Drinking Contest (not used by the code yet)

- **`sway_marker.png`, about 8 x 16 px:** the cursor on the steadiness bar, drawn as a tiny mug tilted
  30 degrees, or a drop of ale. It replaces the plain white line.
- **`sway_bar.png`, about 150 x 7 px:** the steadiness bar as a wet, stained plank, with a lighter
  "sober" band in the middle where the player aims.
- **`passed_out.png`, about 24 x 12 px:** a small icon for the result line, showing a tipped-over mug
  with ale pooling out of it.
- **`mug_stack` variant:** a third mug frame lying on its side, to show the loser's last mug knocked
  over at the end.

---

## Delivering the files

1. **Generate large, then reduce.** Image models rarely hit exact tiny sizes. Generate each sprite at
   **8x or 16x** on a flat, easy-to-remove background (pure magenta `#FF00FF`). Then:
   - downscale to the exact size with **nearest-neighbour** sampling;
   - make the background transparent;
   - optionally snap the colours to the palette above.

   A small Pillow helper does it (run with `C:\Python314\python.exe`):

   ```python
   from PIL import Image
   big = Image.open("arm_big.png").convert("RGBA")
   small = big.resize((24, 46), Image.NEAREST)          # the exact size from this brief
   px = small.load()
   for y in range(small.height):
       for x in range(small.width):
           r, g, b, a = px[x, y]
           if r > 200 and g < 60 and b > 200:            # the magenta background
               px[x, y] = (0, 0, 0, 0)
   small.save("arm.png")
   ```
2. **Check by hand at 1x.** Every pixel counts at this size. Fix stray semi-transparent pixels (alpha
   must be 0 or 255), one-pixel gaps in the outline, and details that turned to mush.
3. **Keep the exact sizes and pivots:**

   | File | Size | Frames | Pivot |
   |---|---|---|---|
   | `arm.png` | 36 x 46 | 2 (18 x 46) | (9, 43), the elbow |
   | `fists.png` | 18 x 14 | 1 | (9, 7), the centre |
   | `mug.png` | 36 x 20 | 2 (18 x 20) | (0, 0), top-left |
   | `tabletop.png` | 64 x 12 | 1, tiled sideways | (0, 0) |
   | `tug_meter.png` / `tug_marker.png` | 180 x 12 / 4 x 14 | 1 each | (0, 0) |

   The forearms became 18 px wide once real art was in: shrunk to the first brief's 12 they looked
   thin. `import_generated.py` turns generated pictures into these files.

   A different size also works if you tell whoever wires it in. The pivots are set in
   `TavernGamesMod.cs` (`AddSprite(..., xOrigin, yOrigin)`) and the forearm's length in
   `ArmWrestling.cs` (`ArmLength`, the distance from the pivot to the wrist).
4. **Drop them in** `managed/Mods/TavernGames/assets/`, replacing the placeholders. Building and
   deploying the mod copies them to the game's `Mods/TavernGames/assets/`, and a hot reload picks them
   up without restarting the game.
5. **Mind the generator.** `assets-src/make_assets.py` writes placeholder versions of these three files.
   Once hand-made art is in place, remove the `arm`, `fists` and `mug` lines from its `main()`, or
   running it again will overwrite your art.
