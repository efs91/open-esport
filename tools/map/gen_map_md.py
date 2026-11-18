"""Génère tools/map/MAP.md : la description de la carte « Frontière 01 » pour quelqu'un (ou une IA) qui lit les fichiers.

La scène Frontiere01.tscn reste la référence (elle s'édite à la main dans Godot) : ce script la LIT, il ne la modifie pas.
Les zones, passes et routes viennent de tools/map/layout.py. Relancer après une modification de la carte :

    python tools/map/gen_map_md.py
"""
import collections
import math
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.dirname(__file__))
import layout  # noqa: E402

SCENE = os.path.join(ROOT, 'scenes', 'maps', 'Frontiere01.tscn')
OUT = os.path.join(os.path.dirname(__file__), 'MAP.md')

text = open(SCENE, encoding='utf-8').read()

# ressources : id -> chemin
resources = {m.group(2): m.group(1) for m in re.finditer(r'\[ext_resource [^\]]*?path="([^"]*)" id="([^"]*)"', text)}

# nœuds : on découpe le fichier en blocs commençant par [node ...]
nodes = []
for block in re.split(r'(?m)^(?=\[node )', text)[1:]:
    head = block.split('\n', 1)[0]
    name = re.search(r'name="([^"]*)"', head).group(1)
    parent = re.search(r'parent="([^"]*)"', head)
    ntype = re.search(r'type="([^"]*)"', head)
    inst = re.search(r'instance=ExtResource\("([^"]*)"\)', head)
    tr = re.search(r'transform = Transform3D\(([^)]*)\)', block)
    pos = None
    if tr:
        v = [float(x) for x in tr.group(1).split(',')]
        pos = (v[9], v[10], v[11])
    asset = None
    if inst and inst.group(1) in resources:
        asset = os.path.splitext(os.path.basename(resources[inst.group(1)]))[0]
    nodes.append(dict(name=name, parent=parent.group(1) if parent else None, type=ntype.group(1) if ntype else None, pos=pos, asset=asset, block=block))


def under(prefix):
    return [n for n in nodes if n['parent'] == prefix or (n['parent'] or '').startswith(prefix + '/')]


def dist2d(p, c):
    return math.hypot(p[0] - c[0], p[2] - c[1]) if p else None


zone_nodes = {'A': 'Zones/A_Orion', 'B': 'Zones/B_Delta', 'C': 'Zones/C_Nama'}
loot_obj = [n for n in nodes if n['parent'] == 'Loot/Objets' and n['pos']]
loot_chest = [n for n in nodes if n['parent'] == 'Loot/Coffres' and n['pos']]
zip_parts = under('Ziplines')
colliders = [n for n in nodes if n['parent'] == 'BuildingColliders']


def zone_of(p):
    """La zone nommée dont le plateau contient (ou est le plus proche de) ce point, sinon « hors zone »."""
    best, bd = None, 1e9
    for k, z in layout.ZONES.items():
        d = dist2d(p, z['c']) - z['r']
        if d < bd:
            best, bd = k, d
    return best if bd <= 40 else None


def top_assets(items, n=8):
    c = collections.Counter(i['asset'] or i['type'] or '?' for i in items)
    return ', '.join(f'`{a}` ×{k}' for a, k in c.most_common(n))


def bbox(items):
    pts = [i['pos'] for i in items if i['pos']]
    if not pts:
        return None
    xs, zs, ys = [p[0] for p in pts], [p[2] for p in pts], [p[1] for p in pts]
    return min(xs), max(xs), min(zs), max(zs), min(ys), max(ys)


L = []
w = L.append
w('# Carte « Frontière 01 » — description pour lecture (IA ou humain)')
w('')
w('> Fichier **généré** par `python tools/map/gen_map_md.py` à partir de `scenes/maps/Frontiere01.tscn` et de `tools/map/layout.py`. Ne pas l\'éditer à la main : relancer le script après une modification de la carte.')
w('> La scène reste la référence et s\'édite dans Godot ; ce fichier ne sert qu\'à se repérer.')
w('')
w('## Repère et dimensions')
w('')
w('- Carte de 1 536 m × 1 536 m (1 pixel = 1 m), origine au centre : **X** vers l\'est, **Z** vers le sud (le nord est en **Z négatif**), **Y** = altitude.')
w('- Zone jouable : ±600 m autour de l\'origine ; au-delà, rebord montagneux (puis le décor de montagnes lointaines, nœud `Montagnes`).')
w('- Terrain : nœud `Terrain3D` (données dans `maps/frontiere01/terrain/`, hauteur brute dans `maps/frontiere01/data/height.r32`, altitudes de 26 m à 311 m).')
w('- Positions ci-dessous : `(x, z)` en mètres.')
w('')
w('## Vue d\'ensemble des zones')
w('')
w('| Lettre | Nom | Nœud de la scène | Centre (x, z) | Rayon du plateau | Altitude du plateau | Bâtiments et accessoires |')
w('|---|---|---|---|---|---|---|')
for k, z in layout.ZONES.items():
    items = [n for n in under(zone_nodes[k]) if n['asset']]
    w(f"| {k} | {z['name']} | `{zone_nodes[k]}` | ({z['c'][0]:.0f}, {z['c'][1]:.0f}) | {z['r']:.0f} m | {z['h']:.0f} m | {len(items)} |")
m = layout.MASSIF
w(f"| — | Massif central | (relief du terrain) | ({m['c'][0]:.0f}, {m['c'][1]:.0f}) | {m['r']:.0f} m | {m['h']:.0f} m | — |")
w('')
w('Les trois zones sont de grands **plateaux plats** reliés par des routes ; le massif central les sépare (il sert de couverture et de point de vue).')
w('')

for k, z in layout.ZONES.items():
    items = [n for n in under(zone_nodes[k]) if n['asset']]
    c = z['c']
    w(f"## Zone {k} — {z['name']}")
    w('')
    w(f"- **Nœud** : `{zone_nodes[k]}` dans `scenes/maps/Frontiere01.tscn` (chercher `parent=\"{zone_nodes[k]}\"` pour lister ses {len(items)} instances).")
    w(f"- **Centre** : ({c[0]:.0f}, {c[1]:.0f}), rayon {z['r']:.0f} m, plateau à {z['h']:.0f} m d'altitude.")
    bb = bbox(items)
    if bb:
        w(f"- **Emprise des bâtiments** : x de {bb[0]:.0f} à {bb[1]:.0f}, z de {bb[2]:.0f} à {bb[3]:.0f}, altitude {bb[4]:.0f} à {bb[5]:.0f} m.")
    w(f"- **Contenu le plus fréquent** : {top_assets(items)}.")
    nobj = sum(1 for n in loot_obj if zone_of(n['pos']) == k)
    nch = sum(1 for n in loot_chest if zone_of(n['pos']) == k)
    w(f"- **Butin** dans l'emprise de la zone : {nch} coffre(s) (`Loot/Coffres`), {nobj} objet(s) au sol (`Loot/Objets`).")
    near = [name for name, p in layout.PASSES.items() if math.hypot(p[0] - c[0], p[1] - c[1]) < 520]
    w(f"- **Passes proches** : {', '.join(near) if near else '—'}.")
    roads = [name for name, r in layout.ROADS.items() if any(math.hypot(px - c[0], pz - c[1]) < 25 for px, pz in r['pts'])]
    w(f"- **Routes qui y arrivent** : {', '.join(roads) if roads else '—'}.")
    w('')

w('## Passes (goulets d\'étranglement)')
w('')
w('| Nom | Position (x, z) |')
w('|---|---|')
for name, p in layout.PASSES.items():
    w(f'| {name} | ({p[0]:.0f}, {p[1]:.0f}) |')
w('')
w('## Routes et chemins')
w('')
w('| Nom | Largeur | Points (x, z) |')
w('|---|---|---|')
for name, r in {**layout.ROADS, **layout.TRAILS}.items():
    pts = ' → '.join(f'({x:.0f}, {z:.0f})' for x, z in r['pts'])
    w(f"| {name} | {r['w']} m | {pts} |")
w('')

w('## Autres parties de la scène')
w('')
w('| Nœud | Contenu |')
w('|---|---|')
w(f'| `Terrain3D` | le relief et sa peinture (aucun enfant) |')
w(f'| `Sun`, `WorldEnvironment` | lumière, ciel, brouillard, occlusion ambiante |')
w(f'| `Montagnes` | décor de montagnes lointaines autour de la carte (`scripts/modules/MountainBackdrop.cs`) |')
w(f'| `BuildingColliders` | {len(colliders)} boîtes de collision des bâtiments (séparées des modèles) |')
w(f'| `Loot/Coffres`, `Loot/Objets` | {len(loot_chest)} coffres, {len(loot_obj)} objets au sol (points de butin éditables) |')
w(f'| `Ziplines` | {len(zip_parts)} éléments de tyroliennes (piliers, câbles, tremplins) |')
w('')
w('## Fichiers liés')
w('')
w('- Scène (référence, éditée dans Godot) : `scenes/maps/Frontiere01.tscn`')
w('- Fichier serveur (collisions, butin, tyroliennes) : `assets/maps/frontiere01.world` — **à régénérer après toute modification** (commande « Fichier serveur » de `tools/map/README.md`).')
w('- Image de la carte (touche M) : `assets/maps/frontiere01.png` (`tools/map/gen_minimap.py`)')
w('- Données : `maps/frontiere01/data/` (hauteurs, bâtiments, tyroliennes, boîtes de la nature), modèles : `maps/frontiere01/models/`')
w('- Positions de référence des zones, passes et routes : `tools/map/layout.py`')
w('')
w('## Comment travailler sur une zone')
w('')
w('1. Repérer la zone ci-dessus (nœud, centre, rayon).')
w('2. Lister ses éléments : chercher `parent="Zones/<NOEUD>"` dans `scenes/maps/Frontiere01.tscn` (le nom du modèle est dans la ressource externe de chaque instance, l\'emplacement dans `transform`).')
w('3. Les collisions sont dans `BuildingColliders` (une boîte par bâtiment, à la même position) ; le butin est dans `Loot` (filtrer par position à l\'intérieur du rayon de la zone).')
w('4. Après toute modification de la zone : régénérer `assets/maps/frontiere01.world`, puis relancer le serveur.')
w('')

open(OUT, 'w', encoding='utf-8', newline='\n').write('\n'.join(L))
print('écrit', OUT, len(L), 'lignes')
