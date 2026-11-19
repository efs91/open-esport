"""Les Deux Écluses. X=0 est le plan de symétrie, Y=4 le niveau de la cour, Y=0 celui du collecteur."""
from pathlib import Path
import sys
from gen_mode_maps import Map, ROOT, TEAM, FLAG

SIZE = (224, 120)
COURT = 4
STONE, PAVING, ROCK, IRON, TIMBER = 1, 2, 3, 6, 9
GREY = (0.68, 0.71, 0.70)
IRON_GREY = (0.39, 0.45, 0.47)
OAK = (0.68, 0.47, 0.31)
LIMESTONE = (0.83, 0.79, 0.65)
HERALDRY = [(0.15,0.50,0.86), (0.85,0.28,0.16)]
STAIRS = [(10,26,-20,-14), (62,78,26,32), (82,98,6,12)]
GALLERIES = [(10,40,18,24), (34,40,12,24), (34,60,12,18),
             (54,60,12,38), (54,82,26,32), (54,84,32,38), (78,84,6,34)]


def substrate(x,z):
    for lo,hi,n,s in STAIRS:
        if lo <= x < hi and n <= z < s: return 'stair'
    for lo,hi,n,s in GALLERIES:
        if lo <= x < hi and n <= z < s: return 'vault'
    return 'bank'


def banks(pair):
    # Merge the 2 m plan into solid blocks. A gallery gets a thin walkable roof.
    cells = {(x,z):substrate(x+1,z+1) for x in range(10,112,2) for z in range(-60,60,2)}
    for z in range(-60,60,2):
        for x in range(10,112,2):
            kind=cells.get((x,z))
            if not kind: continue
            end=x+2
            while cells.get((end,z))==kind: end+=2
            far=z+2
            while all(cells.get((xx,far))==kind for xx in range(x,end,2)): far+=2
            for xx in range(x,end,2):
                for zz in range(z,far,2): del cells[xx,zz]
            if kind!='stair':
                bottom=3.45 if kind=='vault' else 0
                pair((x+end)/2,(z+far)/2,end-x,COURT-bottom,far-z,bottom,IRON_GREY,PAVING)
    for lo,hi,n,s in STAIRS:
        for i in range(hi-lo):
            pair(lo+i+.5,(n+s)/2,1,(i+1)*.25,s-n,0,GREY,STONE)


def build():
    m=Map('Drapeau',SIZE,GREY)
    m.marker_height=COURT
    m.sun_angle=0
    m.sun_yaw=0
    m.environment='res://scenes/maps/lighting/frontiere_daylight.tres'
    m.ground_material='res://maps/frontiere01/wet_pavement.tres'
    m.surface_materials={STONE:'res://scenes/maps/drapeau/Maconnerie.tres'}
    m.scenery='res://scenes/maps/drapeau/Lanternes.tscn'

    def pair(x,z,w,h,d,y=COURT,color=GREY,surface=STONE,team=False,model=None):
        for side,sign in enumerate((-1,1)):
            m.box(sign*x,z,w,h,d,HERALDRY[side] if team else color,y,surface,model=model)

    banks(pair)
    # Edge of the playable valley; the end walls close the dry channel as well.
    for z in (-59.5,59.5): m.box(0,z,224,13,1,GREY,surface=ROCK)
    pair(111.5,0,1,10,120)

    # A timber bridge with a clerestory, plus a narrow open stone viaduct to the north.
    for z,width,mat in [(0,12,TIMBER),(-39,7,STONE)]:
        m.box(0,z,20,0.55,width,OAK if mat==TIMBER else GREY,3.45,mat)
        for edge in (-1,1):
            m.box(0,z+edge*(width/2-.23),20,.94,.46,GREY,4,mat)
    for z in (-5.65,5.65):
        for x in (3,9): pair(x,z,.48,4.7,.48,COURT,OAK,TIMBER)
        m.box(0,z,20.8,.38,.54,OAK,8.42,TIMBER)
    m.box(0,0,21.2,.35,13,IRON_GREY,8.78,IRON)
    # Culvert through the central channel with an exposed mouth at either side.
    m.box(0,21,20,.55,7,GREY,3.45,STONE)
    # Half-length walls leave two staggered water-level doors. Both banks are mirrored.
    pair(6.8,17.72,6.4,3.45,.44,0)
    pair(6.8,24.28,6.4,3.45,.44,0)
    # Each half is built from exactly the same dimensions, including all cover and steps.
    # Gatehouse, two side gates and an enclosed courtyard.
    for z in (-19,19): pair(52,z,2,8,26)
    pair(52,0,2,3,12,9)
    # Stepped gate arch, lintel and projecting keystone.
    for z in (-5.7,5.7):
        pair(51,z,.9,4,.8,COURT,LIMESTONE,IRON)
        pair(51,z*.88,1,.8,1.6,7.9,LIMESTONE,IRON)
        pair(51,z*.70,1,.6,2.0,8.5,LIMESTONE,IRON)
    pair(50.9,0,1.1,.45,10,9,LIMESTONE,IRON)
    pair(50.6,0,.9,1.5,1.25,9,LIMESTONE,IRON)
    for z in (-32,32):
        pair(59,z,16,7,2)
        pair(89,z,28,7,2)
    pair(102,0,2,8,64)
    # Towers with overhanging cornices, merlons and enamelled team panels.
    for x,z,w,h in [(52,-30,7,10),(52,30,7,10),(52,-9,6,12),(52,9,6,12),
                     (100,-30,6,9),(100,30,6,9)]:
        pair(x,z,w,h,w)
        pair(x,z,w+.65,.45,w+.65,COURT+h-.45,LIMESTONE,IRON)
        for dx in (-1,0,1):
            for dz in (-1,0,1):
                if dx or dz: pair(x+dx*(w/2-.65),z+dz*(w/2-.65),1.3,1.0,1.3,COURT+h)
        pair(x-w/2-.06,z,.12,4.2,2.3,COURT+h-5.4,surface=IRON,team=True)
        pair(x-w/2-.14,z,.12,.28,2.35,COURT+h-4.3,LIMESTONE,IRON)
    for z in (-24,-20,-16,16,20,24):
        pair(52,z,2,1,1.75,12)
    # Inner keep: an offset doorway and a side entrance. The gallery stair opens inside.
    for z in (-7.5,7.5): pair(81,z,1.2,5.8,9)
    pair(81,0,1.2,1.6,6,8.2)
    pair(91,-12.6,21,5.8,1.2)
    pair(94,12.6,16,5.8,1.2)
    pair(101,0,1.2,5.8,26)
    pair(91,0,21.6,.45,27,9.8,IRON_GREY,IRON)
    # Stone canopy above the flag; contrasting back panel and an inset floor medallion.
    pair(99.9,0,.45,4.5,6,4.2,surface=IRON,team=True)
    for z in (-3.15,3.15): pair(99.6,z,.55,5.2,.45,COURT,LIMESTONE,IRON)
    pair(99.6,0,.55,.38,6.8,9,LIMESTONE,IRON)
    pair(94,0,6,.09,6,COURT,LIMESTONE,IRON)
    # Muster area, screened by a barracks wall but with exits around either end.
    pair(75,-21,1.6,4,11)
    # The forecourt breaks the sightline between both keeps. Vary cover by route.
    for x,z,w,h,d in [(36,0,4,2.8,9),(31,-29,8,3,5),(41,23,6,2,5),(25,39,9,2.4,4),
                       (65,4,5,2.2,4),(88,-29,4,2.5,3),(20,-7,4,1.15,3)]:
        sections=max(1,round(w/d))
        for i in range(sections):
            pair(x-w/2+(i+.5)*w/sections,z,w/sections,h,d,COURT,OAK,TIMBER,
                 model='res://assets/environement/Modular SciFi MegaKit[Standard]/glTF/Props/Prop_Crate3.gltf')
    # Inspection hatches and beams mark bends from inside the tunnel.
    for x,z in [(20,21),(36,18),(46,15),(56,22),(68,29),(79,19)]:
        pair(x,z,0.38,0.22,5.8,3.23,IRON_GREY,IRON)
    for side,sign in enumerate((-1,1)):
        m.marker(FLAG,sign*94,0,side,0,3)
        for x in (79,83,87,91,95):
            for z in (-25,-18):
                m.marker(TEAM,sign*x,z,side)
    return m


def write_lanterns(path):
    lines=['[gd_scene format=3]', '', '[sub_resource type="StandardMaterial3D" id="light"]',
           'albedo_color = Color(1,0.81,0.49,1)','emission_enabled = true',
           'emission = Color(1,0.65,0.30,1)','emission_energy_multiplier = 3.0','',
           '[sub_resource type="BoxMesh" id="glass"]','size = Vector3(0.38,0.16,0.66)',
           'material = SubResource("light")','',
           '[sub_resource type="StandardMaterial3D" id="brass"]','albedo_color = Color(0.68,0.46,0.19,1)','metallic = 0.58','roughness = 0.44','',
           '[sub_resource type="BoxMesh" id="trim"]','material = SubResource("brass")','',
           '[sub_resource type="CylinderMesh" id="pipe"]','top_radius = 0.12','bottom_radius = 0.12','height = 1','material = SubResource("brass")','',
           '[node name="Lanternes" type="Node3D"]']
    for sign in (-1,1):
        # Bronze chevrons on the gate standards and the flag-room wall.
        for i,(x,y,z) in enumerate([(48.77,12,-9),(48.77,12,9),(99.61,7.4,0)]):
            for tilt in (-1,1):
                lines+=['',f'[node name="Chevron_{sign+1}_{i}_{tilt+1}" type="MeshInstance3D" parent="."]',
                        f'position = Vector3({sign*x},{y},{z+tilt*.40})',f'rotation = Vector3({tilt*.61},0,0)',
                        'scale = Vector3(0.09,0.24,1.15)','mesh = SubResource("trim")']
        for i,(x,z,length,along_x) in enumerate([(25,18.36,29,True),(37,15.4,5,False),(47,12.4,24,True),(57,23,20,False),(69,32.4,27,True),(83.5,21,28,False)]):
            lines+=['',f'[node name="Conduite_{sign+1}_{i}" type="MeshInstance3D" parent="."]',
                    f'position = Vector3({sign*x},2.74,{z})',('rotation = Vector3(0,0,1.5708)' if along_x else 'rotation = Vector3(1.5708,0,0)'),
                    f'scale = Vector3(1,{length},1)','mesh = SubResource("pipe")']
        for i,(x,y,z,r) in enumerate([(17,3.1,21,8),(36,3.1,19,9),(48,3.1,15,9),(57,3.1,26,9),
                (69,3.1,35,10),(81,3.1,30,9),(81,3.1,14,10),(94,9,0,13),(84,9,6,10),(52,8.8,0,10),(8,8.4,0,10)]):
            name=f'Lampe_{sign+1}_{i}'
            lines+=['',f'[node name="{name}" type="MeshInstance3D" parent="."]',f'position = Vector3({sign*x},{y},{z})',
                    'mesh = SubResource("glass")','cast_shadow = 0','',f'[node name="Halo" type="OmniLight3D" parent="{name}"]',
                    'position = Vector3(0,-0.35,0)','light_color = Color(1,0.79,0.55,1)','light_energy = 2.6',f'omni_range = {r}',
                    'distance_fade_enabled = true','distance_fade_begin = 34','distance_fade_length = 16']
    path.write_bytes(('\n'.join(lines)+'\n').encode())


def minimap(m,path,px=1024):
    from PIL import Image, ImageDraw
    w,d=m.size
    image=Image.new('RGB',(px,px),(121,133,122))
    draw=ImageDraw.Draw(image)
    def rect(x,z,bx,bz,color):
        draw.rectangle(((x-bx/2+w/2)*px/w,(z-bz/2+d/2)*px/d,(x+bx/2+w/2)*px/w,(z+bz/2+d/2)*px/d),fill=color)
    rect(0,0,20,d,(53,76,83))
    for x,z,bx,by,bz,c,y,mat,style,solid in m.boxes:
        if y==3.45 and abs(x)<10: rect(x,z,bx,bz,(173,164,142))
        if y==COURT and by>=1: rect(x,z,bx,bz,(48,55,62))
    for sign in (-1,1):
        for x0,x1,n,s in GALLERIES:
            rect(sign*(x0+x1)/2,(n+s)/2,x1-x0,s-n,(76,98,103))
        for x0,x1,n,s in STAIRS:
            rect(sign*(x0+x1)/2,(n+s)/2,x1-x0,s-n,(211,181,123))
    for kind,x,z,team,_,r in m.markers:
        color=(77,166,248) if team==0 else (242,99,74)
        rect(x,z,5 if kind==FLAG else 1.3,5 if kind==FLAG else 1.3,color)
    image.save(path)


if __name__=='__main__':
    m=build()
    if '--minimap' not in sys.argv:
        target=Path(ROOT)/'scenes/maps/Drapeau.tscn'
        if target.exists() and '--force' not in sys.argv:
            sys.exit('La scène existe. Utiliser --force pour la régénérer.')
        write_lanterns(target.parent/'drapeau/Lanternes.tscn')
        m.write()
    minimap(m,Path(ROOT)/'assets/maps/drapeau.png')
