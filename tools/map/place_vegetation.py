"""Calcule le placement de la végétation, des rochers et des falaises de Frontière 01 à partir des masques du terrain.
Sortie dans maps/frontiere01/data : manifest.json (liste des assets) + inst_<id>.bin (transformations, 12 float32 chacune)
que tools/map/vegetation.gd donne à l'instancer de Terrain3D."""
import os, json, math
import numpy as np
from layout import *

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "maps", "frontiere01", "data")
m = np.load(os.path.join(DATA, "masks.npz"))
H = m["H"].astype(np.float32)
slope = m["slope"].astype(np.float32)
dry = m["dry"].astype(np.float32)
road = np.maximum(m["road"].astype(np.float32), m["trail"].astype(np.float32))
dA, dB, dC = (m[k].astype(np.float32) for k in ("zoneA", "zoneB", "zoneC"))
rng = np.random.default_rng(2025)


def blur(a, r):
    fx = np.fft.fftfreq(a.shape[1])[None, :]
    fz = np.fft.fftfreq(a.shape[0])[:, None]
    return np.real(np.fft.ifft2(np.fft.fft2(a) * np.exp(-2 * (np.pi ** 2) * (r ** 2) * (fx ** 2 + fz ** 2)))).astype(np.float32)


def noise(scale, seed):
    w = blur(np.random.default_rng(seed).standard_normal((N, N)).astype(np.float32), scale)
    return w / (w.std() + 1e-9)


def smooth(t):
    t = np.clip(t, 0, 1)
    return t * t * (3 - 2 * t)


def at(a, x, z):
    i = np.clip((x + HALF).astype(int), 0, N - 1)
    j = np.clip((z + HALF).astype(int), 0, N - 1)
    return a[j, i]


def scatter(spacing, prob, jitter=0.5):
    """grille jitterée ; on garde chaque point avec la probabilité du masque en ce point"""
    g = np.arange(-PLAY + spacing / 2, PLAY, spacing, dtype=np.float32)
    X, Z = np.meshgrid(g, g)
    X = X + rng.uniform(-jitter, jitter, X.shape).astype(np.float32) * spacing
    Z = Z + rng.uniform(-jitter, jitter, Z.shape).astype(np.float32) * spacing
    X, Z = X.ravel(), Z.ravel()
    p = at(prob, X, Z)
    keep = rng.random(len(X)) < p
    return X[keep], Z[keep]


# ------------------------------------------------------------------------------------------------------ masques
n1, n2, n3 = noise(40, 1), noise(12, 2), noise(90, 3)
free = (road < 0.03).astype(np.float32)
core = np.zeros((N, N), np.float32)
for k, d in (("A", dA), ("B", dB)):
    core = np.maximum(core, smooth((ZONES[k]["r"] * 1.05 - d) / 20.0))     # zone construite : pas d'arbre
inside_C = smooth((ZONES["C"]["r"] * 1.15 - dC) / 30.0)
forest = np.clip(1 - dry * 1.15, 0, 1)
flat = 1 - smooth((slope - 0.55) / 0.35)
low = 1 - smooth((H - 170.0) / 25.0)
land = free * flat * low * (1 - core)

assets = []          # (nom, glb, visibilité, ombres, [transformations])


def add(name, glb, vis, shadows, X, Z, scale=(0.8, 1.2), sink=0.0, tilt=0.0, yaw=True, hscale=None):
    n = len(X)
    if n == 0:
        return
    y = at(H, X, Z) - sink
    s = rng.uniform(scale[0], scale[1], n).astype(np.float32)
    sx = s if hscale is None else s * rng.uniform(hscale[0], hscale[1], n).astype(np.float32)
    a = rng.uniform(0, 2 * math.pi, n) if yaw else np.zeros(n)
    tx, tz = rng.uniform(-tilt, tilt, n), rng.uniform(-tilt, tilt, n)
    out = np.zeros((n, 12), np.float32)
    for i in range(n):
        cy, sy = math.cos(a[i]), math.sin(a[i])
        ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
        cx_, sx_ = math.cos(tx[i]), math.sin(tx[i])
        rx = np.array([[1, 0, 0], [0, cx_, -sx_], [0, sx_, cx_]])
        cz_, sz_ = math.cos(tz[i]), math.sin(tz[i])
        rz = np.array([[cz_, -sz_, 0], [sz_, cz_, 0], [0, 0, 1]])
        b = (ry @ rx @ rz) * np.array([s[i], sx[i], s[i]])[None, :] if False else (ry @ rx @ rz) @ np.diag([s[i], sx[i], s[i]])
        out[i, :9] = b.reshape(-1)
        out[i, 9:] = (X[i], y[i], Z[i])
    assets.append(dict(name=name, glb=glb, vis=vis, shadows=shadows, data=out))
    print("%-18s %6d" % (name, n))


def pick(X, Z, k):
    c = rng.integers(0, k, len(X))
    return [(X[c == i], Z[c == i]) for i in range(k)]


# ------------------------------------------------------------------------------------------------------ forêt de sapins
pt = np.clip(forest * 1.25 - 0.1, 0, 1) * np.clip(0.55 + 0.55 * n1, 0, 1) * land
tx_, tz_ = scatter(6.5, pt)
for i, (x, z) in enumerate(pick(tx_, tz_, 3)):
    # le même arbre existe en version détaillée (proche) et en cônes (loin) : mêmes positions
    sc = (0.75, 1.25)
    ang_seed = rng.integers(0, 1 << 30)
    add("fir_%s" % "abc"[i], "fir_%s" % "abc"[i], 115, 0, x, z, sc, sink=0.25, tilt=0.03)
for name, glb, vis in (("fir_far_a", "fir_far_a", 900), ("fir_far_b", "fir_far_b", 900)):
    pass
# versions lointaines : mêmes positions (réutilise les listes déjà tirées)
fa = [a for a in assets if a["name"].startswith("fir_")]
for a_, far in zip(fa, ("fir_far_a", "fir_far_b", "fir_far_a")):
    assets.append(dict(name=far + "_" + a_["name"][-1], glb=far, vis=900, shadows=0, data=a_["data"].copy()))
print("arbres :", sum(len(a["data"]) for a in fa))

# sous-bois : fougères, herbes, souches, branches, troncs
fern = np.clip(forest * 1.4 - 0.2, 0, 1) * np.clip(0.4 + 0.6 * n2, 0, 1) * land
for i, (x, z) in enumerate(pick(*scatter(3.2, fern), 2)):
    add("fern_" + "bc"[i], "pine_fern_" + "bc"[i], 55, 0, x, z, (0.9, 1.5), sink=0.02)
grass_p = np.clip(forest * 1.1, 0, 1) * np.clip(0.5 + 0.5 * n2, 0, 1) * land
for i, (x, z) in enumerate(pick(*scatter(2.4, grass_p * 0.9), 2)):
    add("grass_" + "ab"[i], "pine_grass_" + "ab"[i], 40, 0, x, z, (0.9, 1.6), sink=0.03)
rocks_p = np.clip(forest * 0.9 + 0.1, 0, 1) * free * flat * (1 - core) * 0.35
for i, (x, z) in enumerate(pick(*scatter(14, rocks_p), 6)):
    add("rock_" + "abcdef"[i], "pine_rock_" + "abcdef"[i], 160, 1, x, z, (0.7, 1.8), sink=0.15, tilt=0.05)
rocks2 = pick(*scatter(11, rocks_p * 0.8), 7)
for i, (x, z) in enumerate(rocks2):
    add("rockb_%02d" % (i + 7), "pine_rockb_%02d" % (i + 7), 130, 0, x, z, (0.6, 1.5), sink=0.1, tilt=0.05)
logp = forest * land * 0.9
x, z = scatter(34, logp)
add("deadlog", "pine_deadlog", 130, 0, x, z, (0.8, 1.3), sink=0.05)
x, z = scatter(30, logp)
add("stump", "pine_stump", 110, 0, x, z, (0.6, 1.1), sink=0.1)
x, z = scatter(8, forest * land * 0.25)
add("branch", "pine_branch_a", 60, 0, x, z, (0.8, 1.6), sink=0.0)

# ------------------------------------------------------------------------------------------------------ désert (Namaqualand)
desert = np.clip(dry * 1.25 - 0.15, 0, 1) * land
zoneC = inside_C
des_dense = np.clip(desert * (0.55 + 0.45 * smooth(n3 + 0.2)), 0, 1)
x, z = scatter(24, desert * 0.35 * np.clip(0.4 + 0.7 * n1, 0, 1))
for i, (xx, zz) in enumerate(pick(x, z, 2)):
    add("quiver_" + "ab"[i], "nama_quiver_0%d" % (i + 1), 170, 1, xx, zz, (0.8, 1.4), sink=0.05, tilt=0.03)
shr = desert * np.clip(0.3 + 0.6 * n1, 0, 1)
for i, (xx, zz) in enumerate(pick(*scatter(6.5, shr * 0.8), 6)):
    nm = ("searsia_e", "searsia_f", "searsia_g", "othonna_e", "othonna_f", "othonna_g")[i]
    add(nm, "nama_" + nm, 110, 0, xx, zz, (0.8, 1.5), sink=0.03)
fl = desert * np.clip(0.2 + 0.8 * smooth(n2), 0, 1)
for nm in ("ursinia_d", "ursinia_e", "stinkkruid_c", "stinkkruid_d", "gazania_a", "gazania_b", "gazania_c"):
    xx, zz = scatter(5.2, fl * 0.55)
    add(nm, "nama_" + nm, 55, 0, xx[: len(xx) // 2], zz[: len(zz) // 2], (0.9, 1.6), sink=0.01)
xx, zz = scatter(7, desert * 0.5)
add("iceplant", "nama_iceplant", 50, 0, xx, zz, (0.9, 1.6))
xx, zz = scatter(9, desert * 0.45)
add("succulent", "nama_succulent", 50, 0, xx, zz, (0.9, 1.5))
rk = free * flat * (1 - core) * np.clip(dry * 1.2, 0, 1) * 0.45
for nm, sp in (("boulder_03", 22), ("boulder_04", 24), ("boulder_05", 24), ("boulder_06", 22)):
    xx, zz = scatter(sp, rk * 0.55)
    add(nm, "nama_" + nm, 220, 1, xx, zz, (0.8, 1.9), sink=0.2, tilt=0.06)
for nm, sp, sc in (("rock_a", 12, (0.8, 1.6)), ("rock_b", 12, (0.8, 1.6)), ("stone_b", 9, (0.7, 1.6)), ("stone_d", 9, (0.7, 1.6)), ("pebble_b", 6, (0.7, 1.5)), ("pebble_c", 6, (0.7, 1.5))):
    xx, zz = scatter(sp, rk * 0.7)
    add(nm, "nama_" + nm, 100, 0, xx, zz, sc, sink=0.05)

# ------------------------------------------------------------------------------------------------------ montagnes : falaises et gros blocs
steep = smooth((slope - 0.5) / 0.45) * (H > 95).astype(np.float32) * free * (1 - core)
for nm, glb, sp, sc, vis in (("cliff_02", "nama_cliff_02", 26, (0.4, 0.9), 420), ("cliff_01", "nama_cliff_01", 24, (0.5, 1.0), 380)):
    xx, zz = scatter(sp, steep * 0.7)
    add(nm, glb, vis, 1, xx, zz, sc, sink=2.2, tilt=0.12)
xx, zz = scatter(16, steep * 0.8)
for i, (a_, b_) in enumerate(pick(xx, zz, 4)):
    add("mboulder_%d" % i, "nama_boulder_0%d" % (3 + i), 260, 1, a_, b_, (1.6, 3.4), sink=0.6, tilt=0.1)

# ------------------------------------------------------------------------------------------------------ écriture
manifest = []
for i, a in enumerate(assets):
    a["data"].astype("<f4").tofile(os.path.join(DATA, "inst_%d.bin" % i))
    manifest.append(dict(id=i, name=a["name"], glb=a["glb"], vis=a["vis"], shadows=a["shadows"], count=int(len(a["data"]))))
json.dump(manifest, open(os.path.join(DATA, "manifest.json"), "w"), indent=1)
print("assets : %d, instances : %d" % (len(manifest), sum(x["count"] for x in manifest)))
