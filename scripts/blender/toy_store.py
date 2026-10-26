"""Toy-store game department (games room): white pegboard, ticket wall, glass case.

Period read: a late-80s toy-store game aisle — white pegboard walls with ledge
shelves, a ticket wall of pouches (one flap/pouch/price ticket per game), a
glass display case, low white gondolas. Calm white interior, one hanging GAMES
sign; the rainbow stripes stay outside on the mall storefront, not in here.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mall_common as M

ROOM = "toy_store"

P = {
    "width": 14.0,      # X extent
    "depth": 10.0,      # Y extent
    "wall_h": 3.0,
    "wall_t": 0.2,
    "gondolas": [-2.6, 2.6],  # island centres on X
    "gondola_len": 4.6,  # along Y
    "gondola_h": 1.25,
    "shelf_levels": [0.35, 0.65, 0.95, 1.18],
}

TILE = "#dfe3e6"
TILE_DARK = "#c9ced3"
PEG = "#f4f5f6"
PEG_FRAME = "#9aa0a6"
POUCH = "#e8d9b0"
TICKET = "#fafafa"
PRICE_RED = "#b03a2e"
CASE_WHITE = "#f0f1f2"
CEILING = "#f6f6f4"
SIGN_BLUE = "#1f4e9c"


def build_shell(b):
    w, d, h, t = P["width"], P["depth"], P["wall_h"], P["wall_t"]
    tile = M.material("toy_tile", TILE, roughness=0.5)
    tile_dark = M.material("toy_tile_dark", TILE_DARK, roughness=0.5)
    wall = M.material("toy_wall", PEG, roughness=0.9)
    ceil = M.material("toy_ceiling", CEILING, roughness=0.9)
    # Big-checker vinyl tile: 1 m squares read as tile without texture maps.
    b.add_box(tile_dark, (w, d, 0.1), (0, 0, -0.05))
    nx, ny = int(w), int(d)
    for ix in range(nx):
        for iy in range(ny):
            if (ix + iy) % 2:
                continue
            x = -w / 2 + 0.5 + ix
            y = -d / 2 + 0.5 + iy
            b.add_plane(tile, (0.98, 0.98), (x, y, 0.005))
    b.add_box(wall, (w, t, h), (0, d / 2, h / 2))            # back
    b.add_box(wall, (t, d, h), (-w / 2, 0, h / 2))           # left
    b.add_box(wall, (t, d, h), (w / 2, 0, h / 2))            # right
    b.add_box(wall, (w, t, 0.8), (0, -d / 2, h - 0.4))       # storefront header
    b.add_box(wall, (2.0, t, 1.0), (-w / 2 + 1.0, -d / 2, 0.5))
    b.add_box(wall, (2.0, t, 1.0), (w / 2 - 1.0, -d / 2, 0.5))
    glass = M.material("toy_glass", "#cfe0e8", roughness=0.1, alpha=0.25)
    b.add_box(glass, (2.0, 0.06, 1.2), (-w / 2 + 1.0, -d / 2, 1.6))
    b.add_box(glass, (2.0, 0.06, 1.2), (w / 2 - 1.0, -d / 2, 1.6))
    b.add_box(ceil, (w, d, 0.15), (0, 0, h + 0.075))


def build_troffers(b):
    frame = M.material("toy_troffer", "#3a3a3c", roughness=0.6, metallic=0.4)
    glow = M.material("toy_troffer_glow", "#ffffff", emission="#fff6e6", emission_strength=2.5)
    for x in (-4.5, 0.0, 4.5):
        for y in (-2.5, 2.5):
            b.add_box(frame, (1.3, 0.7, 0.08), (x, y, P["wall_h"] - 0.04))
            b.add_plane(glow, (1.15, 0.55), (x, y, P["wall_h"] - 0.085), facing="down")


def build_pegboard(b):
    """White pegboard panels with ledge shelves and a few peg hooks."""
    board = M.material("toy_pegboard", PEG, roughness=0.85)
    frame = M.material("toy_pegframe", PEG_FRAME, roughness=0.6, metallic=0.3)
    hook = M.material("toy_hook", "#6b7076", roughness=0.4, metallic=0.7)
    ledge = M.material("toy_ledge", CASE_WHITE, roughness=0.8)
    back = P["depth"] / 2 - P["wall_t"]
    # Back-wall pegboard runs, flanking the ticket wall.
    for cx, pw in ((-4.6, 3.6), (4.6, 3.6)):
        b.add_box(board, (pw, 0.06, 1.9), (cx, back - 0.05, 1.55))
        b.add_box(frame, (pw + 0.1, 0.05, 0.06), (cx, back - 0.05, 2.53))
        b.add_box(frame, (pw + 0.1, 0.05, 0.06), (cx, back - 0.05, 0.57))
        for z in (1.0, 1.6, 2.15):
            b.add_box(ledge, (pw - 0.2, 0.28, 0.04), (cx, back - 0.22, z))
        for i, z in enumerate((1.28, 1.88)):
            for hx in (-1.2, 0.0, 1.2):
                b.add_cylinder(hook, 0.012, 0.012, 0.22, (cx + hx, back - 0.2, z), (90, 0, 0), sides=8)


def build_ticket_wall(b):
    """The ticket wall: one pouch + price ticket per game, with price rails."""
    panel = M.material("toy_ticket_panel", "#dfe5ea", roughness=0.9)
    pouch = M.material("toy_pouch", POUCH, roughness=0.9)
    ticket = M.material("toy_ticket", TICKET, roughness=0.85)
    rail = M.material("toy_price_rail", PRICE_RED, roughness=0.7)
    back = P["depth"] / 2 - P["wall_t"]
    cx, cols, rows = 0.0, 9, 4
    pw, ph = 4.6, 2.0
    b.add_box(panel, (pw + 0.3, 0.08, ph + 0.3), (cx, back - 0.06, 1.6))
    for r in range(rows):
        z = 2.25 - r * 0.48
        b.add_box(rail, (pw, 0.03, 0.07), (cx, back - 0.12, z - 0.24))
        for c in range(cols):
            x = cx - pw / 2 + 0.28 + c * (pw - 0.56) / (cols - 1)
            b.add_box(pouch, (0.34, 0.05, 0.4), (x, back - 0.12, z))       # pouch
            b.add_box(ticket, (0.26, 0.055, 0.16), (x, back - 0.12, z + 0.2))  # slip


def build_case(b):
    """Freestanding glass display case with a lit-looking shelf."""
    base = M.material("toy_case_base", CASE_WHITE, roughness=0.7)
    trim = M.material("toy_case_trim", PEG_FRAME, roughness=0.5, metallic=0.4)
    glass = M.material("toy_case_glass", "#d8e6ec", roughness=0.05, alpha=0.22)
    shelf = M.material("toy_case_shelf", "#ffffff", roughness=0.4)
    cx, cy = -4.4, -3.0
    cw, cd, bh, gh = 2.6, 0.9, 0.85, 0.55
    b.add_box(base, (cw, cd, bh), (cx, cy, bh / 2))
    b.add_box(trim, (cw + 0.06, cd + 0.06, 0.05), (cx, cy, bh + 0.025))
    b.add_box(trim, (cw + 0.06, cd + 0.06, 0.05), (cx, cy, bh + gh + 0.05))
    gt = 0.03
    gz = bh + gh / 2 + 0.05
    b.add_box(glass, (cw, gt, gh), (cx, cy - cd / 2, gz))
    b.add_box(glass, (cw, gt, gh), (cx, cy + cd / 2, gz))
    b.add_box(glass, (gt, cd, gh), (cx - cw / 2, cy, gz))
    b.add_box(glass, (gt, cd, gh), (cx + cw / 2, cy, gz))
    b.add_box(glass, (cw, cd, gt), (cx, cy, bh + gh + 0.05))
    b.add_box(shelf, (cw - 0.1, cd - 0.1, 0.03), (cx, cy, bh + 0.32))


def build_gondolas(b):
    case = M.material("toy_case_base", CASE_WHITE)
    trim = M.material("toy_case_trim", PEG_FRAME)
    for gx in P["gondolas"]:
        glen, gh = P["gondola_len"], P["gondola_h"]
        b.add_box(case, (0.34, glen, gh), (gx, 0.4, gh / 2))
        b.add_box(trim, (0.8, glen + 0.1, 0.1), (gx, 0.4, 0.05))
        b.add_box(trim, (0.86, glen + 0.16, 0.08), (gx, 0.4, gh + 0.04))
        for z in P["shelf_levels"]:
            b.add_box(case, (0.3, glen, 0.04), (gx - 0.28, 0.4, z))
            b.add_box(case, (0.3, glen, 0.04), (gx + 0.28, 0.4, z))


def build_signs():
    face = M.material("toy_sign_face", "#f7f4ec", roughness=0.6)
    ink = M.material("toy_sign_ink", SIGN_BLUE, roughness=0.6)
    rod = M.material("toy_sign_rod", "#2b2b2e", roughness=0.5, metallic=0.6)
    b = M.GeoBatch()
    b.add_box(face, (2.6, 0.12, 0.55), (0, -3.2, 2.35))
    b.add_box(rod, (0.04, 0.04, 0.4), (-1.0, -3.2, 2.75))
    b.add_box(rod, (0.04, 0.04, 0.4), (1.0, -3.2, 2.75))
    b.add_box(face, (5.2, 0.1, 0.45), (0, P["depth"] / 2 - 0.4, 2.62))  # ticket-wall pelmet
    b.build()
    M.add_text_sign("GAMES", 0.3, 0.015, (0, -3.27, 2.35), (90, 0, 0), ink)
    M.add_text_sign("GAMES", 0.3, 0.015, (0, -3.13, 2.35), (90, 0, 180), ink)
    M.add_text_sign("TICKETS", 0.2, 0.012, (0, P["depth"] / 2 - 0.46, 2.62), (90, 0, 0), ink)


def place_anchors():
    back = P["depth"] / 2 - 0.6
    M.add_anchor("anchor_ticket_1", (-1.2, back, 1.6), (-1.2, 0.0, 1.6))
    M.add_anchor("anchor_ticket_2", (1.2, back, 1.6), (1.2, 0.0, 1.6))
    M.add_anchor("anchor_peg_1", (-4.6, back, 1.6), (-4.6, 0.0, 1.6))
    M.add_anchor("anchor_peg_2", (4.6, back, 1.6), (4.6, 0.0, 1.6))
    M.add_anchor("anchor_case_1", (-4.4, -2.4, 1.1), (-4.4, 0.0, 1.1))


def main():
    M.reset_scene()
    M.set_world_strength(1.1)
    b = M.GeoBatch()
    build_shell(b)
    build_troffers(b)
    build_pegboard(b)
    build_ticket_wall(b)
    build_case(b)
    build_gondolas(b)
    b.build()
    build_signs()
    place_anchors()
    for x in (-4.5, 0.0, 4.5):
        for y in (-2.5, 2.5):
            M.add_area_light(f"troffer_{x}_{y}", (x, y, P["wall_h"] - 0.15), energy=130, size=1.0)
    for x in (-3.0, 3.0):
        M.add_area_light(f"uplight_{x}", (x, 0.0, 1.6), energy=40, size=1.6, rotation=(180, 0, 0))
    presets = [
        {"name": "hero", "location": [0, -4.2, 1.8], "target": [0, 2.5, 1.3], "lens": 30},
        {"name": "tickets", "location": [0, 0.5, 1.7], "target": [0, 5.0, 1.6], "lens": 32},
        {"name": "case", "location": [-2.2, -1.2, 1.5], "target": [-4.4, -3.0, 1.0], "lens": 35},
    ]
    hero = M.add_camera("hero", presets[0]["location"], presets[0]["target"], presets[0]["lens"])
    os.makedirs(M.RENDERS_DIR, exist_ok=True)
    M.render_proof(os.path.join(M.RENDERS_DIR, f"{ROOM}.png"), hero)
    M.export_glb(os.path.join(M.STORES_DIR, f"{ROOM}.glb"))
    M.write_presets(ROOM, presets)
    M.report(ROOM, presets)


main()
