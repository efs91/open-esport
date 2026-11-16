extends SceneTree
## Planche-contact des modèles exportés : Godot --path . --script res://tools/map/preview_models.gd -- --out=C:/x.png --glob=nama_ --step=6
func _init() -> void:
	_run.call_deferred()


func _run() -> void:
	var out := OS.get_environment("TEMP") + "/models.png"
	var prefix := ""
	var step := 6.0
	var per_row := 8
	var height := 8.0
	var dir_override := ""
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="): out = a.substr(6)
		elif a.begins_with("--glob="): prefix = a.substr(7)
		elif a.begins_with("--step="): step = float(a.substr(7))
		elif a.begins_with("--row="): per_row = int(a.substr(6))
		elif a.begins_with("--height="): height = float(a.substr(9))
		elif a.begins_with("--dir="): dir_override = a.substr(6)
	get_root().size = Vector2i(1600, 900)
	var dir := dir_override if dir_override != "" else "res://maps/frontiere01/models/"
	var files := []
	for f in DirAccess.get_files_at(dir):
		if f.ends_with(".glb") and f.begins_with(prefix): files.append(f)
	files.sort()
	var env := WorldEnvironment.new()
	var e := Environment.new()
	e.background_mode = Environment.BG_COLOR
	e.background_color = Color(0.55, 0.65, 0.78)
	e.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	e.ambient_light_color = Color(0.75, 0.75, 0.8)
	e.ambient_light_energy = 0.8
	env.environment = e
	get_root().add_child(env)
	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-50, -30, 0)
	get_root().add_child(sun)
	var floor_mesh := MeshInstance3D.new()
	var pm := PlaneMesh.new()
	pm.size = Vector2(400, 400)
	floor_mesh.mesh = pm
	var fm := StandardMaterial3D.new()
	fm.albedo_color = Color(0.4, 0.38, 0.33)
	floor_mesh.material_override = fm
	get_root().add_child(floor_mesh)
	var i := 0
	var rows := int(ceil(files.size() / float(per_row)))
	for f in files:
		var scene = load(dir + f)
		if scene == null:
			print("illisible : ", f)
			continue
		var n: Node3D = scene.instantiate()
		get_root().add_child(n)
		n.position = Vector3((i % per_row) * step, 0, -(i / per_row) * step)
		print(f, " ", i)
		i += 1
	var cam := Camera3D.new()
	get_root().add_child(cam)
	cam.current = true
	cam.far = 800
	var cx := (per_row - 1) * step * 0.5
	cam.position = Vector3(cx, height * 0.8, step * 1.6 + rows * 0.2)
	cam.look_at(Vector3(cx, height * 0.35, -(rows - 1) * step * 0.5))
	for k in 12: await process_frame
	get_root().get_texture().get_image().save_png(out)
	quit()
