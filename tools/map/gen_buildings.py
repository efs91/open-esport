"""Génère les bâtiments des 3 zones de Frontière 01 à partir des pièces modulaires des kits.
Sortie : maps/frontiere01/data/buildings.json
  pieces : [{"k": "kit|nom", "p": [x,y,z], "r": degrés (multiple de 90)}]    (positions du monde)
  boxes  : [[minx,miny,minz,maxx,maxy,maxz]]                                 (collisions pour le serveur et le client)
  loot   : [[x,y,z]]                                                          (emplacements de butin à l'intérieur)
  spawns : [[x,y,z]]
Repère : x vers l'est, z vers le sud (nord = -z), y vers le haut.
"""
import os, json, math, random
import numpy as np
from layout import *

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "maps", "frontiere01", "data")
KITS = json.load(open(os.path.join(os.path.dirname(__file__), "kits.json"), encoding="utf-8"))
masks = np.load(os.path.join(DATA, "masks.npz"))
H = masks["H"].astype(np.float32)
rng = random.Random(77)

NOCOL = ("Light", "Decal", "Num_", "Sign_", "Word_", "Symb", "Intercom", "Air_Grid", "Grid_", "Pipes", "Cable", "Vent", "Clamp", "Rail",
         "TopAstra", "TopCables", "TopPlastic", "TopSimple", "TopWindow", "Cap_", "Fence_Turn_90_Short", "AccessPoint", "Fan_Small", "ItemHolder")


def ground(x, z):
    return float(H[int(round(z)) + HALF, int(round(x)) + HALF])


class World:
    def __init__(self):
        self.pieces, self.boxes, self.loot, self.spawns = [], [], [], []

    # ------------------------------------------------------------------ pièces
    def rot_xz(self, dx, dz, r):
        a = math.radians(r)
        c, s = round(math.cos(a)), round(math.sin(a))
        return dx * c + dz * s, -dx * s + dz * c          # même convention que Basis(UP, angle) de Godot

    def add(self, key, x, y, z, r=0, col=True, minthick=0.3, flat_slab=0.3):
        """pose une pièce ; sa boîte de collision est calculée par Godot (buildings.gd) d'après la vraie géométrie"""
        k = KITS.get(key)
        if k is None:
            print("pièce inconnue :", key)
            return
        collide = bool(col) and not any(t in key.split("|")[1] for t in NOCOL)
        self.pieces.append({"k": key, "p": [round(x, 3), round(y, 3), round(z, 3)], "r": int(r) % 360, "c": 1 if collide else 0, "mt": minthick, "fs": flat_slab})

    def box(self, x0, y0, z0, x1, y1, z1):
        self.boxes.append([round(v, 3) for v in (x0, y0, z0, x1, y1, z1)])

    def stairs(self, x, y, z, r, run, rise, width, steps, key=None, rel_z0=0.0):
        """marches de collision d'un escalier : on monte vers -X local (avant rotation), la plus basse en +X. (x,z) = coin local (0,0)."""
        dx = run / steps
        for i in range(steps):
            # marche i : x local de (run - (i+1)*dx) à (run - i*dx), hauteur (i+1)*rise/steps
            a, b = run - (i + 1) * dx, run - i * dx
            pts = [self.rot_xz(u, v, r) for u in (a, b) for v in (rel_z0, rel_z0 + width)]
            self.box(min(p[0] for p in pts) + x, y, min(p[1] for p in pts) + z, max(p[0] for p in pts) + x, y + (i + 1) * rise / steps, max(p[1] for p in pts) + z)

    def loot_at(self, x, y, z):
        self.loot.append([round(x, 2), round(y + 0.15, 2), round(z, 2)])


W = World()


def zone_origin(k):
    return ZONES[k]["c"]


# =============================================================================================================== ZONE A : starter kit (tuiles de 6 m, murs de 2 m d'épaisseur)
T = 6.0
SW = ["starter|Wall_Simple_01_Long", "starter|Wall_Simple_02_Long", "starter|Wall_Simple_03_Long"]
SWH = "starter|Wall_Simple_01_Half"


def a_edge(w, X0, Z0, edge, y, door=False, variant=0):
    """mur sur une arête de la tuile (X0,Z0) = coin nord-ouest ; porte = mur de 4 m + ouverture de 2 m"""
    key = SWH if door else SW[variant % len(SW)]
    if edge == "W":
        w.add(key, X0, y, Z0, 0)
    elif edge == "E":
        w.add(key, X0 + T, y, Z0 + T, 180)
    elif edge == "N":
        w.add(key, X0 + T, y, Z0, -90)
    elif edge == "S":
        w.add(key, X0, y, Z0 + T, 90)


def a_building(w, gx, gz, nx, nz, levels=1, doors=(), partitions=(), stairs_at=None, props=True, seed=0):
    """bâtiment de nx*nz tuiles dont le coin nord-ouest est (gx,gz) (monde). doors : liste de (arête, indice de tuile le long de l'arête)."""
    r = random.Random(seed)
    gy = round(ground(gx + nx * T / 2, gz + nz * T / 2), 3)
    LH = 4.5                                                  # hauteur d'un niveau (dalle comprise)
    for lv in range(levels):
        y = gy + lv * LH
        for i in range(nx):
            for j in range(nz):
                X0, Z0 = gx + i * T, gz + j * T
                if lv == 0:
                    w.add("starter|Floor_Squared_01_6x6", X0, y, Z0, 0, flat_slab=0.4)
                else:
                    pass
                # arêtes extérieures
                for edge, on in (("W", i == 0), ("E", i == nx - 1), ("N", j == 0), ("S", j == nz - 1)):
                    if not on:
                        continue
                    idx = j if edge in "WE" else i
                    door = (lv == 0 and (edge, idx) in doors)
                    a_edge(w, X0, Z0, edge, y, door, variant=r.randint(0, 2))
        # cloisons intérieures : (axe, position en tuiles, début, fin)
        for part in partitions:
            lvl, axis, pos, a, b = part
            if lvl != lv:
                continue
            for t in range(a, b):
                if axis == "x":      # cloison parallèle à Z, sur l'arête ouest de la colonne `pos`
                    X0, Z0 = gx + pos * T, gz + t * T
                    w.add(SWH if (t - a) % 2 == 0 else SW[0], X0, y, Z0, 0)
                else:
                    X0, Z0 = gx + t * T, gz + pos * T
                    w.add(SWH if (t - a) % 2 == 0 else SW[0], X0 + T, y, Z0, -90)
        # plafond / dalle de l'étage suivant (sauf au-dessus de l'escalier)
        top = y + 4.0
        for i in range(nx):
            for j in range(nz):
                if stairs_at and lv < levels - 1 and (i, j) in stairs_at.get("hole", ()):
                    continue
                w.add("starter|Roof_Squared_01_6x6", gx + i * T, top, gz + j * T, 0)
        # butin au centre de chaque tuile
        for i in range(nx):
            for j in range(nz):
                if r.random() < 0.55:
                    w.loot_at(gx + i * T + T / 2 + r.uniform(-1.2, 1.2), y + 0.0, gz + j * T + T / 2 + r.uniform(-1.2, 1.2))
    # escalier : la volée (8,25 m) monte vers -X ; son pied est à l'est, elle recouvre la tuile (i,j) et la suivante
    if stairs_at and levels > 1:
        i, j, rot = stairs_at["tile"]
        for lv in range(levels - 1):
            y0 = gy + lv * LH
            X0, Z0 = gx + i * T, gz + j * T
            ox, oz = X0 + 2.25, Z0
            w.add("starter|Stairway_Step_01", ox, y0 + 0.44, oz, 0, col=False)
            n = 11
            for s_ in range(n):
                xa = X0 + 8.25 - (s_ + 1) * 8.25 / n
                xb = X0 + 8.25 - s_ * 8.25 / n
                w.box(xa, y0, oz, xb, y0 + (s_ + 1) * LH / n, oz + 6.0)
    # accessoires
    if props:
        for _ in range(max(2, nx * nz)):
            i, j = r.randrange(nx), r.randrange(nz)
            px, pz = gx + i * T + r.uniform(2.6, 3.4), gz + j * T + r.uniform(2.6, 3.4)
            w.add("starter|Crate_01", px, gy, pz, r.choice((0, 90)))
    return gy


def zone_A():
    cx, cz = zone_origin("A")
    # A1 Laboratoire central : 4x4, 2 niveaux, escalier dans la tuile (3,0) ; portes sur les quatre faces
    a_building(W, cx - 12, cz - 12, 4, 4, 2, doors={("S", 1), ("N", 2), ("W", 1), ("E", 2)}, partitions=[(0, "x", 2, 0, 4), (1, "y", 2, 0, 4)],
               stairs_at={"tile": (2, 1, 0), "hole": [(2, 1), (3, 1)]}, seed=1)
    # A2 Centre médical : 3x2
    a_building(W, cx - 90, cz - 55, 3, 2, 1, doors={("E", 0), ("S", 0)}, partitions=[(0, "x", 1, 0, 2)], seed=2)
    # A3 Entrepôt de stockage : 4x3 (grand volume)
    a_building(W, cx + 45, cz - 70, 4, 3, 1, doors={("W", 1), ("S", 1), ("S", 3)}, seed=3)
    # A4 Générateur / énergie : 2x2 + cuves
    a_building(W, cx + 50, cz + 45, 2, 2, 1, doors={("N", 0), ("W", 1)}, seed=4)
    for k in range(4):
        W.add("starter|Column_01_Big", cx + 82 + k * 3.2, ground(cx + 82, cz + 48), cz + 48, 90)
    # A5 Poste de sécurité surélevé : 3x2 tuiles sur deux niveaux, escalier dans la première tuile
    a_building(W, cx - 90, cz + 35, 3, 2, 2, doors={("E", 0), ("N", 2)}, stairs_at={"tile": (0, 0, 0), "hole": [(0, 0), (1, 0)]}, seed=5)
    # petits abris et barricades autour
    for k in range(10):
        a = rng.uniform(0, math.tau)
        rr = rng.uniform(70, 110)
        x, z = cx + math.cos(a) * rr, cz + math.sin(a) * rr
        W.add("starter|Fence_Long_01", x, ground(x, z), z, rng.choice((0, 90)))
        W.loot_at(x + 2, ground(x, z), z + 2)


# =============================================================================================================== ZONE B : MegaKit (tuiles de 4 m, niveaux de 3 m)
M = 4.0
LHB = 3.0
BW = ["mega|WallAstra_Straight", "mega|WallBand_Straight", "mega|WallWindow_Straight", "mega|WallAstra_Straight_Divided"]


def b_edge(w, cx, cz, edge, y, key, gap=False):
    """mur sur une arête de la tuile centrée en (cx,cz) ; gap = ouverture (porte de 4 m)"""
    if gap:
        return
    if edge == "W":
        w.add(key, cx, y, cz, 0)
    elif edge == "E":
        w.add(key, cx, y, cz, 180)
    elif edge == "N":
        w.add(key, cx, y, cz, -90)
    elif edge == "S":
        w.add(key, cx, y, cz, 90)


def b_building(w, gx, gz, nx, nz, levels=1, doors=(), styles=None, stairs=None, seed=0, columns=True, open_sides=()):
    """bâtiment de nx*nz tuiles de 4 m dont le coin nord-ouest est (gx,gz) ; stairs = (tuile i, tuile j, rot) pour escalier 4WideTall"""
    r = random.Random(seed)
    gy = round(ground(gx + nx * M / 2, gz + nz * M / 2), 3)
    styles = styles or BW
    for lv in range(levels):
        y = gy + lv * LHB
        for i in range(nx):
            for j in range(nz):
                cx, cz = gx + i * M + M / 2, gz + j * M + M / 2
                hole = stairs and lv > 0 and (i, j) in stairs.get("hole", ())
                if lv == 0 or not hole:
                    w.add("mega|Platform_Metal" if (i + j) % 2 == 0 else "mega|Platform_Metal2", cx, y, cz, 0)
                for edge, on in (("W", i == 0), ("E", i == nx - 1), ("N", j == 0), ("S", j == nz - 1)):
                    if not on or edge in open_sides:
                        continue
                    idx = j if edge in "WE" else i
                    door = lv == 0 and (edge, idx) in doors
                    b_edge(w, cx, cz, edge, y, r.choice(styles), gap=door)
                if r.random() < 0.5 and lv == 0:
                    w.loot_at(cx + r.uniform(-1, 1), y, cz + r.uniform(-1, 1))
        if columns:
            for (ux, uz) in ((gx, gz), (gx + nx * M, gz), (gx, gz + nz * M), (gx + nx * M, gz + nz * M)):
                w.add("mega|Column_Round", ux, y, uz, 0)
    # toit
    ty = gy + levels * LHB
    for i in range(nx):
        for j in range(nz):
            w.add("mega|Platform_Simple2", gx + i * M + M / 2, ty, gz + j * M + M / 2, 0)
    # escaliers : montée vers -Z local (origine au haut de la volée), 6 m de long, 4 m de large
    if stairs:
        i, j, rot = stairs["tile"]
        for lv in range(levels - 1 if levels > 1 else 0):
            y = gy + lv * LHB
            cx, cz = gx + i * M + M / 2, gz + j * M + M / 2
            # le haut de la volée (z local 0) est à la tuile (i,j) ; la volée descend vers +Z local
            ox, oz = cx, cz - 3.0
            w.add("mega|Platform_Stairs_4WideTall", ox, y, oz, rot, col=False)
            n = 12
            for s in range(n):
                a, b = s * 6.0 / n, (s + 1) * 6.0 / n          # distance depuis le haut (z local)
                pts = [w.rot_xz(u, v, rot) for u in (-2.0, 2.0) for v in (a, b)]
                w.box(min(p[0] for p in pts) + ox, y, min(p[1] for p in pts) + oz, max(p[0] for p in pts) + ox, y + (n - s) * LHB / n, max(p[1] for p in pts) + oz)
    return gy


def zone_B():
    cx, cz = zone_origin("B")
    # B1 Hangar principal : 6x4 tuiles (24 x 16), face sud ouverte, grandes portes
    b_building(W, cx - 70, cz - 20, 6, 4, 1, doors={("N", 2), ("N", 3), ("S", 2), ("S", 3), ("W", 1), ("E", 2)}, seed=11, columns=True)
    # B2 Tour de contrôle : 3x3 tuiles, 3 niveaux, escalier
    b_building(W, cx + 22, cz - 30, 3, 3, 3, doors={("S", 1), ("W", 0)}, stairs={"tile": (1, 1, 0), "hole": [(1, 0), (1, 1)]}, seed=12)
    # B3 Atelier de maintenance : 4x3
    b_building(W, cx + 30, cz + 20, 4, 3, 1, doors={("N", 1), ("W", 1)}, seed=13)
    # B4 Centre énergétique : 3x3 + grosses colonnes
    b_building(W, cx - 80, cz + 30, 3, 3, 1, doors={("E", 1), ("N", 1)}, seed=14, columns=False)
    for k in range(4):
        W.add("mega|Column_Large_Straight", cx - 56 + k * 5, ground(cx - 56, cz + 40), cz + 44, 0)
    # B5 Stockage / conteneurs : amas de caisses et murets
    for k in range(34):
        a = rng.uniform(0, math.tau)
        rr = rng.uniform(5, 38)
        x, z = cx - 20 + math.cos(a) * rr, cz + 60 + math.sin(a) * rr * 0.5
        gy = ground(x, z)
        W.add("mega|Prop_Crate4" if k % 2 else "mega|Prop_Crate3", x, gy + 0.55, z, rng.choice((0, 90)))
        if k % 3 == 0:
            W.add("mega|Prop_Crate3", x, gy + 1.6, z, 0)
            W.box(x - 0.55, gy, z - 0.55, x + 0.55, gy + 2.1, z + 0.55)
        if k % 5 == 0:
            W.loot_at(x + 1.2, gy, z + 1.2)
    for k in range(10):
        x = cx - 55 + k * 8
        z = cz + 90
        W.add("mega|ShortWall_Simple1_Straight", x, ground(x, z), z, 90, minthick=0.4)


# =============================================================================================================== ZONE C : avant-poste (MegaKit) dans le bassin Nama
def zone_C():
    cx, cz = zone_origin("C")
    # C1 Station d'extraction : plate-forme à étage avec colonnes
    gx, gz = cx - 55, cz - 30
    b_building(W, gx, gz, 3, 3, 2, doors={("S", 1), ("N", 1), ("W", 1), ("E", 1)}, stairs={"tile": (1, 1, 0), "hole": [(1, 0), (1, 1)]}, seed=21, open_sides=("E",))
    for k in range(4):
        W.add("mega|Column_Large_Straight", gx + 20 + (k % 2) * 8, ground(gx + 20, gz), gz + 2 + (k // 2) * 8, 0)
    # C2 Garage / hangar : grand abri ouvert (toit sur colonnes)
    gx, gz = cx + 20, cz - 40
    gy = round(ground(gx + 12, gz + 8), 3)
    for i in range(6):
        for j in range(4):
            W.add("mega|Platform_Simple2", gx + i * M + M / 2, gy + 4.9, gz + j * M + M / 2, 0)
    for (ux, uz) in ((gx, gz), (gx + 24, gz), (gx, gz + 16), (gx + 24, gz + 16), (gx + 12, gz), (gx + 12, gz + 16)):
        W.add("mega|Column_Round", ux, gy, uz, 0)
    for i in range(6):
        W.add("mega|Platform_Metal", gx + i * M + M / 2, gy, gz + M * 2, 0)
    W.add("mega|Prop_Crate4", gx + 6, gy + 0.55, gz + 6, 0)
    W.loot_at(gx + 8, gy, gz + 8)
    # C3 Baraquements : trois petites cabines
    for k in range(3):
        b_building(W, cx - 85 + k * 16, cz + 40, 2, 2, 1, doors={("N", 0)} if k != 1 else {("S", 1)}, seed=30 + k, columns=False)
    # C4 Station de pompage : petit bâtiment + cuves
    b_building(W, cx + 45, cz + 25, 2, 2, 1, doors={("W", 0), ("S", 1)}, seed=34, columns=True)
    for k in range(3):
        x, z = cx + 62 + k * 4, cz + 32
        W.add("mega|Column_Pipes", x, ground(x, z), z, 0)
        W.add("mega|Prop_Barrel_Large", x + 1.5, ground(x, z), z + 2, 0)
    # C5 Antenne / communication : grand mât et plate-forme en hauteur
    x, z = cx + 85, cz - 55
    gy = ground(x, z)
    for k in range(4):
        W.add("mega|Column_Large_Straight", x + (k % 2) * 6, gy, z + (k // 2) * 6, 0)
    W.add("mega|Platform_Metal", x + 3, gy + 10.0, z + 3, 0)
    W.add("mega|Platform_Metal", x + 3, gy + 10.0, z + 7, 0)
    W.loot_at(x + 3, gy, z + 3)
    for k in range(12):
        a = rng.uniform(0, math.tau)
        rr = rng.uniform(40, 120)
        px, pz = cx + math.cos(a) * rr, cz + math.sin(a) * rr
        W.add("mega|ShortWall_Simple1_Straight", px, ground(px, pz), pz, rng.choice((0, 90)), minthick=0.4)


zone_A()
zone_B()
zone_C()

# points d'apparition : en bordure des zones (hors bâtiments) et dans le vallon central
spawns = []
for k, z in ZONES.items():
    for i in range(10):
        a = math.tau * i / 10 + rng.uniform(-0.15, 0.15)
        rr = z["r"] * rng.uniform(0.8, 1.15)
        x, zz = z["c"][0] + math.cos(a) * rr, z["c"][1] + math.sin(a) * rr
        spawns.append([round(x, 2), round(ground(x, zz), 2), round(zz, 2)])
for i in range(20):
    x, zz = rng.uniform(-90, 90), rng.uniform(-40, 130)
    spawns.append([round(x, 2), round(ground(x, zz), 2), round(zz, 2)])

json.dump({"pieces": W.pieces, "boxes": W.boxes, "loot": W.loot, "spawns": spawns}, open(os.path.join(DATA, "buildings.json"), "w"))
print("pièces : %d, boîtes : %d, butin : %d, apparitions : %d" % (len(W.pieces), len(W.boxes), len(W.loot), len(spawns)))
