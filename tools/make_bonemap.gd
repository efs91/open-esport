extends SceneTree
## Outil (hors jeu) : écrit tools/_bonemap.txt, la sérialisation d'une BoneMap Mixamo -> profil humanoïde Godot.
## Ce texte se colle dans l'option « retarget/bone_map » des fichiers .import du personnage et des animations.
# TEMPORAIRE : génère la sérialisation d'une BoneMap Mixamo -> profil humanoïde Godot

func _init() -> void:
	var pairs := {"Hips": "Hips", "Spine": "Spine", "Chest": "Spine1", "UpperChest": "Spine2", "Neck": "Neck", "Head": "Head"}
	for side in ["Left", "Right"]:
		pairs[side + "Shoulder"] = side + "Shoulder"
		pairs[side + "UpperArm"] = side + "Arm"
		pairs[side + "LowerArm"] = side + "ForeArm"
		pairs[side + "Hand"] = side + "Hand"
		pairs[side + "UpperLeg"] = side + "UpLeg"
		pairs[side + "LowerLeg"] = side + "Leg"
		pairs[side + "Foot"] = side + "Foot"
		pairs[side + "Toes"] = side + "ToeBase"
		var fingers := {"Thumb": ["Metacarpal", "Proximal", "Distal"], "Index": ["Proximal", "Intermediate", "Distal"],
			"Middle": ["Proximal", "Intermediate", "Distal"], "Ring": ["Proximal", "Intermediate", "Distal"],
			"Little": ["Proximal", "Intermediate", "Distal"]}
		for finger in fingers:
			var mixamo_name: String = "Pinky" if finger == "Little" else finger
			for i in 3:
				pairs[side + finger + fingers[finger][i]] = side + "Hand" + mixamo_name + str(i + 1)

	var profile := SkeletonProfileHumanoid.new()
	var bone_map := BoneMap.new()
	bone_map.profile = profile
	for key in pairs:
		bone_map.set_skeleton_bone_name(key, "mixamorig_" + pairs[key])
	var out := FileAccess.open("res://tools/_bonemap.txt", FileAccess.WRITE)
	out.store_string(var_to_str(bone_map))
	out.close()
	print("BONEMAP bones=", pairs.size())
	quit()
