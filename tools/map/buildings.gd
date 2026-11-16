extends RefCounted
## Instancie les bâtiments calculés par gen_buildings.py (pièces des kits + boîtes de collision).

const DATA := "res://maps/frontiere01/data/"


static func apply(scene: Node3D) -> void:
	var text := FileAccess.get_file_as_string(DATA + "buildings.json")
	if text.is_empty():
		push_warning("pas de buildings.json : lance gen_buildings.py")
		return
	var d: Dictionary = JSON.parse_string(text)
	var kits: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://tools/map/kits.json"))
	var root := Node3D.new()
	root.name = "Zones"
	scene.add_child(root)
	root.owner = scene
	var cache := {}
	var missing := {}
	var boxes: Array = d["boxes"].duplicate()          # boîtes données par le générateur (marches d'escalier, caisses empilées…)
	for p in d["pieces"]:
		var key: String = p["k"]
		if not cache.has(key):
			var info = kits.get(key)
			cache[key] = load("res://" + info["path"]) if info != null else null
		var ps: PackedScene = cache[key]
		if ps == null:
			missing[key] = true
			continue
		var n: Node3D = ps.instantiate()
		n.position = Vector3(p["p"][0], p["p"][1], p["p"][2])
		n.rotation_degrees = Vector3(0, p["r"], 0)
		root.add_child(n)
		n.owner = scene
		if int(p.get("c", 1)) == 1:
			var b := _piece_box(n, float(p.get("mt", 0.3)), float(p.get("fs", 0.3)))
			if b.size != Vector3.ZERO:
				boxes.append([b.position.x, b.position.y, b.position.z, b.end.x, b.end.y, b.end.z])
	for m in missing:
		push_warning("pièce introuvable : " + m)

	# collisions : un corps statique, une boîte par obstacle (les mêmes boîtes sont envoyées au serveur)
	var body := StaticBody3D.new()
	body.name = "BuildingColliders"
	scene.add_child(body)
	body.owner = scene
	for b in boxes:
		var cs := CollisionShape3D.new()
		var bs := BoxShape3D.new()
		bs.size = Vector3(b[3] - b[0], b[4] - b[1], b[5] - b[2])
		cs.shape = bs
		cs.position = Vector3((b[0] + b[3]) / 2.0, (b[1] + b[4]) / 2.0, (b[2] + b[5]) / 2.0)
		body.add_child(cs)
		cs.owner = scene
	# fichier final lu par l'export du serveur : boîtes (pièces + manuelles), butin, apparitions
	for i in boxes.size():
		boxes[i] = [snappedf(boxes[i][0], 0.001), snappedf(boxes[i][1], 0.001), snappedf(boxes[i][2], 0.001), snappedf(boxes[i][3], 0.001), snappedf(boxes[i][4], 0.001), snappedf(boxes[i][5], 0.001)]
	var f := FileAccess.open(DATA + "buildings_final.json", FileAccess.WRITE)
	f.store_string(JSON.stringify({"boxes": boxes, "loot": d["loot"], "spawns": d["spawns"]}))
	f.close()
	print("bâtiments : %d pièces, %d boîtes" % [d["pieces"].size(), boxes.size()])


## Boîte (dans le repère de la carte) de la géométrie d'une pièce posée. Les plans sans épaisseur (sols) deviennent une dalle fine
## sous leur surface ; les murs trop minces sont épaissis pour que le joueur et les balles ne passent pas au travers.
static func _piece_box(n: Node3D, min_thick: float, flat_slab: float) -> AABB:
	var acc: Array = []
	_collect(n, n.transform, acc)
	if acc.is_empty():
		return AABB()
	var box: AABB = acc[0]
	for a in acc:
		box = box.merge(a)
	var mn := box.position
	var mx := box.end
	if mx.y - mn.y < 0.3:
		mn.y = mx.y - flat_slab
	if mx.x - mn.x < min_thick:
		var c := (mn.x + mx.x) / 2.0
		mn.x = c - min_thick / 2.0
		mx.x = c + min_thick / 2.0
	if mx.z - mn.z < min_thick:
		var c2 := (mn.z + mx.z) / 2.0
		mn.z = c2 - min_thick / 2.0
		mx.z = c2 + min_thick / 2.0
	return AABB(mn, mx - mn)


static func _collect(node: Node, xf: Transform3D, acc: Array) -> void:
	if node is MeshInstance3D and node.mesh != null and node.visible:
		acc.append(xf * node.mesh.get_aabb())
	for c in node.get_children():
		if c is Node3D:
			_collect(c, xf * (c as Node3D).transform, acc)
