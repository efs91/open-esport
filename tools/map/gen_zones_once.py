"""Zones B « Terminal Delta » et C « Bassin Nama » : premier jet UNIQUE des bâtiments, passerelles, voies et butin (comme Orion).

⚠ À NE LANCER QU'UNE FOIS. Écrit des scènes Godot ordinaires (scenes/maps/delta/, scenes/maps/nama/) puis remplace UNIQUEMENT le contenu de Delta et de
Nama dans scenes/maps/Frontiere01.tscn (nœuds de Zones/B_Delta et Zones/C_Nama, boîtes de BuildingColliders et points de Loot dans leur cercle).
Ensuite les scènes sont la référence et s'éditent dans Godot. Refuse de tourner si scenes/maps/delta/ existe.
Réutilise gen_orion_once.py (bâtiments, passerelles, groupes de coffres). Option : ZONES_DRY=1 = contrôles seulement, rien n'est écrit.

DELTA : terminal de fret et de transit, plateau plat à 84,10 m. Halle à atrium, tour de contrôle, deux hangars, quai couvert, bureaux, cour de conteneurs.
NAMA  : site d'extraction ouvert dans une cuvette (butte plate centrale à ~52 m, anneau creux à ~43 m) : station et derrick sur la butte,
        quatre postes sur l'anneau reliés à la butte par de longues passerelles sur pylônes, silos, abris, camp.
"""
import os, sys, json, math, re, random, shutil
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_orion_once as o
from gen_orion_once import Scn, Bld, bridge, facade_rule, rot_xz, tf, fmt, GROUPS, FOOT, SIDE_ROT

ROOT = o.ROOT
DRY = bool(os.environ.get("ZONES_DRY"))
MAIN, FINAL = o.MAIN, o.FINAL
BACKUP = os.environ.get("ORION_BACKUP", os.path.join(ROOT, "logs", "zones_backup"))
HM = np.fromfile(os.path.join(ROOT, "maps", "frontiere01", "data", "height.r32"), dtype="<f4").reshape(1536, 1536)
rng = random.Random(4242)


# ============================================================================================ relief
def terr(x, z):
    fx, fz = x + 768, z + 768
    i, j = int(math.floor(fx)), int(math.floor(fz))
    ax, az = fx - i, fz - j
    return float((HM[j, i] * (1 - ax) + HM[j, i + 1] * ax) * (1 - az) + (HM[j + 1, i] * (1 - ax) + HM[j + 1, i + 1] * ax) * az)


def terr_rect(x0, z0, x1, z1, step=1.0):
    vals = [terr(x, z) for x in np.arange(x0, x1 + 0.01, step) for z in np.arange(z0, z1 + 0.01, step)]
    return min(vals), max(vals)


# ============================================================================================ contexte d'une zone
class Zone:
    def __init__(self, key, parent, folder, prefix, cx, cz, y0, radius):
        self.key, self.parent, self.folder, self.prefix = key, parent, folder, prefix
        self.cx, self.cz, self.y0, self.radius = cx, cz, y0, radius
        self.pads = []              # (x0, z0, x1, z1, top) locaux, en y monde : sert au calcul du sol
        self.extra_nodes = []       # (Scn, x, y, z, yaw)
        self.cases = []


Z = None


def begin(zone):
    global Z
    Z = zone
    o.CX, o.CZ, o.Y0 = zone.cx, zone.cz, zone.y0
    o.OUT = os.path.join(ROOT, "scenes", "maps", zone.folder)
    o.B.clear(); o.LOOT_GROUPS.clear(); o.OBJETS.clear(); o.BRIDGES.clear(); o.EXTRA.clear(); o._BOXES.clear()


def ground(x, z):
    t = terr(Z.cx + x, Z.cz + z)
    for (x0, z0, x1, z1, top) in Z.pads:
        if x0 <= x <= x1 and z0 <= z <= z1:
            t = max(t, top)
    return t


def add_bld(b, base):
    b.base = base
    o.B[b.name] = b.build()
    # contrôle du sol : le relief ne doit pas dépasser la dalle ni la laisser trop haut
    lo, hi = terr_rect(Z.cx + b.wx, Z.cz + b.wz, Z.cx + b.wx + b.nx * 4, Z.cz + b.wz + b.nz * 4, 2.0)
    if hi > base + 0.02 or lo < base - 0.55:
        print("  !! relief sous %s : %.2f à %.2f pour un sol à %.2f" % (b.name, lo, hi, base))
    return b


# ---- remplacements des fonctions de gen_orion_once qui supposent un sol unique (Y0) ----------------------------------------
def place(b, group, lv, bx, bz, yaw=0):
    o.LOOT_GROUPS.append((group, b.wx + bx, b.base + b.y(lv) + 0.01, b.wz + bz, yaw))


def world_boxes():
    out = []
    for b in o.B.values():
        for (x0, y0, z0, x1, y1, z1) in b.s.boxes:
            out.append((b.wx + x0, b.base + y0, b.wz + z0, b.wx + x1, b.base + y1, b.wz + z1))
    for (s_, x, y, z, r) in o.BRIDGES + o.EXTRA:
        for (x0, y0, z0, x1, y1, z1) in s_.boxes:
            out.append((x + x0, y + y0, z + z0, x + x1, y + y1, z + z1))
    for (c, x, y, z) in getattr(Z, "conteneurs", []):
        for (x0, y0, z0, x1, y1, z1) in c.s.boxes:
            out.append((x + x0, y + y0, z + z0, x + x1, y + y1, z + z1))
    return out


def valid_obj(x, y, z, r=0.3):
    for (x0, y0, z0, x1, y1, z1) in o._BOXES:
        if x0 - r < x < x1 + r and z0 - r < z < z1 + r and y1 > y + 0.1 and y0 < y + 0.8:
            return False
    if y <= ground(x, z) + 0.3:
        return True
    for (x0, y0, z0, x1, y1, z1) in o._BOXES:
        if x0 <= x <= x1 and z0 <= z <= z1 and abs(y1 - (y - 0.15)) < 0.12:
            return True
    return False


def objets_auto():
    for b in o.B.values():
        for lv in range(0, b.L + 1):
            tiles = b.free_tiles(lv)
            occ = set()
            for (g, gx, gy, gz, yaw) in o.LOOT_GROUPS:
                if abs(gy - (b.base + b.y(lv) + 0.01)) > 0.01:
                    continue
                lx, lz = gx - b.wx, gz - b.wz
                if not (-6 <= lx <= b.nx * 4 + 6 and -6 <= lz <= b.nz * 4 + 6):
                    continue
                rr = max(FOOT[g])
                for (i, j) in tiles:
                    cx, cz = b.tc(i, j)
                    if abs(cx - lx) < rr + 2.1 and abs(cz - lz) < rr + 2.1:
                        occ.add((i, j))
            for (i, j) in tiles:
                if (i, j) in occ:
                    continue
                if rng.random() < (0.42 if lv < b.L else 0.30):
                    cx, cz = b.tc(i, j)
                    x, y, z = b.wx + cx + rng.uniform(-1.0, 1.0), b.base + b.y(lv) + 0.15, b.wz + cz + rng.uniform(-1.0, 1.0)
                    if valid_obj(x, y, z):
                        o.OBJETS.append((x, y, z))


def around_groups():
    for (g, gx, gy, gz, yaw) in o.LOOT_GROUPS:
        n, tries = 0, 0
        while n < 3 and tries < 60:
            tries += 1
            a = rng.uniform(0, math.tau)
            rr = max(FOOT[g]) + rng.uniform(0.8, 2.2)
            x, z = gx + math.cos(a) * rr, gz + math.sin(a) * rr
            y = gy + 0.14 if gy > ground(gx, gz) + 0.4 else ground(x, z) + 0.15      # étage : sur la dalle ; dehors : sur le sol
            if valid_obj(x, y, z):
                o.OBJETS.append((x, y, z))
                n += 1


def validate():
    boxes = o._BOXES
    bad = 0
    def check(x, y, z, r, label):
        nonlocal bad
        for (x0, y0, z0, x1, y1, z1) in boxes:
            if x0 - r < x < x1 + r and z0 - r < z < z1 + r and y1 > y + 0.15 and y0 < y + 0.8:
                print("  dans un mur/obstacle :", label, round(x, 1), round(z, 1), round(y, 1)); bad += 1; return
        if y <= ground(x, z) + 0.3:
            return
        for (x0, y0, z0, x1, y1, z1) in boxes:
            if x0 <= x <= x1 and z0 <= z <= z1 and (abs(y1 - (y - 0.05)) < 0.2 or abs(y1 - (y - 0.15)) < 0.2):
                return
        print("  dans le vide :", label, round(x, 1), round(z, 1), round(y, 1)); bad += 1
    for (g, gx, gy, gz, yaw) in o.LOOT_GROUPS:
        for (cx, cz, cyaw) in GROUPS[g]:
            dx, dz = rot_xz(cx, cz, yaw)
            check(gx + dx, gy, gz + dz, 0.55, g)
    for (x, y, z) in o.OBJETS:
        check(x, y, z, 0.25, "objet")
    print("  contrôle du butin : %d problème(s)" % bad)
    return bad


# ============================================================================================ outils de scène
def pad(sc, name, x0, z0, x1, z1, top, color, bottom=None, mesh=True, rough=0.85):
    """dalle/route : boîte visible et collision, dessus à `top` (y monde), enregistrée pour le calcul du sol"""
    if bottom is None:
        bottom = top - 1.0
    if mesh:
        sc.mesh_box(name, x0, bottom, z0, x1, top, z1, color, rough)
    sc.col(x0, bottom, z0, x1, top, z1)
    Z.pads.append((x0, z0, x1, z1, top))


ASPH, BETON, SABLE, LIGNE = (0.13, 0.14, 0.17), (0.62, 0.64, 0.67), (0.70, 0.62, 0.50), (0.85, 0.88, 0.92)


def rect_free(x0, z0, x1, z1):
    """aucune structure (boîte haute) dans le rectangle local"""
    for (px0, pz0, px1, pz1, _top) in Z.pads:
        if px0 < x1 and px1 > x0 and pz0 < z1 and pz1 > z0:
            return False
    g = terr(Z.cx + (x0 + x1) / 2, Z.cz + (z0 + z1) / 2)
    for (bx0, by0, bz0, bx1, by1, bz1) in world_boxes():
        if bx0 < x1 and bx1 > x0 and bz0 < z1 and bz1 > z0 and by1 > g + 0.3 and by0 < g + 2.2:
            return False
    return True


USED = []     # rectangles déjà pris par des groupes de coffres en plein air
SOCLES = []   # scène des socles sous les groupes en plein air


def open_group(g, cxw, czw, rad=40, maxrange=0.45, yaw=0):
    """pose un groupe de coffres en plein air au meilleur endroit (relief plat, rien dessus) autour d'un point voulu"""
    f = max(FOOT[g]) + 0.8
    best = None
    for dx in range(-rad, rad + 1, 2):
        for dz in range(-rad, rad + 1, 2):
            x, zz = cxw + dx, czw + dz
            if math.hypot(x, zz) > Z.radius - 30:
                continue
            lo_, hi_ = terr_rect(Z.cx + x - f, Z.cz + zz - f, Z.cx + x + f, Z.cz + zz + f, 1.0)
            if hi_ - lo_ > maxrange:
                continue
            if not rect_free(x - f - 1, zz - f - 1, x + f + 1, zz + f + 1):
                continue
            if any(abs(x - ux) < f + uf + 1 and abs(zz - uz) < f + uf + 1 for (ux, uz, uf) in USED):
                continue
            sc_ = (hi_ - lo_, abs(dx) + abs(dz))
            if best is None or sc_ < best[0]:
                best = (sc_, x, zz, hi_)
    if best is None:
        print("  !! pas de place pour", g, "près de", cxw, czw)
        return
    _, x, zz, hi_ = best
    USED.append((x, zz, f))
    top = round(hi_ + 0.03, 2)
    lo2, _h = terr_rect(Z.cx + x - f, Z.cz + zz - f, Z.cx + x + f, Z.cz + zz + f, 1.0)
    pad(SOCLES[0], "Socle_%s_%d_%d" % (g.replace("Coffres_", ""), x, zz), x - f, zz - f, x + f, zz + f, top, SABLE, bottom=lo2 - 1.0)
    o.LOOT_GROUPS.append((g, x, top + 0.005, zz, yaw))


def silo(sc, x, z, base, s=2.0):
    sc.piece("Silos", "mega|Column_Hollow", x, base, z, 0, s, s, s)
    h = 5.0 * s
    sc.col(x - 0.6 * s, base, z - 0.6 * s, x + 0.6 * s, base + h, z + 0.6 * s)


def posts(b, key="mega|Column_Round", every=2):
    """poteaux d'une structure ouverte : aux coins des tuiles, tous les `every` tuiles, sur le pourtour (colonne de 5 m écrasée à 3 m)"""
    for i in range(0, b.nx + 1, every):
        for j in (0, b.nz):
            b.s.piece("Poteaux", key, i * 4.0, 0, j * 4.0, 0, 1.0, 0.6, 1.0)
            b.s.col(i * 4.0 - 0.5, 0, j * 4.0 - 0.5, i * 4.0 + 0.5, 3.0, j * 4.0 + 0.5)
    for j in range(every, b.nz, every):
        for i in (0, b.nx):
            b.s.piece("Poteaux", key, i * 4.0, 0, j * 4.0, 0, 1.0, 0.6, 1.0)
            b.s.col(i * 4.0 - 0.5, 0, j * 4.0 - 0.5, i * 4.0 + 0.5, 3.0, j * 4.0 + 0.5)


def pylon(sc, x, z, y_top, y_bot):
    """pilier sous une passerelle : colonne creuse étirée entre le sol (y_bot) et le dessous de la passerelle (y_top)"""
    h = max(1.0, y_top - y_bot)
    sc.piece("Piliers", "mega|Column_Hollow", x, y_bot, z, 0, 1.0, h / 5.0, 1.0)
    sc.col(x - 0.6, y_bot, z - 0.6, x + 0.6, y_top, z + 0.6)


def stack_cases(zone):
    """trajets de contrôle (escaliers et passerelles) pour le test de marche"""
    out = []
    for nm, b in o.B.items():
        for (lv, i, j, d) in b.stairs:
            ux, uz = {"N": (0, -1), "S": (0, 1), "E": (1, 0), "W": (-1, 0)}[d]
            if d == "N": tx, tz = (i + .5) * 4, j * 4
            elif d == "S": tx, tz = (i + .5) * 4, (j + 1) * 4
            elif d == "E": tx, tz = (i + 1) * 4, (j + .5) * 4
            else: tx, tz = i * 4, (j + .5) * 4
            sx, sz = tx - ux * 8 - ux * 1.0, tz - uz * 8 - uz * 1.0
            ex, ez = tx + ux * 1.5, tz + uz * 1.5
            yaw = math.atan2(-ux, -uz)
            out.append("S|%s|%d|%.2f|%.2f|%.2f|%.4f|%.2f|%.2f|%.2f" % (nm, lv, zone.cx + b.wx + sx, zone.cz + b.wz + sz, b.base + 3 * lv, yaw, zone.cx + b.wx + ex, zone.cz + b.wz + ez, b.base + 3 * (lv + 1)))
    for (s, x, y, zz, r) in o.BRIDGES:
        L = max(q[3] for q in s.boxes if q[3] - q[0] > 3 or q[3] - q[0] < 40)
        if s.name.startswith("Passerelle_Axe_Z"):
            continue
        out.append("B|%s|0|%.2f|%.2f|%.2f|%.4f|%.2f|%.2f|%.2f" % (s.name, zone.cx + x - 1.5, zone.cz + zz, y, math.atan2(-1, 0), zone.cx + x + L + 1.0, zone.cz + zz, y))
    return out


# ============================================================================================ DELTA
def delta():
    z = Zone("B", "Zones/B_Delta", "delta", "Delta", 336.0, -288.0, 84.10, 140.0)
    begin(z)
    Y = z.y0
    # ---------------------------------------------------------------- voies et places (une seule scène)
    v = Scn("Voies_Delta")
    pad(v, "Piste", -96, -8, 96, 8, Y, ASPH)
    pad(v, "Voie_Ouest", -56, -92, -44, 92, Y, ASPH)
    pad(v, "Voie_Est", 44, -92, 56, 92, Y, ASPH)
    pad(v, "Place_centrale", -14, -14, 14, 14, Y + 0.10, BETON)
    for (nm, a, b_, c, d) in (("Parvis_Halle", -26, -34, 26, -12), ("Aire_Hangars", -40, 12, 40, 40), ("Cour_Conteneurs", 58, -52, 98, 52),
                              ("Aire_Quai", -34, 48, 34, 70), ("Parvis_Tour", 22, -54, 42, -36), ("Cour_Bureaux", -92, -46, -62, -22)):
        pad(v, nm, a, b_, c, d, Y - 0.15, BETON)
    for zz in range(-88, 90, 12):
        if abs(zz) < 18: continue
        for xx in (-50, 50):
            v.mesh_box("Ligne_%d_%d" % (xx, zz), xx - 0.15, Y, zz, xx + 0.15, Y + 0.07, zz + 6, LIGNE, 0.6)
    for xx in range(-90, 92, 12):
        if abs(xx) < 18 or 44 <= abs(xx) <= 56: continue
        v.mesh_box("Ligne_P_%d" % xx, xx, Y, -0.15, xx + 6, Y + 0.07, 0.15, LIGNE, 0.6)
    v.piece("Monument", "mega|Platform_Round1", 0, Y + 0.14, 0)
    v.col(-1.5, Y, -1.5, 1.5, Y + 0.35, 1.5)
    o.EXTRA.append((v, 0, 0.0, 0, 0))

    # ---------------------------------------------------------------- halle du terminal : 10x4, 2 niveaux, nef vitrée avec coursive
    nave = {(i, j) for i in range(1, 9) for j in (1, 2)}
    halle = Bld("Halle_Terminal", -20, -48, 10, 4, 2, facade_rule({0: "glass", "*": "mixed"}), doors={("S", 4), ("S", 5), ("N", 4), ("N", 5), ("W", 3), ("E", 0)},
                voids={1: nave}, stairs=[(0, 0, 1, "N"), (1, 9, 2, "S")], roof_gaps={("E", 9, 0)}, glass_roof=nave)
    add_bld(halle, Y)
    place(halle, "Coffres_Face4", 0, 12, 8); place(halle, "Coffres_Face4", 0, 28, 8)
    place(halle, "Coffres_Ligne5", 1, 20, 1.5, 0); place(halle, "Coffres_Ligne5", 1, 20, 14.5, 180)
    place(halle, "Coffres_U6", 2, 20, 8, 0)

    # ---------------------------------------------------------------- tour de contrôle : 4x3, 5 niveaux
    tour = Bld("Tour_Controle", 24, -52, 4, 3, 5, facade_rule({0: "solid", 1: "mixed", 2: "mixed", 3: "glass", 4: "glass"}), doors={("S", 2), ("E", 1)},
               door_lv={(2, "W", 1)}, stairs=[(0, 2, 1, "E"), (1, 1, 0, "W"), (2, 2, 2, "E"), (3, 1, 0, "W"), (4, 2, 1, "E")])
    add_bld(tour, Y)
    place(tour, "Coffres_Ligne5", 1, 8, 10.5, 180); place(tour, "Coffres_Ligne5", 3, 8, 6, 0); place(tour, "Coffres_Ligne5", 4, 8, 10.5, 180)
    place(tour, "Coffres_Face4", 5, 9, 6)

    # ---------------------------------------------------------------- hangars de fret : A (nef) et B (mezzanine)
    nefA = {(i, j) for i in range(1, 6) for j in (1, 2, 3)}
    hA = Bld("Hangar_Fret_A", -36, 16, 7, 5, 2, facade_rule({0: "solid", "*": "glass"}), doors={("N", 2), ("N", 3), ("N", 4), ("S", 2), ("S", 3), ("S", 4), ("W", 3), ("E", 3)},
             voids={1: nefA}, stairs=[(0, 0, 1, "N"), (1, 6, 3, "S")], roof_gaps={("E", 6, 1)})
    add_bld(hA, Y)
    place(hA, "Coffres_U6", 0, 14, 10, 0); place(hA, "Coffres_Ligne5", 1, 14, 1.5, 0); place(hA, "Coffres_Ligne5", 1, 14, 18.5, 180); place(hA, "Coffres_Face4", 2, 14, 10)
    nefB = {(i, j) for i in range(1, 6) for j in (2, 3)}
    hB = Bld("Hangar_Fret_B", 8, 16, 7, 5, 2, facade_rule({0: "solid", "*": "mixed"}), doors={("N", 2), ("N", 3), ("N", 4), ("S", 2), ("S", 3), ("S", 4), ("W", 3), ("E", 3)},
             voids={1: nefB}, stairs=[(0, 6, 1, "N"), (1, 0, 3, "S")], roof_gaps={("W", 0, 1)})
    add_bld(hB, Y)
    place(hB, "Coffres_Rond6", 0, 14, 10); place(hB, "Coffres_Ligne5", 1, 14, 5.5, 0); place(hB, "Coffres_U6", 2, 14, 10, 0)

    # ---------------------------------------------------------------- quai couvert (structure ouverte) et bureaux
    quai = Bld("Quai_Fret", -24, 52, 12, 2, 1, facade_rule({"*": "G"}), floor_key="mega|Platform_Metal")
    quai.facade = lambda lv, side, idx: "N"
    add_bld(quai, Y)
    posts(quai, every=2)
    place(quai, "Coffres_Face4", 0, 14, 4); place(quai, "Coffres_Face4", 0, 34, 4)
    bur = Bld("Bureaux_Douane", -84, -40, 4, 3, 2, facade_rule({0: "mixed", "*": "glass"}), doors={("E", 1), ("S", 2)}, stairs=[(0, 2, 1, "E"), (1, 1, 2, "W")])
    add_bld(bur, Y)
    place(bur, "Coffres_Ligne5", 0, 9.5, 1.5, 0); place(bur, "Coffres_Ligne5", 1, 8, 1.5, 0)

    # ---------------------------------------------------------------- conteneurs : EO (2x1), pile avec escalier (4x1, 2 niveaux)
    def cont(name, nx, nz, L, doors, stairs=()):
        c = Bld(name, 0, 0, nx, nz, L, lambda lv, side, idx: "S", doors=doors, stairs=list(stairs))
        c.base = Y
        c.build()
        return c
    ceo = cont("Conteneur_EO", 2, 1, 1, {("W", 0)})
    cns = cont("Conteneur_NS", 1, 2, 1, {("N", 0)})
    cpile = cont("Conteneur_Pile", 4, 1, 2, {("W", 0)}, [(0, 2, 0, "E"), (1, 1, 0, "W")])
    z.conteneurs = []
    for (nm, sc_, xx, zz) in (
            ("Pile", cpile, 60, -44), ("Pile", cpile, 80, -44), ("Pile", cpile, 60, -28), ("Pile", cpile, 80, -28), ("Pile", cpile, 60, 40), ("Pile", cpile, 80, 40),
            ("EO", ceo, 62, -18), ("EO", ceo, 80, -18), ("EO", ceo, 70, 16), ("EO", ceo, 86, 16), ("EO", ceo, 62, 28), ("EO", ceo, 84, 28),
            ("NS", cns, 96, -30), ("NS", cns, 96, 22), ("EO", ceo, -88, -12), ("EO", ceo, -88, 12), ("NS", cns, -76, 26), ("NS", cns, -72, 40)):
        z.conteneurs.append((sc_, xx, Y, zz))

    # ---------------------------------------------------------------- dépôt de carburant : silos
    dep = Scn("Depot_Carburant")
    for k, (sx, sz) in enumerate(((-84, 44), (-78, 44), (-72, 44), (-84, 52), (-78, 52), (-72, 52))):
        silo(dep, sx, sz, Y, 1.6)
    z.extra_nodes.append((dep, 0, 0, 0, 0))
    o.EXTRA.append((dep, 0, 0.0, 0, 0))

    # ---------------------------------------------------------------- passerelles
    o.BRIDGES.append((bridge("Passerelle_Halle_Tour", 1, "x", "glass"), 20, Y + 6, -46, 0))
    o.BRIDGES.append((bridge("Passerelle_Hangars", 4, "x", "open"), -8, Y + 6, 22, 0))

    # ---------------------------------------------------------------- coffres en plein air
    for (g, x, zz, yaw) in (("Coffres_Carre4", 0, 0, 0), ("Coffres_U6", 0, -22, 0), ("Coffres_Face4", -34, -22, 0), ("Coffres_Arc5", -70, 4, 0), ("Coffres_Carre4", 70, -2, 0),
                            ("Coffres_U6", 88, 8, 180), ("Coffres_Face4", -60, 30, 0), ("Coffres_Arc5", 0, 76, 0), ("Coffres_Carre4", 70, 56, 0), ("Coffres_Face4", -28, 40, 0),
                            ("Coffres_U6", -20, 80, 0), ("Coffres_Arc5", 64, -66, 0)):
        gy = max(ground(x + dx, zz + dz) for (dx, dz, _) in ((rot_xz(cx, cz, yaw) + (0,)) for (cx, cz, _y) in GROUPS[g]))
        o.LOOT_GROUPS.append((g, x, gy + 0.005, zz, yaw))
    return z


# ============================================================================================ NAMA
def nama():
    USED.clear(); SOCLES.clear(); SOCLES.append(Scn("Socles_Groupes"))
    z = Zone("C", "Zones/C_Nama", "nama", "Nama", 0.0, 340.0, 0.0, 170.0)
    begin(z)
    # ---------------------------------------------------------------- butte centrale : esplanade (dalle) et station d'extraction
    lo, hi = terr_rect(z.cx - 28, z.cz - 24, z.cx + 28, z.cz + 32)
    E_TOP = round(hi + 0.04 + 0.22, 2)           # dessus de l'esplanade : les passerelles (toit des postes) y arrivent à moins de 0,45 m
    Ym = round(E_TOP + 0.12, 2)                  # sol des bâtiments de la butte (dalle au-dessus de l'esplanade)
    print("  butte : relief %.2f a %.2f -> esplanade %.2f, sol des batiments %.2f" % (lo, hi, E_TOP, Ym))
    v = Scn("Esplanade_Nama")
    pad(v, "Esplanade", -28, -24, 28, 32, E_TOP, SABLE, bottom=lo - 1.5)
    # escaliers de la butte : gradins sur les flancs est, ouest, nord et sud (marches de 0,25 m, pour descendre de l'esplanade vers le relief)
    z.extra_nodes.append((v, 0, 0, 0, 0))
    o.EXTRA.append((v, 0, 0.0, 0, 0))

    st = Bld("Station_Extraction", -10, -14, 5, 4, 3, facade_rule({0: "solid", 1: "mixed", "*": "glass"}), doors={("S", 1), ("W", 3), ("E", 2), ("N", 3)},
             stairs=[(0, 2, 1, "E"), (1, 1, 2, "W"), (2, 2, 0, "E")])
    add_bld(st, Ym)
    place(st, "Coffres_Face4", 0, 14, 12); place(st, "Coffres_Ligne5", 1, 10, 1.5, 0); place(st, "Coffres_Ligne5", 2, 12, 14.5, 180); place(st, "Coffres_Carre4", 3, 14, 8)

    dk = Bld("Derrick", 12, 10, 4, 3, 2, None, stairs=[(0, 2, 1, "E"), (1, 1, 0, "W")], floor_key="mega|Platform_Metal")
    dk.facade = lambda lv, side, idx: "N"
    add_bld(dk, Ym)
    posts(dk, every=2)
    place(dk, "Coffres_Ligne5", 1, 8, 10.5, 180); place(dk, "Coffres_Face4", 2, 9, 8)
    mast = Scn("Mats_Derrick")
    for (mx, mz) in ((11, 8), (29, 8), (11, 24), (29, 24)):
        mast.piece("Mats", "mega|Column_Large_Straight", mx, E_TOP, mz, 0)
        mast.col(mx - 0.7, E_TOP, mz - 0.9, mx + 0.7, E_TOP + 10, mz + 1.1)
    z.extra_nodes.append((mast, 0, 0, 0, 0)); o.EXTRA.append((mast, 0, 0.0, 0, 0))

    # ---------------------------------------------------------------- quatre postes sur l'anneau, reliés à la butte par des passerelles sur pylônes
    target = E_TOP + 0.2
    sites = {}
    def site_search(name, cxw, czw, fixed_x=None):
        best = None
        for dx in range(-24, 25, 4):
            for dz in range(-24, 25, 4):
                wx, wz = int(round((cxw + dx) / 4.0)) * 4 - 8, int(round((czw + dz) / 4.0)) * 4 - 6
                lo_, hi_ = terr_rect(z.cx + wx - 2, z.cz + wz - 2, z.cx + wx + 18, z.cz + wz + 14, 2.0)
                base = round(hi_ + 0.04, 2)
                roof = base + 9.0
                score = (abs(roof - target) > 0.45, (hi_ - lo_), abs(dx) + abs(dz))
                if best is None or score < best[0]:
                    best = (score, wx, wz, base, lo_, hi_)
        print("  site %s : (%d,%d) relief %.2f..%.2f base %.2f toit %.2f (cible %.2f) %s" % (name, best[1], best[2], best[4], best[5], best[3], best[3] + 9, target, "" if not best[0][0] else "!! ecart"))
        return best
    outposts = []
    for (nm, cxw, czw, side) in (("Poste_Nord", 0, -88, "N"), ("Poste_Est", 90, 4, "E"), ("Poste_Sud", 0, 92, "S"), ("Poste_Ouest", -90, 4, "W")):
        sc_, wx, wz, base, lo_, hi_ = site_search(nm, cxw, czw)
        # emprise du poste : calage sur la grille pour que la passerelle (n tuiles) tombe pile sur le bord de l'esplanade
        if side == "N":
            wx = -8; wz = -36 - 4 * round((-36 - wz) / 4.0)
        elif side == "S":
            wx = -8; wz = 32 + 4 * round((wz - 32) / 4.0)
        elif side == "E":
            wx = 28 + 4 * round((wx - 28) / 4.0)
        else:
            wx = -44 - 4 * round((-44 - wx) / 4.0)
        p = Bld(nm, wx, wz, 4, 3, 3, facade_rule({0: "solid", "*": "mixed"}), doors={("W", 1), ("E", 1)} if side in "NS" else {("N", 1), ("S", 1)},
                stairs=[(0, 2, 1, "E"), (1, 1, 0, "W"), (2, 2, 2, "E")],
                roof_gaps={"N": {("S", 3, 2)}, "S": {("N", 3, 0)}, "E": {("W", 0, 0)}, "W": {("E", 3, 0)}}[side])
        lo_, hi_ = terr_rect(z.cx + wx, z.cz + wz, z.cx + wx + 16, z.cz + wz + 12, 2.0)
        base = round(hi_ + 0.04, 2)
        add_bld(p, base)
        # coussin de béton sous le poste (marges de 2 m, dessus = sol du poste)
        pd = Scn("Socle_%s" % nm)
        pad(pd, "Socle", wx - 2, wz - 2, wx + 18, wz + 14, base - 0.12, SABLE, bottom=lo_ - 1.5, mesh=True)
        z.extra_nodes.append((pd, 0, 0, 0, 0)); o.EXTRA.append((pd, 0, 0.0, 0, 0))
        roof_y = base + 9.0
        if abs(roof_y - E_TOP) > 0.45: print("  !! passerelle %s : marche de %.2f m" % (nm, roof_y - E_TOP))
        if side == "E":
            n = int((wx - 28) / 4); br = bridge("Passerelle_%s" % nm, n, "x", "open"); o.BRIDGES.append((br, 28, roof_y, wz + 2, 0))
            pyl = [(28 + 6 + 12 * k, wz + 2) for k in range(max(1, n // 3))]
            ax = "x"
        elif side == "W":
            n = int((-28 - (wx + 16)) / 4); br = bridge("Passerelle_%s" % nm, n, "x", "open"); o.BRIDGES.append((br, wx + 16, roof_y, wz + 2, 0))
            pyl = [(wx + 16 + 6 + 12 * k, wz + 2) for k in range(max(1, n // 3))]
            ax = "x"
        elif side == "N":
            n = int((-24 - (wz + 12)) / 4); br = bridge("Passerelle_%s_Axe_Z" % nm, n, "z", "open"); o.BRIDGES.append((br, wx + 14, roof_y, wz + 12, 0))
            pyl = [(wx + 14, wz + 12 + 6 + 12 * k) for k in range(max(1, n // 3))]
            ax = "z"
        else:
            n = int((wz - 32) / 4); br = bridge("Passerelle_%s_Axe_Z" % nm, n, "z", "open"); o.BRIDGES.append((br, wx + 14, roof_y, 32, 0))
            pyl = [(wx + 14, 32 + 6 + 12 * k) for k in range(max(1, n // 3))]
            ax = "z"
        py_sc = Scn("Piliers_%s" % nm)
        for (px, pz) in pyl:
            for off in (-2.4, 2.4):
                qx, qz = (px, pz + off) if ax == "x" else (px + off, pz)
                pylon(py_sc, qx, qz, roof_y - 0.3, terr(z.cx + qx, z.cz + qz) - 0.5)
        z.extra_nodes.append((py_sc, 0, 0, 0, 0)); o.EXTRA.append((py_sc, 0, 0.0, 0, 0))
        place(p, "Coffres_Ligne5", 0, 8, 1.5, 0) if side != "N" else place(p, "Coffres_Ligne5", 0, 8, 10.5, 180)
        place(p, "Coffres_Face4", 1, 12, 6) if False else None
        place(p, "Coffres_Ligne5", 1, 8, 10.5, 180)
        place(p, "Coffres_Carre4", 3, 8, 4.5)
        outposts.append(p)

    # ---------------------------------------------------------------- silos, abris ouverts et camp (sur l'anneau, sites les plus plats)
    def flat_site(w, d, cxw, czw, rad=26):
        best = None
        for dx in range(-rad, rad + 1, 4):
            for dz in range(-rad, rad + 1, 4):
                wx, wz = int(round((cxw + dx) / 4.0)) * 4, int(round((czw + dz) / 4.0)) * 4
                if math.hypot(wx, wz) > z.radius - 40 or not rect_free(wx - 4, wz - 4, wx + w + 4, wz + d + 4):
                    continue
                lo_, hi_ = terr_rect(z.cx + wx - 2, z.cz + wz - 2, z.cx + wx + w + 2, z.cz + wz + d + 2, 2.0)
                sc_ = (hi_ - lo_, abs(dx) + abs(dz))
                if best is None or sc_ < best[0]:
                    best = (sc_, wx, wz, lo_, hi_)
        return best
    for k, (cxw, czw) in enumerate(((-64, -56), (64, -56), (64, 60), (-64, 60))):
        sc_, wx, wz, lo_, hi_ = flat_site(12, 8, cxw, czw)
        ab = Bld("Abri_%d" % (k + 1), wx, wz, 3, 2, 1, None, floor_key="mega|Platform_Metal")
        ab.facade = lambda lv, side, idx: "N"
        base = round(hi_ + 0.04, 2)
        add_bld(ab, base)
        posts(ab, every=1)
        pd = Scn("Socle_Abri_%d" % (k + 1))
        pad(pd, "Socle", wx - 2, wz - 2, wx + 14, wz + 10, base - 0.12, SABLE, bottom=lo_ - 1.5)
        z.extra_nodes.append((pd, 0, 0, 0, 0)); o.EXTRA.append((pd, 0, 0.0, 0, 0))
        place(ab, "Coffres_Face4", 0, 6, 4)
        print("  abri %d : (%d,%d) relief %.2f..%.2f" % (k + 1, wx, wz, lo_, hi_))
    # silos : deux grappes
    for k, (cxw, czw) in enumerate(((-40, 76), (44, -78))):
        sc_, wx, wz, lo_, hi_ = flat_site(16, 8, cxw, czw)
        base = round(hi_ + 0.04, 2)
        sl = Scn("Silos_%d" % (k + 1))
        pad(sl, "Socle", wx - 2, wz - 2, wx + 18, wz + 10, base - 0.02, SABLE, bottom=lo_ - 1.5)
        for (sx, sz) in ((wx + 2, wz + 2), (wx + 8, wz + 2), (wx + 14, wz + 2), (wx + 2, wz + 8), (wx + 8, wz + 8)):
            silo(sl, sx, sz, base, 1.8)
        z.extra_nodes.append((sl, 0, 0, 0, 0)); o.EXTRA.append((sl, 0, 0.0, 0, 0))
    # camp : caisses et clôtures orange du Starter Kit, sur des sites plats et libres
    camp = Scn("Camp_Nama")
    for (cxw, czw) in ((-30, 74), (32, -72), (82, 44), (-84, -32)):
        best = None
        for dx in range(-20, 21, 4):
            for dz in range(-20, 21, 4):
                wx, wz = int(round((cxw + dx) / 4.0)) * 4, int(round((czw + dz) / 4.0)) * 4
                if math.hypot(wx, wz) > Z.radius - 40 or not rect_free(wx - 3, wz - 3, wx + 15, wz + 11):
                    continue
                lo_, hi_ = terr_rect(Z.cx + wx - 1, Z.cz + wz - 1, Z.cx + wx + 13, Z.cz + wz + 9, 2.0)
                sc_ = (hi_ - lo_, abs(dx) + abs(dz))
                if best is None or sc_ < best[0]:
                    best = (sc_, wx, wz, lo_, hi_)
        if best is None:
            print("  !! pas de place pour un camp près de", cxw, czw); continue
        _, wx, wz, lo_, hi_ = best
        base = round(hi_ + 0.04, 2)
        pad(camp, "Socle_%d_%d" % (wx, wz), wx - 1, wz - 1, wx + 13, wz + 9, base - 0.02, SABLE, bottom=lo_ - 1.5)
        for k in range(6):
            qx, qz = wx + 2 + (k % 3) * 3.6, wz + 2 + (k // 3) * 4.2
            camp.piece("Caisses", "starter|Crate_01", qx, base, qz, 0)
            camp.col(qx - 0.5, base, qz - 1.0, qx + 0.5, base + 0.75, qz + 1.0)
        for k in range(3):
            fx = wx + k * 5.0
            camp.piece("Clotures", "starter|Fence_Long_01", fx + 2.0, base, wz + 7.0, 90)
            camp.col(fx - 0.5, base, wz + 6.9, fx + 4.5, base + 1.3, wz + 7.15)
        USED.append((wx + 6, wz + 4, 7))
    z.extra_nodes.append((camp, 0, 0, 0, 0)); o.EXTRA.append((camp, 0, 0.0, 0, 0))
    # grands groupes en plein air (relief plat, rien dessus)
    for (g, x, zz, yaw) in (("Coffres_U6", -20, -70, 0), ("Coffres_Arc5", 100, -30, 0), ("Coffres_U6", 100, 60, 0), ("Coffres_Arc5", -100, 50, 0),
                            ("Coffres_Rond6", 0, 62, 0), ("Coffres_Rond6", 0, -52, 0), ("Coffres_Face4", -44, 8, 0), ("Coffres_Face4", 52, 14, 0),
                            ("Coffres_Carre4", -60, -10, 0), ("Coffres_Carre4", 60, -20, 0), ("Coffres_U6", -66, 40, 0), ("Coffres_Face4", 66, 60, 0)):
        open_group(g, x, zz, 40, 0.45, yaw)
    o.EXTRA.append((SOCLES[0], 0, 0.0, 0, 0))
    return z


# ============================================================================================ écriture
def parse_blocks(text):
    blocks, cur = [], []
    for ln in text.split("\n"):
        if ln.startswith(("[node", "[ext_resource", "[sub_resource", "[gd_scene", "[connection", "[resource")):
            if cur: blocks.append(cur)
            cur = [ln]
        else:
            cur.append(ln)
    if cur: blocks.append(cur)
    return blocks


def block_pos(b):
    for ln in b:
        if ln.startswith("transform = Transform3D("):
            v = [float(t) for t in ln[len("transform = Transform3D("):-1].split(",")]
            return v[9], v[10], v[11]
    return None


def edit_main(zs):
    text = open(MAIN, encoding="utf-8").read()
    blocks = parse_blocks(text)
    drop_subs, kept = set(), []
    stats = {z.key: [0, 0, 0] for z in zs}
    def inside(z, p):
        return p is not None and math.hypot(p[0] - z.cx, p[2] - z.cz) <= z.radius
    for b in blocks:
        h = b[0]
        zz = next((z for z in zs if 'parent="%s"' % z.parent in h), None)
        if zz:
            stats[zz.key][0] += 1; continue
        if 'parent="BuildingColliders"' in h:
            p = block_pos(b)
            zz = next((z for z in zs if inside(z, p)), None)
            if zz:
                for ln in b:
                    m = re.match(r'shape = SubResource\("([^"]+)"\)', ln)
                    if m: drop_subs.add(m.group(1))
                stats[zz.key][1] += 1; continue
        if h.startswith("[node") and ('parent="Loot/Coffres"' in h or 'parent="Loot/Objets"' in h):
            p = block_pos(b)
            zz = next((z for z in zs if inside(z, p)), None)
            if zz:
                stats[zz.key][2] += 1; continue
        kept.append(b)
    body = "\n".join("\n".join(b) for b in kept)
    kept = [b for b in kept if not (b[0].startswith("[sub_resource") and (m := re.search(r'id="([^"]+)"', b[0])) and m.group(1) in drop_subs and ('SubResource("%s")' % m.group(1)) not in body)]
    body = "\n".join("\n".join(b) for b in kept)
    kept = [b for b in kept if not (b[0].startswith("[ext_resource") and (m := re.search(r'id="([^"]+)"', b[0])) and ('ExtResource("%s")' % m.group(1)) not in body and "assets/environement" in b[0])]
    # ressources existantes réutilisables (groupes de coffres de l'étape Orion)
    have = {}
    for b in kept:
        if b[0].startswith("[ext_resource"):
            m = re.search(r'path="(.+?)" id="([^"]+)"', b[0])
            if m: have[m.group(1)] = m.group(2)
    new_ext, ids = [], {}
    def rid(path, prefix):
        if path in have: return have[path]
        if path not in ids:
            ids[path] = "%s_%d" % (prefix, len(ids) + 1)
            new_ext.append(['[ext_resource type="PackedScene" path="%s" id="%s"]' % (path, ids[path])])
        return ids[path]
    zone_blocks, coffre_blocks, objet_blocks = {}, [], []
    for z in zs:
        nodes = []
        for nm, b in z.B.items():
            nodes.append((nm, "res://scenes/maps/%s/%s.tscn" % (z.folder, nm), z.cx + b.wx, b.base, z.cz + b.wz, 0))
        for (s_, x, y, zz, r) in z.bridges + z.extras:
            nodes.append((s_.name, "res://scenes/maps/%s/%s.tscn" % (z.folder, s_.name), z.cx + x, y, z.cz + zz, r))
        for (s_, x, y, zz) in getattr(z, "conteneurs", []):
            cnt = z.cont_count = getattr(z, "cont_count", 0) + 1
            nodes.append(("%s_%02d" % (s_.name, cnt), "res://scenes/maps/%s/%s.tscn" % (z.folder, s_.name), z.cx + x, y, z.cz + zz, 0))
        zone_blocks[z.key] = [['[node name="%s" parent="%s" instance=ExtResource("%s")]' % (nm, z.parent, rid(path, z.folder)), "transform = %s" % tf(x, y, zz, r), ""] for (nm, path, x, y, zz, r) in nodes]
        cnt = {}
        for (g, gx, gy, gz, yaw) in z.groups:
            cnt[g] = cnt.get(g, 0) + 1
            nm = "%s_%s_%02d" % (z.prefix, g.replace("Coffres_", ""), cnt[g])
            coffre_blocks.append(['[node name="%s" parent="Loot/Coffres" instance=ExtResource("%s")]' % (nm, rid("res://scenes/loot/groupes/%s.tscn" % g, "grp")), "transform = %s" % tf(z.cx + gx, gy, z.cz + gz, yaw), ""])
        for k, (x, y, zz) in enumerate(z.objets):
            objet_blocks.append(['[node name="%s_Objet_%03d" parent="Loot/Objets" instance=ExtResource("loot_item_prefab")]' % (z.prefix, k + 1), "transform = %s" % tf(z.cx + x, y, z.cz + zz), ""])
    last_ext = max(i for i, b in enumerate(kept) if b[0].startswith("[ext_resource"))
    kept[last_ext + 1:last_ext + 1] = new_ext
    for z in zs:
        ia = next(i for i, b in enumerate(kept) if b[0].startswith('[node name="%s" type="Node3D" parent="Zones"' % z.parent.split("/")[-1]))
        kept[ia + 1:ia + 1] = zone_blocks[z.key]
    def last_index(prefix):
        idx = None
        for i, b in enumerate(kept):
            if b[0].startswith("[node") and prefix in b[0]: idx = i
        return idx
    ic = last_index('parent="Loot/Coffres"'); kept[ic + 1:ic + 1] = coffre_blocks
    io = last_index('parent="Loot/Objets"'); kept[io + 1:io + 1] = objet_blocks
    open(MAIN, "w", encoding="utf-8", newline="\n").write("\n".join("\n".join(b) for b in kept))
    for z in zs:
        print("  %s : -%d pièces, -%d boîtes, -%d points de butin" % (z.key, *stats[z.key]))


def edit_final(zs):
    d = json.load(open(FINAL, encoding="utf-8"))
    n0 = len(d["boxes"])
    d["boxes"] = [b for b in d["boxes"] if not any(math.hypot((b[0] + b[3]) / 2 - z.cx, (b[2] + b[5]) / 2 - z.cz) <= z.radius for z in zs)]
    json.dump(d, open(FINAL, "w", encoding="utf-8"), separators=(",", ":"))
    print("buildings_final.json : %d -> %d boîtes" % (n0, len(d["boxes"])))


def finish(z):
    """calculs de fin de zone : butin autour des groupes, objets dans les bâtiments, contrôles ; fige l'état dans l'objet zone"""
    o._BOXES.extend(world_boxes())
    around_groups()
    objets_auto()
    bad = validate()
    z.B, z.bridges, z.extras = dict(o.B), list(o.BRIDGES), list(o.EXTRA)
    z.groups, z.objets = list(o.LOOT_GROUPS), list(o.OBJETS)
    z.nbox = len(o._BOXES)
    z.cases = stack_cases(z)
    print("  %s : %d bâtiments, %d boîtes, %d groupes (%d coffres), %d objets" % (z.key, len(z.B), z.nbox, len(z.groups), sum(len(GROUPS[g[0]]) for g in z.groups), len(z.objets)))
    return bad


def save_zone(z):
    folder = os.path.join(ROOT, "scenes", "maps", z.folder)
    for b in z.B.values(): b.s.save(folder)
    for (s_, *_r) in z.bridges + z.extras: s_.save(folder)
    for (sc_, *_r) in getattr(z, "conteneurs", []):
        pass
    seen = set()
    for (sc_, *_r) in getattr(z, "conteneurs", []):
        if sc_.name not in seen:
            sc_.s.save(folder); seen.add(sc_.name)


def main():
    if os.path.isdir(os.path.join(ROOT, "scenes", "maps", "delta")) and not (DRY or os.environ.get("ORION_FORCE")):
        sys.exit("scenes/maps/delta existe déjà : le script est à usage unique (éditez les scènes dans Godot).")
    zs = []
    for build in (delta, nama):
        print(build.__name__.upper())
        z = build()
        finish(z)
        zs.append(z)
        # contrôles : cas de marche pour le test
        with open(os.path.join(os.environ.get("ZONES_CASES", "."), "cases_%s.txt" % z.key), "w", encoding="utf-8") as f:
            f.write("\n".join(z.cases))
    if DRY:
        print("(essai à blanc : rien n'a été écrit)")
        return
    os.makedirs(BACKUP, exist_ok=True)
    for f in (MAIN, FINAL, os.path.join(ROOT, "assets", "maps", "frontiere01.world"), os.path.join(ROOT, "assets", "maps", "frontiere01.png")):
        shutil.copy2(f, os.path.join(BACKUP, os.path.basename(f)))
    o.write_group_scenes()
    for z in zs:
        save_zone(z)
    edit_main(zs)
    edit_final(zs)


if __name__ == "__main__":
    main()
