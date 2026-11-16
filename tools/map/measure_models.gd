extends SceneTree
## Dimensions des modèles de végétation / rochers : res://tools/map/models.json
func _init() -> void:
	var out := {}
	var dir := "res://maps/frontiere01/models/"
	for f in DirAccess.get_files_at(dir):
		if not f.ends_with(".glb"): continue
		var inst: Node = load(dir + f).instantiate()
		var box := AABB()
		var first := true
		for mi in inst.find_children("*", "MeshInstance3D", true, false):
			var a: AABB = (mi as Node3D).transform * mi.mesh.get_aabb()
			box = a if first else box.merge(a)
			first = false
		out[f.get_basename()] = [snappedf(box.size.x, 0.01), snappedf(box.size.y, 0.01), snappedf(box.size.z, 0.01)]
		inst.free()
	var fa := FileAccess.open("res://tools/map/models.json", FileAccess.WRITE)
	fa.store_string(JSON.stringify(out))
	fa.close()
	print("modèles : ", out.size())
	quit()
