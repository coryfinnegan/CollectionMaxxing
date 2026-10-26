"""Mall hub (share view): skylit atrium with three storefronts and a kiosk island.

Period read: a small 80s mall court — big tile floor, a glowing skylight slot,
three storefront facades (MOVIES, GAMES, BOOKS) with a directory kiosk island
in the middle, benches and planters. The rainbow stripes live here on the
games storefront facade, never inside the rooms.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mall_common as M

ROOM = "mall_hub"

P = {
    "width": 18.0,      # X extent
    "depth": 14.0,      # Y extent
    "wall_h": 4.2,
    "wall_t": 0.25,
    "sky_w": 6.0,       # skylight slot width (X)
    "sky_d": 8.0,       # skylight slot depth (Y)
}

TILE_A = "#d8cfbd"
TILE_B = "#b9ac90"
WALL = "#e5ddc9"
CEILING = "#efe9da"
TRIM = "#6b5638"
GLASS_TINT = "#cfe0e8"

STRIPES = ["#c0392b", "#d97b29", "#d9b829", "#3e8e4f", "#2f6db0", "#7a4fa3"]


def build_shell(b):
    w, d, h, t = P["width"], P["depth"], P["wall_h"], P["wall_t"]
    tile_a = M.material("hub_tile_a", TILE_A, roughness=0.45)
    tile_b = M.material("hub_tile_b", TILE_B, roughness=0.45)
    wall = M.material("hub_wall", WALL, roughness=0.9)
    ceil = M.material("hub_ceiling", CEILING, roughness=0.9)
    # 2 m tile grid, alternating warm tones, over a dark grout slab.
    grout = M.material("hub_grout", "#5c5344", roughness=0.9)
    b.add_box(grout, (w, d, 0.1), (0, 0, -0.051))
    nx, ny = int(w / 2), int(d / 2)
    for ix in range(nx):
        for iy in range(ny):
            mat = tile_a if (ix + iy) % 2 == 0 else tile_b
            x = -w / 2 + 1.0 + ix * 2.0
            y = -d / 2 + 1.0 + iy * 2.0
            b.add_plane(mat, (1.96, 1.96), (x, y, 0.0))
    b.add_box(wall, (w, t, h), (0, d / 2, h / 2))              # back
    b.add_box(wall, (t, d, h), (-w / 2, 0, h / 2))             # left
    b.add_box(wall, (t, d, h), (w / 2, 0, h / 2))              # right
    b.add_box(wall, (w, t, h), (0, -d / 2, h / 2))             # front (entry wall)
    b.add_box(wall, (0.5, 0.5, h), (-w / 2 + 1.2, -d / 2 + 1.2, h / 2))
    b.add_box(wall, (0.5, 0.5, h), (w / 2 - 1.2, -d / 2 + 1.2, h / 2))
    # Entry doors on the front wall: dark glass double doors.
    glass = M.material("hub_glass", GLASS_TINT, roughness=0.1, alpha=0.3)
    trim = M.material("hub_trim", TRIM, roughness=0.6)
    b.add_box(trim, (3.4, 0.3, 2.6), (0, -d / 2, 1.3))
    b.add_box(glass, (2.8, 0.34, 2.2), (0, -d / 2, 1.2))
    b.add_box(trim, (0.12, 0.36, 2.2), (0, -d / 2, 1.2))
    # Ceiling ring around the skylight slot + the glowing slot itself.
    sw, sd = P["sky_w"], P["sky_d"]
    b.add_box(ceil, (w, (d - sd) / 2, 0.15), (0, (sd + d) / 4, h + 0.075))
    b.add_box(ceil, (w, (d - sd) / 2, 0.15), (0, -(sd + d) / 4, h + 0.075))
    b.add_box(ceil, ((w - sw) / 2, sd, 0.15), ((sw + w) / 4, 0, h + 0.075))
    b.add_box(ceil, ((w - sw) / 2, sd, 0.15), (-(sw + w) / 4, 0, h + 0.075))
    sky = M.material("hub_sky", "#dceef7", emission="#e8f4fc", emission_strength=1.6)
    frame = M.material("hub_sky_frame", "#4a4a4e", roughness=0.5, metallic=0.5)
    b.add_plane(sky, (sw, sd), (0, 0, h + 0.12), facing="down")
    for i in range(5):  # mullions across the slot
        x = -sw / 2 + sw * i / 4
        b.add_box(frame, (0.09, sd, 0.09), (x, 0, h + 0.1))
    b.add_box(frame, (sw + 0.2, 0.12, 0.12), (0, sd / 2, h + 0.1))
    b.add_box(frame, (sw + 0.2, 0.12, 0.12), (0, -sd / 2, h + 0.1))


def storefront(b, cx, sign_text, accent, stripes=False):
    """One facade on the back wall: entry recess, glass sides, sign band."""
    d = P["depth"]
    back = d / 2 - P["wall_t"]
    wall = M.material("hub_wall", WALL)
    trim = M.material("hub_trim", TRIM)
    glass = M.material("hub_glass", GLASS_TINT)
    dark = M.material("hub_entry_dark", "#1c1a17", roughness=0.95)
    face = M.material("hub_sign_face", "#f5f0e2", roughness=0.6)
    ink = M.material(f"hub_sign_ink_{sign_text.lower()}", accent, roughness=0.6)
    fw = 4.6
    b.add_box(dark, (1.8, 0.5, 2.4), (cx, back - 0.1, 1.2))          # entry recess
    b.add_box(trim, (2.0, 0.14, 0.14), (cx, back - 0.3, 2.47))       # entry header
    b.add_box(glass, (1.2, 0.08, 1.7), (cx - 1.6, back - 0.15, 1.15))  # display glass
    b.add_box(glass, (1.2, 0.08, 1.7), (cx + 1.6, back - 0.15, 1.15))
    b.add_box(trim, (1.3, 0.12, 0.1), (cx - 1.6, back - 0.15, 0.25))
    b.add_box(trim, (1.3, 0.12, 0.1), (cx + 1.6, back - 0.15, 0.25))
    b.add_box(trim, (1.3, 0.12, 0.1), (cx - 1.6, back - 0.15, 2.05))
    b.add_box(trim, (1.3, 0.12, 0.1), (cx + 1.6, back - 0.15, 2.05))
    if stripes:  # the games facade carries the rainbow, with its band above
        for i, color in enumerate(STRIPES):
            mat = M.material(f"hub_stripe_{i}", color, roughness=0.7)
            b.add_box(mat, (fw, 0.1, 0.16), (cx, back - 0.2, 3.1 - i * 0.16))
        band_z = 3.72
    else:
        band_z = 2.95
    b.add_box(face, (fw, 0.12, 0.7), (cx, back - 0.2, band_z))        # sign band
    return band_z, ink, face


def build_storefronts():
    back = P["depth"] / 2 - P["wall_t"]
    b = M.GeoBatch()
    signs = []
    for cx, word, accent, stripes in (
        (-5.4, "MOVIES", "#b03a2e", False),
        (0.0, "GAMES", "#1f4e9c", True),
        (5.4, "BOOKS", "#2f4f3e", False),
    ):
        band_z, ink, _face = storefront(b, cx, word, accent, stripes)
        signs.append((word, cx, band_z, ink))
    b.build()
    for word, cx, band_z, ink in signs:
        M.add_text_sign(word, 0.3, 0.015, (cx, back - 0.27, band_z), (90, 0, 0), ink)


def build_kiosk(b):
    """Directory island: counter, glass top, hanging DIRECTORY board."""
    trim = M.material("hub_trim", TRIM)
    body = M.material("hub_kiosk_body", "#8a6f4d", roughness=0.7)
    glass = M.material("hub_glass", GLASS_TINT)
    face = M.material("hub_sign_face", "#f5f0e2")
    ink = M.material("hub_sign_ink_directory", "#3a3a3c", roughness=0.6)
    b.add_box(body, (2.6, 1.2, 0.95), (0, 0.5, 0.475))
    b.add_box(trim, (2.8, 1.4, 0.07), (0, 0.5, 0.985))
    b.add_box(glass, (2.4, 1.0, 0.35), (0, 0.5, 1.2))
    b.add_box(trim, (2.5, 1.1, 0.05), (0, 0.5, 1.4))
    b.add_box(face, (3.0, 0.12, 0.55), (0, 0.5, 3.35))
    b.add_box(trim, (0.05, 0.05, 0.7), (-1.2, 0.5, 3.85))
    b.add_box(trim, (0.05, 0.05, 0.7), (1.2, 0.5, 3.85))
    return ink


def build_benches_planters(b):
    trim = M.material("hub_trim", TRIM)
    slat = M.material("hub_slat", "#9a7c53", roughness=0.7)
    pot = M.material("hub_pot", "#a5552f", roughness=0.8)
    leaf = M.material("hub_leaf", "#3e6b35", roughness=0.9)
    for bx, bz_rot in ((-4.5, 0), (4.5, 0)):
        for i, dy in enumerate((-0.2, 0.0, 0.2)):
            b.add_box(slat, (1.8, 0.16, 0.05), (bx, -3.5 + dy, 0.45))
        for dx in (-0.75, 0.75):
            b.add_box(trim, (0.08, 0.6, 0.43), (bx + dx, -3.5, 0.215))
    for px in (-6.5, 6.5):
        b.add_box(pot, (0.7, 0.7, 0.55), (px, 3.5, 0.275))
        b.add_cylinder(trim, 0.05, 0.05, 0.7, (px, 3.5, 0.85), sides=10)
        b.add_cylinder(leaf, 0.42, 0.3, 0.5, (px, 3.5, 1.35), sides=16)
        b.add_cylinder(leaf, 0.3, 0.18, 0.4, (px, 3.5, 1.75), sides=14)


def place_anchors():
    back = P["depth"] / 2 - 1.0
    M.add_anchor("anchor_store_1", (-5.4, back, 1.4), (-5.4, 0.0, 1.4))
    M.add_anchor("anchor_store_2", (0.0, back, 1.4), (0.0, 0.0, 1.4))
    M.add_anchor("anchor_store_3", (5.4, back, 1.4), (5.4, 0.0, 1.4))
    M.add_anchor("anchor_kiosk_1", (-0.8, 0.5, 1.2), (-0.8, -3.0, 1.2))
    M.add_anchor("anchor_kiosk_2", (0.8, 0.5, 1.2), (0.8, -3.0, 1.2))


def main():
    M.reset_scene()
    M.set_world_strength(1.2)
    b = M.GeoBatch()
    build_shell(b)
    directory_ink = build_kiosk(b)
    build_benches_planters(b)
    b.build()
    build_storefronts()
    M.add_text_sign("DIRECTORY", 0.22, 0.012, (0, 0.43, 3.35), (90, 0, 0), directory_ink)
    M.add_text_sign("DIRECTORY", 0.22, 0.012, (0, 0.57, 3.35), (90, 0, 180), directory_ink)
    place_anchors()
    M.add_area_light("skylight", (0, 0, 4.0), energy=220, size=4.0, color=(0.92, 0.96, 1.0, 1.0))
    for x in (-6.0, 6.0):
        for y in (-4.5, 4.5):
            M.add_area_light(f"court_{x}_{y}", (x, y, 4.0), energy=90, size=1.2)
    for x in (-3.0, 3.0):
        M.add_area_light(f"uplight_{x}", (x, 0.0, 2.0), energy=40, size=1.8, rotation=(180, 0, 0))
    presets = [
        {"name": "hero", "location": [0, -5.6, 2.3], "target": [0, 3.0, 1.6], "lens": 26},
        {"name": "directory", "location": [0, -2.6, 1.7], "target": [0, 0.5, 1.6], "lens": 32},
        {"name": "storefront", "location": [-5.4, 1.5, 1.7], "target": [-5.4, 7.0, 1.8], "lens": 32},
    ]
    hero = M.add_camera("hero", presets[0]["location"], presets[0]["target"], presets[0]["lens"])
    os.makedirs(M.RENDERS_DIR, exist_ok=True)
    M.render_proof(os.path.join(M.RENDERS_DIR, f"{ROOM}.png"), hero)
    M.export_glb(os.path.join(M.STORES_DIR, f"{ROOM}.glb"))
    M.write_presets(ROOM, presets)
    M.report(ROOM, presets)


main()
