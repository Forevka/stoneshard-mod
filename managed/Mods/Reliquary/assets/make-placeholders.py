# Draws the placeholder inventory sprites: "<name> image will be here" on a
# framed tile, sized to the carrier's footprint (27 px per inventory cell).
# Run with any Python that has Pillow:  python make-placeholders.py
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

CELL = 27
# id, name, cells wide, cells high - must match Relic.Carrier in the mod.
RELICS = [
    ("pilgrims_millstone", "Pilgrim's Millstone", 1, 2),
    ("wolfs_heart", "Wolf's Heart", 1, 2),
    ("gorgoneion", "Gorgoneion", 1, 2),
    ("copper_ring", "Copper Ring of Faith", 1, 1),
    ("stavebound_ember", "Stavebound Ember", 1, 2),
    ("grafted_hand", "Grafted Hand", 1, 2),
]

here = Path(__file__).parent
font = ImageFont.load_default(size=5)

for rid, name, w, h in RELICS:
    img = Image.new("RGBA", (w * CELL, h * CELL), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rectangle([1, 1, w * CELL - 2, h * CELL - 2], fill=(40, 30, 22, 235), outline=(212, 151, 63, 255))
    words = f"{name} image will be here".split()
    lines, cur = [], ""
    for word in words:
        trial = (cur + " " + word).strip()
        if d.textlength(trial, font=font) <= w * CELL - 5:
            cur = trial
        else:
            if cur:
                lines.append(cur)
            cur = word
    lines.append(cur)
    y = 3
    for line in lines:
        d.text((3, y), line, font=font, fill=(236, 222, 190, 255))
        y += 6
    img.save(here / f"{rid}.png")
    print("wrote", rid)
