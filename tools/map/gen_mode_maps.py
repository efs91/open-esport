#!/usr/bin/env python3
"""Génère les scènes des cartes simples des modes de jeu (sans bâtiments : un sol, des abris et les points d'attention).

    python tools/map/gen_mode_maps.py

Écrit scenes/maps/Arene.tscn (Deathmatch / Team Deathmatch), Drapeau.tscn (Capture du drapeau), Sabotage.tscn et Controle.tscn.
Ensuite on peut les ouvrir dans Godot et déplacer / copier / régler les nœuds : CoverBox (abris), MapMarker (apparitions, points de contrôle, bases de drapeau,
sites de bombe). Puis « Exporter la carte » (panneau Open Esport) fabrique le fichier .world que lisent le serveur et le jeu.
Ne relance pas ce script sur une carte déjà retouchée : il la réécrirait.
"""
import os, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')

# types de marqueurs (voir MapMarker.KindEnum)
FFA, TEAM, CONTROL, FLAG, SITE = 1, 2, 3, 4, 5

WALL = (0.22, 0.24, 0.28)
COVER = (0.42, 0.47, 0.55)
LOW = (0.5, 0.45, 0.35)


class Map:
    def __init__(self, name, size, ground, sky_top=(0.35, 0.5, 0.75)):
        self.name, self.size, self.ground = name, size, ground
        self.boxes, self.markers = [], []
        self.environment = 'res://scenes/maps/lighting/arena_daylight.tres'
        self.scenery = None
        self.ground_material = None
        self.surface_materials = {}
        self.box_models = {}
        self.sun_angle = None
        self.ground_surface = 0         # matière du sol (voir CoverBox.cs : Surface) ; 0 = couleur unie

    def box(self, x, z, sx, sy, sz, color=COVER, y=0.0, surface=0, style=0, solid=True, model=None):
        """Une boîte pleine : centre (x, z), dimensions, couleur ; `y` = hauteur du dessous (0 = posée au sol ; plus haut : toit de tunnel, passerelle)."""
        if model: self.box_models[len(self.boxes)] = model
        self.boxes.append((x, z, sx, sy, sz, color, y, surface, style, solid))

    def walls(self):
        w, d = self.size
        t = 1.0
        self.box(0, -d / 2 + t / 2, w, 4, t, WALL)
        self.box(0, d / 2 - t / 2, w, 4, t, WALL)
        self.box(-w / 2 + t / 2, 0, t, 4, d, WALL)
        self.box(w / 2 - t / 2, 0, t, 4, d, WALL)

    def mirror4(self, x, z, sx, sy, sz, color=COVER):
        for fx in {x, -x}:
            for fz in {z, -z}:
                self.box(fx, fz, sx, sy, sz, color)

    def mirror_x(self, x, z, sx, sy, sz, color=COVER):
        for fx in {x, -x}:
            self.box(fx, z, sx, sy, sz, color)

    def marker(self, kind, x, z, team=255, index=0, radius=2.0):
        self.markers.append((kind, x, z, team, index, radius))

    def tscn(self, export=False):
        """Le texte de la scène. `export=True` : version sans décor (ni dalles de sol, ni matières) pour fabriquer le fichier .world avec un exécutable de jeu plus ancien."""
        w, d = self.size
        first_spawn = next((m for m in self.markers if m[0] in (TEAM, FFA)), None)
        sx, sz = (first_spawn[1], first_spawn[2]) if first_spawn else (0, 0)
        o = []
        o.append('[gd_scene format=4]\n')
        o.append('[ext_resource type="Script" path="res://scripts/online/SimpleMap.cs" id="1_map"]')
        o.append('[ext_resource type="Script" path="res://scripts/online/CoverBox.cs" id="2_cover"]')
        o.append('[ext_resource type="Script" path="res://scripts/online/MapMarker.cs" id="3_marker"]')
        models = {path: f"prop_{i}" for i, path in enumerate(sorted(set(self.box_models.values())))}
        if not export:
            for path, key in models.items():
                o.append(f'[ext_resource type="PackedScene" path="{path}" id="{key}"]')
            o.append(f'[ext_resource type="Environment" path="{self.environment}" id="daylight"]')
            if self.scenery:
                o.append(f'[ext_resource type="PackedScene" path="{self.scenery}" id="scenery"]')
            if self.ground_material:
                o.append(f'[ext_resource type="Material" path="{self.ground_material}" id="ground_mat"]')
            for surface, path in self.surface_materials.items():
                o.append(f'[ext_resource type="Material" path="{path}" id="material_{surface}"]')
            o.append('[ext_resource type="PackedScene" path="res://scenes/maps/lighting/afternoon_sun.tscn" id="sun"]')
        o.append('')
        o.append(f'[node name="{self.name}" type="Node3D"]')
        o.append('script = ExtResource("1_map")')
        o.append(f'Size = Vector2({w}, {d})')
        o.append(f'GroundColor = Color({self.ground[0]}, {self.ground[1]}, {self.ground[2]}, 1)')
        if self.ground_material and not export: o.append('GroundMaterial = ExtResource("ground_mat")')
        if self.ground_surface and not export: o.append(f'GroundSurface = {self.ground_surface}')
        o.append('')
        if not export:
            o.append('[node name="Sun" parent="." instance=ExtResource("sun")]')
            if self.sun_angle is not None: o.append(f'light_angular_distance = {self.sun_angle}')
            o.append('')
            if self.scenery: o.append('[node name="Terminal" parent="." instance=ExtResource("scenery")]\n')
            o.append('[node name="WorldEnvironment" type="WorldEnvironment" parent="."]')
            o.append('environment = ExtResource("daylight")\n')
        o.append('[node name="SpawnPoint" type="Marker3D" parent="."]')
        o.append(f'position = Vector3({sx}, 0.2, {sz})\n')
        o.append('[node name="Points" type="Node3D" parent="."]\n')
        for i, (kind, x, z, team, index, radius) in enumerate(self.markers):
            o.append(f'[node name="Marker_{i + 1:02d}" type="Marker3D" parent="Points"]')
            o.append(f'position = Vector3({x}, 0, {z})')
            o.append('script = ExtResource("3_marker")')
            o.append(f'Kind = {kind}')
            o.append(f'Team = {team}')
            o.append(f'Index = {index}')
            o.append(f'Radius = {radius}\n')
        o.append('[node name="Cover" type="Node3D" parent="."]\n')
        for i, (x, z, bx, by, bz, c, y, surface, style, solid) in enumerate(self.boxes):
            if export and not solid:
                continue
            o.append(f'[node name="Box_{i + 1:02d}" type="StaticBody3D" parent="Cover"]')
            o.append(f'position = Vector3({x}, {y}, {z})')
            o.append('script = ExtResource("2_cover")')
            o.append(f'Size = Vector3({bx}, {by}, {bz})')
            o.append(f'Color = Color({c[0]}, {c[1]}, {c[2]}, 1)')
            if not export:
                if surface: o.append(f'Surface = {surface}')
                if surface in self.surface_materials: o.append(f'BoxMaterial = ExtResource("material_{surface}")')
                if i in self.box_models: o.append(f'UnitModel = ExtResource("{models[self.box_models[i]]}")')
                if style: o.append(f'Style = {style}')
                if not solid: o.append('Solid = false')
            o.append('')
        return '\n'.join(o) + '\n'

    def write(self):
        path = os.path.join(ROOT, 'scenes', 'maps', self.name + '.tscn')
        if os.path.exists(path) and '--force' not in sys.argv:
            print(f'{path} existe déjà : non réécrit (--force pour le refaire)')
            return
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(self.tscn())
        print('écrit', path, f'({len(self.boxes)} abris, {len(self.markers)} points)')


# couleurs des matières sur le dessin de la carte (voir CoverBox.cs : Surface)
SURFACE_COLORS = {1: (196, 196, 202), 2: (126, 132, 146), 3: (156, 104, 74), 4: (182, 156, 118), 5: (96, 146, 74), 6: (124, 132, 142), 7: (150, 112, 74), 8: (210, 184, 134), 9: (134, 104, 74)}


def write_minimap(m, path, px=1024):
    """Dessin de la carte pour la minimap et la grande carte (nord en haut, image carrée : étirée sur toute la carte comme le fait le jeu).
    Murs sombres, sols colorés selon leur matière, abris bruns, sites de bombe cerclés de jaune."""
    from PIL import Image, ImageDraw, ImageFont
    w, d = m.size
    sx, sz = px / w, px / d
    def P(x, z): return ((x + w / 2) * sx, (z + d / 2) * sz)
    ground = SURFACE_COLORS.get(m.ground_surface) or tuple(int(v * 255) for v in m.ground)
    img = Image.new('RGB', (px, px), ground)
    dr = ImageDraw.Draw(img)
    solids = sorted([b for b in m.boxes if b[9]], key=lambda b: (b[6], b[3]))
    for x, z, bx, by, bz, c, y, surface, style, solid in sorted([b for b in m.boxes if not b[9]], key=lambda b: b[3]):
        col = SURFACE_COLORS.get(surface) or tuple(int(v * 255) for v in c)
        dr.rectangle([*P(x - bx / 2, z - bz / 2), *P(x + bx / 2, z + bz / 2)], fill=col)
    for x, z, bx, by, bz, c, y, surface, style, solid in solids:
        box = [*P(x - bx / 2, z - bz / 2), *P(x + bx / 2, z + bz / 2)]
        if y > 0.5:                                   # toit, passerelle : contour seulement
            dr.rectangle(box, outline=(235, 235, 240), width=2)
        elif by >= 3.5:                               # mur, bâtiment
            dr.rectangle(box, fill=(52, 58, 70), outline=(150, 158, 174), width=2)
        else:                                         # abri, caisse, marche
            col = SURFACE_COLORS.get(surface) or tuple(int(v * 255) for v in c)
            dr.rectangle(box, fill=col, outline=(40, 34, 28), width=1)
    try:
        font = ImageFont.truetype('arial.ttf', 70)
    except Exception:
        font = ImageFont.load_default()
    for kind, x, z, team, index, radius in m.markers:
        if kind == SITE:
            dr.rectangle([*P(x - radius, z - radius), *P(x + radius, z + radius)], fill=None, outline=(255, 214, 64), width=5)
            cx, cy = P(x, z)
            dr.text((cx - 20, cy - 40), 'AB'[index], fill=(255, 255, 255), font=font, stroke_width=4, stroke_fill=(0, 0, 0))
        elif kind == TEAM:
            cx, cy = P(x, z)
            dr.ellipse([cx - 4, cy - 4, cx + 4, cy + 4], fill=(255, 96, 82) if team == 1 else (90, 150, 255))
    img.save(path)


def arene():
    m = Map('Arene', (100, 100), (0.30, 0.34, 0.28))
    m.walls()
    m.box(0, 0, 10, 3, 10)
    m.mirror4(20, 15, 6, 2.5, 3)
    m.mirror4(15, 30, 3, 2.5, 6)
    m.mirror_x(32, 0, 3, 3, 10)
    for z in (-30, 30): m.box(0, z, 12, 2.5, 3)
    m.mirror4(8, 12, 2, 1.2, 2, LOW)
    # chacun pour soi : huit points répartis ; équipes : bleus à l'ouest, rouges à l'est
    for x, z in [(-40, -40), (40, -40), (-40, 40), (40, 40), (0, -42), (0, 42), (-42, 0), (42, 0)]:
        m.marker(FFA, x, z)
    for z in (-12, -4, 4, 12):
        m.marker(TEAM, -44, z, 0)
        m.marker(TEAM, 44, z, 1)
    return m


def drapeau():
    m = Map('Drapeau', (160, 80), (0.33, 0.36, 0.27))
    m.walls()
    m.box(0, 0, 8, 3, 8)
    m.mirror4(25, 12, 6, 2.5, 3)
    m.mirror_x(40, 0, 3, 3, 12)
    m.mirror4(55, 15, 4, 2.5, 4)
    m.mirror4(12, 26, 3, 2.5, 5)
    # bases : un mur derrière chaque drapeau
    m.mirror_x(77, 0, 2, 4, 16, WALL)
    m.mirror_x(70, 10, 6, 2.5, 2, LOW); m.mirror_x(70, -10, 6, 2.5, 2, LOW)
    m.marker(FLAG, -72, 0, 0, 0, 3.0)
    m.marker(FLAG, 72, 0, 1, 0, 3.0)
    for z in (-12, -4, 4, 12):
        m.marker(TEAM, -64, z, 0)
        m.marker(TEAM, 64, z, 1)
    return m


def sabotage():
    from gen_sabotage_map import build
    return build()


def controle():
    m = Map('Controle', (160, 100), (0.28, 0.35, 0.3))
    m.walls()
    m.box(0, 0, 3, 1.2, 3, LOW)
    m.mirror_x(36, 14, 6, 2.5, 3); m.mirror_x(36, -14, 6, 2.5, 3)
    m.mirror_x(18, 22, 3, 2.5, 8); m.mirror_x(18, -22, 3, 2.5, 8)
    m.box(0, 18, 8, 3, 3); m.box(0, -18, 8, 3, 3)
    m.mirror_x(55, 26, 3, 3, 14, WALL); m.mirror_x(55, -26, 3, 3, 14, WALL)
    m.mirror_x(60, 0, 3, 2.5, 8)
    # cinq points : base bleue, A, B, C, base rouge
    m.marker(CONTROL, -72, 0, 0, 0, 7.0)
    m.marker(CONTROL, -36, 0, 255, 1, 7.0)
    m.marker(CONTROL, 0, 0, 255, 2, 7.0)
    m.marker(CONTROL, 36, 0, 255, 3, 7.0)
    m.marker(CONTROL, 72, 0, 1, 4, 7.0)
    for z in (-12, -6, 0, 6, 12, 18):
        m.marker(TEAM, -66, z - 3, 0)
        m.marker(TEAM, 66, z - 3, 1)
    return m


if __name__ == '__main__':
    if '--minimaps' in sys.argv:
        for build in (arene, drapeau, sabotage, controle):
            mp = build()
            out = os.path.join(ROOT, 'assets', 'maps', mp.name.lower() + '.png')
            write_minimap(mp, out)
            print('dessin écrit', out)
        sys.exit(0)
    for build in (arene, drapeau, sabotage, controle):
        build().write()
