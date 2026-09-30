"""Generates the Tavern Games sprite strips (dice faces and a card deck) as
small pixel art, drawn at 1x and scaled up by the game with nearest-neighbour
sampling, so they sit next to Stoneshard's own sprites.

    C:\\Python314\\python.exe managed\\Mods\\TavernGames\\assets-src\\make_assets.py

writes managed\\Mods\\TavernGames\\assets\\dice.png (6 frames) and cards.png
(53 frames: 13 ranks x 4 suits in suit-major order, then the back).
"""

from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "assets"

# ------------------------------------------------------------------ dice

DIE = 20
BONE = (226, 214, 184, 255)
BONE_SHADE = (184, 168, 132, 255)
BONE_LIGHT = (246, 238, 214, 255)
INK = (43, 30, 24, 255)
OUTLINE = (30, 22, 18, 255)

# Pip centres on a 3x3 grid, per face.
PIPS = {
    1: [(1, 1)],
    2: [(0, 0), (2, 2)],
    3: [(0, 0), (1, 1), (2, 2)],
    4: [(0, 0), (2, 0), (0, 2), (2, 2)],
    5: [(0, 0), (2, 0), (1, 1), (0, 2), (2, 2)],
    6: [(0, 0), (2, 0), (0, 1), (2, 1), (0, 2), (2, 2)],
}


def die_face(n: int) -> Image.Image:
    im = Image.new("RGBA", (DIE, DIE), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    # Rounded body: outline, bone fill, a light top-left edge and a shaded bottom-right one.
    d.rectangle([1, 0, DIE - 2, DIE - 1], fill=OUTLINE)
    d.rectangle([0, 1, DIE - 1, DIE - 2], fill=OUTLINE)
    d.rectangle([1, 1, DIE - 2, DIE - 2], fill=BONE)
    d.line([2, 1, DIE - 3, 1], fill=BONE_LIGHT)
    d.line([1, 2, 1, DIE - 3], fill=BONE_LIGHT)
    d.line([2, DIE - 2, DIE - 2, DIE - 2], fill=BONE_SHADE)
    d.line([DIE - 2, 2, DIE - 2, DIE - 2], fill=BONE_SHADE)
    if n == 1:
        # The ace gets one big red pip, as on old bone dice.
        c = DIE // 2 - 1
        d.rectangle([c - 2, c - 1, c + 3, c + 2], fill=RED)
        d.rectangle([c - 1, c - 2, c + 2, c + 3], fill=RED)
        return im
    for gx, gy in PIPS[n]:
        cx, cy = 5 + gx * 4 + 1, 5 + gy * 4 + 1
        # A 3x3 pip with its corners knocked off reads as round at this size.
        d.rectangle([cx - 1, cy - 1, cx + 1, cy + 1], fill=INK)
        for px, py in ((cx - 1, cy - 1), (cx + 1, cy - 1), (cx - 1, cy + 1), (cx + 1, cy + 1)):
            im.putpixel((px, py), BONE_SHADE)
    return im


# ------------------------------------------------------------------ cards

CW, CH = 26, 36
PAPER = (232, 220, 190, 255)
PAPER_SHADE = (200, 184, 146, 255)
RED = (150, 36, 30, 255)
BLACK = (34, 28, 26, 255)
BACK = (92, 40, 32, 255)
BACK_LINE = (150, 104, 56, 255)

# A 3x5 pixel font for the ranks ("T" draws as 10 below).
GLYPHS = {
    "A": ["010", "101", "111", "101", "101"],
    "2": ["110", "001", "010", "100", "111"],
    "3": ["110", "001", "010", "001", "110"],
    "4": ["101", "101", "111", "001", "001"],
    "5": ["111", "100", "110", "001", "110"],
    "6": ["011", "100", "110", "101", "010"],
    "7": ["111", "001", "010", "010", "010"],
    "8": ["010", "101", "010", "101", "010"],
    "9": ["010", "101", "011", "001", "110"],
    "1": ["010", "110", "010", "010", "111"],
    "0": ["010", "101", "101", "101", "010"],
    "J": ["001", "001", "001", "101", "010"],
    "Q": ["010", "101", "101", "110", "011"],
    "K": ["101", "110", "100", "110", "101"],
}

# 5x5 suit marks, and 9x9 ones for the middle of the card.
SUITS_SMALL = {
    "hearts": ["01010", "11111", "11111", "01110", "00100"],
    "diamonds": ["00100", "01110", "11111", "01110", "00100"],
    "clubs": ["00100", "01110", "10101", "11111", "00100"],
    "spades": ["00100", "01110", "11111", "11111", "00100"],
}
SUITS_BIG = {
    "hearts": ["011000110", "111101111", "111111111", "111111111", "011111110",
               "001111100", "000111000", "000010000", "000000000"],
    "diamonds": ["000010000", "000111000", "001111100", "011111110", "111111111",
                 "011111110", "001111100", "000111000", "000010000"],
    "clubs": ["000111000", "001111100", "001111100", "110111011", "111111111",
              "110111011", "000010000", "000111000", "001111100"],
    "spades": ["000010000", "000111000", "001111100", "011111110", "111111111",
               "111111111", "011010110", "000010000", "000111000"],
}
SUIT_ORDER = ["clubs", "diamonds", "hearts", "spades"]
RANKS = ["A", "2", "3", "4", "5", "6", "7", "8", "9", "T", "J", "Q", "K"]


def blit(im: Image.Image, pattern: list[str], x: int, y: int, colour) -> None:
    for j, row in enumerate(pattern):
        for i, c in enumerate(row):
            if c == "1":
                im.putpixel((x + i, y + j), colour)


def card_blank(fill) -> tuple[Image.Image, ImageDraw.ImageDraw]:
    im = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rectangle([1, 0, CW - 2, CH - 1], fill=OUTLINE)
    d.rectangle([0, 1, CW - 1, CH - 2], fill=OUTLINE)
    d.rectangle([1, 1, CW - 2, CH - 2], fill=fill)
    return im, d


def card_face(rank: str, suit: str) -> Image.Image:
    im, d = card_blank(PAPER)
    d.line([2, CH - 2, CW - 2, CH - 2], fill=PAPER_SHADE)
    d.line([CW - 2, 2, CW - 2, CH - 2], fill=PAPER_SHADE)
    ink = RED if suit in ("hearts", "diamonds") else BLACK
    # Rank and small suit in the top-left corner, mirrored in the bottom-right.
    if rank == "T":
        blit(im, GLYPHS["1"], 3, 3, ink)
        blit(im, GLYPHS["0"], 7, 3, ink)
    else:
        blit(im, GLYPHS[rank], 3, 3, ink)
    blit(im, SUITS_SMALL[suit], 3, 10, ink)
    corner = im.crop((3, 3, 12, 15)).rotate(180)
    im.alpha_composite(corner, (CW - 12, CH - 15))
    blit(im, SUITS_BIG[suit], (CW - 9) // 2, (CH - 9) // 2, ink)
    # Court cards get a frame round the middle mark, so they read apart from the pips.
    if rank in ("J", "Q", "K"):
        d.rectangle([8, 11, CW - 9, CH - 12], outline=ink)
    return im


def card_back() -> Image.Image:
    im, d = card_blank(BACK)
    d.rectangle([3, 3, CW - 4, CH - 4], outline=BACK_LINE)
    for y in range(6, CH - 6, 4):
        for x in range(6 + (y // 4) % 2 * 2, CW - 6, 4):
            im.putpixel((x, y), BACK_LINE)
    return im


def strip(frames: list[Image.Image]) -> Image.Image:
    w, h = frames[0].size
    out = Image.new("RGBA", (w * len(frames), h), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        out.alpha_composite(f, (i * w, 0))
    return out


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    strip([die_face(n) for n in range(1, 7)]).save(OUT / "dice.png")
    cards = [card_face(r, s) for s in SUIT_ORDER for r in RANKS] + [card_back()]
    strip(cards).save(OUT / "cards.png")
    print(f"wrote {OUT / 'dice.png'} and {OUT / 'cards.png'}")


if __name__ == "__main__":
    main()
