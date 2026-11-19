#!/usr/bin/env python3
"""Terminal des Cimes : scène, collisions et mini-carte du mode Sabotage.
python tools/map/gen_sabotage_map.py --check vérifie les accès sans réécrire la scène.
"""
import math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_mode_maps import Map, TEAM, SITE, ROOT, write_minimap

CELL, W, H = 4, 48, 46
X0, Z0 = -W * CELL // 2, -H * CELL // 2
SITE_A, SITE_B = (-64, -12), (64, -4)
SITE_R = 8

class Grid:
    def __init__(self):
        self.g = [['#'] * W for _ in range(H)]

    def carve(self, c0, r0, c1, r1, ch='.'):
        for r in range(r0, r1 + 1):
            for c in range(c0, c1 + 1):
                self.g[r][c] = ch

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
    grid = Grid()

    def room(x0, z0, x1, z1):
        # World metres, east and south edges excluded. All room boundaries meet on the 4 m grid.
        grid.carve((x0-X0)//CELL, (z0-Z0)//CELL, (x1-X0)//CELL-1, (z1-Z0)//CELL-1)

    # North yard. Two offset service entrances give each site its own return route.
    room(-20,-88, 20,-68)
    room(-44,-80, -16,-68); room(-44,-76, -32,-48)
    room(-68,-56, -32,-44); room(-72,-52, -56,-28)
    room(16,-80, 44,-68); room(32,-72, 44,-44)
    room(32,-52, 68,-40); room(56,-44, 68,-20)

    # West loading dock and east turbine hall, linked through the dispatch square.
    room(-84,-32, -44,8); room(44,-24, 84,16)
    room(-48,-16, -28,-4); room(-36,-12, -24,4); room(-28,-4, -12,8)
    room(16,-20, 32,-8); room(24,-16, 36,0); room(32,-8, 48,4)
    room(-16,-24, 20,12)
    room(-12,-48, 0,-20); room(-32,-52, -8,-44)

    # South concourse splits before the midpoint. West has two successive courtyards.
    # East switches sides under a roof. Mid is broken by the dispatch booth.
    room(-20,68, 20,88)
    room(-48,64, -12,76); room(-52,44, -36,72)
    room(-76,40, -40,56); room(-80,16, -64,48)
    room(-80,12, -56,24); room(-64,4, -52,20)
    room(12,64, 48,76); room(36,44, 52,72)
    room(40,36, 72,52); room(60,20, 76,48)
    room(48,16, 72,28); room(48,12, 60,20)
    room(-8,44, 8,72); room(-16,32, 12,52)
    room(-16,16, -4,40); room(-16,8, 8,24)
    # Crosscuts are staggered so the whole width cannot be seen from either end.
    room(-40,44, -12,52); room(8,36, 44,44)
    return grid


CONCRETE, ASPHALT, STEEL, CARGO = 1, 2, 6, 7
SLATE, OCHRE, BLUE = (0.57,0.64,0.66), (0.83,0.46,0.20), (0.18,0.48,0.55)
ART = 'res://scenes/maps/sabotage/'


def build():
    m = Map('Sabotage', (W*CELL,H*CELL), SLATE)
    m.environment = 'res://scenes/maps/lighting/frontiere_daylight.tres'
    m.sun_angle = 0.0
    m.scenery = ART + 'Terminal.tscn'
    m.ground_surface = ASPHALT
    m.ground_material = 'res://maps/frontiere01/wet_pavement.tres'
    m.surface_materials = {CONCRETE: ART+'Cladding.tres', CARGO: ART+'Freight.tres'}

    for c,r,c1,r1 in layout().rects():
        x0,z0 = X0+c*CELL, Z0+r*CELL
        width,depth = (c1-c+1)*CELL,(r1-r+1)*CELL
        height = (6.2, 8.0, 7.2, 9.6)[(c+3*r)%4]
        m.box(x0+width/2,z0+depth/2,width,height,depth,SLATE,surface=CONCRETE)

    # Cargo lanes use ISO-sized loads and low equipment pallets; the central booth
    # forces a decision on entry to the square while keeping both exits visible.
    loads = [(-71,-17, 4,3,9),(-57,-7, 6,1.2,3),(-78,0, 4,2.7,5),
             (56,-10, 8,3.1,4),(72,-1, 4,2.7,8),(62,10, 5,1.2,3),
             (-72,32, 4,2.7,7),(-47,48, 4,1.25,3),(67,36, 4,2.6,6),
             (-2,-6, 8,3.8,6),(10,-18, 3,1.25,4),(-9,34, 3,1.2,3),
             (-37,-62, 3,1.25,4),(38,-55, 3,2.6,5)]
    for x,z,w,h,d in loads:
        m.box(x,z,w,h,d,OCHRE if x<0 else BLUE,surface=CARGO)

    # Stacked freight and the dispatch cabin have the same volume in the scene and on the server.
    unit_crate = 'res://assets/environement/Modular SciFi MegaKit[Standard]/glTF/Props/Prop_Crate3.gltf'
    for z in (-19.6,-17,-14.4):
        m.box(-71,z,1.35,1.35,1.35,OCHRE,y=3,surface=CARGO,model=unit_crate)
    for x,z in [(-74,4),(-58,14),(75,6),(70,42),(-7,-21)]:
        m.box(x,z,2.1,1.9,2.1,SLATE,surface=STEEL,model=unit_crate)
    m.box(-2,-6,7.6,2,5.7,(0.15,0.25,0.30),y=3.8,surface=STEEL)
    # B opens into a half-covered turbine hall. Its open half keeps the sky visible.
    m.box(76,-4,16,0.45,30,BLUE,y=6.15,surface=STEEL)
    for z in (-18,10):
        m.box(68.4,z,0.8,6.15,0.8,BLUE,surface=STEEL)
    # Light concrete loading pads break up the wet road. Site paint sits above these slabs.
    for x,z,w,d in [(-64,-12,22,23),(64,-4,23,21),(-2,0,12,12),(0,-78,25,13),(0,78,25,13)]:
        m.box(x,z,w,0.012,d,(0.84,0.84,0.79),surface=ASPHALT,solid=False)

    # Walkable gallery against the west wall. The projected server navigation
    # also has a clear ground route alongside its steps.
    m.box(-81,-14,5,1.6,15,SLATE,surface=STEEL)
    for i in range(4):
        m.box(-81,-6.5+1.3*(i+0.5),5,1.28-i*0.32,1.3,SLATE,surface=STEEL)

    # Roof over the eastern bend; a second entrance from mid stays open to the sky.
    m.box(68,33,16,0.55,14,BLUE,y=5.4,surface=STEEL)
    for x in (60.5,75.5):
        for z in (26.5,39.5):
            m.box(x,z,0.72,5.4,0.72,SLATE,surface=STEEL)

    # Gantry supports are authoritative obstacles as well as visual geometry.
    for x in (-80,-48):
        for z in (-23,1):
            m.box(x,z,0.85,8.4,0.85,OCHRE,surface=STEEL)
    for x in (-12,-6,0,6,12):
        for z in (76,82): m.marker(TEAM,x,z,1)
        for z in (-82,-76): m.marker(TEAM,x,z,0)
    for i,(x,z) in enumerate((SITE_A,SITE_B)): m.marker(SITE,x,z,255,i,SITE_R)
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
    from heapq import heappop, heappush
    d, w = len(blocked), len(blocked[0])
    si, sj = int((start[0] - X0) / res), int((start[1] - Z0) / res)
    dist = {(si, sj): 0.0}
    q = [(0.0, si, sj)]
    while q:
        length, i, j = heappop(q)
        if length > dist[(i,j)]: continue
        for di, dj, cost in ((1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)):
            ni, nj = i + di, j + dj
            if not (0 <= ni < w and 0 <= nj < d) or blocked[nj][ni]:
                continue
            if di and dj and (blocked[j][ni] or blocked[nj][i]):
                continue
            new_length = length + cost * res
            if new_length < dist.get((ni,nj), float("inf")):
                dist[(ni,nj)] = new_length
                heappush(q, (new_length,ni,nj))
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
        from sabotage_terminal import write_terminal
        write_terminal(os.path.join(ROOT, "scenes/maps/sabotage/Terminal.tscn"), layout(), X0, Z0, CELL)
        m.write()
        write_minimap(m, os.path.join(ROOT, "assets/maps/sabotage.png"))
