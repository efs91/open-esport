"""Bake the authored sci-fi outfit into the MakeHuman kit. Run before build_character_details.py."""
import io
import json
import struct
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.spatial import cKDTree
from scipy.spatial.transform import Rotation

ROOT = Path(__file__).resolve().parents[2]
KIT = ROOT / "assets/characters/kit"
PART = "c_sci_fi_battle_royale"
SOURCE = ROOT / "assets/skins/Skin_Sci_Fi_Battle_Royal_1Rigged_animated_mixamo_All_animation.glb"
TYPES = {5126: "<f4", 5123: "<u2", 5125: "<u4", 5121: "u1"}
DIMS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


class Glb:
    def __init__(self, path):
        with path.open("rb") as f:
            f.seek(12)
            length, _ = struct.unpack("<II", f.read(8))
            self.doc = json.loads(f.read(length))
            size, _ = struct.unpack("<II", f.read(8))
            self.data = f.read(size)

    def array(self, index):
        a = self.doc["accessors"][index]
        if "sparse" in a:
            sp = a["sparse"]
            def values(key, dtype, width):
                block = sp[key]
                view = self.doc["bufferViews"][block["bufferView"]]
                offset = view.get("byteOffset", 0) + block.get("byteOffset", 0)
                return np.frombuffer(self.data, dtype, sp["count"] * width, offset).reshape(-1, width)
            width = DIMS[a["type"]]
            result = np.zeros((a["count"], width), dtype=TYPES[a["componentType"]])
            indices = values("indices", TYPES[sp["indices"]["componentType"]], 1).ravel()
            result[indices] = values("values", result.dtype, width)
            return result
        view = self.doc["bufferViews"][a["bufferView"]]
        width = DIMS[a["type"]]
        dtype = np.dtype(TYPES[a["componentType"]])
        return np.ndarray((a["count"], width), dtype, self.data,
                          offset=view.get("byteOffset", 0) + a.get("byteOffset", 0),
                          strides=(view.get("byteStride", width * dtype.itemsize), dtype.itemsize))

    def joints(self):
        skin = self.doc["skins"][0]
        return {self.doc["nodes"][j]["name"].split(":")[-1]: np.linalg.inv(b.reshape(4, 4).T)[:3, 3]
                for j, b in zip(skin["joints"], self.array(skin["inverseBindMatrices"]))}


def outfit_geometry(source, target, body):
    prim = source.doc["meshes"][0]["primitives"][0]
    a = {key: source.array(idx) for key, idx in prim["attributes"].items()}
    points = a["POSITION"]
    bones, dest = source.joints(), target.joints()
    transformed = np.zeros_like(points)
    # Blend local limb transforms so a straight Mixamo sleeve wraps around the kit's bent elbow.
    chain = {"Hips": "Spine", "Spine": "Spine1", "Spine1": "Spine2", "Spine2": "Neck", "Neck": "Head"}
    for side in ("Left", "Right"):
        for parent, child in (("Shoulder", "Arm"), ("Arm", "ForeArm"), ("ForeArm", "Hand"),
                              ("UpLeg", "Leg"), ("Leg", "Foot")):
            chain[side+parent] = side+child
    for i, name in enumerate(bones):
        influence = np.sum(np.where(a["JOINTS_0"] == i, a["WEIGHTS_0"], 0), axis=1)
        if not influence.any():
            continue
        joint = name if name in dest else "Head"
        parent = joint
        if joint.endswith("Hand"):
            parent = joint.replace("Hand", "ForeArm")
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

    # Clip actual triangles at the collar and cuffs, interpolating UVs at the cuts.
    uv = a["TEXCOORD_0"]
    rows = list(np.column_stack((points, transformed, uv)))
    faces = source.array(prim["indices"]).reshape(-1, 3)
    # The original head and exposed forearms carry baked skin colour. Keep the player's skin there.
    # Remove skin islands just inside the raised collar using the authored albedo in that region.
    image_index = source.doc["textures"][source.doc["materials"][0]["pbrMetallicRoughness"]["baseColorTexture"]["index"]]["source"]
    image_view = source.doc["bufferViews"][source.doc["images"][image_index]["bufferView"]]
    start = image_view.get("byteOffset", 0)
    tex = np.asarray(Image.open(io.BytesIO(source.data[start:start+image_view["byteLength"]])).convert("RGB").resize((1024,1024)))
    samples = uv[faces].mean(axis=1) % 1
    r, g, b = tex[(samples[:,1]*1023).astype(int), (samples[:,0]*1023).astype(int)].astype(float).T
    centers = points[faces].mean(axis=1)
    flesh = (r > g*1.06) & (g > b*1.06) & (b > r*.38)
    faces = faces[~((centers[:,1] > 1.44) & (abs(centers[:,0]) < .1) & flesh)]
    cuffs = [bones[side+"ForeArm"][0]*.63 + bones[side+"Hand"][0]*.37 for side in ("Left","Right")]
    for axis, sign, bound in [(1, 1, 1.494), (0, 1, cuffs[0]), (0, -1, -cuffs[1])]:
        clipped, crossings = [], {}
        for face in faces:
            poly = []
            for j, first in enumerate(face):
                second = face[(j+1) % 3]
                d1, d2 = rows[first][axis]*sign-bound, rows[second][axis]*sign-bound
                if d1 <= 0:
                    poly.append(first)
                if (d1 <= 0) != (d2 <= 0):
                    edge = tuple(sorted((first, second)))
                    if edge not in crossings:
                        crossings[edge] = len(rows)
                        rows.append(rows[first] + (rows[second]-rows[first]) * (d1/(d1-d2)))
                    poly.append(crossings[edge])
            clipped.extend((poly[0], poly[k], poly[k+1]) for k in range(1, len(poly)-1))
        faces = np.asarray(clipped)
    used, indices = np.unique(faces, return_inverse=True)
    data = np.array(rows)[used]
    verts, uv = data[:, 3:6], data[:, 6:8]
    faces = indices.reshape(-1, 3)
    return bind_geometry(target, body, verts, uv, faces)


def bind_geometry(target, body, verts, uv, faces):
    normals = np.zeros_like(verts)
    x, y, z = verts[faces].transpose(1, 0, 2)
    area = np.cross(y-x, z-x)
    for col in faces.T:
        np.add.at(normals, col, area)
    normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-10)

    attrs = body["attributes"]
    body_points = target.array(attrs["POSITION"])
    unique, first = np.unique(body_points, axis=0, return_index=True)
    distances, near = cKDTree(unique).query(verts, k=3)
    near = first[near]
    blend = 1 / np.maximum(distances, .0001)**2
    blend /= blend.sum(axis=1, keepdims=True)
    joints, weights = target.array(attrs["JOINTS_0"]), target.array(attrs["WEIGHTS_0"])
    mix = np.zeros((len(verts), len(target.doc["skins"][0]["joints"])))
    for corner in range(3):
        for j in range(4):
            np.add.at(mix, (np.arange(len(verts)), joints[near[:, corner], j]),
                      weights[near[:, corner], j] * blend[:, corner])
    joint = np.argsort(-mix, axis=1)[:, :4]
    weight = np.take_along_axis(mix, joint, axis=1)
    weight /= weight.sum(axis=1, keepdims=True)
    morphs = [(target.array(shape["POSITION"])[near] * blend[:, :, None]).sum(axis=1) for shape in body["targets"]]
    return verts, normals, uv, joint, weight, faces, morphs


def bake():
    path = KIT / "character_kit.glb"
    source, kit = Glb(SOURCE), Glb(path)
    doc = kit.doc
    node = next(n for n in doc["nodes"] if n.get("name") == "Body")
    parent = next(n for n in doc["nodes"] if doc["nodes"].index(node) in n.get("children", []))
    # Replace only our appended data on rebakes; preserve the original imported kit byte for byte.
    keys = ("accessors", "bufferViews", "meshes", "nodes", "textures", "images", "materials")
    mark = doc.setdefault("extras", {}).get(PART)
    if mark:
        parent["children"] = [idx for idx in parent["children"] if idx < mark["nodes"]]
        for k in keys:
            del doc[k][mark[k]:]
        kit.data = kit.data[:mark["bytes"]]
    doc["extras"][PART] = {**{k: len(doc[k]) for k in keys}, "bytes": len(kit.data)}
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
    body_mesh = doc["meshes"][node["mesh"]]
    def texture(image, name):
        stream = io.BytesIO()
        image.save(stream, format="PNG")
        doc["images"].append({"bufferView": view(stream.getvalue()), "mimeType":"image/png", "name":name})
        doc["textures"].append({"source":len(doc["images"])-1})
        return len(doc["textures"])-1

    def add(part, geometry, mat):
        verts, normals, uv, joints, weights, faces, morphs = geometry
        prim = {"material": len(doc["materials"]), "attributes": {"POSITION": accessor(verts, "VEC3"),
                "NORMAL": accessor(normals,"VEC3"), "TEXCOORD_0": accessor(uv,"VEC2"),
                "JOINTS_0": accessor(joints,"VEC4",5123), "WEIGHTS_0": accessor(weights,"VEC4")},
                "indices": accessor(faces,"SCALAR",5123), "targets": [{"POSITION": accessor(m,"VEC3")} for m in morphs]}
        doc["materials"].append(mat)
        parent["children"].append(len(doc["nodes"]))
        doc["nodes"].append({"mesh":len(doc["meshes"]), "name":part, "skin":node["skin"]})
        doc["meshes"].append({"name":part, "primitives":[prim],"extras":body_mesh["extras"]})

    geometry = outfit_geometry(source, kit, body_mesh["primitives"][0])
    mat = source.doc["materials"][0]
    mat["name"] = "SciFiBattleRoyale"
    pbr = mat["pbrMetallicRoughness"]
    for slot, label in [(pbr["baseColorTexture"], "albedo"), (pbr["metallicRoughnessTexture"], "orm"), (mat["normalTexture"], "normal")]:
        image = source.doc["images"][source.doc["textures"][slot["index"]]["source"]]
        v = source.doc["bufferViews"][image["bufferView"]]
        start = v.get("byteOffset", 0)
        im = Image.open(io.BytesIO(source.data[start:start+v["byteLength"]])).convert("RGB")
        im.thumbnail((1024, 1024), Image.Resampling.LANCZOS)
        slot["index"] = texture(im, PART+"_"+label)
    add(PART, geometry, mat)
    from build_raider_outfits import OUTFITS, extract
    for part, label, filename in OUTFITS:
        geometry, material = extract(Glb(ROOT / "assets/skins" / filename), kit, body_mesh["primitives"][0], part, texture)
        add(part, geometry, material)
    from build_headwear import headwear, HEADWEAR
    for part, geometry, material in headwear(kit, body_mesh["primitives"][0], texture):
        add(part, geometry, material)
    buf.extend(bytes(-len(buf) % 4))
    doc["buffers"][0]["byteLength"] = len(buf)
    header = json.dumps(doc, separators=(",", ":")).encode()
    header += b" " * (-len(header) % 4)
    temp = path.with_suffix(".tmp")
    with temp.open("wb") as f:
        f.write(struct.pack("<4sIII4s", b"glTF", 2, 28+len(header)+len(buf), len(header), b"JSON"))
        f.write(header)
        f.write(struct.pack("<I4s",len(buf),b"BIN\0"))
        f.write(buf)
    temp.replace(path)
    manifest = KIT / "kit.json"
    catalog = json.loads(manifest.read_text(encoding="utf8"))
    clothes = catalog["items"]["clothes"]
    for part, label in [(PART, "Sci-Fi Battle Royale")] + [(part, label) for part, label, _ in OUTFITS]:
        if not any(item["id"] == part for item in clothes):
            clothes.append({"id":part,"name":label,"includes_shoes":True})
    catalog["items"]["headwear"] = [{"id":k,"name":v} for k,v in HEADWEAR.items()]
    manifest.write_text(json.dumps(catalog, indent=1, ensure_ascii=False),encoding="utf8")


if __name__ == "__main__":
    bake()
