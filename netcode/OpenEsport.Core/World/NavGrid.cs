using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>
	/// Grille de navigation pour les bots, construite à partir de la carte : une case de 1 m est « praticable » si la pente est
	/// franchissable et si aucun obstacle (mur, rocher, tronc, pilier…) ne bloque le corps d'un joueur à cet endroit. Sert à chercher
	/// des chemins (A*) qui contournent les obstacles et passent par les portes. Elle ne dépend d'aucun moteur : le serveur dédié l'utilise.
	/// </summary>
	public sealed class NavGrid : INav
	{
		public bool IsPortal(Vec3 waypoint) => false;

		public const float Cell = 1f;
		/// <summary>Marge (m) autour des obstacles : le rayon d'un joueur (0,4 m) : une porte de 2 m reste franchissable.</summary>
		private const float Margin = 0.4f;
		private const float MaxGradient = 0.9f;
		private const float StepUp = 0.55f, BodyHeight = 1.75f;

		private readonly WorldData _d;
		public readonly int Width, Depth;
		private readonly float _x0, _z0;
		private readonly byte[] _blocked;

		// tampons de la recherche, réutilisés d'un appel à l'autre (pas d'allocation à chaque chemin)
		private readonly float[] _g;
		private readonly int[] _stamp, _parent;
		private int _run;
		private readonly PriorityQueue<int, float> _open = new PriorityQueue<int, float>();
		private readonly List<int> _cells = new List<int>(512);

		public NavGrid(WorldData data)
		{
			_d = data;
			_x0 = data.MinX; _z0 = data.MinZ;
			Width = (int)Math.Ceiling((data.MaxX - data.MinX) / Cell);
			Depth = (int)Math.Ceiling((data.MaxZ - data.MinZ) / Cell);
			_blocked = new byte[Width * Depth];
			_g = new float[_blocked.Length];
			_stamp = new int[_blocked.Length];
			_parent = new int[_blocked.Length];

			// pente
			for (int cz = 0; cz < Depth; cz++)
				for (int cx = 0; cx < Width; cx++)
				{
					float x = _x0 + (cx + 0.5f) * Cell, z = _z0 + (cz + 0.5f) * Cell;
					float gx = (data.HeightAt(x + 1f, z) - data.HeightAt(x - 1f, z)) * 0.5f;
					float gz = (data.HeightAt(x, z + 1f) - data.HeightAt(x, z - 1f)) * 0.5f;
					if (gx * gx + gz * gz > MaxGradient * MaxGradient) _blocked[cz * Width + cx] = 1;
				}

			// obstacles : une boîte bloque une case si elle est plus haute qu'une marche et recouvre la hauteur du corps
			var obstacles = new List<Aabb>(data.Boxes);
			for (int li = 0; li < data.LootSpawns.Count; li++) if (data.LootSpawns[li].IsChest) obstacles.Add(WorldData.ChestBox(data.LootSpawns[li].Pos, data.LootSpawns[li].Yaw));      // les coffres sont des obstacles
			foreach (var b in obstacles)
			{
				int ix0 = Math.Max(0, (int)Math.Floor((b.Min.X - Margin - _x0) / Cell)), ix1 = Math.Min(Width - 1, (int)Math.Floor((b.Max.X + Margin - _x0) / Cell));
				int iz0 = Math.Max(0, (int)Math.Floor((b.Min.Z - Margin - _z0) / Cell)), iz1 = Math.Min(Depth - 1, (int)Math.Floor((b.Max.Z + Margin - _z0) / Cell));
				for (int cz = iz0; cz <= iz1; cz++)
					for (int cx = ix0; cx <= ix1; cx++)
					{
						float x = _x0 + (cx + 0.5f) * Cell, z = _z0 + (cz + 0.5f) * Cell;
						if (x < b.Min.X - Margin || x > b.Max.X + Margin || z < b.Min.Z - Margin || z > b.Max.Z + Margin) continue;
						float ground = data.HeightAt(x, z);
						if (b.Max.Y > ground + StepUp && b.Min.Y < ground + BodyHeight) _blocked[cz * Width + cx] = 1;
					}
			}
		}

		private bool InGrid(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Width && cz < Depth;
		private int CellX(float x) => (int)Math.Floor((x - _x0) / Cell);
		private int CellZ(float z) => (int)Math.Floor((z - _z0) / Cell);

		public bool Walkable(float x, float z)
		{
			int cx = CellX(x), cz = CellZ(z);
			return InGrid(cx, cz) && _blocked[cz * Width + cx] == 0;
		}

		/// <summary>Vrai si on peut aller en ligne droite de a à b sans traverser une case bloquée.</summary>
		public bool LineClear(Vec3 a, Vec3 b)
		{
			float dx = b.X - a.X, dz = b.Z - a.Z;
			float len = MathF.Sqrt(dx * dx + dz * dz);
			int n = Math.Max(1, (int)(len / 0.25f));
			for (int i = 1; i <= n; i++)
			{
				float t = i / (float)n;
				if (!Walkable(a.X + dx * t, a.Z + dz * t)) return false;
			}
			return true;
		}

		/// <summary>Case praticable la plus proche (jusqu'à 10 m) d'une position, ou -1.</summary>
		private int Snap(float x, float z)
		{
			int cx = CellX(x), cz = CellZ(z);
			if (InGrid(cx, cz) && _blocked[cz * Width + cx] == 0) return cz * Width + cx;
			for (int r = 1; r <= 10; r++)
			{
				int best = -1; float bd = float.MaxValue;
				for (int dz = -r; dz <= r; dz++)
					for (int dx = -r; dx <= r; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
						int nx = cx + dx, nz = cz + dz;
						if (!InGrid(nx, nz) || _blocked[nz * Width + nx] != 0) continue;
						float d = dx * dx + dz * dz;
						if (d < bd) { bd = d; best = nz * Width + nx; }
					}
				if (best >= 0) return best;
			}
			return -1;
		}

		private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
		private static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

		/// <summary>
		/// Cherche un chemin de <paramref name="from"/> à <paramref name="to"/> et écrit dans <paramref name="path"/> les points à suivre
		/// (chemin tiré au cordeau : seulement les virages). Retourne faux si aucun chemin n'est trouvé dans la limite de recherche.
		/// </summary>
		public bool FindPath(Vec3 from, Vec3 to, List<Vec3> path, int maxExpansions = 40000)
		{
			path.Clear();
			int start = Snap(from.X, from.Z), goal = Snap(to.X, to.Z);
			if (start < 0 || goal < 0) return false;
			int gx = goal % Width, gz = goal / Width;

			_run++;
			_open.Clear();
			_g[start] = 0f; _stamp[start] = _run; _parent[start] = -1;
			_open.Enqueue(start, 0f);
			int expanded = 0;
			bool found = false;
			while (_open.TryDequeue(out int cur, out _))
			{
				if (cur == goal) { found = true; break; }
				if (++expanded > maxExpansions) break;
				int cx = cur % Width, cz = cur / Width;
				float gcur = _g[cur];
				for (int k = 0; k < 8; k++)
				{
					int nx = cx + DX[k], nz = cz + DZ[k];
					if (!InGrid(nx, nz)) continue;
					int ni = nz * Width + nx;
					if (_blocked[ni] != 0) continue;
					if (k >= 4 && (_blocked[cz * Width + nx] != 0 || _blocked[nz * Width + cx] != 0)) continue;    // pas de passage en biais entre deux obstacles
					float step = k < 4 ? 1f : 1.4142f;
					float ng = gcur + step;
					if (_stamp[ni] == _run && ng >= _g[ni]) continue;
					_stamp[ni] = _run; _g[ni] = ng; _parent[ni] = cur;
					float hx = Math.Abs(nx - gx), hz = Math.Abs(nz - gz);
					float h = (hx + hz) + (1.4142f - 2f) * Math.Min(hx, hz);      // distance « octile »
					_open.Enqueue(ni, ng + h * 1.4f);
				}
			}
			if (!found) return false;

			_cells.Clear();
			for (int c = goal; c >= 0; c = _parent[c]) _cells.Add(c);
			_cells.Reverse();
			// tirer au cordeau : on garde le point le plus lointain encore visible en ligne droite
			var pts = new List<Vec3>(_cells.Count);
			foreach (int c in _cells)
			{
				float x = _x0 + (c % Width + 0.5f) * Cell, z = _z0 + (c / Width + 0.5f) * Cell;
				pts.Add(new Vec3(x, 0f, z));
			}
			int i = 0;
			while (i < pts.Count - 1)
			{
				int j = pts.Count - 1;
				while (j > i + 1 && !LineClear(pts[i], pts[j])) j--;
				path.Add(pts[j]);
				i = j;
			}
			if (path.Count > 0) path[path.Count - 1] = new Vec3(to.X, 0f, to.Z);
			return true;
		}
	}
}
