"""Pièces des tyroliennes (construites par code, d'après l'image de référence) :
  zip_tunnel.glb : tronçon de gaine de verre de 4 m, arceaux métalliques, bandes néon et câble au centre (axe le long de -Z dans Godot)
  zip_pylon.glb  : pilier en trois parties (Base, Column de 1 m de haut à étirer, Cradle)
  zip_pad.glb    : tremplin de lancement
  blender -b -P build_zipline_parts.py -- <dossier de sortie>"""
import bpy, bmesh, math, sys, os
from mathutils import Vector

OUT = os.path.abspath(sys.argv[sys.argv.index("--") + 1])
os.makedirs(OUT, exist_ok=True)
R = 2.5            # rayon de la gaine
LEN = 4.0          # longueur d'un tronçon


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mat(name, color, metallic=0.0, rough=0.5, emission=None, strength=0.0, alpha=1.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*color, alpha)
    b.inputs["Metallic"].default_value = metallic
    b.inputs["Roughness"].default_value = rough
    if emission:
        b.inputs["Emission Color"].default_value = (*emission, 1)
        b.inputs["Emission Strength"].default_value = strength
    if alpha < 1.0:
        b.inputs["Alpha"].default_value = alpha
        m.blend_method = "BLEND"
    m.use_backface_culling = False
    return m


METAL = None


def new_obj(name, bm, material):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    ob.data.materials.append(material)
    return ob


def box(bm, cx, cy, cz, sx, sy, sz):
    r = bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(sx, sy, sz), verts=r["verts"])
    bmesh.ops.translate(bm, vec=(cx, cy, cz), verts=r["verts"])


def cyl(bm, cx, cy, cz, radius, depth, axis="Y", segs=12, r2=None):
    r = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=radius, radius2=radius if r2 is None else r2, depth=depth)
    if axis == "Y":
        bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=__import__("mathutils").Matrix.Rotation(math.pi / 2, 3, "X"), verts=r["verts"])
    bmesh.ops.translate(bm, vec=(cx, cy, cz), verts=r["verts"])


def export(objs, name):
    for o in bpy.context.scene.objects:
        o.select_set(o in objs)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, name + ".glb"), export_format="GLB", use_selection=True, export_yup=True)
    print("OK", name)


# ----------------------------------------------------------------------------------------------- tunnel
clear()
metal = mat("zip_metal", (0.16, 0.17, 0.2), metallic=0.85, rough=0.38)
orange = mat("zip_orange", (0.9, 0.42, 0.06), metallic=0.5, rough=0.45)
neon = mat("zip_neon", (0.1, 0.5, 0.9), emission=(0.15, 0.7, 1.0), strength=5.0)
glass = mat("zip_glass", (0.62, 0.82, 1.0), metallic=0.0, rough=0.04, alpha=0.16)
rope = mat("zip_cable", (0.07, 0.07, 0.08), metallic=0.6, rough=0.5)

ARC0, ARC1 = math.radians(-32), math.radians(212)         # arc couvert : ouvert vers le bas (de -32° à 212°)
N = 24


def arc_pt(a, r, y):
    return Vector((math.cos(a) * r, y, math.sin(a) * r))


# verre : surface de révolution partielle
bm = bmesh.new()
rows = []
for yi in (-LEN / 2, LEN / 2):
    rows.append([bm.verts.new(arc_pt(ARC0 + (ARC1 - ARC0) * i / N, R, yi)) for i in range(N + 1)])
for i in range(N):
    bm.faces.new((rows[0][i], rows[0][i + 1], rows[1][i + 1], rows[1][i]))
glass_obj = new_obj("Glass", bm, glass)

# arceaux : deux, à 1 m des extrémités, section carrée 0.14 m
bm = bmesh.new()
for yc in (-LEN / 2 + 0.07, LEN / 2 - 0.07):
    prev = None
    for i in range(N + 1):
        a = ARC0 + (ARC1 - ARC0) * i / N
        c = arc_pt(a, R + 0.02, yc)
        radial = Vector((math.cos(a), 0, math.sin(a)))
        side = Vector((0, 1, 0))
        pts = [c + radial * 0.09 + side * 0.07, c + radial * 0.09 - side * 0.07, c - radial * 0.09 - side * 0.07, c - radial * 0.09 + side * 0.07]
        ring = [bm.verts.new(p) for p in pts]
        if prev:
            for k in range(4):
                bm.faces.new((prev[k], prev[(k + 1) % 4], ring[(k + 1) % 4], ring[k]))
        prev = ring
rib_obj = new_obj("Ribs", bm, metal)

# bandes néon : le long des deux bords inférieurs de la gaine
bm = bmesh.new()
for a in (ARC0 + math.radians(4), ARC1 - math.radians(4)):
    c = arc_pt(a, R - 0.03, 0)
    box(bm, c.x, 0, c.z, 0.12, LEN - 0.3, 0.07)
neon_obj = new_obj("Neon", bm, neon)

# câble au centre + coulisseau fixe d'attache des arceaux (petits supports)
bm = bmesh.new()
cyl(bm, 0, 0, 0, 0.07, LEN, "Y", 10)
cable_obj = new_obj("Cable", bm, rope)
bm = bmesh.new()
for yc in (-LEN / 2 + 0.07, LEN / 2 - 0.07):
    cyl(bm, 0, yc, R * 0.92, 0.05, R * 0.2, "Y", 6)
    box(bm, 0, yc, R * 0.96, 0.22, 0.12, 0.18)
orange_obj = new_obj("Brackets", bm, orange)
export([glass_obj, rib_obj, neon_obj, cable_obj, orange_obj], "zip_tunnel")

# ----------------------------------------------------------------------------------------------- pilier
clear()
metal = mat("zip_metal", (0.16, 0.17, 0.2), metallic=0.85, rough=0.38)
orange = mat("zip_orange", (0.9, 0.42, 0.06), metallic=0.5, rough=0.45)
neon = mat("zip_neon", (0.1, 0.5, 0.9), emission=(0.15, 0.7, 1.0), strength=5.0)
concrete = mat("zip_concrete", (0.42, 0.43, 0.45), metallic=0.1, rough=0.8)

bm = bmesh.new()
box(bm, 0, 0, -0.5, 1.6, 1.6, 1.0)              # socle enterré d'un mètre
box(bm, 0, 0, 0.25, 1.2, 1.2, 0.5)
base = new_obj("Base", bm, concrete)

bm = bmesh.new()                                  # colonne de 1 m de haut (étirée en Z dans le jeu)
box(bm, 0, 0, 0.5, 0.7, 0.7, 1.0)
col = new_obj("Column", bm, metal)
bm = bmesh.new()
box(bm, 0.36, 0, 0.5, 0.06, 0.12, 0.9)
box(bm, -0.36, 0, 0.5, 0.06, 0.12, 0.9)
neon_col = new_obj("ColumnNeon", bm, neon)

bm = bmesh.new()                                  # berceau sous la gaine
box(bm, 0, 0, 0.2, 2.6, 1.4, 0.4)
box(bm, 0, 0, 0.5, 1.2, 1.0, 0.3)
cradle = new_obj("Cradle", bm, orange)
bm = bmesh.new()
box(bm, 0, 0.72, 0.25, 2.2, 0.05, 0.1)
box(bm, 0, -0.72, 0.25, 2.2, 0.05, 0.1)
cradle_neon = new_obj("CradleNeon", bm, neon)
export([base, col, neon_col, cradle, cradle_neon], "zip_pylon")

# ----------------------------------------------------------------------------------------------- tremplin
clear()
metal = mat("zip_metal", (0.16, 0.17, 0.2), metallic=0.85, rough=0.38)
orange = mat("zip_orange", (0.9, 0.42, 0.06), metallic=0.5, rough=0.45)
neon = mat("zip_neon", (0.1, 0.5, 0.9), emission=(0.15, 0.7, 1.0), strength=6.0)
bm = bmesh.new()
cyl(bm, 0, 0, 0.06, 1.5, 0.12, "Z", 32)
disc = new_obj("Disc", bm, metal)
bm = bmesh.new()
r = bmesh.ops.create_circle(bm, cap_ends=False, segments=48, radius=1.32)
for k in range(48):
    a0, a1 = math.tau * k / 48, math.tau * (k + 1) / 48
    pts = [Vector((math.cos(a0) * 1.25, math.sin(a0) * 1.25, 0.135)), Vector((math.cos(a0) * 1.4, math.sin(a0) * 1.4, 0.135)),
           Vector((math.cos(a1) * 1.4, math.sin(a1) * 1.4, 0.135)), Vector((math.cos(a1) * 1.25, math.sin(a1) * 1.25, 0.135))]
    bm.faces.new([bm.verts.new(p) for p in pts])
ring = new_obj("Ring", bm, neon)
bm = bmesh.new()
for dx in (-0.45, 0.0, 0.45):                      # trois chevrons vers le haut
    bm.faces.new([bm.verts.new(p) for p in (Vector((dx - 0.3, -0.3, 0.14)), Vector((dx, 0.1, 0.14)), Vector((dx + 0.3, -0.3, 0.14)), Vector((dx, -0.1, 0.14)))])
chev = new_obj("Chevrons", bm, orange)
export([disc, ring, chev], "zip_pad")

# ----------------------------------------------------------------------------------------------- chariot + poignée triangulaire
clear()
metal = mat("zip_metal", (0.16, 0.17, 0.2), metallic=0.85, rough=0.38)
steel = mat("zip_steel", (0.55, 0.57, 0.6), metallic=0.9, rough=0.3)
orange = mat("zip_orange", (0.9, 0.42, 0.06), metallic=0.5, rough=0.45)
neon = mat("zip_neon", (0.1, 0.5, 0.9), emission=(0.15, 0.7, 1.0), strength=5.0)
rope = mat("zip_cable", (0.07, 0.07, 0.08), metallic=0.6, rough=0.5)

bm = bmesh.new()
box(bm, 0, 0, 0.0, 0.17, 0.36, 0.15)                       # boîtier (le câble passe en son centre, axe Y)
box(bm, 0, 0, 0.1, 0.12, 0.30, 0.04)                       # capot
body = new_obj("Body", bm, metal)
bm = bmesh.new()
box(bm, 0.095, 0, 0.0, 0.03, 0.30, 0.12)                   # plaques orange sur les flancs
box(bm, -0.095, 0, 0.0, 0.03, 0.30, 0.12)
plates = new_obj("Plates", bm, orange)
bm = bmesh.new()
box(bm, 0, 0.19, -0.02, 0.10, 0.015, 0.05)                 # bandes néon à l'avant et à l'arrière
box(bm, 0, -0.19, -0.02, 0.10, 0.015, 0.05)
box(bm, 0.115, 0, -0.04, 0.012, 0.22, 0.03)
box(bm, -0.115, 0, -0.04, 0.012, 0.22, 0.03)
lights = new_obj("Neon", bm, neon)
bm = bmesh.new()
for yy in (-0.11, 0.11):                                    # galets sur le câble
    cyl(bm, 0.0, yy, 0.06, 0.07, 0.09, "X", 16)
wheels = new_obj("Wheels", bm, steel)
bm = bmesh.new()
cyl(bm, 0, 0, 0.0, 0.025, 0.5, "Y", 8)                      # le câble qui traverse
cab = new_obj("Cable", bm, rope)

# maillon + poignée triangulaire (pointe en haut, barre à tenir en bas) : sommet à z=-0.08, barre à z=-0.32
bm = bmesh.new()
cyl(bm, 0, 0, -0.09, 0.03, 0.06, "Z", 10)


def bar(bm, p0, p1, t=0.028):
    a, b = Vector(p0), Vector(p1)
    mid = (a + b) / 2
    d = b - a
    L = d.length
    r = bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(t, t, L), verts=r["verts"])
    q = Vector((0, 0, 1)).rotation_difference(d.normalized())
    bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=q.to_matrix(), verts=r["verts"])
    bmesh.ops.translate(bm, vec=mid, verts=r["verts"])


bar(bm, (0, 0, -0.10), (0.11, 0, -0.32))
bar(bm, (0, 0, -0.10), (-0.11, 0, -0.32))
bar(bm, (-0.115, 0, -0.32), (0.115, 0, -0.32), 0.034)
handle = new_obj("Handle", bm, metal)
export([body, plates, lights, wheels, cab, handle], "zip_trolley")
