extends SceneTree
## Construit la carte Frontière 01 : relief + peinture (générés par gen_terrain.py), textures, assets du terrain.
## Lancement :  Godot --headless --path . --script res://tools/map/build_frontiere.gd
## Sortie : res://maps/frontiere01/terrain/*.res (régions) et res://scenes/maps/Frontiere01.tscn

const DATA := "res://maps/frontiere01/data/"
const TERRAIN_DIR := "res://maps/frontiere01/terrain"
const TEX_DIR := "res://maps/frontiere01/textures/"
const SCENE_OUT := "res://scenes/maps/Frontiere01.tscn"
const HALF := 768

# (nom, échelle UV, détiling, teinte)
const TEXTURES := [
	["forest_ground", 0.09, 0.15, Color(1, 1, 1)],   # 0
	["forest_leaves", 0.09, 0.15, Color(1, 1, 1)],   # 1
	["cliff",         0.06, 0.20, Color(0.95, 0.92, 0.9)],   # 2
	["sand",          0.08, 0.15, Color(1.0, 0.95, 0.9)],    # 3
	["dry_gravel",    0.08, 0.15, Color(1, 1, 1)],   # 4
	["dirt_road",     0.12, 0.10, Color(0.78, 0.7, 0.6)],   # 5
	["concrete",      0.12, 0.10, Color(0.42, 0.41, 0.4)],   # 6
	["asphalt",       0.15, 0.10, Color(0.8, 0.8, 0.82)],   # 7
	["metal",         0.15, 0.10, Color(0.75, 0.78, 0.82)],   # 8
]


func _read(path: String) -> PackedByteArray:
	var f := FileAccess.open(path, FileAccess.READ)
	assert(f != null, "introuvable : " + path)
	return f.get_buffer(f.get_length())


func _init() -> void:
	if FileAccess.file_exists(SCENE_OUT):
		push_error("Frontiere01 existe : carte editee manuellement. Ouvrir la scene dans l'editeur ; regeneration bloquee pour proteger les modifications.")
		quit(1)
		return
	_build.call_deferred()


func _build() -> void:
	# --- vérifie la disposition des bits de la carte de contrôle (base 27-31, overlay 22-26, blend 14-21)
	var u := Terrain3DUtil
	var probe: int = (3 << 27) | (5 << 22) | (200 << 14)
	if u.get_base(probe) != 3 or u.get_overlay(probe) != 5 or u.get_blend(probe) != 200:
		push_error("disposition de la carte de contrôle inattendue")
		quit(1)
		return

	var terrain := Terrain3D.new()
	terrain.name = "Terrain3D"
	var assets := Terrain3DAssets.new()
	for i in TEXTURES.size():
		var spec: Array = TEXTURES[i]
		var t := Terrain3DTextureAsset.new()
		t.name = spec[0]
		t.id = i
		t.albedo_texture = load(TEX_DIR + spec[0] + "_alb_ht.png")
		t.normal_texture = load(TEX_DIR + spec[0] + "_nrm_rgh.png")
		assert(t.albedo_texture != null and t.normal_texture != null, "texture introuvable : " + spec[0])
		t.uv_scale = spec[1]
		t.detiling = spec[2]
		t.albedo_color = spec[3]
		assets.set_texture(i, t)
	terrain.assets = assets
	terrain.data_directory = TERRAIN_DIR
	terrain.collision_enabled = false   # le moteur sans écran n'a pas de physique pour ça
	root.add_child(terrain)
	await process_frame
	await process_frame
	terrain.material.world_background = Terrain3DMaterial.NONE
	terrain.material.shader_override = preload("res://shaders/frontiere_terrain.gdshader")
	terrain.material.shader_override_enabled = true
	terrain.material.set_shader_param("blend_sharpness", 0.8)

	# --- relief et peinture
	var n := 1536
	var h_img := Image.create_from_data(n, n, false, Image.FORMAT_RF, _read(DATA + "height.r32"))
	var c_img := Image.create_from_data(n, n, false, Image.FORMAT_RF, _read(DATA + "control.u32"))
	terrain.data.import_images([h_img, c_img, null], Vector3(-HALF, 0, -HALF), 0.0, 1.0)
	print("régions : ", terrain.data.get_region_count())

	# --- végétation : fichiers produits par place_vegetation.py (optionnels)
	if ResourceLoader.exists("res://tools/map/vegetation.gd"):
		load("res://tools/map/vegetation.gd").apply(terrain)

	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(TERRAIN_DIR))
	terrain.data.save_directory(TERRAIN_DIR)
	assets.save("res://maps/frontiere01/terrain_assets.tres")

	# --- scène
	var scene := Node3D.new()
	scene.name = "Frontiere01"
	terrain.get_parent().remove_child(terrain)
	scene.add_child(terrain)
	terrain.owner = scene
	terrain.data_directory = TERRAIN_DIR
	terrain.assets = load("res://maps/frontiere01/terrain_assets.tres")
	terrain.collision_enabled = true
	terrain.collision_mode = Terrain3D.CollisionMode.FULL_GAME

	var sun := preload("res://scenes/maps/lighting/afternoon_sun.tscn").instantiate() as DirectionalLight3D
	sun.light_angular_distance = 0.0
	scene.add_child(sun)
	sun.owner = scene

	var env := WorldEnvironment.new()
	env.name = "WorldEnvironment"
	env.environment = preload("res://scenes/maps/lighting/frontiere_daylight.tres")
	scene.add_child(env)
	env.owner = scene

	# point d'apparition : au centre du vallon, sur le sol
	if ResourceLoader.exists("res://tools/map/buildings.gd"):
		load("res://tools/map/buildings.gd").apply(scene)

	if ResourceLoader.exists("res://tools/map/ziplines.gd"):
		load("res://tools/map/ziplines.gd").apply(scene)

	var spawn := Marker3D.new()
	spawn.name = "SpawnPoint"
	var sp := Vector3(-10, 0, 30)
	sp.y = terrain.data.get_height(sp) + 1.0
	spawn.position = sp
	scene.add_child(spawn)
	spawn.owner = scene

	var packed := PackedScene.new()
	packed.pack(scene)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(SCENE_OUT.get_base_dir()))
	var err := ResourceSaver.save(packed, SCENE_OUT)
	print("scène écrite : ", SCENE_OUT, " (", error_string(err), ")")
	quit()
