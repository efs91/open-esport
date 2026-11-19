using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>Navigation des bots : chemin à suivre et ligne droite praticable.</summary>
	public interface INav
	{
		bool LineClear(Vec3 a, Vec3 b);
		bool FindPath(Vec3 from, Vec3 to, List<Vec3> path, int maxExpansions = 40000);
		/// <summary>Ce point du chemin est-il l'entrée d'un tremplin ou d'un téléporteur ? Il faut alors marcher dessus avant de passer au suivant.</summary>
		bool IsPortal(Vec3 waypoint);
	}

	public static class Navigation
	{
		/// <summary>La navigation adaptée à la carte : sur plusieurs niveaux pour une arène (tremplins, téléporteurs), sinon la grille au sol.</summary>
		public static INav For(WorldData data)
		{
			bool elevatedObjective = data.Points.Exists(p => p.Kind == MapPointKind.FlagBase && p.Pos.Y > data.HeightAt(p.Pos.X, p.Pos.Z) + 0.55f);
			return data.Jumps.Count > 0 || data.Teleports.Count > 0 || elevatedObjective ? new LayeredNav(data) : new NavGrid(data);
		}
	}

	/// <summary>
	/// Navigation sur plusieurs niveaux (arènes) : chaque case de 1 m peut porter plusieurs sols (relief, dalle d'étage, marche, passerelle).
	/// Deux sols voisins sont reliés si l'écart de hauteur se franchit d'un pas ; on peut aussi sauter d'un rebord (jusqu'à 4,2 m plus bas),
	/// prendre un tremplin ou un téléporteur. A* sur ces nœuds, puis chemin tiré au cordeau (sans sauter les tremplins et téléporteurs).
	/// </summary>
	public sealed class LayeredNav : INav
	{
		private const float Cell = 1f, Margin = 0.4f, StepUp = 0.55f, BodyHeight = 1.75f, MaxDrop = 4.2f;
		private readonly float _x0, _z0;
		private readonly int _w, _d;
		private readonly int[] _cellStart, _cellCount;
		private readonly List<float> _y = new List<float>();
		private readonly List<int> _cellOf = new List<int>();
		private readonly Dictionary<int, List<(int to, float cost)>> _portals = new Dictionary<int, List<(int, float)>>();
		private readonly List<Vec3> _portalPoints = new List<Vec3>();

		private float[] _g;
		private int[] _stamp, _parent;
		private int _run;
		private readonly PriorityQueue<int, float> _open = new PriorityQueue<int, float>();

		public int NodeCount => _y.Count;

		public LayeredNav(WorldData data)
		{
			_x0 = data.MinX; _z0 = data.MinZ;
			_w = (int)Math.Ceiling((data.MaxX - data.MinX) / Cell);
			_d = (int)Math.Ceiling((data.MaxZ - data.MinZ) / Cell);
			_cellStart = new int[_w * _d];
			_cellCount = new int[_w * _d];

			// boîtes par case (avec la marge du corps), pour ne pas tout parcourir à chaque case
			var near = new List<int>[_w * _d];
			for (int i = 0; i < data.Boxes.Count; i++)
			{
				var b = data.Boxes[i];
				int ix0 = Math.Max(0, CellX(b.Min.X - Margin)), ix1 = Math.Min(_w - 1, CellX(b.Max.X + Margin));
				int iz0 = Math.Max(0, CellZ(b.Min.Z - Margin)), iz1 = Math.Min(_d - 1, CellZ(b.Max.Z + Margin));
				for (int cz = iz0; cz <= iz1; cz++)
					for (int cx = ix0; cx <= ix1; cx++) (near[cz * _w + cx] ??= new List<int>()).Add(i);
			}

			var cand = new List<float>();
			for (int cz = 0; cz < _d; cz++)
				for (int cx = 0; cx < _w; cx++)
				{
					int c = cz * _w + cx;
					float x = _x0 + (cx + 0.5f) * Cell, z = _z0 + (cz + 0.5f) * Cell;
					cand.Clear();
					cand.Add(data.HeightAt(x, z));
					var list = near[c];
					if (list != null)
						foreach (int bi in list)
						{
							var b = data.Boxes[bi];
							if (x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z) cand.Add(b.Max.Y);
						}
					cand.Sort();
					_cellStart[c] = _y.Count;
					float last = float.MinValue;
					foreach (float y in cand)
					{
						if (y - last < 0.05f) continue;
						if (!Standable(data, list, x, z, y)) continue;
						last = y;
						_y.Add(y); _cellOf.Add(c);
					}
					_cellCount[c] = _y.Count - _cellStart[c];
				}

			// tremplins et téléporteurs : arêtes à sens unique
			foreach (var j in data.Jumps) AddPortal(j.Pos, j.Target, Vec3.Distance(j.Pos, j.Target) * 0.5f);
			foreach (var t in data.Teleports) AddPortal(t.Pos, t.Dest, 3f);

			_g = new float[_y.Count]; _stamp = new int[_y.Count]; _parent = new int[_y.Count];
		}

		private static bool Standable(WorldData data, List<int> list, float x, float z, float y)
		{
			if (list == null) return true;
			foreach (int bi in list)
			{
				var b = data.Boxes[bi];
				bool inside = x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z;
				// enterré dans une boîte (sol sous un mur)
				if (inside && b.Min.Y <= y + 0.001f && b.Max.Y > y + 0.05f) return false;
				// le corps (au-dessus d'un pas) heurte une boîte, marge comprise
				if (x < b.Min.X - Margin || x > b.Max.X + Margin || z < b.Min.Z - Margin || z > b.Max.Z + Margin) continue;
				if (b.Max.Y > y + StepUp && b.Min.Y < y + BodyHeight) return false;
			}
			return true;
		}

		private void AddPortal(Vec3 from, Vec3 to, float cost)
		{
			int a = Snap(from, 2), b = Snap(to, 3);
			if (a < 0 || b < 0) return;
			if (!_portals.TryGetValue(a, out var l)) _portals[a] = l = new List<(int, float)>();
			l.Add((b, cost));
			_portalPoints.Add(PosOf(a));
		}

		private int CellX(float x) => (int)Math.Floor((x - _x0) / Cell);
		private int CellZ(float z) => (int)Math.Floor((z - _z0) / Cell);
		private Vec3 PosOf(int n) { int c = _cellOf[n]; return new Vec3(_x0 + (c % _w + 0.5f) * Cell, _y[n], _z0 + (c / _w + 0.5f) * Cell); }

		/// <summary>Nœud de la case (x, z) au sol le plus proche sous les pieds (une marche au-dessus au plus), ou −1.</summary>
		private int NodeAt(float x, float z, float feetY)
		{
			int cx = CellX(x), cz = CellZ(z);
			if (cx < 0 || cz < 0 || cx >= _w || cz >= _d) return -1;
			int c = cz * _w + cx, best = -1;
			float bd = float.MaxValue;
			for (int k = 0; k < _cellCount[c]; k++)
			{
				int n = _cellStart[c] + k;
				float dy = feetY - _y[n];
				if (dy < -StepUp) continue;
				if (dy < bd) { bd = dy; best = n; }
			}
			return best;
		}

		/// <summary>Le nœud le plus proche d'une position (même niveau de préférence), jusqu'à <paramref name="radius"/> cases.</summary>
		private int Snap(Vec3 p, int radius = 6)
		{
			int cx = CellX(p.X), cz = CellZ(p.Z);
			int best = -1; float bd = float.MaxValue;
			for (int r = 0; r <= radius; r++)
			{
				for (int dz = -r; dz <= r; dz++)
					for (int dx = -r; dx <= r; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
						int nx = cx + dx, nz = cz + dz;
						if (nx < 0 || nz < 0 || nx >= _w || nz >= _d) continue;
						int c = nz * _w + nx;
						for (int k = 0; k < _cellCount[c]; k++)
						{
							int n = _cellStart[c] + k;
							float dy = _y[n] - p.Y;
							float score = dx * dx + dz * dz + (dy > 0.6f ? dy * dy * 6f : dy * dy * 2f);
							if (score < bd) { bd = score; best = n; }
						}
					}
				if (best >= 0 && r >= 1) break;
			}
			return best;
		}

		public bool IsPortal(Vec3 w)
		{
			foreach (var p in _portalPoints) if (MathF.Abs(p.X - w.X) < 0.01f && MathF.Abs(p.Z - w.Z) < 0.01f && MathF.Abs(p.Y - w.Y) < 0.6f) return true;
			return false;
		}

		/// <summary>Ligne droite praticable de a à b : chaque pas reste sur un sol que l'on atteint d'une marche (pas de chute).</summary>
		public bool LineClear(Vec3 a, Vec3 b)
		{
			int n0 = NodeAt(a.X, a.Z, a.Y + 0.3f);
			if (n0 < 0) return false;
			float y = _y[n0];
			float dx = b.X - a.X, dz = b.Z - a.Z;
			float len = MathF.Sqrt(dx * dx + dz * dz);
			int steps = Math.Max(1, (int)(len / 0.25f));
			for (int i = 1; i <= steps; i++)
			{
				float t = i / (float)steps;
				int n = NodeAt(a.X + dx * t, a.Z + dz * t, y + StepUp);
				if (n < 0 || y - _y[n] > StepUp) return false;
				y = _y[n];
			}
			return MathF.Abs(y - b.Y) < 1.2f;
		}

		private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
		private static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

		/// <summary>Nœud de la case voisine relié à <paramref name="n"/> (montée d'une marche, ou descente jusqu'à <see cref="MaxDrop"/>), ou −1.</summary>
		private int Neighbour(int n, int nx, int nz, out bool drop)
		{
			drop = false;
			if (nx < 0 || nz < 0 || nx >= _w || nz >= _d) return -1;
			int c = nz * _w + nx;
			float y = _y[n];
			int best = -1; float bd = float.MaxValue;
			for (int k = 0; k < _cellCount[c]; k++)
			{
				int m = _cellStart[c] + k;
				float dy = _y[m] - y;
				if (dy > StepUp || dy < -MaxDrop) continue;
				float s = MathF.Abs(dy);
				if (s < bd) { bd = s; best = m; }
			}
			if (best >= 0) drop = _y[best] < y - StepUp;
			return best;
		}

		public bool FindPath(Vec3 from, Vec3 to, List<Vec3> path, int maxExpansions = 40000)
		{
			path.Clear();
			int start = Snap(from), goal = Snap(to);
			if (start < 0 || goal < 0) return false;
			var gp = PosOf(goal);
			_run++;
			_open.Clear();
			_g[start] = 0f; _stamp[start] = _run; _parent[start] = -1;
			_open.Enqueue(start, 0f);
			int expanded = 0;
			bool found = false;
			void Relax(int cur, int m, float cost)
			{
				float ng = _g[cur] + cost;
				if (_stamp[m] == _run && ng >= _g[m]) return;
				_stamp[m] = _run; _g[m] = ng; _parent[m] = cur;
				_open.Enqueue(m, ng + Vec3.Distance(PosOf(m), gp) * 1.2f);
			}
			while (_open.TryDequeue(out int cur, out _))
			{
				if (cur == goal) { found = true; break; }
				if (++expanded > maxExpansions) break;
				int c = _cellOf[cur], cx = c % _w, cz = c / _w;
				for (int k = 0; k < 8; k++)
				{
					int m = Neighbour(cur, cx + DX[k], cz + DZ[k], out bool drop);
					if (m < 0) continue;
					if (k >= 4 && (Neighbour(cur, cx + DX[k], cz, out _) < 0 || Neighbour(cur, cx, cz + DZ[k], out _) < 0)) continue;
					Relax(cur, m, (k < 4 ? 1f : 1.4142f) + (drop ? 2f : 0f) + MathF.Max(0f, _y[m] - _y[cur]));
				}
				if (_portals.TryGetValue(cur, out var outs)) foreach (var (to2, cost) in outs) Relax(cur, to2, cost);
			}
			if (!found) return false;

			var nodes = new List<int>();
			for (int c = goal; c >= 0; c = _parent[c]) nodes.Add(c);
			nodes.Reverse();
			var pts = new List<Vec3>(nodes.Count);
			var portal = new List<bool>(nodes.Count);
			for (int i = 0; i < nodes.Count; i++)
			{
				pts.Add(PosOf(nodes[i]));
				// un nœud suivi d'un saut de tremplin ou de téléporteur (pas une case voisine) est un passage obligé
				bool jump = i + 1 < nodes.Count && _portals.TryGetValue(nodes[i], out var o) && o.Exists(e => e.to == nodes[i + 1]);
				portal.Add(jump);
			}
			int a = 0;
			while (a < pts.Count - 1)
			{
				int j = a + 1;
				// on va au plus loin en ligne droite, sans dépasser un passage obligé (ni partir du point d'arrivée d'un saut sans s'y être posé)
				while (j + 1 < pts.Count && !portal[j] && !portal[a] && LineClear(pts[a], pts[j + 1])) j++;
				path.Add(pts[j]);
				a = j;
			}
			if (path.Count > 0) path[path.Count - 1] = new Vec3(to.X, path[path.Count - 1].Y, to.Z);
			return true;
		}
	}
}
