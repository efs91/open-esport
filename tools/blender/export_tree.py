"""Allège un arbre des scènes Blender (nœuds géométriques : 1 à 7 millions de sommets) pour le jeu.
Principe : on évalue l'arbre, on sépare ses îlots (le tronc + des milliers de brindilles), on garde le tronc et une fraction des
brindilles (agrandies pour compenser), on décime le tout, puis on exporte en glTF avec des matériaux simples.

  blender -b scene.blend --python export_tree.py -- <objet> <sortie.glb> <fraction_brindilles> <triangles_max> <matériau_tronc> <matériau_feuillage>
"""
import bpy, bmesh, sys, os, random, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
name, out, keep, tri_max = argv[0], argv[1], float(argv[2]), int(argv[3])
random.seed(7)

dg = bpy.context.evaluated_depsgraph_get()
src = bpy.data.objects[name]
ev = src.evaluated_get(dg)
me = bpy.data.meshes.new_from_object(ev, depsgraph=dg)
ob = bpy.data.objects.new(name + "_game", me)
bpy.context.scene.collection.objects.link(ob)
print("évalué : %d sommets" % len(me.vertices))

# --- îlots (tronc + brindilles) par propagation d'étiquettes avec numpy (l'opérateur « séparer » est bien trop lent ici)
import numpy as np
import bmesh
me = ob.data
n = len(me.vertices)
co = np.empty(n * 3, np.float32)
me.vertices.foreach_get("co", co)
co = co.reshape(n, 3)
ne = len(me.edges)
ed = np.empty(ne * 2, np.int32)
me.edges.foreach_get("vertices", ed)
ea, eb = ed[0::2], ed[1::2]
label = np.arange(n, dtype=np.int32)
for it in range(400):
    m = np.minimum(label[ea], label[eb])
    new = label.copy()
    np.minimum.at(new, ea, m)
    np.minimum.at(new, eb, m)
    new = new[new]                      # saut de pointeurs
    if np.array_equal(new, label):
        break
    label = new
uniq, inv, counts = np.unique(label, return_inverse=True, return_counts=True)
print("îlots :", len(uniq), "(%d itérations)" % it)
zmin = np.full(len(uniq), 1e9, np.float32)
zmax = np.full(len(uniq), -1e9, np.float32)
np.minimum.at(zmin, inv, co[:, 2])
np.maximum.at(zmax, inv, co[:, 2])
is_trunk = (counts > 12000) & ((zmax - zmin) > 2.5)
print("troncs :", int(is_trunk.sum()), "brindilles :", int((~is_trunk).sum()))

# on garde une fraction des brindilles, agrandies autour de leur centre pour compenser
scale_up = 1.0 / max(keep, 0.05) ** 0.35
rng = np.random.default_rng(7)
keep_island = is_trunk | (rng.random(len(uniq)) < keep)
cent = np.zeros((len(uniq), 3), np.float64)
for k in range(3):
    cent[:, k] = np.bincount(inv, weights=co[:, k], minlength=len(uniq)) / counts
factor = np.where(is_trunk, 1.0, scale_up).astype(np.float32)[inv][:, None]
co2 = (cent[inv] + (co - cent[inv]) * factor).astype(np.float32)
me.vertices.foreach_set("co", co2.reshape(-1))
sel = keep_island[inv]
me.vertices.foreach_set("select", (~sel).tolist())
me.update()
bpy.context.view_layer.objects.active = ob
for o in bpy.context.scene.objects:
    o.select_set(False)
ob.select_set(True)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.delete(type="VERT")
bpy.ops.object.mode_set(mode="OBJECT")
tree = ob
tree.name = os.path.splitext(os.path.basename(out))[0]
print("assemblé : %d sommets, %d triangles" % (len(tree.data.vertices), sum(len(p.vertices) - 2 for p in tree.data.polygons)))

# --- décimation
tris = sum(len(p.vertices) - 2 for p in tree.data.polygons)
if tris > tri_max:
    m = tree.modifiers.new("dec", "DECIMATE")
    m.ratio = tri_max / tris
    m.use_collapse_triangulate = True
    bpy.ops.object.modifier_apply(modifier=m.name)
print("final : %d triangles" % sum(len(p.vertices) - 2 for p in tree.data.polygons))

# --- pose les pieds du tronc à z = 0
zs = [v.co.z for v in tree.data.vertices]
for v in tree.data.vertices:
    v.co.z -= min(zs)

# --- matériaux simples (textures déjà préparées par prepare_veg_textures.py)
TEX = os.path.join(os.path.dirname(os.path.abspath(out)), "..", "textures", "veg")


def simple_material(mat_name, diff, alpha=False, rough=0.9):
    m = bpy.data.materials.new(mat_name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    img = nt.nodes.new("ShaderNodeTexImage")
    img.image = bpy.data.images.load(os.path.join(TEX, diff))
    nt.links.new(img.outputs["Color"], bsdf.inputs["Base Color"])
    if alpha:
        nt.links.new(img.outputs["Alpha"], bsdf.inputs["Alpha"])
        m.blend_method = "CLIP" if hasattr(m, "blend_method") else None
    bsdf.inputs["Roughness"].default_value = rough
    return m


trunk_mat = sys.argv[sys.argv.index("--") + 5] if len(argv) > 4 else "pine_bark.png"
leaf_mat = sys.argv[sys.argv.index("--") + 6] if len(argv) > 5 else "fir_twig.png"
slots = [s.material.name if s.material else "" for s in tree.material_slots]
print("matériaux d'origine :", slots)
for i, s in enumerate(tree.material_slots):
    nm = (s.material.name if s.material else "").lower()
    if "twig" in nm or "leaf" in nm or "needle" in nm or "cover" in nm:
        s.material = simple_material("foliage", leaf_mat, alpha=True)
    else:
        s.material = simple_material("bark", trunk_mat)

os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
bpy.ops.object.select_all(action="DESELECT")
tree.select_set(True)
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format="GLB", export_apply=True, export_yup=True,
                          export_image_format="AUTO", export_materials="EXPORT")
print("exporté :", out)
