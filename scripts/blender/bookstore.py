"""Bookstore (books room): forest green, dark wood, mezzanine, ladder, cafe corner.

Period read: the flagship formula — forest-green walls, tall dark-wood cases,
a railed mezzanine balcony with more stacks, a rolling ladder on the tall
wall, and a small cafe corner with round tables. Warm and calm; covers attach
at runtime, so the shelves stand empty.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mall_common as M

ROOM = "bookstore"

P = {
    "width": 16.0,      # X extent
    "depth": 11.0,      # Y extent
    "wall_h": 4.6,
    "wall_t": 0.25,
    "mezz_depth": 2.6,  # balcony depth along Y, against the back wall
    "mezz_h": 2.7,      # balcony deck height
    "case_h": 2.3,      # tall ground cases
    "stack_levels": [0.35, 0.75, 1.15, 1.55, 1.95],
}

GREEN = "#2f4f3e"
GREEN_DARK = "#243d30"
WOOD = "#4a3120"
WOOD_DARK = "#382512"
WOOD_LIGHT = "#6b4a2e"
FLOOR = "#3d2a1a"
CEILING = "#efe6d4"
BRASS = "#a8874e"
CAFE_CREAM = "#e9dfc8"


def bookcase(b, wood, cx, cy, width, height, levels, z0=0.0):
    """One tall run: plinth, sides, shelves, cornice. Faces -Y; z0 lifts it."""
    depth, t = 0.42, 0.05
    b.add_box(wood, (width, depth, 0.12), (cx, cy, z0 + 0.06))
    b.add_box(wood, (width, depth + 0.04, 0.1), (cx, cy, z0 + height + 0.05))
    b.add_box(wood, (t, depth, height), (cx - width / 2 + t / 2, cy, z0 + height / 2))
    b.add_box(wood, (t, depth, height), (cx + width / 2 - t / 2, cy, z0 + height / 2))
    b.add_box(wood, (width, 0.03, height), (cx, cy + depth / 2 - 0.015, z0 + height / 2))
    for z in levels:
        if z < height - 0.1:
            b.add_box(wood, (width - 0.1, depth - 0.06, 0.04), (cx, cy - 0.01, z0 + z))
    x = -width / 2 + 0.9
    while x < width / 2 - 0.4:  # uprights
        b.add_box(wood, (0.04, depth - 0.08, height - 0.3), (cx + x, cy - 0.01, z0 + height / 2))
        x += 0.9


def build_shell(b):
    w, d, h, t = P["width"], P["depth"], P["wall_h"], P["wall_t"]
    green = M.material("book_green", GREEN, roughness=0.9)
    green_dark = M.material("book_green_dark", GREEN_DARK, roughness=0.9)
    floor = M.material("book_floor", FLOOR, roughness=0.55)
    ceil = M.material("book_ceiling", CEILING, roughness=0.9)
    b.add_box(floor, (w, d, 0.1), (0, 0, -0.05))
    b.add_box(green, (w, t, h), (0, d / 2, h / 2))              # back
    b.add_box(green, (t, d, h), (-w / 2, 0, h / 2))             # left
    b.add_box(green, (t, d, h), (w / 2, 0, h / 2))              # right
    b.add_box(green_dark, (w, t + 0.04, 1.1), (0, d / 2, 0.55))  # wainscot backs
    b.add_box(green, (w, t, 0.9), (0, -d / 2, h - 0.45))        # storefront header
    b.add_box(green, (2.5, t, 2.6), (-w / 2 + 1.25, -d / 2, 1.3))
    b.add_box(green, (2.5, t, 2.6), (w / 2 - 1.25, -d / 2, 1.3))
    glass = M.material("book_glass", "#cfe0e8", roughness=0.1, alpha=0.25)
    b.add_box(glass, (2.5, 0.06, 1.4), (-w / 2 + 1.25, -d / 2, 3.3))
    b.add_box(glass, (2.5, 0.06, 1.4), (w / 2 - 1.25, -d / 2, 3.3))
    b.add_box(ceil, (w, d, 0.15), (0, 0, h + 0.075))


def build_pendants(b):
    brass = M.material("book_brass", BRASS, roughness=0.4, metallic=0.7)
    shade = M.material("book_shade", GREEN_DARK, roughness=0.7)
    glow = M.material("book_pendant_glow", "#fff2d8", emission="#ffe9c4", emission_strength=3.0)
    for x in (-4.5, 0.0, 4.5):
        for y in (-2.5, 1.5):
            b.add_cylinder(brass, 0.015, 0.015, 1.1, (x, y, 4.0), sides=8)
            b.add_cylinder(shade, 0.3, 0.12, 0.25, (x, y, 3.35), sides=20)
            b.add_plane(glow, (0.3, 0.3), (x, y, 3.24), facing="down")


def build_ground_cases(b):
    wood = M.material("book_wood", WOOD, roughness=0.6)
    back = P["depth"] / 2 - P["wall_t"]
    left = -P["width"] / 2 + P["wall_t"]
    # Tall wall under the mezzanine (the ladder wall) flanks the stair opening.
    bookcase(b, wood, -4.4, back - 0.35, 5.6, P["case_h"], P["stack_levels"])
    bookcase(b, wood, 5.6, back - 0.35, 3.2, P["case_h"], P["stack_levels"])
    # Low freestanding stacks: two double-sided islands.
    for gx in (-3.2, 0.6):
        b.add_box(wood, (2.2, 0.9, 0.12), (gx, -0.6, 0.06))
        b.add_box(wood, (2.3, 1.0, 0.08), (gx, -0.6, 1.32))
        for z in (0.4, 0.75, 1.1):
            b.add_box(wood, (2.2, 0.4, 0.04), (gx, -0.6 - 0.24, z))
            b.add_box(wood, (2.2, 0.4, 0.04), (gx, -0.6 + 0.24, z))
        b.add_box(wood, (2.2, 0.06, 1.2), (gx, -0.6, 0.72))
    # Display table near the front.
    top = M.material("book_table_top", WOOD_LIGHT, roughness=0.5)
    b.add_box(wood, (1.6, 0.9, 0.08), (4.6, -2.6, 0.72))
    for dx in (-0.7, 0.7):
        for dy in (-0.35, 0.35):
            b.add_box(wood, (0.08, 0.08, 0.68), (4.6 + dx, -2.6 + dy, 0.34))
    b.add_box(top, (1.7, 1.0, 0.05), (4.6, -2.6, 0.78))


def build_mezzanine(b):
    wood = M.material("book_wood", WOOD)
    wood_dark = M.material("book_wood_dark", WOOD_DARK, roughness=0.65)
    brass = M.material("book_brass", BRASS)
    w, d = P["width"], P["depth"]
    back, mh, md = d / 2 - P["wall_t"], P["mezz_h"], P["mezz_depth"]
    deck_y = back - md / 2
    b.add_box(wood_dark, (w - 1.0, md, 0.18), (0, deck_y, mh - 0.09))  # deck
    b.add_box(wood, (w - 1.0, 0.1, 0.24), (0, deck_y - md / 2, mh - 0.1))  # fascia
    for x in (-6.5, -3.5, -0.5, 2.5, 5.5, 7.0):  # support columns
        b.add_box(wood_dark, (0.22, 0.22, mh), (x, deck_y - md / 2 + 0.2, mh / 2))
    rail_y = deck_y - md / 2 + 0.06
    b.add_box(wood, (w - 1.0, 0.08, 0.07), (0, rail_y, mh + 1.02))  # handrail
    b.add_box(brass, (w - 1.0, 0.04, 0.04), (0, rail_y, mh + 0.55))
    x = -(w - 1.0) / 2 + 0.25  # balusters
    while x < (w - 1.0) / 2 - 0.2:
        if abs(x - 3.4) > 0.75:  # stair opening
            b.add_box(wood, (0.05, 0.05, 0.95), (x, rail_y, mh + 0.5))
        x += 0.5
    # Upper stacks along the back wall.
    bookcase(b, wood, -3.0, back - 0.3, 7.0, 1.7, [0.35, 0.75, 1.15, 1.5], z0=mh)
    # Stair up the right side: ten risers plus a sloped handrail.
    import math as _math

    sx = 3.4
    run = 10 * 0.3
    for i in range(10):
        z = mh * (i + 1) / 10 - mh / 20
        b.add_box(wood_dark, (1.3, 0.32, 0.09), (sx, deck_y - md / 2 - 0.4 - i * 0.3, z))
    slope = _math.degrees(_math.atan2(mh, run))
    rail_len = (mh**2 + run**2) ** 0.5 + 0.4
    b.add_box(wood, (0.07, rail_len, 0.07),
              (sx + 0.68, deck_y - md / 2 - 0.4 - run / 2 + 0.15, mh / 2 + 0.95),
              rotation=(-(180 - slope), 0, 0))


def build_ladder(b):
    """The rolling ladder, leaning on the tall ladder wall with its rail."""
    wood = M.material("book_ladder", WOOD_LIGHT, roughness=0.55)
    brass = M.material("book_brass", BRASS)
    back = P["depth"] / 2 - P["wall_t"]
    case_front = back - 0.35 - 0.21
    lx = -4.4
    lean = 12.0
    b.add_cylinder(brass, 0.03, 0.03, 5.6, (lx, case_front - 0.18, 2.2), (0, 90, 0), sides=12)
    for side in (-0.28, 0.28):
        b.add_box(wood, (0.07, 0.05, 2.5), (lx + side, case_front - 0.32, 1.15), rotation=(lean, 0, 0))
    for i in range(7):
        z = 0.35 + i * 0.3
        y = case_front - 0.32 - (z - 1.15) * 0.213  # follow the lean
        b.add_cylinder(wood, 0.022, 0.022, 0.56, (lx, y, z), (0, 90, 0), sides=10)
    for side in (-0.28, 0.28):
        b.add_cylinder(brass, 0.06, 0.06, 0.05, (lx + side, case_front - 0.55, 0.06), (90, 0, 0), sides=12)


def build_cafe(b):
    """Cafe corner: counter plus two round tables with chairs."""
    wood = M.material("book_wood", WOOD)
    top = M.material("book_table_top", WOOD_LIGHT)
    cream = M.material("book_cafe_chair", CAFE_CREAM, roughness=0.8)
    brass = M.material("book_brass", BRASS)
    cx, cy = -5.6, -3.6
    b.add_box(wood, (2.2, 0.6, 0.95), (cx, cy, 0.475))
    b.add_box(top, (2.4, 0.75, 0.06), (cx, cy, 0.98))
    b.add_box(brass, (2.4, 0.06, 0.12), (cx, cy + 0.36, 0.9))
    for tx, ty in ((-6.4, -1.6), (-4.4, -1.4)):
        b.add_cylinder(brass, 0.04, 0.04, 0.68, (tx, ty, 0.34), sides=10)
        b.add_cylinder(top, 0.38, 0.38, 0.05, (tx, ty, 0.7), sides=24)
        b.add_cylinder(brass, 0.22, 0.26, 0.04, (tx, ty, 0.02), sides=16)
        for dx, dy, face in ((-0.62, 0, 1), (0.62, 0, -1)):
            b.add_box(cream, (0.4, 0.4, 0.05), (tx + dx, ty + dy, 0.45))
            b.add_box(cream, (0.4, 0.05, 0.5), (tx + dx + face * 0.18, ty + dy, 0.72))
            for lx in (-0.15, 0.15):
                for ly in (-0.15, 0.15):
                    b.add_box(wood, (0.04, 0.04, 0.43), (tx + dx + lx, ty + dy + ly, 0.215))


def build_signs():
    face = M.material("book_sign_face", "#efe6d4", roughness=0.6)
    ink = M.material("book_sign_ink", GREEN_DARK, roughness=0.6)
    brass = M.material("book_brass", BRASS)
    b = M.GeoBatch()
    b.add_box(face, (2.8, 0.12, 0.6), (0, -3.6, 3.1))
    b.add_box(brass, (0.04, 0.04, 0.9), (-1.1, -3.6, 3.85))
    b.add_box(brass, (0.04, 0.04, 0.9), (1.1, -3.6, 3.85))
    b.add_box(face, (1.6, 0.1, 0.45), (-5.6, -4.6, 2.5))  # CAFE board over the counter
    b.build()
    M.add_text_sign("BOOKS", 0.32, 0.015, (0, -3.67, 3.1), (90, 0, 0), ink)
    M.add_text_sign("BOOKS", 0.32, 0.015, (0, -3.53, 3.1), (90, 0, 180), ink)
    M.add_text_sign("CAFE", 0.22, 0.012, (-5.6, -4.54, 2.5), (90, 0, 0), ink)


def place_anchors():
    back = P["depth"] / 2 - 0.9
    M.add_anchor("anchor_stack_1", (-4.4, back, 1.4), (-4.4, 0.0, 1.4))
    M.add_anchor("anchor_stack_2", (-3.2, -0.1, 1.0), (-3.2, -3.0, 1.0))
    M.add_anchor("anchor_mezz_1", (-3.0, back - 1.2, P["mezz_h"] + 1.0), (-3.0, 0.0, P["mezz_h"] + 1.0))
    M.add_anchor("anchor_cafe_1", (-5.6, -3.0, 1.1), (-5.6, 0.0, 1.1))
    M.add_anchor("anchor_table_1", (4.6, -2.0, 1.0), (4.6, 0.0, 1.0))


def main():
    M.reset_scene()
    M.set_world_strength(0.7)
    b = M.GeoBatch()
    build_shell(b)
    build_pendants(b)
    build_ground_cases(b)
    build_mezzanine(b)
    build_ladder(b)
    build_cafe(b)
    b.build()
    build_signs()
    place_anchors()
    for x in (-4.5, 0.0, 4.5):
        for y in (-2.5, 1.5):
            M.add_area_light(f"pendant_{x}_{y}", (x, y, 3.1), energy=75, size=0.8, color=(1.0, 0.93, 0.82, 1.0))
    M.add_area_light("mezz_wash", (0, 3.5, 3.6), energy=85, size=2.0, color=(1.0, 0.95, 0.87, 1.0))
    for x in (-3.0, 3.0):
        M.add_area_light(f"uplight_{x}", (x, 0.0, 2.2), energy=40, size=1.8, rotation=(180, 0, 0))
    presets = [
        {"name": "hero", "location": [1.5, -4.6, 1.9], "target": [-1.5, 3.0, 1.8], "lens": 28},
        {"name": "mezz", "location": [-5.5, 1.5, 3.9], "target": [2.0, 3.5, 2.9], "lens": 30},
        {"name": "cafe", "location": [-3.4, -0.6, 1.6], "target": [-5.8, -3.4, 1.0], "lens": 35},
    ]
    hero = M.add_camera("hero", presets[0]["location"], presets[0]["target"], presets[0]["lens"])
    os.makedirs(M.RENDERS_DIR, exist_ok=True)
    M.render_proof(os.path.join(M.RENDERS_DIR, f"{ROOM}.png"), hero)
    M.export_glb(os.path.join(M.STORES_DIR, f"{ROOM}.glb"))
    M.write_presets(ROOM, presets)
    M.report(ROOM, presets)


main()
