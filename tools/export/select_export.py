"""Export only what the game uses.

Godot's "Export selected resources (and dependencies)" mode exports the resources listed in
export_files plus everything they depend on. This script finds the roots, i.e. the resources the
game reaches by path rather than through a scene: every path or file name written in the code
(C#, GDScript, the netcode Core), the data files (json, cfg) and project.godot, plus the folders a
scene names as a plain string (Terrain3D data_directory). It then writes the list into the game
presets of export_presets.cfg.

Run it before each export (close the export dialog first; the editor rewrites the file on save):
  python tools/export/select_export.py            -> updates export_presets.cfg
  python tools/export/select_export.py --dry-run  -> prints the roots and the size left out
  --godot <path>: the Godot used to read the dependencies (default: the GODOT variable, then PATH, then C:/Godot)
"""
import fnmatch
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PRESETS = ("Windows Desktop", "Linux Jeu")

# never exported (sources, tools, servers, downloads)
SKIP_DIRS = (".godot", ".git", ".vs", ".vscode", "builds", "netcode", "tools", "addons/ambientcg/Downloads")
RESOURCE_EXT = {".tscn", ".scn", ".tres", ".res", ".gdshader", ".shader", ".material", ".anim", ".mesh", ".gdextension"}
# texts the game reads paths from
CODE_EXT = {".cs", ".gd"}
DATA_EXT = {".json", ".cfg"}
CODE_DIRS = ("scripts", "scenes", "maps", "addons", "netcode/OpenEsport.Core")
DATA_DIRS = ("data", "assets/characters/kit", "maps")
# always needed: project settings resources, every GDExtension, the audio bus layout
ALWAYS = ("project.godot", "default_bus_layout.tres")
# non-resource files the game reads; kept in include_filter (the existing filter is preserved)
INCLUDE = ("scripts/*",)
# resources loaded by a computed name the literals cannot reveal (minimap: <map>.world -> <map>.png)
ALWAYS_GLOBS = ("assets/maps/*",)

LITERAL = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
# res:// paths anywhere in a text, whatever the quoting (C# verbatim strings write ""res://..."")
RAW_PATH = re.compile(r"""res://[^"'\n<>|]+?\.[A-Za-z0-9]{2,6}(?=["'\s,;)\]]|$)""")
DIR_PROPERTY = re.compile(r'^\w+ = "res://([^"]*)"\s*$', re.M)


def rel(path):
    return path.relative_to(ROOT).as_posix()


def walk(top=ROOT, skip=SKIP_DIRS):
    for root, dirs, files in os.walk(top):
        r = rel(Path(root)) if Path(root) != ROOT else ""
        dirs[:] = [d for d in dirs if not any((f"{r}/{d}" if r else d) == s or (f"{r}/{d}" if r else d).startswith(s + "/") for s in skip)]
        for name in files:
            yield Path(root) / name


def candidates():
    """Every exportable resource: imported files (with a .import beside them) and native resources."""
    found = []
    for path in walk():
        if path.suffix == ".import":
            source = path.with_suffix("")
            if source.exists():
                found.append(rel(source))
        elif path.suffix in RESOURCE_EXT:
            found.append(rel(path))
    return sorted(set(found))


def uid_map():
    uids = {}
    for path in walk():
        try:
            if path.suffix == ".uid":
                uids[path.read_text(encoding="utf-8").strip()] = rel(path.with_suffix(""))
            elif path.suffix == ".import":
                m = re.search(r'^uid="(uid://\w+)"', path.read_text(encoding="utf-8", errors="ignore"), re.M)
                if m: uids[m.group(1)] = rel(path.with_suffix(""))
            elif path.suffix in (".tscn", ".tres"):
                with open(path, encoding="utf-8", errors="ignore") as f:
                    m = re.search(r'uid="(uid://\w+)"', f.readline())
                if m: uids[m.group(1)] = rel(path)
        except OSError:
            pass
    return uids


def texts():
    for top in CODE_DIRS:
        for path in walk(ROOT / top, ("addons/ambientcg/Downloads",)) if (ROOT / top).exists() else ():
            if any(part in ("bin", "obj") for part in path.parts):
                continue
            if path.suffix in CODE_EXT:
                yield path
    for top in DATA_DIRS:
        for path in walk(ROOT / top) if (ROOT / top).exists() else ():
            if path.suffix in DATA_EXT:
                yield path
    for name in ALWAYS:
        if (ROOT / name).exists():
            yield ROOT / name
    yield ROOT / "balance.json"


def roots():
    files = candidates()
    by_stem = {}
    for f in files:
        by_stem.setdefault(Path(f).stem.lower(), []).append(f)
    uids = uid_map()
    exts = {Path(f).suffix.lower() for f in files}
    chosen = {f for f in files if f.endswith(".gdextension")}
    chosen |= {name for name in ALWAYS if name in files}
    chosen |= {f for f in files if any(fnmatch.fnmatchcase(f, g) for g in ALWAYS_GLOBS)}

    def add_literal(text):
        text = text.replace("\\\\", "/").strip().lstrip("*")
        if text.startswith("uid://"):
            if text in uids: chosen.add(uids[text])
            return
        absolute = text.startswith("res://")
        path = text[6:] if absolute else text
        if not path or len(path) > 300:
            return
        if "{" in path:  # interpolated string: $"res://clips/{name}.res"
            pattern = re.sub(r"\{[^}]*\}", "*", path)
            if not Path(pattern).suffix or "*" in Path(pattern).suffix:
                return
            chosen.update(f for f in files if fnmatch.fnmatchcase(f, pattern if absolute else "*" + pattern))
            return
        suffix = Path(path).suffix.lower()
        if suffix and suffix in exts:
            chosen.update(f for f in files if f == path or (not absolute and f.endswith("/" + path)) or (not absolute and f == path))
        elif not absolute and "/" not in path and len(path) >= 3:  # bare name completed in code: "Bipedal_Mech_AT27_rig" + ".glb"
            chosen.update(by_stem.get(path.lower(), ()))

    for path in texts():
        content = path.read_text(encoding="utf-8", errors="ignore")
        if path.suffix in CODE_EXT:  # paths quoted in comments are not loaded
            content = "\n".join(line for line in content.splitlines() if not line.lstrip().startswith(("//", "#")))
        for literal in LITERAL.findall(content) + RAW_PATH.findall(content):
            add_literal(literal)

    # folders a scene names as a string property (Terrain3D data_directory): everything inside
    for path in walk():
        if path.suffix in (".tscn", ".tres"):
            for folder in DIR_PROPERTY.findall(path.read_text(encoding="utf-8", errors="ignore")):
                if (ROOT / folder).is_dir():
                    chosen.update(f for f in files if f.startswith(folder.rstrip("/") + "/"))

    return sorted(chosen), files


def find_godot():
    if "--godot" in sys.argv:
        return sys.argv[sys.argv.index("--godot") + 1]
    if os.environ.get("GODOT"):
        return os.environ["GODOT"]
    for name in ("godot", "godot4", "godot-mono"):
        if shutil.which(name): return shutil.which(name)
    found = sorted(glob.glob("C:/Godot/*/Godot_v4*mono_win64_console.exe"))
    return found[-1] if found else None


def dependencies(chosen):
    """Dépendances complètes, lues par Godot dans les fichiers importés (tools/export/dependencies.gd), et celles qui n'existent pas.
    L'export de Godot se fie à un cache qui oublie celles des modèles importés : sans leurs matières, les armes devenaient un cube rose."""
    godot = find_godot()
    if not godot:
        sys.exit("Godot introuvable : indiquer --godot <chemin> (ou la variable GODOT).")
    with tempfile.TemporaryDirectory() as tmp:
        source, result = Path(tmp) / "roots.txt", Path(tmp) / "deps.txt"
        source.write_text("\n".join("res://" + f for f in chosen), encoding="utf-8", newline="\n")
        log = Path(tmp) / "log.txt"
        with open(log, "wb") as out:
            run = subprocess.Popen([godot, "--headless", "--path", str(ROOT), "--script", "res://tools/export/dependencies.gd", "--", str(source), str(result)],
                                   stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
            try: run.wait(timeout=180)
            except subprocess.TimeoutExpired: run.kill(); run.wait()
        lines = result.read_text(encoding="utf-8").splitlines() if result.exists() else []
        if not lines or lines[-1] != "# fin":
            sys.exit("Lecture des dépendances impossible :\n" + log.read_text(encoding="utf-8", errors="replace")[-2000:])
    found, missing = set(), []
    for line in lines[:-1]:
        if line.startswith("! "): missing.append(line[2:])
        else:
            f = line.removeprefix("res://")
            if not f.startswith(".godot/") and (ROOT / f).is_file(): found.add(f)
    return found, missing


def write_presets(chosen):
    cfg = ROOT / "export_presets.cfg"
    text = cfg.read_text(encoding="utf-8")
    listing = "export_files=PackedStringArray(" + ", ".join(f'"res://{f}"' for f in chosen) + ")"
    out, sections = [], re.split(r"(?m)^(?=\[)", text)
    for section in sections:
        name = re.search(r'^name="([^"]*)"', section, re.M)
        if section.startswith("[preset.") and ".options]" not in section[:30] and name and name.group(1) in PRESETS:
            section = re.sub(r'(?m)^export_filter=.*$', 'export_filter="resources"', section)
            if re.search(r"(?m)^export_files=", section):
                section = re.sub(r"(?m)^export_files=.*$", lambda _: listing, section)
            else:
                section = re.sub(r'(?m)^(export_filter=.*)$', lambda m: m.group(1) + "\n" + listing, section)
            m = re.search(r'(?m)^include_filter="([^"]*)"', section)
            parts = [p.strip() for p in m.group(1).split(",") if p.strip()] if m else []
            parts += [p for p in INCLUDE if p not in parts]
            section = re.sub(r'(?m)^include_filter=.*$', lambda _: 'include_filter="' + ", ".join(parts) + '"', section)
        out.append(section)
    cfg.write_text("".join(out), encoding="utf-8", newline="\n")


def glb_textures(chosen):
    """(textures extraites présentes, manquantes) des .glb retenus : voir missing_glb_textures."""
    present, missing = [], []
    for name in missing_glb_textures(chosen, present):
        missing.append(name)
    return present, missing


def missing_glb_textures(chosen, present=None):
    """Images intégrées aux .glb dont Godot a extrait un .png à côté (gltf/embedded_image_handling=1) : si un de ces .png a été supprimé,
    le modèle entier ne se charge plus dans le jeu exporté (l'éditeur, lui, le tolère). Retourne les fichiers manquants."""
    import json, struct
    missing = []
    for f in chosen:
        if not f.endswith(".glb"):
            continue
        imp = ROOT / (f + ".import")
        if not imp.exists() or "gltf/embedded_image_handling=1" not in imp.read_text(encoding="utf-8", errors="ignore"):
            continue
        try:
            with open(ROOT / f, "rb") as g:
                g.read(12)
                length, _ = struct.unpack("<II", g.read(8))
                doc = json.loads(g.read(length))
        except (OSError, ValueError, struct.error):
            continue
        stem = Path(f).with_suffix("")
        for image in doc.get("images", []):
            name = image.get("name")
            ext = {"image/jpeg": "jpg", "image/webp": "webp"}.get(image.get("mimeType"), "png")
            if not name or image.get("bufferView") is None:
                continue
            if (ROOT / f"{stem}_{name}.{ext}").exists():
                if present is not None: present.append(f"{stem.as_posix()}_{name}.{ext}")
            else:
                missing.append(f"{stem.as_posix()}_{name}.{ext}")
    return missing


def main():
    try: sys.stdout.reconfigure(encoding="utf-8")
    except Exception: pass
    chosen, files = roots()
    # les textures extraites des modèles .glb : l'export ne les suit pas toujours (fichiers recréés par une réimportation)
    present, missing = glb_textures(chosen)
    chosen = sorted(set(chosen) | set(present))
    found, absent = dependencies(chosen)
    chosen = sorted(set(chosen) | found)
    if absent:
        print("ATTENTION : dépendances introuvables (la ressource ne se chargera pas, ou incomplète) :")
        for line in absent[:40]: print("  - " + line.replace("\t", " -> "))
    if missing:
        print("ATTENTION : textures extraites d'un modèle .glb supprimées, le modèle ne se chargera pas dans le jeu exporté.")
        print("  Dans Godot : clic droit sur le .glb > Réimporter (les textures sont recréées). Fichiers manquants :")
        for m in missing[:20]: print("  - " + m)
    if "--dry-run" in sys.argv:
        for f in chosen: print(f)
    print(f"{len(chosen)} ressources retenues, dépendances comprises (sur {len(files)})")
    if "--dry-run" not in sys.argv:
        write_presets(chosen)
        print("export_presets.cfg mis à jour :", ", ".join(PRESETS))


if __name__ == "__main__":
    main()
