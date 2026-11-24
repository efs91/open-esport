using System;

namespace OpenEsport.Core
{
	/// <summary>Entité vue par le client, avec l'état interpolé à l'instant d'affichage.</summary>
	public struct InterpolatedEntity
	{
		public ushort Id;
		public EntityKind Kind;
		public Vec3 Pos;
		public float Yaw, Pitch;
		/// <summary>Phase du pas (0..1).</summary>
		public float Gait;
		public Vec3 Vel;
		public EntityFlags Flags;
		public byte Health, Shield, ShieldTier, Weapon, Team, Zip;
		public ushort Count;

		public bool Has(EntityFlags f) => ((byte)Flags & (byte)f) != 0;
	}

	/// <summary>
	/// Historique récent d'une entité distante. On l'affiche légèrement dans le passé en interpolant entre deux snapshots reçus,
	/// ce qui donne un mouvement fluide même si des paquets se perdent.
	/// </summary>
	public sealed class RemoteEntity
	{
		private const int Size = 8;
		private readonly uint[] _ticks = new uint[Size];
		private readonly EntityState[] _states = new EntityState[Size];
		private int _count;
		private int _newest = -1;

		public ushort Id;
		public EntityKind Kind;

		public uint NewestTick => _newest >= 0 ? _ticks[_newest] : 0;

		/// <summary>
		/// Ajoute un état reçu. Si l'entité n'a rien envoyé depuis longtemps (elle ne bougeait pas), on insère d'abord son ancienne
		/// position juste avant, pour qu'elle ne glisse pas lentement depuis un point lointain dans le temps.
		/// </summary>
		public void Push(uint tick, in EntityState state, int holdTicks)
		{
			if (_count > 0 && tick <= NewestTick) return;
			if (_count > 0 && tick - NewestTick > (uint)(holdTicks * 2) && Kind == EntityKind.Player)
				Store(tick - (uint)holdTicks, _states[_newest]);
			Store(tick, state);
		}

		private void Store(uint tick, in EntityState state)
		{
			_newest = (_newest + 1) % Size;
			_ticks[_newest] = tick;
			_states[_newest] = state;
			if (_count < Size) _count++;
		}

		public bool Sample(float renderTick, in Quantizer q, out InterpolatedEntity result)
		{
			result = default;
			if (_count == 0) return false;

			// cherche le dernier état dont le tick est <= au tick d'affichage, et celui qui le suit
			int a = -1, b = -1;
			for (int i = 0; i < _count; i++)
			{
				int idx = (_newest - i + Size) % Size; // du plus récent au plus ancien
				if (_ticks[idx] <= renderTick) { a = idx; break; }
				b = idx;
			}
			if (a < 0) a = (_newest - (_count - 1) + Size) % Size; // rien d'aussi ancien : on garde le plus ancien
			var sa = _states[a];
			if (b < 0 || _ticks[b] <= _ticks[a])
			{
				Fill(ref result, sa, sa, 0f, q);
				return true;
			}
			float t = (renderTick - _ticks[a]) / (float)(_ticks[b] - _ticks[a]);
			t = Math.Clamp(t, 0f, 1f);
			// téléporteur, réapparition : plus de 4 m par tick à l'horizontale, ce n'est pas un déplacement : on saute d'une position à l'autre
			float jx = q.DX(_states[b].X) - q.DX(sa.X), jz = q.DZ(_states[b].Z) - q.DZ(sa.Z);
			if (jx * jx + jz * jz > 16f * (float)(_ticks[b] - _ticks[a]) * (_ticks[b] - _ticks[a])) t = t < 0.5f ? 0f : 1f;
			Fill(ref result, sa, _states[b], t, q);
			return true;
		}

		private void Fill(ref InterpolatedEntity r, in EntityState a, in EntityState b, float t, in Quantizer q)
		{
			r.Id = a.Id;
			r.Kind = a.Kind;
			r.Pos = new Vec3(
				q.DX(a.X) + (q.DX(b.X) - q.DX(a.X)) * t,
				q.DY(a.Y) + (q.DY(b.Y) - q.DY(a.Y)) * t,
				q.DZ(a.Z) + (q.DZ(b.Z) - q.DZ(a.Z)) * t);
			r.Flags = (EntityFlags)b.Flags;
			r.Health = b.Health;
			r.Shield = b.Shield;
			r.ShieldTier = b.ShieldTier;
			r.Zip = b.Zip;
			r.Weapon = b.Weapon;
			r.Team = b.Team;
			r.Count = b.Count;
			if (a.Kind == EntityKind.Player)
			{
				float ya = EntityState.DequantizeYaw12(a.Yaw), yb = EntityState.DequantizeYaw12(b.Yaw);
				float d = yb - ya;
				while (d > MathF.PI) d -= MathF.PI * 2f;
				while (d < -MathF.PI) d += MathF.PI * 2f;
				r.Yaw = ya + d * t;
				float ga = a.Gait / 32f, gd = b.Gait / 32f - ga;
				if (gd < -.5f) gd += 1f; else if (gd > .5f) gd -= 1f;
				r.Gait = ga + gd * t; r.Gait -= MathF.Floor(r.Gait);
				r.Pitch = EntityState.DequantizePitch8(a.Pitch) + (EntityState.DequantizePitch8(b.Pitch) - EntityState.DequantizePitch8(a.Pitch)) * t;
				r.Vel = new Vec3(EntityState.DequantizeVel(a.VelX) + (EntityState.DequantizeVel(b.VelX) - EntityState.DequantizeVel(a.VelX)) * t, 0,
					EntityState.DequantizeVel(a.VelZ) + (EntityState.DequantizeVel(b.VelZ) - EntityState.DequantizeVel(a.VelZ)) * t);
			}
		}
	}
}
