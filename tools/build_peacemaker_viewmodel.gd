extends SceneTree

# Prépare les ressources de la scène FPS du PeaceMaker (addons/fps-hands/fps-peacemaker/) à partir de assets/armes/shotgun_animated.glb :
#   - resources/animations.res : le fichier n'a qu'une animation de 4,7 s (« allanims »), découpée ici (images à 30 par seconde) :
#     tir + pompe 0-25, rechargement 26-71 (on penche l'arme 26-34, on glisse UNE cartouche 34-66 (la main revient dans la pose de départ), on redresse 66-71),
#     sortie de l'arme 82-102 (elle remonte, on pompe), rangement 72-82, repos 102-124, coup de crosse 124-140 ;
#     le rechargement est allongé : reload_1 à reload_6 glissent 1 à 6 cartouches (reload = reload_6) ;
#   - resources/arms.tres et peacemaker.tres : matériaux de vue FPS (shader du kit, champ de vision fixe pour l'arme).
# La scène fps-peacemaker.tscn (héritée du .glb : machine d'états, métadonnées, MuzzlePoint) est écrite à la main : elle n'est pas régénérée.
#   Godot --headless --path . --script res://tools/build_peacemaker_viewmodel.gd

const GLB := "res://assets/armes/shotgun_animated.glb"
const OUT_DIR := "res://addons/fps-hands/fps-peacemaker/resources/"
const FPS := 30.0
const SHADER := "res://addons/fps-hands/shader.gdshader"
const TEX := "res://assets/armes/shotgun_animated_%d.png"
const ANIM := "allanims"

const FIRE := [0, 25]
const RELOAD_START := [26, 34]
const RELOAD_SHELL := [34, 66]
const RELOAD_END := [66, 71]
const HIDE := [72, 82]
const TAKE := [82, 102]
const IDLE := [102, 124]
const MELEE := [124, 140]
const SHELLS := 6
# Les bras du personnage (FpsArms) ont un bout d'épaule que le bras gauche de ce modèle, plus en avant que sur les autres armes, montrait en bas
# à gauche de l'écran : le bras tourne sur lui-même pour le cacher, le coude compense (l'avant-bras et la main ne bougent pas).
const ARM_ROLL_AXIS := Vector3(0, 0, -1)
const ARM_ROLL_DEGREES := -70.0


func _init() -> void:
	_run.call_deferred()


# Images [f0, f1] d'une animation.
func _frames(range_: Array) -> Array:
	var list := []
	for f in range(range_[0], range_[1] + 1):
		list.append(f)
	return list


# Assemble des images d'une animation, en échantillonnant chaque image (le découpage reste exact aux bornes).
func _build(src: Animation, frames: Array, looped := false) -> Animation:
	var a := Animation.new()
	a.length = maxf((frames.size() - 1) / FPS, 1.0 / FPS)
	a.loop_mode = Animation.LOOP_LINEAR if looped else Animation.LOOP_NONE
	for t in src.get_track_count():
		var type := src.track_get_type(t)
		if type != Animation.TYPE_POSITION_3D and type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_SCALE_3D:
			continue
		var nt := a.add_track(type)
		a.track_set_path(nt, src.track_get_path(t))
		a.track_set_interpolation_type(nt, Animation.INTERPOLATION_LINEAR)
		for i in frames.size():
			var time := minf(frames[i] / FPS, src.length)
			var key_time := i / FPS
			match type:
				Animation.TYPE_POSITION_3D:
					a.position_track_insert_key(nt, key_time, src.position_track_interpolate(t, time))
				Animation.TYPE_ROTATION_3D:
					var q := src.rotation_track_interpolate(t, time)
					var roll := Quaternion(ARM_ROLL_AXIS, deg_to_rad(ARM_ROLL_DEGREES))
					if str(src.track_get_path(t)).ends_with(":L_arm_01"): q = q * roll
					elif str(src.track_get_path(t)).ends_with(":L_elbow_02"): q = roll.inverse() * q
					a.rotation_track_insert_key(nt, key_time, q)
				Animation.TYPE_SCALE_3D:
					a.scale_track_insert_key(nt, key_time, src.scale_track_interpolate(t, time))
	return a


# Rechargement de n cartouches : on penche l'arme, n fois le geste, on redresse.
func _reload(src: Animation, shells: int) -> Animation:
	var frames := _frames(RELOAD_START)
	for i in shells:
		# la première image du geste est la dernière du précédent (même pose) : on ne la répète pas
		frames.append_array(_frames([RELOAD_SHELL[0] + 1, RELOAD_SHELL[1]]))
	frames.append_array(_frames([RELOAD_END[0] + 1, RELOAD_END[1]]))
	return _build(src, frames)


func _material(mat_name: String, base: int) -> ShaderMaterial:
	var m := ShaderMaterial.new()
	m.resource_name = mat_name
	m.shader = load(SHADER)
	m.set_shader_parameter("fov", 70.0)
	m.set_shader_parameter("albedo", Color(1, 1, 1, 1))
	m.set_shader_parameter("point_size", 1.0)
	m.set_shader_parameter("roughness", 1.0)
	m.set_shader_parameter("specular", 0.5)
	m.set_shader_parameter("metallic", 1.0)
	m.set_shader_parameter("normal_scale", 1.0)
	m.set_shader_parameter("ao_light_affect", 0.0)
	m.set_shader_parameter("uv1_scale", Vector3(1, 1, 1))
	m.set_shader_parameter("uv1_offset", Vector3(0, 0, 0))
	m.set_shader_parameter("uv2_scale", Vector3(1, 1, 1))
	m.set_shader_parameter("uv2_offset", Vector3(0, 0, 0))
	m.set_shader_parameter("texture_albedo", load(TEX % base))
	m.set_shader_parameter("texture_metallic", load(TEX % (base + 1)))
	m.set_shader_parameter("texture_roughness", load(TEX % (base + 1)))
	m.set_shader_parameter("texture_normal", load(TEX % (base + 2)))
	m.set_shader_parameter("texture_ambient_occlusion", load(TEX % (base + 1)))
	return m


func _run() -> void:
	var root: Node3D = (load(GLB) as PackedScene).instantiate()
	var player: AnimationPlayer = root.find_children("*", "AnimationPlayer", true, false)[0]
	var src := player.get_animation(ANIM)
	_check_loop(src)
	var lib := AnimationLibrary.new()
	lib.add_animation("fire", _build(src, _frames(FIRE)))
	for n in range(1, SHELLS + 1):
		lib.add_animation("reload_%d" % n, _reload(src, n))
	lib.add_animation("reload", _reload(src, SHELLS))
	lib.add_animation("reload_full", _reload(src, SHELLS))
	lib.add_animation("idle", _build(src, _frames(IDLE), true))
	lib.add_animation("take", _build(src, _frames(TAKE)))
	lib.add_animation("hide", _build(src, _frames(HIDE)))
	lib.add_animation("melee", _build(src, _frames(MELEE)))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	var ok := ResourceSaver.save(lib, OUT_DIR + "animations.res")
	ok = ok if ok != OK else ResourceSaver.save(_material("arms", 0), OUT_DIR + "arms.tres")
	ok = ok if ok != OK else ResourceSaver.save(_material("peacemaker", 3), OUT_DIR + "peacemaker.tres")
	print("ressources enregistrées : ", ok == OK)
	root.free()
	quit(0 if ok == OK else 1)


# Le geste doit finir dans la pose où il commence, sinon la boucle saute : affiche l'écart le plus grand.
func _check_loop(src: Animation) -> void:
	var worst := 0.0
	for pair in [RELOAD_SHELL, [RELOAD_START[0], RELOAD_END[1]]]:
		for t in src.get_track_count():
			if src.track_get_type(t) == Animation.TYPE_ROTATION_3D:
				var qa := src.rotation_track_interpolate(t, pair[0] / FPS)
				var qb := src.rotation_track_interpolate(t, pair[1] / FPS)
				var angle := rad_to_deg(qa.angle_to(qb))
				if angle > 3.0:
					print("  écart ", pair, " ", src.track_get_path(t), " : ", angle, "°")
				worst = maxf(worst, angle)
	print("écart de pose au raccord : ", worst, "°")
