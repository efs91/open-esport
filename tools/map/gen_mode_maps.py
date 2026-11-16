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

    def box(self, x, z, sx, sy, sz, color=COVER):
        self.boxes.append((x, z, sx, sy, sz, color))

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

    def tscn(self):
        w, d = self.size
        first_spawn = next((m for m in self.markers if m[0] in (TEAM, FFA)), None)
        sx, sz = (first_spawn[1], first_spawn[2]) if first_spawn else (0, 0)
        o = []
        o.append('[gd_scene format=4]\n')
        o.append('[ext_resource type="Script" path="res://scripts/online/SimpleMap.cs" id="1_map"]')
        o.append('[ext_resource type="Script" path="res://scripts/online/CoverBox.cs" id="2_cover"]')
        o.append('[ext_resource type="Script" path="res://scripts/online/MapMarker.cs" id="3_marker"]\n')
        o.append('[sub_resource type="ProceduralSkyMaterial" id="sky_mat"]')
        o.append('sky_top_color = Color(0.32, 0.5, 0.78, 1)')
        o.append('sky_horizon_color = Color(0.72, 0.78, 0.86, 1)\n')
        o.append('[sub_resource type="Sky" id="sky"]')
        o.append('sky_material = SubResource("sky_mat")\n')
        o.append('[sub_resource type="Environment" id="env"]')
        o.append('background_mode = 2')
        o.append('sky = SubResource("sky")')
        o.append('ambient_light_source = 3')
        o.append('tonemap_mode = 2\n')
        o.append(f'[node name="{self.name}" type="Node3D"]')
        o.append('script = ExtResource("1_map")')
        o.append(f'Size = Vector2({w}, {d})')
        o.append(f'GroundColor = Color({self.ground[0]}, {self.ground[1]}, {self.ground[2]}, 1)\n')
        o.append('[node name="Sun" type="DirectionalLight3D" parent="."]')
        o.append('transform = Transform3D(0.8, -0.35, 0.49, 0, 0.81, 0.58, -0.6, -0.46, 0.65, 0, 20, 0)')
        o.append('shadow_enabled = true\n')
        o.append('[node name="WorldEnvironment" type="WorldEnvironment" parent="."]')
        o.append('environment = SubResource("env")\n')
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
        for i, (x, z, bx, by, bz, c) in enumerate(self.boxes):
            o.append(f'[node name="Box_{i + 1:02d}" type="StaticBody3D" parent="Cover"]')
            o.append(f'position = Vector3({x}, 0, {z})')
            o.append('script = ExtResource("2_cover")')
            o.append(f'Size = Vector3({bx}, {by}, {bz})')
            o.append(f'Color = Color({c[0]}, {c[1]}, {c[2]}, 1)\n')
        return '\n'.join(o) + '\n'

    def write(self):
        path = os.path.join(ROOT, 'scenes', 'maps', self.name + '.tscn')
        if os.path.exists(path) and '--force' not in sys.argv:
            print(f'{path} existe déjà : non réécrit (--force pour le refaire)')
            return
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(self.tscn())
        print('écrit', path, f'({len(self.boxes)} abris, {len(self.markers)} points)')


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
    m = Map('Sabotage', (120, 80), (0.36, 0.33, 0.27))
    m.walls()
    # couloirs : un mur central, deux murs de part et d'autre
    m.box(-5, 0, 3, 4, 26, WALL)
    m.mirror4(-22, 14, 3, 4, 12, WALL)
    m.mirror4(10, 8, 2, 4, 10, WALL)
    # sites A (z négatif) et B (z positif) : quelques caisses
    for s in (-1, 1):
        m.box(20, s * 22, 3, 2.5, 3)
        m.box(30, s * 28, 6, 2.5, 2)
        m.box(34, s * 16, 2, 2.5, 6)
        m.box(12, s * 30, 5, 1.5, 2, LOW)
    m.box(-35, 0, 6, 2.5, 3)
    # attaquants à l'ouest (camp 1), défenseurs à l'est (camp 0)
    for z in (-12, -6, 0, 6, 12):
        m.marker(TEAM, -50, z, 1)
        m.marker(TEAM, 48, z, 0)
    m.marker(SITE, 25, -22, 255, 0, 8.0)
    m.marker(SITE, 25, 22, 255, 1, 8.0)
    return m


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
    for build in (arene, drapeau, sabotage, controle):
        build().write()
