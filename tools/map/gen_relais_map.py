#!/usr/bin/env python3
"""Carte « Relais » du Deathmatch : arène intérieure sur trois niveaux (0, +4 m, +8 m), à la Quake.

    python tools/map/gen_relais_map.py            # écrit scenes/maps/Relais.tscn (refuse si elle existe : --force pour la refaire)
    python tools/map/gen_relais_map.py --minimap  # redessine seulement assets/maps/relais.png
    python tools/map/gen_relais_map.py --check    # vérifie les trajectoires des tremplins sans rien écrire

Plan (cases de 2 m, 48 x 48, nord en haut) :
  - atrium central ouvert sur les trois niveaux, passerelle nord-sud au niveau +4, passerelle est-ouest au niveau +8 ;
  - anneau du milieu (+4) autour de l'atrium, balcon (+8) au-dessus, anneau haut (+8) sur tout le pourtour avec quatre salles d'angle ;
  - au sol : grande salle à piliers sous l'anneau du milieu, quatre axes, couloir périphérique, quatre salles d'angle
    (soins au nord, téléporteurs au sud) reliées par des passages ;
  - salle du Rail (+8, nord, sniper légendaire) : tremplin dans un puits depuis l'axe nord, ou portes de l'anneau haut ;
  - salle Roquette (+4, sud, mitrailleuse légendaire) : tremplin dans un puits depuis l'axe sud, ou couloir étroit ;
  - salles d'armure (+4, est et ouest) : gros bouclier, escalier vers le niveau +8.
Ensuite la scène s'édite dans Godot (CoverBox, ArenaJumpPad avec sa « Cible », ArenaTeleporter avec sa « Sortie », points de butin, lumières),
puis « Exporter la carte » (ou la commande de tools/map/README.md) fabrique assets/maps/relais.world.
"""
import math, os, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
N = 48            # cases par côté
CELL = 2.0        # mètres par case
LEVEL = 4.0       # hauteur d'un niveau
SLAB = 0.4        # épaisseur d'une dalle d'étage
CEIL = 12.0       # dessous du plafond
GRAVITY = 29.4    # movement.gravity
ART = 'res://scenes/maps/relais/'


def X(c):
    """Abscisse (m) du bord ouest de la case c."""
    return -N * CELL / 2 + c * CELL


def cx(c):
    """Abscisse (m) du centre de la case c (c peut être fractionnaire : 23.5 = frontière entre 23 et 24 → 0 m)."""
    return X(c) + CELL / 2


# ------------------------------------------------------------------ plan : trois grilles de caractères
# '#' plein (mur sur toute la hauteur du niveau), '.' sol praticable (dalle à LEVEL*l), ' ' vide (pas de sol : on voit et on tire vers le bas)

class Plan:
    def __init__(self):
        self.g = [[['#'] * N for _ in range(N)] for _ in range(3)]
        self.stairs = []          # (c0, r0, c1, r1, niveau, sens de montée)
        self.no_rail = set()      # arêtes (niveau, (c, r) sol, (c, r) vide) sans garde-corps (haut des escaliers)

    def carve(self, l, c0, r0, c1, r1, ch='.'):
        for r in range(min(r0, r1), max(r0, r1) + 1):
            for c in range(min(c0, c1), max(c0, c1) + 1):
                self.g[l][r][c] = ch

    def mirror_x(self, l, c0, r0, c1, r1, ch='.'):
        self.carve(l, c0, r0, c1, r1, ch)
        self.carve(l, N - 1 - c1, r0, N - 1 - c0, r1, ch)

    def mirror_xz(self, l, c0, r0, c1, r1, ch='.'):
        self.mirror_x(l, c0, r0, c1, r1, ch)
        self.mirror_x(l, c0, N - 1 - r1, c1, N - 1 - r0, ch)

    def stair(self, c0, r0, c1, r1, l, up):
        """Escalier de 16 marches de 0,25 m du niveau l au niveau l+1, montant vers `up` ('N', 'S', 'E', 'O') ; le niveau l+1 est vidé au-dessus."""
        self.carve(l, c0, r0, c1, r1, '.')
        self.carve(l + 1, c0, r0, c1, r1, ' ')
        self.stairs.append((c0, r0, c1, r1, l, up))
        # haut de l'escalier : pas de garde-corps entre le palier et la dernière marche
        for c in range(c0, c1 + 1):
            for r in range(r0, r1 + 1):
                top = {'N': r == r0, 'S': r == r1, 'E': c == c1, 'O': c == c0}[up]
                if not top: continue
                d = {'N': (0, -1), 'S': (0, 1), 'E': (1, 0), 'O': (-1, 0)}[up]
                self.no_rail.add((l + 1, (c + d[0], r + d[1]), (c, r)))

    def at(self, l, c, r):
        if c < 0 or r < 0 or c >= N or r >= N: return '#'
        return self.g[l][r][c]


def layout():
    p = Plan()
    # ================= niveau 0 (sol)
    p.carve(0, 14, 14, 33, 33)                                   # grande salle sous l'anneau du milieu
    for c, r in [(15, 15), (31, 15), (15, 31), (31, 31)]:         # piliers d'angle
        p.carve(0, c, r, c + 1, r + 1, '#')
    for c, r in [(15, 22), (31, 22)]:                            # piliers des côtés est et ouest (l'escalier passe entre eux)
        p.carve(0, c, r, c + 1, r + 3, '#')
    p.carve(0, 22, 6, 25, 13); p.carve(0, 22, 34, 25, 41)        # axes nord et sud
    p.carve(0, 6, 22, 13, 25); p.carve(0, 34, 22, 41, 25)        # axes ouest et est
    # couloir périphérique
    p.carve(0, 4, 4, 43, 5); p.carve(0, 4, 42, 43, 43); p.carve(0, 4, 4, 5, 43); p.carve(0, 42, 4, 43, 43)
    # salles d'angle (soins au nord, téléporteurs au sud) et leurs passages
    p.mirror_xz(0, 7, 7, 12, 12)
    p.mirror_xz(0, 9, 6, 10, 6)        # porte vers le couloir nord / sud
    p.mirror_xz(0, 6, 9, 6, 10)        # porte vers le couloir ouest / est
    p.mirror_xz(0, 13, 9, 21, 10)      # passage vers l'axe nord / sud
    p.mirror_xz(0, 9, 13, 10, 21)      # passage vers l'axe ouest / est
    # caches dans le couloir périphérique (renfoncements)
    p.mirror_xz(0, 16, 3, 17, 3); p.mirror_xz(0, 3, 16, 3, 17)

    # ================= niveau 1 (+4 m)
    p.carve(1, 14, 14, 33, 33)                                   # anneau du milieu
    p.carve(1, 18, 18, 29, 29, ' ')                              # atrium
    p.carve(1, 23, 18, 24, 29)                                   # passerelle nord-sud
    p.carve(1, 22, 10, 25, 13, ' ')                              # puits nord (tremplin de la salle du Rail)
    # salles d'armure et leurs couloirs
    p.mirror_x(1, 4, 19, 11, 28)
    p.mirror_x(1, 12, 22, 13, 25)
    # salle Roquette (sud) : couloir étroit en chicane depuis l'anneau, puits du tremplin
    p.carve(1, 18, 38, 29, 44)
    p.carve(1, 18, 34, 19, 37)
    p.carve(1, 22, 34, 25, 37, ' ')

    # ================= niveau 2 (+8 m)
    p.carve(2, 16, 16, 31, 31)                                   # balcon
    p.carve(2, 18, 18, 29, 29, ' ')                              # atrium
    p.carve(2, 18, 23, 29, 24)                                   # passerelle est-ouest
    p.carve(2, 22, 10, 25, 13, ' ')                              # puits nord
    p.carve(2, 22, 34, 25, 41, ' ')                              # puits sud (ouvert jusqu'au plafond au-dessus de l'entrée de la salle Roquette)
    # anneau haut et salles d'angle
    p.carve(2, 1, 1, 46, 2); p.carve(2, 1, 45, 46, 46); p.carve(2, 1, 1, 2, 46); p.carve(2, 45, 1, 46, 46)
    p.mirror_xz(2, 1, 1, 5, 5)
    # liaisons balcon → anneau haut : est et ouest, deux au sud
    p.carve(2, 3, 23, 15, 24); p.carve(2, 32, 23, 44, 24)
    p.mirror_x(2, 19, 32, 20, 44)
    # salle du Rail : deux portes sur l'anneau haut
    p.carve(2, 19, 4, 28, 9)
    p.carve(2, 20, 3, 21, 3); p.carve(2, 26, 3, 27, 3)

    # ================= escaliers
    p.stair(18, 23, 21, 24, 0, 'O')                              # atrium → anneau du milieu (ouest)
    p.stair(26, 23, 29, 24, 0, 'E')                              # atrium → anneau du milieu (est)
    p.stair(8, 25, 9, 28, 1, 'N')                                # salle d'armure ouest → liaison ouest du niveau +8
    p.stair(38, 25, 39, 28, 1, 'N')                              # salle d'armure est → liaison est
    p.stair(6, 31, 7, 34, 0, 'N')                                # renfoncement du couloir ouest → salle d'armure ouest (par le sud)
    p.carve(1, 6, 29, 7, 30, '.')
    p.stair(40, 31, 41, 34, 0, 'N')
    p.carve(1, 40, 29, 41, 30, '.')
    return p


# ------------------------------------------------------------------ volumes pleins

def columns(p):
    """Pour chaque case : liste des intervalles pleins (bas, haut) en mètres, fusionnés."""
    out = {}
    for r in range(N):
        for c in range(N):
            iv = []
            for l in range(3):
                ch = p.g[l][r][c]
                base = l * LEVEL
                if ch == '#':
                    iv.append((base, base + LEVEL))
                elif ch == '.' and l > 0:
                    iv.append((base - SLAB, base))
            iv.sort()
            merged = []
            for a, b in iv:
                if merged and a <= merged[-1][1] + 1e-6: merged[-1] = (merged[-1][0], max(b, merged[-1][1]))
                else: merged.append((a, b))
            out[(c, r)] = tuple(merged)
    # les marches sont posées à part : la case d'un escalier n'a pas de dalle sous elle au niveau de l'escalier
    return out


def rects_by_profile(cols):
    """Rectangles de cases qui ont exactement les mêmes intervalles (fusion au plus large puis au plus haut)."""
    seen = set()
    out = []
    for r in range(N):
        for c in range(N):
            if (c, r) in seen: continue
            prof = cols[(c, r)]
            if not prof: seen.add((c, r)); continue
            c1 = c
            while c1 + 1 < N and (c1 + 1, r) not in seen and cols[(c1 + 1, r)] == prof: c1 += 1
            r1 = r
            while r1 + 1 < N and all((k, r1 + 1) not in seen and cols[(k, r1 + 1)] == prof for k in range(c, c1 + 1)): r1 += 1
            for rr in range(r, r1 + 1):
                for k in range(c, c1 + 1): seen.add((k, rr))
            out.append((c, r, c1, r1, prof))
    return out


# ------------------------------------------------------------------ contenu : tremplins, téléporteurs, butin, apparitions

def content():
    pads = [
        # (départ x, y, z), (cible x, y, z), rayon, durée
        ((-7.0, 0.0, -7.0), (-15.0, 4.0, -15.0), 1.2, None),     # atrium → anneau du milieu (nord-ouest)
        ((7.0, 0.0, 7.0), (15.0, 4.0, 15.0), 1.2, None),         # atrium → anneau du milieu (sud-est)
        ((7.0, 0.0, -7.0), (14.0, 8.0, -14.0), 1.2, None),       # atrium → balcon (nord-est)
        ((-7.0, 0.0, 7.0), (-14.0, 8.0, 14.0), 1.2, None),       # atrium → balcon (sud-ouest)
        ((0.0, 0.0, -21.0), (0.0, 8.0, -34.0), 1.3, None),       # axe nord → salle du Rail
        ((0.0, 0.0, 25.0), (0.0, 4.0, 35.0), 1.3, None),         # axe sud → salle Roquette
    ]
    d = math.sqrt(0.5) * 3.5
    teleporters = [
        # A : salle sud-ouest (sol) ↔ angle nord-est (anneau haut) ; B : salle sud-est ↔ angle nord-ouest
        dict(name='A_Sol', pos=(-28.0, 0.0, 31.0), exit=(-28.0, 0.0, 27.5), yaw=0.0, color=(0.75, 0.35, 1.0)),
        dict(name='A_Haut', pos=(41.0, 8.0, -41.0), exit=(41.0 - d, 8.0, -41.0 + d), yaw=math.radians(135), color=(0.75, 0.35, 1.0)),
        dict(name='B_Sol', pos=(28.0, 0.0, 31.0), exit=(28.0, 0.0, 27.5), yaw=0.0, color=(0.3, 0.75, 1.0)),
        dict(name='B_Haut', pos=(-41.0, 8.0, -41.0), exit=(-41.0 + d, 8.0, -41.0 + d), yaw=math.radians(-135), color=(0.3, 0.75, 1.0)),
    ]
    # chaque téléporteur envoie à la sortie de son partenaire
    pair = {'A_Sol': 'A_Haut', 'A_Haut': 'A_Sol', 'B_Sol': 'B_Haut', 'B_Haut': 'B_Sol'}
    by = {t['name']: t for t in teleporters}
    for t in teleporters:
        o = by[pair[t['name']]]
        t['dest'], t['dest_yaw'] = o['exit'], o['yaw']

    EPIC, LEG = 7, 8                     # LootPoint.QualityEnum : QueÉpique, QueLégendaire
    loot = [
        # (objet, x, y, z, qualité, quantité)
        ('sniper', 0.0, 8.0, -37.0, LEG, 0), ('ammo_sniper', 1.2, 8.0, -37.0, 0, 20),
        ('lmg63', 0.0, 4.0, 38.0, LEG, 0), ('ammo_rifle', 1.2, 4.0, 38.0, 0, 60),
        ('ak', 0.0, 8.0, 0.0, EPIC, 0), ('ammo_rifle', 1.4, 8.0, 0.0, 0, 0),
        ('ak', -30.0, 8.0, 0.0, EPIC, 0), ('ammo_rifle', -31.2, 8.0, 0.0, 0, 0),
        ('ak', 30.0, 8.0, 0.0, EPIC, 0), ('ammo_rifle', 31.2, 8.0, 0.0, 0, 0),
        ('smg45', -17.0, 0.0, 13.0, EPIC, 0), ('ammo_9mm', -18.2, 0.0, 12.0, 0, 0),
        ('smg45', 17.0, 0.0, -13.0, EPIC, 0), ('ammo_9mm', 18.2, 0.0, -12.0, 0, 0),
        ('sawnoff', 0.0, 4.0, 0.0, EPIC, 0), ('ammo_shell', 0.0, 4.0, 1.4, 0, 0),
        ('sawnoff', 0.0, 0.0, -38.0, EPIC, 0), ('ammo_shell', 1.2, 0.0, -38.0, 0, 0),
        # armes supplémentaires : pistolets C19 (holo) sous les salles d'armure, SMG sur l'anneau haut, AK et LMG sur l'anneau du milieu, fusil à pompe au sud
        ('c19', -28.0, 0.0, 2.0, EPIC, 0), ('ammo_9mm', -29.2, 0.0, 2.0, 0, 0),
        ('c19', 28.0, 0.0, -2.0, EPIC, 0), ('ammo_9mm', 29.2, 0.0, -2.0, 0, 0),
        ('smg45', -12.0, 8.0, 44.0, EPIC, 0), ('ammo_9mm', -13.2, 8.0, 44.0, 0, 0),
        ('smg45', 12.0, 8.0, -44.0, EPIC, 0), ('ammo_9mm', 13.2, 8.0, -44.0, 0, 0),
        ('ak', 0.0, 4.0, 17.0, EPIC, 0), ('ammo_rifle', 1.2, 4.0, 17.0, 0, 0),
        ('lmg63', 0.0, 4.0, -17.0, EPIC, 0), ('ammo_rifle', 1.2, 4.0, -17.0, 0, 0),
        ('sawnoff', 0.0, 0.0, 38.0, EPIC, 0), ('ammo_shell', 1.2, 0.0, 38.0, 0, 0),
        # munitions de pistolet dans les couloirs
        ('ammo_9mm', -38.0, 0.0, -10.0, 0, 0), ('ammo_9mm', 38.0, 0.0, 10.0, 0, 0), ('ammo_9mm', -10.0, 0.0, 38.0, 0, 0), ('ammo_9mm', 10.0, 0.0, -38.0, 0, 0),
        ('ammo_shell', -38.0, 0.0, 30.0, 0, 0), ('ammo_rifle', 38.0, 0.0, -30.0, 0, 0),
        # soins : salles nord (grands), salles sud et grande salle (petits)
        ('heal_big', -28.0, 0.0, -28.0, 0, 0), ('heal_big', 28.0, 0.0, 28.0 - 56.0, 0, 0),
        ('heal_small', -25.0, 0.0, 25.0, 0, 0), ('heal_small', 25.0, 0.0, 25.0, 0, 0),
        ('heal_small', -10.0, 0.0, 6.0, 0, 0), ('heal_small', 10.0, 0.0, -6.0, 0, 0),
        ('heal_small', -42.0, 8.0, 42.0, 0, 0), ('heal_small', 42.0, 8.0, 42.0, 0, 0),
        # boucliers : grands dans les salles d'armure, petits sur l'anneau haut et dans la salle Roquette
        ('shield_big', -36.0, 4.0, -8.0, 0, 0), ('shield_big', 36.0, 4.0, 8.0, 0, 0),
        ('shield_small', -45.0, 8.0, -32.0, 0, 0), ('shield_small', 45.0, 8.0, -32.0, 0, 0),
        ('shield_small', -16.0, 4.0, 0.0, 0, 0), ('shield_small', 16.0, 4.0, 0.0, 0, 0),
        ('shield_small', 8.0, 4.0, 40.0, 0, 0),
    ]
    spawns = [
        (-28.0, 0, -26.0), (28.0, 0, -26.0), (-38.0, 0, -17.0), (38.0, 0, 17.0), (-22.0, 0, 38.0), (22.0, 0, 38.0),
        (-36.0, 4, -6.0), (36.0, 4, 6.0), (-8.0, 4, -18.0), (8.0, 4, 18.0),
        (-24.0, 8, -45.0), (24.0, 8, 45.0), (-45.0, 8, 22.0), (45.0, 8, -22.0), (-39.0, 8, 39.0), (39.0, 8, 39.0),
    ]
    return pads, teleporters, loot, spawns


# ------------------------------------------------------------------ trajectoires des tremplins

def boxes_of(p):
    """Boîtes pleines (x0, y0, z0, x1, y1, z1) : structure, marches, garde-corps, plafond — comme sur le serveur."""
    out = []
    for c, r, c1, r1, prof in rects_by_profile(columns(p)):
        for a, b in prof:
            out.append((X(c), a, X(r), X(c1 + 1), b, X(r1 + 1)))
    for s in stair_boxes(p): out.append(s)
    for rb in rail_boxes(p): out.append(rb)
    out.append((X(0), CEIL, X(0), X(N), CEIL + 0.6, X(N)))
    return out


def stair_boxes(p):
    out = []
    for c0, r0, c1, r1, l, up in p.stairs:
        x0, x1, z0, z1 = X(c0), X(c1 + 1), X(r0), X(r1 + 1)
        base = l * LEVEL
        n = 16
        for k in range(n):
            top = base + LEVEL * (k + 1) / n
            if up == 'N': out.append((x0, base, z1 - (k + 1) * (z1 - z0) / n, x1, top, z1 - k * (z1 - z0) / n))
            elif up == 'S': out.append((x0, base, z0 + k * (z1 - z0) / n, x1, top, z0 + (k + 1) * (z1 - z0) / n))
            elif up == 'E': out.append((x0 + k * (x1 - x0) / n, base, z0, x0 + (k + 1) * (x1 - x0) / n, top, z1))
            else: out.append((x1 - (k + 1) * (x1 - x0) / n, base, z0, x1 - k * (x1 - x0) / n, top, z1))
    return out


RAIL_H, RAIL_T = 1.1, 0.16


def rail_boxes(p):
    """Garde-corps de 1,1 m le long des bords des sols (+4, +8) qui donnent sur le vide, sauf en haut des escaliers."""
    out = []
    stair_cells = set()
    for c0, r0, c1, r1, l, up in p.stairs:
        for c in range(c0, c1 + 1):
            for r in range(r0, r1 + 1): stair_cells.add((l + 1, c, r))
    for l in (1, 2):
        y = l * LEVEL
        for r in range(N):
            for c in range(N):
                if p.g[l][r][c] != '.': continue
                for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nc, nr = c + dc, r + dr
                    if p.at(l, nc, nr) != ' ': continue
                    if (l, (c, r), (nc, nr)) in p.no_rail: continue
                    x0, z0, x1, z1 = X(c), X(r), X(c + 1), X(r + 1)
                    if dc == 1: out.append((x1 - RAIL_T, y, z0, x1, y + RAIL_H, z1))
                    elif dc == -1: out.append((x0, y, z0, x0 + RAIL_T, y + RAIL_H, z1))
                    elif dr == 1: out.append((x0, y, z1 - RAIL_T, x1, y + RAIL_H, z1))
                    else: out.append((x0, y, z0, x1, y + RAIL_H, z0 + RAIL_T))
    return merge_rails(out)


def merge_rails(rails):
    """Fusionne les tronçons de garde-corps alignés et jointifs."""
    rails = sorted(set(rails))
    changed = True
    while changed:
        changed = False
        out = []
        used = [False] * len(rails)
        for i, a in enumerate(rails):
            if used[i]: continue
            cur = list(a)
            for j in range(i + 1, len(rails)):
                if used[j]: continue
                b = rails[j]
                if cur[1] != b[1] or cur[4] != b[4]: continue
                if cur[0] == b[0] and cur[3] == b[3] and abs(cur[5] - b[2]) < 1e-6: cur[5] = b[5]; used[j] = True; changed = True
                elif cur[2] == b[2] and cur[5] == b[5] and abs(cur[3] - b[0]) < 1e-6: cur[3] = b[3]; used[j] = True; changed = True
            out.append(tuple(cur))
        rails = sorted(out)
    return rails


def arc_ok(start, target, T, boxes):
    """Le corps (cylindre de 0,4 m, 1,8 m) traverse-t-il une boîte pendant le vol ? Même cloche que la simulation."""
    sx, sy, sz = start; tx, ty, tz = target
    vx, vz = (tx - sx) / T, (tz - sz) / T
    vy = (ty - sy + 0.5 * GRAVITY * T * T) / T
    steps = 80
    for i in range(1, steps):
        t = T * i / steps
        x, y, z = sx + vx * t, sy + vy * t - 0.5 * GRAVITY * t * t, sz + vz * t
        if t > T * 0.85 and abs(y - ty) < 0.35: break    # posé
        for b in boxes:
            if x + 0.4 > b[0] and x - 0.4 < b[3] and z + 0.4 > b[2] and z - 0.4 < b[5] and y + 1.8 > b[1] and y + 0.05 < b[4]:
                return False, (round(x, 2), round(y, 2), round(z, 2), b)
    return True, None


def choose_times(p, pads):
    """Durée de vol de chaque tremplin : le milieu de la plage de durées qui passent depuis le centre ET depuis le bord du disque
    (le joueur peut monter n'importe où dessus : la simulation vise la cible depuis sa vraie position)."""
    boxes = boxes_of(p)
    out = []
    for start, target, radius, T in pads:
        starts = [start] + [(start[0] + math.cos(a) * radius * 0.85, start[1], start[2] + math.sin(a) * radius * 0.85) for a in [k * math.pi / 4 for k in range(8)]]
        def good(t): return all(arc_ok(s0, target, t, boxes)[0] for s0 in starts)
        if T:
            out.append((start, target, radius, T, good(T), None)); continue
        runs, cur = [], []
        for k in range(30, 220):
            t = k / 100
            if good(t): cur.append(t)
            elif cur: runs.append(cur); cur = []
        if cur: runs.append(cur)
        if runs:
            best = max(runs, key=len)
            out.append((start, target, radius, best[len(best) // 2], True, None))
        else:
            out.append((start, target, radius, 1.2, False, arc_ok(start, target, 1.2, boxes)[1]))
    return out


# ------------------------------------------------------------------ lumières

def lights(p):
    """Plafonniers : un tous les 9 m environ sur chaque niveau, au-dessus des sols praticables."""
    out = []
    spacing = 9.0
    colors = {0: (1.0, 0.86, 0.7), 1: (0.78, 0.88, 1.0), 2: (0.9, 0.8, 1.0)}
    for l in range(3):
        placed = []
        for r in range(N):
            for c in range(N):
                if p.g[l][r][c] != '.': continue
                x, z = cx(c), cx(r)
                if any((x - a) ** 2 + (z - b) ** 2 < spacing ** 2 for a, b in placed): continue
                placed.append((x, z))
                above = p.at(l + 1, c, r) if l < 2 else '#'
                y = l * LEVEL + (3.35 if above != ' ' else 3.6)
                out.append((x, y, z, colors[l], 3.2, 11.0))
    return out


# ------------------------------------------------------------------ scène Godot

def tscn(p, pads, teleporters, loot, spawns, export=False):
    o = []
    res = [
        ('Script', 'res://scripts/online/SimpleMap.cs', 'map'),
        ('Script', 'res://scripts/online/CoverBox.cs', 'cover'),
        ('Script', 'res://scripts/online/MapMarker.cs', 'marker'),
        ('Script', 'res://scripts/online/ArenaJumpPad.cs', 'pad'),
        ('Script', 'res://scripts/online/ArenaTeleporter.cs', 'tele'),
        ('PackedScene', 'res://scenes/loot/ObjetButin.tscn', 'loot'),
        ('Material', ART + 'surfaces.tres', 'm_surf'),
        ('Material', ART + 'stairs.tres', 'm_stairs'),
        ('Material', ART + 'rail.tres', 'm_rail'),
        ('Material', ART + 'glow_orange.tres', 'm_orange'),
        ('Material', ART + 'glow_cyan.tres', 'm_cyan'),
        ('Material', ART + 'glow_purple.tres', 'm_purple'),
        ('Material', ART + 'lamp.tres', 'm_lamp'),
        ('Material', ART + 'core.tres', 'm_core'),
        ('Material', ART + 'skylight.tres', 'm_sky'),
        ('Environment', ART + 'relais_env.tres', 'env'),
        ('PackedScene', 'res://assets/environement/Modular SciFi MegaKit[Standard]/glTF/Props/Prop_Crate3.gltf', 'crate'),
    ]
    o.append('[gd_scene format=4]\n')
    for t, path, key in res:
        o.append(f'[ext_resource type="{t}" path="{path}" id="{key}"]')
    o.append('')
    o.append('[node name="Relais" type="Node3D"]')
    o.append('script = ExtResource("map")')
    o.append(f'Size = Vector2({N * CELL}, {N * CELL})')
    o.append('GroundColor = Color(0.35, 0.36, 0.4, 1)')
    o.append('GroundMaterial = ExtResource("m_surf")\n')
    o.append('[node name="WorldEnvironment" type="WorldEnvironment" parent="."]')
    o.append('environment = ExtResource("env")\n')
    o.append('[node name="SpawnPoint" type="Marker3D" parent="."]')
    o.append(f'position = Vector3({spawns[0][0]}, {spawns[0][1] + 0.2}, {spawns[0][2]})\n')

    def box(parent, name, b, mat, solid=True, model=None, cast=True):
        x0, y0, z0, x1, y1, z1 = b
        o.append(f'[node name="{name}" type="StaticBody3D" parent="{parent}"]')
        o.append(f'position = Vector3({round((x0 + x1) / 2, 4)}, {round(y0, 4)}, {round((z0 + z1) / 2, 4)})')
        o.append('script = ExtResource("cover")')
        o.append(f'Size = Vector3({round(x1 - x0, 4)}, {round(y1 - y0, 4)}, {round(z1 - z0, 4)})')
        if mat: o.append(f'BoxMaterial = ExtResource("{mat}")')
        if model: o.append(f'UnitModel = ExtResource("{model}")')
        if not solid: o.append('Solid = false')
        o.append('')

    o.append('[node name="Structure" type="Node3D" parent="."]\n')
    i = 0
    for c, r, c1, r1, prof in rects_by_profile(columns(p)):
        for a, b in prof:
            i += 1
            box('Structure', f'Bloc_{i:03d}', (X(c), a, X(r), X(c1 + 1), b, X(r1 + 1)), 'm_surf')
    o.append('[node name="Plafond" type="Node3D" parent="."]\n')
    box('Plafond', 'Plafond', (X(0), CEIL, X(0), X(N), CEIL + 0.6, X(N)), 'm_surf')
    o.append('[node name="Escaliers" type="Node3D" parent="."]\n')
    for k, s in enumerate(stair_boxes(p)):
        box('Escaliers', f'Marche_{k + 1:03d}', s, 'm_stairs')
    o.append('[node name="GardeCorps" type="Node3D" parent="."]\n')
    rails = rail_boxes(p)
    for k, s in enumerate(rails):
        box('GardeCorps', f'Rambarde_{k + 1:03d}', s, 'm_rail')
    # caisses (abris) dans la grande salle et les couloirs : même volume sur le serveur
    crates = [(-20.0, -17.0), (20.0, 17.0), (-17.0, 8.0), (17.0, -8.0), (-30.0, -38.0), (30.0, 38.0), (-38.0, 30.0 - 60.0), (38.0, 30.0)]
    for k, (x, z) in enumerate(crates):
        box('Structure', f'Caisse_{k + 1:02d}', (x - 0.9, 0.0, z - 0.9, x + 0.9, 1.25, z + 0.9), None, model='crate')

    if not export:
        # décor sans collision : bande lumineuse sur le dessus des garde-corps, cœur de l'atrium, verrière
        o.append('[node name="Decor" type="Node3D" parent="."]\n')
        for k, (x0, y0, z0, x1, y1, z1) in enumerate(rails):
            lvl = int(round(y0 / LEVEL))
            box('Decor', f'Bande_{k + 1:03d}', (x0 + 0.03, y1, z0 + 0.03, x1 - 0.03, y1 + 0.035, z1 - 0.03), 'm_cyan' if lvl == 1 else 'm_purple', solid=False, cast=False)
        box('Decor', 'Coeur_Socle', (-1.6, 0.0, -1.6, 1.6, 0.5, 1.6), 'm_rail', solid=True)
        box('Decor', 'Coeur', (-0.7, 0.5, -0.7, 0.7, 3.6, 0.7), 'm_core', solid=False, cast=False)
        box('Decor', 'Coeur_Haut', (-0.7, 4.0, -0.7, 0.7, 7.6, 0.7), 'm_core', solid=False, cast=False)
        box('Decor', 'Coeur_Ciel', (-0.7, 8.0, -0.7, 0.7, CEIL, 0.7), 'm_core', solid=False, cast=False)
        box('Decor', 'Verriere', (X(18) + 0.5, CEIL - 0.06, X(18) + 0.5, X(30) - 0.5, CEIL - 0.01, X(30) - 0.5), 'm_sky', solid=False, cast=False)
        # repères colorés au sol des passages (bande orange au sol aux entrées de la grande salle)
        for (x0, z0, x1, z1) in [(-4, -28.2, 4, -27.8), (-4, 27.8, 4, 28.2), (-28.2, -4, -27.8, 4), (27.8, -4, 28.2, 4)]:
            box('Decor', f'Seuil_{x0}_{z0}', (x0, 0.0, z0, x1, 0.02, z1), 'm_orange', solid=False, cast=False)

        o.append('[node name="Lumieres" type="Node3D" parent="."]\n')
        for k, (x, y, z, col, energy, rng) in enumerate(lights(p)):
            o.append(f'[node name="Lampe_{k + 1:03d}" type="OmniLight3D" parent="Lumieres"]')
            o.append(f'position = Vector3({round(x, 3)}, {round(y, 3)}, {round(z, 3)})')
            o.append(f'light_color = Color({col[0]}, {col[1]}, {col[2]}, 1)')
            o.append(f'light_energy = {energy}')
            o.append('light_specular = 0.6')
            o.append(f'omni_range = {rng}')
            o.append('omni_attenuation = 0.9\n')
            box('Lumieres', f'Plafonnier_{k + 1:03d}', (x - 0.6, y + 0.12, z - 0.18, x + 0.6, y + 0.2, z + 0.18), 'm_lamp', solid=False, cast=False)
        # l'atrium : grande lumière froide du cœur (avec ombres) et lumière chaude de la verrière
        o.append('[node name="Lumiere_Coeur" type="OmniLight3D" parent="Lumieres"]')
        o.append('position = Vector3(0, 6, 0)')
        o.append('light_color = Color(0.45, 0.8, 1, 1)')
        o.append('light_energy = 6.0')
        o.append('shadow_enabled = true')
        o.append('omni_range = 24.0\n')
        o.append('[node name="Lumiere_Verriere" type="SpotLight3D" parent="Lumieres"]')
        o.append('transform = Transform3D(1, 0, 0, 0, -4.371139e-08, 1, 0, -1, -4.371139e-08, 0, 11.7, 0)')
        o.append('light_color = Color(1, 0.9, 0.78, 1)')
        o.append('light_energy = 8.0')
        o.append('shadow_enabled = true')
        o.append('spot_range = 16.0')
        o.append('spot_angle = 55.0\n')

    o.append('[node name="Points" type="Node3D" parent="."]\n')
    for k, (x, y, z) in enumerate(spawns):
        o.append(f'[node name="Apparition_{k + 1:02d}" type="Marker3D" parent="Points"]')
        o.append(f'position = Vector3({x}, {y}, {z})')
        o.append('script = ExtResource("marker")')
        o.append('Kind = 1')
        o.append('Team = 255\n')

    o.append('[node name="Tremplins" type="Node3D" parent="."]\n')
    for k, (start, target, radius, T, ok, hit) in enumerate(pads):
        name = f'Tremplin_{k + 1}'
        o.append(f'[node name="{name}" type="Node3D" parent="Tremplins"]')
        o.append(f'position = Vector3({start[0]}, {start[1]}, {start[2]})')
        o.append('script = ExtResource("pad")')
        o.append(f'Radius = {radius}')
        o.append(f'FlightTime = {T}\n')
        o.append(f'[node name="Cible" type="Marker3D" parent="Tremplins/{name}"]')
        o.append(f'position = Vector3({target[0] - start[0]}, {target[1] - start[1]}, {target[2] - start[2]})\n')

    o.append('[node name="Teleporteurs" type="Node3D" parent="."]\n')
    for t in teleporters:
        x, y, z = t['pos']
        dx, dy, dz = t['dest']
        o.append(f'[node name="{t["name"]}" type="Node3D" parent="Teleporteurs"]')
        o.append(f'position = Vector3({x}, {y}, {z})')
        o.append('script = ExtResource("tele")')
        o.append(f'Tint = Color({t["color"][0]}, {t["color"][1]}, {t["color"][2]}, 1)\n')
        # la sortie est un nœud enfant : position et orientation dans le repère du téléporteur (non tourné)
        yaw = t['dest_yaw']
        cs, sn = math.cos(yaw), math.sin(yaw)
        o.append(f'[node name="Sortie" type="Marker3D" parent="Teleporteurs/{t["name"]}"]')
        o.append(f'transform = Transform3D({cs:.6f}, 0, {-sn:.6f}, 0, 1, 0, {sn:.6f}, 0, {cs:.6f}, {round(dx - x, 4)}, {round(dy - y, 4)}, {round(dz - z, 4)})\n')

    o.append('[node name="Butin" type="Node3D" parent="."]\n')
    for k, (item, x, y, z, quality, count) in enumerate(loot):
        o.append(f'[node name="{item}_{k + 1:02d}" parent="Butin" instance=ExtResource("loot")]')
        o.append(f'position = Vector3({x}, {y}, {z})')
        o.append('Activation = 1')
        if quality: o.append(f'Quality = {quality}')
        o.append(f'Item = "{item}"')
        if count: o.append(f'Count = {count}')
        o.append('')
    return '\n'.join(o) + '\n'


# ------------------------------------------------------------------ mini-carte

def minimap(p, pads, teleporters, loot, path, px=1024):
    from PIL import Image, ImageDraw
    s = px / (N * CELL)
    def P(x, z): return ((x + N * CELL / 2) * s, (z + N * CELL / 2) * s)
    img = Image.new('RGB', (px, px), (18, 20, 26))
    dr = ImageDraw.Draw(img, 'RGBA')
    fills = {0: (92, 96, 106, 255), 1: (58, 110, 190, 200), 2: (130, 70, 200, 190)}
    for l in range(3):
        for r in range(N):
            for c in range(N):
                if p.g[l][r][c] == '.':
                    dr.rectangle([*P(X(c), X(r)), *P(X(c + 1) - 0.01, X(r + 1) - 0.01)], fill=fills[l])
    for c0, r0, c1, r1, l, up in p.stairs:
        dr.rectangle([*P(X(c0), X(r0)), *P(X(c1 + 1), X(r1 + 1))], fill=(200, 200, 210, 230))
        x0, z0, x1, z1 = X(c0), X(r0), X(c1 + 1), X(r1 + 1)
        for k in range(1, 8):
            if up in 'NS':
                z = z0 + (z1 - z0) * k / 8; dr.line([*P(x0, z), *P(x1, z)], fill=(90, 90, 100, 255), width=1)
            else:
                x = x0 + (x1 - x0) * k / 8; dr.line([*P(x, z0), *P(x, z1)], fill=(90, 90, 100, 255), width=1)
    for b in rail_boxes(p):
        dr.rectangle([*P(b[0], b[2]), *P(b[3], b[5])], fill=(230, 230, 240, 255))
    for start, target, radius, T, ok, hit in pads:
        x, y, z = start
        dr.ellipse([*P(x - radius, z - radius), *P(x + radius, z + radius)], fill=(255, 150, 40, 255))
        dr.line([*P(x, z), *P(target[0], target[2])], fill=(255, 170, 60, 200), width=3)
    for t in teleporters:
        x, y, z = t['pos']
        col = tuple(int(v * 255) for v in t['color']) + (255,)
        dr.ellipse([*P(x - 1.2, z - 1.2), *P(x + 1.2, z + 1.2)], fill=col, outline=(255, 255, 255, 255), width=2)
    for item, x, y, z, quality, count in loot:
        if item in ('sniper', 'lmg63'): col = (255, 70, 60, 255)
        elif item in ('ak', 'smg45', 'sawnoff', 'c19', 'p2020'): col = (190, 90, 255, 255)
        elif item.startswith('heal'): col = (90, 230, 110, 255)
        elif item.startswith('shield'): col = (90, 170, 255, 255)
        else: continue
        dr.rectangle([*P(x - 0.7, z - 0.7), *P(x + 0.7, z + 0.7)], fill=col)
    img.save(path)


def validate(p, pads, teleporters, loot, spawns):
    """Chaque objet, apparition, tremplin et téléporteur doit être posé sur un sol libre de son niveau (pas dans un mur, une marche, un garde-corps ou une caisse)."""
    solids = boxes_of(p)
    errors = []
    def check(what, x, y, z, r=0.45):
        c, rr = int((x + N * CELL / 2) // CELL), int((z + N * CELL / 2) // CELL)
        l = int(round(y / LEVEL))
        if not (0 <= c < N and 0 <= rr < N) or p.g[l][rr][c] != '.': errors.append(f'{what} ({x}, {y}, {z}) : pas de sol au niveau {l} (case {c}, {rr} = {p.at(l, c, rr)!r})'); return
        for b in solids:
            if x + r > b[0] and x - r < b[3] and z + r > b[2] and z - r < b[5] and y + 1.7 > b[1] and y + 0.05 < b[4]:
                errors.append(f'{what} ({x}, {y}, {z}) : dans une boîte {tuple(round(v, 2) for v in b)}'); return
    for item, x, y, z, q, n in loot: check(item, x, y, z, 0.3)
    for x, y, z in spawns: check('apparition', x, y, z, 1.0)
    for start, target, radius, T in pads: check('tremplin', *start, radius); check('cible de tremplin', *target)
    for t in teleporters: check('téléporteur ' + t['name'], *t['pos'], 1.0); check('sortie ' + t['name'], *t['exit'])
    return errors


def main():
    p = layout()
    pads, teleporters, loot, spawns = content()
    errors = validate(p, pads, teleporters, loot, spawns)
    for e in errors: print('ERREUR', e)
    if errors: sys.exit(1)
    pads = choose_times(p, pads)
    bad = [x for x in pads if not x[4]]
    for start, target, radius, T, ok, hit in pads:
        print(f'tremplin {start} -> {target} : vol {T} s', 'OK' if ok else f'BLOQUÉ {hit}')
    if '--check' in sys.argv:
        sys.exit(1 if bad else 0)
    png = os.path.join(ROOT, 'assets', 'maps', 'relais.png')
    minimap(p, pads, teleporters, loot, png)
    print('dessin écrit', png)
    if '--minimap' in sys.argv: return
    if bad:
        print('trajectoire bloquée : scène non écrite'); sys.exit(1)
    path = os.path.join(ROOT, 'scenes', 'maps', 'Relais.tscn')
    if os.path.exists(path) and '--force' not in sys.argv:
        print(f'{path} existe déjà : non réécrite (--force pour la refaire, ce qui efface les retouches faites dans Godot)')
        return
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(tscn(p, pads, teleporters, loot, spawns))
    print('écrit', path)


if __name__ == '__main__':
    main()
