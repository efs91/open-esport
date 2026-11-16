using System;

namespace OpenEsport.Core
{
	/// <summary>Requêtes de collision utilisées par la simulation (déplacement et tirs).</summary>
	public interface IWorld
	{
		float HeightAt(float x, float z);

		/// <summary>Hauteur du sol sous (x, z) pour quelqu'un dont les pieds sont à feetY : le relief, ou le dessus d'une boîte (sol, marche, toit)
		/// que l'on peut atteindre en montant d'une marche au plus.</summary>
		float GroundAt(float x, float z, float feetY, float stepHeight);
		Vec3 NormalAt(float x, float z);

		/// <summary>Repousse une position hors des obstacles (boîtes).</summary>
		Vec3 ResolveObstacles(Vec3 pos, float radius, float height, float stepHeight);

		/// <summary>Premier impact d'un rayon sur le relief ou un obstacle.</summary>
		bool Raycast(Vec3 origin, Vec3 dir, float maxDist, out float dist);

		void ClampToBounds(ref Vec3 pos);

		/// <summary>Cherche un câble que la main (position <paramref name="hand"/>) peut saisir. <paramref name="onlyLine"/> : −1 = n'importe lequel.</summary>
		bool FindZipline(Vec3 hand, float horizontalTol, float verticalTol, int onlyLine, out int line, out float dist);
		/// <summary>Position et direction du câble à une distance de son début. Retourne sa longueur.</summary>
		bool ZipPointAt(int line, float dist, out Vec3 pos, out Vec3 tangent, out float length);
		/// <summary>Tremplin sous les pieds (position au sol) ?</summary>
		bool PadAt(Vec3 feet, out LaunchPad pad);
	}

	public sealed class StaticWorld : IWorld
	{
		public readonly WorldData Data;

		// Grille d'accélération : les boîtes (par milliers sur une grande carte) sont rangées par cases de 16 m.
		private const float GridCell = 16f;
		private readonly int[][] _grid;
		private readonly int _gridW, _gridD;
		private static readonly int[] None = new int[0];

		/// <summary>Boîtes de collision de la carte.</summary>
		private readonly System.Collections.Generic.List<Aabb> _boxes;

		// Obstacles qui apparaissent en cours de partie : les coffres (pleins : on se cache derrière, les balles s'y arrêtent). Ils dépendent du tirage
		// de la partie (points « au hasard »), donc ils ne sont pas dans le fichier de carte : le serveur les pose au tirage, le client à la réception du coffre.
		private System.Collections.Generic.List<Aabb> _dyn = new System.Collections.Generic.List<Aabb>();
		private int[][] _dynGrid;

		public StaticWorld(WorldData data)
		{
			Data = data;
			_boxes = new System.Collections.Generic.List<Aabb>(data.Boxes);
			_gridW = Math.Max(1, (int)Math.Ceiling((data.MaxX - data.MinX) / GridCell) + 1);
			_gridD = Math.Max(1, (int)Math.Ceiling((data.MaxZ - data.MinZ) / GridCell) + 1);
			var lists = new System.Collections.Generic.List<int>[_gridW * _gridD];
			for (int i = 0; i < _boxes.Count; i++)
			{
				var b = _boxes[i];
				int x0 = CellX(b.Min.X - 1f), x1 = CellX(b.Max.X + 1f), z0 = CellZ(b.Min.Z - 1f), z1 = CellZ(b.Max.Z + 1f);
				for (int cz = z0; cz <= z1; cz++)
					for (int cx = x0; cx <= x1; cx++)
						(lists[cz * _gridW + cx] ??= new System.Collections.Generic.List<int>()).Add(i);
			}
			_grid = new int[lists.Length][];
			for (int i = 0; i < lists.Length; i++) _grid[i] = lists[i] == null ? None : lists[i].ToArray();
		}

		private int CellX(float x) { int c = (int)Math.Floor((x - Data.MinX) / GridCell); return c < 0 ? 0 : (c >= _gridW ? _gridW - 1 : c); }
		private int CellZ(float z) { int c = (int)Math.Floor((z - Data.MinZ) / GridCell); return c < 0 ? 0 : (c >= _gridD ? _gridD - 1 : c); }
		private int[] Near(float x, float z) => _grid[CellZ(z) * _gridW + CellX(x)];
		private int[] NearDyn(float x, float z) => _dynGrid == null ? None : _dynGrid[CellZ(z) * _gridW + CellX(x)];

		/// <summary>Remplace les obstacles de la partie (coffres posés). Appelé quand l'ensemble change, pas à chaque tick.</summary>
		public void SetDynamicBoxes(System.Collections.Generic.IEnumerable<Aabb> boxes)
		{
			var list = new System.Collections.Generic.List<Aabb>(boxes);
			if (list.Count == 0 && _dyn.Count == 0) return;
			_dyn = list;
			if (list.Count == 0) { _dynGrid = null; return; }
			var lists = new System.Collections.Generic.List<int>[_gridW * _gridD];
			for (int i = 0; i < list.Count; i++)
			{
				var b = list[i];
				int x0 = CellX(b.Min.X - 1f), x1 = CellX(b.Max.X + 1f), z0 = CellZ(b.Min.Z - 1f), z1 = CellZ(b.Max.Z + 1f);
				for (int cz = z0; cz <= z1; cz++)
					for (int cx = x0; cx <= x1; cx++)
						(lists[cz * _gridW + cx] ??= new System.Collections.Generic.List<int>()).Add(i);
			}
			var grid = new int[lists.Length][];
			for (int i = 0; i < lists.Length; i++) grid[i] = lists[i] == null ? None : lists[i].ToArray();
			_dynGrid = grid;
		}

		public float HeightAt(float x, float z) => Data.HeightAt(x, z);

		public bool ZipPointAt(int line, float dist, out Vec3 pos, out Vec3 tangent, out float length)
		{
			pos = Vec3.Zero; tangent = new Vec3(0, 0, -1); length = 0f;
			if (line < 0 || line >= Data.Ziplines.Count) return false;
			var z = Data.Ziplines[line];
			z.PointAt(dist, out pos, out tangent);
			length = z.Length;
			return true;
		}

		public bool FindZipline(Vec3 hand, float horizontalTol, float verticalTol, int onlyLine, out int line, out float dist)
		{
			line = -1; dist = 0f;
			float best = float.MaxValue;
			for (int li = 0; li < Data.Ziplines.Count; li++)
			{
				if (onlyLine >= 0 && li != onlyLine) continue;
				var z = Data.Ziplines[li];
				for (int i = 1; i < z.Points.Length; i++)
				{
					var a = z.Points[i - 1]; var b = z.Points[i];
					// pas de test si le segment est loin
					if (hand.X < MathF.Min(a.X, b.X) - horizontalTol || hand.X > MathF.Max(a.X, b.X) + horizontalTol
						|| hand.Z < MathF.Min(a.Z, b.Z) - horizontalTol || hand.Z > MathF.Max(a.Z, b.Z) + horizontalTol) continue;
					float dx = b.X - a.X, dz = b.Z - a.Z;
					float l2 = dx * dx + dz * dz;
					float t = l2 < 1e-6f ? 0f : Math.Clamp(((hand.X - a.X) * dx + (hand.Z - a.Z) * dz) / l2, 0f, 1f);
					float cx = a.X + dx * t, cz = a.Z + dz * t, cy = a.Y + (b.Y - a.Y) * t;
					float hd = MathF.Sqrt((hand.X - cx) * (hand.X - cx) + (hand.Z - cz) * (hand.Z - cz));
					float vd = MathF.Abs(hand.Y - cy);
					if (hd > horizontalTol || vd > verticalTol) continue;
					float score = hd + vd;
					if (score < best) { best = score; line = li; dist = z.Cumulative[i - 1] + (z.Cumulative[i] - z.Cumulative[i - 1]) * t; }
				}
			}
			return line >= 0;
		}

		public bool PadAt(Vec3 feet, out LaunchPad pad)
		{
			var pads = Data.Pads;
			for (int i = 0; i < pads.Count; i++)
			{
				float dx = feet.X - pads[i].Pos.X, dz = feet.Z - pads[i].Pos.Z;
				if (dx * dx + dz * dz < 1.5f * 1.5f && MathF.Abs(feet.Y - pads[i].Pos.Y) < 1.0f) { pad = pads[i]; return true; }
			}
			pad = default;
			return false;
		}

		/// <summary>Rayon de la gaine de verre autour du câble (les balles ne la traversent pas).</summary>
		public const float TunnelRadius = 2.5f;

		/// <summary>Premier impact d'un rayon sur la gaine de verre des tyroliennes (cylindre autour de chaque tronçon de câble, ouvert par le bas).</summary>
		private bool RayTunnel(Vec3 o, Vec3 d, float maxDist, out float tHit)
		{
			tHit = maxDist;
			bool hit = false;
			float R = TunnelRadius;
			foreach (var z in Data.Ziplines)
				for (int i = 1; i < z.Points.Length; i++)
				{
					var a = z.Points[i - 1]; var b = z.Points[i];
					var ab = b - a;
					float len = ab.Length();
					if (len < 1e-4f) continue;
					var u = ab / len;
					// éloignement rapide : sphère englobante du tronçon
					var mid = new Vec3((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f);
					var om = mid - o;
					float proj = om.Dot(d);
					float perp2 = om.LengthSquared() - proj * proj;
					float sr = len * 0.5f + R;
					if (perp2 > sr * sr || proj < -sr || proj > maxDist + sr) continue;
					// intersection avec le cylindre infini d'axe (a, u), rayon R
					var oa = o - a;
					float dDotU = d.Dot(u), oDotU = oa.Dot(u);
					var dPerp = d - u * dDotU;
					var oPerp = oa - u * oDotU;
					float qa = dPerp.Dot(dPerp), qb = 2f * dPerp.Dot(oPerp), qc = oPerp.Dot(oPerp) - R * R;
					if (qa < 1e-8f) continue;
					float disc = qb * qb - 4f * qa * qc;
					if (disc < 0f) continue;
					float sq = MathF.Sqrt(disc);
					for (int k = 0; k < 2; k++)
					{
						float t = (-qb + (k == 0 ? -sq : sq)) / (2f * qa);
						if (t < 0f || t >= tHit) continue;
						var p = o + d * t;
						float along = (p - a).Dot(u);
						if (along < 0f || along > len) continue;
						var radial = p - (a + u * along);
						if (radial.Y < -R * 0.5f) continue;     // le dessous du tunnel est ouvert
						tHit = t; hit = true;
						break;
					}
				}
			return hit;
		}

		public float GroundAt(float x, float z, float feetY, float stepHeight)
		{
			float ground = Data.HeightAt(x, z);
			var near = Near(x, z);
			var boxes = _boxes;
			for (int k = 0; k < near.Length; k++)
			{
				var b = boxes[near[k]];
				if (x < b.Min.X || x > b.Max.X || z < b.Min.Z || z > b.Max.Z) continue;
				if (b.Max.Y > ground && b.Max.Y <= feetY + stepHeight) ground = b.Max.Y;
			}
			var dn = NearDyn(x, z);
			for (int k = 0; k < dn.Length; k++)
			{
				var b = _dyn[dn[k]];
				if (x < b.Min.X || x > b.Max.X || z < b.Min.Z || z > b.Max.Z) continue;
				if (b.Max.Y > ground && b.Max.Y <= feetY + stepHeight) ground = b.Max.Y;
			}
			return ground;
		}

		public Vec3 NormalAt(float x, float z)
		{
			float e = Data.CellSize;
			float dx = Data.HeightAt(x + e, z) - Data.HeightAt(x - e, z);
			float dz = Data.HeightAt(x, z + e) - Data.HeightAt(x, z - e);
			return new Vec3(-dx, 2f * e, -dz).Normalized();
		}

		public void ClampToBounds(ref Vec3 pos)
		{
			if (pos.X < Data.MinX) pos.X = Data.MinX; else if (pos.X > Data.MaxX) pos.X = Data.MaxX;
			if (pos.Z < Data.MinZ) pos.Z = Data.MinZ; else if (pos.Z > Data.MaxZ) pos.Z = Data.MaxZ;
		}

		public Vec3 ResolveObstacles(Vec3 pos, float radius, float height, float stepHeight)
		{
			var boxes = _boxes;
			var near = Near(pos.X, pos.Z);
			var dn = NearDyn(pos.X, pos.Z);
			for (int pass = 0; pass < 2; pass++)
			{
				for (int k = 0; k < near.Length; k++) PushOut(ref pos, boxes[near[k]], radius, height, stepHeight);
				for (int k = 0; k < dn.Length; k++) PushOut(ref pos, _dyn[dn[k]], radius, height, stepHeight);
			}
			return pos;
		}

		private static void PushOut(ref Vec3 pos, Aabb b, float radius, float height, float stepHeight)
		{
			// obstacle seulement s'il recouvre la hauteur du joueur au-dessus de ses pieds
			if (b.Max.Y <= pos.Y + stepHeight || b.Min.Y >= pos.Y + height) return;
			if (pos.X < b.Min.X - radius || pos.X > b.Max.X + radius || pos.Z < b.Min.Z - radius || pos.Z > b.Max.Z + radius) return;

			float cx = pos.X < b.Min.X ? b.Min.X : (pos.X > b.Max.X ? b.Max.X : pos.X);
			float cz = pos.Z < b.Min.Z ? b.Min.Z : (pos.Z > b.Max.Z ? b.Max.Z : pos.Z);
			float dx = pos.X - cx, dz = pos.Z - cz;
			float d2 = dx * dx + dz * dz;
			if (d2 >= radius * radius) return;

			if (d2 > 1e-8f)
			{
				float d = MathF.Sqrt(d2), push = radius - d;
				pos.X += dx / d * push;
				pos.Z += dz / d * push;
			}
			else
			{
				// centre dans la boîte : on sort par le côté le plus proche
				float l = pos.X - b.Min.X, r = b.Max.X - pos.X, n = pos.Z - b.Min.Z, f = b.Max.Z - pos.Z;
				float m = MathF.Min(MathF.Min(l, r), MathF.Min(n, f));
				if (m == l) pos.X = b.Min.X - radius;
				else if (m == r) pos.X = b.Max.X + radius;
				else if (m == n) pos.Z = b.Min.Z - radius;
				else pos.Z = b.Max.Z + radius;
			}
		}

		public bool Raycast(Vec3 origin, Vec3 dir, float maxDist, out float dist)
		{
			dist = maxDist;
			bool hit = false;

			// obstacles : méthode des plans (slab)
			var boxes = _boxes;
			// on ne teste que les boîtes des cases de la grille traversées par le rayon (parcours « DDA » dans le plan XZ)
			{
				int cx = CellX(origin.X), cz = CellZ(origin.Z);
				float fx = (origin.X - Data.MinX) / GridCell, fz = (origin.Z - Data.MinZ) / GridCell;
				int stepX = dir.X > 0 ? 1 : -1, stepZ = dir.Z > 0 ? 1 : -1;
				float tDeltaX = MathF.Abs(dir.X) < 1e-6f ? float.MaxValue : GridCell / MathF.Abs(dir.X);
				float tDeltaZ = MathF.Abs(dir.Z) < 1e-6f ? float.MaxValue : GridCell / MathF.Abs(dir.Z);
				float tMaxX = MathF.Abs(dir.X) < 1e-6f ? float.MaxValue : ((dir.X > 0 ? MathF.Floor(fx) + 1 - fx : fx - MathF.Floor(fx)) * GridCell) / MathF.Abs(dir.X);
				float tMaxZ = MathF.Abs(dir.Z) < 1e-6f ? float.MaxValue : ((dir.Z > 0 ? MathF.Floor(fz) + 1 - fz : fz - MathF.Floor(fz)) * GridCell) / MathF.Abs(dir.Z);
				for (int guard = 0; guard < 4096; guard++)
				{
					var cell = _grid[cz * _gridW + cx];
					for (int k = 0; k < cell.Length; k++)
						if (RayAabb(origin, dir, boxes[cell[k]], out float t) && t >= 0 && t < dist)
						{
							dist = t;
							hit = true;
						}
					if (_dynGrid != null)
					{
						var dcell = _dynGrid[cz * _gridW + cx];
						for (int k = 0; k < dcell.Length; k++)
							if (RayAabb(origin, dir, _dyn[dcell[k]], out float t) && t >= 0 && t < dist)
							{
								dist = t;
								hit = true;
							}
					}
					float tNext = MathF.Min(tMaxX, tMaxZ);
					if (tNext > dist || tNext > maxDist) break;
					if (tMaxX < tMaxZ) { cx += stepX; tMaxX += tDeltaX; } else { cz += stepZ; tMaxZ += tDeltaZ; }
					if (cx < 0 || cx >= _gridW || cz < 0 || cz >= _gridD) break;
				}
			}

			// gaines de verre des tyroliennes
			if (Data.Ziplines.Count > 0 && RayTunnel(origin, dir, dist, out float tTunnel)) { dist = tTunnel; hit = true; }

			// relief : marche le long du rayon, puis affinage par dichotomie
			float step = MathF.Max(Data.CellSize * 0.5f, 0.5f);
			float prevT = 0f;
			bool prevAbove = origin.Y >= Data.HeightAt(origin.X, origin.Z);
			for (float t = step; t <= dist + step; t += step)
			{
				float tt = MathF.Min(t, dist);
				float px = origin.X + dir.X * tt, py = origin.Y + dir.Y * tt, pz = origin.Z + dir.Z * tt;
				bool above = py >= Data.HeightAt(px, pz);
				if (prevAbove && !above)
				{
					float lo = prevT, hi = tt;
					for (int k = 0; k < 8; k++)
					{
						float mid = (lo + hi) * 0.5f;
						if (origin.Y + dir.Y * mid >= Data.HeightAt(origin.X + dir.X * mid, origin.Z + dir.Z * mid)) lo = mid; else hi = mid;
					}
					if (hi < dist) { dist = hi; hit = true; }
					break;
				}
				prevAbove = above;
				prevT = tt;
				if (tt >= dist) break;
			}
			return hit;
		}

		private static bool RayAabb(Vec3 o, Vec3 d, Aabb b, out float tHit)
		{
			float tMin = 0f, tMax = float.MaxValue;
			tHit = 0;
			for (int axis = 0; axis < 3; axis++)
			{
				float oa = axis == 0 ? o.X : (axis == 1 ? o.Y : o.Z);
				float da = axis == 0 ? d.X : (axis == 1 ? d.Y : d.Z);
				float lo = axis == 0 ? b.Min.X : (axis == 1 ? b.Min.Y : b.Min.Z);
				float hi = axis == 0 ? b.Max.X : (axis == 1 ? b.Max.Y : b.Max.Z);
				if (MathF.Abs(da) < 1e-8f)
				{
					if (oa < lo || oa > hi) return false;
				}
				else
				{
					float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
					if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
					if (t1 > tMin) tMin = t1;
					if (t2 < tMax) tMax = t2;
					if (tMin > tMax) return false;
				}
			}
			tHit = tMin;
			return true;
		}
	}
}
