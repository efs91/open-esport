extends SceneTree
## Mesure les pièces modulaires des kits : taille (AABB) et décalage par rapport à l'origine.
## Godot --headless --path . --script res://tools/map/measure_kits.gd -- --out=C:/fichier.json

const KITS := {
	"starter": ["res://assets/environement/3D Scifi Starter Kit Free/_Meshes", "fbx"],
	"mega": ["res://assets/environement/Modular SciFi MegaKit[Standard]/glTF", "gltf"],
}


func _init() -> void:
	_run.call_deferred()


func _files(dir: String, ext: String, acc: Array) -> void:
	var d := DirAccess.open(dir)
	if d == null: return
	for f in d.get_files():
		if f.get_extension().to_lower() == ext: acc.append(dir + "/" + f)
	for sub in d.get_directories():
		_files(dir + "/" + sub, ext, acc)


func _aabb(n: Node, xf: Transform3D, acc: Array) -> void:
	if n is MeshInstance3D and n.mesh != null:
		var a: AABB = xf * n.transform * n.mesh.get_aabb() if false else (xf * n.transform) * n.mesh.get_aabb()
		acc.append(a)
	for c in n.get_children():
		if c is Node3D:
			_aabb(c, xf * n.transform if n is Node3D else xf, acc)
		else:
			_aabb(c, xf, acc)


func _run() -> void:
	var out := ""
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="): out = a.substr(6)
	var result := {}
	for kit in KITS:
		var files := []
		_files(KITS[kit][0], KITS[kit][1], files)
		files.sort()
		for f in files:
			var res = load(f)
			if res == null or not (res is PackedScene): continue
			var inst: Node = res.instantiate()
			var boxes := []
			_aabb(inst, Transform3D.IDENTITY, boxes)
			if boxes.is_empty():
				inst.free()
				continue
			var box: AABB = boxes[0]
			for b in boxes: box = box.merge(b)
			var tris := 0
			for m in inst.find_children("*", "MeshInstance3D", true, false):
				if m.mesh != null:
					for s in m.mesh.get_surface_count():
						tris += int(m.mesh.surface_get_array_len(s) / 3)
			result[kit + "|" + f.get_file().get_basename() + "|" + f.get_base_dir().get_file()] = {
				"path": f, "pos": [snappedf(box.position.x, 0.01), snappedf(box.position.y, 0.01), snappedf(box.position.z, 0.01)],
				"size": [snappedf(box.size.x, 0.01), snappedf(box.size.y, 0.01), snappedf(box.size.z, 0.01)], "verts": tris}
			inst.free()
	var fa := FileAccess.open(out, FileAccess.WRITE)
	fa.store_string(JSON.stringify(result, "  "))
	fa.close()
	print("pièces : ", result.size())
	quit()
