using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Les modes de jeu autres que le battle royale : Deathmatch, Team Deathmatch, Capture du drapeau, Sabotage et Contrôle.
	/// Ils ont la même vie de partie que le battle royale (salle d'attente, départ, fin) mais sur une petite carte dédiée, avec classes d'équipement
	/// choisies à la réapparition (ou boutique en Sabotage) et des règles propres, codées dans une sous-classe de <see cref="ModeLogic"/>.
	/// </summary>
	public sealed partial class GameServer
	{
		private ModeLogic _mode;
		private bool Modal => _mode != null;
		/// <summary>Une partie avec salle d'attente, départ et fin : battle royale ou mode à objectif.</summary>
		private bool Phased => Royale || Modal;
		public GameMode CurrentMode => _opt.Mode;
		/// <summary>Scores des deux équipes (modes à deux équipes) pour les tests et la télémétrie.</summary>
		public int ScoreOf(int team) => _mode == null ? 0 : _mode.Score[Math.Clamp(team, 0, 1)];
		public bool IsModal => Modal;
		/// <summary>État interne du mode en texte (tests, diagnostic).</summary>
		public string ModeDebug() => _mode == null ? "" : _mode.Debug();

		private static ModeLogic CreateMode(GameServer s, GameMode mode)
		{
			var info = Modes.Get(mode);
			return mode switch
			{
				GameMode.Deathmatch => new ScoreMode(s, info, ffa: true),
				GameMode.TeamDeathmatch => new ScoreMode(s, info, ffa: false),
				GameMode.Control => new ControlMode(s, info),
				GameMode.CaptureTheFlag => new CtfMode(s, info),
				GameMode.Sabotage => new SabotageMode(s, info),
				_ => null,
			};
		}

		// ------------------------------------------------------------------ équipes, équipement, apparition

		private int JoinTeam()
		{
			if (Modal && _mode.Info.TwoTeams)
			{
				int c0 = 0, c1 = 0;
				foreach (var q in _players) { if (q.Team == 0) c0++; else if (q.Team == 1) c1++; }
				return c1 < c0 ? 1 : 0;
			}
			return _teams.Join();
		}

		private void LeaveTeam(byte team)
		{
			if (!(Modal && _mode.Info.TwoTeams)) _teams.Leave(team);
		}

		/// <summary>Donne à un joueur l'équipement de sa classe (sac, armes, munitions, bouclier, soins).</summary>
		private void ApplyKit(ServerPlayer p, ClassKit k)
		{
			var inv = new InventoryState();
			var none = new List<LootItem>();
			var bag = LootItem.Of(ItemIds.Backpack3); inv.TryPickup(ref bag, none);
			void AddAmmoFor(byte weapon)
			{
				var wd = WeaponDefs.Get(weapon);
				if (wd == null || wd.AmmoType == 0 || wd.MagazineSize <= 0) return;
				var ammoId = ItemDefs.AmmoItemFor(wd.AmmoType);
				if (ammoId == ItemIds.None) return;
				int total = wd.MagazineSize * k.ReserveMagazines;
				for (int guard = 0; total > 0 && guard < 20; guard++)
				{
					var a = LootItem.Of(ammoId); a.Count = (ushort)Math.Min(total, ItemDefs.Get(ammoId).StackSize);
					int before = a.Count;
					inv.TryPickup(ref a, none);
					total -= before - a.Count;
					if (a.Count == before) break;       // plus de place
				}
			}
			AddAmmoFor(k.Primary); AddAmmoFor(k.Secondary);
			if (k.Secondary != ItemIds.None) { var w2 = LootItem.Of(k.Secondary); inv.TryPickup(ref w2, none); }
			if (k.Primary != ItemIds.None) { var w1 = LootItem.Of(k.Primary); w1.Optic = k.PrimaryOptic; inv.TryPickup(ref w1, none); }
			for (int i = 0; i < inv.Weapons.Length; i++) if (inv.Weapons[i] != null && inv.Weapons[i].WeaponId == k.Primary) inv.ActiveSlot = i;
			if (k.HealSmall > 0) { var h = LootItem.Of(ItemIds.HealSmall); h.Count = (ushort)k.HealSmall; inv.TryPickup(ref h, none); }
			if (k.ShieldSmall > 0) { var h = LootItem.Of(ItemIds.ShieldSmall); h.Count = (ushort)k.ShieldSmall; inv.TryPickup(ref h, none); }
			inv.ShieldTier = k.ShieldTier;
			p.Inventory = inv;
			p.Shield = k.ShieldTier >= 0 ? Consumables.ShieldMaxOf(k.ShieldTier) : 0f;
			p.UseId = 0; p.Reloading = false;
			SendInventory(p);
		}

		private void ChooseClass(ServerPlayer p, int id)
		{
			p.ClassId = (byte)Math.Clamp(id, 0, Loadouts.Kits.Length - 1);
			// en salle d'attente, le changement est immédiat ; en partie il prend effet à la prochaine réapparition (le mode peut en décider autrement)
			if (Phase == MatchPhase.Lobby) ApplyKit(p, Loadouts.Get(p.ClassId));
		}

		private Vec3 ModalSpawn(ServerPlayer p) => _mode.Spawn(p);

		/// <summary>Tue un joueur (tests, administration) : passe par la même chaîne que les tirs (score, argent, réapparition). À appeler depuis le thread du serveur.</summary>
		public void AdminKill(int killerId, int victimId)
		{
			var v = GetPlayer(victimId); var k = killerId >= 0 ? GetPlayer(killerId) : null;
			if (v != null && v.Alive) Kill(k, v, 0);
		}

		// ------------------------------------------------------------------ cycle de la partie

		private void StartModalMatch()
		{
			Phase = MatchPhase.Playing;
			_countdown = -1f;
			if (_opt.FillBots)
			{
				int guard = 0;
				while (_players.Count < BotFillTarget && (_opt.MaxBots < 0 || BotCount < _opt.MaxBots) && guard++ < 200) { int before = _players.Count; AddBot(); if (_players.Count == before) break; }
			}
			foreach (var p in _players)
			{
				p.Alive = true; p.Health = 100f; p.Kills = 0; p.DamageDealt = 0f; p.Placement = 0; p.LastAttacker = null;
				if (p.Brain is BotBrain bb) p.ClassId = (byte)bb.Rng.Next(Loadouts.Kits.Length);
				p.ProtectUntil = 0;
			}
			_mode.OnStart();
			BroadcastRoster();
			Log($"Départ de la partie {Modes.Label(_opt.Mode, _m.TeamSize)} : {_players.Count} joueurs ({_humans.Count} humains, {BotCount} bots)");
			SendStatusToAll();
		}

		private void TickModal(float dt)
		{
			_mode.Tick(dt);
		}

		/// <summary>Termine la partie : l'équipe gagnante (255 = égalité).</summary>
		private void EndModal(byte winnerTeam)
		{
			if (Phase != MatchPhase.Playing) return;
			Phase = MatchPhase.Over;
			_overTimer = 0f;
			_winnerTeam = winnerTeam;
			foreach (var p in _players) p.Placement = (byte)(winnerTeam != 255 && p.Team == winnerTeam ? 1 : 2);
			Log(winnerTeam == 255 ? "Fin de la partie : égalité" : $"Fin de la partie : l'équipe {winnerTeam} gagne");
			SendStatusToAll();
		}

		/// <summary>Un joueur meurt dans un mode à objectif : score, fil des éliminations, réapparition éventuelle.</summary>
		private void ModalKill(ServerPlayer killer, ServerPlayer victim, byte weaponId)
		{
			if (!victim.Alive) return;
			TotalKills++;
			victim.Alive = false;
			victim.Health = 0f; victim.Shield = 0f;
			victim.UseId = 0; victim.Reloading = false;
			victim.State.Vel = Vec3.Zero;
			victim.DiedThisRound = true;
			var delay = _mode.RespawnDelay(victim);
			victim.RespawnTick = delay > 0f ? _tick + (uint)(delay * _m.TickRate) : uint.MaxValue;
			_mode.OnKill(killer, victim, weaponId);
			var feed = new ServerEvent { Type = EventType.KillFeed, A = killer?.Id ?? victim.Id, B = victim.Id, C = weaponId };
			for (int i = 0; i < _players.Count; i++) SendEvent(_players[i], feed);
		}

		private void ModalRespawns()
		{
			if (Phase != MatchPhase.Playing) return;
			for (int i = 0; i < _players.Count; i++)
			{
				var p = _players[i];
				if (p.Alive || _tick < p.RespawnTick) continue;
				RespawnPlayer(p);
			}
		}

		private void RespawnPlayer(ServerPlayer p)
		{
			p.Alive = true;
			p.Health = 100f;
			p.LastAttacker = null;
			p.State = PlayerMovement.Spawn(_mode.Spawn(p), _world);
			ApplyKit(p, _mode.KitFor(p));
			p.ProtectUntil = _tick + (uint)(GameBalance.F("mode.respawnProtection") * _m.TickRate);
			p.ShotCooldown = 0f;
			SendEvent(p, new ServerEvent { Type = EventType.Respawned, Pos = p.State.Pos });
		}

		private void SendModeStates()
		{
			if (!Modal) return;
			for (int i = 0; i < _humans.Count; i++)
			{
				var p = _humans[i];
				if (p.Peer == null) continue;
				var st = _mode.BuildState(p);
				_tmp.Reset();
				st.Write(_tmp);
				p.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, LiteNetLib.DeliveryMethod.ReliableOrdered);
			}
		}

		private void HandleModeCommand(ServerPlayer p, CommandType type, int arg)
		{
			if (!Modal) return;
			if (type == CommandType.SelectClass) ChooseClass(p, arg);
			else if (type == CommandType.Buy) _mode.OnBuy(p, arg);
		}

		// ------------------------------------------------------------------ la logique d'un mode

		private abstract class ModeLogic
		{
			protected readonly GameServer S;
			public readonly ModeInfo Info;
			public readonly int[] Score = new int[2];
			public float TimeLeft;
			protected ModeLogic(GameServer s, ModeInfo info) { S = s; Info = info; }

			protected IEnumerable<ServerPlayer> Players => S._players;
			protected IEnumerable<MapPoint> Points(MapPointKind kind) => S._data.PointsOf(kind);

			public virtual void OnStart() { }
			public virtual string Debug() => Info.Key;
			public abstract void Tick(float dt);
			public virtual void OnKill(ServerPlayer killer, ServerPlayer victim, byte weapon) { }
			public virtual bool CanDamage(ServerPlayer attacker, ServerPlayer victim) => attacker == null || victim.ProtectUntil <= S._tick;
			public virtual bool Frozen(ServerPlayer p) => false;
			public virtual float SpeedScale(ServerPlayer p) => 1f;
			public virtual float RespawnDelay(ServerPlayer p) => Info.RespawnSeconds;
			public virtual ClassKit KitFor(ServerPlayer p) => Loadouts.Get(p.ClassId);
			public virtual void OnBuy(ServerPlayer p, int code) { }
			public virtual void OnPlayerLeft(ServerPlayer p) { }
			public virtual int SideOf(ServerPlayer p) => p.Team;
			public virtual void FillState(ref ModeState st, ServerPlayer viewer) { }
			/// <summary>Un but pour un bot qui ne se bat pas : où aller, à quelle distance il est arrivé, et les touches à tenir une fois arrivé.</summary>
			public virtual bool BotGoal(ServerPlayer p, BotBrain b, out Vec3 goal, out float arrive, out Buttons hold) { goal = default; arrive = 2f; hold = Buttons.None; return false; }

			public ModeState BuildState(ServerPlayer viewer)
			{
				var st = ModeState.Empty;
				st.Mode = Info.Mode; st.MySide = (byte)SideOf(viewer);
				st.Score0 = (ushort)Score[0]; st.Score1 = (ushort)Score[1];
				st.TimeLeft = (ushort)Math.Clamp((int)Math.Ceiling(TimeLeft), 0, 65535);
				st.WinnerTeam = S._winnerTeam;
				st.ClassId = viewer.ClassId; st.Money = (ushort)Math.Clamp(viewer.Money, 0, 65535); st.HasKit = viewer.HasKit;
				st.Invulnerable = viewer.ProtectUntil > S._tick;
				FillState(ref st, viewer);
				return st;
			}

			// ---- outils communs
			public Vec3 Spawn(ServerPlayer p)
			{
				var list = Info.Mode == GameMode.Deathmatch ? Points(MapPointKind.SpawnFfa).ToList() : Points(MapPointKind.SpawnTeam).Where(pt => pt.Team == SideOf(p)).ToList();
				if (list.Count == 0) return S.PickSpawn();
				// le point le plus loin des adversaires en vie (au hasard parmi les meilleurs)
				MapPoint best = list[0]; float bestScore = -1f;
				foreach (var pt in list)
				{
					float near = float.MaxValue;
					foreach (var q in Players) if (q != p && q.Alive && q.Team != p.Team) near = MathF.Min(near, Vec3.DistanceSquared(q.State.Pos, pt.Pos));
					float score = near + (float)S._rng.NextDouble() * 25f;
					if (score > bestScore) { bestScore = score; best = pt; }
				}
				var jitter = new Vec3((float)(S._rng.NextDouble() * 2 - 1) * 2f, 0, (float)(S._rng.NextDouble() * 2 - 1) * 2f);
				var pos = best.Pos + jitter;
				return new Vec3(pos.X, S._world.HeightAt(pos.X, pos.Z), pos.Z);
			}

			public static float Flat(Vec3 a, Vec3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }
			protected bool Near(ServerPlayer p, Vec3 pos, float radius) => p.Alive && Flat(p.State.Pos, pos) <= radius && MathF.Abs(p.State.Pos.Y - pos.Y) < 3f;
		}

		// ================================================================== Deathmatch et Team Deathmatch

		private sealed class ScoreMode : ModeLogic
		{
			private readonly bool _ffa;
			public ScoreMode(GameServer s, ModeInfo info, bool ffa) : base(s, info) { _ffa = ffa; }
			private int Target => (int)GameBalance.F(_ffa ? "mode.dmKills" : "mode.tdmKills");

			public override void OnStart()
			{
				Score[0] = Score[1] = 0;
				TimeLeft = GameBalance.F(_ffa ? "mode.dmTime" : "mode.tdmTime");
				foreach (var p in Players) { p.State = PlayerMovement.Spawn(Spawn(p), S._world); S.ApplyKit(p, Loadouts.Get(p.ClassId)); p.ProtectUntil = S._tick + (uint)(GameBalance.F("mode.respawnProtection") * S._m.TickRate); }
			}

			public override void Tick(float dt)
			{
				TimeLeft -= dt;
				if (TimeLeft > 0f) return;
				TimeLeft = 0f;
				if (_ffa)
				{
					ServerPlayer top = null; bool tie = false;
					foreach (var p in Players) { if (top == null || p.Kills > top.Kills) { top = p; tie = false; } else if (p.Kills == top.Kills) tie = true; }
					S.EndModal(top == null || tie ? (byte)255 : top.Team);
				}
				else S.EndModal(Score[0] == Score[1] ? (byte)255 : (byte)(Score[0] > Score[1] ? 0 : 1));
			}

			public override void OnKill(ServerPlayer killer, ServerPlayer victim, byte weapon)
			{
				if (killer == null || killer == victim || killer.Team == victim.Team) return;
				killer.Kills++;
				if (_ffa) { if (killer.Kills >= Target) S.EndModal(killer.Team); return; }
				Score[killer.Team]++;
				if (Score[killer.Team] >= Target) S.EndModal(killer.Team);
			}

			public override void FillState(ref ModeState st, ServerPlayer viewer)
			{
				st.Target = (ushort)Target;
				if (_ffa) { st.MyScore = (ushort)viewer.Kills; int top = 0; foreach (var p in Players) top = Math.Max(top, p.Kills); st.TopScore = (ushort)top; }
			}
		}

		// ================================================================== Contrôle

		private sealed class ControlMode : ModeLogic
		{
			private MapPoint[] _pts = new MapPoint[5];
			private readonly float[] _progress = new float[5];
			private readonly byte[] _owner = { 255, 255, 255, 255, 255 };
			private readonly bool[] _contested = new bool[5];
			private float _scoreTimer;
			public ControlMode(GameServer s, ModeInfo info) : base(s, info) { }

			public override void OnStart()
			{
				var list = Points(MapPointKind.ControlPoint).OrderBy(pt => pt.Index).ToList();
				for (int i = 0; i < 5; i++)
				{
					_pts[i] = i < list.Count ? list[i] : default;
					_owner[i] = i < list.Count ? list[i].Team : (byte)255;
					_progress[i] = _owner[i] == 0 ? -100f : _owner[i] == 1 ? 100f : 0f;
				}
				Score[0] = Score[1] = 0; _scoreTimer = 0f;
				TimeLeft = GameBalance.F("mode.controlTime");
				foreach (var p in Players) { p.State = PlayerMovement.Spawn(Spawn(p), S._world); S.ApplyKit(p, Loadouts.Get(p.ClassId)); p.ProtectUntil = S._tick + (uint)(GameBalance.F("mode.respawnProtection") * S._m.TickRate); }
			}

			public int Held(int team) { int n = 0; for (int i = 0; i < 5; i++) if (_owner[i] == team) n++; return n; }

			public override void Tick(float dt)
			{
				TimeLeft -= dt;
				float capture = MathF.Max(0.5f, GameBalance.F("mode.controlCaptureSeconds"));
				for (int i = 0; i < 5; i++)
				{
					var pt = _pts[i];
					if (pt.Radius <= 0f) continue;
					int n0 = 0, n1 = 0;
					foreach (var p in Players) if (Near(p, pt.Pos, pt.Radius)) { if (p.Team == 0) n0++; else if (p.Team == 1) n1++; }
					_contested[i] = n0 > 0 && n1 > 0;
					if (_contested[i] || (n0 == 0 && n1 == 0)) continue;
					int n = Math.Min(4, n0 > 0 ? n0 : n1);
					float rate = 100f / capture * (1f + 0.5f * (n - 1));
					_progress[i] = Math.Clamp(_progress[i] + (n0 > 0 ? -rate : rate) * dt, -100f, 100f);
					if (_progress[i] <= -100f) _owner[i] = 0;
					else if (_progress[i] >= 100f) _owner[i] = 1;
					else if (_owner[i] == 0 && _progress[i] >= 0f) _owner[i] = 255;       // le point change de main : d'abord neutre
					else if (_owner[i] == 1 && _progress[i] <= 0f) _owner[i] = 255;
				}
				_scoreTimer += dt;
				float step = MathF.Max(0.2f, GameBalance.F("mode.controlTickSeconds"));
				while (_scoreTimer >= step)
				{
					_scoreTimer -= step;
					int h0 = Held(0), h1 = Held(1);
					if (h0 > h1) Score[0] += h0 - h1; else if (h1 > h0) Score[1] += h1 - h0;
				}
				int target = (int)GameBalance.F("mode.controlScore");
				if (Score[0] >= target || Score[1] >= target) { S.EndModal((byte)(Score[0] >= Score[1] ? 0 : 1)); return; }
				if (TimeLeft <= 0f) { TimeLeft = 0f; S.EndModal(Score[0] == Score[1] ? (byte)255 : (byte)(Score[0] > Score[1] ? 0 : 1)); }
			}

			public override void FillState(ref ModeState st, ServerPlayer viewer)
			{
				st.Target = (ushort)GameBalance.F("mode.controlScore");
				for (int i = 0; i < 5; i++) st.Points[i] = new ControlPointState { Owner = _owner[i], Progress = (sbyte)Math.Clamp((int)_progress[i], -100, 100), Contested = _contested[i] };
			}

			public override bool BotGoal(ServerPlayer p, BotBrain b, out Vec3 goal, out float arrive, out Buttons hold)
			{
				hold = Buttons.None; arrive = 3f; goal = default;
				// les points que son équipe ne tient pas ; chaque bot en vise un différent (selon son numéro)
				var wanted = new List<int>();
				for (int i = 0; i < 5; i++) if (_pts[i].Radius > 0f && _owner[i] != p.Team) wanted.Add(i);
				int pick;
				if (wanted.Count > 0) pick = wanted[(p.Id + (int)(S._tick / 1800)) % wanted.Count];
				else { var mine = Enumerable.Range(0, 5).Where(i => _pts[i].Radius > 0f).ToList(); if (mine.Count == 0) return false; pick = mine[p.Id % mine.Count]; }
				var pt = _pts[pick];
				float ang = p.Id * 2.3999632f;
				goal = pt.Pos + new Vec3(MathF.Cos(ang), 0, MathF.Sin(ang)) * MathF.Min(pt.Radius * 0.5f, 3f);
				arrive = 1.2f;
				return true;
			}
		}

		// ================================================================== Capture du drapeau

		private sealed class CtfMode : ModeLogic
		{
			private readonly Vec3[] _home = new Vec3[2];
			private readonly byte[] _state = new byte[2];
			private readonly ServerPlayer[] _carrier = new ServerPlayer[2];
			private readonly Vec3[] _pos = new Vec3[2];
			private readonly float[] _dropTimer = new float[2];
			public CtfMode(GameServer s, ModeInfo info) : base(s, info) { }

			public override void OnStart()
			{
				foreach (var pt in Points(MapPointKind.FlagBase)) if (pt.Team < 2) _home[pt.Team] = pt.Pos;
				for (int t = 0; t < 2; t++) { _state[t] = 0; _carrier[t] = null; _pos[t] = _home[t]; }
				Score[0] = Score[1] = 0;
				TimeLeft = GameBalance.F("mode.ctfTime");
				foreach (var p in Players) { p.State = PlayerMovement.Spawn(Spawn(p), S._world); S.ApplyKit(p, Loadouts.Get(p.ClassId)); p.ProtectUntil = S._tick + (uint)(GameBalance.F("mode.respawnProtection") * S._m.TickRate); }
			}

			public override float SpeedScale(ServerPlayer p) => (_carrier[0] == p || _carrier[1] == p) ? GameBalance.F("mode.flagCarrierSpeed") : 1f;

			private void ReturnHome(int team) { _state[team] = 0; _carrier[team] = null; _pos[team] = _home[team]; _dropTimer[team] = 0f; }

			public override void Tick(float dt)
			{
				TimeLeft -= dt;
				const float reach = 1.8f;
				for (int flag = 0; flag < 2; flag++)
				{
					if (_state[flag] == 1)
					{
						var c = _carrier[flag];
						if (c == null || !c.Alive) { DropFlag(flag, c?.State.Pos ?? _pos[flag]); }
						else _pos[flag] = c.State.Pos;
					}
					if (_state[flag] == 2)
					{
						_dropTimer[flag] -= dt;
						if (_dropTimer[flag] <= 0f) { ReturnHome(flag); continue; }
					}
					if (_state[flag] == 1) continue;
					foreach (var p in Players)
					{
						if (!Near(p, _pos[flag], reach)) continue;
						if (p.Team == flag)
						{
							if (_state[flag] == 2) ReturnHome(flag);            // son propre drapeau tombé : on le ramène
						}
						else
						{
							// drapeau adverse (à sa base ou tombé) : on le prend
							_state[flag] = 1; _carrier[flag] = p; _dropTimer[flag] = 0f;
							break;
						}
					}
				}
				// capture : porter le drapeau adverse jusqu'à son propre drapeau, resté à sa base
				for (int flag = 0; flag < 2; flag++)
				{
					var c = _carrier[flag];
					if (_state[flag] != 1 || c == null || !c.Alive) continue;
					int mine = c.Team;
					if (_state[mine] == 0 && Near(c, _home[mine], reach + 0.8f))
					{
						Score[mine]++;
						ReturnHome(flag);
						if (Score[mine] >= (int)GameBalance.F("mode.ctfCaptures")) { S.EndModal((byte)mine); return; }
					}
				}
				if (TimeLeft <= 0f) { TimeLeft = 0f; S.EndModal(Score[0] == Score[1] ? (byte)255 : (byte)(Score[0] > Score[1] ? 0 : 1)); }
			}

			private void DropFlag(int flag, Vec3 pos)
			{
				_state[flag] = 2; _carrier[flag] = null;
				_pos[flag] = new Vec3(pos.X, S._world.HeightAt(pos.X, pos.Z), pos.Z);
				_dropTimer[flag] = GameBalance.F("mode.flagReturnSeconds");
			}

			public override void OnKill(ServerPlayer killer, ServerPlayer victim, byte weapon)
			{
				for (int flag = 0; flag < 2; flag++) if (_carrier[flag] == victim) DropFlag(flag, victim.State.Pos);
			}

			public override void OnPlayerLeft(ServerPlayer p)
			{
				for (int flag = 0; flag < 2; flag++) if (_carrier[flag] == p) DropFlag(flag, p.State.Pos);
			}

			public override void FillState(ref ModeState st, ServerPlayer viewer)
			{
				st.Target = (ushort)GameBalance.F("mode.ctfCaptures");
				st.Flag0 = new FlagState { State = _state[0], Carrier = _carrier[0]?.Id ?? (ushort)0xFFFF, Pos = _pos[0] };
				st.Flag1 = new FlagState { State = _state[1], Carrier = _carrier[1]?.Id ?? (ushort)0xFFFF, Pos = _pos[1] };
			}

			public override bool BotGoal(ServerPlayer p, BotBrain b, out Vec3 goal, out float arrive, out Buttons hold)
			{
				hold = Buttons.None; arrive = 2.5f;
				int mine = p.Team, theirs = 1 - mine;
				bool carrying = _carrier[theirs] == p;
				if (carrying) { goal = _home[mine]; arrive = 1f; return true; }             // il rapporte le drapeau
				if (_state[mine] == 2) { goal = _pos[mine]; arrive = 1f; return true; }      // son drapeau est tombé : on le ramène
				bool attacker = p.Id % 2 == 0;
				if (attacker) { goal = _pos[theirs]; arrive = 1f; return true; }            // attaquants : vers le drapeau adverse
				// défenseurs : ils gardent la base ; si l'ennemi porte notre drapeau, ils le poursuivent
				if (_state[mine] == 1 && _carrier[mine] != null) { goal = _carrier[mine].State.Pos; arrive = 2f; return true; }
				float ang = p.Id * 2.3999632f;
				goal = _home[mine] + new Vec3(MathF.Cos(ang), 0, MathF.Sin(ang)) * 6f;
				arrive = 2.5f;
				return true;
			}
		}

		// ================================================================== Sabotage

		private sealed class SabotageMode : ModeLogic
		{
			private SabotageStage _stage;
			private float _stageTimer;
			private int _round, _swapped;
			private readonly int[] _lossStreak = new int[2];
			private byte _bombState;                    // 0 portée, 1 au sol, 2 posée, 3 désamorcée, 4 explosée
			private ServerPlayer _carrier;
			private Vec3 _bombPos;
			private int _site;
			private float _bombTimer, _action;
			private byte _actionKind;                   // 1 pose, 2 désamorçage
			private byte _roundWinner = 255;
			private int _botSite;
			private MapPoint[] _sites = new MapPoint[2];

			public SabotageMode(GameServer s, ModeInfo info) : base(s, info) { }

			private int RoundsToWin => (int)GameBalance.F("sabotage.roundsToWin");
			/// <summary>Camp d'un joueur : 1 = attaquants, 0 = défenseurs. Les bleus attaquent d'abord ; les camps s'échangent à la mi-temps.</summary>
			public override int SideOf(ServerPlayer p) => (p.Team == 0 ? 1 : 0) ^ _swapped;
			private int TeamOfSide(int side) => (side ^ _swapped) == 1 ? 0 : 1;

			public override string Debug() => $"stage={_stage} timer={_stageTimer:F1} carrier={_carrier?.Id} carrierHold={_carrier?.Holding} carrierPos={_carrier?.State.Pos} bomb={_bombState} sites=({_sites[0].Pos},{_sites[0].Radius})/({_sites[1].Pos},{_sites[1].Radius}) action={_actionKind}/{_action:F2}";
			public override bool Frozen(ServerPlayer p) => _stage == SabotageStage.Buy;
			public override bool CanDamage(ServerPlayer attacker, ServerPlayer victim) => _stage == SabotageStage.Round || _stage == SabotageStage.Planted;
			public override float RespawnDelay(ServerPlayer p) => 0f;

			public override void OnStart()
			{
				var sites = Points(MapPointKind.BombSite).OrderBy(pt => pt.Index).ToList();
				for (int i = 0; i < 2; i++) _sites[i] = i < sites.Count ? sites[i] : (sites.Count > 0 ? sites[0] : default);
				Score[0] = Score[1] = 0; _round = 0; _swapped = 0; _lossStreak[0] = _lossStreak[1] = 0;
				foreach (var p in Players) { p.Money = (int)GameBalance.F("sabotage.startMoney"); p.HasKit = false; p.DiedThisRound = true; }
				StartRound();
			}

			private void StartRound()
			{
				_round++;
				// mi-temps : on échange les camps, argent et équipement repartent de zéro
				bool half = _round == RoundsToWin;     // après (RoundsToWin - 1) manches jouées
				if (half)
				{
					_swapped ^= 1; _lossStreak[0] = _lossStreak[1] = 0;
					foreach (var p in Players) { p.Money = (int)GameBalance.F("sabotage.startMoney"); p.HasKit = false; p.DiedThisRound = true; }
				}
				_stage = SabotageStage.Buy; _stageTimer = GameBalance.F("sabotage.buySeconds");
				_bombState = 0; _site = 0; _action = 0; _actionKind = 0; _roundWinner = 255; _bombTimer = 0f;
				var attackers = new List<ServerPlayer>();
				foreach (var p in Players)
				{
					p.Alive = true; p.Health = 100f; p.LastAttacker = null; p.UseId = 0; p.Reloading = false; p.ProtectUntil = 0;
					p.State = PlayerMovement.Spawn(Spawn(p), S._world);
					if (p.DiedThisRound) { PistolOnly(p); p.HasKit = false; }
					else { p.Shield = MathF.Max(p.Shield, 0f); S.SendInventory(p); }
					p.DiedThisRound = false;
					if (SideOf(p) == 1) attackers.Add(p);
					if (p.Brain is BotBrain) BotBuy(p);
				}
				_carrier = attackers.Count > 0 ? attackers[S._rng.Next(attackers.Count)] : null;
				_bombPos = _carrier?.State.Pos ?? default;
				_botSite = S._rng.Next(2);
				GameServer.Log($"Sabotage : manche {_round} ({Score[0]}-{Score[1]})");
			}

			private void PistolOnly(ServerPlayer p)
			{
				var kit = new ClassKit { Id = 255, Primary = ItemIds.C19, Secondary = ItemIds.None, ReserveMagazines = 3, ShieldTier = -1 };
				S.ApplyKit(p, kit);
			}

			public override ClassKit KitFor(ServerPlayer p) => Loadouts.Get(p.ClassId);

			private int Alive(int side) { int n = 0; foreach (var p in Players) if (p.Alive && SideOf(p) == side) n++; return n; }

			public override void Tick(float dt)
			{
				_stageTimer -= dt;
				switch (_stage)
				{
					case SabotageStage.Buy:
						TimeLeft = _stageTimer;
						if (_stageTimer <= 0f) { _stage = SabotageStage.Round; _stageTimer = GameBalance.F("sabotage.roundSeconds"); }
						break;
					case SabotageStage.Round:
						TimeLeft = _stageTimer;
						TickBomb(dt);
						if (_stage != SabotageStage.Round) break;
						if (Alive(0) == 0) EndRound(1);
						else if (Alive(1) == 0) EndRound(0);
						else if (_stageTimer <= 0f) EndRound(0);        // le temps est écoulé sans pose : les défenseurs gagnent
						break;
					case SabotageStage.Planted:
						TimeLeft = _bombTimer;
						TickBomb(dt);
						if (_stage != SabotageStage.Planted) break;
						if (Alive(0) == 0) EndRound(1);                 // plus de défenseur : la bombe fera le reste
						break;
					case SabotageStage.RoundEnd:
						TimeLeft = _stageTimer;
						if (_stageTimer <= 0f) StartRound();
						break;
				}
			}

			private void TickBomb(float dt)
			{
				float plantReach = 0f;
				if (_stage == SabotageStage.Round)
				{
					// bombe au sol : un attaquant la ramasse
					if (_bombState == 1) foreach (var p in Players) if (SideOf(p) == 1 && Near(p, _bombPos, 1.8f)) { _carrier = p; _bombState = 0; break; }
					if (_bombState == 0 && _carrier != null && _carrier.Alive) _bombPos = _carrier.State.Pos;
					// pose
					var c = _carrier;
					int onSite = -1;
					if (_bombState == 0 && c != null && c.Alive)
						for (int i = 0; i < 2; i++) if (_sites[i].Radius > 0f && Flat(c.State.Pos, _sites[i].Pos) <= _sites[i].Radius) onSite = i;
					if (onSite >= 0 && c.Holding)
					{
						if (_actionKind != 1) { _actionKind = 1; _action = 0f; }
						_action += dt;
						if (_action >= GameBalance.F("sabotage.plantSeconds"))
						{
							_bombState = 2; _bombPos = c.State.Pos; _site = onSite; _bombTimer = GameBalance.F("sabotage.bombSeconds");
							_stage = SabotageStage.Planted; _action = 0f; _actionKind = 0;
							GameServer.Log($"Sabotage : bombe posée sur le site {(char)('A' + onSite)}");
						}
					}
					else if (_actionKind == 1) { _actionKind = 0; _action = 0f; }
					_ = plantReach;
				}
				else if (_stage == SabotageStage.Planted)
				{
					_bombTimer -= dt;
					if (_bombTimer <= 0f) { _bombState = 4; EndRound(1); return; }
					ServerPlayer defuser = null;
					foreach (var p in Players) if (SideOf(p) == 0 && p.Holding && Near(p, _bombPos, 2.5f)) { defuser = p; break; }
					if (defuser != null)
					{
						if (_actionKind != 2) { _actionKind = 2; _action = 0f; }
						_action += dt;
						float need = GameBalance.F(defuser.HasKit ? "sabotage.defuseKitSeconds" : "sabotage.defuseSeconds");
						if (_action >= need) { _bombState = 3; _actionKind = 0; _action = 0f; EndRound(0); }
					}
					else if (_actionKind == 2) { _actionKind = 0; _action = 0f; }
				}
			}

			private void EndRound(int winnerSide)
			{
				if (_stage == SabotageStage.RoundEnd) return;
				_roundWinner = (byte)winnerSide;
				_stage = SabotageStage.RoundEnd; _stageTimer = GameBalance.F("sabotage.endSeconds");
				int winTeam = TeamOfSide(winnerSide), loseTeam = 1 - winTeam;
				Score[winTeam]++;
				bool planted = _bombState >= 2;
				// argent : les gagnants touchent la prime, les perdants une prime qui monte à chaque défaite de suite
				_lossStreak[winTeam] = 0;
				_lossStreak[loseTeam] = Math.Min(4, _lossStreak[loseTeam] + 1);
				int lossMoney = Math.Min((int)GameBalance.F("sabotage.lossBonusMax"), (int)GameBalance.F("sabotage.lossMoney") + (int)GameBalance.F("sabotage.lossBonus") * (_lossStreak[loseTeam] - 1));
				foreach (var p in Players)
				{
					int gain = p.Team == winTeam ? (int)GameBalance.F("sabotage.winMoney") : lossMoney;
					if (planted && SideOf(p) == 1) gain += (int)GameBalance.F("sabotage.plantMoney");
					p.Money = Math.Min((int)GameBalance.F("sabotage.maxMoney"), p.Money + gain);
				}
				GameServer.Log($"Sabotage : manche {_round} gagnée par {(winnerSide == 1 ? "les attaquants" : "les défenseurs")} ({Score[0]}-{Score[1]})");
				if (Score[winTeam] >= RoundsToWin) S.EndModal((byte)winTeam);
			}

			public override void OnKill(ServerPlayer killer, ServerPlayer victim, byte weapon)
			{
				if (killer != null && killer != victim && killer.Team != victim.Team)
				{
					killer.Kills++;
					killer.Money = Math.Min((int)GameBalance.F("sabotage.maxMoney"), killer.Money + (int)GameBalance.F("sabotage.killMoney"));
				}
				if (_carrier == victim && _bombState == 0) { _bombState = 1; _bombPos = victim.State.Pos; _carrier = null; }
			}

			public override void OnPlayerLeft(ServerPlayer p)
			{
				if (_carrier == p && _bombState == 0) { _bombState = 1; _bombPos = p.State.Pos; _carrier = null; }
			}

			public override void OnBuy(ServerPlayer p, int code)
			{
				if (_stage != SabotageStage.Buy || !p.Alive) return;
				var item = Shop.Get(code);
				if (item == null || p.Money < item.Price) return;
				if (item.Side != 255 && item.Side != SideOf(p)) return;
				var inv = p.Inventory;
				if (item.DefuseKit) { if (p.HasKit) return; p.HasKit = true; }
				else if (item.ShieldTier >= 0) { inv.ShieldTier = item.ShieldTier; p.Shield = Consumables.ShieldMaxOf(item.ShieldTier); }
				else if (item.ItemId != ItemIds.None)
				{
					var def = ItemDefs.Get(item.ItemId);
					if (def.Kind == ItemKind.Weapon)
					{
						var wd = WeaponDefs.Get(item.ItemId);
						// arme achetée : elle remplace l'arme tenue (l'ancienne est rendue au sol : ici simplement perdue), avec trois chargeurs
						if (inv.Weapons[0] != null && inv.Weapons[1] != null && inv.ActiveSlot >= 0) inv.Weapons[inv.ActiveSlot] = null;
						var none = new List<LootItem>();
						var w = LootItem.Of(item.ItemId); inv.TryPickup(ref w, none);
						var ammoId = wd != null && wd.AmmoType != 0 ? ItemDefs.AmmoItemFor(wd.AmmoType) : ItemIds.None;
						if (ammoId != ItemIds.None)
						{
							int total = wd.MagazineSize * 3;
							for (int g = 0; total > 0 && g < 10; g++) { var a = LootItem.Of(ammoId); a.Count = (ushort)Math.Min(total, ItemDefs.Get(ammoId).StackSize); int before = a.Count; inv.TryPickup(ref a, none); total -= before - a.Count; if (a.Count == before) break; }
						}
					}
					else if (def.Kind == ItemKind.Optic)
					{
						var wpn = inv.Active;
						if (wpn == null || !ItemDefs.Get(wpn.WeaponId).AcceptsOptic) return;
						wpn.OpticId = item.ItemId;
					}
					else
					{
						var none = new List<LootItem>();
						var it = LootItem.Of(item.ItemId);
						if (!inv.TryPickup(ref it, none)) return;
					}
				}
				p.Money -= item.Price;
				S.SendInventory(p);
			}

			private void BotBuy(ServerPlayer p)
			{
				var rng = ((BotBrain)p.Brain).Rng;
				int[] weapons = { 4, 2, 3, 5, 1 };       // codes de la boutique : AK, SMG, pompe, LMG, pistolet
				foreach (int code in weapons)
				{
					var it = Shop.Get(code);
					if (it.Price <= p.Money - (p.Money > 3000 ? 650 : 0) && (code != 5 || rng.Next(5) == 0)) { OnBuy(p, code); break; }
				}
				if (p.Money >= 650) OnBuy(p, 6);
				if (SideOf(p) == 0 && p.Money >= 400) OnBuy(p, 13);
				if (p.Money >= 100) OnBuy(p, 9);
			}

			public override void FillState(ref ModeState st, ServerPlayer viewer)
			{
				st.Stage = (byte)_stage; st.Round = (byte)_round; st.Target = (ushort)RoundsToWin; st.RoundWinner = _roundWinner;
				st.CanBuy = _stage == SabotageStage.Buy && viewer.Alive;
				st.BombState = _bombState; st.BombCarrier = _carrier?.Id ?? (ushort)0xFFFF; st.BombPos = _bombPos; st.BombTimer = _bombState == 2 ? _bombTimer : 0f; st.BombSite = (byte)_site;
				// scores par équipe (bleus 0, rouges 1) : l'interface sait qui attaque d'après MySide
				bool mine = viewer == _carrier && _actionKind == 1 || _actionKind == 2 && viewer.Holding;
				if (_actionKind != 0 && mine)
				{
					st.Action = _actionKind;
					float need = _actionKind == 1 ? GameBalance.F("sabotage.plantSeconds") : GameBalance.F(viewer.HasKit ? "sabotage.defuseKitSeconds" : "sabotage.defuseSeconds");
					st.ActionProgress = (byte)Math.Clamp((int)(_action / MathF.Max(0.1f, need) * 100f), 0, 100);
				}
			}

			public override bool BotGoal(ServerPlayer p, BotBrain b, out Vec3 goal, out float arrive, out Buttons hold)
			{
				hold = Buttons.None; arrive = 2f; goal = default;
				if (_stage == SabotageStage.Buy || _stage == SabotageStage.RoundEnd) return false;
				int side = SideOf(p);
				int site = _botSite;
				if (side == 1)
				{
					if (_bombState == 2) { goal = _bombPos + new Vec3(MathF.Cos(p.Id), 0, MathF.Sin(p.Id)) * 6f; arrive = 3f; return true; }       // la bombe est posée : on la garde
					if (_bombState == 1) { goal = _bombPos; arrive = 1f; return true; }                                                          // bombe tombée
					if (_carrier == p) { goal = _sites[site].Pos; arrive = MathF.Max(1f, _sites[site].Radius * 0.4f); hold = Buttons.Interact; return true; }
					goal = _sites[site].Pos + new Vec3(MathF.Cos(p.Id * 2.4f), 0, MathF.Sin(p.Id * 2.4f)) * 5f; arrive = 3f;
					return true;
				}
				if (_bombState == 2) { goal = _bombPos; arrive = 1.2f; hold = Buttons.Interact; return true; }                                  // désamorçage
				int mySite = p.Id % 2;
				goal = _sites[mySite].Pos + new Vec3(MathF.Cos(p.Id * 2.4f), 0, MathF.Sin(p.Id * 2.4f)) * 5f; arrive = 3f;
				return true;
			}
		}
	}
}
