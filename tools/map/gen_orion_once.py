"""Zone A « Complexe Orion » : premier jet UNIQUE des bâtiments, passerelles, rues et butin.

⚠ À NE LANCER QU'UNE FOIS. Ce script écrit des scènes Godot ordinaires (scenes/maps/orion/*.tscn, scenes/loot/groupes/*.tscn)
puis remplace UNIQUEMENT le contenu d'Orion dans scenes/maps/Frontiere01.tscn (nœuds de Zones/A_Orion, boîtes de BuildingColliders et
points de Loot situés dans l'emprise d'Orion). Ensuite, les scènes sont la référence et s'éditent dans Godot : ne pas relancer le script,
il refuse d'ailleurs de tourner si scenes/maps/orion/ existe déjà.

Repère : x vers l'est, z vers le sud, y vers le haut. Tuiles de 4 m, niveaux de 3 m (MegaKit). Centre d'Orion : (-372, -288).
Les collisions sont dans chaque scène de bâtiment (StaticBody3D « Colliders ») ; WorldExporter les lit sous « Zones ».
"""
import os, sys, json, math, random, re, shutil

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "scenes", "maps", "orion")
LOOTG = os.path.join(ROOT, "scenes", "loot", "groupes")
MAIN = os.path.join(ROOT, "scenes", "maps", "Frontiere01.tscn")
FINAL = os.path.join(ROOT, "maps", "frontiere01", "data", "buildings_final.json")
KITS = json.load(open(os.path.join(os.path.dirname(__file__), "kits.json"), encoding="utf-8"))
BACKUP = os.environ.get("ORION_BACKUP", os.path.join(ROOT, "logs", "orion_backup"))

CX, CZ = -372.0, -288.0          # centre d'Orion (monde)
Y0 = 78.10                        # sol commun : au-dessus du relief (77,6 à 78,06) avec une marche d'au plus 0,46 m
T, LH = 4.0, 3.0                  # tuile et hauteur d'étage
RECT = (-470.0, -375.0, -275.0, -180.0)   # emprise d'Orion (x0, z0, x1, z1) pour le nettoyage de la scène


def fmt(v):
    s = ("%.4f" % v).rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def rot_xz(dx, dz, r):
    a = math.radians(r)
    c, s = round(math.cos(a)), round(math.sin(a))
    return dx * c + dz * s, -dx * s + dz * c          # convention Basis(UP, angle) de Godot


def tf(x, y, z, r=0, sx=1.0, sy=1.0, sz=1.0):
    a = math.radians(r)
    c, s = round(math.cos(a), 6), round(math.sin(a), 6)
    return "Transform3D(%s, 0, %s, 0, %s, 0, %s, 0, %s, %s, %s, %s)" % (fmt(c * sx), fmt(s * sz), fmt(sy), fmt(-s * sx), fmt(c * sz), fmt(x), fmt(y), fmt(z))


# ============================================================================================ scène texte
class Scn:
    def __init__(self, name, kind="Node3D"):
        self.name, self.kind = name, kind
        self.exts, self.subs, self.nodes = {}, {}, []
        self.groups, self.cnt, self.nbox = {}, 0, 0
        self.boxes = []          # collisions déjà ajoutées (pour la fusion et les statistiques)

    def ext(self, path):
        if path not in self.exts:
            self.exts[path] = "e%d" % (len(self.exts) + 1)
        return self.exts[path]

    def sub(self, body, kind):
        key = (kind, body)
        if key not in self.subs:
            self.subs[key] = "s%d" % (len(self.subs) + 1)
        return self.subs[key]

    def group(self, g):
        if g not in self.groups:
            self.groups[g] = True
            self.nodes.append('[node name="%s" type="Node3D" parent="."]\n' % g)
        return g

    def piece(self, g, key, x, y, z, r=0, sx=1.0, sy=1.0, sz=1.0):
        k = KITS.get(key)
        if k is None:
            print("pièce inconnue :", key)
            return
        self.group(g)
        self.cnt += 1
        base = key.split("|")[1]
        eid = self.ext("res://" + k["path"])
        self.nodes.append('[node name="%s_%d" parent="%s" instance=ExtResource("%s")]\ntransform = %s\n' % (base, self.cnt, g, eid, tf(x, y, z, r, sx, sy, sz)))

    def scene_inst(self, g, path, x, y, z, r=0, name=None):
        self.group(g)
        self.cnt += 1
        eid = self.ext(path)
        self.nodes.append('[node name="%s_%d" parent="%s" instance=ExtResource("%s")]\ntransform = %s\n' % (name or "Inst", self.cnt, g, eid, tf(x, y, z, r)))

    def col(self, x0, y0, z0, x1, y1, z1):
        if x1 - x0 < 0.01 or y1 - y0 < 0.01 or z1 - z0 < 0.01:
            return
        self.boxes.append((x0, y0, z0, x1, y1, z1))

    def mesh_box(self, name, x0, y0, z0, x1, y1, z1, color, rough=0.85, emission=None):
        mat = "albedo_color = Color(%s, %s, %s, 1)\nroughness = %s\n" % (fmt(color[0]), fmt(color[1]), fmt(color[2]), fmt(rough))
        if emission:
            mat += "emission_enabled = true\nemission = Color(%s, %s, %s, 1)\nemission_energy_multiplier = 1.5\n" % emission
        mid = self.sub(mat, "StandardMaterial3D")
        mesh = self.sub("material = SubResource(\"%s\")\nsize = Vector3(%s, %s, %s)\n" % (mid, fmt(x1 - x0), fmt(y1 - y0), fmt(z1 - z0)), "BoxMesh")
        self.nodes.append('[node name="%s" type="MeshInstance3D" parent="."]\ntransform = %s\nmesh = SubResource("%s")\n' % (name, tf((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), mesh))

    def text(self):
        out = ["[gd_scene format=3]\n"]
        for p, i in self.exts.items():
            out.append('[ext_resource type="PackedScene" path="%s" id="%s"]' % (p, i))
        out.append("")
        # collisions : boîtes dédupliquées
        nodes = list(self.nodes)
        if self.boxes:
            nodes.append('[node name="Colliders" type="StaticBody3D" parent="."]\n')
            seen = set()
            for n, b in enumerate(self.boxes):
                kb = tuple(round(v, 3) for v in b)
                if kb in seen:
                    continue
                seen.add(kb)
                sid = self.sub("size = Vector3(%s, %s, %s)\n" % (fmt(b[3] - b[0]), fmt(b[4] - b[1]), fmt(b[5] - b[2])), "BoxShape3D")
                nodes.append('[node name="Box_%d" type="CollisionShape3D" parent="Colliders"]\ntransform = %s\nshape = SubResource("%s")\n' % (n, tf((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2), sid))
        for (kind, body), i in self.subs.items():
            out.append('[sub_resource type="%s" id="%s"]\n%s' % (kind, i, body))
        out.append('[node name="%s" type="%s"]\n' % (self.name, self.kind))
        out.extend(nodes)
        return "\n".join(out) + "\n"

    def save(self, folder):
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, self.name + ".tscn")
        open(path, "w", encoding="utf-8", newline="\n").write(self.text())
        return path


# ============================================================================================ pièces et collisions
WALLS = {   # clé -> (épaisseur de collision [x0,x1] en local, type)
    "mega|WallAstra_Straight": ((-2.45, -1.65), "solid", 3.0),
    "mega|WallAstra_Straight_Flat": ((-2.2, -1.8), "solid", 3.0),
    "mega|WallBand_Straight": ((-2.2, -1.8), "solid", 3.0),
    "mega|WallAstra_Straight_Window": ((-2.45, -1.65), "window", 3.0),
    "mega|WallWindow_Straight": ((-2.2, -1.8), "window", 3.2),
}
SILL, LINTEL = 1.35, 2.35
FACE = {"S": "mega|WallAstra_Straight", "A": "mega|WallAstra_Straight_Window", "G": "mega|WallWindow_Straight", "F": "mega|WallAstra_Straight_Flat"}
SIDE_ROT = {"W": 0, "E": 180, "N": 270, "S": 90}


def wall_boxes(key, x, y, z, r):
    (x0, x1), kind, h = WALLS[key]
    parts = [(0.0, h)] if kind == "solid" else [(0.0, SILL), (LINTEL, h)]
    out = []
    for (a, b) in parts:
        pts = [rot_xz(u, v, r) for u in (x0, x1) for v in (-2.0, 2.0)]
        out.append((x + min(p[0] for p in pts), y + a, z + min(p[1] for p in pts), x + max(p[0] for p in pts), y + b, z + max(p[1] for p in pts)))
    return out


def merge_tiles(tiles):
    """rectangles (i0, j0, i1, j1) [fin exclue] couvrant exactement un ensemble de tuiles (fusion par lignes puis par colonnes)"""
    rows = {}
    for (i, j) in sorted(tiles, key=lambda t: (t[1], t[0])):
        rows.setdefault(j, []).append(i)
    runs = []
    for j, lst in sorted(rows.items()):
        a = lst[0]
        p = a
        for i in lst[1:]:
            if i != p + 1:
                runs.append((a, j, p + 1))
                a = i
            p = i
        runs.append((a, j, p + 1))
    rects = []
    for (a, j, b) in runs:
        for k, rc in enumerate(rects):
            if rc[0] == a and rc[2] == b and rc[3] == j:
                rects[k] = (a, rc[1], b, j + 1)
                break
        else:
            rects.append((a, j, b, j + 1))
    return rects


# ============================================================================================ bâtiment
class Bld:
    def __init__(self, name, wx, wz, nx, nz, L, facade=None, doors=(), voids=None, stairs=(), partitions=(), roof_gaps=(), door_lv=(),
                 glass_roof=(), floor_key=None, wall_gaps=()):
        """(wx, wz) = coin nord-ouest en coordonnées locales à la zone ; stairs = [(niveau, i, j, dir)] ; voids = {niveau: {(i,j)}}"""
        self.name, self.nx, self.nz, self.L = name, nx, nz, L
        self.wx, self.wz = wx, wz
        self.s = Scn(name)
        self.facade = facade or (lambda lv, side, idx: "G")
        self.doors = set(doors)
        self.door_lv = set(door_lv)
        self.voids = {k: set(v) for k, v in (voids or {}).items()}
        self.stairs = list(stairs)
        self.partitions = list(partitions)
        self.roof_gaps = set(roof_gaps)
        self.wall_gaps = set(wall_gaps)            # (niveau, côté, indice) : arête sans mur (passerelle)
        self.glass_roof = set(glass_roof)
        self.floor_key = floor_key
        self.holes = {}
        self.blocked = {}
        for (lv, i, j, d) in self.stairs:
            t0, t1, land, appr = self.stair_tiles(i, j, d)
            self.holes.setdefault(lv + 1, set()).update((t0, t1))
            self.blocked.setdefault(lv, set()).update((t0, t1, appr))
            self.blocked.setdefault(lv + 1, set()).add(land)

    # --- repères
    def tc(self, i, j):
        return (i + 0.5) * T, (j + 0.5) * T

    @staticmethod
    def stair_tiles(i, j, d):
        dx, dz = {"N": (0, 1), "S": (0, -1), "E": (-1, 0), "W": (1, 0)}[d]       # sens de la descente
        top, bot = (i, j), (i + dx, j + dz)
        land = (i - dx, j - dz)
        appr = (i + 2 * dx, j + 2 * dz)
        return top, bot, land, appr

    def y(self, lv):
        return lv * LH

    def has_floor(self, lv, i, j):
        if not (0 <= i < self.nx and 0 <= j < self.nz):
            return False
        if (i, j) in self.voids.get(lv, ()) or (i, j) in self.holes.get(lv, ()):
            return False
        return True

    # --- construction
    def build(self):
        s, L = self.s, self.L
        for lv in range(L + 1):
            y = self.y(lv)
            tiles = [(i, j) for i in range(self.nx) for j in range(self.nz) if self.has_floor(lv, i, j)]
            for (i, j) in tiles:
                cx, cz = self.tc(i, j)
                if lv == L and (i, j) in self.glass_roof:
                    key = "mega|Platform_Window_Wide"
                elif self.floor_key and lv == 0:
                    key = self.floor_key
                else:
                    key = "mega|Platform_DarkPlates" if lv == 0 else "mega|Platform_Simple2"
                s.piece("Sols", key, cx, y, cz)
            for (a, b, c, d) in merge_tiles(tiles):
                s.col(a * T, y - (0.5 if lv == 0 else 0.3), b * T, c * T, y, d * T)
        # murs extérieurs
        for lv in range(L):
            y = self.y(lv)
            for i in range(self.nx):
                for j in range(self.nz):
                    for side, on, idx in (("W", i == 0, j), ("E", i == self.nx - 1, j), ("N", j == 0, i), ("S", j == self.nz - 1, i)):
                        if not on:
                            continue
                        if (lv == 0 and (side, idx) in self.doors) or (lv, side, idx) in self.door_lv or (lv, side, idx) in self.wall_gaps:
                            continue
                        key = FACE[self.facade(lv, side, idx)]
                        cx, cz = self.tc(i, j)
                        r = SIDE_ROT[side]
                        s.piece("Murs", key, cx, y, cz, r)
                        for b in wall_boxes(key, cx, y, cz, r):
                            s.col(*b)
        # cloisons : (niveau, 'V'|'H', ligne, a, b, trous, style)
        for (lv, axis, line, a, b, gaps, style) in self.partitions:
            y = self.y(lv)
            key = FACE[style]
            for t in range(a, b):
                if t in gaps:
                    continue
                if axis == "V":       # ligne verticale (x = line*4), tuile (line, t) : arête ouest
                    cx, cz = self.tc(line, t)
                    r = 0
                else:                 # ligne horizontale (z = line*4), tuile (t, line) : arête nord
                    cx, cz = self.tc(t, line)
                    r = 270
                s.piece("Cloisons", key, cx, y, cz, r)
                for bx in wall_boxes(key, cx, y, cz, r):
                    s.col(*bx)
        # escaliers
        for (lv, i, j, d) in self.stairs:
            top, bot, land, appr = self.stair_tiles(i, j, d)
            if d == "N":
                px, pz, r = (i + 0.5) * T, j * T, 0
            elif d == "S":
                px, pz, r = (i + 0.5) * T, (j + 1) * T, 180
            elif d == "E":
                px, pz, r = (i + 1) * T, (j + 0.5) * T, 270
            else:
                px, pz, r = i * T, (j + 0.5) * T, 90
            y = self.y(lv)
            sz = 8.0 / 6.08
            s.piece("Escaliers", "mega|Platform_Stairs_4WideTall", px, y, pz, r, 1.0, 1.0, sz)
            n = 12
            for k in range(n):
                a, b = k * 8.0 / n, (k + 1) * 8.0 / n
                pts = [rot_xz(u, v, r) for u in (-2.0, 2.0) for v in (a, b)]
                s.col(px + min(p[0] for p in pts), y, pz + min(p[1] for p in pts), px + max(p[0] for p in pts), y + (n - k) * LH / n, pz + max(p[1] for p in pts))
        # garde-corps autour des vides (niveau >= 1) et parapets de toit
        for lv in range(1, L + 1):
            y = self.y(lv)
            for i in range(self.nx):
                for j in range(self.nz):
                    if not self.has_floor(lv, i, j):
                        continue
                    for side, di, dj in (("W", -1, 0), ("E", 1, 0), ("N", 0, -1), ("S", 0, 1)):
                        ni, nj = i + di, j + dj
                        edge_out = not (0 <= ni < self.nx and 0 <= nj < self.nz)
                        void_n = (not edge_out) and ((ni, nj) in self.voids.get(lv, ()))
                        if (lv == L and edge_out and (side, i, j) not in self.roof_gaps) or void_n:
                            cx, cz = self.tc(i, j)
                            r = SIDE_ROT[side]
                            s.piece("Garde-corps", "mega|ShortWall_Simple1_Straight", cx, y, cz, r)
                            pts = [rot_xz(u, v, r) for u in (-2.15, -1.85) for v in (-2.0, 2.0)]
                            s.col(cx + min(p[0] for p in pts), y, cz + min(p[1] for p in pts), cx + max(p[0] for p in pts), y + 1.0, cz + max(p[1] for p in pts))
        return self

    def free_tiles(self, lv):
        out = []
        for i in range(self.nx):
            for j in range(self.nz):
                if self.has_floor(lv, i, j) and (i, j) not in self.blocked.get(lv, ()):
                    out.append((i, j))
        return out


def facade_rule(style_by_lv):
    """style_by_lv : dict niveau -> 'glass' | 'mixed' | 'solid' (par défaut le dernier)"""
    def f(lv, side, idx):
        st = style_by_lv.get(lv, style_by_lv.get("*", "mixed"))
        if st == "glass":
            return "G"
        if st == "solid":
            return "S" if idx % 2 == 0 else "A"
        return "S" if idx % 3 == 2 else ("G" if idx % 2 == 0 else "A")
    return f


# ============================================================================================ passerelles
def bridge(name, length_tiles, axis, kind, h_lv=2):
    """passerelle de length_tiles*4 m. origine = centre du bord de départ au niveau du sol de départ ; axe 'x' (vers l'est) ou 'z' (vers le sud).
    kind : 'glass' (tube vitré fermé) ou 'open' (rampe ouverte avec garde-corps)"""
    s = Scn(name)
    n = length_tiles
    for k in range(n):
        u = (k + 0.5) * T
        x, z = (u, 0.0) if axis == "x" else (0.0, u)
        s.piece("Sol", "mega|Platform_Window_Wide" if kind == "glass" else "mega|Platform_Metal", x, 0, z)
        if kind == "glass":
            s.piece("Toit", "mega|Platform_Simple2", x, LH, z)
        for side in (("N", "S") if axis == "x" else ("W", "E")):
            r = SIDE_ROT[side]
            ex, ez = {"N": (0, -2.0), "S": (0, 2.0), "W": (-2.0, 0), "E": (2.0, 0)}[side]
            # les murs sont posés au centre de la tuile : le décalage vient de la rotation de la pièce
            if kind == "glass":
                s.piece("Parois", "mega|WallWindow_Straight", x, 0, z, r)
                for b in wall_boxes("mega|WallWindow_Straight", x, 0, z, r):
                    s.col(*b)
            else:
                s.piece("Garde-corps", "mega|ShortWall_Simple1_Straight", x, 0, z, r)
                pts = [rot_xz(u2, v, r) for u2 in (-2.15, -1.85) for v in (-2.0, 2.0)]
                s.col(x + min(p[0] for p in pts), 0, z + min(p[1] for p in pts), x + max(p[0] for p in pts), 1.0, z + max(p[1] for p in pts))
    if axis == "x":
        s.col(0, -0.3, -2.0, n * T, 0, 2.0)
        if kind == "glass":
            s.col(0, LH - 0.3, -2.0, n * T, LH, 2.0)
    else:
        s.col(-2.0, -0.3, 0, 2.0, 0, n * T)
        if kind == "glass":
            s.col(-2.0, LH - 0.3, 0, 2.0, LH, n * T)
    return s


# ============================================================================================ prefabs de groupes de coffres
CHEST = "res://scenes/loot/CoffreButin.tscn"
GROUPS = {}   # nom -> liste de (x, z, yaw)  [le yaw local 180° = regarde vers +z]


def _face(px, pz):
    return math.degrees(math.atan2(px, pz))


GROUPS["Coffres_U6"] = [(-1.5, -4.5, 180), (1.5, -4.5, 180), (-4.2, -1.4, -90), (-4.2, 1.6, -90), (4.2, -1.4, 90), (4.2, 1.6, 90)]
GROUPS["Coffres_Face4"] = [(-1.4, -2.6, 180), (1.4, -2.6, 180), (-1.4, 2.6, 0), (1.4, 2.6, 0)]
GROUPS["Coffres_Ligne5"] = [((k - 2) * 1.9, 0.0, 180) for k in range(5)]
GROUPS["Coffres_Rond6"] = [(3.6 * math.sin(math.radians(a)), 3.6 * math.cos(math.radians(a)), _face(3.6 * math.sin(math.radians(a)), 3.6 * math.cos(math.radians(a)))) for a in range(0, 360, 60)]
GROUPS["Coffres_Carre4"] = [(px, pz, _face(px, pz)) for (px, pz) in ((0, -2.6), (2.6, 0), (0, 2.6), (-2.6, 0))]
GROUPS["Coffres_Arc5"] = [(4.0 * math.sin(math.radians(a)), -4.0 * math.cos(math.radians(a)), _face(4.0 * math.sin(math.radians(a)), -4.0 * math.cos(math.radians(a)))) for a in (-72, -36, 0, 36, 72)]
FOOT = {"Coffres_U6": (5.2, 5.6, 2.8), "Coffres_Face4": (2.6, 3.4, 3.4), "Coffres_Ligne5": (5.0, 0.9, 0.9), "Coffres_Rond6": (4.6, 4.6, 4.6), "Coffres_Carre4": (3.5, 3.5, 3.5), "Coffres_Arc5": (5.0, 5.0, 1.4)}


def write_group_scenes():
    os.makedirs(LOOTG, exist_ok=True)
    for name, ch in GROUPS.items():
        lines = ["[gd_scene format=3]\n", '[ext_resource type="PackedScene" path="%s" id="1_chest"]\n' % CHEST, '[node name="%s" type="Node3D"]\n' % name]
        for k, (x, z, yaw) in enumerate(ch):
            lines.append('[node name="Coffre_%d" parent="." instance=ExtResource("1_chest")]\ntransform = %s\n' % (k + 1, tf(x, 0, z, yaw)))
        open(os.path.join(LOOTG, name + ".tscn"), "w", encoding="utf-8", newline="\n").write("\n".join(lines))


# ============================================================================================ plan d'Orion
B = {}          # nom -> Bld
LOOT_GROUPS = []   # (groupe, x_zone, y_monde, z_zone, yaw)
OBJETS = []        # (x_zone, y_monde, z_zone)
BRIDGES = []       # (Scn, x_zone, y_monde, z_zone, yaw)
EXTRA = []         # autres scènes (Scn, x_zone, y_monde, z_zone, yaw)
rng = random.Random(2026)


def place(b, group, lv, bx, bz, yaw=0):
    LOOT_GROUPS.append((group, b.wx + bx, Y0 + b.y(lv) + 0.05, b.wz + bz, yaw))
    # empreinte -> tuiles occupées (pour éviter les objets au sol sur les coffres)
    ex, ez, _ = FOOT[group][0], FOOT[group][1], 0
    r = rng
    return


def build_all():
    # ------------------------------------------------------------------------------- Tour Orion (5x5, 4 niveaux, atrium)
    atrium = {(i, j) for i in (1, 2, 3) for j in (1, 2, 3)}
    tour = Bld("Tour_Orion", 16, -52, 5, 5, 4, facade_rule({0: "glass", "*": "mixed"}),
               doors={("W", 3), ("S", 2), ("N", 2)}, door_lv={(2, "W", 1), (2, "E", 1)},
               voids={1: atrium, 2: atrium, 3: atrium},
               stairs=[(0, 0, 1, "N"), (1, 2, 4, "E"), (2, 4, 3, "S"), (3, 0, 3, "S")],
               glass_roof=atrium)
    B["Tour_Orion"] = tour.build()
    place(tour, "Coffres_Rond6", 0, 10, 10)
    place(tour, "Coffres_Ligne5", 1, 18.2, 10, -90)
    place(tour, "Coffres_Ligne5", 2, 10, 1.5, 0)
    place(tour, "Coffres_Ligne5", 3, 10, 18.5, 180)
    place(tour, "Coffres_Carre4", 4, 10, 10)

    # ------------------------------------------------------------------------------- Aile Laboratoires (12x3, 3 niveaux)
    gaps = {2, 5, 9}
    parts = []
    for lv in range(3):
        parts.append((lv, "H", 1, 0, 12, gaps, "G"))
        parts.append((lv, "H", 2, 0, 12, gaps, "G"))
    labs = Bld("Aile_Laboratoires", -60, -52, 12, 3, 3, facade_rule({0: "mixed", "*": "glass"}),
               doors={("S", 1), ("S", 6), ("N", 3), ("N", 8), ("W", 1), ("E", 1)}, door_lv={(2, "E", 1)},
               stairs=[(0, 4, 0, "E"), (1, 7, 2, "W"), (2, 4, 2, "E")], partitions=parts)
    B["Aile_Laboratoires"] = labs.build()
    place(labs, "Coffres_Ligne5", 0, 34, 1.5, 0)
    place(labs, "Coffres_Ligne5", 1, 18, 10.5, 180)
    place(labs, "Coffres_Ligne5", 2, 14, 1.5, 0)

    # ------------------------------------------------------------------------------- Data Center (6x4, salle haute + coursive)
    hall = {(i, j) for i in (1, 2, 3, 4) for j in (1, 2)}
    dc = Bld("Data_Center", -60, -32, 6, 4, 2, facade_rule({0: "solid", "*": "mixed"}),
             doors={("E", 1), ("S", 2), ("N", 3), ("W", 3)}, voids={1: hall}, stairs=[(0, 0, 1, "N"), (1, 5, 2, "S")])
    B["Data_Center"] = dc.build()
    for k in range(4):                                       # rangées de baies de serveurs au rez-de-chaussée
        for r_ in (1.6, 6.4):
            pass
    place(dc, "Coffres_Face4", 0, 12, 8)
    place(dc, "Coffres_Ligne5", 1, 12, 1.5, 0)
    place(dc, "Coffres_U6", 2, 12, 8, 0)

    # ------------------------------------------------------------------------------- Quartiers (4x3, 2 niveaux, toit-terrasse)
    q = Bld("Quartiers", 44, -48, 4, 3, 2, facade_rule({"*": "mixed"}), doors={("S", 1), ("N", 2), ("E", 1)},
            stairs=[(0, 2, 1, "E"), (1, 1, 2, "W")], roof_gaps={("W", 0, 0)}, door_lv=set())
    B["Quartiers"] = q.build()
    place(q, "Coffres_Ligne5", 0, 9.5, 1.5, 0)
    place(q, "Coffres_Ligne5", 1, 8, 1.5, 0)
    place(q, "Coffres_Face4", 2, 10, 4)

    # ------------------------------------------------------------------------------- Hangar (7x5, grande nef + coursive)
    nave = {(i, j) for i in (1, 2, 3, 4, 5) for j in (1, 2, 3)}
    hg = Bld("Hangar", 16, 20, 7, 5, 2, facade_rule({0: "solid", "*": "glass"}),
             doors={("N", 2), ("N", 3), ("N", 4), ("S", 2), ("S", 3), ("S", 4), ("W", 3), ("E", 2)}, voids={1: nave},
             stairs=[(0, 0, 1, "N"), (1, 6, 3, "S")], roof_gaps={("E", 6, 1)})
    B["Hangar"] = hg.build()
    place(hg, "Coffres_U6", 0, 14, 10, 0)
    place(hg, "Coffres_Ligne5", 1, 14, 1.5, 0)
    place(hg, "Coffres_Ligne5", 1, 14, 18.5, 180)
    place(hg, "Coffres_Face4", 2, 14, 10)

    # ------------------------------------------------------------------------------- Centrale d'énergie (4x4, 2 niveaux)
    ce = Bld("Centrale_Energie", 56, 16, 4, 4, 2, facade_rule({0: "solid", "*": "mixed"}), doors={("W", 3), ("S", 2)},
             stairs=[(0, 0, 1, "N"), (1, 3, 2, "S")], roof_gaps={("W", 0, 2)})
    B["Centrale_Energie"] = ce.build()
    place(ce, "Coffres_Carre4", 0, 9, 9)
    place(ce, "Coffres_Ligne5", 1, 9.5, 1.5, 0)
    place(ce, "Coffres_Face4", 2, 8, 8)

    # ------------------------------------------------------------------------------- Poste de sécurité (3x3, 3 niveaux)
    sp = Bld("Poste_Securite", -36, 24, 4, 3, 3, facade_rule({0: "solid", 1: "mixed", 2: "glass", "*": "glass"}), doors={("S", 1), ("E", 1)},
             stairs=[(0, 2, 1, "E"), (1, 1, 2, "W"), (2, 2, 0, "E")], door_lv={(2, "W", 0)})
    B["Poste_Securite"] = sp.build()
    place(sp, "Coffres_Carre4", 3, 10, 8)

    # ------------------------------------------------------------------------------- Centre médical (5x3, 2 niveaux)
    md = Bld("Centre_Medical", -64, 24, 5, 3, 2, facade_rule({0: "mixed", "*": "glass"}), doors={("E", 1), ("S", 2), ("N", 1)},
             stairs=[(0, 2, 1, "E"), (1, 3, 2, "E")], roof_gaps={("E", 4, 0)})
    B["Centre_Medical"] = md.build()
    place(md, "Coffres_Face4", 0, 16, 6)
    place(md, "Coffres_Ligne5", 1, 10, 1.5, 0)
    place(md, "Coffres_Carre4", 2, 10, 4.5)

    # ------------------------------------------------------------------------------- Portails nord et sud (2 x [3x2, 2 niveaux] + arche)
    for tag, zz in (("Nord", -88), ("Sud", 80)):
        for side, xx in (("O", -28), ("E", 12)):
            nm = "Portail_%s_%s" % (tag, side)
            if side == "O":
                st = [(0, 2, 0, "E"), (1, 1, 1, "W")]
                gap = {("E", 3, 0)}
            else:
                st = [(0, 1, 0, "W"), (1, 2, 1, "E")]
                gap = {("W", 0, 0)}
            g = Bld(nm, xx, zz, 4, 2, 2, facade_rule({0: "solid", "*": "mixed"}), doors={("W", 1), ("E", 1)}, stairs=st, roof_gaps=gap)
            B[nm] = g.build()


def objets_auto():
    """objets au sol : autour de chaque groupe de coffres + dans les tuiles libres de chaque bâtiment"""
    for b in B.values():
        for lv in range(0, b.L + 1):
            if lv == b.L and b.L > 0 and False:
                continue
            tiles = b.free_tiles(lv)
            # tuiles occupées par un groupe de coffres de ce niveau
            occ = set()
            for (g, gx, gy, gz, yaw) in LOOT_GROUPS:
                if abs(gy - (Y0 + b.y(lv) + 0.05)) > 0.01:
                    continue
                lx, lz = gx - b.wx, gz - b.wz
                if not (-6 <= lx <= b.nx * T + 6 and -6 <= lz <= b.nz * T + 6):
                    continue
                f = FOOT[g]
                rr = max(f[0], f[1], f[2])
                for (i, j) in tiles:
                    cx, cz = b.tc(i, j)
                    if abs(cx - lx) < rr + 2.1 and abs(cz - lz) < rr + 2.1:
                        occ.add((i, j))
            for (i, j) in tiles:
                if (i, j) in occ:
                    continue
                p = 0.42 if lv < b.L else 0.30
                if rng.random() < p:
                    cx, cz = b.tc(i, j)
                    x, y, z = b.wx + cx + rng.uniform(-1.0, 1.0), Y0 + b.y(lv) + 0.15, b.wz + cz + rng.uniform(-1.0, 1.0)
                    if valid_obj(x, y, z):
                        OBJETS.append((x, y, z))


def valid_obj(x, y, z):
    for (x0, y0, z0, x1, y1, z1) in _BOXES:
        if x0 - 0.3 < x < x1 + 0.3 and z0 - 0.3 < z < z1 + 0.3 and y1 > y + 0.1 and y0 < y + 0.8:
            return False
    if y <= Y0 + 0.2:
        return True
    for (x0, y0, z0, x1, y1, z1) in _BOXES:
        if x0 <= x <= x1 and z0 <= z <= z1 and abs(y1 - (y - 0.15)) < 0.12:
            return True
    return False


_BOXES = []


def around_groups():
    for (g, gx, gy, gz, yaw) in LOOT_GROUPS:
        n = 0
        tries = 0
        while n < 3 and tries < 60:
            tries += 1
            a = rng.uniform(0, math.tau)
            rr = max(FOOT[g]) + rng.uniform(0.8, 2.2)
            x, y, z = gx + math.cos(a) * rr, gy + 0.10, gz + math.sin(a) * rr
            if valid_obj(x, y, z):
                OBJETS.append((x, y, z))
                n += 1


# ============================================================================================ extérieurs : rues, parvis, décor, groupes de plein air
def outdoor():
    ex = Scn("Rues_Orion")
    top = Y0 + 0.0
    asph, beton, ligne = (0.13, 0.14, 0.17), (0.62, 0.64, 0.67), (0.85, 0.88, 0.92)
    # avenues (hauteur : le dessus est à Y0 ; épaisseur 1 m ; les coordonnées sont locales à Orion)
    def road(name, x0, z0, x1, z1, col=asph, h=1.0, top=0.0):
        ex.mesh_box(name, x0, top - h, z0, x1, top, z1, col)
        ex.col(x0, top - h, z0, x1, top, z1)
    road("Avenue_NordSud", -8, -96, 8, 96)
    road("Avenue_EstOuest", -92, -8, 92, 8)
    road("Place_centrale", -14, -14, 14, 14, beton, top=0.02)
    road("Parvis_Tour", 8, -56, 40, -28, beton, top=-0.03)
    road("Parvis_Hangar", 14, 16, 46, 44, beton, top=-0.03)
    road("Cour_Centrale", 52, 12, 92, 36, beton, top=-0.03)
    road("Cour_Serveurs", -40, -36, -8, -12, beton, top=-0.03)
    road("Aire_Securite", -66, 20, -22, 40, beton, top=-0.03)
    road("Portail_Nord", -26, -92, 26, -76, beton, top=-0.03)
    road("Portail_Sud", -26, 76, 26, 92, beton, top=-0.03)
    road("Parvis_Labos", -62, -56, -10, -36, beton, top=-0.03)
    road("Parvis_Quartiers", 40, -52, 64, -32, beton, top=-0.03)
    # lignes blanches au centre des avenues
    for z in range(-90, 90, 12):
        if abs(z) < 16:
            continue
        ex.mesh_box("Ligne_NS_%d" % z, -0.15, 0.01, z, 0.15, 0.03, z + 6, ligne, 0.6)
    for x in range(-86, 90, 12):
        if abs(x) < 16:
            continue
        ex.mesh_box("Ligne_EO_%d" % x, x, 0.01, -0.15, x + 6, 0.03, 0.15, ligne, 0.6)
    # monument central : disque + piliers lumineux
    ex.piece("Monument", "mega|Platform_Round1", 0, 0.05, 0)
    ex.col(-2.2, 0, -2.2, 2.2, 0.3, 2.2)
    for (px, pz) in ((-1.6, -1.6), (1.6, -1.6), (-1.6, 1.6), (1.6, 1.6)):
        ex.piece("Monument", "mega|Column_Hollow", px, 0.05, pz, 0, 1.0, 0.6, 1.0)
        ex.col(px - 0.6, 0, pz - 0.6, px + 0.6, 3.0, pz + 0.6)
    EXTRA.append((ex, 0, Y0, 0, 0))

    # supports sous les passerelles et blocs de couverture dans les cours : une scène de décor
    dec = Scn("Decor_Orion")
    def crate(x, z, big=False, stack=False, yaw=0):
        k = "mega|Prop_Crate4" if big else "mega|Prop_Crate3"
        sz = 1.12 if big else 1.0
        dec.piece("Caisses", k, x, sz / 2, z, yaw)
        dec.col(x - sz / 2, 0, z - sz / 2, x + sz / 2, sz, z + sz / 2)
        if stack:
            dec.piece("Caisses", "mega|Prop_Crate3", x, sz + 0.5, z, 0)
            dec.col(x - 0.5, sz, z - 0.5, x + 0.5, sz + 1.0, z + 0.5)
    def barrier(x, z, yaw):
        dec.piece("Barrieres", "mega|ShortWall_Simple1_Straight", x, 0, z, yaw)
        pts = [rot_xz(u, v, yaw) for u in (-0.15, 0.15) for v in (-2.0, 2.0)]
        dec.col(x + min(p[0] for p in pts), 0, z + min(p[1] for p in pts), x + max(p[0] for p in pts), 1.0, z + max(p[1] for p in pts))
    # cours de stockage (aux extrémités, hors des rues)
    for (bx, bz) in ((-76, 40), (-76, 56), (-28, 64), (70, -70), (30, -78), (84, 4), (-80, -40)):
        for k in range(8):
            crate(bx + (k % 4) * 1.4 + rng.uniform(-.3, .3), bz + (k // 4) * 1.4 + rng.uniform(-.3, .3), big=(k % 2 == 1), stack=(k % 3 == 0))
    # cuves de la centrale : colonnes hautes + disques
    for k in range(4):
        px = 77 + k * 3.4
        dec.piece("Cuves", "mega|Column_Large_Straight", px, 0, 20.0, 0)
        dec.col(px - 0.7, 0, 20.0 - 0.9, px + 0.7, 10.0, 20.0 + 1.1)
    EXTRA.append((dec, 0, Y0, 0, 0))

    # groupes de coffres de plein air (monde local Orion)
    for (g, x, z, yaw) in (("Coffres_Rond6", 0, 0, 0), ("Coffres_Carre4", -24, -24, 0), ("Coffres_U6", 26, -22, 0), ("Coffres_Arc5", 82, 31, 0),
                           ("Coffres_Face4", -22, 52, 0), ("Coffres_U6", 32, 62, 180), ("Coffres_Carre4", -60, 64, 0), ("Coffres_Arc5", -44, -72, 0),
                           ("Coffres_Face4", 52, -72, 0), ("Coffres_Face4", -76, -22, 0)):
        LOOT_GROUPS.append((g, x, Y0 + 0.05, z, yaw))


def make_bridges():
    # Aile Laboratoires -> Tour Orion : tube vitré au niveau 2 (y = 6 m), du bord est de l'aile (x = -12) au bord ouest de la tour (x = 16), couloir z = -46
    BRIDGES.append((bridge("Passerelle_Labos_Tour", 7, "x", "glass"), -12, Y0 + 6, -46, 0))
    # Tour Orion -> toit des Quartiers : passerelle ouverte (x 36 -> 44), z = -46
    BRIDGES.append((bridge("Passerelle_Tour_Quartiers", 2, "x", "open"), 36, Y0 + 6, -46, 0))
    # Hangar (toit) -> Centrale (toit) : x 44 -> 56, z = 26
    BRIDGES.append((bridge("Passerelle_Hangar_Centrale", 3, "x", "open"), 44, Y0 + 6, 26, 0))
    # Centre médical (toit) -> Poste de sécurité (niveau 2) : x -44 -> -36, z = 26
    BRIDGES.append((bridge("Passerelle_Medical_Securite", 2, "x", "open"), -44, Y0 + 6, 26, 0))
    # Arches des portails (niveau 2 : toit des portails, y = 6 m) : de x = -12 à x = 12
    BRIDGES.append((bridge("Arche_Portail_Nord", 6, "x", "glass"), -12, Y0 + 6, -86, 0))
    BRIDGES.append((bridge("Arche_Portail_Sud", 6, "x", "glass"), -12, Y0 + 6, 82, 0))
    # piliers sous la passerelle des Labos
    sup = Scn("Piliers_Passerelle")
    for (px, pz) in ((-7.0, -48.6), (-7.0, -43.4), (7.0, -48.6), (7.0, -43.4)):
        sup.piece("Piliers", "mega|Column_Hollow", px, 0, pz, 0, 1.0, 1.2, 1.0)
        sup.col(px - 0.6, 0, pz - 0.6, px + 0.6, 6.0, pz + 0.6)
    EXTRA.append((sup, 0, Y0, 0, 0))



def world_boxes():
    """toutes les boîtes de collision en coordonnées de zone (centre d'Orion = origine, y monde)"""
    out = []
    for b in B.values():
        for (x0, y0, z0, x1, y1, z1) in b.s.boxes:
            out.append((b.wx + x0, Y0 + y0, b.wz + z0, b.wx + x1, Y0 + y1, b.wz + z1))
    for (s_, x, y, z, r) in BRIDGES + EXTRA:
        for (x0, y0, z0, x1, y1, z1) in s_.boxes:
            out.append((x + x0, y + y0, z + z0, x + x1, y + y1, z + z1))
    return out


def validate(quiet=False, only=None):
    boxes = world_boxes()
    bad = 0
    def check(x, y, z, r, label):
        nonlocal bad
        ground = False
        for (x0, y0, z0, x1, y1, z1) in boxes:
            if x0 - r < x < x1 + r and z0 - r < z < z1 + r:
                if abs(y1 - (y - 0.05)) < 0.2 or abs(y1 - (y - 0.15)) < 0.2:
                    if x0 <= x <= x1 and z0 <= z <= z1:
                        ground = True
                if y1 > y + 0.15 and y0 < y + 0.8 and x0 - r < x < x1 + r and z0 - r < z < z1 + r:
                    if not quiet: print("  dans un mur/obstacle :", label, round(x, 1), round(z, 1), round(y, 1))
                    bad += 1
                    return
        if y <= Y0 + 0.2:
            ground = True              # relief ou voie : le sol est le terrain
        if not ground:
            if not quiet: print("  dans le vide :", label, round(x, 1), round(z, 1), round(y, 1))
            bad += 1
    if only is not None:
        x, y, z = only
        check(x, y, z, 0.25, "objet")
        return bad == 0
    for (g, gx, gy, gz, yaw) in LOOT_GROUPS:
        for (cx, cz, cyaw) in GROUPS[g]:
            dx, dz = rot_xz(cx, cz, yaw)
            check(gx + dx, gy, gz + dz, 0.55, g)
    for (x, y, z) in OBJETS:
        check(x, y, z, 0.25, "objet")
    print("contrôle du butin : %d problème(s)" % bad)
    return bad

# ============================================================================================ écriture de la scène principale
def parse_blocks(text):
    lines = text.split("\n")
    blocks, cur = [], []
    for ln in lines:
        if ln.startswith("[") and (ln.startswith("[node") or ln.startswith("[ext_resource") or ln.startswith("[sub_resource") or ln.startswith("[gd_scene") or ln.startswith("[connection") or ln.startswith("[resource")):
            if cur:
                blocks.append(cur)
            cur = [ln]
        else:
            cur.append(ln)
    if cur:
        blocks.append(cur)
    return blocks


def in_rect(x, z):
    return RECT[0] <= x <= RECT[2] and RECT[1] <= z <= RECT[3]


def block_pos(b):
    for ln in b:
        if ln.startswith("transform = Transform3D("):
            v = [float(t) for t in ln[len("transform = Transform3D("):-1].split(",")]
            return v[9], v[10], v[11]
    return None


def edit_main():
    text = open(MAIN, encoding="utf-8").read()
    blocks = parse_blocks(text)
    removed_orion = removed_col = removed_loot = 0
    drop_subs = set()
    kept = []
    for b in blocks:
        h = b[0]
        if 'parent="Zones/A_Orion"' in h:
            removed_orion += 1
            continue
        if 'parent="BuildingColliders"' in h:
            p = block_pos(b)
            if p and in_rect(p[0], p[2]):
                for ln in b:
                    m = re.match(r'shape = SubResource\("([^"]+)"\)', ln)
                    if m:
                        drop_subs.add(m.group(1))
                removed_col += 1
                continue
        if h.startswith("[node") and ('parent="Loot/Coffres"' in h or 'parent="Loot/Objets"' in h):
            p = block_pos(b)
            if p and in_rect(p[0], p[2]):
                removed_loot += 1
                continue
        kept.append(b)
    # sous-ressources de collision devenues orphelines (uniquement celles des boîtes retirées)
    body = "\n".join("\n".join(b) for b in kept)
    final = []
    for b in kept:
        if b[0].startswith("[sub_resource"):
            m = re.search(r'id="([^"]+)"', b[0])
            if m and m.group(1) in drop_subs and ('SubResource("%s")' % m.group(1)) not in body:
                continue
        final.append(b)
    kept = final
    # ressources externes inutilisées (kit Starter d'Orion)
    body = "\n".join("\n".join(b) for b in kept)
    final = []
    for b in kept:
        if b[0].startswith("[ext_resource"):
            m = re.search(r'id="([^"]+)"', b[0])
            if m and ('ExtResource("%s")' % m.group(1)) not in body and "Starter" in b[0]:
                continue
        final.append(b)
    kept = final

    # nouvelles ressources
    paths = {}
    def rid(path):
        if path not in paths:
            paths[path] = "orion_%d" % (len(paths) + 1)
        return paths[path]
    zone_nodes = []
    all_scenes = [(s_, x, y, z, r) for (s_, x, y, z, r) in BRIDGES] + [(s_, x, y, z, r) for (s_, x, y, z, r) in EXTRA]
    for nm, b in B.items():
        zone_nodes.append((nm, "res://scenes/maps/orion/%s.tscn" % nm, CX + b.wx, Y0, CZ + b.wz, 0))
    for (s_, x, y, z, r) in all_scenes:
        zone_nodes.append((s_.name, "res://scenes/maps/orion/%s.tscn" % s_.name, CX + x, y, CZ + z, r))
    ext_lines = []
    for (nm, path, x, y, z, r) in zone_nodes:
        rid(path)
    for (g, gx, gy, gz, yaw) in LOOT_GROUPS:
        rid("res://scenes/loot/groupes/%s.tscn" % g)
    for p, i in paths.items():
        ext_lines.append(['[ext_resource type="PackedScene" path="%s" id="%s"]' % (p, i)])
    last_ext = max(i for i, b in enumerate(kept) if b[0].startswith("[ext_resource"))
    kept[last_ext + 1:last_ext + 1] = ext_lines

    # nœuds de zone
    zl = []
    for (nm, path, x, y, z, r) in zone_nodes:
        zl.append(['[node name="%s" parent="Zones/A_Orion" instance=ExtResource("%s")]' % (nm, paths[path]), "transform = %s" % tf(x, y, z, r), ""])
    ia = next(i for i, b in enumerate(kept) if b[0].startswith('[node name="A_Orion" type="Node3D" parent="Zones"'))
    # fin du bloc A_Orion (il peut être suivi de lignes vides intégrées)
    kept[ia + 1:ia + 1] = zl

    # butin : groupes de coffres -> Loot/Coffres ; objets -> Loot/Objets
    def last_index(prefix):
        idx = None
        for i, b in enumerate(kept):
            if b[0].startswith("[node") and prefix in b[0]:
                idx = i
        return idx
    ic = last_index('parent="Loot/Coffres"')
    if ic is None:
        ic = next(i for i, b in enumerate(kept) if b[0].startswith('[node name="Coffres"'))
    existing = set()
    for b in kept:
        m = re.match(r'\[node name="([^"]+)" parent="Loot/Coffres"', b[0])
        if m:
            existing.add(m.group(1))
    cl = []
    cnt = {}
    for (g, gx, gy, gz, yaw) in LOOT_GROUPS:
        cnt[g] = cnt.get(g, 0) + 1
        nm = "Orion_%s_%02d" % (g.replace("Coffres_", ""), cnt[g])
        cl.append(['[node name="%s" parent="Loot/Coffres" instance=ExtResource("%s")]' % (nm, paths["res://scenes/loot/groupes/%s.tscn" % g]), "transform = %s" % tf(CX + gx, gy, CZ + gz, yaw), ""])
    kept[ic + 1:ic + 1] = cl
    io = last_index('parent="Loot/Objets"')
    if io is None:
        io = next(i for i, b in enumerate(kept) if b[0].startswith('[node name="Objets"'))
    # prefab des objets déjà déclaré dans la scène (id loot_item_prefab)
    ol = []
    for k, (x, y, z) in enumerate(OBJETS):
        ol.append(['[node name="Orion_Objet_%03d" parent="Loot/Objets" instance=ExtResource("loot_item_prefab")]' % (k + 1), "transform = %s" % tf(CX + x, y, CZ + z), ""])
    kept[io + 1:io + 1] = ol

    out = "\n".join("\n".join(b) for b in kept)
    open(MAIN, "w", encoding="utf-8", newline="\n").write(out)
    print("scène principale : -%d pièces Orion, -%d boîtes, -%d points de butin ; +%d scènes, +%d groupes de coffres, +%d objets"
          % (removed_orion, removed_col, removed_loot, len(zone_nodes), len(LOOT_GROUPS), len(OBJETS)))


def edit_final():
    d = json.load(open(FINAL, encoding="utf-8"))
    n0 = len(d["boxes"])
    d["boxes"] = [b for b in d["boxes"] if not in_rect((b[0] + b[3]) / 2, (b[2] + b[5]) / 2)]
    json.dump(d, open(FINAL, "w", encoding="utf-8"), separators=(",", ":"))
    print("buildings_final.json : %d -> %d boîtes" % (n0, len(d["boxes"])))


def main():
    if os.path.isdir(OUT) and not os.environ.get("ORION_FORCE"):
        sys.exit("scenes/maps/orion existe déjà : le script est à usage unique (éditez les scènes dans Godot).")
    os.makedirs(BACKUP, exist_ok=True)
    for f in (MAIN, FINAL):
        shutil.copy2(f, os.path.join(BACKUP, os.path.basename(f)))
    build_all()
    make_bridges()
    outdoor()
    _BOXES.extend(world_boxes())
    around_groups()
    objets_auto()
    validate()
    write_group_scenes()
    nb = 0
    for nm, b in B.items():
        b.s.save(OUT)
        nb += len(b.s.boxes)
    for (s_, *_r) in BRIDGES + EXTRA:
        s_.save(OUT)
        nb += len(s_.boxes)
    print("bâtiments : %d, boîtes de collision : %d" % (len(B), nb))
    edit_main()
    edit_final()
    nch = sum(len(GROUPS[g[0]]) for g in LOOT_GROUPS)
    print("coffres : %d (%d groupes), objets au sol : %d" % (nch, len(LOOT_GROUPS), len(OBJETS)))


if __name__ == "__main__":
    main()
