# Stoneshard Trials: intro slides (art brief)

The intro is a six-slide slideshow shown before the first trial. Each slide is one
full-screen picture. The game slowly pans and zooms it, fades through black between
slides, and reveals one or two lines of narration over a black band at the bottom.

This brief covers each slide's narration, the picture we need, and a ready-to-paste
prompt for an image generator. Placeholders with the same file names are in `slides/`.
To use real art, replace those files; no code changes are needed.

## Format (every slide)

- **1920 x 1080 PNG, 16:9.** Anything 16:9 works, but 1920x1080 is the target. Larger
  files cost memory and gain nothing.
- **Bottom third is covered.** The game lays a solid black band over roughly the bottom
  25% of the picture, fading into it from about 63% of the height down. Keep faces,
  eyes and the focal point in the **top two thirds**. Below that line, put only ground,
  shadow or floor.
- **Edge margin of about 8% on every side.** The camera zooms in 4-14% and drifts, so
  the edges get cropped. Put nothing important near the frame.
- **Top-right corner stays calm.** A SKIP button sits there (about 170 x 50 px at
  1080p). Do not put a key detail behind it.
- **No text, logos, borders or watermarks** in the picture. The game draws the words.
- Keep it dark overall, with one clear light source per slide. The slides are viewed
  in a dark room, one after another, as one sequence.

## Style (the whole set)

These must read as one set that belongs in Stoneshard:

- **Setting:** Stoneshard's world. The kingdom of **Aldor**, worn down by plague and
  war. The cold **Skadian north** at its border: pine forest, grey stone, snow on the
  high ground. The village of **Osbrook** and its timber tavern. Low fantasy: steel,
  wool, leather, mud and candlelight. No shining armour, no high-fantasy spires, no
  glowing runes everywhere. The only magic is the gods, and they should feel wrong:
  very old, quiet, too large.
- **Look:** painterly, like a dark-fantasy book illustration or a matte painting. Visible
  brush texture, soft edges and strong silhouettes. A *hand-painted pixel-art* version
  (chunky, limited palette, like Stoneshard's own art scaled up) also fits. Pick one
  look and keep it for all six.
- **Palette:** muted and desaturated. Earth browns, slate greys and cold blue-greens,
  each slide with **one** accent colour: dusk blue, sickly green, tarnished gold, ember
  orange, cold teal, blood red. Blacks are deep and shadows are crushed.
- **Light:** low-key and directional, as if lit by a single fire, moon or doorway.
  Heavy vignette.
- **Camera:** cinematic, eye level or slightly low, wide to medium shots. The
  sellsword is small in the frame on most slides. The world and the gods are big.
- **The sellsword**, the same figure throughout: a lone mercenary in a worn hooded
  cloak over padded gambeson and mail. A sword on the hip and a pack on the back. The
  face is always hidden by hood, shadow or angle, so any player can be them. Ordinary,
  tired, dangerous.
- **The old gods:** older than the Church and the kings of Aldor, forgotten by them.
  Huge, robed, faceless shapes of standing stone, root and shadow, with small points
  of tarnished-gold light for eyes. Never fully lit, never detailed, never cute or
  demonic. Think standing stones that are paying attention.

**Shared negative prompt** (add each slide's own):

```
text, letters, words, caption, logo, watermark, signature, border, frame, UI, HUD,
bright saturated colours, neon, anime, cartoon, chibi, 3D render, plastic, glossy,
photorealistic photo, modern clothing, guns, sci-fi, high fantasy castle, shining
plate armour, glowing runes, lens flare, oversaturated, cheerful, cute, blurry face
in focus, extra limbs, deformed hands, cropped head, busy composition
```

Every prompt below assumes this style suffix. Append it if your tool does not keep a
style:

```
dark medieval low fantasy, painterly matte painting, visible brush strokes, muted
desaturated palette, low-key cinematic lighting, heavy vignette, Stoneshard game
art style, 16:9, 1920x1080, important subject in the upper two thirds, empty dark
ground in the bottom third
```

---

## 1. The Road North (`01_the_road.png`)

**Narration**

> Aldor is bleeding. Plague in the south, war in the north, and on every road a sellsword selling his blade.
>
> *(grey)* You were one of them, bound for the Skadian border with a debt behind you and nothing ahead.

**Picture.** Wide establishing shot at dusk. A muddy cart road winds north over bare
hills toward dark pine forest and the first snowy ridges of the Skadian border. The
sellsword walks away from us, small, just right of centre, about a third of the way
up the frame. The hooded cloak is wet and the sword is at the hip. A crow sits on a
leaning wayside gibbet at the left edge, the rope empty. In the far distance, smoke
rises from a burned farm. The sky is low, heavy cloud, with a thin cold band of
blue-grey light on the horizon behind the ridges. Puddles in the ruts catch that
light. The bottom third is dark road and grass.

- *Mood:* weary, lonely, a world going wrong.
- *Palette:* slate blue, wet brown, black pines. Accent: pale dusk blue on the horizon.
- *Camera:* eye level, wide. The game drifts right and pushes in slowly.

**Prompt**

```
lone hooded mercenary walking away down a muddy medieval road at dusk, seen from
behind, small in frame, bare hills, dark pine forest and snowy ridges on the horizon,
empty gibbet with a crow at the left, distant smoke from a burned farm, low heavy
clouds, thin cold blue-grey band of light on the horizon, puddles in the cart ruts
reflecting the sky, melancholy, desolate kingdom ravaged by plague and war
```

**Negative:** shared, plus `sunny, green meadow, village crowd, horse, close-up`.

---

## 2. The Green Fire (`02_the_taking.png`)

**Narration**

> On the third night your campfire burned green, and the stars went out one by one.
>
> Then *(gold)* the old gods came for you: the ones who were here before the Church, before the first king.

**Picture.** Night in a pine clearing. A small campfire in the centre burns a sickly,
unnatural **green**, and its light is the only light in the picture. The sellsword is
half-risen from a bedroll on the left, one hand on the sword hilt, staring up. Above
the treeline the stars are going out in a spreading patch of total black. At the edge
of the firelight, between the trunks, stand tall dark shapes that might be trees and
are not. Tiny pinpricks of gold light hang where eyes would be. The green light
throws long, wrong shadows up the trunks. The bottom third is dark ground, a pack and
a cooking pot.

- *Mood:* dread, the moment the world tilts.
- *Palette:* near-black and deep forest green. Accent: poisonous green firelight and
  tiny gold eyes.
- *Camera:* low, slightly wide, looking across the fire. The game drifts up and left
  as it pushes in.

**Prompt**

```
night forest clearing, small campfire burning with eerie sickly green flames, the
only light source, a hooded mercenary half-rising from a bedroll gripping a sword,
looking up in fear, stars disappearing from the sky in a spreading patch of
blackness, tall dark shapes like trees standing between the pines at the edge of the
light with tiny points of gold light for eyes, long distorted green shadows on tree
trunks, ominous, supernatural, quiet horror
```

**Negative:** shared, plus `orange fire, warm light, moon, monsters with fangs, gore`.

---

## 3. The Old Gods (`03_the_old_gods.png`)

**Narration**

> Aldor has forgotten their names. They have not forgotten Aldor. They are hungry, and they are bored.
>
> *(red)* They want to watch a mortal fight for his life, and they have chosen you.

**Picture.** A vast, dim, timeless space: a ring of standing stones on a hilltop
under a starless sky, or a hall with no walls. Three colossal robed figures rise far
above the frame's middle. They are made of weathered standing stone, roots and
shadow. Their faces are hidden in deep hoods, with only small points of tarnished
gold light for eyes. They lean in slightly, the way an audience leans toward a stage.
Far below, tiny, at the bottom of the top two thirds, the sellsword stands alone on a
flat stone, looking up. Faint dusty gold light falls from somewhere above, catching
the edges of the gods. The bottom third is the dark stone floor.

- *Mood:* awe, insignificance, being watched.
- *Palette:* bruised purple-black and cold stone grey. Accent: tarnished gold eyes and
  rim light.
- *Camera:* very low angle, looking up past the tiny figure. The game starts close and
  pulls back while tilting up. Keep the gods' eyes in the upper third.

**Prompt**

```
three colossal faceless robed figures made of ancient weathered standing stone and
roots, deep hoods, small glowing tarnished gold eyes, towering over a tiny lone
hooded warrior standing on a flat stone far below, ring of standing stones under a
starless sky, faint dusty gold rim light from above, the figures leaning in like an
audience, extreme low angle, sense of scale and insignificance, ancient pagan gods,
forgotten, ominous and silent
```

**Negative:** shared, plus `demons, horns, skulls, angels, wings, halo, detailed faces, Greek statues, temple columns`.

---

## 4. Osbrook Tavern (`04_the_tavern.png`)

**Narration**

> You woke in Osbrook, in a tavern that stands between worlds. The fire is warm and the ale is real.
>
> *(light green)* Here you may rest, trade and mend your gear, but you cannot leave the way you came.

**Picture.** Inside a timber village tavern at night, Stoneshard's Osbrook inn. A
low beamed ceiling, a long plank table, benches, barrels and a stone hearth with a
real, warm orange fire on the right. The sellsword sits at the table, hood down,
seen from behind or in shadow, with a tankard, looking toward the far wall. In the
far wall stands the tavern's front door, an ordinary heavy oak door. Thin cold
**blue-white** light leaks around its frame and under it, as if something other than
the village were outside. The innkeeper is a dim shape behind the counter, polishing
a mug and not looking. Dust hangs in the air. Everything is cosy except that door.
The bottom third is floorboards and the table's near edge.

- *Mood:* uneasy refuge. Warmth with something wrong at the edge.
- *Palette:* amber, ember orange, smoky brown. Accent: a thin cold blue-white leaking
  from the door.
- *Camera:* eye level from the room's corner, medium-wide. The game drifts right
  toward the door and pushes in.

**Prompt**

```
interior of a medieval timber village tavern at night, low wooden beams, long plank
table and benches, barrels, stone hearth with warm orange fire on the right, a lone
mercenary seated at the table seen from behind holding a tankard, a heavy oak front
door in the far wall with thin cold blue-white light leaking around its frame and
under it, innkeeper as a dim silhouette behind the counter, dust in the air, cosy
but uncanny, contrast between warm firelight and cold otherworldly light
```

**Negative:** shared, plus `crowded, party, bright daylight, modern bar, clean, fantasy elves`.

---

## 5. Every Door a Trial (`05_the_doors.png`)

**Narration**

> Every door out of the tavern leads to a trial: a crypt, a camp, a ruin no map remembers.
>
> *(gold)* Win, and the gods reward you. *(red)* Fall, and they find another.

**Picture.** A dreamlike vision of the same tavern door opening onto many places at
once. A dark stone threshold runs across the frame, and in it stands a row of five
arched doorways. Each opens onto a different place:
- a candlelit crypt with sarcophagi;
- a bandit camp's palisade under a red sky;
- a flooded ruin in fog;
- a snowy Skadian pine forest;
- the centre one, larger, opens onto plain cold **teal** darkness.

The sellsword stands before the centre door, small, seen from behind. Old blood
stains and dropped coins lie on the threshold stone. In the dark above the doors, the
faint gold eyes of the gods are just visible, watching. The bottom third is the
threshold floor.

- *Mood:* choice, threat and temptation.
- *Palette:* cold grey stone, black. Each doorway has its own muted tone. Accent: cold
  teal in the centre door.
- *Camera:* straight-on and symmetrical, slightly low. The game drifts left and pulls
  back.

**Prompt**

```
row of five ancient stone arched doorways in darkness, each opening onto a different
place: candlelit crypt with sarcophagi, bandit camp palisade under a red sky, flooded
ruin in fog, snowy pine forest, the larger central door opening onto cold teal
darkness, a lone hooded mercenary standing before the central door seen from behind,
old blood stains and scattered coins on the stone threshold, faint gold eyes watching
from the darkness above, symmetrical composition, dreamlike, ominous invitation
```

**Negative:** shared, plus `modern doors, portals with swirling energy, bright magic circles, crowds`.

---

## 6. Blood and Coin (`06_the_price.png`)

**Narration**

> Their gifts are never free. Every boon is paid for in *(red)* blood or in *(gold)* coin.
>
> The gods are watching. *(white)* The door awaits.

**Picture.** The closing image, and the most intimate. A single tall wooden door,
the tavern door, stands slightly ajar in darkness. Warm gold light pours through the
gap and falls in a long wedge across a stone floor. The sellsword stands at the
threshold, in silhouette against the light, with a drawn sword held low. On a stone
slab or offering table at one side lie a small heap of silver and gold coins and a
dagger. A thin line of fresh **blood** runs from the slab into a carved groove.
Dust motes float in the light. Above the door, barely visible in the dark, a ring of
faint gold eyes. The bottom third is the floor and the base of the light's wedge.

- *Mood:* resolve and the price of going on. The last breath before the first trial.
- *Palette:* black and warm stone. Accents: blood red and old gold.
- *Camera:* eye level, centred on the door, medium shot. The game pushes in slowly
  toward the gap.

**Prompt**

```
a single tall wooden door slightly ajar in darkness, warm golden light pouring
through the gap in a long wedge across a stone floor, a hooded mercenary standing at
the threshold in silhouette with a drawn sword held low, a stone offering slab beside
the door with a small heap of silver and gold coins and a dagger, a thin line of
fresh blood running into a carved groove, dust motes in the light, faint ring of
gold eyes in the darkness above the door, solemn, resolute, the price of power
```

**Negative:** shared, plus `treasure hoard, piles of gold, bright room, gore, corpses, cheerful`.

---

## Notes for whoever replaces the art

- Keep the file names. The intro looks for `slides/01_the_road.png` to
  `slides/06_the_price.png` and draws a dark gradient with the slide's title when a
  file is missing.
- The narration lives in `Slides.cs` with the game's colour codes (`~y~` gold, `~r~`
  red, `~lg~` light green, `~gr~` grey, `~w~` white, closed by `~/~`). If the wording
  changes, change both files.
- The placeholders come from `make_placeholders.py` (Pillow). Running it again
  overwrites `slides/`, so do not run it once the real art is in.
