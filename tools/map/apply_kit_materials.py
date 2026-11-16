"""Règle, dans le .import de chaque pièce des kits, les matériaux externes créés par make_kit_materials.gd."""
import json, os, re
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
d = json.load(open(os.path.join(os.path.dirname(__file__), "_filemats.json"), encoding="utf-8"))
n = 0
for path, mats in d.items():
    f = os.path.join(ROOT, path.replace("res://", "")) + ".import"
    if not os.path.exists(f):
        continue
    s = open(f, encoding="utf-8").read()
    sub = {"materials": {name: {"use_external/enabled": True, "use_external/path": p} for name, p in mats}}
    # Godot attend la forme Godot de dictionnaire ; JSON convient (true/false en minuscules)
    line = "_subresources=" + json.dumps(sub, ensure_ascii=False)
    s2 = re.sub(r"^_subresources=.*$", lambda m: line, s, flags=re.M)
    if s2 != s:
        open(f, "w", encoding="utf-8").write(s2)
        n += 1
print("fichiers réglés :", n)
