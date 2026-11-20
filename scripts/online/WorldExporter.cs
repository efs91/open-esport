using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Outil (hors jeu) : exporte le relief d'une carte Godot (Terrain3D) vers le fichier .world que le serveur et les clients
	/// chargent pour calculer exactement les mêmes collisions.
	///
	/// Lancement :  Godot --headless --path . res://tools/export_world.tscn -- --map=res://demo/Demo.tscn --out=res://assets/maps/demo.world
	/// </summary>
	public partial class WorldExporter : Node
	{
		private const float Cell = 2f;

		public override async void _Ready()
		{
			string map = "res://scenes/maps/Frontiere01.tscn", output = "res://assets/maps/frontiere01.world";
			string extras = "res://maps/frontiere01/data/buildings_final.json", nature = "res://maps/frontiere01/data/nature_boxes.json";
			foreach (var arg in OS.GetCmdlineUserArgs())
			{
				if (arg.StartsWith("--map=")) map = arg.Substring(6);
				else if (arg.StartsWith("--out=")) output = arg.Substring(6);
				else if (arg.StartsWith("--extras=")) extras = arg.Substring(9);
				else if (arg.StartsWith("--nature=")) nature = arg.Substring(9);
			}

			try
			{
				var scene = GD.Load<PackedScene>(map).Instantiate();
				AddChild(scene);
				for (int i = 0; i < 10; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Export(scene, output, extras, nature);
			}
			catch (Exception e)
			{
				GD.PrintErr("[WorldExporter] " + e);
			}
			GetTree().Quit();
		}

		private static float Height(GodotObject source, Vector3 p)
		{
			return source.Call("get_height", p).AsSingle();
		}

		/// <summary>Carte simple d'un mode de jeu (sans Terrain3D) : sol plat, obstacles <see cref="CoverBox"/>, points d'attention <see cref="MapMarker"/>.</summary>
		private static void ExportSimple(SimpleMap map, Node sceneRoot, string output)
		{
			const float cell = 2f;
			float minX = -map.Size.X / 2f, minZ = -map.Size.Y / 2f;
			int width = (int)(map.Size.X / cell) + 1, depth = (int)(map.Size.Y / cell) + 1;
			var data = new WorldData { OriginX = minX, OriginZ = minZ, CellSize = cell, Width = width, Depth = depth, Heights = new float[width * depth] };
			var terrain = map.GetNodeOrNull("Terrain3D");
			if (terrain != null)
			{
				var source = terrain.Call("get_data").AsGodotObject();
				for (int z = 0; z < depth; z++)
					for (int x = 0; x < width; x++)
					{
						float h = Height(source, new Vector3(minX + x * cell, 0, minZ + z * cell));
						if (!float.IsFinite(h)) throw new InvalidOperationException("Région Terrain3D manquante dans la zone jouable.");
						data.Heights[z * width + x] = h;
					}
			}
			data.Quantize();
			int boxes = 0;
			void Walk(Node n)
			{
				foreach (var c in n.GetChildren())
				{
					if (c is CoverBox cb && cb.Solid)         // une dalle de sol (non solide) n'est pas un obstacle
					{
						var b = cb.WorldBounds();
						data.Boxes.Add(new OpenEsport.Core.Aabb(new Vec3(b.Position.X, b.Position.Y, b.Position.Z), new Vec3(b.End.X, b.End.Y, b.End.Z)));
						boxes++;
					}
					else if (c is ArenaJumpPad jp) data.Jumps.Add(jp.ToPad());
					else if (c is ArenaTeleporter tp) data.Teleports.Add(tp.ToTeleporter());
					else if (c is LootPoint lp)
					{
						if (lp.Activation != LootPoint.ActivationEnum.Désactivé) { var g = lp.GlobalPosition; data.LootSpawns.Add(lp.ToSpawn(new Vec3(g.X, g.Y, g.Z))); }
					}
					else if (c is MapMarker mm)
					{
						var g = mm.GlobalPosition;
						var pt = mm.ToPoint(new Vec3(g.X, g.Y, g.Z));
						data.Points.Add(pt);
						if (pt.Kind == MapPointKind.SpawnFfa || pt.Kind == MapPointKind.SpawnTeam) data.SpawnPoints.Add(pt.Pos);
					}
					Walk(c);
				}
			}
			Walk(sceneRoot);
			ExportBuildingColliders(sceneRoot, data);
			string path = ProjectSettings.GlobalizePath(output);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			using (var fs = File.Create(path)) data.Save(fs);
			GD.Print($"[WorldExporter] carte simple écrite : {path} ({new FileInfo(path).Length / 1024} Ko), {boxes} obstacles, {data.Points.Count} points d'attention, {data.LootSpawns.Count} objets, {data.Jumps.Count} tremplins, {data.Teleports.Count} téléporteurs, empreinte {data.Checksum():X8}");
		}

		private static void ExportBuildingColliders(Node scene, WorldData data)
		{
			var zonesNode = scene.GetNodeOrNull<Node>("Zones");
			if (zonesNode != null)
			{
				int sceneBoxes = 0;
				void WalkBoxes(Node n)
				{
					foreach (var c in n.GetChildren())
					{
						if (c is CollisionShape3D cs && cs.Shape is BoxShape3D bs && !cs.Disabled)
						{
							var xf = cs.GlobalTransform;
							var h = bs.Size / 2f;
							var mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
							var mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
							for (int k = 0; k < 8; k++)
							{
								var p = xf * new Vector3((k & 1) == 0 ? -h.X : h.X, (k & 2) == 0 ? -h.Y : h.Y, (k & 4) == 0 ? -h.Z : h.Z);
								mn = new Vector3(MathF.Min(mn.X, p.X), MathF.Min(mn.Y, p.Y), MathF.Min(mn.Z, p.Z));
								mx = new Vector3(MathF.Max(mx.X, p.X), MathF.Max(mx.Y, p.Y), MathF.Max(mx.Z, p.Z));
							}
							var collection = cs.IsInGroup("window_panes") ? data.Panes : data.Boxes;
							collection.Add(new OpenEsport.Core.Aabb(new Vec3(mn.X, mn.Y, mn.Z), new Vec3(mx.X, mx.Y, mx.Z)));
							sceneBoxes++;
						}
						WalkBoxes(c);
					}
				}
				WalkBoxes(zonesNode);
				if (sceneBoxes > 0) GD.Print($"[WorldExporter] boîtes des scènes de bâtiments (Zones) : {sceneBoxes}");
			}
		}

		private static SimpleMap FindSimple(Node n)
		{
			if (n is SimpleMap sm) return sm;
			foreach (var c in n.GetChildren()) { var r = FindSimple(c); if (r != null) return r; }
			return null;
		}

		private void Export(Node scene, string output, string extras, string nature)
		{
			var simple = FindSimple(scene);
			if (simple != null) { ExportSimple(simple, scene, output); return; }
			var terrain = scene.FindChild("Terrain3D", true, false);
			if (terrain == null) throw new InvalidOperationException("Aucun nœud Terrain3D dans la carte");
			GodotObject source = terrain.HasMethod("get_data") ? terrain.Call("get_data").AsGodotObject()
				: terrain.HasMethod("get_storage") ? terrain.Call("get_storage").AsGodotObject() : terrain;
			GD.Print($"[WorldExporter] source des hauteurs : {source.GetClass()}");

			// étendue du relief : sondage grossier de part et d'autre de l'origine
			float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
			const float probeStep = 16f, probeRange = 3000f;
			for (float z = -probeRange; z <= probeRange; z += probeStep)
				for (float x = -probeRange; x <= probeRange; x += probeStep)
				{
					float h = Height(source, new Vector3(x, 0, z));
					if (float.IsNaN(h)) continue;
					minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
					minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
				}
			if (minX > maxX) throw new InvalidOperationException("Le terrain ne renvoie aucune hauteur");
			minX = MathF.Floor(minX / Cell) * Cell; minZ = MathF.Floor(minZ / Cell) * Cell;
			int width = (int)((maxX - minX) / Cell) + 2, depth = (int)((maxZ - minZ) / Cell) + 2;
			GD.Print($"[WorldExporter] étendue : x {minX}..{maxX}, z {minZ}..{maxZ} -> grille {width}x{depth}");

			var data = new WorldData { OriginX = minX, OriginZ = minZ, CellSize = Cell, Width = width, Depth = depth, Heights = new float[width * depth] };
			float fallback = float.MaxValue;
			var holes = new List<int>();
			for (int z = 0; z < depth; z++)
				for (int x = 0; x < width; x++)
				{
					float h = Height(source, new Vector3(minX + x * Cell, 0, minZ + z * Cell));
					if (float.IsNaN(h)) { holes.Add(z * width + x); h = 0; }
					else fallback = MathF.Min(fallback, h);
					data.Heights[z * width + x] = h;
				}
			foreach (int i in holes) data.Heights[i] = fallback; // trous : on pose le sol au point le plus bas
			data.Quantize();
			GD.Print($"[WorldExporter] hauteurs : {data.MinHeight:F1} à {data.MaxHeight:F1} m, {holes.Count} trous comblés");

			// bâtiments : boîtes de collision, butin à l'intérieur et points d'apparition (produits par tools/map/gen_buildings.py)
			bool hasExtras = Godot.FileAccess.FileExists(extras);
			var lootPoints = LootPoint.FindAll(scene);
			bool hasLootPoints = lootPoints.Count > 0;
			var rng = new Random(7);
			var spawnNode = scene.GetNodeOrNull<Node3D>("SpawnPoint");
			Vector3 origin = spawnNode != null ? spawnNode.GlobalPosition : new Vector3((minX + maxX) / 2, 0, (minZ + maxZ) / 2);
			if (hasExtras)
			{
				var dict = Godot.Json.ParseString(Godot.FileAccess.GetFileAsString(extras)).AsGodotDictionary();
				foreach (var v in dict["boxes"].AsGodotArray())
				{
					var bx = v.AsGodotArray();
					data.Boxes.Add(new OpenEsport.Core.Aabb(new Vec3(bx[0].AsSingle(), bx[1].AsSingle(), bx[2].AsSingle()), new Vec3(bx[3].AsSingle(), bx[4].AsSingle(), bx[5].AsSingle())));
				}
				foreach (var v in dict["spawns"].AsGodotArray())
				{
					var sp = v.AsGodotArray();
					data.SpawnPoints.Add(new Vec3(sp[0].AsSingle(), sp[1].AsSingle(), sp[2].AsSingle()));
				}
				// le butin vient des points de la scène (nœuds LootPoint) ; l'ancien fichier ne sert que si la carte n'en a pas
				if (!hasLootPoints)
					foreach (var v in dict["loot"].AsGodotArray())
					{
						var lp = v.AsGodotArray();
						data.LootSpawns.Add(new LootSpawn { Pos = new Vec3(lp[0].AsSingle(), lp[1].AsSingle(), lp[2].AsSingle()), ItemId = ItemIds.None, Count = 0 });
					}
				GD.Print($"[WorldExporter] bâtiments : {data.Boxes.Count} boîtes, {data.LootSpawns.Count} objets, {data.SpawnPoints.Count} apparitions");
			}
			else
			{
				data.SpawnPoints.Add(new Vec3(origin.X, data.HeightAt(origin.X, origin.Z), origin.Z));
				for (int i = 0; i < 40; i++)
				{
					float a = (float)(rng.NextDouble() * Math.PI * 2), r = 8f + (float)rng.NextDouble() * 70f;
					float x = origin.X + MathF.Cos(a) * r, z = origin.Z + MathF.Sin(a) * r;
					data.SpawnPoints.Add(new Vec3(x, data.HeightAt(x, z), z));
				}
				ScatterLoot(data, rng, origin.X, origin.Z, 90f, 140);
			}

			// arbres, rochers et falaises (tools/map/gen_nature_boxes.py)
			if (Godot.FileAccess.FileExists(nature))
			{
				var nd = Godot.Json.ParseString(Godot.FileAccess.GetFileAsString(nature)).AsGodotDictionary();
				foreach (var v in nd["boxes"].AsGodotArray())
				{
					var bx = v.AsGodotArray();
					data.Boxes.Add(new OpenEsport.Core.Aabb(new Vec3(bx[0].AsSingle(), bx[1].AsSingle(), bx[2].AsSingle()), new Vec3(bx[3].AsSingle(), bx[4].AsSingle(), bx[5].AsSingle())));
				}
				GD.Print($"[WorldExporter] avec la nature : {data.Boxes.Count} boîtes");
			}

			// bâtiments éditables dans Godot (Zones/A_Orion…) : les boîtes de collision (CollisionShape3D + BoxShape3D) posées dans les scènes
			// de bâtiments sont lues directement, avec leur transformation dans la carte. Sans ces nœuds, rien n'est ajouté.
			ExportBuildingColliders(scene, data);

			// tyroliennes : câbles, tremplins et piliers (tools/map/gen_ziplines.py)
			string zipPath = "res://maps/frontiere01/data/ziplines.json";
			if (Godot.FileAccess.FileExists(zipPath))
			{
				var zd = Godot.Json.ParseString(Godot.FileAccess.GetFileAsString(zipPath)).AsGodotDictionary();
				foreach (var lv in zd["lines"].AsGodotArray())
				{
					var pts = lv.AsGodotDictionary()["points"].AsGodotArray();
					var zl = new ZipLine { Points = new Vec3[pts.Count] };
					for (int i = 0; i < pts.Count; i++) { var pp = pts[i].AsGodotArray(); zl.Points[i] = new Vec3(pp[0].AsSingle(), pp[1].AsSingle(), pp[2].AsSingle()); }
					zl.Build();
					data.Ziplines.Add(zl);
				}
				foreach (var pv in zd["pads"].AsGodotArray())
				{
					var pd = pv.AsGodotDictionary(); var pos = pd["pos"].AsGodotArray();
					data.Pads.Add(new LaunchPad { Pos = new Vec3(pos[0].AsSingle(), pos[1].AsSingle(), pos[2].AsSingle()), Line = (ushort)pd["line"].AsInt32(), Dist = pd["dist"].AsSingle() });
				}
				string pbPath = "res://maps/frontiere01/data/zipline_boxes.json";
				if (Godot.FileAccess.FileExists(pbPath))
					foreach (var v in Godot.Json.ParseString(Godot.FileAccess.GetFileAsString(pbPath)).AsGodotDictionary()["boxes"].AsGodotArray())
					{
						var bx = v.AsGodotArray();
						data.Boxes.Add(new OpenEsport.Core.Aabb(new Vec3(bx[0].AsSingle(), bx[1].AsSingle(), bx[2].AsSingle()), new Vec3(bx[3].AsSingle(), bx[4].AsSingle(), bx[5].AsSingle())));
					}
				GD.Print($"[WorldExporter] tyroliennes : {data.Ziplines.Count} câbles, {data.Pads.Count} tremplins");
			}

			if (hasLootPoints)
			{
				// butin et coffres : les points posés dans la scène (dossier Loot) ; les points désactivés ne sont pas exportés
				int off = 0, chests = 0, random = 0;
				foreach (var lp in lootPoints)
				{
					if (lp.Activation == LootPoint.ActivationEnum.Désactivé) { off++; continue; }
					var g = lp.GlobalPosition;
					float y = lp.SnapToTerrain ? data.HeightAt(g.X, g.Z) : g.Y;
					var spawn = lp.ToSpawn(new Vec3(g.X, y, g.Z));
					data.LootSpawns.Add(spawn);
					if (spawn.IsChest) chests++;
					if (spawn.Mode == LootSpawn.ModeRandom) random++;
				}
				GD.Print($"[WorldExporter] points de butin de la scène : {data.LootSpawns.Count} exportés ({chests} coffres, {random} au hasard), {off} désactivés");
			}
			else
			{
				// butin en extérieur, réparti sur la carte jouable (hors bâtiments et pentes raides)
				float cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
				ScatterLoot(data, rng, cx, cz, MathF.Min(maxX - minX, maxZ - minZ) * 0.38f, hasExtras ? 420 : 500);
				// anciennes cartes sans points : un emplacement sur douze devient un coffre
				for (int i = 0; i < data.LootSpawns.Count; i += 12) { var l = data.LootSpawns[i]; l.Kind = LootSpawn.KindChest; data.LootSpawns[i] = l; }
			}

			string path = ProjectSettings.GlobalizePath(output);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			using (var fs = File.Create(path)) data.Save(fs);
			GD.Print($"[WorldExporter] carte écrite : {path} ({new FileInfo(path).Length / 1024} Ko), {data.LootSpawns.Count} objets, empreinte {data.Checksum():X8}");
		}

		private static void ScatterLoot(WorldData data, Random rng, float centerX, float centerZ, float radius, int count)
		{
			for (int i = 0; i < count; i++)
			{
				float a = (float)(rng.NextDouble() * Math.PI * 2), r = MathF.Sqrt((float)rng.NextDouble()) * radius;
				float x = centerX + MathF.Cos(a) * r, z = centerZ + MathF.Sin(a) * r;
				if (x < data.MinX + 4 || x > data.MaxX - 4 || z < data.MinZ + 4 || z > data.MaxZ - 4) continue;
				// pas sur une pente raide
				float slope = MathF.Abs(data.HeightAt(x + 2, z) - data.HeightAt(x - 2, z)) / 4f + MathF.Abs(data.HeightAt(x, z + 2) - data.HeightAt(x, z - 2)) / 4f;
				if (slope > 0.6f) continue;
				bool inside = false;
				foreach (var b in data.Boxes)
					if (x > b.Min.X - 0.5f && x < b.Max.X + 0.5f && z > b.Min.Z - 0.5f && z < b.Max.Z + 0.5f && b.Max.Y > data.HeightAt(x, z) + 0.3f) { inside = true; break; }
				if (inside) continue;
				data.LootSpawns.Add(new LootSpawn { Pos = new Vec3(x, data.HeightAt(x, z), z), ItemId = ItemIds.None, Count = 0 });
			}
		}
	}
}
