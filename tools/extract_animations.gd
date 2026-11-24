extends SceneTree
## Outil (hors jeu) : extrait les animations Mixamo importées (assets/animations/*.fbx, retargetées sur le squelette
## humanoïde Godot) vers des ressources assets/animations/clips/*.res, remises « sur place ».
## Lancer : Godot --headless --path . --script res://tools/extract_animations.gd
## Pour ajouter une animation : déposer le .fbx dans assets/animations, importer le projet, ajouter une ligne ci-dessous.

# fichier -> [nom du clip, en boucle, aplatir le déplacement du bassin : "linear" (cycle) / "flat" (glissade) / ""]
const CLIPS := {
	"Rifle Idle": ["idle", true, ""],
	"Ninja Idle": ["idle_unarmed", true, "segment"],
	"Bouncing Fight Idle": ["fight_idle", true, "arms"],
	"Boxing": ["punch", false, "arms:0.45:1.0"],
	"Rifle Walk": ["walk", true, "linear"],
	"Rifle Run": ["run", true, "linear"],
	"Jump Up": ["jump_up", false, ""],
	"Jump Loop": ["jump_loop", true, ""],
	"Jump Down": ["jump_down", false, ""],
	"Running Slide": ["slide", false, "flat"],
	"Jump To Hang": ["hang_start", false, ""],
	"Hanging Idle": ["hang_idle", true, ""],
	"Freehang Climb": ["climb", false, "rise"],
	"Idle Crouching": ["crouch_idle", true, ""],
	"Crouch Walking": ["crouch_walk", true, "linear"],
}

# Le squelette du personnage est en pose en T comme celui de Mixamo (réglage « Fix Silhouette » de l'import du modèle) :
# les animations se jouent telles quelles. Ninja Idle est une longue boucle très dynamique (un bras passe parfois dans le dos) :
# on n'en garde que le passage où la garde est devant le corps, lu en aller-retour (mode « segment »).
const SEGMENT_START := 7.25
const SEGMENT_END := 8.5

func _segment(src: Animation, t0: float, t1: float) -> Animation:
	var out := Animation.new()
	out.length = t1 - t0
	var step := 1.0 / 30.0
	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_POSITION_3D and type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_SCALE_3D:
			continue
		var j := out.add_track(type)
		out.track_set_path(j, src.track_get_path(i))
		var t := t0
		while t <= t1 + 0.0001:
			var v: Variant
			if type == Animation.TYPE_POSITION_3D:
				v = src.position_track_interpolate(i, t)
			elif type == Animation.TYPE_ROTATION_3D:
				v = src.rotation_track_interpolate(i, t)
			else:
				v = src.scale_track_interpolate(i, t)
			out.track_insert_key(j, t - t0, v)
			t += step
	return out

const ARM_PARTS := ["Shoulder", "UpperArm", "LowerArm", "Hand", "Thumb", "Index", "Middle", "Ring", "Little"]

## Ne garde que les rotations des os des bras et des mains : l'animation ne s'applique qu'aux bras, les jambes et le tronc
## gardent leur propre animation (marche, course…).
func _keep_arms_only(anim: Animation) -> void:
	for i in range(anim.get_track_count() - 1, -1, -1):
		var keep := false
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			var bone := String(anim.track_get_path(i)).get_slice(":", 1)
			if bone.begins_with("Left") or bone.begins_with("Right"):
				for part in ARM_PARTS:
					if bone.contains(part):
						keep = true
						break
		if not keep:
			anim.remove_track(i)

func _init() -> void:
	DirAccess.make_dir_recursive_absolute("res://assets/animations/clips")
	# OE_ONLY="crouch_idle,crouch_walk" : n'extrait que ces clips (les autres .res restent tels quels)
	var only := OS.get_environment("OE_ONLY").split(",", false)
	for src in CLIPS:
		var clip_name: String = CLIPS[src][0]
		if only.size() > 0 and not only.has(clip_name):
			continue
		var scene: Node = load("res://assets/animations/%s.fbx" % src).instantiate()
		var player: AnimationPlayer = scene.get_node("AnimationPlayer")
		# Mixamo exporte la vraie animation sous « mixamo_com » ; certains fichiers contiennent en plus une prise figée
		# (« Take 001 », une seule clé par piste, parfois plus longue) : on ne la prend jamais
		var best_name: String = player.get_animation_list()[0]
		if player.has_animation("mixamo_com"):
			best_name = "mixamo_com"
		var anim: Animation = player.get_animation(best_name).duplicate()
		if CLIPS[src][2] == "segment":
			anim = _segment(anim, SEGMENT_START, SEGMENT_END)
		if String(CLIPS[src][2]).begins_with("arms"):
			# « arms:début:fin » : on ne garde que ce passage (la boxe de Mixamo : garde, un coup, garde)
			var parts: PackedStringArray = String(CLIPS[src][2]).split(":")
			if parts.size() == 3:
				anim = _segment(anim, float(parts[1]), float(parts[2]))
			_keep_arms_only(anim)
		anim.loop_mode = Animation.LOOP_PINGPONG if CLIPS[src][2] == "segment" else (Animation.LOOP_LINEAR if CLIPS[src][1] else Animation.LOOP_NONE)

		var hips := anim.find_track(NodePath("%GeneralSkeleton:Hips"), Animation.TYPE_POSITION_3D)
		if hips < 0:
			hips = anim.find_track(NodePath("Skeleton3D:mixamorig_Hips"), Animation.TYPE_POSITION_3D)
		var travelled := 0.0
		if hips >= 0 and CLIPS[src][2] == "rise":
			# escalade : le fichier monte le bassin de plus de 2 m ; c'est le jeu qui déplace le joueur, on ne garde que l'oscillation
			var cnt := anim.track_get_key_count(hips)
			var f0: Vector3 = anim.track_get_key_value(hips, 0)
			var f1: Vector3 = anim.track_get_key_value(hips, cnt - 1)
			for k in cnt:
				var v: Vector3 = anim.track_get_key_value(hips, k)
				var t := anim.track_get_key_time(hips, k) / maxf(anim.length, 0.001)
				v.y -= (f1.y - f0.y) * t
				v.z -= (f1.z - f0.z) * t
				anim.track_set_key_value(hips, k, v)
		if hips >= 0 and (CLIPS[src][2] == "center" or CLIPS[src][2] == "segment"):
			# Posture sur place : le bassin est recentré (sa position horizontale moyenne devient l'origine)
			var rot := anim.find_track(NodePath("%GeneralSkeleton:Hips"), Animation.TYPE_ROTATION_3D)
			var n := anim.track_get_key_count(hips)
			var mean := Vector3.ZERO
			for k in n:
				mean += anim.track_get_key_value(hips, k)
			mean /= float(n)
			var yaw := 0.0
			var undo := Quaternion(Vector3.UP, -yaw)
			for k in n:
				var v: Vector3 = anim.track_get_key_value(hips, k) - Vector3(mean.x, 0.0, mean.z - 0.01)
				anim.track_set_key_value(hips, k, undo * v)
			if rot >= 0:
				for k in anim.track_get_key_count(rot):
					anim.track_set_key_value(rot, k, undo * (anim.track_get_key_value(rot, k) as Quaternion))
		if hips >= 0 and CLIPS[src][2] != "" and CLIPS[src][2] != "rise" and CLIPS[src][2] != "center" and CLIPS[src][2] != "segment" and not String(CLIPS[src][2]).begins_with("arms"):
			var count := anim.track_get_key_count(hips)
			var first: Vector3 = anim.track_get_key_value(hips, 0)
			var last: Vector3 = anim.track_get_key_value(hips, count - 1)
			travelled = last.z - first.z
			for k in count:
				var v: Vector3 = anim.track_get_key_value(hips, k)
				var t := anim.track_get_key_time(hips, k) / maxf(anim.length, 0.001)
				if CLIPS[src][2] == "linear":
					# On retire la dérive moyenne : l'oscillation du pas est conservée
					v.z -= (last.z - first.z) * t
				else:
					v.z = first.z
				anim.track_set_key_value(hips, k, v)
		# Vitesse au sol d'origine (m/s), pour caler la vitesse de lecture sur la vitesse réelle du joueur
		anim.set_meta("ground_speed", absf(travelled) / maxf(anim.length, 0.001))
		print("CLIP ", clip_name, " len=", snappedf(anim.length, 0.01), " ground_speed=", snappedf(anim.get_meta("ground_speed"), 0.01))
		ResourceSaver.save(anim, "res://assets/animations/clips/%s.res" % clip_name)
		scene.free()
	quit()
