extends SceneTree

func _init():
	var model = load("res://assets/characters/kit/character_kit.glb").instantiate()
	root.add_child(model)
	for node in model.find_children("*", "MeshInstance3D", true, false):
		for surface in node.mesh.get_surface_count():
			var arrays = node.mesh.surface_get_arrays(surface)
			var path = "%s/%s_%d.raw" % [OS.get_environment("OE_MORPH_OUT"), node.name, surface]
			var file = FileAccess.open(path, FileAccess.WRITE)
			file.store_buffer(arrays[Mesh.ARRAY_VERTEX].to_byte_array())
			file.store_buffer(arrays[Mesh.ARRAY_TEX_UV].to_byte_array())
			if node.name == &"Body" or str(node.name).begins_with("c_") or str(node.name).begins_with("s_"):
				var topology = FileAccess.open(path + ".indices", FileAccess.WRITE)
				topology.store_buffer(arrays[Mesh.ARRAY_INDEX].to_byte_array())
				if node.name == &"Body":
					var normals = FileAccess.open(path + ".normals", FileAccess.WRITE)
					normals.store_buffer(arrays[Mesh.ARRAY_NORMAL].to_byte_array())
	model.free()
	quit()
