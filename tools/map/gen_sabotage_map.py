#!/usr/bin/env python3
"""Carte du Sabotage « Poste Frontière », dans l'esprit des cartes de Counter-Strike (Dust 2) et de Valorant (Bind, Ascent) :
deux sites de bombe, trois routes d'approche, des goulets d'étranglement, des rotations, des couvertures.

    python tools/map/gen_sabotage_map.py            # écrit scenes/maps/Sabotage.tscn (écrase l'ancienne) + un plan de contrôle (PNG)
    python tools/map/gen_sabotage_map.py --check    # seulement les vérifications (accès, distances) et le plan

La carte est dessinée sur une grille de 36 x 34 cases de 4 m (144 m x 136 m). Tout ce qui n'est pas creusé est un bloc plein de 6 m de haut ; les rues,
cours et sites sont des rectangles creusés. Le plan ci-dessous donne les noms des lieux (à utiliser dans les messages et les annonces) :

        Nord = -Z · Est = +X
        ┌──────────────────────────────────────────────┐
        │  Site A  ◄── Couloir ──  Apparition  ── Couloir ──►  Site B   │
        │ (Hangar)     nord       défenseurs        nord       (Entrepôt)│
        │   ▲  ◄ Short A ─  Place centrale  ─ Short B ►  ▲        │
        │   │              ▲ Mid (rue centrale)           │ Tunnel B │
        │  Long A          │                              │ (couvert)│
        │   └─────  Cour d'assaut ─── Apparition attaquants ───┘     │
        └──────────────────────────────────────────────┘

Ne pas relancer sur une carte retouchée dans Godot : ce script la réécrirait (c'est le but ici : « refaire la carte »).
"""
import math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_mode_maps import Map, TEAM, SITE, COVER, LOW, WALL

CELL, W, H = 4.0, 36, 34
X0, Z0 = -W * CELL / 2, -H * CELL / 2           # coin nord-ouest
WALL_H = 6.0

SAND = (0.74, 0.66, 0.52)
SAND_DARK = (0.62, 0.55, 0.43)
A_COL = (0.82, 0.52, 0.30)          # côté du site A : ocre
B_COL = (0.36, 0.50, 0.72)          # côté du site B : bleu acier
T_COL = (0.68, 0.34, 0.30)          # apparition des attaquants : rouille
CT_COL = (0.30, 0.40, 0.58)         # apparition des défenseurs : acier
CRATE = (0.50, 0.38, 0.24)
METAL = (0.42, 0.46, 0.52)

SITE_A = (-48.0, -30.0)
SITE_B = (48.0, -30.0)
SITE_R = 9.0


def cx(c): return X0 + CELL * c + CELL / 2
def cz(r): return Z0 + CELL * r + CELL / 2


class Grid:
    def __init__(self):
        self.g = [['#'] * W for _ in range(H)]

    def carve(self, c0, r0, c1, r1, ch='.'):
        for r in range(r0, r1 + 1):
            for c in range(c0, c1 + 1):
                self.g[r][c] = ch

    def solid(self, c0, r0, c1=None, r1=None):
        c1 = c0 if c1 is None else c1
        r1 = r0 if r1 is None else r1
        for r in range(r0, r1 + 1):
            for c in range(c0, c1 + 1):
                self.g[r][c] = '#'

    def rects(self):
        """Blocs pleins : rectangles de cases, fusionnés au plus large puis au plus haut."""
        seen = [[False] * W for _ in range(H)]
        out = []
        for r in range(H):
            for c in range(W):
                if self.g[r][c] != '#' or seen[r][c]:
                    continue
                c1 = c
                while c1 + 1 < W and self.g[r][c1 + 1] == '#' and not seen[r][c1 + 1]:
                    c1 += 1
                r1 = r
                while r1 + 1 < H and all(self.g[r1 + 1][k] == '#' and not seen[r1 + 1][k] for k in range(c, c1 + 1)):
                    r1 += 1
                for rr in range(r, r1 + 1):
                    for k in range(c, c1 + 1):
                        seen[rr][k] = True
                out.append((c, r, c1, r1))
        return out


def layout():
    g = Grid()
    # ---- apparitions
    g.carve(14, 30, 21, 33)            # attaquants (sud)
    g.carve(16, 29, 19, 29)            # porte de leur apparition vers la cour
    g.carve(12, 1, 23, 4)              # défenseurs (nord)
    # ---- cour d'assaut et les trois routes
    g.carve(1, 23, 34, 28)             # cour d'assaut
    g.carve(1, 14, 4, 22)              # Long A : grand couloir ouvert (voie du sniper)
    g.carve(16, 16, 19, 22)            # Mid : rue centrale
    g.carve(28, 16, 30, 22)            # Tunnel B (couvert)
    # ---- sites et place centrale
    g.carve(2, 5, 9, 13)               # site A (Hangar)
    g.carve(26, 5, 33, 13)             # site B (Entrepôt)
    g.carve(13, 8, 22, 15)             # place centrale
    g.carve(28, 14, 30, 15)            # sortie du tunnel B vers le site B
    # ---- liaisons
    g.carve(10, 10, 12, 11)            # Short A : place -> site A
    g.carve(23, 10, 25, 11)            # Short B : place -> site B
    g.carve(17, 5, 18, 7)              # portes du milieu : apparition défenseurs -> place
    g.carve(8, 3, 11, 4)               # couloir nord vers A
    g.carve(24, 3, 27, 4)              # couloir nord vers B
    g.carve(5, 18, 15, 18)             # porte « long-mid » : Long A <-> Mid
    g.carve(20, 19, 27, 19)            # couloir est : Mid <-> Tunnel B
    # ---- le kiosque au centre de la place
    g.solid(17, 11, 18, 12)
    return g


# matières (voir CoverBox.cs : Surface) et style de bâtiment
BETON, ASPHALTE, ROCHE, TERRE, HERBE, METALMAT, CAISSE = 1, 2, 3, 4, 5, 6, 7
BATIMENT = 1


def build():
    g = layout()
    m = Map('Sabotage', (W * CELL, H * CELL), (0.95, 0.88, 0.76))
    m.ground_surface = TERRE

    # ---- les bâtiments : blocs pleins (béton ; grès côté A), avec socle, corniche et fenêtres. Teinte selon le quartier.
    def look(x, z):
        def near(p, d): return math.hypot(x - p[0], z - p[1]) < d
        if near(SITE_A, 30): return ROCHE, (1.0, 0.86, 0.74)
        if near(SITE_B, 30): return BETON, (0.74, 0.84, 1.0)
        if z > 40 and abs(x) < 40: return BETON, (1.0, 0.78, 0.7)
        if z < -40 and abs(x) < 34: return BETON, (0.7, 0.78, 0.95)
        return BETON, (0.96, 0.93, 0.86)
    for c0, r0, c1, r1 in g.rects():
        x0, x1 = X0 + CELL * c0, X0 + CELL * (c1 + 1)
        z0, z1 = Z0 + CELL * r0, Z0 + CELL * (r1 + 1)
        xm, zm = (x0 + x1) / 2, (z0 + z1) / 2
        surface, tint = look(xm, zm)
        m.box(xm, zm, round(x1 - x0, 2), WALL_H, round(z1 - z0, 2), tint, surface=surface, style=BATIMENT)

    # ---- sol : dalles et plaques d'herbe (visuelles seulement : ni collision ni obstacle exporté)
    def slab(x0, z0, x1, z1, surface, tint, h):
        m.box((x0 + x1) / 2, (z0 + z1) / 2, x1 - x0, h, z1 - z0, tint, surface=surface, solid=False)
    slab(-68, 24, 68, 48, ASPHALTE, (0.92, 0.92, 0.95), 0.02)                    # cour d'assaut
    slab(-16, 48, 16, 68, BETON, (1.0, 0.9, 0.84), 0.03)                          # apparition des attaquants
    slab(-24, -64, 24, -48, BETON, (0.84, 0.9, 1.0), 0.03)                        # apparition des défenseurs
    slab(-8, -16, 8, 24, ASPHALTE, (0.92, 0.92, 0.95), 0.02)                      # rue du milieu
    slab(-20, -48, 20, -16, BETON, (0.95, 0.92, 0.86), 0.03)                      # place centrale
    slab(-64, -48, -32, -12, BETON, (1.0, 0.88, 0.76), 0.03)                      # site A
    slab(32, -48, 64, -12, BETON, (0.82, 0.9, 1.0), 0.03)                         # site B
    slab(40, -4, 52, 24, BETON, (0.72, 0.74, 0.78), 0.03)                         # tunnel B
    for x0, z0, x1, z1 in [(-64, -24, -54, -12), (-40, -48, -32, -40), (-68, -12, -62, 24), (-54, 8, -52, 24)]:
        slab(x0, z0, x1, z1, HERBE, (0.85, 0.95, 0.75), 0.045)                    # herbe : coins du site A et bord de Long A
    for x0, z0, x1, z1 in [(54, -24, 64, -12), (32, -48, 40, -40), (-20, -20, -14, -16), (14, -48, 20, -42)]:
        slab(x0, z0, x1, z1, HERBE, (0.85, 0.95, 0.75), 0.045)                    # coins du site B et de la place

    # ---- toit du tunnel B (on y entre de plein pied)
    m.box(46.0, 11.0, 12.0, 0.8, 22.0, (0.62, 0.62, 0.66), y=3.4, surface=BETON)

    # ---- plateformes : « Heaven » (A) et le balcon (B), avec leurs marches
    METAL_T = (0.8, 0.86, 0.92)
    def stairs(x, z, direction, width, n=3, run=1.5, rise=0.45):
        """Marches qui montent à partir de (x, z) en allant vers `direction` (-1 = nord, +1 = sud) ; la plus basse est la plus loin."""
        for i in range(n):
            h = rise * (n - i)
            m.box(x, z + direction * (run * i + run / 2), width, h, run, METAL_T, surface=METALMAT)
    m.box(-60.0, -45.0, 8.0, 1.8, 6.0, METAL_T, surface=METALMAT)
    stairs(-60.0, -42.0, +1, 6.0)
    m.box(60.0, -45.0, 8.0, 1.8, 6.0, METAL_T, surface=METALMAT)
    stairs(60.0, -42.0, +1, 6.0)

    def crate(x, z, sx, sy, sz):
        m.box(x, z, sx, sy, sz, (1.0, 1.0, 1.0), surface=CAISSE)
    # ---- site A : le « défaut » (caisses autour du point de pose)
    cxA, czA = SITE_A
    for dx, dz, sx, sy, sz in [(-7, -8, 3, 2.5, 3), (7, -9, 2, 2.5, 6), (-8, 5, 4, 1.4, 2), (9, 4, 3, 2.5, 3), (0, 9, 6, 1.2, 2), (-2, -13, 3, 2.5, 3)]:
        crate(cxA + dx, czA + dz, sx, sy, sz)
    # ---- site B : disposition différente (pas un miroir)
    cxB, czB = SITE_B
    for dx, dz, sx, sy, sz in [(7, -8, 3, 2.5, 3), (-7, -3, 2, 2.5, 7), (8, 6, 4, 1.4, 2), (-9, 8, 3, 2.5, 3), (1, 10, 3, 1.2, 4), (3, -13, 4, 2.5, 3)]:
        crate(cxB + dx, czB + dz, sx, sy, sz)
    # ---- Long A, Mid, place centrale, cour d'assaut : couvertures
    for x, z, sx, sy, sz in [(-60, 18, 3, 2.5, 3), (-56, 4, 3, 1.4, 6), (-62, -6, 3, 2.5, 3), (-55, 22, 4, 1.2, 2),
                             (-3, 18, 3, 1.2, 3), (3, 8, 3, 1.2, 3), (-3, -2, 3, 1.2, 3),
                             (-12, -10, 4, 1.2, 2), (12, -10, 4, 1.2, 2), (-12, -30, 3, 2.5, 3), (12, -30, 3, 2.5, 3),
                             (-30, 33, 4, 2.5, 4), (-8, 30, 3, 1.4, 3), (10, 32, 3, 1.4, 3), (30, 33, 4, 2.5, 4), (-44, 40, 3, 1.2, 3), (44, 40, 3, 1.2, 3)]:
        crate(x, z, sx, sy, sz)
    # ---- Tunnel B : colonnes
    for x, z, sx, sy, sz in [(43, 2, 2, 3, 2), (49, 8, 2, 3, 2), (43, 14, 2, 3, 2), (49, 20, 2, 3, 2)]:
        m.box(x, z, sx, sy, sz, METAL_T, surface=METALMAT)

    # ---- apparitions : attaquants (camp 1) au sud, défenseurs (camp 0) au nord
    for x in (-14, -7, 0, 7, 14):
        for z in (58, 64):
            m.marker(TEAM, x, z, 1)
        for z in (-60, -53):
            m.marker(TEAM, x, z, 0)
    # ---- sites de bombe
    m.marker(SITE, SITE_A[0], SITE_A[1], 255, 0, SITE_R)
    m.marker(SITE, SITE_B[0], SITE_B[1], 255, 1, SITE_R)
    return m


# ------------------------------------------------------------------ contrôles et plan

def blocked_map(m, res=1.0, radius=0.45, max_h=0.6):
    """Grille 1 m : vrai = un obstacle plein (plus haut qu'une marche) à moins d'un rayon de joueur."""
    w, d = int(W * CELL / res), int(H * CELL / res)
    blocked = [[False] * w for _ in range(d)]
    for x, z, sx, sy, sz, _c, y, _surface, _style, solid in m.boxes:
        if not solid or y > 0.5 or sy <= max_h:            # une dalle, un toit ou une marche ne gêne pas
            continue
        i0 = int((x - sx / 2 - radius - X0) / res); i1 = int((x + sx / 2 + radius - X0) / res)
        j0 = int((z - sz / 2 - radius - Z0) / res); j1 = int((z + sz / 2 + radius - Z0) / res)
        for j in range(max(0, j0), min(d - 1, j1) + 1):
            for i in range(max(0, i0), min(w - 1, i1) + 1):
                blocked[j][i] = True
    return blocked


def distances(blocked, start, res=1.0):
    from collections import deque
    d, w = len(blocked), len(blocked[0])
    si, sj = int((start[0] - X0) / res), int((start[1] - Z0) / res)
    dist = {(si, sj): 0.0}
    q = deque([(si, sj)])
    while q:
        i, j = q.popleft()
        for di, dj, cost in ((1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)):
            ni, nj = i + di, j + dj
            if not (0 <= ni < w and 0 <= nj < d) or blocked[nj][ni] or (ni, nj) in dist:
                continue
            if di and dj and (blocked[j][ni] or blocked[nj][i]):
                continue
            dist[(ni, nj)] = dist[(i, j)] + cost * res
            q.append((ni, nj))
    return dist


def check(m):
    ok = True
    blocked = blocked_map(m)
    def at(p): return (int((p[0] - X0)), int((p[1] - Z0)))
    spawns = {s: [(mk[1], mk[2]) for mk in m.markers if mk[0] == TEAM and mk[3] == s] for s in (0, 1)}
    sites = [(mk[1], mk[2]) for mk in m.markers if mk[0] == SITE]
    for side, pts in spawns.items():
        for p in pts:
            i, j = at(p)
            if blocked[j][i]: print(f'ERREUR : apparition {p} (camp {side}) dans un obstacle'); ok = False
    for s in sites:
        i, j = at(s)
        if blocked[j][i]: print(f'ERREUR : site {s} dans un obstacle'); ok = False
    t0, ct0 = spawns[1][0], spawns[0][0]
    for name, origin in (('attaquants', t0), ('défenseurs', ct0)):
        dist = distances(blocked, origin)
        for k, s in enumerate(sites):
            v = dist.get(at(s))
            print(f'  {name} -> site {"AB"[k]} : {"inaccessible" if v is None else f"{v:.0f} m"}')
            if v is None: ok = False
        for side, pts in spawns.items():
            for p in pts:
                if at(p) not in dist: print(f'ERREUR : {p} inaccessible depuis les {name}'); ok = False
    return ok


def preview(m, path):
    from PIL import Image, ImageDraw
    k = 5
    img = Image.new('RGB', (int(W * CELL * k), int(H * CELL * k)), (110, 100, 82))
    dr = ImageDraw.Draw(img)
    def px(x, z): return ((x - X0) * k, (z - Z0) * k)
    for x, z, sx, sy, sz, c, y, _surface, _style, solid in sorted(m.boxes, key=lambda b: (b[6], b[4])):
        col = tuple(int(v * 255) for v in c)
        if not solid:
            dr.rectangle([*px(x - sx / 2, z - sz / 2), *px(x + sx / 2, z + sz / 2)], fill=tuple(int(v * 0.8) for v in col))
            continue
        if y > 0.5:
            dr.rectangle([*px(x - sx / 2, z - sz / 2), *px(x + sx / 2, z + sz / 2)], outline=(255, 255, 255))
            continue
        if sy < 3: col = tuple(min(255, int(v * 0.8 + 40)) for v in col)
        dr.rectangle([*px(x - sx / 2, z - sz / 2), *px(x + sx / 2, z + sz / 2)], fill=col)
    for kind, x, z, team, index, radius in m.markers:
        if kind == SITE:
            dr.rectangle([*px(x - radius, z - radius), *px(x + radius, z + radius)], outline=(255, 235, 80), width=3)
            dr.text(px(x - 3, z - 5), 'AB'[index], fill=(255, 255, 255))
        else:
            dr.ellipse([*px(x - 1, z - 1), *px(x + 1, z + 1)], fill=(255, 80, 70) if team == 1 else (80, 140, 255))
    img.save(path)


if __name__ == '__main__':
    m = build()
    print(f'{len(m.boxes)} boîtes, {len(m.markers)} points')
    ok = check(m)
    out = os.path.join(os.environ.get('TEMP', '.'), 'sabotage_plan.png')
    preview(m, out)
    print('plan :', out)
    if not ok:
        sys.exit(1)
    if '--check' not in sys.argv:
        sys.argv.append('--force')
        m.write()
