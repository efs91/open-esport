extends SceneTree
## Captures de la carte : Godot --path . --script res://tools/map/shot.gd -- --scene=res://scenes/maps/Frontiere01.tscn --out=C:/dossier
## Chaque vue : "nom:x,y,z:regarde_x,y,z"  (par défaut : une vue d'ensemble et une vue au sol)
var views := []
var caps := []
var show_boxes := ""


func _init() -> void:
	_run.call_deferred()


func _run() -> void:
	var scene_path := "res://scenes/maps/Frontiere01.tscn"
	var out := OS.get_environment("TEMP")
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--scene="): scene_path = a.substr(8)
		elif a.begins_with("--out="): out = a.substr(6)
		elif a.begins_with("--view="): views.append(a.substr(7))
		elif a.begins_with("--cap="): caps.append(a.substr(6))
		elif a.begins_with("--boxes="): show_boxes = a.substr(8)   # fichier json de boîtes à superposer en rouge translucide
	if views.is_empty():
		views = ["overview:0,700,900:0,40,-60", "top:0,1300,1:0,0,0"]
	get_root().size = Vector2i(1280, 720)
	var map: Node = load(scene_path).instantiate()
	get_root().add_child(map)
	var cam := Camera3D.new()
	cam.far = 4000.0
	cam.fov = 60.0
	get_root().add_child(cam)
	cam.current = true
	var terrain := map.get_node_or_null("Terrain3D")
	if terrain != null:
		terrain.set_camera(cam)
	if show_boxes != "":
		var bd: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(show_boxes))
		var bm := StandardMaterial3D.new()
		bm.albedo_color = Color(1, 0.1, 0.1, 0.45)
		bm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		for b in bd["boxes"]:
			var mi2 := MeshInstance3D.new()
			var bx := BoxMesh.new()
			bx.size = Vector3(b[3] - b[0], b[4] - b[1], b[5] - b[2])
			mi2.mesh = bx
			mi2.material_override = bm
			mi2.position = Vector3((b[0] + b[3]) / 2.0, (b[1] + b[4]) / 2.0, (b[2] + b[5]) / 2.0)
			get_root().add_child(mi2)
	for c in caps:   # repère d'échelle : un joueur (capsule de 1,8 m, rayon 0,4 m) ; "x,y,z" (y = -999 : sur le relief)
		var f: PackedStringArray = c.split(",")
		var mi := MeshInstance3D.new()
		var cm := CapsuleMesh.new()
		cm.radius = 0.4
		cm.height = 1.8
		mi.mesh = cm
		var mt := StandardMaterial3D.new()
		mt.albedo_color = Color(1, 0.1, 0.1)
		mi.material_override = mt
		var cp := Vector3(float(f[0]), float(f[1]), float(f[2]))
		if float(f[1]) < -900.0 and terrain != null:
			cp.y = terrain.data.get_height(cp)
		mi.position = cp + Vector3(0, 0.9, 0)
		get_root().add_child(mi)
	for i in 30: await process_frame
	for v in views:
		var p: PackedStringArray = v.split(":")
		var pos := _vec(p[1])
		var look := _vec(p[2])
		if p.size() > 3 and p[3] == "ground" and terrain != null:
			pos.y = terrain.data.get_height(pos) + 1.7
			look.y = pos.y - float(p[4]) if p.size() > 4 else pos.y - 2.0
		cam.position = pos
		cam.look_at(look, Vector3.UP if absf(look.x - pos.x) + absf(look.z - pos.z) > 1.0 else Vector3.FORWARD)
		for i in 20: await process_frame
		var img := get_root().get_texture().get_image()
		img.save_png(out + "/" + p[0] + ".png")
		print("capture ", p[0])
	quit()


func _vec(s: String) -> Vector3:
	var f := s.split(",")
	return Vector3(float(f[0]), float(f[1]), float(f[2]))
