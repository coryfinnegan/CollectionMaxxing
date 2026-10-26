#!/usr/bin/env python3
"""Castle theme textures (#361): small tileable and 9-slice PNGs with baked contact shadows.

Regenerate: python3 scripts/castle-textures.py (repo root: app/). Deterministic (seeded);
base colors come from src/theme/tokens.json (castle section) so the art can't drift from
the tokens. No text is baked into any asset; nothing here is a background image, only
small tiles (fills), frame pieces (edges/corners with baked bevel + inner shadow) and
ornament strips (crenellations, ledge).

Layout (all RGB, 8-bit, no interlace):
  <surface>-fill.png    64x64  seamless grain tile (per-pixel noise + integer-frequency
                           blotches, so it wraps exactly)
  <surface>-edge-h.png  64x12  top/bottom frame rail: bevel + inner shadow on the content side
  <surface>-edge-v.png  12x64  left/right frame rail (same treatment, rotated)
  <surface>-corner.png  16x16  frame corner (shadow on the inner sides)
  crenel-strip.png      48x24  CastleHeader top: repeating merlons, tileable in X
  ledge-strip.png       32x16  divider ledge, tileable in X, baked shadow under the lip
Surfaces: parchment, stone, iron (iron has a fill only; BannerMarker is frameless).
Night (#363): stone-<piece>-night.png, crenel-strip-night.png and ledge-strip-night.png
regenerate the stone pieces from the castleDark bases for the torchlit hall.
Parchment stays lit and iron stays put, so both schemes share that art.
"""
import json
import math
import os
import random
import sys

SEED = 361
HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.dirname(HERE)
OUT = os.path.join(APP, "assets", "castle")

try:
    from PIL import Image
except ImportError:
    sys.exit("castle-textures: Pillow is required (pip install pillow)")

with open(os.path.join(APP, "src", "theme", "tokens.json"), encoding="utf-8") as f:
    LOADED = json.load(f)
DAY = LOADED["castle"]
NIGHT = LOADED["castleDark"]

EDGE = 12  # frame rail width; content padding in components must exceed this
CORNER = 12  # matches the rail so the 9-slice grid lines up


def hex_to_rgb(s):
    s = s.lstrip("#")
    return tuple(int(s[i : i + 2], 16) for i in (0, 2, 4))


def clamp(v):
    return max(0, min(255, int(round(v))))


def shade(rgb, amt):
    return tuple(clamp(c + amt) for c in rgb)


def blotch(x, y, w, h, rng):
    """Seamless large-scale variation: integer-frequency sines wrap exactly."""
    a1, a2 = rng.uniform(-7, 7), rng.uniform(-7, 7)
    f1, f2 = rng.choice([2, 3, 4]), rng.choice([2, 3, 4])
    p1, p2 = rng.uniform(0, 6.28), rng.uniform(0, 6.28)
    return a1 * math.sin(2 * math.pi * (f1 * x / w) + p1) * math.sin(2 * math.pi * (f1 * y / h) + p1) + a2 * math.sin(
        2 * math.pi * (f2 * x / w + f2 * y / h) + p2
    )


def seed_for(tag, *parts):
    # Day seeds keep their Phase 0 strings exactly, so regenerating never churns
    # the committed day art; night generations take their own seed namespace.
    return f"{SEED}:{':'.join(parts)}" if tag == "day" else f"{SEED}:night:{':'.join(parts)}"


def make_fill(base_key, grain, blotches, size=64, palette=DAY, tag="day"):
    base = hex_to_rgb(palette[base_key])
    rng = random.Random(seed_for(tag, base_key, "fill"))
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            n = rng.uniform(-grain, grain) + sum(blotch(x, y, size, size, rng) for _ in range(blotches))
            px[x, y] = shade(base, n)
    return img


def frame_shade(t):
    """Bevel + contact shadow across a rail, t=0 outer edge .. t=1 content side."""
    # Outer lip highlight, then body, then inner shadow biting into the content side.
    if t < 0.18:
        return 14 * (1 - t / 0.18)
    if t > 0.62:
        k = (t - 0.62) / 0.38
        return -34 * k * k
    return 0


def make_edge_h(base_key, grain, palette=DAY, tag="day"):
    base = hex_to_rgb(palette[base_key])
    rng = random.Random(seed_for(tag, base_key, "edge-h"))
    w, h = 64, EDGE
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            # Top rail: outer edge at the top. Bottom rail reuses this art flipped.
            n = rng.uniform(-grain, grain) + blotch(x, y, w, h * 4, rng) * 0.4
            px[x, y] = shade(base, frame_shade(y / (h - 1)) + n)
    return img


def make_edge_v(base_key, grain, palette=DAY, tag="day"):
    # Same treatment rotated: outer edge at the left; right rail flips it.
    return make_edge_h(base_key, grain, palette, tag).transpose(Image.ROTATE_90).resize((EDGE, 64))


def make_corner(base_key, grain, palette=DAY, tag="day"):
    base = hex_to_rgb(palette[base_key])
    rng = random.Random(seed_for(tag, base_key, "corner"))
    img = Image.new("RGB", (CORNER, CORNER))
    px = img.load()
    for y in range(CORNER):
        for x in range(CORNER):
            # Top-left corner: outer edges at top/left; other corners flip this art.
            n = rng.uniform(-grain, grain)
            px[x, y] = shade(base, min(frame_shade(y / (CORNER - 1)), frame_shade(x / (CORNER - 1))) + n)
    return img


def make_crenel(palette=DAY, tag="day"):
    """Repeating merlons (period 16px) over a stone band, with baked lip shadows."""
    base = hex_to_rgb(palette["stone"])
    dark = hex_to_rgb(palette["iron"])
    rng = random.Random(seed_for(tag, "crenel"))
    w, h = 48, 24
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            period = x % 16
            merlon = period < 10  # 10px block, 6px gap
            n = rng.uniform(-6, 6)
            if y < 13 and not merlon:
                # Gap: recessed, in shadow; deeper toward the top.
                px[x, y] = shade(base, -46 + y * 1.2 + n * 0.4)
            elif y < 13:
                # Merlon face: lit top, lip shadow on its right edge and base.
                lip = -22 if period >= 8 else 0
                foot = -16 if y >= 11 else 0
                px[x, y] = shade(base, 10 - y * 0.5 + lip + foot + n)
            else:
                # Band under the teeth: shadow cast by the merlons fades downward.
                px[x, y] = shade(base, -30 * max(0, 1 - (y - 13) / 6) + n)
    # Iron pin line seating the strip into the header.
    for x in range(w):
        px[x, h - 1] = shade(dark, rng.uniform(-4, 4))
    return img


def make_ledge(palette=DAY, tag="day"):
    """A divider ledge: lit lip, body, baked contact shadow under it."""
    base = hex_to_rgb(palette["stone"])
    rng = random.Random(seed_for(tag, "ledge"))
    w, h = 32, 16
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            n = rng.uniform(-5, 5)
            if y == 0:
                px[x, y] = shade(base, 16 + n * 0.5)  # lit lip
            elif y < 6:
                px[x, y] = shade(base, 6 - y + n)
            elif y < 9:
                px[x, y] = shade(base, -18 + n * 0.6)  # shadow under the lip
            else:
                k = (y - 9) / (h - 1 - 9)  # fading contact shadow
                px[x, y] = shade(base, -18 * (1 - k) * (1 - k) + n * 0.5)
    return img


def extremes(img):
    px = img.load()
    pixels = [px[x, y] for y in range(img.size[1]) for x in range(img.size[0])]
    lum = [0.2126 * r + 0.7152 * g + 0.0722 * b for r, g, b in pixels]
    return pixels[lum.index(max(lum))], pixels[lum.index(min(lum))]


def main():
    os.makedirs(OUT, exist_ok=True)
    jobs = {
        "parchment-fill.png": make_fill("parchment", 7, 2),
        "parchment-edge-h.png": make_edge_h("parchment", 6),
        "parchment-edge-v.png": make_edge_v("parchment", 6),
        "parchment-corner.png": make_corner("parchment", 6),
        "stone-fill.png": make_fill("stone", 7, 2),
        "stone-edge-h.png": make_edge_h("stone", 6),
        "stone-edge-v.png": make_edge_v("stone", 6),
        "stone-corner.png": make_corner("stone", 6),
        "iron-fill.png": make_fill("iron", 6, 1),
        "crenel-strip.png": make_crenel(),
        "ledge-strip.png": make_ledge(),
        "stone-night-fill.png": make_fill("stone", 7, 2, palette=NIGHT, tag="night"),
        "stone-night-edge-h.png": make_edge_h("stone", 6, palette=NIGHT, tag="night"),
        "stone-night-edge-v.png": make_edge_v("stone", 6, palette=NIGHT, tag="night"),
        "stone-night-corner.png": make_corner("stone", 6, palette=NIGHT, tag="night"),
        "crenel-strip-night.png": make_crenel(palette=NIGHT, tag="night"),
        "ledge-strip-night.png": make_ledge(palette=NIGHT, tag="night"),
    }
    for name, img in jobs.items():
        path = os.path.join(OUT, name)
        img.save(path, optimize=True)
        light, dark = extremes(img)
        print(f"{name}: {img.size[0]}x{img.size[1]} {os.path.getsize(path)}B lightest=#{light[0]:02x}{light[1]:02x}{light[2]:02x} darkest=#{dark[0]:02x}{dark[1]:02x}{dark[2]:02x}")


if __name__ == "__main__":
    main()
