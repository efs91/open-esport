"""Extract the two Unreal-rigged outfits into the common MakeHuman bind pose."""
import io
import re

import numpy as np
from PIL import Image
from scipy.interpolate import RBFInterpolator
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree

from build_scifi_outfit import bind_geometry

OUTFITS = [
    ("c_mandalorian", "Mandalorien", "mandalorian__the_fortnite_season_6_skin_updated.glb"),
    ("c_renegade_raider", "Renegade Raider", "renegade_raider_fortnite_skin_-_seasonal_shop.glb"),
]


def registration(source, kit, unit=.01):
    bones = {re.sub(r"_0\d+$", "", k): v*unit for k,v in source.joints().items()}
    dest = kit.joints()
    pairs = {"pelvis":"Hips", "spine_02":"Spine", "spine_03":"Spine1", "spine_05":"Spine2",
             "neck_01":"Neck", "head":"Head"}
    for suffix, side in [("l","Left"), ("r","Right")]:
        for old,new in [("upperarm","Arm"), ("lowerarm","ForeArm"), ("hand","Hand"),
                        ("thigh","UpLeg"), ("calf","Leg"), ("foot","Foot"), ("ball","ToeBase")]:
            pairs[old+"_"+suffix] = side+new
    old, new = [], []
    for bone, target in pairs.items():
        p, q = bones[bone], dest[target]
        for offset in ([0,0,0], [0,0,.09], [0,0,-.09]):
            old.append(p+offset)
            new.append(q+offset)
        if bone.startswith("foot_"):
            old.append([p[0],0,p[2]])
            new.append([q[0],0,q[2]])
    return RBFInterpolator(np.array(old), np.array(new)-old, smoothing=.00003), bones


def raider_faces(v, uv, faces):
    # Work on connected UV islands: the exposed arms, fingers and neck share the torso material.
    _, welded = np.unique(np.column_stack((v,uv)).round(4),axis=0,return_inverse=True)
    t = welded.ravel()[faces]
    graph = coo_matrix((np.ones(t.size), (t.ravel(), np.roll(t,1,axis=1).ravel())),
                       shape=(welded.max()+1,)*2)
    count, labels = connected_components(graph, directed=False)
    island = labels[t[:,0]]
    keep = np.ones(len(faces),dtype=bool)
    for k in range(count):
        ids = faces[island == k].ravel()
        y = v[ids,1].mean()
        center = uv[ids].mean(axis=0)
        bare_arm = y > 95 and center[0] > .62 and center[1] > .70
        fingers = y > 72 and y < 100 and abs(v[ids,0].mean()) > 29
        neck = y > 136 and np.max(uv[ids]) == 0
        if bare_arm or fingers or neck:
            keep[island == k] = False
    return faces[keep]


def read_texture(source, slot):
    im = source.doc["images"][source.doc["textures"][slot["index"]]["source"]]
    v = source.doc["bufferViews"][im["bufferView"]]
    start = v.get("byteOffset",0)
    return Image.open(io.BytesIO(source.data[start:start+v["byteLength"]])).convert("RGB")


def extract(source, kit, body, part, texture):
    mando = part == "c_mandalorian"
    warp, bones = registration(source, kit)
    # One indexed surface per outfit, shared by the appearance fitter and first-person arms.
    materials = [0,1,3] if mando else [1]
    grid = 2 if mando else 1
    tile = 1024
    maps = {key:Image.new("RGB",(tile*grid, tile*grid)) for key in ("albedo","orm")}
    for i,m in enumerate(materials):
        pbr = source.doc["materials"][m]["pbrMetallicRoughness"]
        for channel,slot in [("albedo","baseColorTexture"),("orm","metallicRoughnessTexture")]:
            image = read_texture(source,pbr[slot]).resize((tile,tile),Image.Resampling.LANCZOS)
            if channel == "orm":
                rgb = np.array(image)
                rgb[:,:,1] = np.clip(rgb[:,:,1]*pbr.get("roughnessFactor",1),0,255)
                if not mando:
                    rgb[:,:,1] = np.maximum(rgb[:,:,1], 135)
                rgb[:,:,2] = np.clip(rgb[:,:,2]*pbr.get("metallicFactor",1),0,255)
                image = Image.fromarray(rgb)
            maps[channel].paste(image,((i%grid)*tile,(i//grid)*tile))
    verts, texcoords, triangles, cape = [], [], [], []
    count = 0
    for mesh in source.doc["meshes"]:
        prim = mesh["primitives"][0]
        m = prim["material"]
        if m not in materials or (mando and ".mo.001_" in mesh["name"]):
            continue
        a = prim["attributes"]
        v = source.array(a["POSITION"])
        uv = source.array(a["TEXCOORD_0"])
        faces = source.array(prim["indices"]).reshape(-1,3)
        if not mando:
            faces = raider_faces(v,uv,faces)
        elif m == 0:
            # Trim the original gloves at the wrists so fist/weapon poses use the player's fingers.
            centers = v[faces].mean(axis=1)*.01
            keep = np.ones(len(faces),dtype=bool)
            for suffix in ("l","r"):
                wrist = bones["hand_"+suffix]
                axis = wrist - bones["lowerarm_"+suffix]
                axis /= np.linalg.norm(axis)
                keep &= ~(((centers-wrist) @ axis > -.012) & (np.linalg.norm(centers-wrist,axis=1) < .23))
            faces = faces[keep]
        ids, indices = np.unique(faces,return_inverse=True)
        points = v[ids]*.01
        pos = points + warp(points)
        if not mando:
            # Fit the sleeveless top against the actual neutral torso before transferring morphs.
            torso = (np.abs(points[:,0]) < .145) & (points[:,1] > 1.025)
            attrs = body["attributes"]
            skin = kit.array(attrs["POSITION"])
            normal = kit.array(attrs["NORMAL"])
            bands = (np.abs(points[:,0]) > .15) & (points[:,1] > .95)
            fitted = torso | bands
            dist, near = cKDTree(skin).query(pos[fitted], k=6)
            w = 1/np.maximum(dist,.001)**2
            w /= w.sum(axis=1,keepdims=True)
            surface = (skin[near] * w[:,:,None]).sum(1)
            n = (normal[near] * w[:,:,None]).sum(1)
            gap = ((pos[fitted]-surface)*n).sum(axis=1)
            correction = np.maximum(.013-gap,0)
            correction[bands[fitted]] = np.clip(.009-gap[bands[fitted]], -.05, .05)
            pos[fitted] += n*correction[:,None]
        if mando and m == 3:
            pos[:,2] -= .065*np.clip((1.45-pos[:,1])/.8,0,1)
        verts.append(pos)
        cape.extend([mando and m == 3]*len(pos))
        i = materials.index(m)
        texcoords.append((np.clip(uv[ids], .0005, .9995)+[i%grid,i//grid])/grid)
        triangles.append(indices.reshape(-1,3)+count)
        count += len(ids)
    geometry = bind_geometry(kit, body, np.concatenate(verts),np.concatenate(texcoords),np.concatenate(triangles))
    if mando:
        # Keep the cape on the back while the legs and arms move independently.
        v, _, _, joints, weights, _, _ = geometry
        names = list(kit.joints())
        idx = np.asarray(cape)
        t = np.clip((v[idx,1]-.85)/.54,0,1)
        joints[idx] = [names.index("Hips"),names.index("Spine2"),0,0]
        weights[idx] = np.column_stack((1-t,t,np.zeros_like(t),np.zeros_like(t)))
    material = {"name":part, "doubleSided":True, "pbrMetallicRoughness":{
        "baseColorTexture":{"index":texture(maps["albedo"],part+"_albedo")},
        "metallicRoughnessTexture":{"index":texture(maps["orm"],part+"_orm")}}}
    return geometry, material
