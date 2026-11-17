"""Procedural open U-shaped 2x holographic reflex sight (meters, Z up, -Y forward).
Exports usable 3D mesh with separate named parts and no geometry crossing the aperture.
Requires: trimesh numpy shapely.
"""
from pathlib import Path
import math, json, zipfile
import numpy as np
import trimesh
from shapely.geometry import Polygon
from shapely import constrained_delaunay_triangles

OUT=Path('/mnt/data')
scene=trimesh.Scene()
C={
'frame':(43,47,53,255), 'frame_side':(27,32,39,255),
'panel':(69,76,84,255), 'panel_lite':(89,94,103,255),
'base':(25,29,35,255), 'trim':(106,111,116,255),
'rubber':(19,22,27,255), 'screw':(88,93,99,255),
'cyan':(26,228,249,255), 'softcyan':(64,171,197,255),
'orange':(224,117,44,255), 'text':(89,246,255,255)
}
materials={}
try:
    from trimesh.visual.material import PBRMaterial
    materials={key:PBRMaterial(name=key,baseColorFactor=np.array(rgba)/255,metallicFactor=0.62 if key not in ('cyan','text','softcyan') else .10,roughnessFactor=.43 if key not in ('cyan','text','softcyan') else .22, emissiveFactor=np.array(rgba[:3])/255*.85 if key in ('cyan','text','softcyan') else [0,0,0],alphaMode='OPAQUE') for key,rgba in C.items()}
except Exception: pass

def add(mesh,name,mat):
    mesh.metadata['name']=name
    if materials: mesh.visual=trimesh.visual.TextureVisuals(material=materials[mat])
    else: mesh.visual.vertex_colors=np.tile(C[mat], (len(mesh.vertices),1))
    scene.add_geometry(mesh,geom_name=name,node_name=name)

def poly_mesh(pts,y0,y1):
    """Extrude arbitrary simple polygon in X,Z plane between y0(front) and y1(back)."""
    p=Polygon(pts)
    if not p.is_valid or p.area==0: raise ValueError(f'bad polygon {pts}')
    if p.exterior.is_ccw is False:
       pts=list(reversed(pts));p=Polygon(pts)
    ring=list(p.exterior.coords)[:-1]
    d={tuple(np.round(v,8)):i for i,v in enumerate(ring)}
    # triangulation may produce interior vertices, so keep mapping general
    verts=[[x,y0,z] for x,z in ring]+[[x,y1,z] for x,z in ring]
    n=len(ring)
    faces=[]
    def point_index(x,z):
        key=tuple(np.round((x,z),8))
        if key not in d:
            d[key]=len(verts)//2 # not expected for shapely triangulation
        return d[key]
    for t in constrained_delaunay_triangles(p).geoms:
        if not p.covers(t.representative_point()):continue
        tri=list(t.exterior.coords)[:-1]
        ids=[point_index(x,z) for x,z in tri]
        # signed area in X/Z, CCW yields front normal negative Y
        area=sum(tri[i][0]*tri[(i+1)%3][1]-tri[(i+1)%3][0]*tri[i][1] for i in range(3))
        if area<0: ids.reverse()
        faces.append(ids); faces.append([j+n for j in ids[::-1]])
    for i in range(n):
        j=(i+1)%n
        faces.extend([[i,j,j+n],[i,j+n,i+n]])
    mesh=trimesh.Trimesh(vertices=np.array(verts),faces=np.array(faces),process=False)
    return mesh

def poly(name,pts,yf,yb,mat):add(poly_mesh(pts,yf,yb),name,mat)

def cube(name,center,size,mat):
    mesh=trimesh.creation.box(extents=size)
    mesh.apply_translation(center);add(mesh,name,mat)

def rod(name,start,end,width,mat,nsides=8):
    a,b=np.asarray(start),np.asarray(end)
    direction=b-a;length=np.linalg.norm(direction)
    if length<1e-6:return
    cyl=trimesh.creation.cylinder(radius=width/2,height=length,sections=nsides)
    cyl.apply_transform(trimesh.geometry.align_vectors([0,0,1],direction/length))
    cyl.apply_translation((a+b)/2)
    add(cyl,name,mat)

def cyl(name,center,radius,height,axis,mat,nsides=24):
    m=trimesh.creation.cylinder(radius=radius,height=height,sections=nsides)
    m.apply_transform(trimesh.geometry.align_vectors([0,0,1],axis))
    m.apply_translation(center);add(m,name,mat)

def mirror(points):return [(-x,z) for x,z in reversed(points)]

# OPEN MAIN CHASSIS: frame arms with tall vertical sides; crossbar is BELOW the sight window.
left=[(-.054,.026),(-.061,.038),(-.061,.083),(-.058,.090),(-.043,.109),(-.035,.109),(-.035,.102),(-.048,.084),(-.048,.043),(-.038,.034)]
right=mirror(left)
poly('FRAME_Left_U_arm',left,-.021,.022,'frame')
poly('FRAME_Right_U_arm',right,-.021,.022,'frame')
base=[(-.048,.017),(-.053,.026),(-.046,.038),(-.039,.040),(.039,.040),(.046,.038),(.053,.026),(.048,.017),(.038,.012),(-.038,.012)]
poly('FRAME_Low_crossbar_below_sight',base,-.030,.039,'frame')
# Smaller face plates add surface layering to mechanical frame
left_panel=[(-.056,.041),(-.057,.083),(-.054,.090),(-.042,.104),(-.040,.104),(-.050,.083),(-.050,.044)]
for name,points in [('Left',left_panel),('Right',mirror(left_panel))]:
    poly('ARM_Face_plate_'+name,points,-.023,-.021,'panel')
    # exposed outer metallic straight edge
    x=-.060 if name=='Left' else .060
    rod('ARM_Metal_rail_'+name,(x,-.024,.046),(x,-.024,.081),.0028,'trim',6)
# For the aiming aperture the mounts/crossbar all remain UNDER z=0.04
plate=[(-.041,.020),(-.037,.032),(.037,.032),(.041,.020),(.031,.016),(-.031,.016)]
poly('FRONT_Brand_plate',plate,-.032,-.030,'base')
# accent armored lower cover edges
for side in (-1,1):
    x=side*.035
    poly('Lower_Side_Inlay_'+str(side), [(x-.004,.014),(x+.004,.014),(x+.008,.022),(x+.003,.028)],-.034,-.031,'panel')
    cyl('Fastener_L_'+str(side),(side*.042,-.0345,.024),.0018,.002,[0,1,0],'screw',12)
# Metal wear and orange tiny ID bars on non-optical base
for i in range(3):
    poly(f'Orange_emblem_{i}',[(.025+i*.004,.015),(.027+i*.004,.015),(.030+i*.004,.023),(.028+i*.004,.023)],-.035,-.033,'orange')

# LEDs just INSIDE the chassis arm surfaces, NO solid support in the open central window.
for side in (-1,1):
    x=side*.0475
    rod(f'LED_inner_vertical_{side}',(x,-.024,.046),(x,-.024,.080),.0016,'cyan',6)
    rod(f'LED_inner_lower_diagonal_{side}',(x,-.024,.046),(side*.037,-.024,.036),.0016,'cyan',6)
    # top 2 subtle marks
    rod(f'LED_top_tip_{side}',(side*.046,-.024,.096),(side*.041,-.024,.103),.0012,'softcyan',6)

# Actual mechanical body, wholly below/behind the bottom of open window.
cube('Riser_Main_block',(0,.037,.015),(.075,.067,.023),'base')
poly('Riser_Top_profile',[(-.042,.019),(-.038,.031),(.038,.031),(.042,.019)], .035,.064,'panel')
for side in (-1,1):
    x=side*.032
    cube('Clamping_jaw_'+str(side),(x,.054,.005),(.018,.043,.012),'frame')
    cube('Clamping_tooth_'+str(side),(x,.059,-.003),(.014,.027,.005),'rubber')
    cyl('Rear_mount_bolt_'+str(side),(x,.080,.009),.003,.003,[0,1,0],'screw',12)
# Weaver/Picatinny tooth underneath (will remain outside the aperture)
for i in range(4):
    cube('Rail_tread_'+str(i),(0,.022+i*.012,-.005),(.043,.004,.004),'trim')
# Reticle supports zero optical obstruction: floating emissive geometry only.
# If implementing the reticle in the game HUD, hide the nodes prefixed RETICLE_.
z=.067; y=-.012
for s in (-1,1):
    p1=(s*.009,y,z+.004);p2=(s*.0127,y,z);p3=(s*.009,y,z-.004)
    rod(f'RETICLE_UpperChevron_{s}',p1,p2,.0008,'cyan',6)
    rod(f'RETICLE_LowerChevron_{s}',p2,p3,.0008,'cyan',6)
    rod(f'RETICLE_Dash_{s}',(s*.007,y,z+.004),(s*.006,y,z+.004),.0007,'cyan',6)
# dot center and short ladder marks
cyl('RETICLE_Center_dot',(0,y,z),.00085,.0007,[0,1,0],'text',12)
rod('RETICLE_Holdover_1',(0,y,z-.009),(0,y,z-.011),.00055,'cyan',6)
rod('RETICLE_Holdover_2',(0,y,z-.017),(0,y,z-.019),.00055,'cyan',6)

# A knob on the right side, not protruding INTO the lens.
cyl('KNOB_Rubber_adjustment',(.065,.012,.023),.011,.016,[1,0,0],'rubber',24)
cyl('KNOB_Outer_ring',(.075,.012,.023),.010,.002,[1,0,0],'trim',24)
cyl('KNOB_Center_panel',(.077,.012,.023),.0076,.0018,[1,0,0],'frame',24)
for i in range(16):
    t=i*math.pi*2/16
    cy=.012+.0105*math.sin(t); cz=.023+.0105*math.cos(t)
    rod('KNOB_Grippattern_%02d'%i,(.059,cy,cz),(.072,cy,cz),.0012,'panel',5)
# 2x LED lettering on front base, thick segmented mechanical strokes
# glyph 2 from top-left to top-right, then diagonal center and lower base.
front=-.036
segments2=[(-.008,.027),(.001,.027),(.001,.024),(-.008,.021),(-.008,.018),(.001,.018)]
for i in range(len(segments2)-1):
    a,b=segments2[i:i+2]
    rod(f'LABEL_2_{i}',(a[0],front,a[1]),(b[0],front,b[1]),.0017,'text',6)
rod('LABEL_X_1',(.006,front,.027),(.015,front,.018),.0018,'text',6)
rod('LABEL_X_2',(.015,front,.027),(.006,front,.018),.0018,'text',6)

# Two minor scratches highlights on the face panels
for side in (-1,1):
    rod('ENGRAVING_Slash_A_'+str(side),(side*.054,-.024,.067),(side*.055,-.024,.071),.00055,'trim',5)
    rod('ENGRAVING_Slash_B_'+str(side),(side*.053,-.024,.061),(side*.056,-.024,.063),.00045,'trim',5)

# Export glTF binary; GLB has all materials and explicit meshes as separate nodes.
glb=OUT/'viseur_holographique_U_x2.glb'
glb.write_bytes(scene.export(file_type='glb'))
# Export OBJ + its MTL for software with no GLB support
from trimesh.exchange.obj import export_obj
obj_text, texture_map=export_obj(scene, include_color=True, return_texture=True, mtl_name='viseur_holographique_U_x2.mtl')
(OUT/'viseur_holographique_U_x2.obj').write_text(obj_text,encoding='utf8')
for fname,blob in texture_map.items():
    (OUT/fname).write_bytes(blob if isinstance(blob,bytes) else blob.encode('utf8'))
# Make ZIP with two interchange formats and source script
with zipfile.ZipFile(OUT/'viseur_holographique_U_x2_pack.zip','w',zipfile.ZIP_DEFLATED) as zf:
    for f in [glb,OUT/'viseur_holographique_U_x2.obj',*map(lambda f:OUT/f,texture_map),OUT/'generate_viseur_2x.py']:
        if f.exists(): zf.write(f,f.name)
print('meshes',len(scene.geometry),'GLB bytes',glb.stat().st_size,'textures',list(texture_map))
print('bounding',scene.bounds)
