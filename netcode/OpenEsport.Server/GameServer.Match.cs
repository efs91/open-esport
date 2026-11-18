using System;
using System.Collections.Generic;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Cycle d'une partie « battle royale » : salle d'attente (on lance dès qu'il y a assez de joueurs), tout le monde dans le même avion
	/// (bots compris), pas de réapparition, joueurs « à terre » réanimables en équipe, le dernier en vie (ou la dernière équipe) gagne.
	/// Sans l'option <see cref="ServerOptions.Royale"/>, rien de tout cela ne s'applique (partie classique avec réapparition).
	/// </summary>
	public sealed partial class GameServer
	{
		public MatchPhase Phase { get; private set; }
		public bool StopRequested => _stopRequested;
		public byte WinnerTeam => _winnerTeam;

		private bool Royale => _opt.Royale;
		private float _countdown = -1f;
		private float _overTimer, _statusTimer, _idleTimer, _noHumansTimer;
		private byte _winnerTeam = 255;
		private bool _stopRequested;
		private PlayerState _flight;

		// ------------------------------------------------------------------ boucle

		private void TickMatch(float dt)
		{
			if (!Phased) return;
			switch (Phase)
			{
				case MatchPhase.Lobby:
					if (_humans.Count >= _opt.MinPlayers)
					{
						if (_countdown < 0f) { _countdown = _opt.LobbyCountdown; Log($"Départ dans {_countdown:F0} s ({_humans.Count} joueur(s))"); }
						_countdown -= dt;
						if (_countdown <= 0f) StartMatch();
					}
					else _countdown = -1f;
					if (_humans.Count == 0 && _opt.IdleExitSeconds > 0f)
					{
						_idleTimer += dt;
						if (_idleTimer >= _opt.IdleExitSeconds) { Log("Salle d'attente vide : arrêt du serveur"); _stopRequested = true; }
					}
					else _idleTimer = 0f;
					break;
				case MatchPhase.Playing:
					if (Modal) { TickModal(dt); break; }
					TickDowned(dt);
					TickZone(dt);
					break;
				case MatchPhase.Over:
					_overTimer += dt;
					if (_overTimer >= _opt.EndLinger) FinishMatch();
					break;
			}
			// plus que des bots : la partie n'a plus de raison d'être
			if (Phase != MatchPhase.Lobby && _humans.Count == 0)
			{
				_noHumansTimer += dt;
				if (_noHumansTimer >= _opt.EmptyExitSeconds) { Log("Plus aucun joueur humain : fin de la partie"); _noHumansTimer = 0f; FinishMatch(); return; }
			}
			else _noHumansTimer = 0f;
			_statusTimer -= dt;
			if (_statusTimer <= 0f) { _statusTimer = 0.25f; SendStatusToAll(); }
		}

		// ------------------------------------------------------------------ départ

		private void StartMatch()
		{
			_orders.Clear();
			if (Modal) { StartModalMatch(); return; }
			Phase = MatchPhase.Playing;
			_countdown = -1f;
			if (_opt.FillBots)
			{
				int guard = 0;
				while (_players.Count < BotFillTarget && (_opt.MaxBots < 0 || BotCount < _opt.MaxBots) && guard++ < 200) { int before = _players.Count; AddBot(); if (_players.Count == before) break; }
			}
			_flight = NewFlight();
			StartZone();
			foreach (var p in _players)
			{
				p.Alive = true; p.Downed = false; p.Health = 100f; p.Kills = 0; p.Deaths = 0; p.DamageDealt = 0f; p.Placement = 0; p.LastAttacker = null; p.ReviveTarget = null;
				p.Shield = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);
				p.State = _flight;
				p.State.OnGround = false;
				if (p.Brain is BotBrain b) PlanBotDrop(b);
				if (!p.IsBot) SendInventory(p);
			}
			BroadcastRoster();
			Log($"Départ de la partie : {_players.Count} joueurs ({_humans.Count} humains, {BotCount} bots), {TeamsAliveCount()} équipes");
			SendStatusToAll();
		}

		private void PlanBotDrop(BotBrain b)
		{
			float enter = _flight.PlaneEnter + 0.5f, exit = _flight.PlaneExit - 0.5f;
			b.JumpAt = enter + (float)b.Rng.NextDouble() * Math.Max(1f, exit - enter);
			if (_data.LootSpawns.Count > 0) b.LandTarget = _data.LootSpawns[b.Rng.Next(_data.LootSpawns.Count)].Pos;
			else b.LandTarget = new Vec3(_data.MinX + (float)b.Rng.NextDouble() * (_data.MaxX - _data.MinX), 0, _data.MinZ + (float)b.Rng.NextDouble() * (_data.MaxZ - _data.MinZ));
		}

		// ------------------------------------------------------------------ comptes

		public int PlayersAliveCount() { int n = 0; foreach (var p in _players) if (p.Alive) n++; return n; }

		public int TeamsAliveCount()
		{
			var seen = new HashSet<byte>();
			foreach (var p in _players) if (p.Alive) seen.Add(p.Team);
			return seen.Count;
		}

		private bool HasStandingTeammate(ServerPlayer v)
		{
			foreach (var p in _players) if (p != v && p.Team == v.Team && p.Alive && !p.Downed) return true;
			return false;
		}

		// ------------------------------------------------------------------ mort, à terre, élimination

		/// <summary>Un coup fatal : en équipe, si un coéquipier tient encore debout on tombe « à terre » ; sinon on est éliminé.</summary>
		private void OnLethal(ServerPlayer killer, ServerPlayer victim, byte weaponId)
		{
			if (_m.TeamSize > 1 && !victim.Downed && HasStandingTeammate(victim)) { Down(killer, victim, weaponId); return; }
			Eliminate(victim, killer, weaponId);
		}

		private void Down(ServerPlayer killer, ServerPlayer victim, byte weaponId)
		{
			victim.Downed = true;
			victim.Health = 100f;           // 100 points de vie qui s'écoulent en DownSeconds
			victim.Shield = 0f;
			victim.UseId = 0; victim.Reloading = false;
			victim.LastAttacker = killer;
			victim.ReviveTarget = null;
			victim.State.Stance = Stance.Downed;
			victim.State.Vel = Vec3.Zero;
			var ev = new ServerEvent { Type = EventType.Downed, A = killer?.Id ?? victim.Id, B = victim.Id, C = weaponId };
			for (int i = 0; i < _players.Count; i++) SendEvent(_players[i], ev);
		}

		private void EliminateCore(ServerPlayer victim, ServerPlayer killer, byte weaponId, bool announce)
		{
			victim.Alive = false; victim.Downed = false; victim.Deaths++;
			victim.Health = 0f; victim.Shield = 0f;
			victim.UseId = 0; victim.Reloading = false; victim.ReviveTarget = null;
			victim.State.Stance = Stance.Standing;
			DropAllLoot(victim);
			SendInventory(victim);
			var credit = killer ?? victim.LastAttacker;
			if (credit != null && credit != victim && credit.Team != victim.Team) { credit.Kills++; TotalKills++; }
			if (announce && (credit != null || weaponId == ZoneWeaponId))
			{
				var feed = new ServerEvent { Type = EventType.KillFeed, A = credit?.Id ?? victim.Id, B = victim.Id, C = weaponId };      // mort dans la zone : l'auteur est la victime elle-même
				for (int i = 0; i < _players.Count; i++) SendEvent(_players[i], feed);
			}
		}

		private void Eliminate(ServerPlayer victim, ServerPlayer killer, byte weaponId, bool announce = true)
		{
			EliminateCore(victim, killer, weaponId, announce);
			// équipe sans personne debout : ceux qui sont à terre meurent aussi
			bool standing = false;
			foreach (var p in _players) if (p.Team == victim.Team && p.Alive && !p.Downed) { standing = true; break; }
			if (!standing)
				foreach (var p in _players.ToArray())
					if (p.Team == victim.Team && p.Alive) EliminateCore(p, killer, weaponId, announce);
			bool teamDead = true;
			foreach (var p in _players) if (p.Team == victim.Team && p.Alive) { teamDead = false; break; }
			if (teamDead)
			{
				byte place = (byte)Math.Min(255, TeamsAliveCount() + 1);
				foreach (var p in _players) if (p.Team == victim.Team) p.Placement = place;
			}
			CheckWin();
		}

		private void CheckWin()
		{
			if (!Royale || Phase != MatchPhase.Playing) return;
			if (TeamsAliveCount() > 1) return;
			Phase = MatchPhase.Over;
			_overTimer = 0f;
			_winnerTeam = 255;
			foreach (var p in _players) if (p.Alive) { _winnerTeam = p.Team; break; }
			if (_winnerTeam != 255) foreach (var p in _players) if (p.Team == _winnerTeam) p.Placement = 1;
			Log(_winnerTeam == 255 ? "Fin de la partie : personne n'a gagné" : $"Fin de la partie : l'équipe {_winnerTeam} gagne");
			ReportToAccounts(_winnerTeam);
			SendStatusToAll();
		}

		/// <summary>À terre, la vie s'écoule : à zéro on est éliminé.</summary>
		private void TickDowned(float dt)
		{
			for (int i = _players.Count - 1; i >= 0; i--)
			{
				if (i >= _players.Count) continue;
				var p = _players[i];
				if (!p.Alive || !p.Downed) continue;
				p.Health -= 100f / _opt.DownSeconds * dt;
				if (p.Health <= 0f) Eliminate(p, p.LastAttacker, 0);
			}
			// filet de sécurité : une équipe dont tous les membres vivants sont à terre meurt tout de suite (rien ne peut plus relever personne)
			for (int i = _players.Count - 1; i >= 0; i--)
			{
				if (i >= _players.Count) continue;
				var p = _players[i];
				if (p.Alive && p.Downed && !HasStandingTeammate(p)) Eliminate(p, p.LastAttacker, 0, false);
			}
		}

		// ------------------------------------------------------------------ réanimation

		private ServerPlayer FindDownedTeammate(ServerPlayer p)
		{
			ServerPlayer best = null; float bd = Sim.ReviveRange * Sim.ReviveRange;
			foreach (var q in _players)
			{
				if (q == p || q.Team != p.Team || !q.Alive || !q.Downed) continue;
				float dx = q.State.Pos.X - p.State.Pos.X, dz = q.State.Pos.Z - p.State.Pos.Z, dy = q.State.Pos.Y - p.State.Pos.Y;
				float d2 = dx * dx + dz * dz;
				if (d2 < bd && MathF.Abs(dy) < 2f) { bd = d2; best = q; }
			}
			return best;
		}

		/// <summary>Maintenir E près d'un coéquipier à terre : au bout de ReviveSeconds il se relève.</summary>
		private void TickRevive(ServerPlayer p, bool holding, float dt)
		{
			if (!Royale || Phase != MatchPhase.Playing || !p.Alive || p.Downed || !holding) { p.ReviveTarget = null; return; }
			var t = FindDownedTeammate(p);
			if (t == null) { p.ReviveTarget = null; return; }
			if (p.ReviveTarget != t) { p.ReviveTarget = t; p.ReviveLeft = _opt.ReviveSeconds; }
			p.ReviveLeft -= dt;
			if (p.ReviveLeft <= 0f)
			{
				t.Downed = false;
				t.State.Stance = Stance.Standing;
				t.Health = _opt.RevivedHealth;
				t.LastAttacker = null;
				p.ReviveTarget = null;
				Log($"{p.Name} réanime {t.Name}");
			}
		}

		// ------------------------------------------------------------------ messages

		private MatchStatus StatusFor(ServerPlayer p)
		{
			var m = new MatchStatus
			{
				Phase = Phase, Lobby = (byte)Math.Min(255, _humans.Count), Needed = (byte)Math.Min(255, _opt.MinPlayers),
				Countdown = (byte)(_countdown > 0f ? Math.Min(255, (int)Math.Ceiling(_countdown)) : 0),
				PlayersAlive = (ushort)PlayersAliveCount(), TeamsAlive = (byte)Math.Min(255, TeamsAliveCount()), TeamSize = (byte)_m.TeamSize,
				Kills = (ushort)Math.Min(65535, p.Kills), Damage = (ushort)Math.Min(65535f, p.DamageDealt),
				Placement = p.Placement, WinnerTeam = _winnerTeam
			};
			if (p.Alive && p.Downed) m.DownLeft = (byte)Math.Clamp((int)Math.Ceiling(p.Health / 100f * _opt.DownSeconds), 1, 255);
			if (p.ReviveTarget != null) m.Revive = (byte)Math.Clamp((int)((1f - p.ReviveLeft / _opt.ReviveSeconds) * 100f), 0, 100);
			else if (p.Downed)
				foreach (var q in _players) if (q.ReviveTarget == p) { m.Revive = (byte)Math.Clamp((int)((1f - q.ReviveLeft / _opt.ReviveSeconds) * 100f), 0, 100); break; }
			if (Phase == MatchPhase.Playing && p.Alive && !p.Downed && FindDownedTeammate(p) != null) m.CanRevive = true;
			var lead = Phase == MatchPhase.Playing ? LeaderOf(p.Team) : null;
			m.JumpLeader = lead == null ? (ushort)0xFFFF : lead.Id;
			var ctrl = ControllerOf(p);
			m.ControlledBy = ctrl == p ? (ushort)0xFFFF : ctrl.Id;
			return m;
		}

		private void SendStatusToAll()
		{
			for (int i = 0; i < _humans.Count; i++)
			{
				var p = _humans[i];
				if (p.Peer == null) continue;
				_tmp.Reset();
				StatusFor(p).Write(_tmp);
				p.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, LiteNetLib.DeliveryMethod.ReliableOrdered);
			}
			SendModeStates();
		}

		/// <summary>Réponse à la requête « STATUS » d'un matchmaking : OE|phase|humains|requis|taille d'équipe|places|compte à rebours.</summary>
		private string StatusLine() => $"OE|{(int)Phase}|{_humans.Count}|{_opt.MinPlayers}|{_m.TeamSize}|{_m.MaxPlayers}|{Math.Max(0, (int)Math.Ceiling(_countdown))}";

		// ------------------------------------------------------------------ fin de partie

		private void FinishMatch()
		{
			Log("Partie terminée : les joueurs sont renvoyés au menu");
			foreach (var h in _humans.ToArray())
			{
				try { _tmp.Reset(); Messages.WriteReject(_tmp, "Partie terminée"); h.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, LiteNetLib.DeliveryMethod.ReliableOrdered); h.Peer.Disconnect(); } catch { }
				RemovePlayer(h);
			}
			if (_opt.ExitWhenOver) { _stopRequested = true; return; }
			foreach (var b in _players.ToArray()) if (b.IsBot) RemoveBot(b);
			_zone = null;
			ClearProjectiles();
			ClearGrenades();
			ClearLoot();
			SpawnInitialLoot();
			Phase = MatchPhase.Lobby;
			if (Modal) _mode = CreateMode(this, _opt.Mode);
			_countdown = -1f; _winnerTeam = 255; _overTimer = 0f;
		}

		private void ClearLoot()
		{
			for (int i = 0; i < _lootHigh; i++) _loot[i].Active = false;
			_lootHigh = 0; _freeLoot.Clear(); _lootDirty = true;
		}

		// ------------------------------------------------------------------ chute en équipe : chef de saut

		private readonly Dictionary<byte, ServerPlayer> _leaders = new Dictionary<byte, ServerPlayer>();

		private static bool InAir(ServerPlayer p) => p.State.Stance == Stance.Plane || p.State.Stance == Stance.Diving;

		/// <summary>
		/// Chef de saut d'une équipe : c'est lui seul qui dirige l'équipe pendant la chute. Un bot ne peut pas l'être tant qu'un humain vivant est dans l'équipe ;
		/// il change quand le chef disparaît (mort, départ) ou quand il passe la main (TAB).
		/// </summary>
		private ServerPlayer LeaderOf(byte team)
		{
			_leaders.TryGetValue(team, out var l);
			ServerPlayer firstHuman = null, firstAny = null;
			foreach (var q in _players)
			{
				if (q.Team != team || !q.Alive) continue;
				firstAny ??= q;
				if (!q.IsBot) firstHuman ??= q;
			}
			bool valid = l != null && l.Alive && l.Team == team && !(firstHuman != null && l.IsBot);
			if (!valid) { l = firstHuman ?? firstAny; _leaders[team] = l; }
			return l;
		}

		/// <summary>Qui dirige la chute de <paramref name="p"/> : le chef de saut tant qu'il est lui-même en l'air, sinon le joueur lui-même.</summary>
		private ServerPlayer ControllerOf(ServerPlayer p)
		{
			if (!InAir(p)) return p;
			var l = LeaderOf(p.Team);
			return l == null || l == p || !InAir(l) ? p : l;
		}

		/// <summary>Le chef passe le commandement au prochain humain de l'équipe encore en l'air (s'il y en a un).</summary>
		private void PassLead(ServerPlayer from)
		{
			ServerPlayer next = null, firstBefore = null;
			bool seenSelf = false;
			foreach (var q in _players)
			{
				if (q.Team != from.Team || !q.Alive || q.IsBot || !InAir(q)) continue;
				if (q == from) { seenSelf = true; continue; }
				if (!seenSelf) firstBefore ??= q; else { next = q; break; }
			}
			next ??= firstBefore;
			if (next == null) return;
			_leaders[from.Team] = next;
			_statusTimer = 0f;             // tout le monde voit tout de suite qui dirige
			Log($"Équipe {from.Team} : {next.Name} prend le commandement du saut");
		}

		/// <summary>Un passager ne se dirige pas : il rejoue la commande de son chef (et saute avec lui), décalé sur le côté pour ne pas tomber au même point.</summary>
		private void PassengerStep(ServerPlayer p, ServerPlayer lead, float dt)
		{
			var inp = lead.LastInput;
			inp.Seq = p.LastProcessedSeq;
			bool wasPlane = p.State.Stance == Stance.Plane;
			if (wasPlane && lead.State.Stance == Stance.Diving) inp.ButtonBits |= (ushort)Buttons.Jump;     // le chef a sauté : toute l'équipe saute
			else inp.ButtonBits &= unchecked((ushort)~(ushort)Buttons.Jump);
			PlayerMovement.Step(ref p.State, inp, _world, dt, 1f);
			p.Yaw = inp.Yaw;
			p.LastButtons = inp.Buttons;
			if (p.State.Stance == Stance.Diving && lead.State.Stance == Stance.Diving)
			{
				// formation : les coéquipiers restent de part et d'autre du chef (3 m d'écart) pendant toute la chute. On recale leur position sur celle du chef à chaque tick :
				// rejouer seulement ses commandes laissait les écarts s'accumuler (entrées perdues ou groupées, collisions) et l'équipe s'éparpillait
				int rank = 0;
				foreach (var q in _players) { if (q == p) break; if (q.Team == p.Team && q != lead && InAir(q)) rank++; }
				float side = ((rank / 2) + 1) * 3f * (rank % 2 == 0 ? 1f : -1f);
				// la ligne est perpendiculaire à la trajectoire du chef (sa vitesse), pas à sa caméra : s'il regarde derrière ou de côté, l'équipe reste côte à côte
				var lv = lead.State.Vel; float hs = MathF.Sqrt(lv.X * lv.X + lv.Z * lv.Z);
				var right = hs > 1f ? new Vec3(-lv.Z / hs, 0f, lv.X / hs) : new Vec3(MathF.Cos(inp.Yaw), 0f, -MathF.Sin(inp.Yaw));
				var pos = lead.State.Pos + right * side;
				if (_world.HeightAt(pos.X, pos.Z) < lead.State.Pos.Y - 1f) { p.State.Pos = pos; p.State.Vel = lead.State.Vel; }
			}
			if (p.State.Stance == Stance.Diving) p.Pitch = PlayerMovement.DivePitch(PlayerMovement.DiveAmount(inp.Pitch, inp.Forward > 12));
		}

		// ------------------------------------------------------------------ bots dans l'avion

		/// <summary>Un bot dans l'avion saute à l'instant qu'il a choisi (ou quand un coéquipier humain saute), puis plonge vers son point d'atterrissage.</summary>
		private void ThinkBotAir(ServerPlayer p, BotBrain b, float dt)
		{
			var st = p.State;
			var buttons = Buttons.None;
			float pitch = b.Pitch;
			if (st.Stance == Stance.Plane)
			{
				if (st.FlightT >= b.JumpAt) buttons |= Buttons.Jump;       // les coéquipiers d'un bot chef de saut sautent avec lui (PassengerStep)
			}
			else
			{
				Vec3 target = b.LandTarget;
				float dx = target.X - st.Pos.X, dz = target.Z - st.Pos.Z;
				float dist = MathF.Sqrt(dx * dx + dz * dz);
				float alt = Math.Max(5f, st.Pos.Y - _world.HeightAt(target.X, target.Z));
				// l'inclinaison qui amène juste au-dessus du but : temps de chute = distance / vitesse horizontale
				// « avancer » plonge (au moins 72 %), sinon on reste à plat ventre : le bot choisit entre les deux, plus les piqués plus raides
				float bestT = 0f, bestErr = float.MaxValue;
				for (int i = -1; i <= 10; i++)
				{
					float t = i < 0 ? 0f : 0.72f + 0.28f * (i / 10f);
					float fall = Sim.DiveFallMin + (Sim.DiveFallMax - Sim.DiveFallMin) * t;
					float glide = Sim.DiveGlideMax + (Sim.DiveGlideMin - Sim.DiveGlideMax) * t;
					float err = MathF.Abs(dist - glide * alt / fall);
					if (err < bestErr) { bestErr = err; bestT = t; }
				}
				float desiredYaw = MathF.Atan2(-dx, -dz);
				float turn = 3.5f * dt;
				b.Yaw += Math.Clamp(WrapAngle(desiredYaw - b.Yaw), -turn, turn);
				pitch = -bestT * 1.4f;
				b.Pitch = pitch;
				b.DiveForward = bestT > 0f;
			}
			p.EnqueueInput(PlayerInput.Create(++b.Seq, st.Stance == Stance.Diving && b.DiveForward ? 1f : 0f, 0f, buttons, 0, b.Yaw, pitch, _tick));
		}
	}
}
