# Turns the delivered relic artwork (large renders on a flat magenta key) into the
# inventory sprites the mod loads: assets/<id>.png, sized to the relic's carrier
# footprint (27 px per inventory cell).
#
#   C:\Python314\python.exe process-sprites.py --src "C:\...\out"
#
# The interesting part is the keying. The sources are lossy-compressed, so the key
# is not one colour but a tight cloud around one, and the anti-aliased rim of every
# outline is a genuine blend of black and magenta - a plain distance threshold calls
# those pixels "art" and leaves a dark purple fringe all the way round. So instead:
#
#   1. Sample the key from the image itself (border first, then refined over the
#      whole key cluster) and measure how far compression spreads the cloud.
#   2. Measure each pixel's *spill*: how far R and B rise above G, which is what
#      this magenta key contributes and nothing in this artwork does. Since spill
#      is linear in the channels, an observed P = a*C + (1-a)*K has
#      spill(P) = a*spill(C) + (1-a)*spill(K), so for art with no magenta in it
#      alpha = 1 - spill(P)/spill(K) is not an estimate - it is exact.
#   3. Un-mix: C = (P - (1-a)*K)/a recovers the true colour of every blended pixel,
#      and a final despill mops up what compression noise leaves behind.
#   4. Trim to the content, and downscale in premultiplied *linear* light so the
#      edges neither darken nor drag the key colour back in.
#
# Step 2 only holds where the art really has no key hue, so it is applied only in a
# band hugging the background - which is also the only place spill can land.
# Everything further in is opaque by fiat. That is what keeps the Echoing Bell's
# violet echo (the one genuinely magenta-ish thing here) from dissolving.
#
# The Echoing Bell is a special case: the sprite holds the real bell plus a purple
# "echo" of it, and the echo is meant to read as a ghost. The two overlap, so they
# are not separable as connected components - but the echo is the only purple thing
# in the frame, so it is picked out by hue and its alpha is halved.
import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image

CELL = 27

# source stem (without "_sprite") -> relic id, carrier size in cells.
# Relics with no delivered art (stavebound_ember, gorgoneion, wolfs_heart,
# copper_ring) are absent on purpose: their placeholders stay.
RELICS = [
    ("censer-of-the-drowned-choir", "censer", 1, 2),
    ("cinder-rosary", "cinder_rosary", 1, 2),
    ("faceless-mirror", "faceless_mirror", 1, 2),
    ("grafted-hand-of-the-hanged-man", "grafted_hand", 1, 2),
    ("lodestone-idol", "lodestone_idol", 1, 2),
    ("oath-stone-of-the-deep-road", "oath_stone", 1, 2),
    ("pallbearer-s-coin", "pallbearers_coin", 1, 2),
    ("pilgrim-s-millstone", "pilgrims_millstone", 1, 2),
    ("reliquary-of-saint-mardun", "reliquary_of_saint_mardun", 1, 2),
    ("the-debtor-s-knot", "debtors_knot", 1, 2),
    ("the-echoing-bell", "echoing_bell", 1, 2),
    ("the-iron-lung", "iron_lung", 1, 2),
    ("the-sated-worm", "sated_worm", 1, 2),
    ("the-split-quiver", "split_quiver", 1, 2),
    ("the-sundered-gate", "sundered_gate", 1, 2),
    ("the-surveyor-s-chain", "surveyors_chain", 1, 2),
    ("the-usurer-s-scale", "usurers_scale", 1, 2),
    ("the-weeping-candle", "weeping_candle", 1, 2),
    ("vessel-of-borrowed-years", "vessel_of_borrowed_years", 1, 2),
]

# Alpha below this is haze, not art; it is snapped to zero so the sprite does not
# carry a faint rectangle of nearly-invisible pixels.
ALPHA_FLOOR = 0.04
# How wide the "touching the background" band is: at least this many pixels, and
# more for very large sources. It has to cover the blended rim plus the chroma
# bleed that subsampling smears a few pixels past it. Erring wide is cheap - inside
# the band a pixel with no key hue still keys out to fully opaque - and narrowing it
# to 2 px was what left a plum cast on the Surveyor's Chain.
BAND_PIXELS = 8
BAND_FRACTION = 1 / 128


# ---------------------------------------------------------------- key removal

def _dilate(mask, radius):
    """Binary dilation with a plus-shaped element, applied `radius` times."""
    out = mask
    for _ in range(radius):
        grown = out.copy()
        grown[1:] |= out[:-1]
        grown[:-1] |= out[1:]
        grown[:, 1:] |= out[:, :-1]
        grown[:, :-1] |= out[:, 1:]
        out = grown
    return out


def sample_key(rgb):
    """The key colour, and how far the compression noise spreads it."""
    h, w, _ = rgb.shape
    edge = max(2, min(h, w) // 128)
    border = np.concatenate([
        rgb[:edge].reshape(-1, 3), rgb[-edge:].reshape(-1, 3),
        rgb[:, :edge].reshape(-1, 3), rgb[:, -edge:].reshape(-1, 3),
    ])
    # The border is mostly key even when the art bleeds off the canvas, so a median
    # lands on the key; then refine over every pixel that clusters around it.
    coarse = np.median(border, axis=0)
    near = np.linalg.norm(rgb - coarse, axis=2) < 0.25
    key = np.median(rgb[near], axis=0)

    # Measure the noise over the tight core only. Widening this window drags in the
    # blended rim of the artwork, which inflates the estimate until the background
    # threshold starts reaching for real colours.
    core = np.linalg.norm(rgb - key, axis=2) < 0.15
    spread = float(np.percentile(np.linalg.norm(rgb[core] - key, axis=1), 99.9))
    return key, spread


def key_spill(rgb, key):
    """How much of the key's own colour cast a pixel carries.

    A magenta key lifts R and B together above G; a green one lifts G above both.
    Nothing in this artwork does either (the Echoing Bell's violet echo comes
    closest, and even it keeps far more green than the key), so this quantity
    reads as "how much key is mixed in here".
    """
    if key[1] < min(key[0], key[2]):
        return np.clip(np.minimum(rgb[..., 0], rgb[..., 2]) - rgb[..., 1], 0.0, None), (0, 2)
    return np.clip(rgb[..., 1] - np.maximum(rgb[..., 0], rgb[..., 2]), 0.0, None), (1,)


def key_alpha(rgb, key, spread):
    """Alpha from the key's spill, but only where the background can reach."""
    h, w, _ = rgb.shape
    dist = np.linalg.norm(rgb - key, axis=2).astype(np.float32)

    # Pure key, allowing for the measured compression spread. Tested per pixel
    # rather than by flooding from the border, so key trapped inside a closed shape
    # (the loops of the Debtor's Knot, the gaps between the Split Quiver's arrows)
    # is cut out too.
    background = dist < max(spread * 1.3, 0.05)
    reach = max(BAND_PIXELS, round(min(h, w) * BAND_FRACTION))
    band = _dilate(background, reach) & ~background

    spill, _ = key_spill(rgb, key)
    full, _ = key_spill(key[None, None, :], key)
    blended = np.clip(1.0 - spill / max(float(full[0, 0]), 1e-6), 0.0, 1.0)

    alpha = np.ones((h, w), dtype=np.float32)
    alpha[band] = blended[band]
    alpha[background] = 0.0
    return alpha, dist, band


def unmix(rgb, alpha, key):
    """Undo the key that is blended into every partially transparent pixel."""
    out = rgb.copy()
    partial = (alpha > 0.0) & (alpha < 1.0)
    a = alpha[partial][:, None]
    out[partial] = np.clip((rgb[partial] - (1.0 - a) * key) / a, 0.0, 1.0)
    return out


def despill(rgb, alpha, band, key):
    """Mop up whatever key tint survives the un-mix.

    With an exact alpha the un-mix leaves nothing behind, but the key is not one
    colour - compression jitters it - so a little cast is left on the rim. Removing
    the residual `min(R,B) - G` from both channels neutralises it. Confined to the
    same band as the keying, so art further in keeps its own colour.
    """
    excess, channels = key_spill(rgb, key)
    strength = (band & (alpha > 0.0)).astype(np.float32)
    out = rgb.copy()
    for c in channels:
        out[..., c] = np.clip(out[..., c] - excess * strength, 0.0, 1.0)
    return out


# ------------------------------------------------------------ the echo bell

def fade_echo(rgb, alpha, factor=0.5):
    """Halve the alpha of the purple "echo" bell, leaving the real bell opaque.

    The two bells overlap, so they are one connected blob; what separates them is
    hue. The echo is violet (R and B well above G); the real bell is bronze (G
    above B). The echo's dark outline is not purple itself, so pixels that are
    both dark and hugging the purple mask are folded in.
    """
    solid = alpha > 0.5
    purple = ((np.minimum(rgb[..., 0], rgb[..., 2]) - rgb[..., 1]) > 0.10) & solid
    if not purple.any():
        return alpha, 0
    luma = rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    echo = purple | (_dilate(purple, 4) & (luma < 0.30) & solid)
    faded = alpha.copy()
    faded[echo] *= factor
    return faded, int(echo.sum())


# ------------------------------------------------------------------ resizing

def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.clip(c, 0, None) ** (1 / 2.4) - 0.055)


def trim(rgb, alpha):
    """Crop to the content bounding box, ignoring sub-visible haze and specks.

    A few sources have a pinhole of key showing through the art (a two-pixel gap in
    the Iron Lung's ribs), which compression smears enough that it survives keying
    as a faint speck. Left in, one such speck out at the edge of the frame pushes
    the bounding box wide and shrinks the whole relic inside its 27x54 cell. So the
    box is measured on an eroded mask: anything thinner than a few source pixels
    does not get a vote. The crop itself still keeps every pixel.
    """
    solid = alpha > ALPHA_FLOOR
    if not solid.any():
        return rgb, alpha
    speck = 3
    substantial = ~_dilate(~solid, speck)
    if substantial.any():
        solid = substantial
    else:
        speck = 0
    h, w = alpha.shape
    rows = np.flatnonzero(solid.any(axis=1))
    cols = np.flatnonzero(solid.any(axis=0))
    # Give back what the erosion took, so the crop is the real content box.
    y0, y1 = max(0, rows[0] - speck), min(h, rows[-1] + 1 + speck)
    x0, x1 = max(0, cols[0] - speck), min(w, cols[-1] + 1 + speck)
    return rgb[y0:y1, x0:x1], alpha[y0:y1, x0:x1]


def resize_rgba(rgb, alpha, size, method):
    """Downscale in premultiplied linear light, so edges keep their colour.

    Resampling straight sRGB with un-premultiplied colour is what produces dark
    halos and key-coloured fringes: the fully transparent pixels still hold the
    key's RGB and get averaged in. Premultiplying makes them contribute nothing.
    """
    lin = srgb_to_linear(rgb).astype(np.float32)
    pre = lin * alpha[..., None]
    filt = {
        "box": Image.BOX, "area": Image.BOX, "lanczos": Image.LANCZOS,
        "bicubic": Image.BICUBIC, "bilinear": Image.BILINEAR, "nearest": Image.NEAREST,
    }[method]

    planes = [
        np.asarray(Image.fromarray(pre[..., c]).resize(size, filt), dtype=np.float32)
        for c in range(3)
    ]
    out_a = np.asarray(Image.fromarray(alpha).resize(size, filt), dtype=np.float32)
    out_a = np.clip(out_a, 0.0, 1.0)
    out_a[out_a < ALPHA_FLOOR] = 0.0

    pre_small = np.stack(planes, axis=-1)
    safe = np.maximum(out_a, 1e-6)[..., None]
    out_rgb = linear_to_srgb(np.clip(pre_small / safe, 0.0, None))
    out_rgb = np.clip(out_rgb, 0.0, 1.0)
    out_rgb[out_a == 0.0] = 0.0
    return out_rgb.astype(np.float32), out_a


def fit_into(rgb, alpha, box, method):
    """Scale to fit `box` keeping aspect, then centre on a transparent canvas."""
    bw, bh = box
    h, w = alpha.shape
    scale = min(bw / w, bh / h)
    tw, th = max(1, round(w * scale)), max(1, round(h * scale))
    small_rgb, small_a = resize_rgba(rgb, alpha, (tw, th), method)

    canvas_rgb = np.zeros((bh, bw, 3), dtype=np.float32)
    canvas_a = np.zeros((bh, bw), dtype=np.float32)
    x0, y0 = (bw - tw) // 2, (bh - th) // 2
    canvas_rgb[y0:y0 + th, x0:x0 + tw] = small_rgb
    canvas_a[y0:y0 + th, x0:x0 + tw] = small_a
    return canvas_rgb, canvas_a


# ------------------------------------------------------------------ pipeline

def to_image(rgb, alpha):
    data = np.concatenate([rgb, alpha[..., None]], axis=-1)
    return Image.fromarray(np.round(data * 255.0).astype(np.uint8), "RGBA")


def hue_of(rgb):
    """Hue in degrees and chroma, for the residue test."""
    top, bottom = rgb.max(axis=-1), rgb.min(axis=-1)
    chroma = top - bottom
    safe = np.where(chroma > 0, chroma, 1.0)
    sector = np.where(
        top == rgb[..., 0], ((rgb[..., 1] - rgb[..., 2]) / safe) % 6.0,
        np.where(top == rgb[..., 1], (rgb[..., 2] - rgb[..., 0]) / safe + 2.0,
                 (rgb[..., 0] - rgb[..., 1]) / safe + 4.0))
    return np.where(chroma > 0, sector * 60.0, 0.0), chroma


def count_key_residue(rgb, alpha, key):
    """Visible pixels that still carry the key: near its colour, or near its hue.

    The hue test is the strict one and the one worth watching. A fringe does not
    survive as bright magenta - it survives as a *dark* magenta along the outlines,
    which a colour-distance test waves through because black-mixed-with-magenta is
    far from magenta. Hue does not care how dark the pixel is.
    """
    visible = alpha > 0.0
    near = (np.linalg.norm(rgb - key, axis=2) < 0.5) & visible

    hue, chroma = hue_of(rgb)
    key_hue, _ = hue_of(key[None, None, :])
    off = np.abs((hue - float(key_hue[0, 0]) + 180.0) % 360.0 - 180.0)
    same_hue = (off < 35.0) & (chroma > 0.15) & visible
    return int(near.sum()), int(same_hue.sum())


def process(path, box, method, fade_echo_bell):
    rgb = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0
    key, spread = sample_key(rgb)
    alpha, dist, band = key_alpha(rgb, key, spread)
    alpha[alpha < ALPHA_FLOOR] = 0.0

    rgb = unmix(rgb, alpha, key)
    rgb = despill(rgb, alpha, band, key)

    echo_px = 0
    if fade_echo_bell:
        alpha, echo_px = fade_echo(rgb, alpha)

    # How much empty room sits between the key cluster and the artwork. Read off a
    # low percentile rather than the minimum, because a stray pinhole of key inside
    # the art is one pixel that would otherwise speak for the whole sprite.
    opaque = dist[alpha >= 0.999]
    gap = float(np.percentile(opaque, 0.05)) if opaque.size else 0.0

    rgb, alpha = trim(rgb, alpha)
    out_rgb, out_a = fit_into(rgb, alpha, box, method)

    near, same_hue = count_key_residue(out_rgb, out_a, key)
    stats = {
        "key": np.round(key * 255).astype(int).tolist(),
        "spread": spread,
        "gap": gap,
        "content": tuple(reversed(alpha.shape)),
        "echo_px": echo_px,
        "near_key": near,
        "key_hue": same_hue,
        "opaque": int((out_a > 0.5).sum()),
    }
    return to_image(out_rgb, out_a), stats


# ------------------------------------------------------------------- preview

def _checker(size, a=(90, 90, 96), b=(64, 64, 70), square=4):
    w, h = size
    ys, xs = np.mgrid[0:h, 0:w]
    pick = ((xs // square) + (ys // square)) % 2
    img = np.where(pick[..., None] == 0, np.array(a), np.array(b))
    return Image.fromarray(img.astype(np.uint8), "RGB")


def _over(sprite, bg, zoom):
    w, h = sprite.size
    big = sprite.resize((w * zoom, h * zoom), Image.NEAREST)
    plate = bg.copy() if isinstance(bg, Image.Image) else Image.new("RGB", big.size, bg)
    plate.paste(big, (0, 0), big)
    return plate


def build_preview(sprites, out_path):
    """Contact sheet: every sprite at 4x over the inventory's dark, over light and
    over a checkerboard, plus a 1x strip - the size the game actually draws."""
    from PIL import ImageDraw

    DARK, LIGHT = (29, 26, 36), (216, 212, 204)
    zoom, cols = 4, 5
    cw, ch = CELL * zoom + 10, CELL * 2 * zoom + 24
    rows = (len(sprites) + cols - 1) // cols
    panel_w, panel_h = cols * cw, rows * ch

    sheet = Image.new("RGB", (panel_w * 3 + 40, panel_h + 180), (24, 24, 28))
    draw = ImageDraw.Draw(sheet)

    for panel, (label, bg) in enumerate([
        ("4x over inventory dark", DARK),
        ("4x over light", LIGHT),
        ("4x over checker", None),
    ]):
        ox = panel * (panel_w + 20)
        draw.text((ox + 4, 4), label, fill=(255, 255, 255))
        for i, (rid, sprite) in enumerate(sprites):
            w, h = sprite.size
            back = _checker((w * zoom, h * zoom)) if bg is None else bg
            tile = _over(sprite, back, zoom)
            x = ox + (i % cols) * cw + 5
            y = 22 + (i // cols) * ch + 14
            sheet.paste(tile, (x, y))
            draw.rectangle([x - 1, y - 1, x + tile.width, y + tile.height], outline=(70, 70, 80))
            draw.text((x, y - 12), rid[:18], fill=(190, 190, 200))

    # The row that matters: 1x, the size the inventory draws.
    y = panel_h + 40
    draw.text((6, y - 16), "1x (game size) - dark, then light", fill=(255, 255, 255))
    for shade, oy in ((DARK, 0), (LIGHT, CELL * 2 + 8)):
        x = 8
        for rid, sprite in sprites:
            sheet.paste(_over(sprite, shade, 1), (x, y + oy))
            x += sprite.width + 6
    out_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out_path)
    return out_path


# ---------------------------------------------------------------------- main

def main():
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--src", default=r"C:\Users\forevkassh\Downloads\out",
                    help="folder holding <name>_sprite.png")
    ap.add_argument("--out", default=str(here), help="where <id>.png goes")
    # box/bicubic were tried side by side at 27x54: both read softer, and at this
    # size the art has no detail to spare. Lanczos keeps the outlines readable and
    # shows no ringing worth the name, because it runs on premultiplied linear data.
    ap.add_argument("--method", default="lanczos",
                    choices=["box", "area", "lanczos", "bicubic", "bilinear", "nearest"],
                    help="downscale filter (lanczos reads crispest at this size)")
    ap.add_argument("--preview", default=None,
                    help="also write a contact sheet here")
    ap.add_argument("--dry-run", action="store_true", help="report, write nothing")
    args = ap.parse_args()

    src, out = Path(args.src), Path(args.out)
    if not src.is_dir():
        sys.exit(f"source folder not found: {src}")

    sprites, missing = [], []
    print(f"{'id':26s} {'key':>14s} {'gap':>5s} {'source':>12s} "
          f"{'near-key':>8s} {'key-hue':>7s}")
    for stem, rid, cw, chh in RELICS:
        path = src / f"{stem}_sprite.png"
        if not path.exists():
            missing.append(stem)
            continue
        box = (cw * CELL, chh * CELL)
        img, st = process(path, box, args.method, fade_echo_bell=(rid == "echoing_bell"))
        if not args.dry_run:
            img.save(out / f"{rid}.png")
        sprites.append((rid, img))
        print(f"{rid:26s} {str(st['key']):>14s} {st['gap']:5.2f} "
              f"{st['content'][0]:5d}x{st['content'][1]:<6d} {st['near_key']:8d} {st['key_hue']:7d}"
              + (f"   (the echo, faded over {st['echo_px']} source px)"
                 if st["echo_px"] else ""))

    print("\nnear-key / key-hue are leftover purple: both should be 0. The Echoing"
          "\nBell is the exception - its ghost is genuinely violet, and is counted.")
    print("gap is the colour distance from the key to the artwork's nearest opaque"
          "\ncolour; a large gap means the keying never came near the artwork.")
    if missing:
        print("\nno source art (placeholders kept):", ", ".join(missing))
    if args.preview:
        print("preview:", build_preview(sprites, Path(args.preview)))


if __name__ == "__main__":
    main()
