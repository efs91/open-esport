using System;
using System.Collections.Generic;
using System.IO;

namespace OpenEsport.Core
{
	public struct Aabb
	{
		public Vec3 Min, Max;
		public Aabb(Vec3 min, Vec3 max) { Min = min; Max = max; }
	}

	/// <summary>Un câble de tyrolienne : polyligne de points (position du câble, que le joueur tient au-dessus de sa tête).</summary>
	public sealed class ZipLine
	{
		public Vec3[] Points = new Vec3[0];
		public float[] Cumulative = new float[0];
		public float Length;

		public void Build()
		{
			Cumulative = new float[Points.Length];
			float acc = 0f;
			for (int i = 1; i < Points.Length; i++)
			{
				acc += Vec3.Distance(Points[i - 1], Points[i]);
				Cumulative[i] = acc;
			}
			Length = acc;
		}

		/// <summary>Position et direction unitaire (vers la fin du câble) à la distance donnée depuis son début.</summary>
		public void PointAt(float dist, out Vec3 pos, out Vec3 tangent)
		{
			if (Points.Length < 2) { pos = Points.Length == 1 ? Points[0] : Vec3.Zero; tangent = new Vec3(0, 0, -1); return; }
			dist = Math.Clamp(dist, 0f, Length);
			int i = 1;
			while (i < Points.Length - 1 && Cumulative[i] < dist) i++;
			float seg = Cumulative[i] - Cumulative[i - 1];
			float t = seg < 1e-5f ? 0f : (dist - Cumulative[i - 1]) / seg;
			var a = Points[i - 1]; var b = Points[i];
			pos = new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
			tangent = (b - a).Normalized();
		}
	}

	/// <summary>Tremplin : celui qui marche dessus est projeté jusqu'au point <see cref="Dist"/> du câble <see cref="Line"/>.</summary>
	public struct LaunchPad
	{
		public Vec3 Pos;
		public ushort Line;
		public float Dist;
	}

	/// <summary>Tremplin d'arène : celui qui marche dessus (disque de rayon <see cref="Radius"/> autour de <see cref="Pos"/>) est projeté en cloche
	/// jusqu'au point <see cref="Target"/>, qu'il atteint en <see cref="Time"/> secondes ; pas de correction en l'air pendant le vol.</summary>
	public struct JumpPad
	{
		public Vec3 Pos, Target;
		public float Radius, Time;
	}

	/// <summary>Téléporteur : celui qui entre dans le disque de rayon <see cref="Radius"/> autour de <see cref="Pos"/> réapparaît en <see cref="Dest"/>,
	/// tourné vers <see cref="Yaw"/> (radians, comme les joueurs), avec sa vitesse horizontale dans cette direction.</summary>
	public struct Teleporter
	{
		public Vec3 Pos, Dest;
		public float Radius, Yaw;
	}

	/// <summary>
	/// Un point de butin posé dans l'éditeur (nœud LootPoint de la carte) : un objet au sol ou un coffre.
	/// Les points désactivés à la main ne sont pas écrits dans le fichier. Les autres sont soit toujours présents, soit tirés au sort
	/// à chaque partie (<see cref="Chance"/> propre au point, sinon le réglage commun loot.pointChance / loot.chestChance).
	/// </summary>
	public enum MapPointKind : byte
	{
		/// <summary>Point d'apparition d'un chacun-pour-soi (Deathmatch).</summary>
		SpawnFfa = 1,
		/// <summary>Point d'apparition d'une équipe (Team = 0 bleue, 1 rouge ; en Sabotage 0 = défenseurs, 1 = attaquants).</summary>
		SpawnTeam = 2,
		/// <summary>Relais A à E (Index 0 à 4 ; Team = propriétaire au départ, 255 = personne).</summary>
		ControlPoint = 3,
		/// <summary>Base d'un drapeau (Team = l'équipe à qui il appartient).</summary>
		FlagBase = 4,
		/// <summary>Site de bombe (Index 0 = A, 1 = B).</summary>
		BombSite = 5,
		VehicleBay = 6,
	}

	/// <summary>Un point d'attention posé dans la carte (nœud MapPoint) : apparitions, points de contrôle, bases de drapeau, sites de bombe.</summary>
	public struct MapPoint
	{
		public MapPointKind Kind;
		public byte Team, Index;
		public Vec3 Pos;
		public float Radius, Yaw;

		/// <summary>Le point (x, z) est-il dans la zone carrée de demi-côté <see cref="Radius"/>, tournée de <see cref="Yaw"/> ? (sites de bombe : toute la zone, pas un disque)</summary>
		public readonly bool InSquare(float x, float z)
		{
			float dx = x - Pos.X, dz = z - Pos.Z;
			float c = MathF.Cos(Yaw), s = MathF.Sin(Yaw);
			float lx = dx * c - dz * s, lz = dx * s + dz * c;
			return MathF.Abs(lx) <= Radius && MathF.Abs(lz) <= Radius;
		}
	}

	public struct LootSpawn
	{
		public const byte KindItem = 0, KindChest = 1;
		public const byte ModeAlways = 0, ModeRandom = 1;

		public Vec3 Pos;
		/// <summary>Objet imposé ; 0 = tiré au hasard dans la table de butin à chaque partie (ignoré pour un coffre).</summary>
		public byte ItemId;
		public ushort Count;
		public byte Kind;
		public byte Mode;
		/// <summary>Probabilité propre à ce point (0 à 1) quand <see cref="HasChance"/> ; sinon celle du réglage commun.</summary>
		public bool HasChance;
		public float Chance;
		/// <summary>Niveau du butin : 0 = tirage normal du jeu ; 1 à 4 = en majorité classique / rare / épique / légendaire ; 5 à 8 = uniquement classique / rare / épique / légendaire.</summary>
		public byte Quality;
		/// <summary>Rotation du coffre autour de la verticale (radians, comme le nœud Godot).</summary>
		public float Yaw;
		/// <summary>Liste précise d'objets possibles (null = tirage normal) et leurs poids : un objet de la liste pour un point « objet », les 4 objets du contenu pour un coffre.</summary>
		public byte[] PoolIds;
		public float[] PoolWeights;

		/// <summary>Un objet de la liste, tiré selon les poids ; <see cref="ItemIds.None"/> s'il n'y a pas de liste.</summary>
		public byte PickFromPool(Random rng)
		{
			if (PoolIds == null || PoolIds.Length == 0) return ItemIds.None;
			double total = 0;
			for (int i = 0; i < PoolIds.Length; i++) total += PoolWeights[i];
			double r = rng.NextDouble() * total;
			for (int i = 0; i < PoolIds.Length; i++) { r -= PoolWeights[i]; if (r < 0) return PoolIds[i]; }
			return PoolIds[PoolIds.Length - 1];
		}
		public bool HasPool => PoolIds != null && PoolIds.Length > 0;

		public bool IsChest => Kind == KindChest;
	}

	/// <summary>
	/// Carte telle que la voient la simulation et le serveur : relief (grille de hauteurs), obstacles, points d'apparition et de butin.
	/// Le même fichier est chargé par le client et par le serveur, ce qui garantit des collisions identiques.
	/// </summary>
	public sealed class WorldData
	{
		private const uint Magic = 0x3157454F; // "OEW1"
		private const ushort FormatVersion = 9;

		public float OriginX, OriginZ, CellSize = 1f;
		public int Width, Depth;
		public float[] Heights = new float[0];
		public readonly List<Aabb> Panes = new List<Aabb>();
		public List<Aabb> Boxes = new List<Aabb>();
		/// <summary>Anciennes cartes (format 2) : un emplacement de butin sur douze était un coffre.</summary>
		private const int LegacyChestEvery = 12;
		/// <summary>Volume plein d'un coffre posé en <paramref name="pos"/> : 1,5 x 0,63 x 0,84 m (le modèle).</summary>
		public static Aabb ChestBox(Vec3 pos) => ChestBox(pos, 0f);

		/// <summary>Volume d'un coffre tourné de <paramref name="yaw"/> : la boîte alignée sur les axes qui contient le coffre (exacte aux quarts de tour, un peu plus large entre deux).</summary>
		public static Aabb ChestBox(Vec3 pos, float yaw)
		{
			float c = MathF.Abs(MathF.Cos(yaw)), s = MathF.Abs(MathF.Sin(yaw));
			float ex = c * 0.75f + s * 0.42f, ez = s * 0.75f + c * 0.42f;
			return new Aabb(new Vec3(pos.X - ex, pos.Y, pos.Z - ez), new Vec3(pos.X + ex, pos.Y + 0.63f, pos.Z + ez));
		}

		/// <summary>Décalage d'un point du coffre (repère du coffre) une fois tourné de <paramref name="yaw"/> (rotation Godot autour de Y).</summary>
		public static Vec3 ChestOffset(Vec3 local, float yaw)
		{
			float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
			return new Vec3(local.X * c + local.Z * s, local.Y, -local.X * s + local.Z * c);
		}

		public List<Vec3> SpawnPoints = new List<Vec3>();
		public List<LootSpawn> LootSpawns = new List<LootSpawn>();
		public List<ZipLine> Ziplines = new List<ZipLine>();
		public List<LaunchPad> Pads = new List<LaunchPad>();
		/// <summary>Tremplins d'arène (vers un point) et téléporteurs (format 9).</summary>
		public List<JumpPad> Jumps = new List<JumpPad>();
		public List<Teleporter> Teleports = new List<Teleporter>();
		/// <summary>Points d'attention des modes de jeu (voir <see cref="MapPoint"/>).</summary>
		public List<MapPoint> Points = new List<MapPoint>();

		public IEnumerable<MapPoint> PointsOf(MapPointKind kind) { foreach (var p in Points) if (p.Kind == kind) yield return p; }

		public float MinX => OriginX;
		public float MaxX => OriginX + (Width - 1) * CellSize;
		public float MinZ => OriginZ;
		public float MaxZ => OriginZ + (Depth - 1) * CellSize;
		public float MinHeight { get; private set; }
		public float MaxHeight { get; private set; }

		/// <summary>Hauteur du relief, interpolée entre les quatre points les plus proches. Hors carte, on garde la hauteur du bord.</summary>
		public float HeightAt(float x, float z)
		{
			float fx = (x - OriginX) / CellSize;
			float fz = (z - OriginZ) / CellSize;
			float maxX = Width - 1.001f, maxZ = Depth - 1.001f;
			if (fx < 0) fx = 0; else if (fx > maxX) fx = maxX;
			if (fz < 0) fz = 0; else if (fz > maxZ) fz = maxZ;
			int ix = (int)fx, iz = (int)fz;
			float tx = fx - ix, tz = fz - iz;
			int i = iz * Width + ix;
			float h00 = Heights[i], h10 = Heights[i + 1], h01 = Heights[i + Width], h11 = Heights[i + Width + 1];
			float a = h00 + (h10 - h00) * tx;
			float b = h01 + (h11 - h01) * tx;
			return a + (b - a) * tz;
		}

		/// <summary>Empreinte de la carte : client et serveur la comparent à la connexion pour être sûrs de simuler le même relief.</summary>
		public uint Checksum()
		{
			uint h = 2166136261u;
			void Mix(uint v) { h = (h ^ v) * 16777619u; }
			Mix((uint)Width); Mix((uint)Depth); Mix((uint)BitConverter.SingleToInt32Bits(CellSize));
			Mix((uint)BitConverter.SingleToInt32Bits(OriginX)); Mix((uint)BitConverter.SingleToInt32Bits(OriginZ));
			for (int i = 0; i < Heights.Length; i += 7) Mix((uint)BitConverter.SingleToInt32Bits(Heights[i]));
			Mix((uint)Boxes.Count);
			void MixPoint(Vec3 point)
			{
				Mix((uint)BitConverter.SingleToInt32Bits(point.X));
				Mix((uint)BitConverter.SingleToInt32Bits(point.Y));
				Mix((uint)BitConverter.SingleToInt32Bits(point.Z));
			}
			foreach (var pane in Panes) { MixPoint(pane.Min); MixPoint(pane.Max); }
			Mix((uint)Ziplines.Count); Mix((uint)Pads.Count);
			foreach (var z in Ziplines) Mix((uint)BitConverter.SingleToInt32Bits(z.Length));
			Mix((uint)Jumps.Count); Mix((uint)Teleports.Count);
			foreach (var j in Jumps) { MixPoint(j.Pos); MixPoint(j.Target); }
			foreach (var t in Teleports) { MixPoint(t.Pos); MixPoint(t.Dest); }
			return h;
		}

		public void RecomputeBounds()
		{
			float lo = float.MaxValue, hi = float.MinValue;
			for (int i = 0; i < Heights.Length; i++)
			{
				if (Heights[i] < lo) lo = Heights[i];
				if (Heights[i] > hi) hi = Heights[i];
			}
			MinHeight = lo;
			MaxHeight = hi;
		}

		/// <summary>Arrondit les hauteurs au format de stockage (16 bits), pour que la carte en mémoire soit identique à celle relue du fichier.</summary>
		public void Quantize()
		{
			RecomputeBounds();
			float range = Math.Max(MaxHeight - MinHeight, 0.001f);
			float step = range / 65535f;
			for (int i = 0; i < Heights.Length; i++)
			{
				int q = (int)MathF.Round((Heights[i] - MinHeight) / step);
				Heights[i] = MinHeight + q * step;
			}
			RecomputeBounds();
		}

		public void Save(Stream stream)
		{
			Quantize();
			float range = Math.Max(MaxHeight - MinHeight, 0.001f);
			float step = range / 65535f;
			using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				w.Write(Magic); w.Write(FormatVersion);
				w.Write(OriginX); w.Write(OriginZ); w.Write(CellSize); w.Write(Width); w.Write(Depth);
				w.Write(MinHeight); w.Write(step);
				for (int i = 0; i < Heights.Length; i++)
					w.Write((ushort)MathF.Round((Heights[i] - MinHeight) / step));
				w.Write(Boxes.Count);
				foreach (var b in Boxes) { WriteVec(w, b.Min); WriteVec(w, b.Max); }
				w.Write(SpawnPoints.Count);
				foreach (var p in SpawnPoints) WriteVec(w, p);
				w.Write(LootSpawns.Count);
				foreach (var l in LootSpawns) { WriteVec(w, l.Pos); w.Write(l.ItemId); w.Write(l.Count); w.Write(l.Kind); w.Write(l.Mode); w.Write(l.HasChance); w.Write(l.Chance); w.Write(l.Quality); w.Write(l.Yaw);
					int pc = l.PoolIds == null ? 0 : l.PoolIds.Length;
					w.Write((byte)pc);
					for (int k = 0; k < pc; k++) { w.Write(l.PoolIds[k]); w.Write(l.PoolWeights[k]); }
				}
				w.Write(Ziplines.Count);
				foreach (var z in Ziplines) { w.Write(z.Points.Length); foreach (var p in z.Points) WriteVec(w, p); }
				w.Write(Pads.Count);
				foreach (var p in Pads) { WriteVec(w, p.Pos); w.Write(p.Line); w.Write(p.Dist); }
				w.Write(Points.Count);
				foreach (var p in Points) { w.Write((byte)p.Kind); w.Write(p.Team); w.Write(p.Index); WriteVec(w, p.Pos); w.Write(p.Radius); w.Write(p.Yaw); }
				w.Write(Panes.Count);
				foreach (var pane in Panes) { WriteVec(w, pane.Min); WriteVec(w, pane.Max); }
				w.Write(Jumps.Count);
				foreach (var j in Jumps) { WriteVec(w, j.Pos); WriteVec(w, j.Target); w.Write(j.Radius); w.Write(j.Time); }
				w.Write(Teleports.Count);
				foreach (var t in Teleports) { WriteVec(w, t.Pos); WriteVec(w, t.Dest); w.Write(t.Radius); w.Write(t.Yaw); }
			}
		}

		public static WorldData Load(Stream stream)
		{
			var d = new WorldData();
			using (var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
			{
				if (r.ReadUInt32() != Magic) throw new InvalidDataException("Fichier de carte invalide");
				ushort version = r.ReadUInt16();
				if (version < 1 || version > FormatVersion) throw new InvalidDataException("Version de carte non supportée");
				d.OriginX = r.ReadSingle(); d.OriginZ = r.ReadSingle(); d.CellSize = r.ReadSingle();
				d.Width = r.ReadInt32(); d.Depth = r.ReadInt32();
				if (d.Width < 2 || d.Depth < 2 || (long)d.Width * d.Depth > 64_000_000) throw new InvalidDataException("Dimensions de carte invalides");
				float minH = r.ReadSingle(), step = r.ReadSingle();
				d.Heights = new float[d.Width * d.Depth];
				for (int i = 0; i < d.Heights.Length; i++)
					d.Heights[i] = minH + r.ReadUInt16() * step;
				int n = r.ReadInt32();
				for (int i = 0; i < n; i++) d.Boxes.Add(new Aabb(ReadVec(r), ReadVec(r)));
				n = r.ReadInt32();
				for (int i = 0; i < n; i++) d.SpawnPoints.Add(ReadVec(r));
				n = r.ReadInt32();
				for (int i = 0; i < n; i++)
					{
						var ls = new LootSpawn { Pos = ReadVec(r), ItemId = r.ReadByte(), Count = r.ReadUInt16() };
						if (version >= 3) { ls.Kind = r.ReadByte(); ls.Mode = r.ReadByte(); ls.HasChance = r.ReadBoolean(); ls.Chance = r.ReadSingle(); }
						if (version >= 4) ls.Quality = r.ReadByte();
						if (version >= 6) ls.Yaw = r.ReadSingle();
						if (version >= 5)
						{
							int pc = r.ReadByte();
							if (pc > 0)
							{
								ls.PoolIds = new byte[pc]; ls.PoolWeights = new float[pc];
								for (int k = 0; k < pc; k++) { ls.PoolIds[k] = r.ReadByte(); ls.PoolWeights[k] = r.ReadSingle(); }
							}
						}
						if (version < 3 && i % LegacyChestEvery == 0) ls.Kind = LootSpawn.KindChest;
						d.LootSpawns.Add(ls);
					}
				if (version >= 2)
				{
					n = r.ReadInt32();
					for (int i = 0; i < n; i++)
					{
						int pc = r.ReadInt32();
						if (pc < 0 || pc > 100000) throw new InvalidDataException("Câble invalide");
						var z = new ZipLine { Points = new Vec3[pc] };
						for (int k = 0; k < pc; k++) z.Points[k] = ReadVec(r);
						z.Build();
						d.Ziplines.Add(z);
					}
					n = r.ReadInt32();
					for (int i = 0; i < n; i++) d.Pads.Add(new LaunchPad { Pos = ReadVec(r), Line = r.ReadUInt16(), Dist = r.ReadSingle() });
				}
				if (version >= 7)
				{
					int np = r.ReadInt32();
					for (int i = 0; i < np; i++) d.Points.Add(new MapPoint { Kind = (MapPointKind)r.ReadByte(), Team = r.ReadByte(), Index = r.ReadByte(), Pos = ReadVec(r), Radius = r.ReadSingle(), Yaw = r.ReadSingle() });
				}
				if (version >= 8)
				{
					int count = r.ReadInt32();
					if (count < 0 || count > WindowState.MaxPanes) throw new InvalidDataException("Nombre de vitres invalide");
					for (int i = 0; i < count; i++) d.Panes.Add(new Aabb(ReadVec(r), ReadVec(r)));
				}
				if (version >= 9)
				{
					int nj = r.ReadInt32();
					if (nj < 0 || nj > 4096) throw new InvalidDataException("Nombre de tremplins invalide");
					for (int i = 0; i < nj; i++) d.Jumps.Add(new JumpPad { Pos = ReadVec(r), Target = ReadVec(r), Radius = r.ReadSingle(), Time = r.ReadSingle() });
					int nt = r.ReadInt32();
					if (nt < 0 || nt > 4096) throw new InvalidDataException("Nombre de téléporteurs invalide");
					for (int i = 0; i < nt; i++) d.Teleports.Add(new Teleporter { Pos = ReadVec(r), Dest = ReadVec(r), Radius = r.ReadSingle(), Yaw = r.ReadSingle() });
				}
			}
			d.RecomputeBounds();
			return d;
		}

		public static WorldData LoadFile(string path)
		{
			using (var fs = File.OpenRead(path))
				return Load(fs);
		}

		private static void WriteVec(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
		private static Vec3 ReadVec(BinaryReader r) => new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

		/// <summary>Carte plate, pour les tests.</summary>
		public static WorldData CreateFlat(int sizeMeters = 400, float height = 100f)
		{
			var d = new WorldData { OriginX = -sizeMeters / 2f, OriginZ = -sizeMeters / 2f, CellSize = 2f };
			d.Width = d.Depth = sizeMeters / 2 + 1;
			d.Heights = new float[d.Width * d.Depth];
			for (int i = 0; i < d.Heights.Length; i++) d.Heights[i] = height;
			d.Quantize();
			return d;
		}

		/// <summary>Carte synthétique (collines) pour les tests et les essais de charge.</summary>
		public static WorldData CreateProcedural(int sizeMeters = 2000, int seed = 1, int lootCount = 1500)
		{
			var rng = new Random(seed);
			var d = new WorldData { OriginX = -sizeMeters / 2f, OriginZ = -sizeMeters / 2f, CellSize = 2f };
			d.Width = d.Depth = sizeMeters / 2 + 1;
			d.Heights = new float[d.Width * d.Depth];
			float p1 = (float)rng.NextDouble() * 6f, p2 = (float)rng.NextDouble() * 6f;
			for (int z = 0; z < d.Depth; z++)
				for (int x = 0; x < d.Width; x++)
				{
					float wx = d.OriginX + x * d.CellSize, wz = d.OriginZ + z * d.CellSize;
					d.Heights[z * d.Width + x] = 100f + 6f * MathF.Sin(wx * 0.011f + p1) * MathF.Cos(wz * 0.013f + p2) + 2.5f * MathF.Sin(wx * 0.05f) * MathF.Sin(wz * 0.043f);
				}
			d.Quantize();

			float half = sizeMeters * 0.45f;
			for (int i = 0; i < 128; i++)
			{
				float x = (float)(rng.NextDouble() * 2 - 1) * half, z = (float)(rng.NextDouble() * 2 - 1) * half;
				d.SpawnPoints.Add(new Vec3(x, d.HeightAt(x, z), z));
			}
			for (int i = 0; i < 200; i++)
			{
				float x = (float)(rng.NextDouble() * 2 - 1) * half, z = (float)(rng.NextDouble() * 2 - 1) * half;
				float h = d.HeightAt(x, z), sx = 2 + (float)rng.NextDouble() * 6, sz = 2 + (float)rng.NextDouble() * 6;
				d.Boxes.Add(new Aabb(new Vec3(x - sx / 2, h - 1, z - sz / 2), new Vec3(x + sx / 2, h + 3 + (float)rng.NextDouble() * 4, z + sz / 2)));
			}
			for (int i = 0; i < lootCount; i++)
			{
				float x = (float)(rng.NextDouble() * 2 - 1) * half, z = (float)(rng.NextDouble() * 2 - 1) * half;
				d.LootSpawns.Add(new LootSpawn { Pos = new Vec3(x, d.HeightAt(x, z), z), ItemId = LootTable.Roll(rng), Count = 0 });
			}
			return d;
		}
	}
}
