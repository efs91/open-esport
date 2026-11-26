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
				GameMode.Duel => new DuelMode(s, info),
				_ => null,
			};
		}

		// ------------------------------------------------------------------ équipes, équipement, apparition

		/// <summary>Équipe du groupe de chaque joueur : les membres d'un groupe jouent ensemble.</summary>
		private readonly Dictionary<long, int> _partyTeam = new Dictionary<long, int>();

		private int JoinTeam(long partyId = 0, bool bot = false)
		{
			bool twoTeams = Modal && _mode.Info.TwoTeams;
			if (partyId != 0 && _partyTeam.TryGetValue(partyId, out int known) && _players.Any(q => !q.IsBot && q.PartyId == partyId && q.Team == known))
			{
				if (twoTeams) return known;
				if (_teams.JoinTeam(known)) return known;
			}
			int team;
			if (twoTeams)
			{
				int c0 = 0, c1 = 0;
				foreach (var q in _players) { if (q.Team == 0) c0++; else if (q.Team == 1) c1++; }
				team = c1 < c0 ? 1 : 0;
			}
			else team = partyId != 0 ? _teams.JoinReserved() : _teams.Join(allowReserved: bot);
			if (partyId != 0) _partyTeam[partyId] = team;
			return team;
		}

		private void LeaveTeam(byte team)
		{
			if (!(Modal && _mode.Info.TwoTeams)) _teams.Leave(team);
		}

		/// <summary>Donne à un joueur l'équipement de sa classe (sac, armes, munitions, bouclier, soins).</summary>
		private void ApplyKit(ServerPlayer p, ClassKit k, bool keepSlot = false)
		{
			int slot = p.Inventory?.ActiveSlot ?? -1;
			var inv = KitInventory(k);
			if (keepSlot && slot >= 0 && slot < inv.Weapons.Length && inv.Weapons[slot] != null) inv.ActiveSlot = slot;
			p.Inventory = inv;
			p.Shield = k.ShieldTier >= 0 && !k.ShieldEmpty ? Consumables.ShieldMaxOf(k.ShieldTier) : 0f;
			p.UseId = 0; p.Reloading = false;
			SendInventory(p);
		}

		/// <summary>L'inventaire complet d'une classe : sac, armes chargées, munitions de réserve, soins, grenades et niveau de bouclier.</summary>
		private static InventoryState KitInventory(ClassKit k)
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
				int total = wd.Reserve(k.ReserveMagazines);
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
			if (k.Secondary != ItemIds.None) { var w2 = LootItem.Of(k.Secondary); w2.Optic = k.SecondaryOptic; w2.Tier = k.WeaponTier; inv.TryPickup(ref w2, none); }
			if (k.Primary != ItemIds.None) { var w1 = LootItem.Of(k.Primary); w1.Optic = k.PrimaryOptic; w1.Tier = k.WeaponTier; inv.TryPickup(ref w1, none); }
			for (int i = 0; i < inv.Weapons.Length; i++) if (inv.Weapons[i] != null && inv.Weapons[i].WeaponId == k.Primary) inv.ActiveSlot = i;
			if (k.HealSmall > 0) { var h = LootItem.Of(ItemIds.HealSmall); h.Count = (ushort)k.HealSmall; inv.TryPickup(ref h, none); }
			if (k.ShieldSmall > 0) { var h = LootItem.Of(ItemIds.ShieldSmall); h.Count = (ushort)k.ShieldSmall; inv.TryPickup(ref h, none); }
			if (k.HealBig > 0) { var h = LootItem.Of(ItemIds.HealBig); h.Count = (ushort)k.HealBig; inv.TryPickup(ref h, none); }
			if (k.ShieldBig > 0) { var h = LootItem.Of(ItemIds.ShieldBig); h.Count = (ushort)k.ShieldBig; inv.TryPickup(ref h, none); }
			if (k.Grenades != null) foreach (var gid in k.Grenades) { var gi = LootItem.Of(gid); inv.TryPickup(ref gi, none); }
			inv.ShieldTier = k.ShieldTier;
			return inv;
		}

		private void ChooseClass(ServerPlayer p, int id)
		{
			if (Arena) return;
			p.ClassId = (byte)Math.Clamp(id, 0, Loadouts.Kits.Length - 1);
			// en salle d'attente, le changement est immédiat ; en partie il prend effet à la prochaine réapparition (le mode peut en décider autrement)
			if (Phase == MatchPhase.Lobby) ApplyKit(p, _mode.KitFor(p));
			else if (p.Alive && _mode.AtResupply(p)) { ApplyKit(p, _mode.KitFor(p)); p.Health = 100f; }      // à la station de sa base : tout de suite
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
			_playStartTick = _tick;
			_departed.Clear();
			_countdown = -1f;
			if (_opt.FillBots)
			{
				int guard = 0;
				while (_players.Count < BotFillTarget && (_opt.MaxBots < 0 || BotCount < _opt.MaxBots) && guard++ < 200) { int before = _players.Count; AddBot(); if (_players.Count == before) break; }
			}
			foreach (var p in _players)
			{
				p.Alive = true; p.Health = 100f; p.Kills = 0; p.Deaths = 0; p.DamageDealt = 0f; p.Placement = 0; p.LastAttacker = null; p.OutTick = 0;
				if (p.Brain is BotBrain bb) p.ClassId = (byte)bb.Rng.Next(Loadouts.Kits.Length);
				p.ProtectUntil = 0;
			}
			_mode.OnStart();
			BroadcastRoster();
			// partie de tournoi entre inscrits : un camp qui ne s'est pas présenté perd par forfait (score limite à zéro) ; en deathmatch, celui qui est seul gagne
			if (_opt.Tournament > 0 && !_opt.FillBots && !_mode.Info.TwoTeams && _humans.Count == 1) EndModal(_humans[0].Team);
			if ((!string.IsNullOrEmpty(_opt.TournamentMatch) || (_opt.Tournament > 0 && !_opt.FillBots)) && _mode.Info.TwoTeams && _opt.ScoreLimit > 0)
				for (int side = 0; side < 2; side++)
					if (!_humans.Any(h => h.Team == side) && _humans.Any(h => h.Team == 1 - side)) { _mode.Score[1 - side] = _opt.ScoreLimit; Log($"Forfait : le camp {side} est absent"); EndModal((byte)(1 - side)); }
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
			ReportToAccounts(winnerTeam);
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
			victim.DiedThisRound = true; victim.Deaths++;
			var delay = _mode.RespawnDelay(victim);
			victim.RespawnTick = delay > 0f ? _tick + (uint)(delay * _m.TickRate) : uint.MaxValue;
			_mode.OnKill(killer, victim, weaponId);
			var feed = new ServerEvent { Type = EventType.KillFeed, A = killer?.Id ?? victim.Id, B = victim.Id, C = weaponId };
			for (int i = 0; i < _players.Count; i++) SendEvent(_players[i], feed);
			SendToSpectators(feed);
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
			else if (type == CommandType.DuelPick) _mode.OnDuelPick(p, (byte)arg);
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
			/// <summary>Le joueur est-il à une station de ravitaillement de son équipe (Capture du drapeau) ?</summary>
			public virtual bool AtResupply(ServerPlayer p) => false;
			public virtual void OnBuy(ServerPlayer p, int code) { }
			/// <summary>1 VS 1 : le joueur règle son équipement ou valide son choix (voir <see cref="CommandType.DuelPick"/>).</summary>
			public virtual void OnDuelPick(ServerPlayer p, byte arg) { }
			public virtual void OnPlayerLeft(ServerPlayer p) { }
			/// <summary>Le joueur lâche la bombe qu'il porte (Sabotage).</summary>
			public virtual void OnDropBomb(ServerPlayer p) { }
			/// <summary>Le porteur sort la bombe en main (<paramref name="on"/>) ou la range.</summary>
			public virtual void OnEquipBomb(ServerPlayer p, bool on) { }
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
				if (Info.Mode == GameMode.TeamDeathmatch && list.Count == 0) list = Points(MapPointKind.SpawnFfa).ToList();
				if (list.Count == 0) return S.PickSpawn();
				if (Info.Mode == GameMode.CaptureTheFlag || Info.Mode == GameMode.TeamDeathmatch || Info.Mode == GameMode.Control)
				{
					var free = list.Where(pt => !Players.Any(q => q != p && q.Alive && q.Team == p.Team && Vec3.DistanceSquared(q.State.Pos, pt.Pos) < 9f)).ToList();
					if (free.Count > 0) list = free;
				}
				// le point le plus loin des adversaires en vie (au hasard parmi les meilleurs)
				MapPoint best = list[0]; float bestScore = -1f;
				foreach (var pt in list)
				{
					float near = float.MaxValue;
					foreach (var q in Players) if (q != p && q.Alive && q.Team != p.Team) near = MathF.Min(near, Vec3.DistanceSquared(q.State.Pos, pt.Pos));
					float score = near + (float)S._rng.NextDouble() * 25f;
					if (score > bestScore) { bestScore = score; best = pt; }
				}
				// un peu de hasard autour du point, sans sortir de son étage (cartes à plusieurs niveaux : le point peut être sur une dalle)
				float spread = best.Pos.Y > S._world.HeightAt(best.Pos.X, best.Pos.Z) + 0.5f ? 0.8f : 2f;
				var jitter = new Vec3((float)(S._rng.NextDouble() * 2 - 1) * spread, 0, (float)(S._rng.NextDouble() * 2 - 1) * spread);
				var pos = best.Pos + jitter;
				return new Vec3(pos.X, S._world.GroundAt(pos.X, pos.Z, best.Pos.Y + 0.3f, Sim.StepHeight), pos.Z);
			}

			public static float Flat(Vec3 a, Vec3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }
			protected bool Near(ServerPlayer p, Vec3 pos, float radius) => p.Alive && Flat(p.State.Pos, pos) <= radius && MathF.Abs(p.State.Pos.Y - pos.Y) < 3f;
		}

		// ================================================================== Deathmatch et Team Deathmatch

		private sealed class ScoreMode : ModeLogic
		{
			private readonly bool _ffa;
			public ScoreMode(GameServer s, ModeInfo info, bool ffa) : base(s, info) { _ffa = ffa; }
			private int Target => S._opt.ScoreLimit > 0 ? S._opt.ScoreLimit : (int)GameBalance.F(_ffa ? "mode.dmKills" : "mode.tdmKills");

			public override void OnStart()
			{
				Score[0] = Score[1] = 0;
				TimeLeft = GameBalance.F(_ffa ? "mode.dmTime" : "mode.tdmTime");
				foreach (var p in Players) { p.State = PlayerMovement.Spawn(Spawn(p), S._world); S.ApplyKit(p, KitFor(p)); p.ProtectUntil = S._tick + (uint)(GameBalance.F("mode.respawnProtection") * S._m.TickRate); }
			}

			/// <summary>Deathmatch : tout le monde part au pistolet avec un bouclier épique vide (règles d'arène).</summary>
			public override ClassKit KitFor(ServerPlayer p) => ArenaRules.StartKit;

			public override bool BotGoal(ServerPlayer p, BotBrain b, out Vec3 goal, out float arrive, out Buttons hold)
			{
				hold = Buttons.None; arrive = 0.8f;
				return S.ArenaBotGoal(p, out goal);
			}

			public override void Tick(float dt)
			{
				S.TickArena();
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
				if (killer.Alive) killer.Health = MathF.Min(100f, killer.Health + ArenaRules.KillHeal);
				if (_ffa)
				{
					if (killer.Kills >= Target) S.EndModal(killer.Team);
					return;
				}
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
				S.StartVehicles();
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

			public override void OnKill(ServerPlayer killer, ServerPlayer victim, byte weapon) => S.ReleaseSeat(victim);
			public override void OnPlayerLeft(ServerPlayer p) => S.ReleaseSeat(p);

			private int Target => S._opt.ScoreLimit > 0 ? S._opt.ScoreLimit : (int)GameBalance.F("mode.controlScore");
			public int Held(int team) { int n = 0; for (int i = 0; i < 5; i++) if (_owner[i] == team) n++; return n; }

			public override void Tick(float dt)
			{
				TimeLeft -= dt;
				S.TickVehicles(dt);
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
				int target = Target;
				if (Score[0] >= target || Score[1] >= target) { S.EndModal((byte)(Score[0] >= Score[1] ? 0 : 1)); return; }
				if (TimeLeft <= 0f) { TimeLeft = 0f; S.EndModal(Score[0] == Score[1] ? (byte)255 : (byte)(Score[0] > Score[1] ? 0 : 1)); }
			}

			public override void FillState(ref ModeState st, ServerPlayer viewer)
			{
				st.Vehicles = S._vehicles;
				st.Target = (ushort)Target;
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
			private List<MapPoint> _stations = new List<MapPoint>();
			public CtfMode(GameServer s, ModeInfo info) : base(s, info) { }

			public override bool AtResupply(ServerPlayer p)
			{
				foreach (var st in _stations) if (st.Team == p.Team && Resupply.Inside(st, p.State.Pos)) return true;
				return false;
			}

			/// <summary>Stations de ravitaillement : armes, munitions, grenades et soins de la classe, vie et bouclier au maximum (au plus une fois par seconde, et seulement s'il manque quelque chose).</summary>
			private void TickResupply(float dt)
			{
				foreach (var p in Players)
				{
					if (!p.Alive) continue;
					if (p.ResupplyClock > 0f) p.ResupplyClock -= dt;
					if (p.ResupplyClock > 0f || !AtResupply(p)) continue;
					p.ResupplyClock = Resupply.Interval;
					var kit = KitFor(p);
					if (!NeedsResupply(p, kit)) continue;
					S.ApplyKit(p, kit, keepSlot: true);
					p.Health = 100f;
					S.SendEvent(p, new ServerEvent { Type = EventType.ArenaPickup, A = p.Id, Pos = p.State.Pos });
				}
			}

			private static bool NeedsResupply(ServerPlayer p, ClassKit kit)
			{
				if (p.Health < 99.5f) return true;
				if (kit.ShieldTier >= 0 && p.Shield < Consumables.ShieldMaxOf(kit.ShieldTier) - 0.5f) return true;
				var inv = p.Inventory;
				if (inv == null) return true;
				var full = KitInventory(kit);
				for (int i = 0; i < full.Weapons.Length; i++)
				{
					var want = full.Weapons[i];
					if (want == null) continue;
					var have = Array.Find(inv.Weapons, w => w != null && w.WeaponId == want.WeaponId);
					if (have == null || have.Magazine < want.Magazine) return true;
				}
				foreach (var it in full.Items) if (inv.CountOf(it.Id) < it.Count) return true;
				return false;
			}

			public override void OnStart()
			{
				_stations = Resupply.Stations(S._data);
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
				TickResupply(dt);
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
						if (Score[mine] >= Captures) { S.EndModal((byte)mine); return; }
					}
				}
				if (TimeLeft <= 0f) { TimeLeft = 0f; S.EndModal(Score[0] == Score[1] ? (byte)255 : (byte)(Score[0] > Score[1] ? 0 : 1)); }
			}

			/// <summary>Prises pour gagner : celles d'un match de tournoi, sinon le réglage du jeu.</summary>
			private int Captures => S._opt.ScoreLimit > 0 ? S._opt.ScoreLimit : (int)GameBalance.F("mode.ctfCaptures");

			private void DropFlag(int flag, Vec3 pos)
			{
				_state[flag] = 2; _carrier[flag] = null;
				_pos[flag] = new Vec3(pos.X, S._world.GroundAt(pos.X, pos.Z, pos.Y, 0f), pos.Z);
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
				st.Target = (ushort)Captures;
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
			private readonly HashSet<ServerPlayer> _carried = new HashSet<ServerPlayer>();       // ceux qui ont déjà porté la bombe dans le tour de rôle en cours
			private ServerPlayer _actor, _lastDropper;   // qui pose ou désamorce ; qui vient de lâcher la bombe (il ne la ramasse pas aussitôt)
			private float _noPickup;
			private byte _roundWinner = 255;
			private int _botSite;
			private MapPoint[] _sites = new MapPoint[2];

			public SabotageMode(GameServer s, ModeInfo info) : base(s, info) { }

			private int RoundsToWin => S._opt.ScoreLimit > 0 ? S._opt.ScoreLimit : (int)GameBalance.F("sabotage.roundsToWin");
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
				Score[0] = Score[1] = 0; _round = 0; _swapped = S._opt.RandomSides ? Random.Shared.Next(2) : 0; _lossStreak[0] = _lossStreak[1] = 0;
				foreach (var p in Players) { p.Money = (int)GameBalance.F("sabotage.startMoney"); p.HasKit = false; p.DiedThisRound = true; }
				StartRound();
			}

			private void StartRound()
			{
				_round++;
				byte prevWinner = _roundWinner;      // le camp qui vient de gagner la manche : il garde tout son équipement, même ceux qui sont morts
				// mi-temps : on échange les camps, argent et équipement repartent de zéro
				bool half = _round == RoundsToWin;     // après (RoundsToWin - 1) manches jouées
				if (half)
				{
					_swapped ^= 1; _lossStreak[0] = _lossStreak[1] = 0; _carried.Clear(); prevWinner = 255;      // mi-temps : tout repart de zéro
					foreach (var p in Players) { p.Money = (int)GameBalance.F("sabotage.startMoney"); p.HasKit = false; p.DiedThisRound = true; }
				}
				_stage = SabotageStage.Buy; _stageTimer = GameBalance.F("sabotage.buySeconds");
				_bombState = 0; _site = 0; _action = 0; _actionKind = 0; _roundWinner = 255; _bombTimer = 0f;
				var attackers = new List<ServerPlayer>();
				foreach (var p in Players)
				{
					p.Alive = true; p.Health = 100f; p.LastAttacker = null; p.UseId = 0; p.Reloading = false; p.ProtectUntil = 0;
					p.State = PlayerMovement.Spawn(Spawn(p), S._world);
					bool won = prevWinner != 255 && SideOf(p) == prevWinner;
					if (p.DiedThisRound && !won) { PistolOnly(p); p.HasKit = false; }
					else
					{
						// vainqueurs : armes, munitions, soins et bouclier conservés (même pour ceux qui sont morts), le bouclier est rechargé à fond
						if (won && p.Inventory.ShieldTier >= 0) p.Shield = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);
						else p.Shield = MathF.Max(p.Shield, 0f);
						S.SendInventory(p);
					}
					p.DiedThisRound = false;
					if (SideOf(p) == 1) attackers.Add(p);
					if (p.Brain is BotBrain) BotBuy(p);
				}
				_carrier = PickCarrier(attackers);
				_bombPos = _carrier?.State.Pos ?? default;
				_actor = null; _lastDropper = null; _noPickup = 0f;
				ResetBombItems();
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
					_noPickup -= dt;
					if (_bombState == 1) foreach (var p in Players) if (p.Alive && SideOf(p) == 1 && Near(p, _bombPos, 1.8f) && !(p == _lastDropper && _noPickup > 0f)) { _carrier = p; _bombState = 0; GiveBomb(p); break; }
					if (_bombState == 0 && _carrier != null && _carrier.Alive) _bombPos = _carrier.State.Pos;
					// pose
					var c = _carrier;
					int onSite = -1;
					if (_bombState == 0 && c != null && c.Alive)
						for (int i = 0; i < 2; i++) if (_sites[i].Radius > 0f && _sites[i].InSquare(c.State.Pos.X, c.State.Pos.Z)) onSite = i;
					if (onSite >= 0 && c.Inventory.BombInHand && (c.LastButtons & Buttons.Fire) != 0)
					{
						if (_actionKind != 1) { _actionKind = 1; _action = 0f; }
						_actor = c;
						_action += dt;
						if (_action >= GameBalance.F("sabotage.plantSeconds"))
						{
							TakeBomb(c); _actor = null;
							_bombState = 2; _bombPos = c.State.Pos; _site = onSite; _bombTimer = GameBalance.F("sabotage.bombSeconds");
							_stage = SabotageStage.Planted; _action = 0f; _actionKind = 0;
							GameServer.Log($"Sabotage : bombe posée sur le site {(char)('A' + onSite)}");
						}
					}
					else if (_actionKind == 1) { _actionKind = 0; _action = 0f; _actor = null; }
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
						_actor = defuser;
						_action += dt;
						float need = GameBalance.F(defuser.HasKit ? "sabotage.defuseKitSeconds" : "sabotage.defuseSeconds");
						if (_action >= need) { _bombState = 3; _actionKind = 0; _action = 0f; _actor = null; EndRound(0); }
					}
					else if (_actionKind == 2) { _actionKind = 0; _action = 0f; _actor = null; }
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
				if (_carrier == victim && _bombState == 0) { _bombState = 1; _bombPos = victim.State.Pos; _carrier = null; TakeBomb(victim); _lastDropper = null; }
				if (_actor == victim) { _actor = null; _actionKind = 0; _action = 0f; }
			}

			/// <summary>La bombe est un objet du sac de celui qui la porte : on l'ajoute, on la retire, on retire toute bombe oubliée d'une manche à l'autre.</summary>
			private void GiveBomb(ServerPlayer p)
			{
				if (p.Inventory.CountOf(ItemIds.Bomb) == 0) { p.Inventory.Items.Add(new ItemStack { Id = ItemIds.Bomb, Count = 1 }); p.Inventory.SortItems(); }
				S.SendInventory(p);
			}

			private void TakeBomb(ServerPlayer p)
			{
				bool wasInHand = p.Inventory.BombInHand;
				p.Inventory.BombInHand = false;
				if (wasInHand) SelectFirstWeapon(p);        // la bombe n'est plus là : on reprend une arme
				bool had = false;
				for (int i = p.Inventory.Items.Count - 1; i >= 0; i--) if (p.Inventory.Items[i].Id == ItemIds.Bomb) { p.Inventory.Items.RemoveAt(i); had = true; }
				if (had) S.SendInventory(p);
			}

			/// <summary>
			/// Le porteur de la manche : un vrai joueur de préférence (les bots ne la portent que s'il n'y a aucun humain chez les attaquants), et chacun son tour :
			/// personne ne la reçoit deux fois tant que tous les autres ne l'ont pas eue.
			/// </summary>
			private ServerPlayer PickCarrier(List<ServerPlayer> attackers)
			{
				if (attackers.Count == 0) return null;
				_carried.RemoveWhere(p => !attackers.Contains(p));        // parti ou changé de camp
				var pool = attackers.Where(p => !p.IsBot).ToList();
				if (pool.Count == 0) pool = attackers;
				var fresh = pool.Where(p => !_carried.Contains(p)).ToList();
				if (fresh.Count == 0) { foreach (var p in pool) _carried.Remove(p); fresh = pool; }       // tout le monde l'a eue : on recommence
				var pick = fresh[S._rng.Next(fresh.Count)];
				_carried.Add(pick);
				return pick;
			}

			private static void SelectFirstWeapon(ServerPlayer p)
			{
				for (int i = 0; i < p.Inventory.Weapons.Length; i++) if (p.Inventory.Weapons[i] != null) { p.Inventory.SelectSlot(i); return; }
			}

			public override void OnEquipBomb(ServerPlayer p, bool on)
			{
				if (p != _carrier || !p.Alive || _bombState != 0) return;
				if (_stage != SabotageStage.Round && _stage != SabotageStage.Buy) return;
				if (p.Inventory.CountOf(ItemIds.Bomb) == 0) return;
				p.Reloading = false; p.UseId = 0;
				if (on) { p.Inventory.SelectSlot(-1); p.Inventory.BombInHand = true; }
				else { p.Inventory.BombInHand = false; SelectFirstWeapon(p); }
				S.SendInventory(p);
			}

			private void ResetBombItems()
			{
				foreach (var p in Players) if (p != _carrier) TakeBomb(p);
				if (_carrier != null) GiveBomb(_carrier);
			}

			/// <summary>Le porteur lâche la bombe : elle tombe un peu devant lui. Il ne la ramasse pas tout de suite (un coéquipier le peut).</summary>
			public override void OnDropBomb(ServerPlayer p)
			{
				if (p != _carrier || _bombState != 0 || !p.Alive) return;
				if (_stage != SabotageStage.Buy && _stage != SabotageStage.Round) return;
				float sy = MathF.Sin(p.Yaw), cy = MathF.Cos(p.Yaw);
				Vec3 pos = p.State.Pos + new Vec3(-sy, 0f, -cy) * 1.2f;
				if (S._world.Raycast(p.State.Pos + new Vec3(0, 1f, 0), new Vec3(-sy, 0f, -cy), 1.2f, out _)) pos = p.State.Pos;       // un mur devant : elle tombe aux pieds
				_bombPos = new Vec3(pos.X, MathF.Max(S._world.HeightAt(pos.X, pos.Z), p.State.Pos.Y - 0.3f), pos.Z);
				_bombState = 1; _carrier = null; _lastDropper = p; _noPickup = 2.5f;
				TakeBomb(p);
				if (_actor == p) { _actor = null; _actionKind = 0; _action = 0f; }
				GameServer.Log($"Sabotage : {p.Name} lâche la bombe en ({_bombPos.X:F0} ; {_bombPos.Z:F0})");
			}

			public override void OnPlayerLeft(ServerPlayer p)
			{
				if (_carrier == p && _bombState == 0) { _bombState = 1; _bombPos = p.State.Pos; _carrier = null; TakeBomb(p); }
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
					else if (def.Kind == ItemKind.Ammo)
					{
						// munitions : on ne paie que si le sac en a pris (il peut être plein)
						var none = new List<LootItem>();
						var am = LootItem.Of(item.ItemId); am.Count = (ushort)item.Quantity;
						int asked = am.Count;
						inv.TryPickup(ref am, none);
						if (am.Count >= asked) return;
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
				st.BombAction = _actionKind; st.BombActor = _actor?.Id ?? (ushort)0xFFFF;
				if (_actionKind != 0)
				{
					float needAll = _actionKind == 1 ? GameBalance.F("sabotage.plantSeconds") : GameBalance.F(_actor != null && _actor.HasKit ? "sabotage.defuseKitSeconds" : "sabotage.defuseSeconds");
					st.BombActionProgress = (byte)Math.Clamp((int)(_action / MathF.Max(0.1f, needAll) * 100f), 0, 100);
				}
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
					if (_carrier == p)
					{
						goal = _sites[site].Pos; arrive = MathF.Max(1f, _sites[site].Radius * 0.4f); hold = Buttons.Fire;
						if (!p.Inventory.BombInHand && Flat(p.State.Pos, _sites[site].Pos) < _sites[site].Radius + 8f) OnEquipBomb(p, true);
						return true;
					}
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
