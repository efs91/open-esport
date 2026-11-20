"""Build the game's sparse morph atlas from installed MakeHuman 1.3 data and Godot's imported kit.
Usage: python tools/blender/build_character_details.py MAKEHUMAN_DATA GODOT_MONO_EXE MPFB_MIXAMO_RIG_JSON
Requires numpy and scipy. Coordinates in targets.npz are thousandths of a decimetre.
"""
import argparse
import gzip
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import os

import numpy as np
from scipy.spatial import cKDTree

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "assets/characters/kit"
BASE_COUNT = 19158


def load_rows(data):
    translate = json.loads((data / "languages/french_generic.json").read_text(encoding="utf8"))
    tr = {k.casefold(): v for k, v in translate.items() if isinstance(v, str)}
    tr.update({k.casefold(): v for k,v in {
        "Body shapes":"Silhouettes", "Measure":"Mensurations", "Gender":"Poitrine", "Left cheek":"Joue gauche",
        "Right cheek":"Joue droite", "Chin/jaw":"Menton et mâchoire", "Macro":"Proportions générales",
        "Left shoulder":"Épaule gauche", "Right shoulder":"Épaule droite", "Legs":"Jambes", "Knee":"Genou",
        "Female body shapes":"Silhouettes féminines", "Male body shapes":"Silhouettes masculines",
        "Female hormonal components":"Volumes féminins", "Male hormonal components":"Volumes masculins",
        "Head Age Decr":"Maturité des traits", "Head Angle In":"Inclinaison du crâne", "Head fat":"Volume des joues",
        "Head Diamond":"Visage en losange", "Head Invertedtriangular":"Triangle inversé", "Head Oval":"Visage ovale",
        "Head Rectangular":"Visage rectangulaire", "Head Round":"Visage rond", "Head Square":"Visage carré",
        "Head Triangular":"Visage triangulaire", "Cleft chin":"Fossette du menton","Dimples":"Fossettes",
        "Dorsi muscle":"Muscles dorsaux","Double neck":"Double menton","Eye bag distorsion":"Forme des poches",
        "Eye bag height":"Hauteur des poches","Eye bag volume":"Volume des poches","Eye epicanthus":"Pli épicanthique",
        "Eyebrows bulge":"Relief des arcades","Eyefold angle":"Angle du pli palpébral",
        "Eyefold position":"Position du pli palpébral","Eyefold volume":"Volume du pli palpébral",
        "Fingers distance":"Écartement des doigts","Forehead bulge":"Relief du front","Knee circ.":"Tour de genou",
        "Laugh-lines":"Sillons du sourire","Lowerarm muscles":"Muscles de l’avant-bras","Lowerleg muscle":"Galbe du mollet",
        "Lowerlegs height":"Longueur des jambes","Move base vert.":"Hauteur de la base","Navel bump":"Relief du nombril",
        "Navel position":"Position du nombril","Nipple point":"Projection du mamelon","Nipple size":"Taille du mamelon",
        "Nose Compression Compress":"Compression du nez","Nose Curve Concave":"Courbure du nez",
        "Nose Greek Decr":"Profil grec","Nose Hump Decr":"Bosse de l’arête","Nose Nostrils Angle Down":"Angle des narines",
        "Nose Volume Decr":"Volume du nez","Pectoral muscle":"Muscles pectoraux","Scale depth of nape":"Profondeur de la nuque",
        "Scale depth of parietal side":"Profondeur pariétale","Scale lowerlip width":"Largeur de la lèvre inférieure",
        "Shoulder muscle mass":"Musculature de l’épaule","Tone of side chin":"Définition du menton","Upperarm muscles":"Muscles du bras",
        "Upperleg muscle":"Muscles de la cuisse","Upperlegs height":"Longueur des cuisses","Waist vertical position":"Hauteur de la taille"
    }.items()})
    presets = {"apple":"Pomme","diamond":"Losange","full hourglass":"Sablier marqué","neat hourglass":"Sablier",
               "inverted triangle":"Triangle inversé","lean column":"Colonne fine","rectangle":"Rectangle","triangle":"Triangle",
               "trapezoid":"Trapèze","adrenal":"Surrénalien","liver":"Hépatique","ovary":"Ovarien","thyroid":"Thyroïdien","gonadal":"Gonadique"}
    def label(s):
        if s.startswith("Elvs "):
            return presets[s.lower().split(" ",2)[2]]
        return tr.get(s.casefold(), s[0].upper() + s[1:])

    rows = []
    for family in ("modeling", "measurement", "bodyshapes"):
        panels = json.loads((data / f"modifiers/{family}_sliders.json").read_text(encoding="utf8"))
        for panel, page in panels.items():
            for section, sliders in page["modifiers"].items():
                for slider in sliders:
                    key = slider["mod"]
                    if slider.get("enabledCondition") == "hasGenitals":
                        continue  # The game kit uses the standard closed body mesh.
                    if key.rsplit("/", 1)[1] in ("Gender", "Age", "Muscle", "Weight", "Height"):
                        continue
                    title = slider.get("label", key.split("/")[1].split("|")[0].replace("-", " ").title())
                    rows.append(dict(id=key, page=label(page.get("label", panel)), section=label(section),
                                     label=label(title), face=panel == "Face"))
    # These indices are part of both saved characters and the network protocol.
    previous = OUT / "details.json"
    order = {r["id"]: i for i, r in enumerate(json.loads(previous.read_text(encoding="utf8")))} if previous.exists() else {}
    assert set(order).issubset({r["id"] for r in rows}), "A shipped modifier was removed"
    return sorted(rows, key=lambda r: (order.get(r["id"], len(order)), r["id"]))


def build_targets(data, rows):
    archive = np.load(data / "targets.npz")
    def get(name):
        if name is None:
            return np.zeros((BASE_COUNT, 3), dtype=np.int16)
        arr = get(None)
        arr[archive["targets/" + name + ".index"]] = archive["targets/" + name + ".vector"]
        return arr

    young = "macrodetails/caucasian-male-young"
    macros = {"macrodetails-proportions/BodyProportions": tuple("macrodetails/proportions/male-young-averagemuscle-averageweight-" + x + "proportions" for x in ("uncommon","ideal")),
              "breast/BreastSize": tuple("breast/female-young-averagemuscle-averageweight-" + x + "cup-averagefirmness" for x in ("min","max")),
              "breast/BreastFirmness": tuple("breast/female-young-averagemuscle-averageweight-averagecup-" + x + "firmness" for x in ("min","max"))}
    targets = []
    for row in rows:
        key = row["id"]
        if key in macros:
            lo, hi = map(get, macros[key])
        elif key.startswith("macrodetails/"):
            # The kit's neutral reference is the Caucasian adult. Ethnic weights are normalized at runtime.
            race = key.split("/")[1].lower()
            lo, hi = get(None), get("macrodetails/" + race + "-male-young") - get(young)
        elif "|" in key:
            left, right = key.split("|")
            lo, hi = get(left), get(left.rsplit("-", 1)[0] + "-" + right)
        else:
            lo, hi = get(None), get(key)
        row["signed"] = bool(np.any(lo))
        targets.append((lo, hi))
    return targets


def mapping(raw, data, posed):
    floats = np.fromfile(raw, dtype="<f4")
    count = len(floats) // 5
    xyz, uv = floats[:count*3].reshape(-1,3), floats[count*3:].reshape(-1,2)
    name = raw.stem.rsplit("_",1)[0]
    if name == "Body":
        obj = np.load(data / "3dobjs/base.npz")
        refs = np.tile(np.arange(BASE_COUNT)[:,None], (1,3))
        weights = np.zeros_like(refs, dtype=float)
        weights[:,0] = 1
        faces = obj["group"] == 0
        positions = posed
    else:
        asset = name.split("_",1)[1].replace("low_poly","low-poly")
        proxy = np.load(next(data.rglob(asset + ".mhpxy")))
        obj = np.load(next(data.rglob(asset + ".npz")))
        if int(proxy["num_refverts"]) == 1:
            refs = np.tile(proxy["ref_vIdxs"].reshape(-1,1),(1,3))
            weights = np.zeros_like(refs,dtype=float)
            weights[:,0] = 1
        else:
            refs, weights = proxy["ref_vIdxs"], proxy["weights"]
        faces = slice(None)
        positions = (posed[refs] * weights[:,:,None]).sum(1)
    vi = obj["fvert"][faces].ravel()
    uv_orig = obj["texco"][obj["fuvs"][faces]].reshape(-1,2).copy()
    uv_orig[:,1] = 1 - uv_orig[:,1]
    # Duplicate UVs are resolved in model space (mirrored pieces and UV seams).
    _, candidates = cKDTree(uv_orig).query(uv,k=12)
    duv = np.linalg.norm(uv[:,None,:] - uv_orig[candidates], axis=2)
    dist = np.linalg.norm(xyz[:,None,:] - positions[vi[candidates]],axis=2)
    choice = vi[candidates[np.arange(count),np.argmin(duv*900 + dist,axis=1)]]
    return xyz, refs[choice], weights[choice]


def garment_interior(body, normals, garment, triangles):
    """Project skin along its normal onto the cloth, leaving a margin around openings."""
    skin = cKDTree(body)
    covered = np.zeros(len(body), dtype=bool)
    # Weld UV seams before locating hems, necklines and cuffs.
    positions, welded = np.unique(garment, axis=0, return_inverse=True)
    tri = welded.ravel()[triangles]
    edges = np.sort(np.concatenate([tri[:, [0, 1]], tri[:, [1, 2]], tri[:, [2, 0]]]), axis=1)
    edges, counts = np.unique(edges, axis=0, return_counts=True)
    boundary = positions[edges[counts == 1]]
    midpoints = boundary.mean(axis=1)
    half_lengths = np.linalg.norm(boundary[:, 1] - boundary[:, 0], axis=1) / 2
    for a, b, c in garment[triangles]:
        center = (a + b + c) / 3
        radius = max(np.linalg.norm(v - center) for v in (a, b, c))
        ids = np.asarray(skin.query_ball_point(center, radius + .065), dtype=int)
        if not len(ids):
            continue
        e, g = b - a, c - a
        n = np.cross(g, e)  # Godot uses clockwise triangles.
        area = np.linalg.norm(n)
        if area < 1e-9:
            continue
        n /= area
        dot = normals[ids] @ n
        ids = ids[dot > .35]
        if not len(ids):
            continue
        t = ((a - body[ids]) @ n) / (normals[ids] @ n)
        hit = body[ids] + normals[ids] * t[:, None]
        local = hit - a
        u = np.cross(g, local) @ n / area
        v = np.cross(local, e) @ n / area
        inside = (u >= 0) & (v >= 0) & (u + v <= 1) & (np.abs(t) < .065)
        # Retain skin immediately around the nearest boundary segment.
        for start, end in boundary[np.linalg.norm(midpoints - center, axis=1) <= radius + half_lengths + .012]:
            if not inside.any():
                break
            seg = end - start
            h = np.clip(((hit - start) @ seg) / max(seg @ seg, 1e-12), 0, 1)
            inside &= np.linalg.norm(hit - start - h[:, None] * seg, axis=1) > .012
        covered[ids[inside]] = True
    return covered


SCIFI = "c_sci_fi_battle_royale"


CUSTOM = {SCIFI, "c_mandalorian", "c_renegade_raider"}


def scifi_coverage(body, kit, cuff=.20):
    # Preserve a little skin inside the open collar and cuffs as joints bend.
    joints = kit.joints()
    covered = body[:, 1] < joints["Neck"][1] - .068
    for side in ("Left", "Right"):
        wrist = joints[side + "Hand"]
        axis = wrist - joints[side + "ForeArm"]
        axis /= np.linalg.norm(axis)
        hand = ((body - wrist) @ axis > -cuff) & (np.linalg.norm(body - wrist, axis=1) < .34)
        covered &= ~hand
    return covered


def write_coverage(files, data, bindings):
    assert all(raw.stem.endswith("_0") for raw in files), "Coverage expects one surface per kit piece"
    from build_scifi_outfit import Glb
    layers = {}
    for raw in files:
        name = raw.stem.rsplit("_", 1)[0]
        if name in CUSTOM:
            layers[name] = (50, np.zeros(BASE_COUNT, dtype=bool))
        elif name.startswith(("c_", "s_")):
            with np.load(next(data.rglob(name[2:] + ".mhpxy"))) as proxy:
                layers[name] = (int(proxy["z_depth"].item()), proxy["deleteVerts"])
    hats = [raw.stem[:-2] for raw in files if raw.stem.startswith("hat_")]
    for hat in hats:
        layers[hat] = (60, None)
    assert len(layers) <= 31
    body_file = next(raw for raw in files if raw.stem == "Body_0")
    body = bindings[body_file][0]
    normals = np.fromfile(str(body_file) + ".normals", dtype="<f4").reshape(-1, 3)
    interiors = {}
    for raw in files:
        part = raw.stem.rsplit("_", 1)[0]
        if part in (SCIFI, "c_mandalorian"):
            interiors[part] = scifi_coverage(body, Glb(OUT / "character_kit.glb"), .20 if part == SCIFI else .05)
        elif part in layers and part not in hats:
            triangles = np.fromfile(str(raw) + ".indices", dtype="<i4").reshape(-1, 3)
            interiors[part] = garment_interior(body, normals, bindings[raw][0], triangles)
            if part == "c_renegade_raider":
                interiors[part] |= (body[:,1] < 1.06) & (np.abs(body[:,0]) < .32)
    surfaces = {}
    for raw in files:
        name = raw.stem.rsplit("_", 1)[0]
        hair = name.startswith("h_")
        if name in hats or (name != "Body" and name not in layers and not hair):
            continue
        xyz, refs, weights = bindings[raw]
        flags = np.zeros(len(xyz), dtype="<i4")
        for bit, (part, (depth, deleted)) in enumerate(layers.items()):
            if part in hats:
                if hair:
                    # Clip hair cards above the brim while retaining hair below it.
                    covered = xyz[:,1] > (1.67 if part == "hat_fedora" else 1.60)
                elif name == "Body" and part == "hat_mandalorian":
                    covered = xyz[:,1] > 1.53
                else:
                    continue
                flags |= covered.astype("<i4") << bit
                continue
            if hair:
                continue
            if name in layers and depth <= layers[name][0]:
                continue
            parents = deleted[refs]
            covered = np.where(np.any(weights[:, 1:] != 0, axis=1), parents.sum(1) >= 2, parents[:, 0])
            if name == "Body":
                covered |= interiors[part]
            flags |= covered.astype("<i4") << bit
        if flags.any():
            surfaces[raw.stem] = (xyz, flags)
    with (OUT / "coverage.bytes").open("wb") as dest, gzip.GzipFile(fileobj=dest, mode="wb", mtime=0) as out:
        def write_name(name):
            encoded = name.encode()
            out.write(struct.pack("<B", len(encoded)) + encoded)
        out.write(struct.pack("<I", len(layers)))
        for name in layers:
            write_name(name)
        out.write(struct.pack("<I", len(surfaces)))
        for name, (xyz, flags) in surfaces.items():
            write_name(name)
            out.write(struct.pack("<I", len(flags)))
            out.write(xyz.astype("<f4").tobytes())
            out.write(flags.tobytes())


def main():
    cli = argparse.ArgumentParser()
    cli.add_argument("data",type=Path)
    cli.add_argument("godot")
    cli.add_argument("rig",type=Path,help="MPFB data/rigs/standard/rig.mixamo.json")
    args = cli.parse_args()
    data = args.data
    rows = load_rows(data)
    targets = build_targets(data,rows)
    base = np.load(data/"3dobjs/base.npz")
    posed = base["coord"].copy() * .1
    archive = np.load(data/"targets.npz")
    for t in ("macrodetails/caucasian-male-young","macrodetails/universal-male-young-averagemuscle-averageweight"):
        posed[archive["targets/"+t+".index"]] += archive["targets/"+t+".vector"] * .0001
    posed[:,1] -= posed[np.unique(base["fvert"][base["group"]==0]),1].min()
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([args.godot,"--headless","--path",str(ROOT),"--script",str(Path(__file__).with_name("character_vertices.gd"))],
                       check=True,env={**os.environ,"OE_MORPH_OUT":tmp})
        files=sorted(Path(tmp).glob("*.raw"))
        customs = {raw for raw in files if raw.stem.rsplit("_", 1)[0] in CUSTOM or raw.stem.startswith("hat_")}
        bindings = {raw: mapping(raw, data, posed) for raw in files if raw not in customs}
        for custom in customs:
            body, body_refs, _ = bindings[Path(tmp) / "Body_0.raw"]
            coords = np.fromfile(custom, dtype="<f4")
            coords = coords[:len(coords)//5*3].reshape(-1, 3)
            unique, first = np.unique(body, axis=0, return_index=True)
            dist, near = cKDTree(unique).query(coords, k=3)
            weights = 1 / np.maximum(dist, .0001)**2
            bindings[custom] = (coords, body_refs[first[near], 0], weights / weights.sum(1, keepdims=True))
        expected = json.loads((OUT / "kit.json").read_text(encoding="utf8"))["items"]
        assert {raw.stem for raw in files} == {"Body_0"} | {it["id"]+"_0" for group in expected.values() for it in group}
        write_coverage(files, data, bindings)
        with (OUT/"details.bytes").open("wb") as dest, gzip.GzipFile(fileobj=dest,mode="wb",mtime=0) as out:
            out.write(struct.pack("<II",len(rows),BASE_COUNT))
            for pair in targets:
                for a in pair:
                    idx=np.flatnonzero(np.any(a,axis=1))
                    out.write(struct.pack("<I",len(idx)))
                    values=np.column_stack((idx,a[idx])).astype("<i2")
                    out.write(values.tobytes())
            out.write(struct.pack("<I",len(files)))
            for raw in files:
                xyz,refs,weights=bindings[raw]
                key=raw.stem.encode()
                out.write(struct.pack("<B",len(key))+key+struct.pack("<I",len(xyz)))
                out.write(xyz.astype("<f4").tobytes())
                out.write(refs.astype("<u2").tobytes())
                out.write(weights.astype("<f4").tobytes())
            # Use the same joint helpers and anatomical landmarks as the MPFB kit builder.
            rig = json.loads(args.rig.read_text(encoding="utf8"))["bones"]
            names = re.findall(r'"bone_map/(\w+)":&"([^"]+)"',(OUT/"character_kit.glb.import").read_text(encoding="utf8"))
            starts = list(base["fgidx"]) + [len(base["fgstr"])]
            groups = [base["fgstr"][a:b].tobytes().decode() for a,b in zip(starts,starts[1:])]
            out.write(struct.pack("<I",len(names)))
            for name, source in names:
                head = rig[source.replace("_",":",1)]["head"]
                if head["strategy"] == "CUBE":
                    indices = np.unique(base["fvert"][base["group"]==groups.index(head["cube_name"])])
                else:
                    assert head["strategy"] == "MEAN"
                    indices = head["vertex_indices"]
                key = name.encode()
                out.write(struct.pack("<B",len(key))+key+struct.pack("<I",len(indices)))
                out.write(np.array(indices,dtype="<u2").tobytes())
    (OUT/"details.json").write_text(json.dumps(rows,ensure_ascii=False,indent=1),encoding="utf8")
    print(len(rows),"detail controls", (OUT/"details.bytes").stat().st_size, "compressed bytes")


if __name__ == "__main__":
    main()
