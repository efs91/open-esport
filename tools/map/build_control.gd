extends SceneTree
## Godot --headless --path . --script res://tools/map/build_control.gd -- --force
const DIR = "res://maps/controle/terrain"
const DEST = "res://scenes/maps/Controle.tscn"
const WIDTH = 320
const DEPTH = 208
var scene := Node3D.new()
var terrain := Terrain3D.new()
var rng := RandomNumberGenerator.new()
var plan := Image.create_empty(800,520,false,Image.FORMAT_RGB8)
var serial := 0
var cover_boxes: Array[AABB] = []
var zones := Node3D.new()

func height_at(x:float,z:float)->float:
	var bank := 6.0 + 7.0 * smoothstep(18,46,-z) - 5.0 * smoothstep(18,46,z)
	bank = lerpf(bank,6.0,smoothstep(92,117,absf(x)))
	var gully := 1.0-smoothstep(12,34,absf(x))
	var rim := maxf(smoothstep(83,109,absf(z)),smoothstep(146,171,absf(x)))
	var wild := smoothstep(9,25,minf(absf(z),absf(absf(z)-57))) * smoothstep(20,34,absf(absf(x)-65)) * (1-smoothstep(92,110,absf(x)))
	return lerpf(bank+wild*(1.8+1.7*cos(x*.13)*sin(z*.2)),-6.0+0.6*cos(z*.14),gully)+rim*(10+5*sin(x*.12)*cos(z*.1))

func road(x:float,z:float)->bool:
	var bend := 65+11*sin(PI*absf(z)/57)
	var outer := 129-25*smoothstep(13,57,absf(z))
	return (minf(absf(z),absf(absf(z)-57))<6 and absf(x)<outer) or (minf(absf(absf(x)-bend),absf(absf(x)-outer))<6 and absf(z)<61)

func building(asset:String,center:Vector3,side:int,half_size:Vector2):
	var n=load("res://scenes/maps/orion/"+asset+".tscn").instantiate()
	n.rotation.y=PI if side<0 else 0.0
	n.position=center-n.basis*Vector3(half_size.x,0,half_size.y)
	zones.add_child(n,true)
	n.owner=scene

func beacon():
	for i in 16:
		var a=TAU*i/16
		var rail=MeshInstance3D.new()
		var tile=BoxMesh.new()
		tile.size=Vector3(3.55,.10,.4)
		var m=StandardMaterial3D.new()
		m.emission_enabled=true
		m.emission=Color(.55,.10,.94)
		m.emission_energy_multiplier=2
		tile.material=m
		rail.mesh=tile
		rail.position=Vector3(9*sin(a),6.05,9*cos(a))
		rail.rotation.y=a
		attach(rail,"Anneau")
	var crystal=MeshInstance3D.new()
	var mesh=PrismMesh.new()
	mesh.size=Vector3(2,4,2)
	var mat=StandardMaterial3D.new()
	mat.emission_enabled=true
	mat.emission=Color(.45,.08,.86)
	mat.emission_energy_multiplier=2.4
	mesh.material=mat
	crystal.mesh=mesh
	crystal.position=Vector3(0,14,0)
	crystal.rotation.z=.24
	attach(crystal,"Cristal")

func attach(n:Node, label:String):
	serial+=1
	n.name=label+str(serial)
	scene.add_child(n)
	n.owner=scene

func box(label:String,x:float,y:float,z:float,w:float,h:float,d:float,surf:int=6,tint:Color=Color(.55,.64,.69)):
	var n=StaticBody3D.new()
	n.set_script(load("res://scripts/online/CoverBox.cs"))
	n.position=Vector3(x,y,z)
	n.set("Size",Vector3(w,h,d))
	n.set("Surface",surf)
	n.set("Color",tint)
	attach(n,label)
	cover_boxes.append(AABB(Vector3(x-w*.5,y,z-d*.5),Vector3(w,h,d)))
	return n

func marker(label:String,kind:int,x:float,y:float,z:float,team:int,index:int,r:float):
	var n=Marker3D.new()
	n.set_script(load("res://scripts/online/MapMarker.cs"))
	n.position=Vector3(x,y,z)
	n.rotation.y = -PI/2 if x<0 else PI/2
	n.set("Kind",kind)
	n.set("Team",team)
	n.set("Index",index)
	n.set("Radius",r)
	attach(n,label)

func bridge(z:float,y:float,w:float):
	box("Tablier",0,y-.65,z,70,.65,w,6)
	for x in [-24,-10,10,24]:
		box("Pile",x,-7,z,2.8,y+6.4,w-2,1)
	for side in [-1,1]:
		for x in [-26,-13,13,26]:
			box("Glissiere",x,y,z+side*(w/2-.35),11,1,.65)
			box("Marquage",x,y+1,z+side*(w/2-.35),11,.12,.68,6,Color(.98,.65,.14))

func facility(x:float,z:float,y:float,base:bool):
	var sign_x=signf(x)
	var accent=Color(.12,.52,1) if x<0 else Color(.96,.27,.12)
	for side in [-1,1]:
		var xx=x+side*13
		box("Bloc",xx,y-.7,z+13,7,6.7,8,1)
		box("Corniche",xx,y+5.7,z+13,8,.7,9)
		box("Bande",xx,y+4.4,z+8.94,6.5,.65,.15,6,accent)
		box("Abri",xx,y-.6,z-14,8,3.5,4,1)
		box("Auvent",xx,y+2.9,z-14,9,.4,5)
	if base:
		box("Hangar",x+sign_x*13,y-.2,z,2,9,20,1)
		box("Poutre",x+sign_x*10,y+8.6,z,8,.65,26,6,accent)
	else:
		box("Antenne",x+sign_x*13,y+6,z+13,.42,7,.42)
		box("Balise",x+sign_x*13,y+12.8,z+13,1,.6,1,6,accent)

func _init():
	if FileAccess.file_exists(DEST) and not "--force" in OS.get_cmdline_user_args():
		push_error("La scène existe. Utiliser --force pour régénérer le canyon.")
		quit(1)
		return
	build.call_deferred()
func build():
	rng.seed=4821
	scene.set_script(load("res://scripts/online/SimpleMap.cs"))
	scene.name="Controle"
	scene.set("Size",Vector2(WIDTH,DEPTH))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(DIR))
	terrain.name="Terrain3D"
	terrain.region_size=256
	var library=load("res://maps/frontiere01/terrain_assets.tres")
	var grass=library.get_texture(0).duplicate()
	grass.albedo_texture=load("res://maps/controle/prairie.png")
	grass.uv_scale=.16
	grass.albedo_color=Color(.73,.90,.61)
	var local_assets=Terrain3DAssets.new()
	for i in 9:
		local_assets.set_texture(i,grass if i==0 else library.get_texture(i))
	var vegetation_ids=[0,6,8,9,10,11]
	for i in vegetation_ids.size():
		var asset=library.get_mesh_asset(vegetation_ids[i]).duplicate()
		asset.id=i
		if vegetation_ids[i] in [0,10,11]: asset.visibility_range=290
		local_assets.set_mesh_asset(i,asset)
	local_assets.save("res://maps/controle/terrain_assets.tres")
	terrain.assets=local_assets
	terrain.collision_enabled=false
	root.add_child(terrain)
	await process_frame
	terrain.material.world_background=Terrain3DMaterial.NONE
	var heights=Image.create_empty(512,512,false,Image.FORMAT_RF)
	var paint=PackedByteArray()
	paint.resize(512*512*4)
	for j in 512:
		for i in 512:
			var x=i-256
			var z=j-256
			heights.set_pixel(i,j,Color(height_at(x,z),0,0))
			var tex=7 if road(x,z) else (2 if abs(x)<26 or abs(z)>89 else 0)
			paint.encode_u32((j*512+i)*4,tex<<27)
	terrain.data.import_images([heights,Image.create_from_data(512,512,false,Image.FORMAT_RF,paint),null],Vector3(-256,0,-256),0,1)
	bridge(0,6,15)
	bridge(-57,13,12)
	bridge(57,1,14)
	zones.name="Zones"
	scene.add_child(zones)
	zones.owner=scene
	for sign_z in [-1,1]:
		for i in 24:
			var t=(i+.5)/24.
			var z=sign_z*(11+t*42)
			var deck=lerpf(6,13 if sign_z<0 else 1,t)
			box("Rampe",0,deck-.65,z,7,.65,1.77)
			if i%4==0:
				box("Support",0,-6,z,1.2,deck+5.4,1.2)
	# Shallow water in the bed; the banks and the lower route remain accessible by foot.
	var stream=MeshInstance3D.new()
	var water=PlaneMesh.new()
	water.size=Vector2(35,245)
	var wet=StandardMaterial3D.new()
	wet.albedo_color=Color(.10,.31,.36)
	wet.roughness=.13
	wet.metallic=.36
	water.material=wet
	stream.mesh=water
	stream.position.y=-5.75
	attach(stream,"Riviere")
	beacon()
	# Midfield platform and its four service bays below the beacon.
	box("Noyau",0,5.35,0,24,.65,24,6,Color(.46,.39,.59))
	for i in 14:
		var z=-13+i*2
		var w=2*sqrt(16*16-z*z)
		box("Couronne",0,5.6,z,w,.4,2,6,Color(.58,.55,.63))
	for side in [-1,1]:
		for zs in [-1,1]:
			box("Pilier",side*9,-7,zs*9,2,12.4,2)
			box("BaliseC",side*10,6,zs*10,1.2,2.4,1.2,6,Color(.68,.27,.95))
	for side in [-1,1]:
		var t=0 if side<0 else 1
		facility(side*129,0,6,true)
		building("Hangar",Vector3(side*125,6,41),side,Vector2(14,10))
		for bay_z in [-23,23]:
			box("AireGarage",side*124,5.65,bay_z,17,.35,13,6)
			for edge in [-1,1]:
				box("Balisage",side*124,6,bay_z+edge*6,16,.04,.32,6,Color(.99,.69,.14))
				box("Poteau",side*131,6,bay_z+edge*5.8,.65,9,.65)
			box("ToitGarage",side*129,15,bay_z,7,.35,13)
		for z in [-57,57]:
			var y=height_at(side*65,z)
			facility(side*65,z,y,false)
			building("Poste_Securite",Vector3(side*61,y,z+signi(z)*20),side,Vector2(8,6))
			for dx in [-24,24]:
				box("Fret",side*65+dx,y,z+9,5,2.2,3,7)
		for i in 10:
			marker("Depart",2,side*(120+4*(i%5)),6,-4+8*floori(i/5.0),t,0,2)
		marker("Hover",6,side*124,6,-23,t,0,5)
		marker("Marcheur",6,side*124,6,23,t,1,5)
	marker("A",3,-65,13,-57,255,0,8)
	marker("B",3,65,13,-57,255,1,8)
	marker("C",3,0,6,0,255,2,9)
	marker("D",3,-65,1,57,255,3,8)
	marker("E",3,65,1,57,255,4,8)
	var spawn=Marker3D.new()
	spawn.name="SpawnPoint"
	spawn.position=Vector3(-120,6.2,-4)
	scene.add_child(spawn)
	spawn.owner=scene
	for id in vegetation_ids:
		var transforms:Array[Transform3D]=[]
		var count=340 if id==0 else (620 if id==6 else (5800 if id<10 else 100))
		for i in count:
			var x=rng.randf_range(-157,157)
			var z=rng.randf_range(-102,102)
			if road(x,z) or abs(x)<16 or (abs(x)>106 and abs(z)<34) or (abs(abs(x)-65)<24 and abs(abs(z)-57)<27): continue
			var s=rng.randf_range(.8,1.8) if id<10 else rng.randf_range(2.3,5.5)
			if id==0:
				if absf(z)<83 and absf(x)<143 and not (absf(x)<42 and absf(z)>22): continue
				s=rng.randf_range(.7,1.4)
			if id>=10 and abs(x)<29: s*=1.7
			if abs(abs(x)-125)<20 and abs(z-41)<16: continue
			var angle=rng.randf_range(-PI,PI)
			var at=Vector3(x,height_at(x,z),z)
			var xf=Transform3D(Basis(Vector3.UP,angle).scaled(Vector3.ONE*s),at)
			transforms.append(xf)
			if id==0:
				box("Tronc",x,at.y,z,.8*s,7*s,.8*s,9).visible=false
			elif id>=10:
				var native=AABB(Vector3(-.567,-.05,-.848),Vector3(1.135,.747,1.697)) if id==10 else AABB(Vector3(-.671,-.05,-.828),Vector3(1.342,.633,1.655))
				var b=xf*native
				var center=b.get_center()
				box("Rocher",center.x,b.position.y,center.z,b.size.x,b.size.y,b.size.z,3).visible=false
		terrain.instancer.add_transforms(vegetation_ids.find(id),transforms,PackedColorArray(),false)
	terrain.instancer.force_update_mmis()
	terrain.data.save_directory(DIR)
	root.remove_child(terrain)
	scene.add_child(terrain)
	terrain.owner=scene
	terrain.data_directory=DIR
	terrain.collision_enabled=true
	terrain.collision_mode=Terrain3D.CollisionMode.FULL_GAME
	var sun=load("res://scenes/maps/lighting/afternoon_sun.tscn").instantiate()
	attach(sun,"Soleil")
	var env=WorldEnvironment.new()
	env.environment=load("res://scenes/maps/lighting/frontiere_daylight.tres")
	attach(env,"Ciel")
	var packed=PackedScene.new()
	packed.pack(scene)
	var err=ResourceSaver.save(packed,DEST)
	for j in 520:
		for i in 800:
			var x=i*.4-160
			var z=j*.4-104
			var color=Color("#526552").lightened(clampf(height_at(x,z)*.008,0,.17))
			if abs(x)<24: color=Color("#454f55")
			if road(x,z): color=Color("#939596")
			plan.set_pixel(i,j,color)
	for b in cover_boxes:
		plan.fill_rect(Rect2i(Vector2i((b.position.x+160)*2.5,(b.position.z+104)*2.5),Vector2i(maxi(1,roundi(b.size.x*2.5)),maxi(1,roundi(b.size.z*2.5)))),Color("#67737a"))
	plan.resize(1024,1024)
	var letters=["010101111101101","110101110101110","111100100100111","110101101101110","111100110100111"]
	for n in scene.get_children():
		if n.get_script()!=load("res://scripts/online/MapMarker.cs"): continue
		var at=Vector2i(roundi((n.position.x+160)*1024/320),roundi((n.position.z+104)*1024/208))
		if n.get("Kind")==3:
			var index:int=n.get("Index")
			plan.fill_rect(Rect2i(at-Vector2i(16,19),Vector2i(32,38)),Color("#c899ff") if index==2 else Color("#bfcbd4"))
			plan.fill_rect(Rect2i(at-Vector2i(13,16),Vector2i(26,32)),Color("#191f29"))
			for pixel in 15:
				if letters[index][pixel]=="1":
					plan.fill_rect(Rect2i(at+Vector2i(pixel%3*5-7,floori(pixel/3.)*5-12),Vector2i(5,5)),Color.WHITE)
		elif n.get("Kind")==6:
			plan.fill_rect(Rect2i(at-Vector2i(5,5),Vector2i(10,10)),Color("#52aaff") if n.get("Team")==0 else Color("#ff6a50"))
	plan.save_png("res://assets/maps/controle.png")
	print("Controle: ", error_string(err))
	scene.free()
	quit(err)
