"""Assemble les textures du terrain Frontière 01 au format Terrain3D :
   <nom>_alb_ht.png  = albédo (RGB) + hauteur (A)
   <nom>_nrm_rgh.png = normale OpenGL (RGB) + rugosité (A)
Toutes en 1024x1024 (Terrain3D exige la même taille pour toutes les textures)."""
import os, sys
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ENV = os.path.join(ROOT, "assets", "environement")
OUT = os.path.join(ROOT, "maps", "frontiere01", "textures")
DL = sys.argv[1] if len(sys.argv) > 1 else None   # dossier des ambientCG décompressés
SIZE = 1024

def load(path, mode=None):
    im = Image.open(path)
    if mode: im = im.convert(mode)
    return im

def to_f(im):
    a = np.asarray(im, dtype=np.float32)
    if a.dtype == np.uint8 or im.mode in ("RGB", "RGBA", "L"): a = a / 255.0
    return a

def gray16(path):
    im = Image.open(path)
    a = np.asarray(im, dtype=np.float32)
    mx = 65535.0 if im.mode.startswith("I;16") or a.max() > 255 else 255.0
    a = a / mx
    if a.ndim == 3: a = a[..., 0]
    return a

def resize(a, rgb=True):
    if a.ndim == 2:
        im = Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8), "L")
    else:
        im = Image.fromarray((np.clip(a[..., :3], 0, 1) * 255).astype(np.uint8), "RGB")
    return np.asarray(im.resize((SIZE, SIZE), Image.LANCZOS), dtype=np.float32) / 255.0

def normal_from_height(h, strength=3.0):
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * strength
    gy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * strength
    n = np.stack([-gx, gy, np.ones_like(h)], -1)   # +Y vers le haut de l'image : convention OpenGL
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5

def save(name, rgb, h, nrm, rough):
    os.makedirs(OUT, exist_ok=True)
    alb = np.dstack([rgb, h])
    Image.fromarray((np.clip(alb, 0, 1) * 255).astype(np.uint8), "RGBA").save(os.path.join(OUT, name + "_alb_ht.png"))
    nr = np.dstack([nrm, rough])
    Image.fromarray((np.clip(nr, 0, 1) * 255).astype(np.uint8), "RGBA").save(os.path.join(OUT, name + "_nrm_rgh.png"))
    print("ok", name)

def from_polyhaven(name, pre, folder="pine_forest", disp=True, rough=True):
    d = os.path.join(ENV, folder, "textures")
    rgb = resize(to_f(load(os.path.join(d, pre + "_diff.png"), "RGB")))
    h = resize(gray16(os.path.join(d, pre + "_disp.png"))) if disp else np.full((SIZE, SIZE), 0.5, np.float32)
    n = resize(to_f(load(os.path.join(d, pre + "_nor_gl.png"), "RGB")))
    r = resize(gray16(os.path.join(d, pre + "_rough.png")))
    save(name, rgb, h, n, r)

def from_namaqualand(name, pre, rough_val=0.85):
    d = os.path.join(ENV, "namaqualand", "textures")
    rgb = resize(to_f(load(os.path.join(d, pre + "_diff_4k.jpg"), "RGB")))
    h = resize(gray16(os.path.join(d, pre + "_disp_4k.png")))
    n = normal_from_height(h, 4.0)
    lum = rgb.mean(-1)
    save(name, rgb, h, n, np.clip(rough_val + (0.5 - lum) * 0.2, 0.4, 1.0))

def from_ambientcg(name, folder, pre):
    d = os.path.join(DL, folder)
    f = lambda s: os.path.join(d, f"{pre}_1K-PNG_{s}.png")
    rgb = resize(to_f(load(f("Color"), "RGB")))
    h = resize(gray16(f("Displacement")))
    n = resize(to_f(load(f("NormalGL"), "RGB")))
    r = resize(gray16(f("Roughness")))
    save(name, rgb, h, n, r)

if __name__ == "__main__":
    from_polyhaven("forest_ground", "forest_ground_04")
    from_polyhaven("forest_leaves", "forest_leaves_04")
    from_polyhaven("dirt_road", "rocky_trail")
    from_namaqualand("cliff", "cliff_side", 0.9)
    from_namaqualand("sand", "gravelly_sand", 0.9)
    from_namaqualand("dry_gravel", "sandy_gravel_02", 0.9)
    if DL:
        from_ambientcg("concrete", "Concrete034", "Concrete034")
        from_ambientcg("asphalt", "Asphalt031", "Asphalt031")
        from_ambientcg("metal", "Metal038", "Metal038")
