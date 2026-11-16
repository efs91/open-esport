extends Node3D
## Inspection locale uniquement : aucune connexion ni modification du monde.
var camera := Camera3D.new()
var label := Label.new()
var points := [Vector3(-360, 85, -258), Vector3(340, 91, -252), Vector3(0, 59, 380)]
var names := ["Orion", "Delta", "Nama"]

func _ready() -> void:
	add_child(camera)
	camera.far = 4000.0
	camera.current = true
	$Frontiere01/Terrain3D.set_camera(camera)
	var ui := CanvasLayer.new()
	add_child(ui)
	ui.add_child(label)
	label.position = Vector2(20, 20)
	label.add_theme_color_override("font_shadow_color", Color.BLACK)
	label.add_theme_constant_override("shadow_offset_x", 2)
	label.add_theme_constant_override("shadow_offset_y", 2)
	_go(0)

func _go(index: int) -> void:
	camera.position = points[index]
	var h: float = $Frontiere01/Terrain3D.data.get_height(camera.position)
	if is_finite(h): camera.position.y = h + 1.7
	camera.rotation = Vector3.ZERO
	label.text = "APERÇU LOCAL — " + names[index] + "\n1 Orion · 2 Delta · 3 Nama\nClic droit maintenu : regarder · ZQSD / WASD : voler\nE / A : monter / descendre · Maj : accélérer · Échap : libérer la souris"

func _input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode >= KEY_1 and event.keycode <= KEY_3:
			_go(event.keycode - KEY_1)
		if event.keycode == KEY_ESCAPE:
			Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_RIGHT:
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED if event.pressed else Input.MOUSE_MODE_VISIBLE
	if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		camera.rotation.y -= event.relative.x * 0.002
		camera.rotation.x = clampf(camera.rotation.x - event.relative.y * 0.002, -1.5, 1.5)

func _process(delta: float) -> void:
	if Input.mouse_mode != Input.MOUSE_MODE_CAPTURED: return
	var move := Vector3.ZERO
	if Input.is_physical_key_pressed(KEY_W): move.z -= 1
	if Input.is_physical_key_pressed(KEY_S): move.z += 1
	if Input.is_physical_key_pressed(KEY_A): move.x -= 1
	if Input.is_physical_key_pressed(KEY_D): move.x += 1
	if Input.is_physical_key_pressed(KEY_E): move.y += 1
	if Input.is_physical_key_pressed(KEY_Q): move.y -= 1
	camera.position += camera.basis * move.normalized() * delta * (90.0 if Input.is_key_pressed(KEY_SHIFT) else 12.0)
