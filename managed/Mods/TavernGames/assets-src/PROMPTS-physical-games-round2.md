# Image prompts, round 2: Arm Wrestling and Drinking Contest

Copy one prompt per image. Each makes **one sprite**, drawn large on flat magenta, so it can be
shrunk to its real size with nearest-neighbour sampling (see `SPRITES-physical-games.md`,
"Delivering the files"). The mugs from round 1 are kept; they need no new prompt.

---

## 1. Player's forearm → `arm.png` frame 0 (12 x 46)

```
Pixel art game sprite, single object, no text, no labels.

Subject: ONE human forearm standing perfectly straight and vertical, like a post. The ELBOW is at the very bottom, the WRIST at the very top. The arm ends at the wrist: NO hand, NO fist, NO fingers - cut cleanly flat at the wrist. NO upper arm, NO bicep, NO bend at all. A rolled-up GREEN WOOL sleeve is bunched around the elbow at the bottom (bottom quarter of the sprite). Above the sleeve: bare muscular forearm, slightly thinner at the wrist than near the elbow, one or two vertical muscle lines.

Shape: very tall and narrow, exactly 12 pixels wide by 46 pixels tall on the pixel grid (proportion about 1 : 4). Draw it at 16x scale: every art pixel is a crisp 16x16 square block, so the image is 192 x 736, with the forearm centred and filling the height.

Style: dark gritty medieval pixel art like the game Stoneshard. Hard pixel edges, NO anti-aliasing, NO gradients, NO blur. A one-pixel (one block) very dark brown outline (#1E1612) all around. Light from the upper left: highlight on the left edge, shadow on the right. Muted earthy colours, at most 12 colours. Skin #E6B894 #CE9874 #A47054 #7A4E3A, sleeve #5E7A48 #466838 #304A28.

Background: completely flat pure magenta #FF00FF, no shadow, no floor, nothing else in the image.
```

## 2. Opponent's forearm → `arm.png` frame 1 (12 x 46)

```
Pixel art game sprite, single object, no text, no labels.

Subject: ONE human forearm standing perfectly straight and vertical, like a post. The ELBOW is at the very bottom, the WRIST at the very top. The arm ends at the wrist: NO hand, NO fist, NO fingers - cut cleanly flat at the wrist. NO upper arm, NO bicep, NO bend at all. A rolled-up BROWN LEATHER sleeve with a stitched seam is bunched around the elbow at the bottom (bottom quarter of the sprite). Above the sleeve: bare, hairy, muscular forearm with a small pale scar, slightly thinner at the wrist than near the elbow.

Shape: very tall and narrow, exactly 12 pixels wide by 46 pixels tall on the pixel grid (proportion about 1 : 4). Draw it at 16x scale: every art pixel is a crisp 16x16 square block, so the image is 192 x 736, with the forearm centred and filling the height.

Style: dark gritty medieval pixel art like the game Stoneshard. Hard pixel edges, NO anti-aliasing, NO gradients, NO blur. A one-pixel (one block) very dark brown outline (#1E1612) all around. Light from the upper left: highlight on the left edge, shadow on the right. Muted earthy colours, at most 12 colours. Skin #E6B894 #CE9874 #A47054 #7A4E3A, sleeve #8A6440 #76543A #54392A.

Background: completely flat pure magenta #FF00FF, no shadow, no floor, nothing else in the image.
```

## 3. Locked hands → `fists.png` (18 x 14)

```
Pixel art game sprite, single object, no text, no labels.

Subject: two hands locked together in an arm-wrestling grip, seen from the side. The left hand comes up from the bottom-left corner, the right hand from the bottom-right corner; the wrists are cut off at those two corners. Both fists meet in the middle with knuckles on top and thumbs locked over each other. Tense: the knuckles are pale, one or two pixels of red at the knuckles.

Keep it SIMPLE and BOLD - this is a tiny icon: exactly 18 pixels wide by 14 pixels tall on the pixel grid. No veins, no fingernails, no fine lines; read the shape from big flat clusters of 3-4 skin shades only. Compact and nearly symmetrical. Draw it at 16x scale: every art pixel is a crisp 16x16 square block, so the image is 288 x 224.

Style: dark gritty medieval pixel art like the game Stoneshard. Hard pixel edges, NO anti-aliasing, NO gradients. A one-pixel (one block) very dark brown outline (#1E1612). Light from the upper left. At most 8 colours: skin #E6B894 #CE9874 #A47054 #7A4E3A, knuckle red #D08070.

Background: completely flat pure magenta #FF00FF, nothing else in the image.
```

## 4. Tabletop edge → `tabletop.png` (optional, 64 x 12, tiles sideways)

```
Pixel art game texture, no text, no labels.

Subject: the side edge of a heavy tavern table seen straight on: two dark oak planks running left to right, a worn lighter top edge, faint wood grain, one small knot, one iron nail head. It must TILE SEAMLESSLY left-to-right (the left and right edges match), so no big unique features like ring stains.

Shape: a long thin strip, exactly 64 pixels wide by 12 pixels tall on the pixel grid. Draw it at 10x scale (every art pixel a crisp 10x10 block): a 640 x 120 strip, placed in the centre of the image with flat magenta above and below it.

Style: dark gritty medieval pixel art like the game Stoneshard. Hard pixel edges, NO anti-aliasing, NO gradients. Light from the top. Muted wood colours only: #A47040 #7A4E2C #54321C #1E1612, iron #8A8A8A #4A4A4A.

Background: completely flat pure magenta #FF00FF around the strip.
```

## 5. Tug meter → `tug_meter.png` + `tug_marker.png` (optional)

```
Pixel art game UI element, no text, no labels.

Subject: a long, very thin horizontal gauge bar set in dark iron. Left half of the inside: dull muted red. Right half: dull muted green. The iron frame is only ONE art pixel thick along the top and bottom; each end has a small riveted iron cap no wider than 5 pixels.

Shape: exactly 180 pixels wide by 8 pixels tall on the pixel grid - a long thin line, NOT a chunky panel. Draw it at 4x scale (every art pixel a crisp 4x4 block): a 720 x 32 strip in the centre of the image, flat magenta above and below it.

Separately, below it in the same image with clear magenta space between: a small brass marker pin, exactly 4 pixels wide by 14 pixels tall (16 x 56 at 4x), standing upright.

Style: dark gritty medieval pixel art like the game Stoneshard. Hard pixel edges, NO anti-aliasing, NO gradients, NO glow. Iron #6A6A72 #3A3A42 #1E1612, red #7A3A30 #5A2A24, green #4A6A3A #34502A, brass #C49C48 #8A6A30.

Background: completely flat pure magenta #FF00FF.
```

---

**After generating:** shrink each to its exact pixel size with nearest-neighbour sampling, turn the
magenta transparent, and check it at 1x (see the Pillow snippet in `SPRITES-physical-games.md`). The two
forearms go side by side into one 24 x 46 strip: the player's on the left, the opponent's on the right.
