extends SceneTree
# Relève les poses du personnage pour les hitbox du serveur :
#   Godot --headless --path . --script tools/hitbox/bake_body_poses.gd
# Écrit netcode/OpenEsport.Core/Game/body_poses.bin (voir scripts/tools/HitboxBaker.cs).

func _init():
	root.add_child(load("res://scripts/tools/HitboxBaker.cs").new())
