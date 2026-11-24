"""Add a Meshy-rigged (Mixamo skeleton) full-body skin to the MakeHuman kit, without rebuilding the other pieces:
the body becomes an outfit, the head a separate headwear that covers the player's head.

  python build_meshy_outfit.py bake <part>                  -> character_kit.glb + kit.json
  python build_meshy_outfit.py bind <part> <godot_console>  -> details.bytes + coverage.bytes (vertices as Godot imports them)

Bake the outfit first, then its headwear (a re-bake is only possible for the last piece appended).
"""
import gzip
import io
import json
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.spatial import cKDTree
from scipy.spatial.transform import Rotation

from build_scifi_outfit import Glb, KIT, ROOT, TYPES, DIMS, bind_geometry

# Full-body skins: the source covers the whole body (hands and feet included); its head is the headwear.
OUTFITS = {
    "c_sigkiller": dict(label="SigKiller", source="SigKiller.glb"),
}
HATS = {
    "hat_sigkiller": dict(label="Tête SigKiller", source="SigKiller.glb", cover="helmet"),
}
HEAD_BONES = ("Head", "HeadTop_End", "headfront")
COLLAR = 1.53   # above it the kit's helmet layer (hat_mandalorian) hides the player's head; below it the outfit hides the body


def to_kit(source, target):
    """Source vertices re-posed onto the kit skeleton (as build_scifi_outfit), with each vertex's source skin weights."""
    prim = source.doc["meshes"][0]["primitives"][0]
    a = {key: source.array(idx) for key, idx in prim["attributes"].items()}
    points = a["POSITION"].astype(float)
    bones, dest = source.joints(), target.joints()
    transformed = np.zeros_like(points)
    chain = {"Hips": "Spine", "Spine": "Spine1", "Spine1": "Spine2", "Spine2": "Neck", "Neck": "Head"}
    for side in ("Left", "Right"):
        for parent, child in (("Shoulder", "Arm"), ("Arm", "ForeArm"), ("ForeArm", "Hand"), ("UpLeg", "Leg"), ("Leg", "Foot")):
            chain[side+parent] = side+child
    for i, name in enumerate(bones):
        influence = np.sum(np.where(a["JOINTS_0"] == i, a["WEIGHTS_0"], 0), axis=1)
        if not influence.any():
            continue
        joint = name if name in dest else "Head"
        parent = joint.replace("Hand", "ForeArm") if joint.endswith("Hand") else joint
        delta = points - bones[joint]
        if parent in chain:
            child = chain[parent]
            before, after = bones[child] - bones[parent], dest[child] - dest[parent]
            axis = before / np.linalg.norm(before)
            ratio = np.linalg.norm(after) / np.linalg.norm(before)
            rot, _ = Rotation.align_vectors([after / np.linalg.norm(after)], [axis])
            delta = rot.apply(delta + np.outer(delta @ axis, axis) * (ratio - 1))
        elif joint.endswith(("Foot", "ToeBase")):
            delta = delta.copy()
            ankle = joint.replace("ToeBase", "Foot")
            delta[:, 1] = points[:, 1] * dest[ankle][1] / bones[ankle][1] - dest[joint][1]
        transformed += (dest[joint] + delta) * influence[:, None]
    names = list(bones)
    dense = np.zeros((len(points), len(names)))
    for k in range(4):
        np.add.at(dense, (np.arange(len(points)), a["JOINTS_0"][:, k].astype(int)), a["WEIGHTS_0"][:, k])
    faces = source.array(prim["indices"]).reshape(-1, 3).astype(int)
    head = np.isin(np.array(names)[dense[faces].sum(1).argmax(1)], HEAD_BONES)
    return transformed, a["TEXCOORD_0"].astype(float), dense, names, faces, head


def outfit_geometry(source, target, body):
    transformed, uv, dense, names, faces, head = to_kit(source, target)
    faces = faces[~head]
    used, indices = np.unique(faces, return_inverse=True)
    verts, normals, uv, joints, weights, faces, morphs = bind_geometry(target, body, transformed[used], uv[used], indices.reshape(-1, 3))
    # The source's own skinning (Mixamo bone names = kit bone names): the robot's parts move with their own bones.
    kit_names = list(target.joints())
    mapped = np.zeros((len(used), len(kit_names)))
    for column, name in enumerate(names):
        mapped[:, kit_names.index(name if name in kit_names else "Head")] += dense[used, column]
    # The Meshy rig has no finger bones: on the hands, the kit body's weights (fingers included) so the grip closes on the weapon.
    hand = sum(mapped[:, kit_names.index(side + "Hand")] for side in ("Left", "Right"))
    kit = np.zeros_like(mapped)
    np.put_along_axis(kit, joints.astype(int), weights, axis=1)
    mapped = mapped * (1 - hand[:, None]) + kit * hand[:, None]
    joints = np.argsort(-mapped, axis=1)[:, :4]
    weights = np.take_along_axis(mapped, joints, axis=1)
    weights /= weights.sum(1, keepdims=True)
    return verts, normals, uv, joints, weights, faces, morphs


def hat_geometry(source, target, body):
    transformed, uv, dense, names, faces, head = to_kit(source, target)
    faces = faces[head]
    used, indices = np.unique(faces, return_inverse=True)
    geometry = list(bind_geometry(target, body, transformed[used], uv[used], indices.reshape(-1, 3)))
    geometry[3][:] = 0
    geometry[3][:, 0] = list(target.joints()).index("Head")
    geometry[4][:] = 0
    geometry[4][:, 0] = 1
    return geometry


def bake(part):
    hat = part in HATS
    cfg = (HATS if hat else OUTFITS)[part]
    path = KIT / "character_kit.glb"
    kit = Glb(path)
    doc = kit.doc
    node = next(n for n in doc["nodes"] if n.get("name") == "Body")
    parent = next(n for n in doc["nodes"] if doc["nodes"].index(node) in n.get("children", []))
    keys = ("accessors", "bufferViews", "meshes", "nodes", "textures", "images", "materials")
    mark = doc.setdefault("extras", {}).get(part)
    later = [k for k, m in doc["extras"].items() if mark and k != part and isinstance(m, dict) and m.get("nodes", 0) > mark["nodes"]]
    if later:
        sys.exit(f"{part} was appended before {later}: bake those again after it")
    if mark:
        parent["children"] = [i for i in parent["children"] if i < mark["nodes"]]
        for k in keys:
            del doc[k][mark[k]:]
        kit.data = kit.data[:mark["bytes"]]
    doc["extras"][part] = {**{k: len(doc[k]) for k in keys}, "bytes": len(kit.data)}
    buf = bytearray(kit.data)
    def view(blob):
        buf.extend(bytes(-len(buf) % 4))
        doc["bufferViews"].append({"buffer": 0, "byteOffset": len(buf), "byteLength": len(blob)})
        buf.extend(blob)
        return len(doc["bufferViews"])-1
    def accessor(array, kind, component=5126):
        array = np.asarray(array, dtype=TYPES[component]).reshape(-1, DIMS[kind])
        doc["accessors"].append({"bufferView": view(array.tobytes()), "count": len(array), "type": kind,
                                 "componentType": component, "min": array.min(0).tolist(), "max": array.max(0).tolist()})
        return len(doc["accessors"])-1
    def texture(image, name):
        stream = io.BytesIO()
        image.save(stream, format="PNG")
        doc["images"].append({"bufferView": view(stream.getvalue()), "mimeType": "image/png", "name": name})
        doc["textures"].append({"source": len(doc["images"])-1})
        return len(doc["textures"])-1

    source = Glb(ROOT / "assets/skins" / cfg["source"])
    body_mesh = doc["meshes"][node["mesh"]]
    verts, normals, uv, joints, weights, faces, morphs = (hat_geometry if hat else outfit_geometry)(source, kit, body_mesh["primitives"][0])
    assert len(verts) < 65536, len(verts)
    mat = source.doc["materials"][0]
    mat["name"] = cfg["label"]
    pbr = mat["pbrMetallicRoughness"]
    made = {}
    for slot, label in [(pbr.get("baseColorTexture"), "albedo"), (pbr.get("metallicRoughnessTexture"), "orm"),
                        (mat.get("normalTexture"), "normal"), (mat.get("occlusionTexture"), "orm")]:
        if slot is None:
            continue
        if label not in made:
            image = source.doc["images"][source.doc["textures"][slot["index"]]["source"]]
            v = source.doc["bufferViews"][image["bufferView"]]
            start = v.get("byteOffset", 0)
            im = Image.open(io.BytesIO(source.data[start:start+v["byteLength"]])).convert("RGB")
            im.thumbnail((1024, 1024), Image.Resampling.LANCZOS)
            made[label] = texture(im, part+"_"+label)
        slot["index"] = made[label]
    prim = {"material": len(doc["materials"]), "attributes": {"POSITION": accessor(verts, "VEC3"),
            "NORMAL": accessor(normals, "VEC3"), "TEXCOORD_0": accessor(uv, "VEC2"),
            "JOINTS_0": accessor(joints, "VEC4", 5123), "WEIGHTS_0": accessor(weights, "VEC4")},
            "indices": accessor(faces, "SCALAR", 5123), "targets": [{"POSITION": accessor(m, "VEC3")} for m in morphs]}
    doc["materials"].append(mat)
    parent["children"].append(len(doc["nodes"]))
    doc["nodes"].append({"mesh": len(doc["meshes"]), "name": part, "skin": node["skin"]})
    doc["meshes"].append({"name": part, "primitives": [prim], "extras": body_mesh["extras"]})
    buf.extend(bytes(-len(buf) % 4))
    doc["buffers"][0]["byteLength"] = len(buf)
    header = json.dumps(doc, separators=(",", ":")).encode()
    header += b" " * (-len(header) % 4)
    temp = path.with_suffix(".tmp")
    with temp.open("wb") as f:
        f.write(struct.pack("<4sIII4s", b"glTF", 2, 28+len(header)+len(buf), len(header), b"JSON"))
        f.write(header)
        f.write(struct.pack("<I4s", len(buf), b"BIN\0"))
        f.write(buf)
    temp.replace(path)
    manifest = KIT / "kit.json"
    catalog = json.loads(manifest.read_text(encoding="utf8"))
    group = catalog["items"]["headwear" if hat else "clothes"]
    entry = {"id": part, "name": cfg["label"], "cover": cfg["cover"]} if hat else {"id": part, "name": cfg["label"], "includes_shoes": True}
    group[:] = [item for item in group if item["id"] != part] + [entry]
    manifest.write_text(json.dumps(catalog, indent=1, ensure_ascii=False), encoding="utf8")
    print(part, len(verts), "vertices", len(faces), "triangles")


def read_name(r):
    return r.read(r.read(1)[0]).decode()


def write_name(out, name):
    encoded = name.encode()
    out.write(struct.pack("<B", len(encoded)) + encoded)


def bind(part, godot):
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([godot, "--headless", "--path", str(ROOT), "--import"], check=False)
        subprocess.run([godot, "--headless", "--path", str(ROOT), "--script", str(Path(__file__).with_name("character_vertices.gd"))],
                       check=True, env={**os.environ, "OE_MORPH_OUT": tmp})
        coords = np.fromfile(Path(tmp) / (part + "_0.raw"), dtype="<f4")
        coords = coords[:len(coords)//5*3].reshape(-1, 3)

    # details.bytes: this piece's binding to the MakeHuman base vertices, as build_character_details.py makes for custom pieces.
    r = io.BytesIO(gzip.open(KIT / "details.bytes").read())
    rows, base_count = struct.unpack("<II", r.read(8))
    start = r.tell()
    for _ in range(rows * 2):
        r.seek(struct.unpack("<I", r.read(4))[0] * 8, 1)
    targets = r.getvalue()[start:r.tell()]
    surfaces = {}
    for _ in range(struct.unpack("<I", r.read(4))[0]):
        name = read_name(r)
        n = struct.unpack("<I", r.read(4))[0]
        surfaces[name] = (np.frombuffer(r.read(n*12), "<f4").reshape(-1, 3), np.frombuffer(r.read(n*6), "<u2").reshape(-1, 3),
                          np.frombuffer(r.read(n*12), "<f4").reshape(-1, 3))
    landmarks = r.read()
    body, body_refs, _ = surfaces["Body_0"]
    unique, first = np.unique(body, axis=0, return_index=True)
    dist, near = cKDTree(unique).query(coords, k=3)
    w = 1 / np.maximum(dist, .0001)**2
    surfaces[part + "_0"] = (coords, body_refs[first[near], 0], w / w.sum(1, keepdims=True))
    with (KIT / "details.bytes").open("wb") as dest, gzip.GzipFile(fileobj=dest, mode="wb", mtime=0) as out:
        out.write(struct.pack("<II", rows, base_count))
        out.write(targets)
        out.write(struct.pack("<I", len(surfaces)))
        for name, (xyz, refs, weights) in surfaces.items():
            write_name(out, name)
            out.write(struct.pack("<I", len(xyz)))
            out.write(np.asarray(xyz, "<f4").tobytes())
            out.write(np.asarray(refs, "<u2").tobytes())
            out.write(np.asarray(weights, "<f4").tobytes())
        out.write(landmarks)
    if part in HATS:
        print("bound", len(coords), "vertices (the helmet cover hides the head)")
        return

    # coverage.bytes: a full-body skin hides the whole body below the collar, hands and feet included.
    r = io.BytesIO(gzip.open(KIT / "coverage.bytes").read())
    layers = [read_name(r) for _ in range(struct.unpack("<I", r.read(4))[0])]
    cover = {}
    for _ in range(struct.unpack("<I", r.read(4))[0]):
        name = read_name(r)
        n = struct.unpack("<I", r.read(4))[0]
        cover[name] = (np.frombuffer(r.read(n*12), "<f4").reshape(-1, 3), np.frombuffer(r.read(n*4), "<i4").copy())
    if part not in layers:
        layers.append(part)
    assert len(layers) <= 31
    bit = layers.index(part)
    xyz, flags = cover["Body_0"]
    covered = xyz[:, 1] < COLLAR
    flags &= ~np.int32(1 << bit)
    flags |= covered.astype("<i4") << bit
    with (KIT / "coverage.bytes").open("wb") as dest, gzip.GzipFile(fileobj=dest, mode="wb", mtime=0) as out:
        out.write(struct.pack("<I", len(layers)))
        for name in layers:
            write_name(out, name)
        out.write(struct.pack("<I", len(cover)))
        for name, (pos, fl) in cover.items():
            write_name(out, name)
            out.write(struct.pack("<I", len(fl)))
            out.write(np.asarray(pos, "<f4").tobytes())
            out.write(np.asarray(fl, "<i4").tobytes())
    print("bound", len(coords), "vertices; coverage bit", bit, "; body vertices hidden", int(covered.sum()))


if __name__ == "__main__":
    {"bake": lambda: bake(sys.argv[2]), "bind": lambda: bind(sys.argv[2], sys.argv[3])}[sys.argv[1]]()
