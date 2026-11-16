"""Génère un modèle 3D avec l'API Meshy à partir d'un texte (aperçu puis texturage).
  python -u tools/meshy/text3d.py "<description>" <sortie.glb>
Lit MESHY_API_KEY dans l'environnement."""
import os, sys, json, time, urllib.request, urllib.error

KEY = os.environ.get("MESHY_API_KEY")
if not KEY:
    sys.exit("MESHY_API_KEY absent")
API = "https://api.meshy.ai/openapi/v2/text-to-3d"
HEAD = {"Authorization": "Bearer " + KEY, "Content-Type": "application/json"}


def call(method, url, body=None):
    req = urllib.request.Request(url, method=method, headers=HEAD, data=json.dumps(body).encode() if body else None)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        sys.exit(f"HTTP {e.code} : {e.read().decode()[:300]}")


def wait(tid):
    while True:
        t = call("GET", f"{API}/{tid}")
        if t["status"] == "SUCCEEDED":
            return t
        if t["status"] in ("FAILED", "CANCELED"):
            sys.exit("échec : " + str(t.get("task_error")))
        print(" ", t["status"], t.get("progress", 0), "%", flush=True)
        time.sleep(10)


prompt, out = sys.argv[1], sys.argv[2]
prev = call("POST", API, {"mode": "preview", "prompt": prompt, "ai_model": "meshy-6", "topology": "triangle", "target_polycount": 20000})["result"]
print("aperçu lancé", prev, flush=True)
wait(prev)
ref = call("POST", API, {"mode": "refine", "preview_task_id": prev, "enable_pbr": True})["result"]
print("texturage lancé", ref, flush=True)
t = wait(ref)
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
urllib.request.urlretrieve(t["model_urls"]["glb"], out)
print("OK", out, flush=True)
