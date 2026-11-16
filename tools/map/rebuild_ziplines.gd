extends SceneTree
## Refait les tyroliennes dans la scène Frontière 01 déjà éditée (sans la reconstruire) : supprime le nœud « Ziplines », le reconstruit
## depuis data/ziplines.json (python tools/map/gen_ziplines.py) et enregistre la scène. Ensuite : réexporter le fichier serveur (export_world).
## Lancer :  Godot --headless --path . --script res://tools/map/rebuild_ziplines.gd

const SCENE := "res://scenes/maps/Frontiere01.tscn"


func _init() -> void:
	var packed: PackedScene = load(SCENE)
	var scene: Node3D = packed.instantiate()
	var old := scene.get_node_or_null("Ziplines")
	if old != null:
		scene.remove_child(old)
		old.free()
	load("res://tools/map/ziplines.gd").apply(scene)
	var out := PackedScene.new()
	var err := out.pack(scene)
	assert(err == OK, "pack a échoué")
	err = ResourceSaver.save(out, SCENE)
	print("scène enregistrée : ", error_string(err))
	scene.free()
	quit()
