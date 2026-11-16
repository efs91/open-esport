"""Coupe un coffret généré par Meshy en deux pièces, « Base » et « Lid » (couvercle), pour pouvoir l'ouvrir en jeu.
Le couvercle pivote autour d'une charnière placée sur l'arête arrière haute de la base (axe X).
  blender -b -P split_lid.py -- entrée.glb sortie.glb <fraction de hauteur où couper> [taille_max_en_m]
La coupe passe par un plan horizontal à `fraction` de la hauteur du modèle ; les deux faces sont rebouchées."""
import bpy, bmesh, sys, os, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
src, dst, frac = argv[0], argv[1], float(argv[2])
size = float(argv[3]) if len(argv) > 3 else 0.3

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
for o in bpy.context.scene.objects:
    o.select_set(o.type == "MESH")
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
obj = bpy.context.view_layer.objects.active
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

# mise à l'échelle : la plus grande dimension vaut `size` mètres ; pieds du coffret à z = 0, centré
dims = obj.dimensions
k = size / max(dims)
obj.scale = (k, k, k)
bpy.ops.object.transform_apply(scale=True)
vs = [v.co for v in obj.data.vertices]
cx = (min(v.x for v in vs) + max(v.x for v in vs)) / 2
cy = (min(v.y for v in vs) + max(v.y for v in vs)) / 2
zmin = min(v.z for v in vs)
for v in obj.data.vertices:
    v.co.x -= cx; v.co.y -= cy; v.co.z -= zmin
zmax = max(v.co.z for v in obj.data.vertices)
ymax = max(v.co.y for v in obj.data.vertices)
zc = zmax * frac

# coupe
bm = bmesh.new()
bm.from_mesh(obj.data)
geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
res = bmesh.ops.bisect_plane(bm, geom=geom, plane_co=(0, 0, zc), plane_no=(0, 0, 1), clear_inner=False, clear_outer=False)
cut_edges = [e for e in res["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
bm.to_mesh(obj.data)
bm.free()

# séparation : les faces dont le centre est au-dessus du plan forment le couvercle
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="DESELECT")
bpy.ops.object.mode_set(mode="OBJECT")
for p in obj.data.polygons:
    p.select = p.center.z > zc
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.separate(type="SELECTED")
bpy.ops.object.mode_set(mode="OBJECT")
parts = [o for o in bpy.context.scene.objects if o.type == "MESH"]
parts.sort(key=lambda o: sum(v.co.z for v in o.data.vertices) / max(1, len(o.data.vertices)))
base, lid = parts[0], parts[-1]
base.name, lid.name = "Base", "Lid"

# reboucher les coupes (plan z = zc)
for o in (base, lid):
    bm = bmesh.new()
    bm.from_mesh(o.data)
    edges = [e for e in bm.edges if e.is_boundary and all(abs(v.co.z - zc) < 1e-4 for v in e.verts)]
    if edges:
        bmesh.ops.holes_fill(bm, edges=edges, sides=0)
        # faces de bouchon : couleur neutre (on garde le matériau existant)
    bm.to_mesh(o.data)
    bm.free()

# charnière : l'origine du couvercle est sur l'arête arrière du plan de coupe
for o in (lid,):
    o.select_set(True)
bpy.context.view_layer.objects.active = lid
bpy.context.scene.cursor.location = (0, ymax * 0.98, zc)
bpy.ops.object.origin_set(type="ORIGIN_CURSOR")

for o in bpy.context.scene.objects:
    o.select_set(o.type == "MESH")
os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=dst, export_format="GLB", use_selection=True, export_yup=True)
print("OK", dst, "hauteur %.3f, coupe à %.3f, charnière y=%.3f" % (zmax, zc, ymax))
