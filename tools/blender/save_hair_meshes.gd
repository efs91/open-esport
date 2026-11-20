extends SceneTree

func build_texture(path):
	var img = Image.load_from_file(path)
	img.generate_mipmaps()
	var tex = PortableCompressedTexture2D.new()
	tex.keep_compressed_buffer = true
	tex.create_from_image(img, PortableCompressedTexture2D.COMPRESSION_MODE_LOSSLESS)
	return tex

func _init():
	var tmp = OS.get_environment("OE_HAIR_BUILD")
	var config = JSON.parse_string(FileAccess.get_file_as_string(tmp + "/hair.json"))
	var model = load("res://assets/characters/kit/character_kit.glb").instantiate()
	root.add_child(model)
	var body = model.find_child("Body", true, false)
	var skeleton = body.get_node(body.skeleton)
	var bind_indices = {}
	var head = -1
	for i in body.skin.get_bind_count():
		var bone = body.skin.get_bind_name(i)
		if bone == &"":
			bone = skeleton.get_bone_name(body.skin.get_bind_bone(i))
		bind_indices[str(bone)] = i
		if bone == &"Head":
			head = i
	if head < 0:
		quit(1)
		return
	for job in config.jobs:
		var path = tmp + "/" + str(job.id)
		var file = FileAccess.open(path + ".bin", FileAccess.READ)
		var n = int(job.count)
		var arrays = []
		arrays.resize(Mesh.ARRAY_MAX)
		arrays[Mesh.ARRAY_VERTEX] = file.get_buffer(n*12).to_vector3_array()
		arrays[Mesh.ARRAY_NORMAL] = file.get_buffer(n*12).to_vector3_array()
		arrays[Mesh.ARRAY_TEX_UV] = file.get_buffer(n*8).to_vector2_array()
		var bones = PackedInt32Array()
		var weights = PackedFloat32Array()
		bones.resize(n*4)
		weights.resize(n*4)
		for i in n:
			bones[i*4] = head
			weights[i*4] = 1
		if job.has("bone_names"):
			var binding = FileAccess.open(path + ".skin", FileAccess.READ)
			bones = binding.get_buffer(n*16).to_int32_array()
			weights = binding.get_buffer(n*16).to_float32_array()
			for i in bones.size():
				bones[i] = bind_indices[job.bone_names[bones[i]]]
		arrays[Mesh.ARRAY_BONES] = bones
		arrays[Mesh.ARRAY_WEIGHTS] = weights
		var mesh = ArrayMesh.new()
		mesh.blend_shape_mode = Mesh.BLEND_SHAPE_MODE_NORMALIZED
		var shapes: Array[Array] = []
		for shape in config.shapes:
			mesh.add_blend_shape(shape)
			var morph = []
			morph.resize(Mesh.ARRAY_MAX)
			morph[Mesh.ARRAY_VERTEX] = file.get_buffer(n*12).to_vector3_array()
			morph[Mesh.ARRAY_NORMAL] = arrays[Mesh.ARRAY_NORMAL]
			shapes.append(morph)
		arrays[Mesh.ARRAY_INDEX] = file.get_buffer(file.get_length()-file.get_position()).to_int32_array()
		mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays, shapes)
		var mat = StandardMaterial3D.new()
		mat.albedo_texture = build_texture(path + ".png")
		if job.has("bone_names"):
			var orm = build_texture(path + "-orm.png")
			mat.metallic = 1.0
			mat.metallic_texture = orm
			mat.metallic_texture_channel = BaseMaterial3D.TEXTURE_CHANNEL_BLUE
			mat.roughness_texture = orm
			mat.roughness_texture_channel = BaseMaterial3D.TEXTURE_CHANNEL_GREEN
			if job.emission:
				mat.emission_enabled = true
				mat.emission_texture = build_texture(path + "-emission.png")
		mat.albedo_color = Color(job.color[0],job.color[1],job.color[2])
		mat.cull_mode = BaseMaterial3D.CULL_DISABLED
		if job.alpha:
			mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR
			mat.alpha_scissor_threshold = 0.32
		mesh.surface_set_material(0, mat)
		if mesh.get_surface_count() != 1 or ResourceSaver.save(mesh,"res://assets/characters/kit/"+str(job.get("folder","hair"))+"/"+str(job.id)+".res",ResourceSaver.FLAG_COMPRESS) != OK:
			quit(1)
			return
	model.free()
	quit()
