"""Placeholder slides for the StoneshardTrials intro: 1920x1080, dark gradient,
a simple silhouette, the title large and 'placeholder' small. Run it with any
Python that has Pillow; the real art replaces these files under the same names."""
import math
import random
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = Path(__file__).resolve().parent / "slides"
W, H = 1920, 1080
FONT = r"C:\Windows\Fonts\georgiab.ttf"
FONT_SMALL = r"C:\Windows\Fonts\georgiai.ttf"


def gradient(top, bottom, glow=None, glow_at=(0.5, 0.45), glow_r=0.55):
    img = Image.new("RGB", (W, H))
    px = img.load()
    gx, gy = glow_at[0] * W, glow_at[1] * H
    for y in range(H):
        t = y / (H - 1)
        base = [top[i] + (bottom[i] - top[i]) * t for i in range(3)]
        for x in range(0, W):
            c = base
            if glow:
                d = math.hypot((x - gx) / W, (y - gy) / H) / glow_r
                k = max(0.0, 1 - d) ** 2
                c = [c[i] + (glow[i] - c[i]) * k for i in range(3)]
            # A vignette keeps the edges dark, as the brief asks of the real art.
            v = 1 - 0.55 * (math.hypot(x / W - 0.5, y / H - 0.5) / 0.707) ** 2
            px[x, y] = tuple(int(max(0, min(255, ch * v))) for ch in c)
    return img


def hills(d, colour, base, amp, seed):
    rnd = random.Random(seed)
    phase = [rnd.uniform(0, 6.28) for _ in range(3)]
    pts = [(0, H)]
    for x in range(0, W + 1, 8):
        y = base + amp * (0.6 * math.sin(x / 310 + phase[0]) + 0.3 * math.sin(x / 120 + phase[1]) + 0.1 * math.sin(x / 47 + phase[2]))
        pts.append((x, y))
    pts.append((W, H))
    d.polygon(pts, fill=colour)


def figure(d, x, y, s, colour):
    """A cloaked standing figure; (x, y) is the feet."""
    d.polygon([(x - 34 * s, y), (x - 20 * s, y - 120 * s), (x - 14 * s, y - 150 * s), (x + 14 * s, y - 150 * s),
               (x + 22 * s, y - 120 * s), (x + 36 * s, y)], fill=colour)
    d.ellipse([x - 15 * s, y - 182 * s, x + 15 * s, y - 146 * s], fill=colour)
    d.line([(x + 30 * s, y - 40 * s), (x + 64 * s, y - 175 * s)], fill=colour, width=max(2, int(5 * s)))


def pine(d, x, y, s, colour):
    d.rectangle([x - 5 * s, y - 40 * s, x + 5 * s, y], fill=colour)
    for i in range(5):
        w = (70 - i * 12) * s
        top = y - (40 + i * 45 + 70) * s
        d.polygon([(x - w, y - (40 + i * 45) * s), (x + w, y - (40 + i * 45) * s), (x, top)], fill=colour)


def arch(d, x, y, w, h, colour):
    d.rectangle([x - w / 2, y - h + w / 2, x + w / 2, y], fill=colour)
    d.ellipse([x - w / 2, y - h, x + w / 2, y - h + w], fill=colour)


def slide_road(img):
    d = ImageDraw.Draw(img)
    hills(d, (22, 28, 38), 600, 60, 1)
    hills(d, (12, 15, 21), 700, 40, 2)
    for i, x in enumerate([180, 260, 1600, 1700, 1790]):
        pine(d, x, 720 + (i % 2) * 20, 1.1, (8, 10, 14))
    figure(d, 1150, 715, 1.2, (6, 7, 10))


def slide_taking(img):
    d = ImageDraw.Draw(img)
    hills(d, (8, 14, 10), 760, 30, 3)
    # The green fire: a glow already in the gradient, flames drawn over it.
    for i in range(9):
        x = 960 + (i - 4) * 18
        d.polygon([(x - 26, 760), (x + 26, 760), (x + (i - 4) * 4, 760 - 120 - (4 - abs(i - 4)) * 30)], fill=(60, 170, 90))
    figure(d, 700, 770, 1.0, (4, 8, 5))
    for i in range(3):
        x = 1180 + i * 150
        d.ellipse([x - 40, 300 - i * 30, x + 40, 380 - i * 30], fill=(20, 40, 26))


def slide_gods(img):
    d = ImageDraw.Draw(img)
    for i, (x, h) in enumerate([(520, 900), (960, 1000), (1400, 880)]):
        d.polygon([(x - 260, H), (x - 170, H - h + 220), (x - 90, H - h + 60), (x + 90, H - h + 60),
                   (x + 170, H - h + 220), (x + 260, H)], fill=(14 + i * 3, 9, 20 + i * 3))
        d.ellipse([x - 80, H - h - 80, x + 80, H - h + 120], fill=(14 + i * 3, 9, 20 + i * 3))
        d.ellipse([x - 30, H - h + 10, x - 14, H - h + 22], fill=(200, 160, 70))
        d.ellipse([x + 14, H - h + 10, x + 30, H - h + 22], fill=(200, 160, 70))
    figure(d, 960, 1000, 0.55, (2, 2, 4))


def slide_tavern(img):
    d = ImageDraw.Draw(img)
    hills(d, (24, 16, 10), 780, 20, 4)
    d.polygon([(620, 790), (620, 520), (960, 330), (1300, 520), (1300, 790)], fill=(18, 12, 8))
    d.rectangle([1150, 330, 1200, 450], fill=(18, 12, 8))
    for x in (720, 1120):
        d.rectangle([x, 580, x + 80, 660], fill=(230, 150, 60))
    d.rectangle([915, 640, 1005, 790], fill=(120, 70, 30))
    d.line([(860, 560), (860, 600)], fill=(18, 12, 8), width=4)
    d.rectangle([820, 600, 900, 640], fill=(40, 26, 14))


def slide_doors(img):
    d = ImageDraw.Draw(img)
    d.rectangle([0, 820, W, H], fill=(14, 18, 20))
    for i, x in enumerate([360, 660, 960, 1260, 1560]):
        h = 560 if i == 2 else 470
        arch(d, x, 820, 200 if i == 2 else 170, h + 20, (24, 30, 34))
        arch(d, x, 810, 160 if i == 2 else 130, h - 10, (70, 150, 150) if i == 2 else (10, 13, 15))
    figure(d, 960, 960, 0.7, (4, 5, 6))


def slide_price(img):
    d = ImageDraw.Draw(img)
    d.rectangle([0, 840, W, H], fill=(16, 6, 6))
    arch(d, 960, 840, 260, 620, (34, 10, 10))
    arch(d, 960, 830, 210, 590, (210, 160, 90))
    d.ellipse([1300, 760, 1380, 790], fill=(170, 130, 40))
    d.ellipse([1340, 790, 1420, 820], fill=(150, 110, 30))
    d.polygon([(600, 700), (580, 760), (620, 760)], fill=(140, 10, 10))
    d.ellipse([578, 740, 622, 784], fill=(140, 10, 10))
    figure(d, 960, 1000, 0.9, (3, 2, 2))


SLIDES = [
    ("01_the_road", "THE ROAD NORTH", (34, 42, 58), (10, 12, 18), (70, 80, 100), (0.7, 0.35), slide_road),
    ("02_the_taking", "THE GREEN FIRE", (6, 12, 8), (3, 6, 4), (50, 140, 80), (0.5, 0.65), slide_taking),
    ("03_the_old_gods", "THE OLD GODS", (26, 14, 36), (6, 4, 10), (110, 70, 40), (0.5, 0.2), slide_gods),
    ("04_the_tavern", "OSBROOK TAVERN", (30, 20, 14), (10, 6, 4), (120, 70, 30), (0.5, 0.6), slide_tavern),
    ("05_the_doors", "EVERY DOOR A TRIAL", (18, 24, 28), (6, 8, 10), (40, 90, 90), (0.5, 0.55), slide_doors),
    ("06_the_price", "BLOOD AND COIN", (36, 8, 8), (8, 2, 2), (140, 60, 30), (0.5, 0.5), slide_price),
]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    big = ImageFont.truetype(FONT, 110)
    small = ImageFont.truetype(FONT_SMALL, 40)
    for slug, title, top, bottom, glow, glow_at, draw in SLIDES:
        img = gradient(top, bottom, glow, glow_at)
        draw(img)
        img = img.filter(ImageFilter.GaussianBlur(1.2))
        d = ImageDraw.Draw(img)
        # The title sits in the upper half: the bottom third is the narration's.
        tw = d.textlength(title, font=big)
        d.text(((W - tw) / 2 + 4, 154), title, font=big, fill=(0, 0, 0))
        d.text(((W - tw) / 2, 150), title, font=big, fill=(225, 210, 180))
        note = f"placeholder  -  {slug}.png  -  see Intro/SLIDES.md"
        nw = d.textlength(note, font=small)
        d.text(((W - nw) / 2, 285), note, font=small, fill=(150, 140, 120))
        img.save(OUT / f"{slug}.png", optimize=True)
        print(slug)


if __name__ == "__main__":
    main()
