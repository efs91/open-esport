using System;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Balles : chaque tir d'une arme à feu crée un projectile qui avance à vitesse finie et tombe sous la gravité (comme en partie standard).
	/// À chaque tick on balaie le segment parcouru : relief et obstacles d'abord, puis joueurs.
	/// Les joueurs sont testés là où le tireur les voyait (compensation de latence) : leur position est relue dans l'historique,
	/// au tick de vue du tir plus le temps de vol, sans jamais dépasser le présent.
	/// </summary>
	public sealed partial class GameServer
	{
		public static float BulletSpeed => GameBalance.BulletSpeed;     // m/s : réglable (balance.json)
		public static float BulletGravity => GameBalance.BulletGravity;
		private const float BulletMaxSeconds = 3f;
		private const float ShotHearingRange = 250f;
		private const int ProjectileCapacity = 2048;

		private struct Projectile
		{
			public bool Active, Cannon;
			public ServerPlayer Owner;
			public byte WeaponId;
			public ControlVehicle SourceVehicle;
			public float Damage;
			public Vec3 Pos, Vel;
			public uint StartTick, ViewTick;
			public float Travelled, MaxDistance, Age;
		}

		private readonly Projectile[] _bullets = new Projectile[ProjectileCapacity];
		private int _bulletHigh;

		private void ClearProjectiles()
		{
			for (int i = 0; i < _bulletHigh; i++) _bullets[i].Active = false;
			_bulletHigh = 0;
		}

		private void SpawnProjectile(ServerPlayer owner, byte weaponId, float damage, WeaponDef def, Vec3 origin, Vec3 dir, uint viewTick, bool cannon = false)
		{
			int idx = -1;
			for (int i = 0; i < _bulletHigh; i++) if (!_bullets[i].Active) { idx = i; break; }
			if (idx < 0 && _bulletHigh < ProjectileCapacity) idx = _bulletHigh++;
			if (idx < 0) return;
			_bullets[idx] = new Projectile
			{
				Active = true, Cannon = cannon, SourceVehicle = owner.Ride, Owner = owner, WeaponId = weaponId, Damage = damage, Pos = origin, Vel = dir * def.Speed,
				StartTick = _tick, ViewTick = viewTick, MaxDistance = def.Range
			};
			BroadcastShot(owner, weaponId, origin, dir);
		}

		/// <summary>Annonce un tir aux joueurs proches pour qu'ils dessinent la balle. Non fiable : un tir perdu n'a aucune conséquence.</summary>
		private void BroadcastShot(ServerPlayer shooter, byte weaponId, Vec3 origin, Vec3 dir)
		{
			_tmp.Reset();
			new ServerEvent { Type = EventType.Shot, A = shooter.Id, C = weaponId,
                D = (byte)(shooter.Ride?.Hover == false && shooter.Seat < 2
                    ? (shooter.Seat == 1 ? ShotAppearance.WalkerCannon : ShotAppearance.WalkerMachineGun) : ShotAppearance.Standard), Pos = origin, Dir = dir }.Write(_tmp);
			foreach (var q in _humans)
			{
				float dx = q.State.Pos.X - shooter.State.Pos.X, dz = q.State.Pos.Z - shooter.State.Pos.Z;
				if (dx * dx + dz * dz > ShotHearingRange * ShotHearingRange) continue;
				q.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.Unreliable);
			}
		}

		/// <summary>Avance toutes les balles d'un tick. À appeler après l'enregistrement de l'historique des joueurs.</summary>
		private void StepProjectiles(float dt)
		{
			for (int i = 0; i < _bulletHigh; i++)
			{
				if (!_bullets[i].Active) continue;
				ref var b = ref _bullets[i];

				b.Vel = new Vec3(b.Vel.X, b.Vel.Y - BulletGravity * dt, b.Vel.Z);
				Vec3 step = b.Vel * dt;
				float len = step.Length();
				b.Age += dt;
				if (len < 1e-5f) { b.Active = false; continue; }
				Vec3 dir = step / len;

				float reach = len;
				bool wall = _world.Raycast(b.Pos, dir, len, out float wallDist, out int pane);
				if (wall) reach = wallDist;
				ControlVehicle hull = null;
				foreach (var v in _vehicles)
				{
					if (v == b.SourceVehicle || v.Integrity <= 0) continue;
					if (StaticWorld.RayAabb(b.Pos,dir,v.Hull,out float t) && t < reach) { hull=v; reach=t; }
				}

				// là où le tireur voyait les autres au moment de tirer, décalé du temps de vol
				uint rewindTick = Math.Min(_tick, b.ViewTick + (_tick - b.StartTick));
				ServerPlayer best = null;
				float bestT = reach;
				HitZone bestZone = HitZone.Torso;
				for (int k = 0; k < _players.Count; k++)
				{
					var o = _players[k];
					if (o == b.Owner || o.Ride != null && !o.Ride.Hover || !o.Alive) continue;
					if (!_m.FriendlyFire && o.Team == b.Owner.Team) continue;
					if (!o.History.TryGet(rewindTick, out var h) || !h.Alive) continue;
					float dx = h.Pos.X - b.Pos.X, dz = h.Pos.Z - b.Pos.Z;
					float r = bestT + 1f;
					if (dx * dx + dz * dz > r * r) continue;
					if (Hitboxes.RayVsBody(h, b.Pos, dir, bestT, out float t, out var zone) && t < bestT)
					{
						best = o; bestT = t; bestZone = zone;
					}
				}

				if (b.Cannon && (best != null || wall || hull != null))
				{
					var impact = b.Pos + dir * MathF.Max(0,bestT-.08f);
					var frag = GrenadeDefs.Get(ItemIds.GrenadeFrag);
					BroadcastExplosion(frag.Kind, impact);
					Blast(b.Owner, frag, impact);
				}
				if (best != null)
				{
					ApplyDamage(b.Owner, best, b.Damage * Hitboxes.Multiplier(bestZone) * BodyBalance.BulletDamage(best.Appearance, best.State.Vel), bestZone, b.WeaponId);
					b.Active = false;
					continue;
				}
				if (hull != null)
				{
					HitVehicle(hull,b.Owner,b.Damage);
					b.Active=false;
					continue;
				}
				if (wall)
				{
					if (pane >= 0) HitWindow(pane, b.Damage);
					b.Active = false;
					continue;
				}

				b.Pos = b.Pos + step;
				b.Travelled += len;
				if (b.Travelled >= b.MaxDistance || b.Age >= BulletMaxSeconds) b.Active = false;
			}
		}
	}
}
