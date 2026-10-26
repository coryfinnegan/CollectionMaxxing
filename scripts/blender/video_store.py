"""Video store (movies room): low cream gondolas, open blue carpet, one sign.

Period read: a late-80s neighborhood video store — cream islands you can see
over, blue carpet aisles, a new-release wall, a checkout counter, one hanging
MOVIES sign. Calm, not wall-to-wall covers (covers attach at runtime).
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mall_common as M

ROOM = "video_store"

P = {
    "width": 14.0,      # X extent
    "depth": 10.0,      # Y extent
    "wall_h": 3.2,
    "wall_t": 0.2,
    "gondolas": [-3.6, 0.0, 3.6],  # island centres on X
    "gondola_len": 5.6,  # along Y
    "gondola_h": 1.35,
    "shelf_levels": [0.35, 0.65, 0.95, 1.22],
}

CARPET = "#2e4f8f"
CARPET_DARK = "#243d6e"
CREAM = "#e9dcc0"
CREAM_DARK = "#d8c8a4"
CEILING = "#f2ede0"
TRIM_WOOD = "#7a5c3e"
SIGN_RED = "#b03a2e"


def build_shell(b):
    w, d, h, t = P["width"], P["depth"], P["wall_h"], P["wall_t"]
    carpet = M.material("video_carpet", CARPET, roughness=0.95)
    carpet_dark = M.material("video_carpet_dark", CARPET_DARK, roughness=0.95)
    wall = M.material("video_wall", CREAM, roughness=0.9)
    ceil = M.material("video_ceiling", CEILING, roughness=0.9)
    b.add_box(carpet, (w, d, 0.1), (0, 0, -0.05))
    b.add_box(carpet_dark, (w - 1.2, d - 1.2, 0.12), (0, 0, -0.045))
    b.add_box(carpet, (w - 2.4, d - 2.4, 0.13), (0, 0, -0.04))
    b.add_box(wall, (w, t, h), (0, d / 2, h / 2))            # back
    b.add_box(wall, (t, d, h), (-w / 2, 0, h / 2))           # left
    b.add_box(wall, (t, d, h), (w / 2, 0, h / 2))            # right
    b.add_box(wall, (w, t, 0.9), (0, -d / 2, h - 0.45))      # storefront header
    b.add_box(wall, (2.5, t, 1.0), (-w / 2 + 1.25, -d / 2, 0.5))  # bulkheads
    b.add_box(wall, (2.5, t, 1.0), (w / 2 - 1.25, -d / 2, 0.5))
    glass = M.material("video_glass", "#cfe0e8", roughness=0.1, alpha=0.25)
    b.add_box(glass, (2.5, 0.06, 1.3), (-w / 2 + 1.25, -d / 2, 1.65))
    b.add_box(glass, (2.5, 0.06, 1.3), (w / 2 - 1.25, -d / 2, 1.65))
    b.add_box(ceil, (w, d, 0.15), (0, 0, h + 0.075))
    b.add_box(wall, (0.4, 0.4, h), (-5.6, 0.5, h / 2))       # columns
    b.add_box(wall, (0.4, 0.4, h), (5.6, 0.5, h / 2))


def build_troffers(b):
    frame = M.material("video_troffer", "#3a3a3c", roughness=0.6, metallic=0.4)
    glow = M.material("video_troffer_glow", "#ffffff", emission="#fff6e6", emission_strength=2.5)
    for x in (-4.5, 0.0, 4.5):
        for y in (-2.5, 2.5):
            b.add_box(frame, (1.3, 0.7, 0.08), (x, y, P["wall_h"] - 0.04))
            b.add_plane(glow, (1.15, 0.55), (x, y, P["wall_h"] - 0.085), facing="down")


def build_gondolas(b):
    shelf = M.material("video_shelf", CREAM, roughness=0.85)
    trim = M.material("video_trim", TRIM_WOOD, roughness=0.7)
    back = M.material("video_gondola_back", CREAM_DARK, roughness=0.9)
    for gx in P["gondolas"]:
        glen, gh = P["gondola_len"], P["gondola_h"]
        b.add_box(back, (0.34, glen, gh), (gx, 0.3, gh / 2))          # spine
        b.add_box(trim, (0.8, glen + 0.1, 0.12), (gx, 0.3, 0.06))     # kickplate
        b.add_box(trim, (0.86, glen + 0.16, 0.09), (gx, 0.3, gh + 0.045))  # top cap
        for z in P["shelf_levels"]:
            b.add_box(shelf, (0.3, glen, 0.05), (gx - 0.28, 0.3, z))
            b.add_box(shelf, (0.3, glen, 0.05), (gx + 0.28, 0.3, z))
        x = -glen / 2 + 0.4
        while x < glen / 2 - 0.2:                                     # dividers
            b.add_box(shelf, (0.26, 0.03, 0.9), (gx - 0.27, 0.3 + x, 0.75))
            b.add_box(shelf, (0.26, 0.03, 0.9), (gx + 0.27, 0.3 + x, 0.75))
            x += 0.8


def build_new_wall(b):
    shelf = M.material("video_shelf", CREAM)
    trim = M.material("video_trim", TRIM_WOOD)
    back = P["depth"] / 2 - P["wall_t"]
    b.add_box(trim, (9.0, 0.5, 0.12), (0, back - 0.3, 0.06))
    for z in (0.4, 0.8, 1.2):
        b.add_box(shelf, (9.0, 0.4, 0.05), (0, back - 0.28, z))
    b.add_box(trim, (9.0, 0.46, 0.12), (0, back - 0.28, 1.52))
    for x in (-4.0, -2.0, 0.0, 2.0, 4.0):
        b.add_box(shelf, (0.05, 0.38, 1.1), (x, back - 0.28, 0.95))


def build_counter(b):
    body = M.material("video_counter", CREAM_DARK, roughness=0.8)
    top = M.material("video_counter_top", TRIM_WOOD, roughness=0.5)
    cx, cy = 4.6, -3.4
    b.add_box(body, (2.4, 0.7, 0.95), (cx, cy, 0.475))
    b.add_box(top, (2.6, 0.9, 0.06), (cx, cy, 0.98))
    b.add_box(body, (0.7, 1.6, 0.95), (cx - 1.55, cy + 1.1, 0.475))
    b.add_box(top, (0.9, 1.7, 0.06), (cx - 1.55, cy + 1.1, 0.98))
    b.add_box(top, (0.6, 0.6, 0.5), (cx + 1.7, cy + 0.4, 0.25))  # returns bin


def build_signs():
    face = M.material("video_sign_face", "#f7f2e4", roughness=0.6)
    ink = M.material("video_sign_ink", SIGN_RED, roughness=0.6)
    rod = M.material("video_sign_rod", "#2b2b2e", roughness=0.5, metallic=0.6)
    b = M.GeoBatch()
    b.add_box(face, (3.0, 0.12, 0.6), (0, -3.2, 2.45))          # the one sign
    b.add_box(rod, (0.04, 0.04, 0.5), (-1.2, -3.2, 2.95))
    b.add_box(rod, (0.04, 0.04, 0.5), (1.2, -3.2, 2.95))
    b.add_box(face, (4.2, 0.1, 0.5), (0, P["depth"] / 2 - 0.45, 2.1))  # new-release pelmet
    b.build()
    M.add_text_sign("MOVIES", 0.32, 0.015, (0, -3.27, 2.45), (90, 0, 0), ink)
    M.add_text_sign("MOVIES", 0.32, 0.015, (0, -3.13, 2.45), (90, 0, 180), ink)
    M.add_text_sign("NEW RELEASES", 0.22, 0.012, (0, P["depth"] / 2 - 0.51, 2.1), (90, 0, 0), ink)


def place_anchors():
    n = 1
    for gx in P["gondolas"]:
        for side in (-1, 1):
            ax = gx + side * 0.55
            M.add_anchor(f"anchor_shelf_{n}", (ax, 0.3, 1.0), (ax + side * 2.0, 0.3, 1.0))
            n += 1
    back = P["depth"] / 2 - 0.6
    M.add_anchor("anchor_new_1", (-2.2, back, 1.0), (-2.2, 0.0, 1.0))
    M.add_anchor("anchor_new_2", (2.2, back, 1.0), (2.2, 0.0, 1.0))
    M.add_anchor("anchor_counter_1", (4.6, -2.9, 1.1), (4.6, 0.0, 1.1))


def main():
    M.reset_scene()
    M.set_world_strength(1.0)
    b = M.GeoBatch()
    build_shell(b)
    build_troffers(b)
    build_gondolas(b)
    build_new_wall(b)
    build_counter(b)
    b.build()
    build_signs()
    place_anchors()
    for x in (-4.5, 0.0, 4.5):
        for y in (-2.5, 2.5):
            M.add_area_light(f"troffer_{x}_{y}", (x, y, P["wall_h"] - 0.15), energy=130, size=1.0)
    for x in (-3.0, 3.0):
        M.add_area_light(f"uplight_{x}", (x, 0.0, 1.6), energy=40, size=1.6, rotation=(180, 0, 0))
    presets = [
        {"name": "hero", "location": [-1.8, -4.2, 1.8], "target": [0.6, 2.5, 1.0], "lens": 30},
        {"name": "aisle", "location": [-1.8, -2.6, 1.5], "target": [-1.8, 3.0, 1.0], "lens": 32},
        {"name": "counter", "location": [2.4, -1.6, 1.6], "target": [4.8, -3.4, 1.0], "lens": 35},
    ]
    hero = M.add_camera("hero", presets[0]["location"], presets[0]["target"], presets[0]["lens"])
    os.makedirs(M.RENDERS_DIR, exist_ok=True)
    M.render_proof(os.path.join(M.RENDERS_DIR, f"{ROOM}.png"), hero)
    M.export_glb(os.path.join(M.STORES_DIR, f"{ROOM}.glb"))
    M.write_presets(ROOM, presets)
    M.report(ROOM, presets)


main()
