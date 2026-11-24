#!/usr/bin/env python3
"""Carte « Duel » du mode 1 VS 1 : une petite salle intérieure au décor de Relais (mêmes matières, bandes lumineuses, plafonniers),
sans couloir, avec un seul bloc au milieu, assez haut pour se cacher debout et assez bas pour l'escalader.

    python tools/map/gen_duel_map.py            # écrit scenes/maps/Duel.tscn et assets/maps/duel.png (refuse d'écraser la scène : --force pour la refaire)
    python tools/map/gen_duel_map.py --minimap  # redessine seulement assets/maps/duel.png

Plan (nord en haut) : salle de 32 x 20 m, plafond à 7 m. Bloc central de 2,4 x 2,2 x 5 m (2,2 m : on s'y cache debout, et c'est sous
la hauteur d'escalade, movement ClimbMaxRise = 2,3 m). Apparition des bleus (équipe 0) à l'ouest, des rouges (équipe 1) à l'est.
Ensuite la scène s'édite dans Godot, puis « Exporter la carte » (ou la commande ci-dessous) fabrique assets/maps/duel.world :
    Godot --headless res://tools/export_world.tscn -- --map=res://scenes/maps/Duel.tscn --out=res://assets/maps/duel.world
"""
import os, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
ART = 'res://scenes/maps/relais/'
W, D = 32.0, 20.0          # intérieur de la salle (x, z)
WALL = 1.0                  # épaisseur des murs
CEIL = 7.0                  # dessous du plafond
BLOCK = (2.4, 2.2, 5.0)     # bloc central (x, hauteur, z)
SPAWN_X = 12.5              # apparitions à ±SPAWN_X


def spawns():
    """(x, z, équipe) : deux points par camp, côte à côte, pour l'aléa du serveur."""
    return [(-SPAWN_X, -1.5, 0), (-SPAWN_X, 1.5, 0), (SPAWN_X, -1.5, 1), (SPAWN_X, 1.5, 1)]


def structure():
    """Boîtes pleines (x0, y0, z0, x1, y1, z1, nom, matière)."""
    hx, hz = W / 2, D / 2
    out = [
        (-hx - WALL, 0, -hz - WALL, hx + WALL, CEIL, -hz, 'Mur_Nord', 'm_surf'),
        (-hx - WALL, 0, hz, hx + WALL, CEIL, hz + WALL, 'Mur_Sud', 'm_surf'),
        (-hx - WALL, 0, -hz, -hx, CEIL, hz, 'Mur_Ouest', 'm_surf'),
        (hx, 0, -hz, hx + WALL, CEIL, hz, 'Mur_Est', 'm_surf'),
    ]
    bx, by, bz = BLOCK
    out.append((-bx / 2, 0, -bz / 2, bx / 2, by, bz / 2, 'Bloc_Central', 'm_rail'))
    # pilastres le long des murs nord et sud (décor en relief, 25 cm : ni abri ni couloir)
    for x in (-12.0, -6.0, 6.0, 12.0):
        out.append((x - 0.3, 0, -hz, x + 0.3, CEIL, -hz + 0.25, f'Pilastre_N_{int(x):+d}', 'm_stairs'))
        out.append((x - 0.3, 0, hz - 0.25, x + 0.3, CEIL, hz, f'Pilastre_S_{int(x):+d}', 'm_stairs'))
    return out


def lights():
    """Plafonniers (x, z, couleur) : deux rangées, chaudes au centre, bleutées à l'ouest, violettes à l'est (couleurs des niveaux de Relais)."""
    out = []
    for x in (-12.0, -6.0, 0.0, 6.0, 12.0):
        for z in (-5.0, 5.0):
            col = (1.0, 0.86, 0.7) if x == 0 else (0.78, 0.88, 1.0) if x < 0 else (0.9, 0.8, 1.0)
            out.append((x, z, col))
    return out


def tscn():
    o = []
    res = [
        ('Script', 'res://scripts/online/SimpleMap.cs', 'map'),
        ('Script', 'res://scripts/online/CoverBox.cs', 'cover'),
        ('Script', 'res://scripts/online/MapMarker.cs', 'marker'),
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
    ]
    o.append('[gd_scene format=4]\n')
    for t, path, key in res:
        o.append(f'[ext_resource type="{t}" path="{path}" id="{key}"]')
    o.append('')
    o.append('[node name="Duel" type="Node3D"]')
    o.append('script = ExtResource("map")')
    o.append(f'Size = Vector2({W + 2 * WALL}, {D + 2 * WALL})')
    o.append('GroundColor = Color(0.35, 0.36, 0.4, 1)')
    o.append('GroundMaterial = ExtResource("m_surf")\n')
    o.append('[node name="WorldEnvironment" type="WorldEnvironment" parent="."]')
    o.append('environment = ExtResource("env")\n')
    o.append('[node name="SpawnPoint" type="Marker3D" parent="."]')
    o.append(f'position = Vector3({-SPAWN_X}, 0.2, 0)\n')

    def box(parent, name, b, mat, solid=True, cast=True):
        x0, y0, z0, x1, y1, z1 = b
        o.append(f'[node name="{name}" type="StaticBody3D" parent="{parent}"]')
        o.append(f'position = Vector3({round((x0 + x1) / 2, 4)}, {round(y0, 4)}, {round((z0 + z1) / 2, 4)})')
        o.append('script = ExtResource("cover")')
        o.append(f'Size = Vector3({round(x1 - x0, 4)}, {round(y1 - y0, 4)}, {round(z1 - z0, 4)})')
        if mat: o.append(f'BoxMaterial = ExtResource("{mat}")')
        if not solid: o.append('Solid = false')
        o.append('')

    o.append('[node name="Structure" type="Node3D" parent="."]\n')
    for x0, y0, z0, x1, y1, z1, name, mat in structure():
        box('Structure', name, (x0, y0, z0, x1, y1, z1), mat)
    o.append('[node name="Plafond" type="Node3D" parent="."]\n')
    hx, hz = W / 2, D / 2
    box('Plafond', 'Plafond', (-hx - WALL, CEIL, -hz - WALL, hx + WALL, CEIL + 0.6, hz + WALL), 'm_surf')

    # décor sans collision
    o.append('[node name="Decor" type="Node3D" parent="."]\n')
    bx, by, bz = BLOCK
    # bandes lumineuses au pied des murs : cyan côté bleu, violet côté rouge
    for side, col in ((-1, 'm_cyan'), (1, 'm_purple')):
        x_in = side * hx
        box('Decor', f'Plinthe_{"Ouest" if side < 0 else "Est"}', (min(x_in, x_in - side * 0.06), 0.35, -hz, max(x_in, x_in - side * 0.06), 0.42, hz), col, solid=False, cast=False)
        for z_wall, zn in ((-hz, 'N'), (hz, 'S')):
            z0, z1 = (z_wall, z_wall + 0.06) if z_wall < 0 else (z_wall - 0.06, z_wall)
            x0, x1 = (-hx, -0.5) if side < 0 else (0.5, hx)
            box('Decor', f'Plinthe_{zn}_{"Ouest" if side < 0 else "Est"}', (x0, 0.35, z0, x1, 0.42, z1), col, solid=False, cast=False)
        # zone d'apparition : dalle lumineuse au sol
        box('Decor', f'Depart_{"Bleu" if side < 0 else "Rouge"}', (side * SPAWN_X - 1.2, 0.0, -3.0, side * SPAWN_X + 1.2, 0.02, 3.0), col, solid=False, cast=False)
    # ligne médiane orange au sol (de part et d'autre du bloc) et liseré lumineux sur le haut du bloc
    box('Decor', 'Mediane_Nord', (-0.15, 0.0, -hz, 0.15, 0.02, -bz / 2 - 0.3), 'm_orange', solid=False, cast=False)
    box('Decor', 'Mediane_Sud', (-0.15, 0.0, bz / 2 + 0.3, 0.15, 0.02, hz), 'm_orange', solid=False, cast=False)
    for name, b in (('Lisere_O', (-bx / 2 - 0.02, by - 0.12, -bz / 2, -bx / 2, by - 0.05, bz / 2)),
                    ('Lisere_E', (bx / 2, by - 0.12, -bz / 2, bx / 2 + 0.02, by - 0.05, bz / 2)),
                    ('Lisere_N', (-bx / 2, by - 0.12, -bz / 2 - 0.02, bx / 2, by - 0.05, -bz / 2)),
                    ('Lisere_S', (-bx / 2, by - 0.12, bz / 2, bx / 2, by - 0.05, bz / 2 + 0.02))):
        box('Decor', name, b, 'm_core', solid=False, cast=False)
    # verrière au-dessus du bloc
    box('Decor', 'Verriere', (-4.0, CEIL - 0.06, -3.0, 4.0, CEIL - 0.01, 3.0), 'm_sky', solid=False, cast=False)

    o.append('[node name="Lumieres" type="Node3D" parent="."]\n')
    for k, (x, z, col) in enumerate(lights()):
        y = CEIL - 0.35
        o.append(f'[node name="Lampe_{k + 1:03d}" type="OmniLight3D" parent="Lumieres"]')
        o.append(f'position = Vector3({x}, {y}, {z})')
        o.append(f'light_color = Color({col[0]}, {col[1]}, {col[2]}, 1)')
        o.append('light_energy = 2.6')
        o.append('light_specular = 0.6')
        o.append('omni_range = 10.0')
        o.append('omni_attenuation = 0.9\n')
        box('Lumieres', f'Plafonnier_{k + 1:03d}', (x - 0.6, y + 0.12, z - 0.18, x + 0.6, y + 0.2, z + 0.18), 'm_lamp', solid=False, cast=False)
    o.append('[node name="Lumiere_Verriere" type="SpotLight3D" parent="Lumieres"]')
    o.append(f'transform = Transform3D(1, 0, 0, 0, -4.371139e-08, 1, 0, -1, -4.371139e-08, 0, {CEIL - 0.3}, 0)')
    o.append('light_color = Color(1, 0.9, 0.78, 1)')
    o.append('light_energy = 6.0')
    o.append('shadow_enabled = true')
    o.append('spot_range = 12.0')
    o.append('spot_angle = 60.0\n')
    for side, col in ((-1, (0.45, 0.8, 1.0)), (1, (0.85, 0.55, 1.0))):
        o.append(f'[node name="Lumiere_{"Bleue" if side < 0 else "Rouge"}" type="OmniLight3D" parent="Lumieres"]')
        o.append(f'position = Vector3({side * (hx - 1.5)}, 3.5, 0)')
        o.append(f'light_color = Color({col[0]}, {col[1]}, {col[2]}, 1)')
        o.append('light_energy = 2.0')
        o.append('omni_range = 12.0\n')

    o.append('[node name="Points" type="Node3D" parent="."]\n')
    for k, (x, z, team) in enumerate(spawns()):
        o.append(f'[node name="Apparition_{"Bleue" if team == 0 else "Rouge"}_{k % 2 + 1}" type="Marker3D" parent="Points"]')
        o.append(f'position = Vector3({x}, 0, {z})')
        o.append('script = ExtResource("marker")')
        o.append('Kind = 2')
        o.append(f'Team = {team}\n')
    return '\n'.join(o) + '\n'


def minimap(path, px=1024):
    """Dessin de la carte (image carrée étirée sur toute la carte, comme le fait le jeu)."""
    from PIL import Image, ImageDraw
    w, d = W + 2 * WALL, D + 2 * WALL
    sx, sz = px / w, px / d
    def P(x, z): return ((x + w / 2) * sx, (z + d / 2) * sz)
    img = Image.new('RGB', (px, px), (52, 58, 70))
    dr = ImageDraw.Draw(img)
    dr.rectangle([*P(-W / 2, -D / 2), *P(W / 2, D / 2)], fill=(124, 132, 142))
    dr.line([*P(0, -D / 2), *P(0, D / 2)], fill=(235, 150, 60), width=4)
    bx, by, bz = BLOCK
    dr.rectangle([*P(-bx / 2, -bz / 2), *P(bx / 2, bz / 2)], fill=(150, 112, 74), outline=(40, 34, 28), width=3)
    for x, z, team in spawns():
        cx, cy = P(x, z)
        dr.ellipse([cx - 14, cy - 14, cx + 14, cy + 14], fill=(255, 96, 82) if team == 1 else (90, 150, 255))
    img.save(path)


def main():
    png = os.path.join(ROOT, 'assets', 'maps', 'duel.png')
    if '--minimap' not in sys.argv:
        path = os.path.join(ROOT, 'scenes', 'maps', 'Duel.tscn')
        if os.path.exists(path) and '--force' not in sys.argv:
            print(f'{path} existe déjà : non réécrit (--force effacerait les retouches faites dans Godot)')
        else:
            with open(path, 'w', encoding='utf-8', newline='\n') as f:
                f.write(tscn())
            print('écrit', path)
    minimap(png)
    print('dessin écrit', png)


if __name__ == '__main__':
    main()
