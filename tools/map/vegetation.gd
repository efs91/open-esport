extends RefCounted
## Place la végétation / les rochers calculés par place_vegetation.py grâce à l'instancer de Terrain3D.

const DATA := "res://maps/frontiere01/data/"
const MODELS := "res://maps/frontiere01/models/"


static func _read(path: String) -> PackedByteArray:
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return PackedByteArray()
	return f.get_buffer(f.get_length())


static func apply(terrain: Terrain3D) -> void:
	var text := FileAccess.get_file_as_string(DATA + "manifest.json")
	if text.is_empty():
		push_warning("pas de manifest.json : lance place_vegetation.py")
		return
	var manifest: Array = JSON.parse_string(text)
	var assets := terrain.assets
	# 1) les modèles
	for m in manifest:
		var scene := load(MODELS + m["glb"] + ".glb") as PackedScene
		if scene == null:
			push_error("modèle introuvable : " + m["glb"])
			continue
		var ma := Terrain3DMeshAsset.new()
		ma.name = m["name"]
		ma.id = int(m["id"])
		ma.scene_file = scene
		ma.visibility_range = float(m["vis"])
		ma.cast_shadows = int(m["shadows"])
		if m["glb"].begins_with("fir_far"):
			# version lointaine : invisible de près (fondu entre 95 et 125 m), pour ne pas voir les cônes à travers les branches
			var mat := StandardMaterial3D.new()
			mat.albedo_color = Color(0.05, 0.11, 0.05)
			mat.roughness = 1.0
			mat.distance_fade_mode = BaseMaterial3D.DISTANCE_FADE_PIXEL_DITHER
			mat.distance_fade_min_distance = 95.0
			mat.distance_fade_max_distance = 125.0
			ma.material_override = mat
		assets.set_mesh_asset(int(m["id"]), ma)
	# 2) les instances
	var total := 0
	for m in manifest:
		var bytes := _read(DATA + "inst_%d.bin" % int(m["id"]))
		var f := bytes.to_float32_array()
		var n := f.size() / 12
		var list: Array[Transform3D] = []
		list.resize(n)
		for i in n:
			var o := i * 12
			var basis := Basis(Vector3(f[o], f[o + 3], f[o + 6]), Vector3(f[o + 1], f[o + 4], f[o + 7]), Vector3(f[o + 2], f[o + 5], f[o + 8]))
			list[i] = Transform3D(basis, Vector3(f[o + 9], f[o + 10], f[o + 11]))
		terrain.instancer.add_transforms(int(m["id"]), list, PackedColorArray(), false)
		total += n
	terrain.instancer.force_update_mmis()
	print("végétation : %d assets, %d instances" % [manifest.size(), total])
