"""
Outil Blender : construit les bras de la vue FPS à partir d'un personnage MakeHuman.

Les bras (peau + manche des vêtements) sont découpés sur le personnage, posés sur le squelette de bras du kit
fps-hands (addons/fps-hands) et liés à ses os, pour que toutes les animations du kit (prise en main, tir,
rechargement…) s'appliquent telles quelles. Sortie : un .glb contenant le squelette du kit et le maillage des bras.

Usage : exécuter ce script dans Blender (fenêtre Scripting, ou serveur MCP) avec le personnage dans SRC.
"""
import bpy, bmesh, os, traceback, math
from mathutils import Vector, Matrix

ROOT = r"C:\Users\ben\Documents\dev\Godot\open-esport-2025"
SRC = ROOT + r"\assets\characters\makehuman_test\makehuman_test.blend"
KIT = ROOT + r"\addons\fps-hands\fps-ak\reasources\fps_ak_animated.glb"
OUT = ROOT + r"\assets\characters\makehuman_test\fps_arms.glb"
WORK = ROOT + r"\assets\characters\makehuman_test\arms_work.blend"

BODY = "Human"
CLOTHES = ["Human.male_worksuit01"]   # vêtements dont on garde la manche
CUT_ALONG = 0.25                      # le bras est coupé à 25 % de l'humérus, en partant de l'épaule
EXTEND = 0.9                          # puis prolongé vers l'épaule de 90 % de la longueur de l'humérus

log = []
bpy.ops.wm.open_mainfile(filepath=SRC)
win = bpy.context.window_manager.windows[0]
area = next((a for a in win.screen.areas if a.type == "VIEW_3D"), win.screen.areas[0])
region = next((r for r in area.regions if r.type == "WINDOW"), area.regions[0])


def ctx(**kw):
    return bpy.context.temp_override(window=win, screen=win.screen, area=area, region=region, **kw)


before = set(bpy.data.objects)
with ctx():
    bpy.ops.import_scene.gltf(filepath=KIT)
new = [o for o in bpy.data.objects if o not in before]
kit = next(o for o in new if o.type == "ARMATURE")
kit_mesh = next(o for o in new if o.type == "MESH" and any(m.type == "ARMATURE" for m in o.modifiers))
mh = next(o for o in bpy.data.objects if o.type == "ARMATURE" and o not in new)


# ---------------------------------------------------------------- repères sur les deux squelettes
def kbone(prefix):
    for b in kit.data.bones:
        if b.name.startswith(prefix + "_") and b.name[len(prefix) + 1:].isdigit():
            return b
    raise KeyError(prefix)


def khead(prefix):
    return kit.matrix_world @ kbone(prefix).head_local


def ktip(prefix):
    """Bout du doigt, mesuré sur le maillage des bras du kit (les os terminaux du kit sont inutilisables)."""
    b = kbone(prefix)
    head = kit.matrix_world @ b.head_local
    gi = kit_mesh.vertex_groups[b.name].index
    pts = [kit_mesh.matrix_world @ v.co for v in kit_mesh.data.vertices
           if any(g.group == gi and g.weight > 0.5 for g in v.groups)]
    if not pts:
        return head + (head - kit.matrix_world @ b.parent.head_local)
    centroid = sum(pts, Vector()) / len(pts)
    d = (centroid - head).normalized()
    return head + d * max((p - head).dot(d) for p in pts)


def mhead(name):
    return mh.matrix_world @ mh.data.bones["mixamorig:" + name].head_local


def mtail(name):
    return mh.matrix_world @ mh.data.bones["mixamorig:" + name].tail_local


def frame(primary, secondary):
    y = primary.normalized()
    x = (secondary - y * secondary.dot(y)).normalized()
    z = x.cross(y)
    return Matrix((x, y, z)).transposed()


FINGERS = [("HandThumb", "thumb"), ("HandIndex", "point"), ("HandMiddle", "middle"), ("HandRing", "ring"), ("HandPinky", "pink")]
group_map = {}   # groupe de sommets Mixamo -> os du kit
segments = []

for side, S in (("Left", "L"), ("Right", "R")):
    m_side = mhead(side + "HandPinky1") - mhead(side + "HandIndex1")
    k_side = khead(S + "_pink1") - khead(S + "_point1")
    m_up, m_fore = mhead(side + "ForeArm") - mhead(side + "Arm"), mhead(side + "Hand") - mhead(side + "ForeArm")
    k_up, k_fore = khead(S + "_elbow") - khead(S + "_arm"), khead(S + "_wrist") - khead(S + "_elbow")
    segments.append((side + "Arm", mhead(side + "Arm"), mhead(side + "ForeArm"), khead(S + "_arm"), khead(S + "_elbow"), m_up.cross(m_fore), k_up.cross(k_fore)))
    segments.append((side + "ForeArm", mhead(side + "ForeArm"), mhead(side + "Hand"), khead(S + "_elbow"), khead(S + "_wrist"), m_side, k_side))
    segments.append((side + "Hand", mhead(side + "Hand"), mhead(side + "HandMiddle1"), khead(S + "_wrist"), khead(S + "_middle1"), m_side, k_side))
    group_map[side + "Shoulder"] = kbone(S + "_arm").name
    group_map[side + "Arm"] = kbone(S + "_arm").name
    group_map[side + "ForeArm"] = kbone(S + "_elbow").name
    group_map[side + "Hand"] = kbone(S + "_wrist").name
    m_normal = (mhead(side + "HandMiddle1") - mhead(side + "Hand")).cross(m_side)
    k_normal = (khead(S + "_middle1") - khead(S + "_wrist")).cross(k_side)
    for m_name, k_name in FINGERS:
        sec_m, sec_k = (m_normal, k_normal) if k_name == "thumb" else (m_side, k_side)
        for i in (1, 2, 3):
            mb = f"{side}{m_name}{i}"
            m_h = mhead(mb)
            m_t = mhead(f"{side}{m_name}{i + 1}") if i < 3 else mtail(mb)
            k_h = khead(f"{S}_{k_name}{i}")
            k_t = khead(f"{S}_{k_name}{i + 1}") if i < 3 else ktip(f"{S}_{k_name}3")
            segments.append((mb, m_h, m_t, k_h, k_t, sec_m, sec_k))
            group_map[mb] = kbone(f"{S}_{k_name}{i}").name

# ---------------------------------------------------------------- les os de bras doivent pouvoir bouger : on les déconnecte
with ctx():
    bpy.context.view_layer.objects.active = mh
    bpy.ops.object.mode_set(mode="EDIT")
    for eb in mh.data.edit_bones:
        if "Arm" in eb.name or "Hand" in eb.name:
            eb.use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
bpy.context.view_layer.update()

# ---------------------------------------------------------------- l'épaule suit le bras : on fusionne ses poids dans ceux du bras
sources = [bpy.data.objects[BODY]] + [bpy.data.objects[n] for n in CLOTHES]
for obj in sources:
    for side in ("Left", "Right"):
        shoulder = obj.vertex_groups.get(f"mixamorig:{side}Shoulder")
        arm = obj.vertex_groups.get(f"mixamorig:{side}Arm")
        if not shoulder or not arm:
            continue
        for v in obj.data.vertices:
            ws = next((g.weight for g in v.groups if g.group == shoulder.index), 0.0)
            if ws > 0:
                wa = next((g.weight for g in v.groups if g.group == arm.index), 0.0)
                arm.add([v.index], min(wa + ws, 1.0), "REPLACE")

# ---------------------------------------------------------------- pose : chaque os du personnage sur le segment du kit
mh_inv = mh.matrix_world.inverted()
for name, m_h, m_t, k_h, k_t, sec_m, sec_k in segments:
    s = (k_t - k_h).length / (m_t - m_h).length
    R = frame(k_t - k_h, sec_k) @ frame(m_t - m_h, sec_m).inverted()
    D = Matrix.Translation(k_h) @ (R * s).to_4x4() @ Matrix.Translation(-m_h)
    pb = mh.pose.bones["mixamorig:" + name]
    pb.matrix = mh_inv @ D @ mh.matrix_world @ pb.bone.matrix_local
    bpy.context.view_layer.update()

for m in sources[0].modifiers:
    if m.type == "MASK":
        m.show_viewport = False
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()

# plan de coupe de chaque bras : perpendiculaire à l'humérus, près de l'épaule
planes = {}
for side, S in (("Left", "L"), ("Right", "R")):
    head, elbow = khead(S + "_arm"), khead(S + "_elbow")
    length = (elbow - head).length
    axis = (head - elbow).normalized()                 # de l'coude vers l'épaule
    planes[side] = (head - axis * length * CUT_ALONG, axis, length * EXTEND, 1 if S == "L" else -1)


def build_arms(src):
    """Maillage posé, réduit aux bras, coupé proprement et prolongé vers l'épaule."""
    mesh = bpy.data.meshes.new_from_object(src.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    mesh.transform(kit.matrix_world.inverted() @ src.matrix_world)
    names = [g.name for g in src.vertex_groups]
    arm_idx = {i for i, n in enumerate(names) if n.replace("mixamorig:", "") in group_map}
    mixamo_idx = {i for i, n in enumerate(names) if n.startswith("mixamorig:")}

    bm = bmesh.new()
    bm.from_mesh(mesh)
    deform = bm.verts.layers.deform.verify()
    doomed = []
    for v in bm.verts:
        w_arm = sum(w for g, w in v[deform].items() if g in arm_idx)
        w_all = sum(w for g, w in v[deform].items() if g in mixamo_idx)
        if w_all <= 0 or w_arm / w_all < 0.5:
            doomed.append(v)
    bmesh.ops.delete(bm, geom=doomed, context="VERTS")

    uv = bm.loops.layers.uv.active
    for side, (P, axis, ext, sign) in planes.items():
        side_verts = [v for v in bm.verts if v.co.x * sign > 0]
        side_faces = list({f for v in side_verts for f in v.link_faces})
        side_edges = list({e for v in side_verts for e in v.link_edges})
        bmesh.ops.bisect_plane(bm, geom=side_verts + side_edges + side_faces, plane_co=P, plane_no=axis, clear_outer=True)
        bm.verts.ensure_lookup_table()

        # bord de coupe : arêtes ouvertes posées sur le plan
        cut = [e for e in bm.edges if e.is_boundary and all(abs((v.co - P).dot(axis)) < 1e-3 and v.co.x * sign > 0 for v in e.verts)]
        if not cut:
            log.append(f"{src.name} {side}: aucune arête de coupe")
            continue
        src_uv = {}
        for e in cut:
            for v in e.verts:
                if v.link_loops:
                    src_uv[tuple(round(c, 4) for c in v.co)] = v.link_loops[0][uv].uv.copy()
        geom = bmesh.ops.extrude_edge_only(bm, edges=cut)["geom"]
        new_verts = [g for g in geom if isinstance(g, bmesh.types.BMVert)]
        new_faces = [g for g in geom if isinstance(g, bmesh.types.BMFace)]
        vert_uv = {}
        for v in new_verts:
            vert_uv[v] = src_uv.get(tuple(round(c, 4) for c in v.co), Vector((0.5, 0.5)))
        bmesh.ops.translate(bm, vec=axis * ext, verts=new_verts)
        nv = set(new_verts)
        cap_edges = [g for g in geom if isinstance(g, bmesh.types.BMEdge) and all(v in nv for v in g.verts)]
        cap = bmesh.ops.edgeloop_fill(bm, edges=cap_edges)["faces"]
        if not cap:
            # Boucle non fermée (couture du vêtement) : on bouche quand même le trou
            cap = bmesh.ops.holes_fill(bm, edges=cap_edges, sides=len(cap_edges) + 4)["faces"]
        for f in cap:
            if f.normal.dot(axis) < 0:
                f.normal_flip()
        for v in bm.verts:
            if v not in nv and tuple(round(c, 4) for c in v.co) in src_uv:
                vert_uv.setdefault(v, src_uv[tuple(round(c, 4) for c in v.co)])
        for f in list(new_faces) + list(cap):
            for loop in f.loops:
                if loop.vert in vert_uv:
                    loop[uv].uv = vert_uv[loop.vert]
        log.append(f"{src.name} {side}: coupe {len(cut)} arêtes, prolongement {ext:.1f}, cap {len(cap)} face(s)")

    # poids : groupes Mixamo fusionnés dans les os du kit
    weights = []
    for v in bm.verts:
        acc = {}
        for g, w in v[deform].items():
            target = group_map.get(names[g].replace("mixamorig:", ""))
            if target and w > 0:
                acc[target] = acc.get(target, 0.0) + w
        total = sum(acc.values()) or 1.0
        weights.append({k: w / total for k, w in acc.items()})
        v[deform].clear()
    bm.to_mesh(mesh)
    bm.free()

    obj = bpy.data.objects.new("FpsArms_" + src.name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    for b in kit.data.bones:
        obj.vertex_groups.new(name=b.name)
    for i, acc in enumerate(weights):
        for name, w in acc.items():
            obj.vertex_groups[name].add([i], w, "REPLACE")
    return obj


parts = [build_arms(s) for s in sources]
for p in parts:
    log.append(f"{p.name}: {len(p.data.vertices)} sommets, matériaux {[m.name for m in p.data.materials if m]}")

# ---------------------------------------------------------------- un seul maillage, lié au squelette du kit
with ctx():
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    if len(parts) > 1:
        bpy.ops.object.join()
arms = bpy.context.view_layer.objects.active
arms.name = "FpsArms"
arms.parent = kit
mod = arms.modifiers.new("Armature", "ARMATURE")
mod.object = kit

bpy.ops.wm.save_as_mainfile(filepath=WORK)
try:
    with ctx():
        bpy.ops.object.select_all(action="DESELECT")
        arms.select_set(True)
        kit.select_set(True)
        bpy.context.view_layer.objects.active = kit
        bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True, export_apply=False,
                                  export_yup=True, export_animations=False, export_skins=True)
    log.append("glb: %d octets" % os.path.getsize(OUT))
except Exception:
    log.append("EXPORT FAIL " + traceback.format_exc()[-500:])

result = {"log": log}
