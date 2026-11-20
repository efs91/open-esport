"""Headwear geometry in the MakeHuman kit's bind space."""
import os
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.spatial.transform import Rotation

from build_raider_outfits import OUTFITS, read_texture
from build_scifi_outfit import Glb, ROOT, bind_geometry

HEADWEAR = {"hat_fedora": "Fedora MakeHuman", "hat_mandalorian": "Casque du Mandalorien",
            "hat_raider": "Bandeau Renegade Raider"}


def makehuman_hat():
    data = Path(os.environ.get("MAKEHUMAN_DATA", "C:/Program Files/makehuman-community/makehuman/data"))
    folder = data / "clothes/fedora01"
    mesh = np.load(folder / "fedora.npz")
    proxy = np.load(folder / "fedora.mhpxy")
    base = np.load(data / "3dobjs/base.npz")
    moves = np.zeros_like(base["coord"])
    with np.load(data / "targets.npz") as archive:
        for target in ("macrodetails/caucasian-male-young", "macrodetails/universal-male-young-averagemuscle-averageweight"):
            key = "targets/"+target
            moves[archive[key+".index"]] += archive[key+".vector"]*.001
    v = mesh["coord"] + (moves[proxy["ref_vIdxs"]] * proxy["weights"][:,:,None]).sum(1)
    v[:,1] -= (base["coord"] + moves)[base["fvert"][base["group"]==0],1].min()
    # Split UV seams before converting MakeHuman quads to GLTF triangles.
    pairs, inverse = np.unique(np.stack((mesh["fvert"],mesh["fuvs"]),axis=-1).reshape(-1,2),axis=0,return_inverse=True)
    q = inverse.reshape(-1,4)
    uv = mesh["texco"][pairs[:,1]].copy()
    uv[:,1] = 1-uv[:,1]
    mats = folder / "materials"
    return v[pairs[:,0]]*.1, uv, np.vstack((q[:,[0,1,2]],q[:,[0,2,3]])), {
        "albedo":Image.open(mats/"fedora_texture.png"), "normal":Image.open(mats/"fedora_normal.png")}


def game_hat(helmet):
    source = Glb(ROOT/"assets/skins"/OUTFITS[0 if helmet else 1][2])
    doc = source.doc
    mesh_index = 3 if helmet else 0
    primitive = doc["meshes"][mesh_index]["primitives"][0]
    a = primitive["attributes"]
    v = source.array(a["POSITION"]).copy()
    if not helmet:
        # The Raider accessory is parented to a posed head. Undo the pose using its inverse bind.
        node = next(i for i,n in enumerate(doc["nodes"]) if n.get("mesh") == mesh_index)
        skin = doc["skins"][0]
        while node not in skin["joints"]:
            n = doc["nodes"][node]
            v = Rotation.from_quat(n.get("rotation",[0,0,0,1])).apply(v*n.get("scale",[1,1,1])) + n.get("translation",[0,0,0])
            node = next(i for i,n in enumerate(doc["nodes"]) if node in n.get("children",[]))
        bind = np.linalg.inv(source.array(skin["inverseBindMatrices"])[skin["joints"].index(node)].reshape(4,4).T)
        v = v @ bind[:3,:3].T + bind[:3,3]
    # Register the original crown to the kit crown; leave clearance around the face and ears.
    v *= [.011, .0105, .0115]
    v += [0, 1.773-v[:,1].max(), .061 if helmet else .067]
    mat = doc["materials"][primitive["material"]]
    pbr = mat["pbrMetallicRoughness"]
    images = {"albedo":read_texture(source,pbr["baseColorTexture"]), "orm":read_texture(source,pbr["metallicRoughnessTexture"])}
    if "normalTexture" in mat:
        images["normal"] = read_texture(source,mat["normalTexture"])
    return v, source.array(a["TEXCOORD_0"]), source.array(primitive["indices"]).reshape(-1,3), images


def headwear(kit, body, texture):
    for part in HEADWEAR:
        v, uv, faces, images = makehuman_hat() if part == "hat_fedora" else game_hat(part == "hat_mandalorian")
        geometry = bind_geometry(kit, body, v, uv, faces)
        _, _, _, joints, weights, _, _ = geometry
        joints[:] = 0
        joints[:,0] = list(kit.joints()).index("Head")
        weights[:] = 0
        weights[:,0] = 1
        material = {"name":part, "doubleSided":True, "pbrMetallicRoughness":{"metallicFactor":0 if part=="hat_fedora" else 1}}
        for key,image in images.items():
            image.thumbnail((1024,1024), Image.Resampling.LANCZOS)
            slot = {"index":texture(image.convert("RGB"),part+"_"+key)}
            if key == "normal":
                material["normalTexture"] = slot
            else:
                material["pbrMetallicRoughness"]["baseColorTexture" if key == "albedo" else "metallicRoughnessTexture"] = slot
        yield part, geometry, material
