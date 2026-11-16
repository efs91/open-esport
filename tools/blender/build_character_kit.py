"""Génère assets/characters/kit (character_kit.glb, kit.json, skins/) depuis MakeHuman/MPFB.
À exécuter dans Blender (Blender Lab MCP, port 9876). Chaque morphologie est un blend shape + des décalages d articulations.
Après exécution : reconvertir les skins/*.jpg avec Pillow (JPEG baseline, sans profil ICC, sinon Godot ne les charge pas),
supprimer leurs .import puis relancer godot --headless --import.
"""
import bpy, bmesh, os, json, shutil, time, re
from mathutils import Vector
from bl_ext.blender_org.mpfb.services.humanservice import HumanService
from bl_ext.blender_org.mpfb.services.assetservice import AssetService

OUT = r"C:\Users\ben\Documents\dev\Godot\open-esport-2025\assets\characters\kit"
os.makedirs(OUT, exist_ok=True)
t0 = time.time()
for o in list(bpy.data.objects):
    n = o.name
    if n in ("Body", "Kit.rig", "eyes") or n.startswith(("Human", "eyes")) or n.split("_")[0] in ("c", "s", "h", "eb", "el") and "_" in n or n.startswith("Human.0"):
        bpy.data.objects.remove(o, do_unlink=True)

# (catégorie MPFB, nom dans la liste, type d'asset, préfixe)
ASSETS = [
    ("clothes", "Male casualsuit01", "Clothes", "c"), ("clothes", "Male casualsuit02", "Clothes", "c"),
    ("clothes", "Male casualsuit03", "Clothes", "c"), ("clothes", "Male elegantsuit01", "Clothes", "c"),
    ("clothes", "Male worksuit01", "Clothes", "c"), ("clothes", "Female casualsuit01", "Clothes", "c"),
    ("clothes", "Female casualsuit02", "Clothes", "c"), ("clothes", "Female elegantsuit01", "Clothes", "c"),
    ("clothes", "Female sportsuit01", "Clothes", "c"),
    ("clothes", "Shoes01", "Clothes", "s"), ("clothes", "Shoes03", "Clothes", "s"), ("clothes", "Shoes04", "Clothes", "s"),
    ("hair", "Afro01", "Hair", "h"), ("hair", "Bob01", "Hair", "h"), ("hair", "Long01", "Hair", "h"),
    ("hair", "Ponytail01", "Hair", "h"), ("hair", "Short02", "Hair", "h"), ("hair", "Short04", "Hair", "h"),
    ("eyebrows", "Eyebrow001", "Eyebrows", "eb"), ("eyebrows", "Eyebrow003", "Eyebrows", "eb"),
    ("eyelashes", "Eyelashes01", "Eyelashes", "el"), ("eyes", "Low-poly", "Eyes", "eyes"),
]
SKINS = ["Young caucasian male", "Young caucasian male2", "Young african male", "Young asian male",
         "Young caucasian female", "Young caucasian female2", "Young african female", "Young asian female",
         "Middleage caucasian male", "Middleage african male", "Middleage caucasian female", "Middleage african female"]
BASE = dict(gender=1.0, age=0.5, muscle=0.5, weight=0.5, proportions=0.5, height=0.5, cupsize=0.5, firmness=0.5,
            race={'asian': 0.0, 'caucasian': 1.0, 'african': 0.0})
MORPHS = {
    "female": dict(gender=0.0), "heavy": dict(weight=1.0), "light": dict(weight=0.0),
    "muscular": dict(muscle=1.0), "frail": dict(muscle=0.0), "tall": dict(height=1.0), "short": dict(height=0.0),
    "elder": dict(age=1.0), "youth": dict(age=0.4),
}

def slug(s):
    return s.lower().replace(" ", "_").replace("-", "_")

def strip_helpers(bm_obj):
    # MPFB stocke la morphologie en shape keys : on les fige dans le maillage avant de garder les nôtres
    if bm_obj.data.shape_keys:
        with bpy.context.temp_override(object=bm_obj, active_object=bm_obj, selected_objects=[bm_obj]):
            bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    vg = bm_obj.vertex_groups.get("body")
    idx = vg.index
    me = bm_obj.data
    b = bmesh.new(); b.from_mesh(me)
    lay = b.verts.layers.deform.verify()
    kill = [v for v in b.verts if idx not in v[lay] or v[lay][idx] < 0.001]
    bmesh.ops.delete(b, geom=kill, context='VERTS')
    b.to_mesh(me); b.free()

def build_state(macro, rigged):
    bm = HumanService.create_human(feet_on_ground=True, scale=0.1, macro_detail_dict=macro)
    rig = HumanService.add_builtin_rig(bm, "mixamo", import_weights=True) if rigged else None
    objs = {}
    for cat, name, atype, pre in ASSETS:
        path = AssetService.get_asset_list(cat)[name]["full_path"]
        o = HumanService.add_mhclo_asset(path, bm, asset_type=atype, material_type='MAKESKIN',
                                         set_up_rigging=rigged, interpolate_weights=rigged, import_weights=rigged)
        objs[pre + "_" + slug(name)] = o
    strip_helpers(bm)
    # les vêtements ne sont pas masqués sous la peau : on les décolle de 4 mm pour que le corps ne les perce pas
    for k, o in objs.items():
        if k.startswith(("c_", "s_")):
            me = o.data
            me.calc_normals() if hasattr(me, "calc_normals") else None
            for v in me.vertices:
                v.co += v.normal * 0.004
    objs["Body"] = bm
    return bm, rig, objs

def merge(m, over):
    r = dict(m); r.update(over); return r

# ---- état de base, avec squelette
base_bm, rig, base_objs = build_state(BASE, True)
print("base", len(base_objs), time.time() - t0)
for k, o in base_objs.items():
    o.name = k
    o.data.name = k
rig.name = "Kit.rig"
vcount = {k: len(o.data.vertices) for k, o in base_objs.items()}

# ---- correspondance os Blender -> os Godot (lue dans le .import du modèle)
IMPORT = os.path.join(OUT, "character_kit.glb.import")
bone_to_godot = {}
if os.path.exists(IMPORT):
    for m in re.finditer(r'"bone_map/(\w+)":&"([^"]*)"', open(IMPORT, encoding="utf-8").read()):
        if m.group(2):
            bone_to_godot[m.group(2).replace("_", ":", 1)] = m.group(1)

heads0 = {b.name: rig.matrix_world @ b.head_local for b in rig.data.bones}

# poids (4 plus forts, normalisés, comme à l'export) de chaque sommet de l'état de base
weights = {}
for k, o in base_objs.items():
    names = {g.index: g.name for g in o.vertex_groups}
    W = []
    for v in o.data.vertices:
        gs = sorted(((names[g.group], g.weight) for g in v.groups if names.get(g.group) in heads0), key=lambda t: -t[1])[:4]
        tot = sum(w for _, w in gs) or 1.0
        W.append([(n, w / tot) for n, w in gs])
    weights[k] = W

offsets = {}
# ---- états morphés : positions des sommets ET des articulations (les os suivent la morphologie dans Godot)
for mname, over in MORPHS.items():
    bm, rig_s, objs = build_state(merge(BASE, over), True)
    heads_s = {b.name: rig_s.matrix_world @ b.head_local for b in rig_s.data.bones}
    delta = {n: heads_s[n] - heads0[n] for n in heads0}
    for k, o in base_objs.items():
        assert len(objs[k].data.vertices) == vcount[k], (k, len(objs[k].data.vertices), vcount[k])
    for k, o in base_objs.items():
        if not o.data.shape_keys:
            o.shape_key_add(name="Basis", from_mix=False)
        key = o.shape_key_add(name=mname, from_mix=False)
        src = objs[k]
        W = weights[k]
        for i, v in enumerate(src.data.vertices):
            move = Vector((0.0, 0.0, 0.0))
            for bn, w in W[i]:
                move += delta[bn] * w
            key.data[i].co = v.co - move
    off = {}
    for n, d in delta.items():
        g = bone_to_godot.get(n)
        if g and d.length > 1e-5:
            off[g] = [round(d.x, 5), round(d.z, 5), round(-d.y, 5)]
    offsets[mname] = off
    for k, src in objs.items():
        d = src.data
        bpy.data.objects.remove(src, do_unlink=True)
        if d.users == 0: bpy.data.meshes.remove(d)
    ad = rig_s.data
    bpy.data.objects.remove(rig_s, do_unlink=True)
    if ad.users == 0: bpy.data.armatures.remove(ad)
    print("morph", mname, len(off), time.time() - t0)

# ---- peau neutre sur le corps (Godot la remplace)
mat = bpy.data.materials.new("Skin"); mat.use_nodes = True
base_objs["Body"].data.materials.clear(); base_objs["Body"].data.materials.append(mat)

# ---- peaux
skins_dir = os.path.join(OUT, "skins"); os.makedirs(skins_dir, exist_ok=True)
skin_list = []
for sname in SKINS:
    info = AssetService.get_asset_list("skins").get(sname)
    if not info: continue
    d = os.path.dirname(info["full_path"])
    pngs = [f for f in os.listdir(d) if f.lower().endswith(".png") and "normal" not in f.lower()]
    if not pngs: continue
    dst = slug(sname) + ".jpg"
    im = bpy.data.images.load(os.path.join(d, pngs[0]))
    im.scale(1024, 1024)
    im.file_format = 'JPEG'
    im.filepath_raw = os.path.join(skins_dir, dst)
    im.save()
    bpy.data.images.remove(im)
    skin_list.append({"name": sname, "file": "skins/" + dst})

# ---- textures plus légères
for im in bpy.data.images:
    if im.users and im.size[0] > 1024 and im.name not in ('Render Result', 'Viewer Node'):
        im.scale(1024, 1024)
# ---- export
sel = [rig] + list(base_objs.values())
bpy.ops.object.select_all(action='DESELECT') if bpy.context.object else None
for o in sel: o.select_set(True)
win = bpy.context.window_manager.windows[0]
area = next(a for a in win.screen.areas if a.type == 'VIEW_3D')
with bpy.context.temp_override(window=win, area=area, region=area.regions[-1], selected_objects=sel, active_object=rig):
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, "character_kit.glb"), export_format='GLB', use_selection=True,
                              export_morph=True, export_morph_normal=False, export_skins=True, export_apply=False,
                              export_yup=True, export_image_format='AUTO', export_animations=False)

groups = {}
for cat, name, atype, pre in ASSETS:
    groups.setdefault({"c": "clothes", "s": "shoes", "h": "hair", "eb": "eyebrows", "el": "eyelashes", "eyes": "eyes"}[pre], []).append(
        {"id": pre + "_" + slug(name), "name": name})
manifest = {"morphs": list(MORPHS.keys()), "bone_offsets": offsets, "skins": skin_list, "items": groups}
json.dump(manifest, open(os.path.join(OUT, "kit.json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print("done", time.time() - t0, vcount)
