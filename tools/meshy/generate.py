"""Génère des modèles 3D avec l'API Meshy (image vers 3D) à partir des images de concept du projet.
  python tools/meshy/generate.py            (lit MESHY_API_KEY dans l'environnement)
Chaque modèle est téléchargé dans assets/accessoires/<dossier>/<nom>.glb. Les tâches déjà terminées (fichier présent) sont sautées."""
import os, sys, io, json, time, base64, urllib.request, urllib.error
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
KEY = os.environ.get("MESHY_API_KEY")
if not KEY:
    sys.exit("MESHY_API_KEY absent de l'environnement")
API = "https://api.meshy.ai/openapi/v1/image-to-3d"
HEAD = {"Authorization": "Bearer " + KEY, "Content-Type": "application/json"}

JOBS = [  # (image, dossier de sortie, nom)
    ("assets/textures/Shield/White Shield.png", "Shield", "shield"),
    ("assets/textures/Soin/Petit Soin vie.png", "Soin", "heal_small"),
    ("assets/textures/Soin/Gros soin Vie.png", "Soin", "heal_big"),
    ("assets/textures/Soin/Petit Bouclier .png", "Soin", "shield_small"),
    ("assets/textures/Soin/Gros bouclier.png", "Soin", "shield_big"),
    ("assets/textures/Soin/Petit Bouclier et Vie.png", "Soin", "both_small"),
    ("assets/textures/Soin/Gros bouclmier et vie.png", "Soin", "both_big"),
]


def call(method, url, body=None):
    req = urllib.request.Request(url, method=method, headers=HEAD, data=json.dumps(body).encode() if body else None)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        sys.exit(f"HTTP {e.code} : {e.read().decode()[:300]}")


def data_uri(path):
    im = Image.open(os.path.join(ROOT, path)).convert("RGB")
    im.thumbnail((1280, 1280))
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=92)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


tasks = {}
for image, folder, name in JOBS:
    out = os.path.join(ROOT, "assets", "accessoires", folder, name + ".glb")
    if os.path.exists(out):
        print("déjà là :", name)
        continue
    r = call("POST", API, {"image_url": data_uri(image), "ai_model": "meshy-6", "model_type": "standard", "topology": "triangle",
                           "target_polycount": 20000, "should_texture": True, "enable_pbr": True, "symmetry_mode": "auto"})
    tasks[r["result"]] = (name, out)
    print("lancé :", name, r["result"])

pending = dict(tasks)
while pending:
    time.sleep(12)
    for tid in list(pending):
        t = call("GET", f"{API}/{tid}")
        name, out = pending[tid]
        st = t.get("status")
        if st == "SUCCEEDED":
            os.makedirs(os.path.dirname(out), exist_ok=True)
            urllib.request.urlretrieve(t["model_urls"]["glb"], out)
            print("OK :", name, "->", out)
            del pending[tid]
        elif st in ("FAILED", "CANCELED"):
            print("ÉCHEC :", name, t.get("task_error"))
            del pending[tid]
        else:
            print(f"  {name} : {st} {t.get('progress', 0)} %")
