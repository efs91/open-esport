"""Build independently loadable Godot hairstyles from the CC0 MakeHuman proxy library."""
import json
import os
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image

from build_scifi_outfit import Glb, KIT, ROOT, bind_geometry

LIBRARY = ROOT / "assets/skins/Hairs/hair01_cc0(1)"


def properties(lines):
    result = {}
    for line in lines:
        tokens = line.strip().split(None, 1)
        if len(tokens) == 2 and not tokens[0].startswith("#"):
            result[tokens[0]] = tokens[1]
    return result


def read_style(proxy, posed):
    lines = proxy.read_text(encoding="utf-8-sig").splitlines()
    start = lines.index("verts 0")
    info = properties(lines[:start])
    assert info["basemesh"] == "hm08"
    rows = []
    for line in lines[start+1:]:
        tokens = line.split()
        if not tokens:
            continue
        if not tokens[0].isdigit():
            break
        rows.append(tokens)
    data = np.asarray(rows, dtype=float)
    refs, weights = data[:,:3].astype(int), data[:,3:6]
    scale = np.ones(3)*.1
    for axis, key in enumerate(("x_scale","y_scale","z_scale")):
        if key in info:
            a,b,original = info[key].split()
            scale[axis] = abs(posed[int(a),axis]-posed[int(b),axis])/float(original)
    verts = (posed[refs]*weights[:,:,None]).sum(1) + data[:,6:9]*scale
    uv, faces = [], []
    obj = proxy.parent/info["obj_file"]
    for line in obj.read_text().splitlines():
        tokens = line.split()
        if not tokens:
            continue
        if tokens[0] == "vt":
            uv.append((float(tokens[1]),1-float(tokens[2])))
        elif tokens[0] == "f":
            polygon = [tuple(int(i)-1 for i in t.split("/")[:2]) for t in tokens[1:]]
            faces.extend((polygon[0],polygon[j],polygon[j+1]) for j in range(1,len(polygon)-1))
    pairs, indices = np.unique(np.array(faces).reshape(-1,2),axis=0,return_inverse=True)
    vi, ti = pairs.T
    material = properties((proxy.parent/info["material"]).read_text(encoding="utf-8-sig").splitlines())
    return verts[vi],np.array(uv)[ti],indices.reshape(-1,3),refs[vi],weights[vi], material


def build_library(posed, tmp, godot):
    kit = Glb(KIT/"character_kit.glb")
    body = kit.doc["meshes"][next(n["mesh"] for n in kit.doc["nodes"] if n.get("name")=="Body")]["primitives"][0]
    manifest = KIT/"kit.json"
    catalog = json.loads(manifest.read_text(encoding="utf8"))
    items = catalog["items"]["hair"]
    ids = {item["id"] for item in items}
    bindings, jobs = {}, []
    (KIT/"hair").mkdir(exist_ok=True)
    proxies = sorted(LIBRARY.rglob("*.mhclo"))
    if not proxies:
        raise FileNotFoundError(LIBRARY)
    for proxy in proxies:
        part = "h_cc0_"+proxy.stem
        v,uv,faces,refs,weights,mat = read_style(proxy,posed)
        geometry = bind_geometry(kit,body,v,uv,faces)
        _,normals,_,_,_,_,morphs = geometry
        bindings[tmp/(part+"_0.raw")] = (v,refs,weights)
        with (tmp/(part+".bin")).open("wb") as f:
            for arr in (v,normals,uv,*[v+m for m in morphs]):
                f.write(np.asarray(arr,dtype="<f4").tobytes())
            # Godot uses clockwise front faces.
            f.write(faces[:,::-1].astype("<i4").tobytes())
        im = Image.open(proxy.parent/mat["diffuseTexture"]).convert("RGBA")
        im.thumbnail((1024,1024), Image.Resampling.LANCZOS)
        im.save(tmp/(part+".png"))
        jobs.append({"id":part,"count":len(v),"alpha":im.getchannel("A").getextrema()[0]<200,
                     "color":list(map(float,mat.get("diffuseColor","1 1 1").split()))})
        if part not in ids:
            items.append({"id":part,"name":proxy.stem.replace("_"," ").title(), "mesh":"hair/"+part+".res"})
            ids.add(part)
    assert len(items) <= 255
    (tmp/"hair.json").write_text(json.dumps({"jobs":jobs,"shapes":catalog["morphs"]}),encoding="utf8")
    subprocess.run([godot,"--headless","--path",str(ROOT),"--script",str(Path(__file__).with_name("save_hair_meshes.gd"))],
                   env={**os.environ,"OE_HAIR_BUILD":str(tmp)},check=True)
    manifest.write_text(json.dumps(catalog,indent=1,ensure_ascii=False),encoding="utf8")
    return bindings
