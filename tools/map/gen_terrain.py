"""Génère le relief et la peinture du terrain de Frontière 01.
Sortie (dossier maps/frontiere01/data) :
  height.r32   float32 N*N   hauteurs en mètres
  control.u32  uint32  N*N   carte de contrôle Terrain3D (textures)
  masks.npz                  masques pour le placement de la végétation / des bâtiments
"""
import os, json
import numpy as np
from layout import *

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "maps", "frontiere01", "data")
os.makedirs(OUT, exist_ok=True)

X, Z = np.meshgrid(np.arange(N, dtype=np.float32) - HALF, np.arange(N, dtype=np.float32) - HALF)


def smooth(t):
    t = np.clip(t, 0, 1)
    return t * t * (3 - 2 * t)


def blur(a, r):
    """flou gaussien par FFT (pas de scipy)"""
    fx = np.fft.fftfreq(a.shape[1])[None, :]
    fz = np.fft.fftfreq(a.shape[0])[:, None]
    k = np.exp(-2 * (np.pi ** 2) * (r ** 2) * (fx ** 2 + fz ** 2))
    return np.real(np.fft.ifft2(np.fft.fft2(a) * k)).astype(np.float32)


def fbm(scale, octaves=5, seed=0, gain=0.5, ridged=False):
    """bruit fractal : somme de champs aléatoires lissés ; valeurs ~[-1,1]"""
    r = np.random.default_rng(seed)
    out = np.zeros((N, N), np.float32)
    amp, tot, s = 1.0, 0.0, scale
    for _ in range(octaves):
        w = blur(r.standard_normal((N, N)).astype(np.float32), s)
        w /= (w.std() + 1e-9)
        if ridged:
            w = 1.0 - np.abs(w) * 1.2
        out += w * amp
        tot += amp
        amp *= gain
        s = max(s * 0.5, 1.2)
    return out / tot


def dist_to_polyline(pts):
    """distance (m) à la polyligne"""
    pts = np.array(pts, np.float32)
    best = np.full((N, N), 1e9, np.float32)
    for i in range(len(pts) - 1):
        a, b = pts[i], pts[i + 1]
        ab = b - a
        L2 = float(ab @ ab)
        t = np.clip(((X - a[0]) * ab[0] + (Z - a[1]) * ab[1]) / L2, 0, 1)
        d = np.hypot(X - (a[0] + t * ab[0]), Z - (a[1] + t * ab[1]))
        best = np.minimum(best, d)
    return best


def densify(pts, step=6.0):
    """polyligne lissée (Catmull-Rom) échantillonnée tous les `step` mètres"""
    p = np.array(pts, np.float32)
    out = []
    ext = np.vstack([p[0] * 2 - p[1], p, p[-1] * 2 - p[-2]])
    for i in range(1, len(ext) - 2):
        p0, p1, p2, p3 = ext[i - 1], ext[i], ext[i + 1], ext[i + 2]
        n = max(2, int(np.linalg.norm(p2 - p1) / step))
        for k in range(n):
            t = k / n
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    out.append(p[-1])
    return np.array(out, np.float32)


def blob(c, r, p=2.0):
    d = np.hypot(X - c[0], Z - c[1]) / r
    return np.clip(1 - d ** p, 0, 1)


# --------------------------------------------------------------------------------------------------- relief
big = fbm(120, 5, 11)
mid = fbm(30, 4, 12)
ridge = fbm(70, 5, 13, 0.55, ridged=True)

H = 70.0 + big * 18.0 + mid * 3.5          # terrain de base : collines douces vers 70 m

mountain = blob(MASSIF["c"], MASSIF["r"], 1.7)
for c, r, k in [((-190, -40), 120, .60), ((215, -40), 120, .65), ((-140, 150), 105, .5), ((150, 140), 105, .5), ((20, -335), 80, .45)]:
    mountain = np.maximum(mountain, blob(c, r, 2.0) * k)
rim = smooth((np.maximum(np.abs(X), np.abs(Z)) - (PLAY - 70)) / 110.0)
mountain = np.maximum(mountain, rim * 0.95)
crag = fbm(14, 3, 14)
H = H + mountain * (105.0 + 110.0 * (0.5 + 0.5 * ridge)) + mountain ** 1.2 * (mid * 14.0 + crag * 9.0)

# plateaux des zones : plat au centre, raccord progressif
zone_d = {}
zone_core = np.zeros((N, N), np.float32)
for k, z in ZONES.items():
    d = np.hypot(X - z["c"][0], Z - z["c"][1]) / (1 + 0.10 * fbm(60, 2, 20 + ord(k)))
    blend = smooth((z["r"] * 1.7 - d) / (z["r"] * 0.9))
    H = H * (1 - blend) + (z["h"] + fbm(15, 3, 30 + ord(k)) * 0.03) * blend
    zone_core = np.maximum(zone_core, smooth((z["r"] - d) / (z["r"] * 0.55)))
    zone_d[k] = d
# bassin C : légèrement creusé autour du plateau
dC = zone_d["C"]
H += -10.0 * smooth((ZONES["C"]["r"] * 1.5 - dC) / (ZONES["C"]["r"] * 1.2)) * (1 - smooth((ZONES["C"]["r"] * 0.55 - dC) / 60.0))
# vallon central
vc = np.hypot((X + 5) / 160.0, (Z - 20) / 210.0)
H = H - 42.0 * smooth((1 - vc) / 0.7) * (1 - smooth(zone_core * 3))

# routes : on aplatit le terrain le long de leur tracé (profil lissé : pas de marche d'escalier)
prof_base = blur(H, 10.0)
dists = {}
for name, spec in {**ROADS, **TRAILS}.items():
    d = dist_to_polyline(densify(spec["pts"]))
    dists[name] = d
    f = smooth((spec["w"] * 0.5 + 12.0 - d) / 12.0) * (1.0 if name in ROADS else 0.8)
    H = H * (1 - f) + (prof_base - 0.35) * f
road_mask = np.zeros((N, N), np.float32)
trail_mask = np.zeros((N, N), np.float32)
for name, spec in ROADS.items():
    road_mask = np.maximum(road_mask, smooth((spec["w"] * 0.5 + 1.2 - dists[name]) / 2.5))
for name, spec in TRAILS.items():
    trail_mask = np.maximum(trail_mask, smooth((spec["w"] * 0.5 + 1.0 - dists[name]) / 2.0))

H = np.maximum(H, 4.0).astype(np.float32)
H.tofile(os.path.join(OUT, "height.r32"))
print("hauteurs : %.1f .. %.1f m" % (H.min(), H.max()))

# --------------------------------------------------------------------------------------------------- peinture
gz, gx = np.gradient(H)
slope = np.hypot(gx, gz)
dry = np.clip(smooth((Z - 20.0) / 260.0) * 0.9 + 0.1 * fbm(50, 2, 40), 0, 1)
nz = fbm(18, 3, 41)
nz2 = fbm(40, 3, 42)

W = np.zeros((9, N, N), np.float32)
forest = 1 - dry
W[0] = forest * (0.7 + 0.5 * nz)
W[1] = forest * (0.8 - 0.5 * nz2)
W[3] = dry * (0.6 + 0.4 * nz)
W[4] = dry * (0.8 - 0.4 * nz2)
rock = smooth((slope - 0.55) / 0.5) + smooth((H - 190.0) / 50.0) * 0.8
W[2] = np.clip(rock, 0, 1) * 3.0
for k, tex in (("A", 6), ("B", 7)):
    core = smooth((ZONES[k]["r"] * 0.92 - zone_d[k]) / 26.0)
    W[tex] = core * 4.0
    W[0] *= (1 - core)
    W[1] *= (1 - core)
    W[2] *= (1 - core * 0.9)
coreC = smooth((ZONES["C"]["r"] * 0.85 - dC) / 40.0)
W[3] += coreC * 1.5
W[2] *= (1 - coreC * 0.8)
W[5] = road_mask * 8.0 + trail_mask * 6.0
cover = np.clip(road_mask + trail_mask, 0, 1)
for k in (0, 1, 2, 3, 4, 6, 7):
    W[k] *= (1 - cover * 0.97)
W = np.maximum(W, 0)

order = np.argsort(-W, axis=0)
i1, i2 = order[0], order[1]
w1 = np.take_along_axis(W, i1[None], 0)[0]
w2 = np.take_along_axis(W, i2[None], 0)[0]
blend8 = np.round(np.clip(w2 / np.maximum(w1 + w2, 1e-6), 0, 1) * 255).astype(np.uint32)
# bits Terrain3D : base 27-31, overlay 22-26, blend 14-21, rotation 10-13, échelle 7-9, trou 2, nav 1, auto 0
ctrl = (i1.astype(np.uint32) << 27) | (i2.astype(np.uint32) << 22) | (blend8 << 14)
ctrl.astype('<u4').tofile(os.path.join(OUT, "control.u32"))

np.savez_compressed(os.path.join(OUT, "masks.npz"), H=H, slope=slope.astype(np.float16), dry=dry.astype(np.float16),
                    road=road_mask.astype(np.float16), trail=trail_mask.astype(np.float16),
                    rock=np.clip(rock, 0, 1).astype(np.float16), zoneA=zone_d["A"].astype(np.float16),
                    zoneB=zone_d["B"].astype(np.float16), zoneC=zone_d["C"].astype(np.float16))
json.dump({"half": HALF, "n": N}, open(os.path.join(OUT, "meta.json"), "w"))
print("écrit", OUT)

# aperçu (hors du projet pour ne pas être importé)
from PIL import Image
h8 = ((H - H.min()) / (H.max() - H.min()) * 255).astype(np.uint8)
pal = np.array([[34, 85, 40], [60, 100, 50], [120, 115, 105], [200, 150, 90], [170, 130, 80], [130, 100, 60], [150, 150, 150], [70, 70, 75], [90, 95, 100]], np.uint8)
col = pal[i1].astype(np.float32)
sh = np.clip(0.55 + 0.45 * (h8 / 255.0), 0, 1)[..., None]
prev = os.environ.get("PREVIEW_DIR", OUT)
Image.fromarray((col * sh).astype(np.uint8)).save(os.path.join(prev, "preview.png"))
Image.fromarray(h8).save(os.path.join(prev, "preview_height.png"))
