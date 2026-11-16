"""Boîtes de collision des arbres, rochers et falaises (pour le serveur et la prédiction) -> data/nature_boxes.json.
Tronc d'arbre : colonne fine ; rochers : boîte centrée un peu plus petite que l'emprise du modèle (les rochers de moins de 45 cm se franchissent)."""
import os, json
import numpy as np
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "maps", "frontiere01", "data")
dims = json.load(open(os.path.join(os.path.dirname(__file__), "models.json")))
manifest = json.load(open(os.path.join(DATA, "manifest.json")))
boxes = []
stats = {}
for a in manifest:
    glb = a["glb"]
    if glb.startswith("fir_far") or glb.startswith(("pine_fern", "pine_grass", "pine_branch", "nama_gazania", "nama_ursinia", "nama_stinkkruid", "nama_iceplant", "nama_succulent", "nama_searsia", "nama_othonna")):
        continue
    sx, sy, sz = dims[glb]
    d = np.fromfile(os.path.join(DATA, "inst_%d.bin" % a["id"]), "<f4").reshape(-1, 12)
    n = 0
    for t in d:
        s = float(np.linalg.norm(t[[0, 3, 6]]))          # échelle horizontale (colonne x de la base)
        sv = float(np.linalg.norm(t[[1, 4, 7]]))         # échelle verticale
        x, y, z = float(t[9]), float(t[10]), float(t[11])
        if glb.startswith("fir_"):
            half, top = 0.32 * s, sy * sv
        elif glb.startswith("nama_quiver"):
            half, top = 0.28 * s, sy * sv * 0.9
        elif glb == "pine_deadlog":
            half, top = 0.45 * s, 0.6 * sv
        else:
            # emprise du rocher tourné selon son orientation (boîte alignée sur les axes), légèrement rentrée pour les formes arrondies
            c, sn = abs(float(t[0])) / s, abs(float(t[2])) / s
            aspect = max(sx, sz) / max(min(sx, sz), 0.01)
            fat = 0.9 if aspect < 1.8 else 0.65
            hx = 0.5 * (c * sx + sn * sz) * s * fat
            hz = 0.5 * (sn * sx + c * sz) * s * fat
            top = sy * sv * 0.95
            if top < 0.25:
                continue
            boxes.append([round(x - hx, 2), round(y - 0.8, 2), round(z - hz, 2), round(x + hx, 2), round(y + top, 2), round(z + hz, 2)])
            n += 1
            continue
        if top < 0.25 and not glb.startswith(("fir_", "nama_quiver")):
            continue
        boxes.append([round(x - half, 2), round(y - 0.8, 2), round(z - half, 2), round(x + half, 2), round(y + top, 2), round(z + half, 2)])
        n += 1
    stats[glb] = n
print("boîtes :", len(boxes), {k: v for k, v in stats.items() if v})
json.dump({"boxes": boxes}, open(os.path.join(DATA, "nature_boxes.json"), "w"))
