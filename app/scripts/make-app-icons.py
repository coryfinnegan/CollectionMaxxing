#!/usr/bin/env python3
"""App icon family (#364): the dragon master as the app icon, favicon and Android
default set, plus the Game Boy alternate for iOS and its Android set.

Regenerate: python3 scripts/make-app-icons.py [--master PATH] (repo root: app/).
Deterministic. The master is the 1024px dragon Cory approved, attached to issue
#364; by default it is downloaded from the issue's attachment URL. The Game Boy
alternate keeps the master's composition and paints it in the classic DMG four
greens (the attached mock governs the mapping: light background, dark line art,
fills ordered by brightness).

Layout (all 8-bit, no interlace):
  images/icon.png                    1024  RGB   dragon master, baked white corners filled
  images/favicon.png                   48  RGB   dragon for the web
  icons/icon-gameboy.png             1024  RGB   alternate master, exactly the four greens
  icons/icon-gameboy@2x.png           120  RGB   iOS alternate @2x (smooth downscale)
  icons/icon-gameboy@3x.png           180  RGB   iOS alternate @3x (smooth downscale)
  images/android-icon-foreground.png  512  RGBA  dragon art, background punched out
  images/android-icon-background.png  512  RGB   solid background
  images/android-icon-monochrome.png  432  RGBA  white art silhouette
  icons/android-gameboy-*.png               same three for the Game Boy variant
"""
import os
import random
import sys
import tempfile
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.dirname(HERE)
IMAGES = os.path.join(APP, "assets", "images")
ICONS = os.path.join(APP, "assets", "icons")
MASTER_URL = "https://github.com/user-attachments/assets/bd9e8ff8-f1db-4135-9d1d-a23951cfcb71"

# Classic Game Boy four-shade green palette: darkest line art, two fills, background.
DARKEST = (0x0F, 0x38, 0x0F)
DARK_MID = (0x30, 0x62, 0x30)
LIGHT_MID = (0x8B, 0xAC, 0x0F)
LIGHTEST = (0x9B, 0xBC, 0x0F)

try:
    from PIL import Image, ImageFilter
except ImportError:
    sys.exit("make-app-icons: Pillow is required (pip install pillow)")


def load_master(path=None):
    if path:
        return Image.open(path).convert("RGB")
    with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tmp:
        urllib.request.urlretrieve(MASTER_URL, tmp.name)
        return Image.open(tmp.name).convert("RGB")


def background_sample(master):
    px = master.load()
    vals = sorted(px[x, y][0] for y in range(2, 40) for x in range(400, 620))
    v = vals[len(vals) // 2]
    return (v, v, v)


def fill_corners(master, bg):
    """The master has white rounded corners baked in; an app icon must be a
    full-bleed opaque square, so flood the corner regions with the background."""
    w, h = master.size
    px = master.load()
    filled = Image.new("1", (w, h), 0)
    fpx = filled.load()
    stack = [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]
    while stack:
        x, y = stack.pop()
        if not (0 <= x < w and 0 <= y < h) or fpx[x, y]:
            continue
        # Background peaks at 32; everything brighter out here is corner or its blend ring.
        if min(px[x, y]) <= 34:
            continue
        fpx[x, y] = 1
        stack.extend([(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)])
    filled = filled.filter(ImageFilter.MaxFilter(5))  # eat the 1-2px blend fringe too
    inside = filled.crop((200, 200, 824, 824)).getbbox()
    assert inside is None, "corner fill leaked into the artwork"
    out = master.copy()
    opx = out.load()
    fpx = filled.load()
    grain = random.Random(364)  # the background grain runs 23-32; a flat fill would rim the corners
    for y in range(h):
        for x in range(w):
            if fpx[x, y]:
                opx[x, y] = (bg[0] + grain.randint(-4, 4), bg[1] + grain.randint(-4, 4), bg[2] + grain.randint(-4, 4))
    return out


def luminance(image):
    r, g, b = image.split()
    return Image.merge("RGB", (r, g, b)).convert("L", (0.299, 0.587, 0.114, 0))


def background_mask(master):
    """Background = dark pixels connected to the edges. The near-black line art
    would leak the flood into dark interiors, so erode the dark mask first (thin
    outlines vanish, the broad background survives), flood, then dilate less than
    the erosion so outlines keep their weight."""
    lum = luminance(master)
    dark = lum.point(lambda v: 255 if v < 70 else 0).convert("1")
    eroded = dark.filter(ImageFilter.MinFilter(7))
    w, h = master.size
    epx = eroded.load()
    seen = Image.new("1", (w, h), 0)
    spx = seen.load()
    stack = [(x, 0) for x in range(w)] + [(x, h - 1) for x in range(w)] + [(0, y) for y in range(h)] + [(w - 1, y) for y in range(h)]
    while stack:
        x, y = stack.pop()
        if not (0 <= x < w and 0 <= y < h) or spx[x, y] or not epx[x, y]:
            continue
        spx[x, y] = 1
        stack.extend([(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)])
    grown = seen.filter(ImageFilter.MaxFilter(3))
    bg = Image.new("1", (w, h), 0)
    bpx, gpx, dpx = bg.load(), grown.load(), dark.load()
    for y in range(h):
        for x in range(w):
            bpx[x, y] = 1 if gpx[x, y] and dpx[x, y] else 0
    return bg


def recolor_gameboy(master):
    """Background to the lightest green, line art to the darkest, fills ordered by
    brightness across the two mids (cream pages and the orange belly land lightest,
    like the reference mock)."""
    w, h = master.size
    lum = luminance(master).load()
    bpx = background_mask(master).load()
    out = Image.new("RGB", (w, h))
    opx = out.load()
    for y in range(h):
        for x in range(w):
            if bpx[x, y]:
                opx[x, y] = LIGHTEST
                continue
            v = lum[x, y]
            opx[x, y] = DARKEST if v < 45 else DARK_MID if v < 105 else LIGHT_MID if v < 165 else LIGHTEST
    return out


def art_alpha(master, size):
    """Soft art silhouette for adaptive foregrounds and monochromes."""
    bg = background_mask(master)
    art = bg.point(lambda v: 0 if v else 255).convert("L")
    return art.resize((size, size), Image.LANCZOS).filter(ImageFilter.GaussianBlur(0.7))


def save(image, *parts):
    path = os.path.join(APP, "assets", *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.save(path)
    print("wrote %s %s %s" % (os.path.relpath(path, APP), image.size, image.mode))


def main():
    master_arg = sys.argv[sys.argv.index("--master") + 1] if "--master" in sys.argv else None
    master = load_master(master_arg)
    assert master.size == (1024, 1024), "the master must be 1024x1024, got %s" % (master.size,)
    bg = background_sample(master)
    full = fill_corners(master, bg)
    gameboy = recolor_gameboy(full)

    save(full, "images", "icon.png")
    save(full.resize((48, 48), Image.LANCZOS), "images", "favicon.png")
    save(gameboy, "icons", "icon-gameboy.png")
    save(gameboy.resize((120, 120), Image.LANCZOS), "icons", "icon-gameboy@2x.png")
    save(gameboy.resize((180, 180), Image.LANCZOS), "icons", "icon-gameboy@3x.png")

    for name, art, bg_color in [("default", full, bg), ("gameboy", gameboy, LIGHTEST)]:
        alpha512 = art_alpha(full, 512)  # same composition, so one mask serves both
        fg = art.resize((512, 512), Image.LANCZOS).convert("RGBA")
        fg.putalpha(alpha512)
        back = Image.new("RGB", (512, 512), bg_color)
        mono = Image.new("RGBA", (432, 432), (255, 255, 255, 0))
        mono.putalpha(art_alpha(full, 432))
        if name == "default":
            save(fg, "images", "android-icon-foreground.png")
            save(back, "images", "android-icon-background.png")
            save(mono, "images", "android-icon-monochrome.png")
        else:
            save(fg, "icons", "android-gameboy-foreground.png")
            save(back, "icons", "android-gameboy-background.png")
            save(mono, "icons", "android-gameboy-monochrome.png")

    print("gameboy shades: %s" % (sorted(gameboy.getcolors(1 << 24)),))


if __name__ == "__main__":
    main()
