"""Fit the newskins wardrobe to the kit; save each selectable piece independently."""
import json
import os
import re
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.interpolate import RBFInterpolator
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree

from build_raider_outfits import registration, read_texture
from build_scifi_outfit import Glb, ROOT, KIT, bind_geometry

# Source filename prefix, menu name, clothing meshes, head mesh, head label, hair/face coverage.
STYLES = [
    ("drift_", "Drift", [1], 2, "Masque de Drift", "face"),
    ("fortnite_harley", "Harley Quinn", [0,1], None, "", ""),
    ("fortnite_leaked", "Indigo Kuno", [0], 3, "Masque Indigo Kuno", "face"),
    ("fortnite_oblivion", "Oblivion", [0], 1, "Casque Oblivion", "helmet"),
    ("iron_man", "Iron Man", [0], 1, "Casque Iron Man", "helmet"),
    ("ishowspeed", "IShowSpeed", [0], None, "", ""),
    ("jules_", "Jules", [0], 2, "Lunettes de Jules", "face"),
    ("ryuji_", "Ryuji", [0], 4, "Chapeau de Ryuji", "crown"),
    ("stormtrooper", "Stormtrooper", [0], 1, "Casque Stormtrooper", "helmet"),
    ("vengeance_", "Vengeance Jones", [0], None, "", ""),
]
FULL_SUIT = {"oblivion","iron_man","stormtrooper","indigo_kuno","vengeance_jones"}


def islands(v, uv, faces):
    _,inv = np.unique(np.column_stack((v,uv)).round(5), axis=0, return_inverse=True)
    t = inv.ravel()[faces]
    graph = coo_matrix((np.ones(t.size), (t.ravel(),np.roll(t,1,axis=1).ravel())),shape=(inv.max()+1,)*2)
    return connected_components(graph,directed=False)[1][t[:,0]]


def static_fit(style, kit):
    # Surveyed joint locations in the two unrigged models, after orienting them Y-up.
    drift = style == "drift"
    mid = [(0,.93,0),(0,1.40,0),(0,1.52,0)] if drift else [(0,.90,0),(0,1.37,0),(0,1.47,0)]
    limb = ([(.16,1.41,0),(.31,1.19,0),(.39,.93,.025),(.09,.89,0),(.14,.49,0),(.16,.09,0)]
            if drift else [(.15,1.35,-.060),(.32,1.16,-.057),(.49,1.045,.04),(.09,.89,0),(.12,.52,.01),(.15,.09,-.02)])
    old,new = [],[]
    def landmark(point,name):
        for offset in ([0,0,0],[0,0,.085]):
            old.append(np.add(point,offset))
            new.append(kit.joints()[name]+offset)
    for p,n in zip(mid,("Hips","Spine2","Neck")):
        landmark(p,n)
    for side,sign in (("Left",1),("Right",-1)):
        for p,n in zip(limb,("Arm","ForeArm","Hand","UpLeg","Leg","Foot")):
            landmark((p[0]*sign,p[1],p[2]),side+n)
        old.append([sign*.16,0,0]);new.append([sign*.197,0,.02])
    return RBFInterpolator(np.array(old),np.array(new)-old,smoothing=.00003)


def source_bone_map(src, names):
    doc = src.doc
    parent = {child:i for i,node in enumerate(doc["nodes"]) for child in node.get("children",[])}
    mapped = {"pelvis":"Hips","spine_01":"Spine","spine_02":"Spine","spine_03":"Chest",
              "spine_04":"UpperChest","spine_05":"UpperChest","neck_01":"Neck","head":"Head"}
    for side,suffix in (("Left","l"),("Right","r")):
        for bone,target in (("clavicle","Shoulder"),("upperarm","UpperArm"),("lowerarm","LowerArm"),
                            ("hand","Hand"),("thigh","UpperLeg"),("calf","LowerLeg"),("foot","Foot"),("ball","Toes")):
            mapped[bone+"_"+suffix]=side+target
    result=[]
    for joint in doc["skins"][0]["joints"]:
        while True:
            key=re.sub(r"_0\d+$","",doc["nodes"][joint].get("name",""))
            if key in mapped or joint not in parent:
                result.append(names.index(mapped.get(key,"Hips")))
                break
            joint=parent[joint]
    return np.array(result)


def fit_torso(pos, skin, style):
    # Match cross-sections, keeping the shoulder straps and neckline of the authored top.
    # The two static exports use different chest depths from the MakeHuman bind pose.
    if style=="harley_quinn":
        pos[:,2] += .072 * np.clip((.235-np.abs(pos[:,0]))/.10,0,1)
    center_z = .038
    pick = (pos[:,1] > .87) & (pos[:,1] < 1.445) & (np.abs(pos[:,0]) < .197)
    vertices = pos[pick]
    torso = skin[(skin[:,1]>.80) & (skin[:,1]<1.48) & (np.abs(skin[:,0])<.205)]
    def polar(q):
        angle = np.arctan2((q[:,2]-center_z)*1.48,q[:,0])
        return np.column_stack((q[:,1]*6,np.cos(angle),np.sin(angle)))
    distance, near = cKDTree(polar(torso)).query(polar(vertices),k=5)
    weight = 1/np.maximum(distance,.002)**2
    weight /= weight.sum(1,keepdims=True)
    axis = np.array([0,center_z])
    target_radius = np.linalg.norm(torso[near][:,:,[0,2]]-axis,axis=2)
    radial = vertices[:,[0,2]]-axis
    radius = np.linalg.norm(radial,axis=1)
    scale = np.maximum(1, ((target_radius*weight).sum(1)+.012)/np.maximum(radius,.01))
    vertices[:,[0,2]] = axis+radial*scale[:,None]
    pos[pick]=vertices


def build_newskins(tmp, godot, body_binding):
    catalog_path = KIT/"kit.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf8"))
    kit = Glb(KIT/"character_kit.glb")
    body = kit.doc["meshes"][next(n["mesh"] for n in kit.doc["nodes"] if n.get("name")=="Body")]["primitives"][0]
    skin = kit.array(body["attributes"]["POSITION"])
    normals = kit.array(body["attributes"]["NORMAL"])
    tree = cKDTree(skin)
    body_v, body_ids, _ = body_binding
    unique, first = np.unique(body_v,axis=0,return_index=True)
    ref_tree = cKDTree(unique)
    bone_map = {v:k for k,v in re.findall(r'"bone_map/(\w+)":&"([^"]+)"',(KIT/"character_kit.glb.import").read_text())}
    names = [bone_map.get("mixamorig_"+s,s) for s in kit.joints()]
    jobs, bindings = [], {}
    (KIT/"wardrobe").mkdir(exist_ok=True)
    for prefix,label,clothes,head,head_label,cover in STYLES:
        slug = label.lower().replace(" ","_")
        path, = (ROOT/"assets/skins/newskins").glob(prefix+"*.glb")
        src = Glb(path)
        rigged = bool(src.doc.get("skins"))
        unit = .01 if rigged and max(p[1] for p in src.joints().values()) > 10 else 1.
        warp = registration(src,kit,unit)[0] if rigged else static_fit(slug,kit)
        selections = [("clothes","c_new_"+slug,clothes,label)]
        if head is not None:
            selections.append(("headwear","gear_"+slug,[head],head_label))
        for group,part,indices,title in selections:
            hat = group == "headwear"
            tile = 1024
            atlas = Image.new("RGB",(tile*len(indices),tile))
            orm = Image.new("RGB",atlas.size,(255,255,0))
            emission = Image.new("RGB",atlas.size)
            has_emission = False
            vertices, uvs, triangles, source_binds = [], [], [], []
            count = 0
            for im,idx in enumerate(indices):
                prim = src.doc["meshes"][idx]["primitives"][0]
                pbr = src.doc["materials"][prim["material"]]["pbrMetallicRoughness"]
                image = read_texture(src,pbr["baseColorTexture"]).resize((tile,tile),Image.Resampling.LANCZOS)
                atlas.paste(image,(im*tile,0))
                m = src.doc["materials"][prim["material"]]
                slot = pbr.get("metallicRoughnessTexture")
                channels = np.array(read_texture(src,slot).resize((tile,tile))) if slot else np.full((tile,tile,3),255,np.uint8)
                channels[:,:,1] = channels[:,:,1]*pbr.get("roughnessFactor",1)
                channels[:,:,2] = channels[:,:,2]*pbr.get("metallicFactor",1)
                orm.paste(Image.fromarray(channels),(im*tile,0))
                if "emissiveTexture" in m:
                    glow = np.array(read_texture(src,m["emissiveTexture"]).resize((tile,tile)))
                    emission.paste(Image.fromarray((glow*np.array(m.get("emissiveFactor",[0,0,0]))).clip(0,255).astype(np.uint8)),(im*tile,0))
                    has_emission = True
                a = prim["attributes"]
                v = src.array(a["POSITION"])
                uv = src.array(a["TEXCOORD_0"])
                f = src.array(prim["indices"]).reshape(-1,3)
                points = v*unit
                if slug=="drift":
                    points = points*[-1,1,-1]*.92 + [0,.94,0]
                elif slug=="harley_quinn":
                    points = points[:,[0,2,1]]*[1,1,-1]
                # Remove baked skin islands and separate personal accessories from the original hair.
                exclude = {"ishowspeed":[0,1,14,29,46,48],"drift":[0,1,19,20],
                           "jules":[4,5,24,36,48],"ryuji":[5,160]}.get(slug,[])
                if hat and slug=="jules":
                    # Only the goggles on the crown, excluding braids and earrings.
                    center = points[f].mean(1)
                    f = f[(center[:,1]>1.646) & (center[:,2]>-.020)]
                elif hat and slug=="drift":
                    labels = islands(v,np.zeros_like(uv),f)
                    f = f[labels==0]
                elif not hat and exclude:
                    f = f[~np.isin(islands(v,uv,f),exclude)]
                used,faces = np.unique(f,return_inverse=True)
                p = points[used]
                pos = p+warp(p)
                faces = faces.reshape(-1,3)
                if hat:
                    if cover=="helmet":
                        # Fit visor and crown to the adult neutral head, retaining the source silhouette.
                        pos = p*([1.16,1.12,1.17]) + [0,1.78-p[:,1].max()*1.12,.064]
                    elif cover=="crown":
                        pos = p + [0,.055,.044]
                    else:
                        pos[:,2] += .018
                        if slug=="indigo_kuno":
                            pos = p*[1.13,1.02,1.30] + [0,1.636-1.54*1.02,.065]
                        elif slug=="jules":
                            pos = p*[1.15,1.05,1.30] + [0,1.765-1.706*1.05,.06]
                else:
                    if not rigged or slug=='jules':
                        fit_torso(pos,skin,slug)
                    centers = pos[faces].mean(1)
                    keep = np.ones(len(faces),dtype=bool)
                    for side in ("Left","Right"):
                        wrist = kit.joints()[side+"Hand"]
                        axis = wrist-kit.joints()[side+"ForeArm"]
                        axis /= np.linalg.norm(axis)
                        keep &= ~(((centers-wrist)@axis>-.009) & (np.linalg.norm(centers-wrist,axis=1)<.22))
                    faces = faces[keep]
                    dist, near = tree.query(pos,k=5)
                    w = 1/np.maximum(dist,.001)**2
                    w /= w.sum(axis=1,keepdims=True)
                    n = (normals[near]*w[:,:,None]).sum(1)
                    gap = ((pos-(skin[near]*w[:,:,None]).sum(1))*n).sum(1)
                    pos += n*np.clip(.008-gap,0,.045)[:,None]
                ids,faces = np.unique(faces,return_inverse=True)
                if rigged and not hat:
                    remap = source_bone_map(src, names)
                    a_bones = src.array(a["JOINTS_0"])[used[ids]]
                    a_weights = src.array(a["WEIGHTS_0"])[used[ids]]
                    source_binds.append((remap[a_bones],a_weights))
                vertices.append(pos[ids])
                uvs.append((np.clip(uv[used[ids]],0,1)+[im,0])/[len(indices),1])
                triangles.append(faces.reshape(-1,3)+count)
                count += len(ids)
            v,n,uv,joints,weights,faces,morphs = bind_geometry(kit,body,np.concatenate(vertices),np.concatenate(uvs),np.concatenate(triangles))
            if source_binds:
                joints = np.concatenate([b for b,w in source_binds])
                weights = np.concatenate([w for b,w in source_binds]).astype(float)
                weights /= np.maximum(weights.sum(1,keepdims=True),1e-8)
            if hat:
                joints[:]=0;joints[:,0]=names.index("Head")
                weights[:]=0;weights[:,0]=1
            raw = tmp/(part+"_0.raw")
            distance,near = ref_tree.query(v,k=3)
            blend = 1/np.maximum(distance,.0001)**2
            bindings[raw]=(v,body_ids[first[near],0],blend/blend.sum(1,keepdims=True))
            faces[:,::-1].astype("<i4").tofile(str(raw)+".indices")
            with (tmp/(part+".bin")).open("wb") as f:
                for array in (v,n,uv,*[v+d for d in morphs]):
                    f.write(array.astype("<f4").tobytes())
                f.write(faces[:,::-1].astype("<i4").tobytes())
            with (tmp/(part+".skin")).open("wb") as f:
                f.write(joints.astype("<i4").tobytes());f.write(weights.astype("<f4").tobytes())
            atlas.save(tmp/(part+".png"))
            orm.save(tmp/(part+"-orm.png"))
            if has_emission:
                emission.save(tmp/(part+"-emission.png"))
            jobs.append(dict(emission=has_emission,id=part,count=len(v),color=[1,1,1],alpha=False,bone_names=names,folder="wardrobe"))
            item=dict(id=part,name=title,mesh="wardrobe/"+part+".res")
            if hat: item["cover"]=cover
            else: item["includes_shoes"]=True
            items=catalog["items"][group]
            index=next((i for i,x in enumerate(items) if x["id"]==part),len(items))
            items[index:index+1]=[item]
    (tmp/"hair.json").write_text(json.dumps(dict(jobs=jobs,shapes=catalog["morphs"])))
    subprocess.run([godot,"--headless","--path",str(ROOT),"--script",str(Path(__file__).with_name("save_hair_meshes.gd"))],
                   env={**os.environ,"OE_HAIR_BUILD":str(tmp)},check=True)
    assert len(catalog["items"]["headwear"])<64
    catalog_path.write_text(json.dumps(catalog,indent=1,ensure_ascii=False),encoding="utf8")
    return bindings
