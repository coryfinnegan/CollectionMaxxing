"""Shared parametric builders for the Mall-mode store rooms (#381).

Each room script (video_store.py, toy_store.py, bookstore.py, mall_hub.py) builds
one interior at true scale (metres, Z up), places anchor_<section>_<n> empties
where the Mall scene graph attaches item covers, exports app/assets/stores/
<room>.glb, and renders a proof still from the room's hero camera preset.

Conventions the Mall spike codes against:
- Anchor local +Y faces the aisle (the direction attached covers face).
- Camera presets are reused by the app; every run merges them into
  app/assets/stores/presets.json under the room id.
- Static geometry merges to one mesh per material (one draw call each).

Trademark guardrails: signs carry generic words only (MOVIES, GAMES, BOOKS,
CAFE, DIRECTORY, TOP 10, TRY ME); no real logos, store names, or baked covers.
"""

import json
import math
import os
import sys

import bpy
from mathutils import Euler, Matrix, Vector

BLENDER = os.path.basename(sys.argv[0]) if sys.argv else "blender"
ROOM_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(ROOM_DIR))
STORES_DIR = os.path.join(REPO_ROOT, "app", "assets", "stores")
RENDERS_DIR = os.path.join(ROOM_DIR, "renders")

FINAL = "--final" in sys.argv

_materials = {}


def reset_scene():
    for coll in list(bpy.data.collections):
        if coll.name != "Collection":
            bpy.data.collections.remove(coll)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for mesh in list(bpy.data.meshes):
        bpy.data.meshes.remove(mesh)
    for mat in list(bpy.data.materials):
        bpy.data.materials.remove(mat)
    for world in list(bpy.data.worlds):
        bpy.data.worlds.remove(world)
    _materials.clear()
    world = bpy.data.worlds.new("MallWorld")
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs["Color"].default_value = (0.055, 0.055, 0.06, 1.0)
    bg.inputs["Strength"].default_value = 1.0
    bpy.context.scene.world = world
    bpy.context.scene.unit_settings.system = "METRIC"


def set_world_strength(strength):
    """Lift the ambient fill so ceilings read instead of going black."""
    world = bpy.context.scene.world
    if world is not None and world.use_nodes:
        world.node_tree.nodes["Background"].inputs["Strength"].default_value = strength


def hex_rgb(value):
    value = value.lstrip("#")
    return tuple(int(value[i : i + 2], 16) / 255.0 for i in (0, 2, 4))


def material(name, hex_color, roughness=0.85, metallic=0.0, emission=None, emission_strength=1.0, alpha=1.0):
    """One flat Principled material per finish; shared across every room."""
    if name in _materials:
        return _materials[name]
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    principled = mat.node_tree.nodes["Principled BSDF"]
    r, g, b = hex_rgb(hex_color)
    principled.inputs["Base Color"].default_value = (r, g, b, 1.0)
    principled.inputs["Roughness"].default_value = roughness
    principled.inputs["Metallic"].default_value = metallic
    if emission is not None:
        er, eg, eb = hex_rgb(emission)
        principled.inputs["Emission Color"].default_value = (er, eg, eb, 1.0)
        principled.inputs["Emission Strength"].default_value = emission_strength
    if alpha < 1.0:
        principled.inputs["Alpha"].default_value = alpha
        mat.blend_method = "BLEND"
    _materials[name] = mat
    return mat


class GeoBatch:
    """Accumulates transformed boxes/cylinders/planes per material, then builds
    one mesh per material so each finish costs a single static draw call."""

    def __init__(self):
        self.buckets = {}

    def _bucket(self, mat):
        return self.buckets.setdefault(mat.name if hasattr(mat, "name") else mat, {"mat": mat, "verts": [], "faces": []})

    @staticmethod
    def _matrix(location, rotation):
        mat = Euler((math.radians(a) for a in rotation), "XYZ").to_matrix().to_4x4()
        mat.translation = Vector(location)
        return mat

    def add_box(self, mat, size, location=(0, 0, 0), rotation=(0, 0, 0)):
        sx, sy, sz = (s / 2.0 for s in size)
        corners = [
            (-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz),
            (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz),
        ]
        # Approximate winding: recalc_face_normals at build time fixes every
        # closed solid, so the corners stay in index order.
        faces = [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
        tx = self._matrix(location, rotation)
        bucket = self._bucket(mat)
        base = len(bucket["verts"])
        bucket["verts"].extend(tx @ Vector(c) for c in corners)
        bucket["faces"].extend(tuple(i + base for i in f) for f in faces)

    def add_cylinder(self, mat, radius_bottom, radius_top, depth, location=(0, 0, 0), rotation=(0, 0, 0), sides=20):
        import math as _math

        tx = self._matrix(location, rotation)
        bucket = self._bucket(mat)
        base = len(bucket["verts"])
        for z, radius in ((-depth / 2, radius_bottom), (depth / 2, radius_top)):
            for i in range(sides):
                a = 2 * _math.pi * i / sides
                bucket["verts"].append(tx @ Vector((radius * _math.cos(a), radius * _math.sin(a), z)))
        for i in range(sides):
            j = (i + 1) % sides
            bucket["faces"].append((base + i, base + j, base + sides + j, base + sides + i))
        bucket["faces"].append(tuple(base + sides - 1 - i for i in range(sides)))
        bucket["faces"].append(tuple(base + sides + i for i in range(sides)))

    def add_plane(self, mat, size, location=(0, 0, 0), rotation=(0, 0, 0), facing="up"):
        sx, sy = size[0] / 2.0, size[1] / 2.0
        corners = [(-sx, -sy, 0), (sx, -sy, 0), (sx, sy, 0), (-sx, sy, 0)]
        tx = self._matrix(location, rotation)
        bucket = self._bucket(mat)
        base = len(bucket["verts"])
        bucket["verts"].extend(tx @ Vector(c) for c in corners)
        bucket["faces"].append((base, base + 1, base + 2, base + 3) if facing == "up" else (base, base + 3, base + 2, base + 1))

    def build(self, collection=None):
        import bmesh

        collection = collection or bpy.context.scene.collection
        for name, bucket in self.buckets.items():
            mesh = bpy.data.meshes.new(f"static_{name}")
            mesh.from_pydata([tuple(v) for v in bucket["verts"]], [], [tuple(f) for f in bucket["faces"]])
            mesh.update()
            bm = bmesh.new()
            bm.from_mesh(mesh)
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
            bm.to_mesh(mesh)
            bm.free()
            mesh.update()
            obj = bpy.data.objects.new(f"static_{name}", mesh)
            obj.data.materials.append(bucket["mat"])
            collection.objects.link(obj)


def add_text_sign(text, size, depth, location, rotation, mat, collection=None):
    """A generic-word sign (MOVIES, GAMES, ...); converted to mesh for GLB."""
    collection = collection or bpy.context.scene.collection
    curve = bpy.data.curves.new("sign_curve", type="FONT")
    curve.body = text
    curve.size = size
    curve.extrude = depth
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"
    tmp = bpy.data.objects.new("sign_tmp", curve)
    tmp.location = location
    tmp.rotation_euler = Euler((math.radians(a) for a in rotation), "XYZ")
    collection.objects.link(tmp)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mesh = bpy.data.meshes.new_from_object(tmp.evaluated_get(depsgraph))
    obj = bpy.data.objects.new(f"sign_{text}", mesh)
    obj.data.materials.append(mat)
    obj.matrix_world = tmp.matrix_world
    collection.objects.link(obj)
    bpy.data.objects.remove(tmp, do_unlink=True)
    bpy.data.curves.remove(curve)
    return obj


def add_anchor(name, location, face_toward, collection=None, size=0.25):
    """An anchor_<section>_<n> empty; local +Y faces the aisle."""
    collection = collection or bpy.context.scene.collection
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = size
    obj.location = location
    dx, dy = face_toward[0] - location[0], face_toward[1] - location[1]
    obj.rotation_euler = Euler((0, 0, math.atan2(-dx, dy)), "XYZ")
    collection.objects.link(obj)
    return obj


def add_camera(name, location, target, lens=32):
    camera_data = bpy.data.cameras.new(name)
    camera_data.lens = lens
    obj = bpy.data.objects.new(name, camera_data)
    bpy.context.scene.collection.objects.link(obj)
    loc, tgt = Vector(location), Vector(target)
    z_axis = (loc - tgt).normalized()
    up = Vector((0, 0, 1))
    if abs(z_axis.dot(up)) > 0.99:
        up = Vector((0, 1, 0))
    x_axis = up.cross(z_axis).normalized()
    y_axis = z_axis.cross(x_axis)
    mat = Matrix((x_axis, y_axis, z_axis)).transposed().to_4x4()
    mat.translation = loc
    obj.matrix_world = mat
    return obj


def add_area_light(name, location, energy=150, size=1.2, color=(1.0, 0.98, 0.94, 1.0), rotation=(0, 0, 0)):
    """An area light; rotation=(180, 0, 0) turns it into a ceiling uplight."""
    light = bpy.data.lights.new(name, "AREA")
    light.energy = energy
    light.size = size
    light.color = color[:3]
    obj = bpy.data.objects.new(name, light)
    obj.location = location
    obj.rotation_euler = Euler((math.radians(a) for a in rotation), "XYZ")
    bpy.context.scene.collection.objects.link(obj)
    return obj


def render_proof(path, camera, resolution=(1280, 800), samples=48):
    scene = bpy.context.scene
    scene.camera = camera
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = resolution
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = path
    scene.eevee.taa_render_samples = samples
    try:
        scene.view_settings.view_transform = "Khronos PBR Neutral"
    except TypeError:
        scene.view_settings.view_transform = "Standard"
    bpy.ops.render.render(write_still=True)


def export_glb(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        use_selection=False,
        export_apply=True,
        export_yup=True,
        export_cameras=False,
        export_lights=False,
    )


def triangle_count():
    depsgraph = bpy.context.evaluated_depsgraph_get()
    total = 0
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        mesh = obj.evaluated_get(depsgraph).to_mesh()
        mesh.calc_loop_triangles()
        total += len(mesh.loop_triangles)
        obj.evaluated_get(depsgraph).to_mesh_clear()
    return total


def mesh_count():
    return sum(1 for obj in bpy.data.objects if obj.type == "MESH")


def anchor_names():
    return sorted(obj.name for obj in bpy.data.objects if obj.type == "EMPTY" and obj.name.startswith("anchor_"))


def write_presets(room, presets):
    os.makedirs(STORES_DIR, exist_ok=True)
    path = os.path.join(STORES_DIR, "presets.json")
    data = {}
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    data[room] = presets
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2, sort_keys=True)
        handle.write("\n")


def report(room, presets):
    print(f"[{room}] meshes={mesh_count()} tris={triangle_count()}")
    print(f"[{room}] anchors={','.join(anchor_names())}")
    print(f"[{room}] presets={json.dumps(presets)}")
