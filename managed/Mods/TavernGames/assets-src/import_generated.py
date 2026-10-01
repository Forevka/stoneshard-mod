"""Brings generated art (image-model output, drawn large) down to the Tavern
Games sprite sizes, the way the arm wrestling and drinking contest art was made:

    C:\\Python314\\python.exe managed\\Mods\\TavernGames\\assets-src\\import_generated.py ^
        --arms forearms.jpg --fists hands.jpg --meter meter.jpg --plank plank.jpg --mugs sheet.jfif

Each source is the model's picture: objects on flat magenta (#FF00FF), except
--mugs, the first sheet, whose objects sit on its dark slate background. The
pieces are cut out, area-averaged down to size, their alpha snapped to 0/255
and their palette reduced, then written to ..\\assets. See
SPRITES-physical-games.md for the sizes and pivots the code expects.
"""

import argparse
from pathlib import Path

from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "assets"


def key_magenta(im: Image.Image) -> Image.Image:
    im = im.convert("RGBA")
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, _ = px[x, y]
            if r > 170 and b > 170 and g < 110:
                px[x, y] = (0, 0, 0, 0)
    return im


def key_colour(im: Image.Image, colour, tolerance=34) -> Image.Image:
    """Makes everything close to a flat background colour transparent."""
    im = im.convert("RGBA")
    px = im.load()
    cr, cg, cb = colour
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, _ = px[x, y]
            if abs(r - cr) + abs(g - cg) + abs(b - cb) < tolerance:
                px[x, y] = (0, 0, 0, 0)
    return im


def trim(im: Image.Image) -> Image.Image:
    return im.crop(im.getbbox())


def columns(im: Image.Image, min_gap=6) -> list[Image.Image]:
    """The objects of a picture, left to right, split by fully transparent columns."""
    a = im.getchannel("A")
    filled = [any(a.getpixel((x, y)) for y in range(0, im.height, 2)) for x in range(im.width)]
    found, start, gap = [], None, 0
    for x, f in enumerate(filled + [False] * (min_gap + 1)):
        if f:
            start = x if start is None else start
            gap = 0
        elif start is not None:
            gap += 1
            if gap > min_gap:
                found.append(trim(im.crop((start, 0, x - gap + 1, im.height))))
                start = None
    return found


def rows(im: Image.Image) -> list[Image.Image]:
    """The objects of a picture, top to bottom, split by fully transparent rows."""
    filled = [any(im.getpixel((x, y))[3] for x in range(0, im.width, 3)) for y in range(im.height)]
    found, start = [], None
    for y, f in enumerate(filled + [False]):
        if f and start is None:
            start = y
        elif not f and start is not None:
            found.append(trim(im.crop((0, start, im.width, y))))
            start = None
    return found


def shrink(im: Image.Image, size, colours=24) -> Image.Image:
    small = im.resize(size, Image.BOX)
    rgb = small.convert("RGB").quantize(colours, method=Image.Quantize.MEDIANCUT).convert("RGB")
    rgb.putalpha(small.getchannel("A").point(lambda v: 255 if v >= 128 else 0))
    return rgb


def centred(im: Image.Image, size) -> Image.Image:
    """Shrinks to the frame's height and centres it in a frame of that size (the elbow stays centred)."""
    w, h = size
    fitted = shrink(im, (min(w, max(1, round(im.width * h / im.height))), h))
    frame = Image.new("RGBA", size, (0, 0, 0, 0))
    frame.alpha_composite(fitted, ((w - fitted.width) // 2, 0))
    return frame


def strip(frames: list[Image.Image]) -> Image.Image:
    w, h = frames[0].size
    out = Image.new("RGBA", (w * len(frames), h), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        out.alpha_composite(f, (i * w, 0))
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--arms", help="forearms on magenta, left to right")
    ap.add_argument("--arm-pick", default="1,2", help="which forearms (0-based): the player's, then the opponent's")
    ap.add_argument("--fists", help="the locked hands on magenta")
    ap.add_argument("--meter", help="the tug meter bar, with its marker under it, on magenta")
    ap.add_argument("--plank", help="the tabletop edge on magenta")
    ap.add_argument("--mugs", help="the first sheet: full and empty mug on its dark background")
    args = ap.parse_args()

    if args.arms:
        arms = columns(key_magenta(Image.open(args.arms)))
        mine, theirs = (int(i) for i in args.arm_pick.split(","))
        strip([centred(arms[mine], (18, 46)), centred(arms[theirs], (18, 46))]).save(OUT / "arm.png")
    if args.fists:
        hands = trim(key_magenta(Image.open(args.fists)))
        # Only the clasped hands: shrunk whole, the long wrists under them eat the space.
        clasp = trim(hands.crop((int(hands.width * 0.22), 0, int(hands.width * 0.78), int(hands.height * 0.62))))
        shrink(clasp, (18, 14)).save(OUT / "fists.png")
    if args.meter:
        bar, marker = rows(key_magenta(Image.open(args.meter)))[:2]
        shrink(bar, (180, 12), 32).save(OUT / "tug_meter.png")
        shrink(marker, (4, 14), 8).save(OUT / "tug_marker.png")
    if args.plank:
        shrink(trim(key_magenta(Image.open(args.plank))), (64, 12), 16).save(OUT / "tabletop.png")
    if args.mugs:
        sheet = Image.open(args.mugs).convert("RGB")
        # The mug panel of the sheet (lower left), keyed by its own background colour.
        panel = sheet.crop((36, 508, 644, 730))
        bg = panel.getpixel((4, 4))
        mugs = columns(key_colour(panel, bg))
        strip([shrink(mugs[0], (18, 20)), shrink(mugs[1], (18, 20))]).save(OUT / "mug.png")
    print(f"wrote to {OUT}")


if __name__ == "__main__":
    main()
