"""Liste, pour chaque collection utile d'une scène Blender, les objets avec leur nombre de sommets (évalué : modificateurs appliqués),
leurs dimensions et leurs matériaux. Lancement :
  blender -b fichier.blend --python inspect_assets.py -- collection1 collection2 ...   (sans argument : toutes)"""
import bpy, sys

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
dg = bpy.context.evaluated_depsgraph_get()


def info(o):
    try:
        e = o.evaluated_get(dg)
        m = e.to_mesh()
        v, f = len(m.vertices), len(m.polygons)
        e.to_mesh_clear()
    except Exception:
        v = f = -1
    mats = [s.material.name for s in o.material_slots if s.material]
    return v, f, tuple(round(x, 2) for x in o.dimensions), mats


def walk(c, depth=0):
    if not args or c.name in args or depth > 0:
        pass
    show = (not args) or any(a == c.name for a in args)
    if show:
        print("[%s]" % c.name)
        for o in c.objects:
            if o.type in ("MESH", "CURVE"):
                v, f, d, mats = info(o)
                print("   %-34s v=%-7d f=%-7d dim=%s mats=%s" % (o.name, v, f, d, ",".join(mats)))
    for ch in c.children:
        walk(ch, depth + 1)


for c in bpy.data.collections:
    show = (not args) or c.name in args
    if show:
        print("[%s]" % c.name)
        for o in c.objects:
            if o.type in ("MESH", "CURVE"):
                v, f, d, mats = info(o)
                print("   %-34s v=%-7d f=%-7d dim=%s loc=%s mats=%s" % (o.name, v, f, d, tuple(round(x, 1) for x in o.location), ",".join(mats)))
