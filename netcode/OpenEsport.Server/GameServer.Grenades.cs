using System;
using System.Collections.Generic;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Grenades : fragmentation, collante, incendiaire et flash. Le serveur simule le vol (gravité, rebonds sur le relief et les obstacles),
	/// décide des dégâts, du feu et de l'aveuglement ; les clients ne reçoivent que les positions (20 fois par seconde) et les effets à dessiner.
	/// Une grenade lancée est retirée du sac du joueur au moment du lancer.
	/// </summary>
	public sealed partial class GameServer
	{
		private const int GrenadeCapacity = 96, FireZoneCapacity = 24;
		private const float GrenadeHearingRange = 220f, GrenadeViewRange = 160f;
		private const float GrenadeRestitution = 0.45f, GrenadeTangentKeep = 0.82f, GrenadeRadius = 0.12f;
		private const float FireTickSeconds = 0.5f;

		private struct Grenade
		{
			public bool Active;
			public ushort Id;
			public GrenadeDef Def;
			public ServerPlayer Owner;
			public Vec3 Pos, Vel;
			public float Age;
			public bool Stuck;
			/// <summary>Collée à un joueur : elle le suit.</summary>
			public ServerPlayer StuckTo;
			public Vec3 StuckOffset;
		}

		private struct FireZone
		{
			public bool Active;
			public ushort Id;
			public ServerPlayer Owner;
			public Vec3 Pos;
			public float Radius, Left, TickLeft, DamagePerTick;
		}

		private readonly Grenade[] _grenades = new Grenade[GrenadeCapacity];
		private readonly FireZone[] _fires = new FireZone[FireZoneCapacity];
		private ushort _grenadeSeq;
		private readonly List<GrenadeInfo> _grenadeView = new List<GrenadeInfo>(GrenadeCapacity + FireZoneCapacity);
		private readonly List<GrenadeInfo> _grenadeSend = new List<GrenadeInfo>(Messages.MaxGrenadesPerMessage);

		private void ClearGrenades()
		{
			for (int i = 0; i < _grenades.Length; i++) _grenades[i].Active = false;
			for (int i = 0; i < _fires.Length; i++) _fires[i].Active = false;
		}

		private ushort NextGrenadeId() { _grenadeSeq++; if (_grenadeSeq == 0) _grenadeSeq = 1; return _grenadeSeq; }

		// ------------------------------------------------------------------ lancer

		/// <summary>Sort une grenade en main (mains nues, l'arme est rangée) ou la range (<paramref name="itemId"/> = 0) et reprend l'arme précédente.</summary>
		private void EquipGrenade(ServerPlayer p, byte itemId)
		{
			if (!p.Alive || p.Downed) return;
			if (itemId == 0) { if (p.Inventory.GrenadeInHand != 0) { p.Inventory.GrenadeInHand = 0; ReturnWeapon(p); SendInventory(p); } return; }
			if (GrenadeDefs.Get(itemId) == null || p.Inventory.CountOf(itemId) <= 0) return;
			var stance = p.State.Stance;
			if (stance == Stance.Plane || stance == Stance.Diving || stance == Stance.Downed) return;
			if (p.Inventory.GrenadeInHand == 0 && p.Inventory.ActiveSlot >= 0) p.PreGrenadeSlot = p.Inventory.ActiveSlot;       // on reprendra cette arme
			p.Reloading = false; p.UseId = 0;
			p.Inventory.SelectSlot(-1);
			p.Inventory.GrenadeInHand = itemId;
			SendInventory(p);
		}

		private void ReturnWeapon(ServerPlayer p)
		{
			int s = p.PreGrenadeSlot;
			if (s >= 0 && s < p.Inventory.Weapons.Length && p.Inventory.Weapons[s] != null) { p.Inventory.SelectSlot(s); return; }
			for (int i = 0; i < p.Inventory.Weapons.Length; i++) if (p.Inventory.Weapons[i] != null) { p.Inventory.SelectSlot(i); return; }
		}


		/// <summary>Le joueur lance une grenade de son sac dans la direction où il regarde.</summary>
		private void ThrowGrenade(ServerPlayer p, byte itemId, float? elevation = null, float botYaw = 0f)
		{
			var def = GrenadeDefs.Get(itemId);
			if (def == null || !p.Alive || p.Downed) return;
			var stance = p.State.Stance;
			if (stance == Stance.Plane || stance == Stance.Diving || stance == Stance.Downed) return;
			if (Phased && Phase != MatchPhase.Playing) return;
			if (Modal && _mode.Frozen(p)) return;
			if (p.GrenadeCooldown > 0f) return;
			int slot = -1;
			for (int i = 0; i < _grenades.Length; i++) if (!_grenades[i].Active) { slot = i; break; }
			if (slot < 0 || !p.Inventory.RemoveOne(itemId)) return;

			p.GrenadeCooldown = GrenadeDefs.ThrowCooldown;
			p.UseId = 0;                                       // lancer interrompt un soin
			var inp = p.LastInput;
			float yaw = elevation.HasValue ? botYaw : inp.Yaw;
			float cp = MathF.Cos(inp.Pitch), sp = MathF.Sin(inp.Pitch), sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
			Vec3 fwd = new Vec3(-sy * cp, sp, -cy * cp);
			Vec3 eye = p.State.Pos + new Vec3(0, Sim.EyeFor(stance), 0);
			Vec3 dir = (fwd + new Vec3(0, 0.2f, 0)).Normalized();      // un peu vers le haut : un arc, pas une ligne droite
			if (elevation.HasValue) dir = new Vec3(-sy * MathF.Cos(elevation.Value), MathF.Sin(elevation.Value), -cy * MathF.Cos(elevation.Value));      // bot : angle calculé pour atteindre la cible
			Vec3 origin = eye + fwd * 0.5f;
			if (_world.Raycast(eye, fwd, 0.5f, out _)) origin = eye;   // contre un mur : elle part de l'œil
			Vec3 carry = new Vec3(p.State.Vel.X * 0.6f, 0f, p.State.Vel.Z * 0.6f);

			_grenades[slot] = new Grenade
			{
				Active = true, Id = NextGrenadeId(), Def = def, Owner = p, Pos = origin,
				Vel = dir * GrenadeDefs.ThrowSpeed + carry
			};
			// grenade lancée depuis la main : on reprend l'arme (ou on en sort une autre si c'était la dernière de ce type)
			if (p.Inventory.GrenadeInHand == itemId) { p.Inventory.GrenadeInHand = 0; ReturnWeapon(p); }
			SendInventory(p);
		}

		// ------------------------------------------------------------------ simulation

		private void StepGrenades(float dt)
		{
			for (int i = 0; i < _players.Count; i++) if (_players[i].GrenadeCooldown > 0f) _players[i].GrenadeCooldown -= dt;
			for (int i = 0; i < _players.Count; i++) if (_players[i].BlindLeft > 0f) _players[i].BlindLeft -= dt;

			for (int i = 0; i < _grenades.Length; i++)
			{
				if (!_grenades[i].Active) continue;
				ref var g = ref _grenades[i];
				g.Age += dt;
				if (g.Age >= g.Def.Fuse) { Detonate(ref g, g.Pos); continue; }
				if (g.Stuck) { FollowStuck(ref g); continue; }

				g.Vel = new Vec3(g.Vel.X, g.Vel.Y - GrenadeDefs.Gravity * dt, g.Vel.Z);
				Vec3 step = g.Vel * dt;
				float len = step.Length();
				if (len < 1e-6f) continue;
				Vec3 dir = step / len;

				// une grenade collante s'accroche d'abord aux joueurs
				if (g.Def.Sticky && TryStickToPlayer(ref g, g.Pos + step)) continue;

				if (_world.Raycast(g.Pos, dir, len + GrenadeRadius, out float wallDist))
				{
					Vec3 hit = g.Pos + dir * MathF.Max(0f, wallDist - GrenadeRadius);
					if (g.Def.Sticky) { g.Pos = hit; g.Stuck = true; g.Vel = Vec3.Zero; continue; }
					if (g.Def.OnImpact) { Detonate(ref g, hit); continue; }
					Vec3 n = SurfaceNormal(hit + dir * GrenadeRadius, dir);
					float vn = g.Vel.Dot(n);
					Vec3 vTan = g.Vel - n * vn;
					g.Vel = vTan * GrenadeTangentKeep - n * (vn * GrenadeRestitution);
					g.Pos = hit + n * 0.02f;
					// presque arrêtée sur un sol : elle reste là
					if (n.Y > 0.6f && g.Vel.Length() < 1.2f) g.Vel = Vec3.Zero;
				}
				else
				{
					g.Pos = g.Pos + step;
				}
				if (g.Pos.Y < _data.MinHeight - 60f || g.Pos.X < _data.MinX - 40f || g.Pos.X > _data.MaxX + 40f || g.Pos.Z < _data.MinZ - 40f || g.Pos.Z > _data.MaxZ + 40f)
					g.Active = false;
			}

			StepFires(dt);
			if (_tick % 3 == 0) BroadcastGrenades();
		}

		private void FollowStuck(ref Grenade g)
		{
			if (g.StuckTo == null) return;
			if (!g.StuckTo.Alive) { g.StuckTo = null; g.Stuck = false; g.Vel = Vec3.Zero; return; }   // son porteur est mort : elle tombe
			g.Pos = g.StuckTo.State.Pos + g.StuckOffset;
		}

		private bool TryStickToPlayer(ref Grenade g, Vec3 next)
		{
			for (int k = 0; k < _players.Count; k++)
			{
				var o = _players[k];
				if (!o.Alive || o.State.Stance == Stance.Plane || o.State.Stance == Stance.Diving) continue;
				if (o == g.Owner && g.Age < 0.35f) continue;               // elle ne se colle pas à celui qui vient de la lancer
				float dx = next.X - o.State.Pos.X, dz = next.Z - o.State.Pos.Z;
				float h = Sim.HeightFor(o.State.Stance);
				if (dx * dx + dz * dz > 0.5f * 0.5f || next.Y < o.State.Pos.Y || next.Y > o.State.Pos.Y + h) continue;
				g.Stuck = true; g.StuckTo = o; g.Vel = Vec3.Zero;
				g.StuckOffset = new Vec3(0f, Math.Clamp(next.Y - o.State.Pos.Y, 0.2f, h), 0f);
				g.Pos = o.State.Pos + g.StuckOffset;
				return true;
			}
			return false;
		}

		/// <summary>Normale de la surface touchée en <paramref name="hit"/> : on sonde les faces tournées vers la grenade (obstacles), sinon le relief.</summary>
		private Vec3 SurfaceNormal(Vec3 hit, Vec3 travel)
		{
			Vec3 best = default;
			float bestDist = 0.45f;
			Vec3 from = hit - travel * 0.2f;
			for (int a = 0; a < 6; a++)
			{
				Vec3 axis = a == 0 ? new Vec3(1, 0, 0) : a == 1 ? new Vec3(-1, 0, 0) : a == 2 ? new Vec3(0, 1, 0) : a == 3 ? new Vec3(0, -1, 0) : a == 4 ? new Vec3(0, 0, 1) : new Vec3(0, 0, -1);
				if (axis.Dot(travel) < 0.15f) continue;                       // une face qu'on ne peut pas avoir touchée
				if (_world.Raycast(from, axis, 0.5f, out float d) && d < bestDist) { bestDist = d; best = new Vec3(-axis.X, -axis.Y, -axis.Z); }
			}
			if (bestDist < 0.45f && !(best.Y > 0.5f && hit.Y - _world.HeightAt(hit.X, hit.Z) < 0.4f)) return best;
			if (hit.Y - _world.HeightAt(hit.X, hit.Z) < 0.5f) return _world.NormalAt(hit.X, hit.Z);
			return bestDist < 0.45f ? best : new Vec3(-travel.X, -travel.Y, -travel.Z).Normalized();
		}

		// ------------------------------------------------------------------ effets

		private void Detonate(ref Grenade g, Vec3 pos)
		{
			g.Active = false;
			var def = g.Def;
			var owner = g.Owner;
			BroadcastExplosion(def.Kind, pos);
			switch (def.Effect)
			{
				case GrenadeEffect.Blast: Blast(owner, def, pos); break;
				case GrenadeEffect.Fire: StartFire(owner, def, pos); break;
				case GrenadeEffect.Flash: FlashAll(def, pos); break;
			}
		}

		private bool GrenadeCanHurt(ServerPlayer owner, ServerPlayer victim) => victim == owner || owner == null || _m.FriendlyFire || owner.Team != victim.Team;

		private void Blast(ServerPlayer owner, GrenadeDef def, Vec3 pos)
		{
			Vec3 from = pos + new Vec3(0, 0.15f, 0);
			for (int i = _players.Count - 1; i >= 0; i--)
			{
				if (i >= _players.Count) continue;
				var v = _players[i];
				if (!v.Alive || v.State.Stance == Stance.Plane) continue;
				if (!GrenadeCanHurt(owner, v)) continue;
				Vec3 center = v.State.Pos + new Vec3(0, Sim.HeightFor(v.State.Stance) * 0.55f, 0);
				Vec3 to = center - from;
				float dist = to.Length();
				if (dist > def.Radius) continue;
				if (dist > 0.3f && _world.Raycast(from, to / dist, dist, out float wall) && wall < dist - 0.4f) continue;   // à couvert : le décor arrête le souffle
				float damage = def.Damage * (0.2f + 0.8f * (1f - dist / def.Radius));
				ApplyDamage(owner ?? v, v, damage, HitZone.Torso, def.Id);
			}
		}

		private void StartFire(ServerPlayer owner, GrenadeDef def, Vec3 pos)
		{
			int slot = -1;
			for (int i = 0; i < _fires.Length; i++) if (!_fires[i].Active) { slot = i; break; }
			if (slot < 0) return;
			float ground = _world.HeightAt(pos.X, pos.Z);
			_fires[slot] = new FireZone
			{
				Active = true, Id = NextGrenadeId(), Owner = owner, Pos = new Vec3(pos.X, MathF.Max(ground, pos.Y - 0.3f), pos.Z),
				Radius = def.Radius, Left = def.BurnSeconds, TickLeft = 0f, DamagePerTick = def.BurnDps * FireTickSeconds
			};
		}

		private void StepFires(float dt)
		{
			for (int i = 0; i < _fires.Length; i++)
			{
				if (!_fires[i].Active) continue;
				ref var f = ref _fires[i];
				f.Left -= dt;
				if (f.Left <= 0f) { f.Active = false; continue; }
				f.TickLeft -= dt;
				if (f.TickLeft > 0f) continue;
				f.TickLeft = FireTickSeconds;
				for (int k = _players.Count - 1; k >= 0; k--)
				{
					if (k >= _players.Count) continue;
					var v = _players[k];
					if (!v.Alive || v.State.Stance == Stance.Plane || v.State.Stance == Stance.Diving) continue;
					if (!GrenadeCanHurt(f.Owner, v)) continue;
					float dx = v.State.Pos.X - f.Pos.X, dz = v.State.Pos.Z - f.Pos.Z;
					if (dx * dx + dz * dz > f.Radius * f.Radius || v.State.Pos.Y > f.Pos.Y + 2.5f || v.State.Pos.Y < f.Pos.Y - 3f) continue;
					ApplyDamage(f.Owner ?? v, v, f.DamagePerTick, HitZone.Legs, ItemIds.GrenadeFire);
				}
			}
		}

		private void FlashAll(GrenadeDef def, Vec3 pos)
		{
			for (int i = 0; i < _players.Count; i++)
			{
				var v = _players[i];
				if (!v.Alive || v.State.Stance == Stance.Plane || v.State.Stance == Stance.Diving) continue;
				Vec3 eye = v.State.Pos + new Vec3(0, Sim.EyeFor(v.State.Stance), 0);
				Vec3 to = pos - eye;
				float dist = to.Length();
				if (dist > def.Radius) continue;
				if (dist > 0.5f && _world.Raycast(eye, to / dist, dist, out float wall) && wall < dist - 0.4f) continue;   // masquée par le décor
				Vec3 dirTo = dist > 0.01f ? to / dist : new Vec3(0, 0, -1);
				float cp = MathF.Cos(v.Pitch), sp = MathF.Sin(v.Pitch), sy = MathF.Sin(v.Yaw), cy = MathF.Cos(v.Yaw);
				float facing = (1f + new Vec3(-sy * cp, sp, -cy * cp).Dot(dirTo)) * 0.5f;     // 1 = on la regarde, 0 = dos tourné
				float seconds = def.FlashSeconds * (0.3f + 0.7f * facing) * (1f - 0.55f * dist / def.Radius);
				if (seconds < 0.3f) continue;
				v.BlindLeft = MathF.Max(v.BlindLeft, seconds);
				if (v.Peer != null)
					SendEvent(v, new ServerEvent { Type = EventType.Flashed, C = (byte)Math.Clamp((int)MathF.Round(seconds * 10f), 1, 255), D = (byte)Math.Clamp((int)MathF.Round(facing * 255f), 0, 255), Pos = pos });
			}
		}

		// ------------------------------------------------------------------ réseau

		private void BroadcastExplosion(byte kind, Vec3 pos)
		{
			_tmp.Reset();
			new ServerEvent { Type = EventType.Explosion, C = kind, Pos = pos }.Write(_tmp);
			foreach (var q in _humans)
			{
				float dx = q.State.Pos.X - pos.X, dz = q.State.Pos.Z - pos.Z;
				if (dx * dx + dz * dz > GrenadeHearingRange * GrenadeHearingRange) continue;
				q.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
			}
		}

		/// <summary>Positions des grenades et zones de feu proches de chaque joueur : non fiable, 20 fois par seconde.</summary>
		private void BroadcastGrenades()
		{
			_grenadeView.Clear();
			for (int i = 0; i < _grenades.Length; i++)
			{
				if (!_grenades[i].Active) continue;
				var g = _grenades[i];
				_grenadeView.Add(new GrenadeInfo { Id = g.Id, Kind = g.Def.Kind, Stuck = g.Stuck, Pos = g.Pos, Vel = g.Vel });
			}
			for (int i = 0; i < _fires.Length; i++)
			{
				if (!_fires[i].Active) continue;
				_grenadeView.Add(new GrenadeInfo { Id = _fires[i].Id, Kind = GrenadeDefs.FireZoneKind, Stuck = true, Pos = _fires[i].Pos });
			}
			if (_grenadeView.Count == 0 && !_grenadesWereSent) return;
			bool any = _grenadeView.Count > 0;
			foreach (var q in _humans)
			{
				_grenadeSend.Clear();
				for (int i = 0; i < _grenadeView.Count && _grenadeSend.Count < Messages.MaxGrenadesPerMessage; i++)
				{
					var gi = _grenadeView[i];
					float dx = q.State.Pos.X - gi.Pos.X, dz = q.State.Pos.Z - gi.Pos.Z;
					if (dx * dx + dz * dz <= GrenadeViewRange * GrenadeViewRange) _grenadeSend.Add(gi);
				}
				if (_grenadeSend.Count == 0 && !_grenadesWereSent) continue;
				_tmp.Reset();
				Messages.WriteGrenades(_tmp, _grenadeSend);
				q.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.Unreliable);
			}
			_grenadesWereSent = any;      // un dernier message vide quand il n'y a plus rien : les clients effacent leurs grenades
		}

		private bool _grenadesWereSent;
	}
}
