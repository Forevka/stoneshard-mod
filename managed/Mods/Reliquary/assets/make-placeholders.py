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
    ("faceless_mirror", "Faceless Mirror", 1, 2),
    ("split_quiver", "The Split Quiver", 1, 2),
    ("cinder_rosary", "Cinder Rosary", 1, 2),
    ("echoing_bell", "The Echoing Bell", 1, 2),
    ("debtors_knot", "The Debtor\'s Knot", 1, 2),
    ("weeping_candle", "The Weeping Candle", 1, 2),
    ("pallbearers_coin", "Pallbearer\'s Coin", 1, 2),
    ("vessel_of_borrowed_years", "Vessel of Borrowed Years", 1, 2),
    ("reliquary_of_saint_mardun", "Reliquary of Saint Mardun", 1, 2),
    ("usurers_scale", "The Usurer\'s Scale", 1, 2),
    ("sundered_gate", "The Sundered Gate", 1, 2),
    ("censer", "Censer of the Drowned Choir", 1, 2),
    ("lodestone_idol", "Lodestone Idol", 1, 2),
    ("surveyors_chain", "The Surveyor\'s Chain", 1, 2),
    ("iron_lung", "The Iron Lung", 1, 2),
    ("oath_stone", "Oath-Stone of the Deep Road", 1, 2),
    ("sated_worm", "The Sated Worm", 1, 2),
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
