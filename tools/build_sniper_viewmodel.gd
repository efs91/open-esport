extends SceneTree

# Prépare les ressources de la scène FPS du sniper (addons/fps-hands/fps-sniper/) à partir de « Sniper sans viseur.glb » :
#   - resources/animations.res : le fichier ne contient qu'une animation de 6,7 s (« allanims »), découpée ici en
#     fire (tir + manœuvre du verrou), reload / reload_full, idle, take / hide (même pose de repos) et melee ;
#   - resources/arms.tres et sniper.tres : matériaux de vue FPS (shader du kit, champ de vision fixe pour l'arme).
# La scène fps-sniper.tscn (scène héritée du .glb, machine d'états, métadonnées, MuzzlePoint et Marker3D) est écrite à la main :
# elle n'est pas régénérée. Relancer ce script seulement si le modèle ou les images de découpe changent :
#   Godot --headless --path . --script res://tools/build_sniper_viewmodel.gd

const GLB := "res://assets/armes/Sniper sans viseur.glb"
const OUT_DIR := "res://addons/fps-hands/fps-sniper/resources/"
const FPS := 30.0
const SHADER := "res://addons/fps-hands/shader.gdshader"
const TEX := "res://assets/armes/Sniper sans viseur_Image_%d.png"

# Images de « allanims » : tir et manœuvre du verrou 0-58, rechargement 58-150, repos 165, coup de crosse 180-200.
const FIRE := [0, 58]
const RELOAD := [58, 150]
const IDLE_FRAME := 165
const MELEE := [180, 200]


func _init() -> void:
	_run.call_deferred()


# Copie les images [f0, f1] d'une animation, en échantillonnant chaque image (le découpage reste exact aux bornes).
func _slice(src: Animation, f0: int, f1: int, looped := false) -> Animation:
	var a := Animation.new()
	a.length = maxf((f1 - f0) / FPS, 1.0 / FPS)
	a.loop_mode = Animation.LOOP_LINEAR if looped else Animation.LOOP_NONE
	for t in src.get_track_count():
		var type := src.track_get_type(t)
		if type != Animation.TYPE_POSITION_3D and type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_SCALE_3D:
			continue
		var nt := a.add_track(type)
		a.track_set_path(nt, src.track_get_path(t))
		a.track_set_interpolation_type(nt, Animation.INTERPOLATION_LINEAR)
		for f in range(f0, maxi(f1, f0 + 1) + 1):
			var time := minf(f / FPS, src.length)
			var key_time := (f - f0) / FPS
			match type:
				Animation.TYPE_POSITION_3D:
					a.position_track_insert_key(nt, key_time, src.position_track_interpolate(t, time))
				Animation.TYPE_ROTATION_3D:
					a.rotation_track_insert_key(nt, key_time, src.rotation_track_interpolate(t, time))
				Animation.TYPE_SCALE_3D:
					a.scale_track_insert_key(nt, key_time, src.scale_track_interpolate(t, time))
	return a


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
	var src := player.get_animation("allanims")
	var lib := AnimationLibrary.new()
	lib.add_animation("fire", _slice(src, FIRE[0], FIRE[1]))
	lib.add_animation("reload", _slice(src, RELOAD[0], RELOAD[1]))
	lib.add_animation("reload_full", _slice(src, RELOAD[0], RELOAD[1]))
	lib.add_animation("idle", _slice(src, IDLE_FRAME, IDLE_FRAME + 1, true))
	lib.add_animation("take", _slice(src, IDLE_FRAME, IDLE_FRAME + 1))
	lib.add_animation("hide", _slice(src, IDLE_FRAME, IDLE_FRAME + 1))
	lib.add_animation("melee", _slice(src, MELEE[0], MELEE[1]))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	var ok := ResourceSaver.save(lib, OUT_DIR + "animations.res")
	ok = ok if ok != OK else ResourceSaver.save(_material("arms", 0), OUT_DIR + "arms.tres")
	ok = ok if ok != OK else ResourceSaver.save(_material("sniper", 3), OUT_DIR + "sniper.tres")
	print("ressources enregistrées : ", ok == OK)
	root.free()
	quit(0 if ok == OK else 1)
