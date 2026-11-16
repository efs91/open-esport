extends RefCounted
## Construit les tyroliennes dans la scène : gaine de verre en MultiMesh (un tronçon tous les ~4 m), piliers, tremplins.
## Données : maps/frontiere01/data/ziplines.json (produit par gen_ziplines.py).

const DATA := "res://maps/frontiere01/data/ziplines.json"
const MODELS := "res://maps/frontiere01/models/"
const SEG_LEN := 4.0
const RADIUS := 2.5


static func apply(scene: Node3D) -> void:
	var text := FileAccess.get_file_as_string(DATA)
	if text.is_empty():
		push_warning("pas de ziplines.json : lance gen_ziplines.py")
		return
	var d: Dictionary = JSON.parse_string(text)
	var root := Node3D.new()
	root.name = "Ziplines"
	scene.add_child(root)
	root.owner = scene

	# ---- gaine : une MultiMesh par partie du modèle (verre, arceaux, néons, câble, supports)
	var tunnel: Node3D = load(MODELS + "zip_tunnel.glb").instantiate()
	var parts: Array = []
	for c in tunnel.get_children():
		if c is MeshInstance3D: parts.append(c)
	var transforms: Array[Transform3D] = []
	for line in d["lines"]:
		var pts: Array = line["points"]
		for i in range(1, pts.size()):
			var a := Vector3(pts[i - 1][0], pts[i - 1][1], pts[i - 1][2])
			var b := Vector3(pts[i][0], pts[i][1], pts[i][2])
			var dir := b - a
			var len := dir.length()
			if len < 0.01: continue
			var basis := Basis.looking_at(dir / len, Vector3.UP)
			basis = basis * Basis.from_scale(Vector3(1, 1, len / SEG_LEN + 0.02))     # léger recouvrement : pas de jour entre deux tronçons
			transforms.append(Transform3D(basis, (a + b) * 0.5))
	for part in parts:
		var mm := MultiMesh.new()
		mm.transform_format = MultiMesh.TRANSFORM_3D
		mm.mesh = (part as MeshInstance3D).mesh
		mm.instance_count = transforms.size()
		# le tampon est écrit à la main : sans écran, le moteur ne garde pas les positions données une à une
		var buf := PackedFloat32Array()
		buf.resize(transforms.size() * 12)
		for i in transforms.size():
			var t: Transform3D = transforms[i] * (part as MeshInstance3D).transform
			var o := i * 12
			buf[o] = t.basis.x.x;     buf[o + 1] = t.basis.y.x;  buf[o + 2] = t.basis.z.x;  buf[o + 3] = t.origin.x
			buf[o + 4] = t.basis.x.y; buf[o + 5] = t.basis.y.y;  buf[o + 6] = t.basis.z.y;  buf[o + 7] = t.origin.y
			buf[o + 8] = t.basis.x.z; buf[o + 9] = t.basis.y.z;  buf[o + 10] = t.basis.z.z; buf[o + 11] = t.origin.z
		mm.buffer = buf
		var mmi := MultiMeshInstance3D.new()
		mmi.name = "Tunnel_" + part.name
		mmi.multimesh = mm
		mmi.visibility_range_end = 450.0
		mmi.visibility_range_end_margin = 40.0
		mmi.visibility_range_fade_mode = GeometryInstance3D.VISIBILITY_RANGE_FADE_SELF
		mmi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		root.add_child(mmi)
		mmi.owner = scene
	tunnel.free()

	# ---- piliers : la colonne est étirée jusqu'à la gaine. Chaque partie est un MeshInstance3D à part (les réglages faits sur les
	# enfants d'une scène instanciée ne seraient pas enregistrés).
	var pylon_src: Node3D = load(MODELS + "zip_pylon.glb").instantiate()
	var pylon_parts := {}
	for c in pylon_src.get_children():
		if c is MeshInstance3D: pylon_parts[c.name] = c
	var boxes: Array = []
	for line in d["lines"]:
		for p in line["pylons"]:
			var n := Node3D.new()
			n.name = "Pylon"
			n.position = Vector3(p[0], p[1], p[2])
			root.add_child(n)
			n.owner = scene
			var top: float = maxf(p[3] - 1.45 - p[1], 0.8)      # le berceau touche les bords de la gaine, 1,45 m sous le câble
			for nm in pylon_parts:
				var src := pylon_parts[nm] as MeshInstance3D
				var mi := MeshInstance3D.new()
				mi.name = nm
				mi.mesh = src.mesh
				var t := src.transform
				if nm == "Column" or nm == "ColumnNeon":
					t = Transform3D(Basis.from_scale(Vector3(1, top - 0.5, 1)), Vector3(0, 0.5, 0)) * src.transform
				elif nm == "Cradle" or nm == "CradleNeon":
					t = Transform3D(Basis.IDENTITY, Vector3(0, top, 0)) * src.transform
				mi.transform = t
				n.add_child(mi)
				mi.owner = scene
			boxes.append([p[0] - 0.4, p[1], p[2] - 0.4, p[0] + 0.4, p[1] + top, p[2] + 0.4])
	pylon_src.free()

	# ---- tremplins
	var pad_scene := load(MODELS + "zip_pad.glb") as PackedScene
	for pad in d["pads"]:
		var n2: Node3D = pad_scene.instantiate()
		n2.position = Vector3(pad["pos"][0], pad["pos"][1], pad["pos"][2])
		root.add_child(n2)
		n2.owner = scene
		# posé dans le plan de la pente (sinon le disque est à moitié enterré)
		var nrm: Array = pad.get("normal", [0, 1, 0])
		var up := Vector3(nrm[0], nrm[1], nrm[2]).normalized()
		var tilt := Quaternion(Vector3.UP, up)
		n2.basis = Basis(tilt)
		# faisceau lumineux vertical : le tremplin se voit de loin, même dans un relief accidenté
		var beam := MeshInstance3D.new()
		beam.name = "Beam"
		beam.mesh = CylinderMesh.new()
		(beam.mesh as CylinderMesh).top_radius = 0.06
		(beam.mesh as CylinderMesh).bottom_radius = 0.85
		(beam.mesh as CylinderMesh).height = 14.0
		(beam.mesh as CylinderMesh).radial_segments = 16
		var bm := StandardMaterial3D.new()
		bm.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
		bm.albedo_color = Color(0.2, 0.75, 1.0, 0.28)
		bm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		bm.cull_mode = BaseMaterial3D.CULL_DISABLED
		bm.blend_mode = BaseMaterial3D.BLEND_MODE_ADD
		beam.material_override = bm
		beam.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		beam.top_level = true
		beam.position = n2.position + Vector3(0, 7.0, 0)
		beam.visibility_range_end = 320.0
		n2.add_child(beam)
		beam.owner = scene
	var f := FileAccess.open("res://maps/frontiere01/data/zipline_boxes.json", FileAccess.WRITE)
	f.store_string(JSON.stringify({"boxes": boxes}))
	f.close()
	print("tyroliennes : %d câbles, %d tronçons, %d tremplins" % [d["lines"].size(), transforms.size(), d["pads"].size()])
