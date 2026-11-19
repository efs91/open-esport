"""Décor du Terminal des Cimes. Les volumes jouables sont définis dans gen_sabotage_map."""
from pathlib import Path
from math import atan2, hypot, pi

KIT = 'res://assets/environement/Modular SciFi MegaKit[Standard]/glTF/Props/'
PROPS = ['Prop_Vent_Big', 'Prop_Fan_Small', 'Prop_Light_Wide', 'Prop_PipeHolder']
COLORS = {
    'iron': (0.19,0.23,0.25), 'zinc': (0.57,0.62,0.63), 'ochre': (0.92,0.55,0.12),
    'teal': (0.11,0.52,0.57), 'ivory': (0.88,0.86,0.74), 'glass': (0.09,0.24,0.30),
    'lamp': (1,0.68,0.32),
}


def vec(values):
    return 'Vector3(' + ', '.join(f'{v:.4f}'.rstrip('0').rstrip('.') or '0' for v in values) + ')'


def write_terminal(path, grid, x0, z0, cell):
    lines = ['[gd_scene format=3]', '']
    def add(*args):
        lines.extend(args)
    for prop in PROPS:
        add(f'[ext_resource type="PackedScene" path="{KIT}{prop}.gltf" id="{prop}"]')
    for name,(r,g,b) in COLORS.items():
        add('',f'[sub_resource type="StandardMaterial3D" id="{name}"]',f'albedo_color = Color({r},{g},{b},1)',
            'metallic = 0.52','roughness = 0.42')
        if name == 'lamp': add('emission_enabled = true','emission = Color(1,0.55,0.22,1)','emission_energy_multiplier = 2.8')
    add('','[sub_resource type="BoxMesh" id="box"]','size = Vector3(1,1,1)',
        '','[sub_resource type="CylinderMesh" id="drum"]','top_radius = 0.5','bottom_radius = 0.5','height = 1','radial_segments = 24',
        '','[node name="Terminal" type="Node3D"]')
    serial = 0
    parent = '.'
    def group(name,pos=(0,0,0),yaw=0):
        nonlocal parent
        add('',f'[node name="{name}" type="Node3D" parent="."]',f'position = {vec(pos)}',f'rotation = {vec((0,yaw,0))}')
        parent = name
    def mesh(name,pos,size,mat,kind='box',rot=(0,0,0)):
        nonlocal serial
        serial += 1
        add('',f'[node name="{name}_{serial}" type="MeshInstance3D" parent="{parent}"]',f'position = {vec(pos)}',
            f'rotation = {vec(rot)}',f'scale = {vec(size)}', f'mesh = SubResource("{kind}")',f'material_override = SubResource("{mat}")')
    def prop(name,pos,size=(1,1,1),rot=(0,0,0)):
        nonlocal serial
        serial+=1
        add('',f'[node name="{name}_{serial}" parent="{parent}" instance=ExtResource("{name}")]',f'position = {vec(pos)}',
            f'scale = {vec(size)}',f'rotation = {vec(rot)}')
    def label(text,pos,pixel,yaw=0):
        nonlocal serial
        serial+=1
        add('',f'[node name="Signal_{serial}" type="Label3D" parent="{parent}"]',f'position = {vec(pos)}',
            f'rotation = {vec((0,yaw,0))}',f'text = "{text}"','font_size = 100',f'pixel_size = {pixel}',
            'outline_size = 0','modulate = Color(0.98,0.94,0.81,1)','double_sided = false')
    # Twin trusses and travelling hoist over the loading court.
    group('Portique_A',(-64,0,-11))
    for z in (-12,12):
        for y in (8.35,10.05):
            mesh('Longeron',(0,y,z),(32.8,0.28,0.55),'ochre')
        for i in range(12):
            mesh('Treillis',(-16+(i+.5)*32/12,9.2,z),(hypot(32/12,1.7),.19,.27),'ochre',
                 rot=(0,0,(-1)**i*atan2(1.7,32/12)))
    for x in (-16,16,4):
        mesh('Traverse',(x,8.6,0),(.56,.62,24.5),'iron')
    mesh('Treuil',(4,8.55,3),(2.4,1.5,3.2),'ochre')
    for x in (3.55,4.45):
        mesh('Elingue',(x,6.8,3),(.07,2.4,.07),'iron')
    mesh('Palonnier',(4,5.61,3),(2.9,.24,.45),'ochre')
    mesh('Plaque',(-9,9.19,12.32),(8.8,1.2,.12),'iron')
    label('A   /   FRET',(-9,9.20,12.40),.011)
    # Cooling drums on the roof behind B. A different skyline and colour from A.
    group('Station_B',(72,0,-31))
    for x in (-5,3):
        for y,diam,height,mat in [(9.0,5.7,4,'zinc'),(11.5,6.2,1.3,'teal'),(12.27,6.45,.26,'iron')]:
            mesh('Aerorefrigerant',(x,y,0),(diam,height,diam),mat,'drum')
        prop('Prop_Fan_Small',(x,12.42,0),(2.6,1,2.6))
        for z in (-1.2,1.2):
            mesh('Conduite',(x,9.7,z+3.2),(.4,.4,6),'teal')
    group('Vigie',(-2,0,-6))
    mesh('Vitrage',(0,4.8,2.86),(7.56,1.65,.035),'glass')
    for x in (-3.65,0,3.65):
        mesh('Montant',(x,4.85,2.88),(.16,2,.22),'zinc')
    mesh('Toiture',(0,5.96,0),(8.4,.32,6.3),'iron')
    mesh('Mat',(2.4,8.6,-1.4),(.16,5.2,.16),'zinc')
    mesh('Balise',(2.4,11.25,-1.4),(.22,.24,.22),'lamp')
    # Door/vent assemblies stay flush with the closed volume. A continuous facade
    # gets a few concentrated service details, with uncluttered stretches between.
    dirs=[(0,1,0),(0,-1,pi),(1,0,pi/2),(-1,0,-pi/2)]
    facade=0
    for dx,dz,yaw in dirs:
        edges=set()
        for row,line in enumerate(grid.g):
            for col,v in enumerate(line):
                nc,nr=col+dx,row+dz
                if v=='#' and 0<=nr<len(grid.g) and 0<=nc<len(line) and grid.g[nr][nc]!='#':
                    edges.add((col,row))
        along=(1,0) if dz else (0,1)
        for c,r in sorted(edges):
            if (c-along[0],r-along[1]) in edges: continue
            n=1
            while (c+n*along[0],r+n*along[1]) in edges: n+=1
            if n<3: continue
            for k in range(1,n-1,7):
                x=x0+(c+.5+k*along[0]+dx*.5)*cell
                z=z0+(r+.5+k*along[1]+dz*.5)*cell
                facade+=1
                group(f'Facade_{facade}',(x,0,z),yaw)
                if facade%3:
                    mesh('Dormant',(0,2.0,.035),(4.4,4.0,.08),'iron')
                    mesh('Rideau',(0,1.85,.09),(3.9,3.65,.07),'zinc')
                    for h in (.75,1.65,2.55,3.45):
                        mesh('Lame',(0,h,.135),(3.85,.07,.035),'iron')
                    mesh('Marquise',(0,3.89,.28),(4.6,.18,.72),'iron')
                    mesh('Lampe',(0,3.78,.48),(1.45,.07,.1),'lamp')
                else:
                    prop('Prop_Vent_Big',(0,2.15,.035),(1.6,1.4,1.6),(pi/2,0,0))
                    prop('Prop_Light_Wide',(1.15,3.55,.1),(.95,.95,.95))
    # Large painted identifiers are readable from approach corridors.
    signs=[('A_Fret',(-70,0,-32),0,'A  /  QUAI','ochre'),('B_Turbines',(74,0,-24),0,'B  /  TURBINES','teal'),
           ('A_Retour',(-84,0,-13),pi/2,'A   01','ochre'),('B_Retour',(84,0,-3),-pi/2,'B   02','teal'),
           ('Transit',(-4,0,-24),0,'TRANSIT','iron'),('Depart_Sud',(0,0,88),pi,'TERMINAL SUD','iron'),
           ('Depart_Nord',(0,0,-88),0,'DOUANE NORD','iron')]
    for name,pos,yaw,title,mat in signs:
        group(name,pos,yaw)
        mesh('Enseigne',(0,4.85,.11),(9.8,1.9,.18),mat)
        label(title,(0,4.86,.23),.0105)
    # Recessed loading stripes and fork-lift bays, painted onto the pavement.
    for site,(x,z),mat in [('A',(-64,-12),'ochre'),('B',(64,-4),'teal')]:
        group('Marquage_'+site,(x,0,z))
        for side in (-1,1):
            mesh('Ligne',(side*8,.014,0),(.14,.015,16),mat)
            mesh('Ligne',(0,.014,side*8),(16,.015,.14),mat)
        for i in range(8):
            mesh('Hachure',(-6.5+1.8*i,.02,8.6),(.24,.016,1.4),mat,rot=(0,-.52,0))
    group('Halle_est',(76,0,-4))
    for z in (-14,-7,0,7,14):
        mesh('Poutre',(0,6.02,z),(16,.25,.33),'teal')
        mesh('Reglette',(0,5.87,z),(5.8,.07,.13),'lamp')
    # White freight stencils on the ends of the three tallest site loads.
    for name,x,z,width,height,code in [('Lot_A',-71,-12.48,4,3,'TC  071'),('Lot_B',56,-7.98,8,3.1,'TC  209'),('Lot_B2',72,3.02,4,2.7,'TC  214')]:
        group(name,(x,0,z))
        for side in (-1,1):
            mesh('Verrou',(side*width*.38,height*.5,.025),(.055,height*.91,.055),'zinc')
        label(code,(0,height*.73,.035),.0048)
    group('Service_couvert',(68,5.68,33))
    for z in (-4,0,4):
        prop('Prop_PipeHolder',(0,.02,z),(1.2,1.2,1.2))
    for x in (-1,1):
        mesh('Collecteur',(x,.85,0),(.56,13.5,.56),'teal','drum',(pi/2,0,0))
    Path(path).write_bytes(('\n'.join(lines)+'\n').encode())
