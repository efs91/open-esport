"""Exporte en glTF (matériaux simplifiés, géométrie décimée) une liste d'objets d'une scène Blender.

  blender -b scene.blend --python export_props.py -- <dossier_sortie> <specs.json>

specs.json : [{"object": "nom_blender", "out": "nom_fichier", "tris": 6000, "scale": 1.0, "ground": true}, ...]
Les textures de chaque matériau (couleur + transparence) sont retrouvées dans ses nœuds, mises en 1024 px max et enregistrées dans
<dossier_sortie>/../textures/veg/<nom>.png.
"""
import bpy, sys, os, json
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:]
OUT = os.path.abspath(argv[0])
SPECS = json.load(open(argv[1], encoding="utf-8"))
TEXDIR = os.path.abspath(os.path.join(OUT, "..", "textures", "veg"))
os.makedirs(OUT, exist_ok=True)
os.makedirs(TEXDIR, exist_ok=True)
MAXTEX = 1024


def upstream_images(socket, depth=0):
    """images (nœud Image Texture) en amont d'une entrée de nœud"""
    found = []
    if depth > 8:
        return found
    for link in socket.links:
        node = link.from_node
        if node.type == "TEX_IMAGE" and node.image:
            found.append((node.image, link.from_socket.name))
        else:
            for inp in node.inputs:
                found += upstream_images(inp, depth + 1)
    return found


def all_images(tree, acc, depth=0):
    for n in tree.nodes:
        if n.type == "TEX_IMAGE" and n.image:
            acc.append(n.image)
        elif n.type == "GROUP" and n.node_tree and depth < 4:
            all_images(n.node_tree, acc, depth + 1)
    return acc


def fallback_images(mat):
    """texture couleur + transparence retrouvées d'après leur nom de fichier (quand la liaison passe par un groupe de nœuds)"""
    imgs = all_images(mat.node_tree, [])
    key = lambda im: (im.filepath or im.name).replace("\\", "/").split("/")[-1].lower()
    col = [i for i in imgs if any(k in key(i) for k in ("diff", "albedo", "color", "col"))
           and not any(k in key(i) for k in ("alpha", "mask", "nor", "rough", "trans", "ao"))]
    alp = [i for i in imgs if any(k in key(i) for k in ("alpha", "mask"))]
    return (col[0] if col else None), (alp[0] if alp else None)


def pixels(img, size):
    """pixels RGBA (n, n, 4) float32, redimensionnés"""
    tmp = img.copy()
    tmp.scale(size, size)
    a = np.empty(size * size * 4, np.float32)
    tmp.pixels.foreach_get(a)
    bpy.data.images.remove(tmp)
    return a.reshape(size, size, 4)[::-1]          # Blender stocke de bas en haut


_cache = {}


def bake_material(mat, opaque=False):
    """écrit <mat>.png (RGBA) et renvoie son nom de fichier ; None si aucun Principled"""
    if mat is None or not mat.use_nodes:
        return None
    if (mat.name, opaque) in _cache:
        return _cache[(mat.name, opaque)]
    bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    col = upstream_images(bsdf.inputs["Base Color"]) if bsdf else []
    alp = upstream_images(bsdf.inputs["Alpha"]) if bsdf else []
    if not col:
        fc, fa = fallback_images(mat)
        if fc is None:
            _cache[(mat.name, opaque)] = None
            return None
        col = [(fc, "Color")]
        alp = [(fa, "Color")] if fa else []
    img = col[0][0]
    size = min(MAXTEX, max(img.size[0], 64))
    size = 1 << (size.bit_length() - 1)
    rgba = pixels(img, size)
    rgb = rgba[..., :3].copy()
    a = np.ones((size, size), np.float32)
    if alp:
        aim, sock = alp[0]
        ap = pixels(aim, size)
        a = ap[..., 3] if (sock == "Alpha" and ap[..., 3].min() < 0.99) else ap[..., 0]
    elif not opaque and img.alpha_mode != "NONE" and rgba[..., 3].min() < 0.99:
        a = rgba[..., 3]
    if opaque:
        a = np.ones((size, size), np.float32)
    # les images en espace linéaire (« Non-Color ») ne doivent pas être converties ; la couleur est en sRGB
    out = np.dstack([rgb, a])
    name = mat.name.replace(" ", "_").replace(".", "_")
    path = os.path.join(TEXDIR, name + ".png")
    im = bpy.data.images.new("bake_" + name, size, size, alpha=True)
    im.pixels.foreach_set(out[::-1].reshape(-1))
    im.filepath_raw = path
    im.file_format = "PNG"
    im.save()
    bpy.data.images.remove(im)
    _cache[(mat.name, opaque)] = (name + ".png", bool(alp) or a.min() < 0.99)
    return _cache[(mat.name, opaque)]


def simple_material(name, file, cutout):
    m = bpy.data.materials.new("g_" + name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = bpy.data.images.load(os.path.join(TEXDIR, file))
    nt.links.new(t.outputs["Color"], bsdf.inputs["Base Color"])
    if cutout:
        nt.links.new(t.outputs["Alpha"], bsdf.inputs["Alpha"])
        m.blend_method = "CLIP"
    bsdf.inputs["Roughness"].default_value = 0.9
    bsdf.inputs["Specular IOR Level"].default_value = 0.2
    return m


dg = bpy.context.evaluated_depsgraph_get()
done = 0
for spec in SPECS:
    src = bpy.data.objects.get(spec["object"])
    if src is None:
        print("INTROUVABLE :", spec["object"])
        continue
    ev = src.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev, depsgraph=dg)
    ob = bpy.data.objects.new(spec["out"], me)
    bpy.context.scene.collection.objects.link(ob)
    # on garde l'orientation et l'échelle de l'objet source, mais on le place à l'origine
    ob.matrix_world = src.matrix_world.copy()
    ob.location = (0, 0, 0)
    s = spec.get("scale", 1.0)
    ob.scale = (ob.scale[0] * s, ob.scale[1] * s, ob.scale[2] * s)
    for o in bpy.context.scene.objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

    # matériaux : une texture RGBA simple par emplacement
    for slot in ob.material_slots:
        res = bake_material(slot.material, spec.get("opaque", False))
        if res:
            slot.material = simple_material(os.path.splitext(res[0])[0], res[0], res[1])
        else:
            print("  pas de texture pour", slot.material.name if slot.material else None)

    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    target = spec.get("tris", 8000)
    if tris > target:
        m = ob.modifiers.new("dec", "DECIMATE")
        m.ratio = target / tris
        bpy.ops.object.modifier_apply(modifier=m.name)
    # coquille de scan (falaise) : on lui donne de l'épaisseur vers l'intérieur, sinon on voit à travers par derrière
    if spec.get("solidify"):
        m = ob.modifiers.new("sol", "SOLIDIFY")
        m.thickness = spec["solidify"]
        m.offset = -1.0
        m.use_rim = True
        m.use_even_offset = False
        bpy.ops.object.modifier_apply(modifier=m.name)
    # pieds à z = 0, centré
    zs = [v.co.z for v in ob.data.vertices]
    xs = [v.co.x for v in ob.data.vertices]
    ys = [v.co.y for v in ob.data.vertices]
    if spec.get("ground", True):
        cx, cy, cz = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, min(zs) + spec.get("sink", 0.0)
        for v in ob.data.vertices:
            v.co.x -= cx
            v.co.y -= cy
            v.co.z -= cz
    path = os.path.join(OUT, spec["out"] + ".glb")
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_apply=True, export_yup=True,
                              export_image_format="AUTO", export_materials="EXPORT")
    print("OK %-38s %6d -> %6d triangles  dim=%s" % (spec["out"], tris, sum(len(p.vertices) - 2 for p in ob.data.polygons), tuple(round(x, 2) for x in ob.dimensions)))
    done += 1
print("exportés :", done)
