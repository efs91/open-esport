using System;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// La zone d'une partie battle royale : le serveur déroule le réglage (<see cref="ServerOptions.Zones"/>) avec <see cref="ZoneRunner"/>, diffuse l'état aux joueurs
	/// et inflige des dégâts périodiques à quiconque est hors de la zone sûre.
	/// </summary>
	public sealed partial class GameServer
	{
		/// <summary>Identifiant d'« arme » des dégâts de zone dans les événements (fil des éliminations).</summary>
		public const byte ZoneWeaponId = 250;

		private ZoneRunner _zone;
		private float _zoneSendTimer;

		public ZoneRunner Zone => _zone;

		private void StartZone()
		{
			var cfg = _opt.Zones ?? ZonePresets.Apex();
			_zone = new ZoneRunner(cfg, _data.MinX, _data.MaxX, _data.MinZ, _data.MaxZ, _rng.Next(), _m.TickRate);
			_zone.Start();
			foreach (var p in _players) p.ZoneTimer = 0f;
			Log($"Zones « {cfg.Name} » : zone 1 de {_zone.ReferenceRadius * cfg.FirstRadius / 100f:F0} m de rayon, {cfg.Stages.Count} étape(s){(cfg.Cycle ? ", puis cyclique" : "")}");
			BroadcastZone();
		}

		private void TickZone(float dt)
		{
			if (_zone == null) return;
			_zone.Update(dt);
			_zoneSendTimer -= dt;
			if (_zone.Changed || _zoneSendTimer <= 0f) { _zoneSendTimer = 1f; BroadcastZone(); }
			if (_zone.Phase == ZonePhase.None) return;

			float tick = _opt.Zones?.TickSeconds ?? 1.5f;
			for (int i = _players.Count - 1; i >= 0; i--)
			{
				var p = _players[i];
				if (!p.Alive || p.State.Stance == Stance.Plane) { p.ZoneTimer = 0f; p.ZoneTicks = 0; continue; }
				if (_zone.Inside(p.State.Pos.X, p.State.Pos.Z)) { p.ZoneTimer = 0f; p.ZoneTicks = 0; continue; }
				p.ZoneTimer += dt;
				if (p.ZoneTimer >= tick)
				{
					p.ZoneTimer -= tick;
					// les dégâts dépendent de la zone (de plus en plus forts à chaque zone) et montent encore tant qu'on reste dehors
					float ramp = MathF.Min(GameBalance.ZoneRampMax, 1f + GameBalance.ZoneRampPerTick * p.ZoneTicks);
					p.ZoneTicks++;
					ApplyZoneDamage(p, _zone.Damage * ramp);
				}
			}
		}

		/// <summary>Un coup de zone : il passe à travers le bouclier si le réglage le demande (Apex), et peut mettre à terre ou éliminer.</summary>
		private void ApplyZoneDamage(ServerPlayer victim, float damage)
		{
			if (damage <= 0f || Phase != MatchPhase.Playing) return;
			if (!(_opt.Zones?.IgnoreShield ?? true))
			{
				float absorbed = MathF.Min(victim.Shield, damage);
				victim.Shield -= absorbed;
				damage -= absorbed;
			}
			victim.Health -= damage;
			byte dmg = (byte)Math.Clamp((int)MathF.Round(damage), 0, 255);
			SendEvent(victim, new ServerEvent { Type = EventType.Damaged, A = victim.Id, C = dmg, D = (byte)(victim.Health <= 0f ? 1 : 0), Pos = victim.State.Pos });
			if (victim.Health <= 0f)
			{
				victim.LastAttacker = null;          // la zone ne donne de points à personne
				OnLethal(null, victim, ZoneWeaponId);
			}
		}

		private void BroadcastZone()
		{
			if (_zone == null) return;
			var st = _zone.ToState(_tick);
			foreach (var s in _spectators) { _tmp.Reset(); st.Write(_tmp); s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, LiteNetLib.DeliveryMethod.ReliableOrdered); }
			for (int i = 0; i < _humans.Count; i++)
			{
				var p = _humans[i];
				if (p.Peer == null) continue;
				_tmp.Reset();
				st.Write(_tmp);
				p.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, LiteNetLib.DeliveryMethod.ReliableOrdered);
			}
		}

		// ------------------------------------------------------------------ bots

		/// <summary>
		/// Le point vers lequel un bot doit aller pour rester en vie : dans la zone visée (la zone suivante quand elle est annoncée, sinon la zone actuelle).
		/// Retourne faux si le bot est déjà bien placé.
		/// </summary>
		private bool BotZoneGoal(ServerPlayer p, BotBrain b, out Vec3 goal)
		{
			goal = default;
			if (_zone == null || _zone.Phase == ZonePhase.None) return false;
			float cx, cz, cr;
			_zone.CurrentCircle(out float ccx, out float ccz, out float ccr);
			if (_zone.Phase == ZonePhase.Collapsed) { cx = ccx; cz = ccz; cr = 30f; }
			else if (_zone.NextVisible) _zone.NextCircle(out cx, out cz, out cr);
			else { cx = ccx; cz = ccz; cr = ccr; }
			float dx = p.State.Pos.X - cx, dz = p.State.Pos.Z - cz;
			float dist = MathF.Sqrt(dx * dx + dz * dz);
			bool outsideNow = !_zone.Inside(p.State.Pos.X, p.State.Pos.Z);
			// il part vers la prochaine zone dès qu'elle est annoncée, sans attendre d'être touché ; avec une marge pour ne pas rester au bord
			if (!outsideNow && dist <= cr * 0.8f) return false;
			float a = (float)(b.Rng.NextDouble() * Math.PI * 2.0), rr = cr * 0.55f * MathF.Sqrt((float)b.Rng.NextDouble());
			goal = new Vec3(cx + MathF.Cos(a) * rr, 0, cz + MathF.Sin(a) * rr);
			return true;
		}

		/// <summary>Ramène un but d'errance dans la zone visée (un bot ne se promène pas hors de la zone).</summary>
		private Vec3 ZoneClamp(Vec3 g)
		{
			if (_zone == null || _zone.Phase == ZonePhase.None) return g;
			float cx, cz, cr;
			if (_zone.NextVisible) _zone.NextCircle(out cx, out cz, out cr); else _zone.CurrentCircle(out cx, out cz, out cr);
			if (_zone.Phase == ZonePhase.Collapsed) cr = 30f;
			float dx = g.X - cx, dz = g.Z - cz, d = MathF.Sqrt(dx * dx + dz * dz), lim = MathF.Max(5f, cr * 0.8f);
			if (d <= lim || d < 1e-3f) return g;
			return new Vec3(cx + dx / d * lim, g.Y, cz + dz / d * lim);
		}
	}
}
