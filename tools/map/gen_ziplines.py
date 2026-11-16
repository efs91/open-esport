"""Tyroliennes de Frontière 01 : un câble (dans une gaine de verre) le long de chaque route et de chaque chemin, avec des piliers
et des tremplins de lancement. Sortie : maps/frontiere01/data/ziplines.json
  lines  : [{"name", "points": [[x,y,z]...] (tous les ~4 m), "pylons": [[x, sol_y, z, haut_y]...]}]
  pads   : [{"pos": [x,y,z], "line": i, "dist": d}]   (tremplin au sol et distance du point visé sur le câble)
"""
import os, json, math
import numpy as np
from layout import *

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "maps", "frontiere01", "data")
H = np.load(os.path.join(DATA, "masks.npz"))["H"].astype(np.float32)

STEP = 4.0              # espacement des points du câble (m)
CLEAR = 7.0             # hauteur du câble au-dessus du relief (m)
MIN_CLEAR = 5.6         # jamais moins que ça au-dessus du relief voisin
MAX_SLOPE = 0.9         # pente maximale du câble (m/m) : plus raide, on le relève au lieu de le couper
PYLON_EVERY = 48.0
PAD_EVERY = 80.0
PAD_MAX_RISE = 18.0     # un tremplin n'est posé que là où le câble est au plus à cette hauteur au-dessus de lui (m)
ZONE_KEEP_OUT = 0.88    # on n'entre pas dans les zones construites (rayon * ce facteur)


def ground(x, z):
    return float(H[int(round(z)) + HALF, int(round(x)) + HALF])


def ground_max(x, z, r=3):
    i, j = int(round(x)) + HALF, int(round(z)) + HALF
    return float(H[max(j - r, 0): j + r + 1, max(i - r, 0): i + r + 1].max())


def catmull(pts, step):
    p = np.array(pts, np.float64)
    ext = np.vstack([p[0] * 2 - p[1], p, p[-1] * 2 - p[-2]])
    dense = []
    for i in range(1, len(ext) - 2):
        p0, p1, p2, p3 = ext[i - 1], ext[i], ext[i + 1], ext[i + 2]
        n = max(2, int(np.linalg.norm(p2 - p1) / 1.0))
        for k in range(n):
            t = k / n
            dense.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    dense.append(p[-1])
    dense = np.array(dense)
    # rééchantillonnage à pas constant
    seg = np.linalg.norm(np.diff(dense, axis=0), axis=1)
    cum = np.concatenate([[0], np.cumsum(seg)])
    n = max(2, int(cum[-1] / step))
    s = np.linspace(0, cum[-1], n + 1)
    return np.stack([np.interp(s, cum, dense[:, 0]), np.interp(s, cum, dense[:, 1])], 1)


def in_zone(x, z):
    for zn in ZONES.values():
        if math.hypot(x - zn["c"][0], z - zn["c"][1]) < zn["r"] * ZONE_KEEP_OUT:
            return True
    return False


def road_near(x, z):
    return False


def smooth1d(v, r):
    k = np.ones(2 * r + 1) / (2 * r + 1)
    pad = np.pad(v, r, mode="edge")
    return np.convolve(pad, k, mode="valid")


def cable_heights(xz):
    """Hauteur du câble en chaque point : au-dessus du relief (CLEAR, jamais moins de MIN_CLEAR), sans jamais dépasser MAX_SLOPE de pente.
    Quand le relief est trop raide, on relève le côté bas du câble (il passe haut au-dessus du vallon, sur des piliers plus hauts) au lieu
    de couper la ligne : les routes restent entièrement couvertes."""
    terr = np.array([ground_max(x, z) for x, z in xz])
    y = smooth1d(terr, 4) + CLEAR
    y = np.maximum(y, terr + MIN_CLEAR)
    for _ in range(4):
        for i in range(1, len(y)):
            y[i] = max(y[i], y[i - 1] - MAX_SLOPE * STEP)
        for i in range(len(y) - 2, -1, -1):
            y[i] = max(y[i], y[i + 1] - MAX_SLOPE * STEP)
        y = smooth1d(y, 1)
        y = np.maximum(y, terr + MIN_CLEAR)
    # le lissage peut recréer de petits dépassements : dernière passe de relèvement
    for i in range(1, len(y)):
        y[i] = max(y[i], y[i - 1] - MAX_SLOPE * STEP)
    for i in range(len(y) - 2, -1, -1):
        y[i] = max(y[i], y[i + 1] - MAX_SLOPE * STEP)
    return y


lines, pads = [], []
specs = list(ROADS.items()) + list(TRAILS.items())
for name, spec in specs:
    xz = catmull(spec["pts"], STEP)
    # on garde les tronçons hors des zones construites (le plus long, puis les autres)
    runs, cur = [], []
    for x, z in xz:
        if in_zone(x, z):
            if len(cur) >= 4: runs.append(cur)
            cur = []
        else:
            cur.append((x, z))
    if len(cur) >= 4: runs.append(cur)
    for ri, run in enumerate(runs):
        pts = np.array(run)
        y = cable_heights(pts)
        pylons = []
        acc = 0.0
        last_p = -1e9
        d = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(pts, axis=0), axis=1))])
        for i in range(len(pts)):
            if i == 0 or i == len(pts) - 1 or d[i] - last_p >= PYLON_EVERY:
                gy = ground(pts[i][0], pts[i][1])
                pylons.append([round(float(pts[i][0]), 2), round(gy, 2), round(float(pts[i][1]), 2), round(float(y[i]), 2)])
                last_p = d[i]
        line = {"name": name + (f" #{ri + 1}" if len(runs) > 1 else ""), "points": [[round(float(pts[i][0]), 2), round(float(y[i]), 2), round(float(pts[i][1]), 2)] for i in range(len(pts))], "pylons": pylons}
        li = len(lines)
        lines.append(line)
        # tremplins : un tous les PAD_EVERY mètres au bord de la route (côté alterné ; si le terrain ne convient pas, on essaie
        # l'autre côté puis d'autres écarts), plus un près de chaque extrémité
        side = 1.0
        marks = [min(12.0, d[-1] / 2)]
        m = marks[0] + PAD_EVERY
        while m < d[-1] - 12.0:
            marks.append(m)
            m += PAD_EVERY
        if d[-1] - marks[-1] > 30: marks.append(d[-1] - 10.0)
        def try_place(i, side):
            """Cherche un emplacement de tremplin au bord de la route au point i (côté préféré d'abord) ; refuse si le câble est trop haut."""
            tx, tz = pts[i + 1] - pts[i - 1]
            L = math.hypot(tx, tz) or 1.0
            nx, nz = -tz / L, tx / L
            for sd in (side, -side):
                for off in (4.5, 3.6, 6.0):
                    px, pz = pts[i][0] + nx * off * sd, pts[i][1] + nz * off * sd
                    if in_zone(px, pz) or abs(px) > PLAY - 15 or abs(pz) > PLAY - 15: continue
                    if road_near(px, pz): continue
                    if abs(ground(px + 2, pz) - ground(px - 2, pz)) > 2.6 or abs(ground(px, pz + 2) - ground(px, pz - 2)) > 2.6: continue
                    if y[i] - ground(px, pz) > PAD_MAX_RISE: continue       # câble trop haut : le tremplin ne l'atteindrait pas
                    return (px, pz)
            return None

        for mk in marks:
            placed, pi = None, 0
            # au repère voulu, sinon un peu plus loin ou en deçà (là où le câble repasse assez bas)
            for shift in (0.0, 10.0, -10.0, 20.0, -20.0, 32.0, -32.0):
                i = int(np.searchsorted(d, mk + shift))
                i = max(1, min(i, len(pts) - 2))
                placed = try_place(i, side)
                if placed: pi = i; break
            side = -side
            if placed:
                px, pz = placed
                gx = (ground(px + 1.5, pz) - ground(px - 1.5, pz)) / 3.0
                gz = (ground(px, pz + 1.5) - ground(px, pz - 1.5)) / 3.0
                n = np.array([-gx, 1.0, -gz]); n /= np.linalg.norm(n)
                pads.append({"pos": [round(px, 2), round(ground(px, pz) + 0.04, 2), round(pz, 2)], "normal": [round(float(v), 3) for v in n], "line": li, "dist": round(float(d[pi]), 2)})

json.dump({"lines": lines, "pads": pads}, open(os.path.join(DATA, "ziplines.json"), "w"))
tot = sum(sum(math.dist(l["points"][i], l["points"][i + 1]) for i in range(len(l["points"]) - 1)) for l in lines)
print("câbles : %d, %.1f km, piliers : %d, tremplins : %d" % (len(lines), tot / 1000, sum(len(l["pylons"]) for l in lines), len(pads)))
