"""Dessine la carte de Frontière 01 (minimap et grande carte) -> assets/maps/frontiere01.png, à partir du relief, des routes, des zones et des bâtiments."""
import os, json, math
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from layout import *

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "maps", "frontiere01", "data")
m = np.load(os.path.join(DATA, "masks.npz"))
H = m["H"].astype(np.float32); slope = m["slope"].astype(np.float32); dry = m["dry"].astype(np.float32)
road = m["road"].astype(np.float32); trail = m["trail"].astype(np.float32); rock = m["rock"].astype(np.float32)
dist = {"A": m["zoneA"].astype(np.float32), "B": m["zoneB"].astype(np.float32), "C": m["zoneC"].astype(np.float32)}
b = json.load(open(os.path.join(DATA, "buildings_final.json")))

forest = np.array([48, 96, 54], np.float32); forest2 = np.array([36, 78, 44], np.float32)
sand = np.array([214, 184, 126], np.float32); rocky = np.array([132, 120, 108], np.float32)
img = np.zeros((N, N, 3), np.float32)
t = np.clip(dry * 1.2 - 0.1, 0, 1)[..., None]
noise = (np.sin(np.arange(N)[None, :] * 0.11) * np.sin(np.arange(N)[:, None] * 0.09))[..., None] * 0.5 + 0.5
base = forest * (1 - noise * 0.3) + forest2 * (noise * 0.3)
img = base * (1 - t) + sand * t
r = np.clip(rock + np.clip((slope - 0.5) * 1.5, 0, 1) * 0.6, 0, 1)[..., None]
img = img * (1 - r) + rocky * r

# relief : ombrage + courbes de niveau
gz, gx = np.gradient(H)
shade = np.clip(0.78 + (-gx * 0.5 - gz * 0.5) * 0.35, 0.45, 1.25)[..., None]
img *= shade
img[(H % 25.0) < 0.7] *= 0.88

# zones : teinte + contour pointillé
tint = {"A": (70, 130, 230), "B": (160, 90, 220), "C": (240, 150, 40)}
for k, z in ZONES.items():
    inside = (dist[k] < z["r"])[..., None]
    img = np.where(inside, img * 0.62 + np.array(tint[k], np.float32) * 0.38, img)
    ring = (np.abs(dist[k] - z["r"]) < 1.6)
    ys, xs = np.nonzero(ring)
    keep = ((xs + ys) // 6) % 2 == 0
    img[ys[keep], xs[keep]] = np.array(tint[k], np.float32) * 1.15

# routes et chemins
rd = np.clip(road, 0, 1)[..., None]; tr = np.clip(trail, 0, 1)[..., None] * (1 - rd)
img = img * (1 - rd) + np.array([236, 228, 205], np.float32) * rd
img = img * (1 - tr * 0.85) + np.array([196, 168, 120], np.float32) * tr * 0.85

# bâtiments : murs et dalles en gris foncé
for bx in b["boxes"]:
    if bx[4] - bx[1] < 1.2 and (bx[3] - bx[0]) * (bx[5] - bx[2]) < 6: continue
    if (bx[3] - bx[0]) > 40 or (bx[5] - bx[2]) > 40: continue
    x0, x1 = int(bx[0] + HALF), int(math.ceil(bx[3] + HALF)); z0, z1 = int(bx[2] + HALF), int(math.ceil(bx[5] + HALF))
    if bx[4] - bx[1] > 1.2 or (bx[3] - bx[0]) * (bx[5] - bx[2]) > 6:
        img[z0:z1 + 1, x0:x1 + 1] = img[z0:z1 + 1, x0:x1 + 1] * 0.25 + np.array([32, 34, 44], np.float32) * 0.75

# hors zone jouable : assombri
edge = np.maximum(np.abs(np.arange(N)[None, :] - HALF), np.abs(np.arange(N)[:, None] - HALF)).astype(np.float32)
out = np.clip((edge - PLAY) / 40.0, 0, 1)[..., None]
img = img * (1 - out * 0.65) + np.array([20, 24, 30], np.float32) * out * 0.65

im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")
d = ImageDraw.Draw(im)
def font(sz, bold=True):
    for f in ("arialbd.ttf" if bold else "arial.ttf", "segoeuib.ttf", "DejaVuSans-Bold.ttf"):
        try: return ImageFont.truetype(f, sz)
        except Exception: pass
    return ImageFont.load_default()

def label(xz, text, sz, fill, stroke=3):
    x, z = xz[0] + HALF, xz[1] + HALF
    f = font(sz)
    w = d.textlength(text, font=f)
    d.text((x - w / 2, z - sz / 2), text, font=f, fill=fill, stroke_width=stroke, stroke_fill=(10, 12, 16))

cols = {"A": (90, 160, 255), "B": (190, 120, 255), "C": (255, 170, 50)}
for k, z in ZONES.items():
    cx, cz = z["c"][0] + HALF, z["c"][1] + HALF
    d.ellipse((cx - 30, cz - 30, cx + 30, cz + 30), fill=cols[k], outline=(255, 255, 255), width=4)
    f = font(44); w = d.textlength(k, font=f)
    d.text((cx - w / 2, cz - 25), k, font=f, fill=(255, 255, 255), stroke_width=2, stroke_fill=(10, 12, 16))
    label((z["c"][0], z["c"][1] + 60), z["name"].upper(), 28, (255, 255, 255))
for name, (x, zz) in PASSES.items():
    px, pz = x + HALF, zz + HALF
    d.ellipse((px - 11, pz - 11, px + 11, pz + 11), outline=(255, 70, 60), width=5)
    label((x, zz + 28), name, 20, (255, 215, 205), 3)
label((MASSIF["c"][0], MASSIF["c"][1]), "MASSIF CENTRAL", 28, (240, 232, 220))
label((0, 40), "Vallon Central", 22, (235, 240, 200))

# recadrage sur la grille exportée pour le serveur (la carte du jeu couvre -760..760 m) puis 1024 px
crop = im.crop((HALF - 760, HALF - 760, HALF + 760, HALF + 760)).resize((1024, 1024), Image.LANCZOS)
out = os.path.join(ROOT, "assets", "maps", "frontiere01.png")
crop.save(out)
print("carte :", out)
