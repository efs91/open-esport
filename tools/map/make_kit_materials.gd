extends SceneTree
## Crée les matériaux des kits (les fichiers FBX/glTF n'en référencent pas les textures) et écrit la liste matériaux-par-fichier
## dans res://tools/map/_filemats.json ; apply_kit_materials.py s'en sert pour régler l'import de chaque pièce.
const OUT := "res://maps/frontiere01/materials/"
const ST := "res://assets/environement/3D Scifi Starter Kit Free/_Textures/"
const MG := "res://assets/environement/Modular SciFi MegaKit[Standard]/Textures/"


func _tex(path: String) -> Texture2D:
	return load(path) as Texture2D if ResourceLoader.exists(path) else null


func _mat(kit: String, n: String) -> StandardMaterial3D:
	var m := StandardMaterial3D.new()
	m.resource_name = n
	if kit == "starter":
		var base := ""
		var num := n.get_slice("_", 1) if n.begins_with("Wall_") or n.begins_with("HD_Wall_") else ""
		if n.begins_with("HD_Wall_"): base = "HD_Wall_" + n.substr(8, 2)
		elif n.begins_with("Wall_"): base = "HD_Wall_" + n.substr(5, 2)
		elif n == "Wall 10": base = "HD_Wall_10"
		elif n.begins_with("Stuff_"): base = "HD_" + n
		elif n == "Alpha_02": base = "HD_Alpha_02"
		elif n == "01 - Default": base = "HD_Floor_01"
		elif n == "10 - Default": base = "HD_Floor_02"
		elif n == "toto": base = "HD_Stuff_01"
		if base != "":
			var alb := _tex(ST + base + "_Dif_Style_02_Alb.png")
			if alb == null: alb = _tex(ST + base + "_Dif_Style_01.png")
			m.albedo_texture = alb
			var nrm := _tex(ST + base + "_Dif_Style_02_Norm.png")
			if nrm == null: nrm = _tex(ST + base + "_Norm.png")
			if nrm != null:
				m.normal_enabled = true
				m.normal_texture = nrm
			var ao := _tex(ST + base + "_Dif_Style_02_AO.png")
			if ao != null:
				m.ao_enabled = true
				m.ao_texture = ao
			var ill := _tex(ST + base + "_Dif_Style_02_Illum.png")
			if ill != null:
				m.emission_enabled = true
				m.emission_texture = ill
				m.emission_energy_multiplier = 1.0
			if base.begins_with("HD_Alpha"):
				m.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR
			m.roughness = 0.7
			m.metallic = 0.35
		elif n == "Orange":
			m.albedo_color = Color(0.9, 0.38, 0.05)
			m.roughness = 0.6
		elif n == "Glass":
			m.albedo_color = Color(0.5, 0.75, 0.9, 0.35)
			m.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
			m.roughness = 0.1
	else:
		var trim := ""
		if n.begins_with("MI_Trim_01"): trim = "T_Trim_01"
		elif n.begins_with("MI_Trim_02"): trim = "T_Trim_02"
		elif n.begins_with("MI_Trim_03"): trim = "T_Trim_03"
		elif n == "MI_PaddedWall": trim = "T_PaddedWall"
		if trim != "":
			m.albedo_texture = _tex(MG + trim + "_BaseColor.png")
			if n.contains("Cables"): m.albedo_texture = _tex(MG + "T_Trim_03_Cables.png")
			var nr := _tex(MG + trim + "_Normal.png")
			if nr != null:
				m.normal_enabled = true
				m.normal_texture = nr
			var orm := _tex(MG + trim + "_ORM.png")
			if orm != null:
				m.ao_enabled = true
				m.ao_texture = orm
				m.ao_texture_channel = BaseMaterial3D.TEXTURE_CHANNEL_RED
				m.roughness_texture = orm
				m.roughness_texture_channel = BaseMaterial3D.TEXTURE_CHANNEL_GREEN
				m.metallic_texture = orm
				m.metallic_texture_channel = BaseMaterial3D.TEXTURE_CHANNEL_BLUE
				m.metallic = 1.0
				m.roughness = 1.0
			if n.ends_with("Dark"): m.albedo_color = Color(0.45, 0.45, 0.5)
			if n.ends_with("Blue"): m.albedo_color = Color(0.5, 0.7, 1.0)
		elif n.begins_with("MI_Decal") or n.begins_with("M_Decal"):
			m.albedo_texture = _tex(MG + "T_Decals.png")
			m.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_DEPTH_PRE_PASS
			m.albedo_color = Color(0.85, 0.12, 0.1) if n.ends_with("Red") else (Color(0.6, 0.6, 0.6) if n.ends_with("Grey") else Color(0.95, 0.95, 0.95))
			if n.ends_with("Screen"):
				m.emission_enabled = true
				m.emission = Color(0.2, 0.6, 1.0)
		elif n == "M_Black":
			m.albedo_color = Color(0.03, 0.03, 0.035)
		elif n == "M_Glass":
			m.albedo_color = Color(0.55, 0.8, 0.95, 0.3)
			m.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
			m.roughness = 0.05
		elif n == "M_Light" or n == "M_LightFade_Red":
			m.albedo_color = Color(1, 0.95, 0.85) if n == "M_Light" else Color(1, 0.15, 0.1)
			m.emission_enabled = true
			m.emission = m.albedo_color
			m.emission_energy_multiplier = 2.0
		elif n.begins_with("M_Alien"):
			m.albedo_color = Color(0.3, 0.6, 0.5)
	m.cull_mode = BaseMaterial3D.CULL_DISABLED     # beaucoup de pièces sont des plans sans épaisseur : visibles des deux côtés
	return m


func _init() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	var kits = JSON.parse_string(FileAccess.get_file_as_string("res://tools/map/kits.json"))
	var made := {}
	var filemats := {}
	for k in kits:
		var kit: String = k.split("|")[0]
		var path: String = "res://" + kits[k]["path"]
		var inst = load(path).instantiate()
		var names := {}
		for mi in inst.find_children("*", "MeshInstance3D", true, false):
			for i in mi.mesh.get_surface_count():
				var mat = mi.mesh.surface_get_material(i)
				if mat != null: names[mat.resource_name] = true
		inst.free()
		var list := []
		for n in names:
			var fname := kit + "_" + String(n).replace(" ", "_").replace("-", "_")
			if not made.has(fname):
				var m := _mat(kit, n)
				ResourceSaver.save(m, OUT + fname + ".tres")
				made[fname] = true
			list.append([n, OUT + fname + ".tres"])
		filemats[path] = list
	var f := FileAccess.open("res://tools/map/_filemats.json", FileAccess.WRITE)
	f.store_string(JSON.stringify(filemats))
	f.close()
	print("matériaux : ", made.size())
	quit()
