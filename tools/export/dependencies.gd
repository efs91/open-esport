extends SceneTree
## Appelé par select_export.py : lit une liste de ressources (une par ligne) et écrit leurs dépendances complètes, lues dans les fichiers importés.
## Godot se sert pour l'export d'un cache qui peut oublier celles des modèles importés (matières des armes : cube rose dans le jeu exporté).
##   godot --headless --path . --script res://tools/export/dependencies.gd -- <entrée> <sortie>
## Sortie : une ressource par ligne ; « ! ressource <tab> dépendance » quand une dépendance n'existe pas.

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	var todo := FileAccess.get_file_as_string(args[0]).split("\n", false)
	var seen := {}
	var missing := []
	while not todo.is_empty():
		var path: String = todo[todo.size() - 1].strip_edges()
		todo.remove_at(todo.size() - 1)
		if path.is_empty() or seen.has(path): continue
		seen[path] = true
		for dependency in ResourceLoader.get_dependencies(path):
			var target := _resolve(dependency)
			if target.is_empty() or not ResourceLoader.exists(target): missing.append("! %s\t%s" % [path, dependency])
			elif not seen.has(target): todo.append(target)
	var out := FileAccess.open(args[1], FileAccess.WRITE)
	for path in seen: out.store_line(path)
	for line in missing: out.store_line(line)
	out.store_line("# fin")
	out.close()
	quit()

## « uid://x::Type::res://chemin » ou « res://chemin::Type » → chemin réel (l'uid d'abord, comme le fait Godot).
func _resolve(dependency: String) -> String:
	var parts := dependency.split("::")
	if parts[0].begins_with("uid://"):
		var id := ResourceUID.text_to_id(parts[0])
		if ResourceUID.has_id(id): return ResourceUID.get_id_path(id)
	for part in parts:
		if part.begins_with("res://"): return part
	return ""
