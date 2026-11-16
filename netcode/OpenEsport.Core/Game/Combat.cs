using System;

namespace OpenEsport.Core
{
	public enum HitZone : byte { Legs = 0, Torso = 1, Head = 2 }

	/// <summary>
	/// Zones de touche d'un joueur : un cylindre vertical dont la hauteur dépend de la posture, découpé en jambes, torse et tête.
	/// Aucun squelette n'est nécessaire sur le serveur, ce qui rend 100 joueurs très peu coûteux.
	/// </summary>
	public static class Hitboxes
	{
		public const float Radius = 0.4f;

		public static float Multiplier(HitZone z) => z == HitZone.Head ? GameBalance.HeadMultiplier : (z == HitZone.Torso ? GameBalance.TorsoMultiplier : GameBalance.LimbMultiplier);

		// Corps en trois volumes (mesures pour un joueur debout de 1,8 m, mises à l'échelle de la posture) : jambes, torse (bras compris) et tête.
		// Un seul gros cylindre touchait dans le vide à côté des jambes et comptait un cou et des épaules entiers comme « tête ».
		public const float LegsTop = 0.90f, LegsRadius = 0.30f;
		public const float TorsoTop = 1.46f, TorsoRadius = 0.36f;
		public const float HeadCenter = 1.64f, HeadRadius = 0.27f;
		public const float DownedTop = 0.55f, DownedRadius = 0.85f;

		public static bool RayVsPlayer(Vec3 feet, Stance stance, Vec3 origin, Vec3 dir, float maxDist, out float t, out HitZone zone)
		{
			t = 0;
			zone = HitZone.Torso;
			if (stance == Stance.Downed)
			{
				// à terre, le corps est allongé (≈ 1,8 m de long) : un cylindre bas et large, au lieu d'un debout rétréci que les balles manquaient
				if (RayVsCylinder(feet, 0f, DownedTop, DownedRadius, origin, dir, out float td) && td <= maxDist) { t = td; return true; }
				return false;
			}
			float s = Sim.HeightFor(stance) / Sim.HeightStanding;
			bool hit = false;
			float best = maxDist;

			if (RayVsCylinder(feet, 0f, LegsTop * s, LegsRadius * s, origin, dir, out float tt) && tt <= best) { best = tt; zone = HitZone.Legs; hit = true; }
			if (RayVsCylinder(feet, LegsTop * s, TorsoTop * s, TorsoRadius * s, origin, dir, out tt) && tt <= best) { best = tt; zone = HitZone.Torso; hit = true; }
			if (RayVsSphere(new Vec3(feet.X, feet.Y + HeadCenter * s, feet.Z), HeadRadius * s, origin, dir, out tt) && tt <= best) { best = tt; zone = HitZone.Head; hit = true; }
			t = best;
			return hit;
		}

		/// <summary>Cylindre vertical (de y0 à y1 au-dessus des pieds, rayon r).</summary>
		private static bool RayVsCylinder(Vec3 feet, float y0o, float y1o, float r, Vec3 origin, Vec3 dir, out float t)
		{
			t = 0;
			float ox = origin.X - feet.X, oz = origin.Z - feet.Z;
			float a = dir.X * dir.X + dir.Z * dir.Z;
			float tEnter, tExit;
			if (a < 1e-8f)
			{
				if (ox * ox + oz * oz > r * r) return false;
				tEnter = float.NegativeInfinity; tExit = float.PositiveInfinity;
			}
			else
			{
				float b = 2f * (ox * dir.X + oz * dir.Z);
				float c = ox * ox + oz * oz - r * r;
				float disc = b * b - 4f * a * c;
				if (disc < 0f) return false;
				float sq = MathF.Sqrt(disc);
				tEnter = (-b - sq) / (2f * a); tExit = (-b + sq) / (2f * a);
			}
			float y0 = feet.Y + y0o, y1 = feet.Y + y1o;
			if (MathF.Abs(dir.Y) < 1e-8f)
			{
				if (origin.Y < y0 || origin.Y > y1) return false;
			}
			else
			{
				float ta = (y0 - origin.Y) / dir.Y, tb = (y1 - origin.Y) / dir.Y;
				if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
				if (ta > tEnter) tEnter = ta;
				if (tb < tExit) tExit = tb;
			}
			if (tEnter > tExit || tExit < 0f) return false;
			t = MathF.Max(tEnter, 0f);
			return true;
		}

		private static bool RayVsSphere(Vec3 c, float r, Vec3 origin, Vec3 dir, out float t)
		{
			t = 0;
			float mx = origin.X - c.X, my = origin.Y - c.Y, mz = origin.Z - c.Z;
			float b = mx * dir.X + my * dir.Y + mz * dir.Z;
			float cc = mx * mx + my * my + mz * mz - r * r;
			float disc = b * b - (dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z) * cc;
			if (disc < 0f) return false;
			float d2 = dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z;
			float sq = MathF.Sqrt(disc);
			float t0 = (-b - sq) / d2, t1 = (-b + sq) / d2;
			if (t1 < 0f) return false;
			t = MathF.Max(t0, 0f);
			return true;
		}
	}

	public struct HistoryEntry
	{
		public uint Tick;
		public Vec3 Pos;
		public Stance Stance;
		public bool Alive;
	}

	/// <summary>
	/// Historique des positions d'un joueur sur environ une seconde. Sert à la compensation de latence :
	/// pour juger un tir, on remet la cible là où le tireur la voyait.
	/// </summary>
	public sealed class PositionHistory
	{
		public const int Capacity = 64;
		private readonly HistoryEntry[] _ring = new HistoryEntry[Capacity];
		private uint _newest;
		private bool _any;

		public void Record(uint tick, Vec3 pos, Stance stance, bool alive)
		{
			_ring[tick % Capacity] = new HistoryEntry { Tick = tick, Pos = pos, Stance = stance, Alive = alive };
			_newest = tick;
			_any = true;
		}

		/// <summary>Position au tick demandé (ramenée à la plage disponible).</summary>
		public bool TryGet(uint tick, out HistoryEntry entry)
		{
			entry = default;
			if (!_any) return false;
			uint oldest = _newest >= Capacity - 1 ? _newest - (Capacity - 1) : 0;
			if (tick > _newest) tick = _newest;
			if (tick < oldest) tick = oldest;
			var e = _ring[tick % Capacity];
			if (e.Tick != tick) return false;
			entry = e;
			return true;
		}
	}

	/// <summary>Générateur pseudo-aléatoire minuscule, reproductible, sans allocation.</summary>
	public struct FastRng
	{
		private uint _s;
		public FastRng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }

		public uint Next()
		{
			_s ^= _s << 13;
			_s ^= _s >> 17;
			_s ^= _s << 5;
			return _s;
		}

		/// <summary>Flottant dans [0, 1[.</summary>
		public float NextFloat() => (Next() & 0xFFFFFF) / (float)0x1000000;

		/// <summary>Flottant dans [-1, 1].</summary>
		public float NextSigned() => (Next() & 0xFFFFFF) / (float)0x7FFFFF - 1f;
	}
}
