"""Prépare les textures de végétation (RGBA : couleur + transparence) en 1024 px dans maps/frontiere01/textures/veg."""
import os
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PINE = os.path.join(ROOT, "assets", "environement", "pine_forest", "textures")
NAMA = os.path.join(ROOT, "assets", "environement", "namaqualand", "textures")
OUT = os.path.join(ROOT, "maps", "frontiere01", "textures", "veg")
os.makedirs(OUT, exist_ok=True)
S = 1024


def gray(path):
    im = Image.open(path)
    a = np.asarray(im, dtype=np.float32)
    mx = 65535.0 if im.mode.startswith("I;16") or a.max() > 255 else 255.0
    a = a / mx
    return a[..., 0] if a.ndim == 3 else a


def rgb(path):
    return np.asarray(Image.open(path).convert("RGB").resize((S, S), Image.LANCZOS), dtype=np.float32) / 255.0


def put(name, color, alpha=None):
    a = np.ones((S, S), np.float32) if alpha is None else np.asarray(
        Image.fromarray((np.clip(alpha, 0, 1) * 255).astype(np.uint8)).resize((S, S), Image.LANCZOS), dtype=np.float32) / 255.0
    Image.fromarray((np.clip(np.dstack([color, a]), 0, 1) * 255).astype(np.uint8), "RGBA").save(os.path.join(OUT, name + ".png"))
    print("ok", name)


def solid(name, pre, folder=PINE, suffix="_diff.png"):
    put(name, rgb(os.path.join(folder, pre + suffix)))


def cut(name, pre, alpha_file, folder=PINE, suffix="_diff.png"):
    put(name, rgb(os.path.join(folder, pre + suffix)), gray(os.path.join(folder, alpha_file)))


if __name__ == "__main__":
    for n in ("pine_trunk_01", "pine_trunk_02", "pine_trunk_03", "fir_trunk_01", "fir_trunk_03", "dead_tree", "tree_roots_01"):
        if os.path.exists(os.path.join(PINE, n + "_diff.png")):
            solid(n, n)
    cut("fir_twig", "fir_twig", "fir_twig_alpha.png")
    cut("pine_twig", "pine_twig", "pine_twig_alpha.png")
    cut("fern", "fern_02", "fern_02_alpha.png")
    cut("grass", "grass_medium_01", "grass_medium_01_alpha.png")
    solid("rock_moss_01", "rock_moss_set_01")
    solid("rock_moss_02", "rock_moss_set_02")
    solid("pine_cover", "pine_cover_01_diffuse") if os.path.exists(os.path.join(PINE, "pine_cover_01_diffuse_diff.png")) else None
