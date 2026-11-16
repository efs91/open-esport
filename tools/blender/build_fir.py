"""Construit des sapins légers à partir des brindilles du pack pine_forest : tronc conique + couronnes de brindilles.
  blender -b polyhaven_pine_fir_forest.blend --python build_fir.py -- <dossier_modeles>

Produit fir_a/b/c.glb (proche : ~15-25 k triangles) et fir_far_a/b.glb (loin : quelques cônes empilés).
"""
import bpy, bmesh, sys, os, math, random
from mathutils import Vector, Matrix, Euler

OUT = os.path.abspath(sys.argv[sys.argv.index("--") + 1])
TEX = os.path.abspath(os.path.join(OUT, "..", "textures", "veg"))
os.makedirs(OUT, exist_ok=True)
random.seed(11)

TWIGS = ["fir_twig_main_a.001", "fir_twig_main_b.001", "fir_twig_main_c.001", "fir_twig_tip_d.001"]


def decimated(name, tris):
    """copie d'une brindille décimée"""
    src = bpy.data.objects[name]
    me = src.data.copy()
    ob = bpy.data.objects.new(name + "_d", me)
    bpy.context.scene.collection.objects.link(ob)
    n = sum(len(p.vertices) - 2 for p in me.polygons)
    if n > tris:
        for o in bpy.context.scene.objects:
            o.select_set(False)
        bpy.context.view_layer.objects.active = ob
        ob.select_set(True)
        m = ob.modifiers.new("d", "DECIMATE")
        m.ratio = tris / n
        bpy.ops.object.modifier_apply(modifier=m.name)
    return ob


def material(name, file, cutout):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    b = nt.nodes["Principled BSDF"]
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = bpy.data.images.load(os.path.join(TEX, file))
    nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
    if cutout:
        nt.links.new(t.outputs["Alpha"], b.inputs["Alpha"])
        m.blend_method = "CLIP"
    b.inputs["Roughness"].default_value = 0.95
    return m


def trunk_mesh(bm, height, r0, r1, sides=8):
    """tronc conique avec UV (u autour, v le long)"""
    uv = bm.loops.layers.uv.verify()
    rings = 6
    verts = []
    for i in range(rings + 1):
        t = i / rings
        r = r0 + (r1 - r0) * t ** 0.8
        ring = []
        for k in range(sides):
            a = 2 * math.pi * k / sides
            ring.append(bm.verts.new((math.cos(a) * r, math.sin(a) * r, height * t)))
        verts.append(ring)
    for i in range(rings):
        for k in range(sides):
            k2 = (k + 1) % sides
            f = bm.faces.new((verts[i][k], verts[i][k2], verts[i + 1][k2], verts[i + 1][k]))
            for loop, (u, v) in zip(f.loops, ((k / sides, i / rings), ((k + 1) / sides, i / rings), ((k + 1) / sides, (i + 1) / rings), (k / sides, (i + 1) / rings))):
                loop[uv].uv = (u * 2, v * height / 3.0)


def build(name, height, spread, whorl_gap, per_whorl, tris_per_twig, seed):
    random.seed(seed)
    bm = bmesh.new()
    trunk_mesh(bm, height, 0.28 * height / 14 + 0.12, 0.03)
    trunk_faces = len(bm.faces)
    twigs = [decimated(t, tris_per_twig) for t in TWIGS]
    # tronc : matériau 0 ; brindilles : matériau 1
    for f in bm.faces:
        f.material_index = 0
    z = height * 0.2
    while z < height * 0.985:
        t = (z - height * 0.2) / (height * 0.785)            # 0 en bas, 1 en haut
        scale = spread * (1.15 - 0.85 * t)
        pitch = math.radians(-8 + 38 * t)                   # les branches du bas retombent, celles du haut montent
        base = random.uniform(0, math.tau)
        for k in range(per_whorl):
            ang = base + k * math.tau / per_whorl + random.uniform(-0.25, 0.25)
            src = random.choice(twigs if t < 0.85 else twigs[1:2] + twigs[3:])
            sc = scale * random.uniform(0.85, 1.15) * (0.55 if "tip" in src.name else 1.0)
            # la brindille pousse vers +Y : on la tourne autour de Z (cap) puis on la penche
            m = (Matrix.Translation((0, 0, z + random.uniform(-0.15, 0.15)))
                 @ Euler((0, 0, ang), "XYZ").to_matrix().to_4x4()
                 @ Euler((pitch, 0, 0), "XYZ").to_matrix().to_4x4()
                 @ Matrix.Scale(sc, 4))
            me = src.data
            idx = {}
            uvl = me.uv_layers.active
            newv = []
            for v in me.vertices:
                newv.append(bm.verts.new(m @ v.co))
            uv = bm.loops.layers.uv.verify()
            for p in me.polygons:
                try:
                    f = bm.faces.new([newv[i] for i in p.vertices])
                except ValueError:
                    continue
                f.material_index = 1
                if uvl:
                    for loop, li in zip(f.loops, p.loop_indices):
                        loop[uv].uv = uvl.data[li].uv
        z += whorl_gap * random.uniform(0.85, 1.15)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    me.materials.append(material("bark_" + name, "fir_trunk_01.png", False))
    me.materials.append(material("twig_" + name, "fir_twig.png", True))
    for t in twigs:
        bpy.data.objects.remove(t)
    return ob


def far_tree(name, height, radius):
    """version lointaine : tronc + cônes empilés"""
    bm = bmesh.new()
    uv = bm.loops.layers.uv.verify()
    layers = 5
    for i in range(layers):
        t = i / layers
        z0 = height * (0.12 + 0.82 * t * 0.9)
        h = height * (0.32 - 0.04 * i * 0.5)
        r = radius * (1.0 - 0.8 * t)
        res = bmesh.ops.create_cone(bm, cap_ends=False, cap_tris=False, segments=7, radius1=r, radius2=0.02, depth=h)
        for v in res["verts"]:
            v.co.z += z0 + h / 2
            v.co.x += random.uniform(-0.02, 0.02) * radius
        for f in bm.faces:
            f.material_index = 0
    for f in bm.faces:
        for loop in f.loops:
            loop[uv].uv = (0.3, 0.3)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    m = bpy.data.materials.new("far_" + name)
    m.use_nodes = True
    m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.03, 0.07, 0.03, 1)
    m.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 1.0
    me.materials.append(m)
    return ob


def export(ob):
    for o in bpy.context.scene.objects:
        o.select_set(False)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    path = os.path.join(OUT, ob.name + ".glb")
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_apply=True, export_yup=True, export_materials="EXPORT")
    tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
    print("OK %-12s %6d triangles  dim=%s" % (ob.name, tris, tuple(round(x, 1) for x in ob.dimensions)))


for spec in (("fir_a", 13.0, 1.7, 0.7, 6, 300, 1), ("fir_b", 17.0, 1.9, 0.8, 6, 300, 2), ("fir_c", 9.0, 1.4, 0.55, 6, 300, 3)):
    export(build(*spec))
export(far_tree("fir_far_a", 13.0, 2.8))
export(far_tree("fir_far_b", 17.0, 3.4))
