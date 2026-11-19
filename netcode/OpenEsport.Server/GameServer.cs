using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	public sealed class ServerOptions
	{
		public int Port = 27015;
		public MatchConfig Match = new MatchConfig();
		public string WorldPath;
		public int ProceduralSize = 2000;
		/// <summary>Si &gt; 0, les joueurs apparaissent tous dans ce rayon autour du centre de la carte (scénario de test « chute au même endroit »).</summary>
		public float SpawnClusterRadius;
		public int StatsIntervalSeconds = 5;
		/// <summary>Rejuge les tirs sur les positions vues par le tireur. À ne désactiver que pour comparer.</summary>
		public bool LagCompensation = true;
		/// <summary>Donne à chaque joueur un fusil, des munitions et un sac niveau 3 (essais de charge : les robots n'ont pas à ramasser).</summary>
		public bool StartingLoadout;
		/// <summary>Complète les parties non pleines avec des bots.</summary>
		public bool FillBots;
		/// <summary>Sabotage : le camp de départ (attaquants ou défenseurs) est tiré au hasard à chaque partie ; sinon l'équipe 0 attaque d'abord (tests).</summary>
		public bool RandomSides;
		/// <summary>Nombre de joueurs visé en comptant les bots (0 = partie pleine).</summary>
		public int BotFillTarget;
		/// <summary>Nombre maximal de bots présents en même temps (-1 = sans limite, 0 = aucun).</summary>
		public int MaxBots = -1;
		/// <summary>Niveau des bots, de 0 (maladroits) à 1 (précis).</summary>
		public float BotSkill = 0.5f;
		/// <summary>Les joueurs arrivent sur la carte par un avion (saut au choix, chute libre, parachute) au lieu d'apparaître au sol.</summary>
		public bool Dropship;
		/// <summary>
		/// Partie « battle royale » : salle d'attente, avion commun, pas de réapparition, joueurs à terre réanimables, le dernier en vie gagne.
		/// Sans cette option : partie classique (réapparition, pas d'avion commun).
		/// </summary>
		public bool Royale;
		/// <summary>Humains nécessaires pour lancer la partie, et compte à rebours (s) une fois ce nombre atteint.</summary>
		public int MinPlayers = 1;
		public float LobbyCountdown = 5f;
		/// <summary>À terre : durée avant la mort (s), durée de la réanimation (s) et vie au relèvement.</summary>
		public float DownSeconds = 60f, ReviveSeconds = 5f, RevivedHealth = 50f;
		/// <summary>Après la fin de la partie, durée avant de renvoyer les joueurs au menu (s).</summary>
		public float EndLinger = 15f;
		/// <summary>Le serveur s'arrête une fois la partie terminée (serveur lancé à la demande par le matchmaking).</summary>
		/// <summary>Service de comptes (http://hôte:27090) et clé du serveur : si l'adresse est renseignée, un joueur doit présenter un jeton de session valide pour entrer ; ses résultats sont rapportés en fin de partie.</summary>
		public string AccountsUrl, AccountsKey;
		/// <summary>Partie d'un tournoi : identifiant du tournoi (0 = aucun), manche et lobby. Seuls les inscrits de ce lobby entrent ; les places sont rapportées au service de comptes.</summary>
		public long Tournament; public int TournamentRace, TournamentLobby;
		/// <summary>Match de duel d'un tournoi (clé du match, vide = aucun) et nombre de victimes pour le gagner (0 = valeur du jeu).</summary>
		public string TournamentMatch; public int ScoreLimit;
		public bool ExitWhenOver;
		/// <summary>Le serveur s'arrête si la salle d'attente reste vide ce nombre de secondes (0 = jamais).</summary>
		public float IdleExitSeconds;
		/// <summary>Une partie en cours sans aucun humain connecté (il ne reste que des bots) se termine au bout de ce délai (s) : le serveur s'arrête si <see cref="ExitWhenOver"/>, sinon il revient à la salle d'attente.</summary>
		public float EmptyExitSeconds = 3f;
		/// <summary>Réglage des zones du mode battle royale (par défaut : celles d'Apex Legends). Voir <see cref="ZoneConfig"/> et <c>--zones</c>.</summary>
		public ZoneConfig Zones = ZonePresets.Apex();
		/// <summary>Mode de jeu. Tout mode autre que le battle royale se joue sur sa carte dédiée, avec salle d'attente puis fin de partie comme le battle royale.</summary>
		public GameMode Mode = GameMode.BattleRoyale;
	}

	/// <summary>
	/// Serveur de jeu autoritaire : il simule tous les joueurs à pas fixe, décide des tirs, du butin et des équipes,
	/// et n'envoie à chaque client que ce qui l'entoure.
	/// </summary>
	public sealed partial class GameServer
	{
		private const int LootIdBase = 1000;
		private const int LootCapacity = ClientReplication.MaxEntityIds - LootIdBase;

		private readonly ServerOptions _opt;
		private readonly MatchConfig _m;
		private readonly WorldData _data;
		private readonly StaticWorld _world;
		private readonly Quantizer _quant;
		private readonly uint _worldChecksum;

		private readonly EventBasedNetListener _listener = new EventBasedNetListener();
		private readonly NetManager _net;
		private readonly BitReader _reader = new BitReader();
		private readonly BitWriter _tmp = new BitWriter(1400);

		private readonly ServerPlayer[] _slots;
		private readonly List<ServerPlayer> _players = new List<ServerPlayer>();
		/// <summary>Les joueurs connectés au réseau (sans les bots).</summary>
		private readonly List<ServerPlayer> _humans = new List<ServerPlayer>();
		private readonly TeamRoster _teams;
		private readonly EntityState[] _packed;

		private readonly SpatialGrid _playerGrid;
		private readonly SpatialGrid _lootGrid;
		private readonly LootEntity[] _loot = new LootEntity[LootCapacity];
		private int _lootHigh;
		private readonly Stack<int> _freeLoot = new Stack<int>();
		private bool _lootDirty = true;
		private readonly List<LootItem> _dropScratch = new List<LootItem>();
		private readonly List<int> _lootScratch = new List<int>();
		private Random _rng = new Random(12345);

		private uint _tick;
		private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadActions = new System.Collections.Concurrent.ConcurrentQueue<Action>();
		private readonly TickMetrics _metrics;
		private string _lastReport = "{}";
		private long _lastBytesOut, _lastBytesIn, _lastAlloc;
		private double _lastReportTime;
		private double _snapshotBytesTotal;
		private long _snapshotCount;
		private readonly Stopwatch _uptime = Stopwatch.StartNew();

		/// <summary>Exécute une action sur le thread du serveur, au début du prochain tick (tests, commandes d'administration).</summary>
		public void RunOnServerThread(Action action) { _mainThreadActions.Enqueue(action); }

		public ServerPlayer GetPlayer(int id) => id >= 0 && id < _slots.Length ? _slots[id] : null;
		public int LootCount => ActiveLootCount();
		/// <summary>À appeler depuis le thread du serveur (voir <see cref="RunOnServerThread"/>).</summary>
		public bool SpawnLootAt(LootItem item, Vec3 pos) => AddLoot(item, pos);
		/// <summary>Ouvre tous les coffres fermés (tests, administration). À appeler depuis le thread du serveur. Retourne leur nombre.</summary>
		public int OpenAllChests()
		{
			int n = 0;
			for (int i = 0; i < _lootHigh; i++)
				if (_loot[i].Active && _loot[i].Item.ItemId == ItemIds.Chest && _loot[i].Item.Count == 0) { OpenChest(i); n++; }
			return n;
		}
		public uint CurrentTick => _tick;
		public event Action Started;
		public uint Tick => _tick;
		public int PlayerCount => _players.Count;
		public string LastReport => _lastReport;
		public WorldData World => _data;

		public GameServer(ServerOptions options) : this(options, null) { }

		/// <param name="world">Carte à utiliser (sinon : fichier indiqué dans les options, ou carte procédurale).</param>
		public GameServer(ServerOptions options, WorldData world)
		{
		_opt = options;
			if (options.RandomSides) _rng = new Random();      // en vrai : un tirage différent à chaque partie (trajet de l'avion, atterrissages des bots) ; les tests gardent une graine fixe
			if (options.Mode != GameMode.BattleRoyale) { options.Royale = false; options.Dropship = false; options.Match.RespawnSeconds = Modes.Get(options.Mode).RespawnSeconds; }
			if (options.Royale) { options.Match.RespawnSeconds = 0f; options.Dropship = true; }
			_m = options.Match.Validated();
			_data = world ?? (!string.IsNullOrEmpty(options.WorldPath) ? WorldData.LoadFile(options.WorldPath) : WorldData.CreateProcedural(options.ProceduralSize));
			_world = new StaticWorld(_data);
			_worldChecksum = _data.Checksum();
			_quant = new Quantizer(_data.MinX, _data.MinHeight - 32f, _data.MinZ);
			_slots = new ServerPlayer[_m.MaxPlayers];
			SetupAccounts(options);
			if (_opt.FillBots) _nav = new NavGrid(_data);      // la grille de navigation des bots se construit une fois, au démarrage
			_packed = new EntityState[_m.MaxPlayers];
			_teams = new TeamRoster(_m.TeamSize);
			_mode = options.Mode != GameMode.BattleRoyale ? CreateMode(this, options.Mode) : null;
			_playerGrid = new SpatialGrid(_data.MinX, _data.MinZ, _data.MaxX, _data.MaxZ, 64f);
			_lootGrid = new SpatialGrid(_data.MinX, _data.MinZ, _data.MaxX, _data.MaxZ, 64f);
			_metrics = new TickMetrics(1000.0 / _m.TickRate);

			_net = new NetManager(_listener)
			{
				UpdateTime = 1,
				AutoRecycle = true,
				EnableStatistics = true,
				UnconnectedMessagesEnabled = true,
				DisconnectTimeout = 5000,
				UnsyncedEvents = false,
			};
			_listener.ConnectionRequestEvent += OnConnectionRequest;
			_listener.PeerDisconnectedEvent += OnPeerDisconnected;
			_listener.NetworkReceiveEvent += OnReceive;
			_listener.NetworkReceiveUnconnectedEvent += OnUnconnected;

			SpawnInitialLoot();
		}

		// ------------------------------------------------------------------ boucle principale

		public void Run(CancellationToken ct)
		{
			if (!_net.Start(_opt.Port))
				throw new InvalidOperationException($"Impossible d'ouvrir le port UDP {_opt.Port} (déjà utilisé ?)");
			Started?.Invoke();
			Log($"Serveur démarré : port {_opt.Port}, {_m.MaxPlayers} joueurs max, équipes de {_m.TeamSize}, tick {_m.TickRate} Hz, snapshot tous les {_m.SnapshotDivisor} ticks, carte {_data.Width}x{_data.Depth} (empreinte {_worldChecksum:X8})");

			long freq = Stopwatch.Frequency;
			long start = Stopwatch.GetTimestamp();
			long tickIndex = 0;
			long ticksPerStep = freq / _m.TickRate;

			while (!ct.IsCancellationRequested && !_stopRequested)
			{
				long now = Stopwatch.GetTimestamp();
				int steps = 0;
				while (start + (tickIndex + 1) * freq / _m.TickRate <= now && steps < 5)
				{
					tickIndex++;
					try { RunTick(); }
					catch (Exception e) { Log("ERREUR dans le tick " + _tick + " : " + e); } // une exception ne doit jamais arrêter le serveur
					steps++;
				}
				if (steps == 5)
					start = now - tickIndex * freq / _m.TickRate; // trop en retard : on abandonne le rattrapage

				long next = start + (tickIndex + 1) * freq / _m.TickRate;
				long remaining = next - Stopwatch.GetTimestamp();
				if (remaining > freq / 500) Thread.Sleep(1);
				else if (remaining > 0) Thread.SpinWait(50);
			}
			_net.Stop();
		}

		private void RunTick()
		{
			long t0 = Stopwatch.GetTimestamp();
			_tick++;
			while (_mainThreadActions.TryDequeue(out var action)) action();
			_net.PollEvents();

			long tNet = Stopwatch.GetTimestamp();
			int gcBefore = GC.CollectionCount(2) + GC.CollectionCount(1);
			float dt = 1f / _m.TickRate;
			TickMatch(dt);
			ThinkBots(dt);
			long tBots = Stopwatch.GetTimestamp();
			for (int i = 0; i < _players.Count; i++)
				SimulatePlayer(_players[i], dt);
			long tSim = Stopwatch.GetTimestamp();
			if (_tick % (uint)_m.TickRate == 0 && !Phased) MaintainBots();
			HandleRespawns();

			// historique pour la compensation de latence, états à répliquer, grille de proximité
			_playerGrid.Clear();
			for (int i = 0; i < _players.Count; i++)
			{
				var p = _players[i];
				p.History.Record(_tick, p.State.Pos, p.State.Stance, p.Alive && p.State.Stance != Stance.Plane);
				_packed[p.Id] = Pack(p);
				_playerGrid.Insert(p.Id, p.State.Pos.X, p.State.Pos.Z);
				if (p.FireFlagTicks > 0) p.FireFlagTicks--;
			}
			_playerGrid.Build();
			StepProjectiles(dt);
			StepGrenades(dt);
			if (_lootDirty) RebuildLootGrid();

			if (_tick % (uint)_m.SnapshotDivisor == 0 && _humans.Count > 0)
			{
				// les snapshots sont construits en parallèle (chaque joueur a ses propres tampons), puis envoyés
				Parallel.ForEach(_humans, BuildSnapshot);
				for (int i = 0; i < _humans.Count; i++)
				{
					var p = _humans[i];
					SendSnapshotPacket(p, p.Out);
					for (int k = 0; k < p.ExtraCount; k++) SendSnapshotPacket(p, p.Extra[k]);
				}
			}

			long tEnd = Stopwatch.GetTimestamp();
			double ms = (tEnd - t0) * 1000.0 / Stopwatch.Frequency;
			_metrics.Record(ms);
			if (ms > 25.0)
			{
				// tick anormalement long : on note où le temps est passé, pour retrouver la cause après coup
				double F(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
				string line = $"[tick {_tick}] {ms:F1} ms (réseau {F(t0, tNet):F1}, bots {F(tNet, tBots):F1}, joueurs {F(tBots, tSim):F1}, le reste {F(tSim, tEnd):F1}) | joueurs {_players.Count}, bots {BotCount} | ramasse-miettes pendant le tick : {(GC.CollectionCount(2) + GC.CollectionCount(1) - gcBefore)} collectes | recherches de chemin restantes {_pathBudget}";
				Log("[lag] " + line);
				try { System.IO.File.AppendAllText("server_lag.log", DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine); } catch { }
			}
			ReportIfDue();
		}

		private void SendSnapshotPacket(ServerPlayer p, BitWriter w)
		{
			try
			{
				p.Peer.Send(w.Buffer, 0, w.ByteLength, DeliveryMethod.Sequenced);
				_snapshotBytesTotal += w.ByteLength;
				_snapshotCount++;
			}
			catch (Exception e)
			{
				Log($"Envoi impossible à {p.Name} ({w.ByteLength} octets) : {e.Message}");
			}
		}

		// ------------------------------------------------------------------ réseau

		private void OnConnectionRequest(ConnectionRequest request)
		{
			if (_humans.Count >= _m.MaxPlayers || (Phased && Phase == MatchPhase.Over)) { request.Reject(); return; }
			request.AcceptIfKey(Protocol.ConnectionKey);
		}

		private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
		{
			if (peer.Tag is ServerPlayer p) RemovePlayer(p);
		}

		private void OnUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
		{
			try
			{
				if (reader.AvailableBytes >= 9 && Encoding.ASCII.GetString(reader.RawData, reader.UserDataOffset, 9) == "TELEMETRY")
				{
					var json = Encoding.UTF8.GetBytes(TelemetryJson());
					_net.SendUnconnectedMessage(json, 0, json.Length, remote);
				}
				else if (reader.AvailableBytes >= 6 && Encoding.ASCII.GetString(reader.RawData, reader.UserDataOffset, 6) == "STATUS")
				{
					var line = Encoding.UTF8.GetBytes(StatusLine());
					_net.SendUnconnectedMessage(line, 0, line.Length, remote);
				}
				else if (reader.AvailableBytes >= 5 && Encoding.ASCII.GetString(reader.RawData, reader.UserDataOffset, 5) == "STATS")
				{
					var bytes = Encoding.UTF8.GetBytes(_lastReport);
					_net.SendUnconnectedMessage(bytes, 0, bytes.Length, remote);
				}
			}
			catch (Exception) { }
		}

		private void OnReceive(NetPeer peer, NetPacketReader packet, byte channel, DeliveryMethod method)
		{
			if (packet.UserDataSize < 1) return;
			_reader.Init(packet.RawData, packet.UserDataOffset, packet.UserDataSize);
			try
			{
				var type = (MsgType)_reader.ReadByte();
				var player = peer.Tag as ServerPlayer;
				switch (type)
				{
					case MsgType.Join: if (player == null) HandleJoin(peer); break;
					case MsgType.Input: if (player != null) HandleInput(player); break;
					case MsgType.Command: if (player != null) HandleCommand(player); break;
					case MsgType.Ping: if (player != null) HandlePing(player); break;
				}
			}
			catch (Exception e)
			{
				Log($"Paquet invalide de {peer.Address} : {e.Message}");
			}
		}

		private void HandleJoin(NetPeer peer)
		{
			ushort version = _reader.ReadUInt16();
			string name = _reader.ReadString();
			var appearance = Messages.ReadAppearance(_reader);
			string token = _reader.ReadString();
			if (_reader.Overflow) return;
			if (peer.Tag != null) return;                                   // déjà entré, ou vérification de son compte en cours
			if (version != Protocol.Version) { Reject(peer, "Version du protocole différente"); return; }
			if (Phased && Phase != MatchPhase.Lobby) { Reject(peer, "Partie déjà commencée"); return; }

			if (_accounts != null)
			{
				// serveur en ligne : il faut un compte. Le jeton est vérifié sans bloquer le serveur, puis l'entrée se termine sur le fil principal.
				if (string.IsNullOrEmpty(token)) { Reject(peer, "Compte requis : connecte-toi."); return; }
				var pending = new object();
				peer.Tag = pending;
				_ = VerifyThenJoin(peer, pending, appearance, token);
				return;
			}
			CompleteJoin(peer, name, appearance, 0);
		}

		private async System.Threading.Tasks.Task VerifyThenJoin(NetPeer peer, object pending, byte[] appearance, string token)
		{
			var (account, unreachable) = await _accounts.VerifyAsync(token);
			_mainThreadActions.Enqueue(() =>
			{
				if (peer.Tag != pending) return;
				peer.Tag = null;
				if (peer.ConnectionState != ConnectionState.Connected) return;           // il est parti pendant la vérification
				if (account != null && TournamentRefusal(account.AccountId) is { } refusal) { Reject(peer, refusal); return; }
				if (account == null) { Reject(peer, unreachable ? "Service de comptes injoignable : réessaie dans un instant." : "Session invalide : reconnecte-toi."); return; }
				foreach (var h in _humans) if (h.AccountId == account.AccountId) { Reject(peer, "Ce compte est déjà connecté."); return; }
				CompleteJoin(peer, account.Login, appearance, account.AccountId, account.PartyId, account.PartySize);
			});
		}

		private void CompleteJoin(NetPeer peer, string name, byte[] appearance, long accountId, long partyId = 0, int partySize = 0)
		{
			if (Phased && Phase != MatchPhase.Lobby) { Reject(peer, "Partie déjà commencée"); return; }

			int slot = -1;
			// partie complétée par des bots : l'humain prend la place d'un bot (et donc de son équipe)
			if (_opt.FillBots && _players.Count >= BotFillTarget && TryFreeSlotFromBot(out int freed)) slot = freed;
			if (slot < 0)
				for (int i = 0; i < _slots.Length; i++) if (_slots[i] == null) { slot = i; break; }
			if (slot < 0 && !TryFreeSlotFromBot(out slot)) { Reject(peer, "Serveur plein"); return; } // un bot cède sa place
			if (slot < 0) { Reject(peer, "Serveur plein"); return; }

			if (partyId != 0 && partySize > 1 && !_partyWait.ContainsKey(partyId)) _partyWait[partyId] = (partySize, DateTime.UtcNow.AddSeconds(PartyWaitSeconds));
			var p = new ServerPlayer { Id = (ushort)slot, Peer = peer, Name = SanitizeName(name, slot), Team = (byte)(_tournamentSide.TryGetValue(accountId, out int side) ? side : JoinTeam(partyId)), Appearance = appearance, AccountId = accountId, PartyId = partyId };
			Vec3 spawn = Modal ? ModalSpawn(p) : PickSpawn();
			p.State = (_opt.Dropship && !Royale) ? NewFlight() : PlayerMovement.Spawn(spawn, _world);
			p.Yaw = 0;
			_slots[slot] = p;
			_players.Add(p);
			_humans.Add(p);
			peer.Tag = p;

			var accept = new JoinAccept
			{
				PlayerId = p.Id, Team = p.Team, TickRate = (byte)_m.TickRate, SnapshotDivisor = (byte)_m.SnapshotDivisor,
				InterpTicks = (byte)_m.InterpTicks, TeamSize = (byte)_m.TeamSize, MaxPlayers = (ushort)_m.MaxPlayers,
				MinX = _quant.MinX, MinY = _quant.MinY, MinZ = _quant.MinZ, ServerTick = _tick, WorldChecksum = _worldChecksum, Spawn = p.State
			};
			// les réglages d'abord : le client en a besoin pour simuler son déplacement exactement comme le serveur
			_tmp.Reset();
			Messages.WriteBalance(_tmp);
			peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
			_tmp.Reset();
			accept.Write(_tmp);
			peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
			GiveStartingLoadout(p);
			if (Modal) ApplyKit(p, Loadouts.Get(p.ClassId));
			SendInventory(p);
			BroadcastRoster();
			Log($"+ {p.Name} (joueur {p.Id}, équipe {p.Team}) — {_players.Count}/{_m.MaxPlayers}");
		}

		private static string SanitizeName(string name, int slot)
		{
			if (string.IsNullOrWhiteSpace(name)) return "Joueur" + slot;
			var sb = new StringBuilder();
			foreach (char c in name) if (!char.IsControl(c) && sb.Length < 24) sb.Append(c);
			return sb.Length == 0 ? "Joueur" + slot : sb.ToString();
		}

		private void Reject(NetPeer peer, string reason)
		{
			_tmp.Reset();
			Messages.WriteReject(_tmp, reason);
			// le motif voyage dans le paquet de déconnexion lui-même : envoyé à part juste avant, il serait perdu
			peer.Disconnect(_tmp.Buffer, 0, _tmp.ByteLength);
		}

		private void HandleInput(ServerPlayer p)
		{
			ushort ackSeq = _reader.ReadUInt16();
			uint ackBits = _reader.ReadUInt32();
			int count = (int)_reader.ReadBits(4);
			if (_reader.Overflow || count > Protocol.MaxInputsPerPacket) return;
			p.Rep.Ack(ackSeq, ackBits);
			for (int i = 0; i < count; i++)
			{
				var input = PlayerInput.Read(_reader);
				if (_reader.Overflow) return;
				p.EnqueueInput(input);
			}
		}

		private void RemovePlayer(ServerPlayer p)
		{
			if (_slots[p.Id] != p) return;
			if (Royale && Phase == MatchPhase.Playing && p.Alive) Eliminate(p, null, 0, announce: false);     // il quitte : éliminé
			RecordDeparture(p);        // son résultat compte même s'il part avant la fin (défaite comprise)
			if (Modal) _mode.OnPlayerLeft(p);
			if (p.Alive && !Modal) DropAllLoot(p);
			_slots[p.Id] = null;
			_players.Remove(p);
			_humans.Remove(p);
			if (_humans.Count == 0) ReportAbandoned();
			LeaveTeam(p.Team);
			p.Peer.Tag = null;
			BroadcastRoster();
			Log($"- {p.Name} — {_players.Count}/{_m.MaxPlayers} ({_humans.Count} humains)");
		}

		private void BroadcastRoster()
		{
			var entries = new List<RosterEntry>(_players.Count);
			foreach (var q in _players) entries.Add(new RosterEntry { Id = q.Id, Team = q.Team, Name = q.Name, IsBot = q.IsBot, Appearance = q.Appearance });
			_tmp.Reset();
			Messages.WriteRoster(_tmp, entries);
			foreach (var q in _humans) q.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		private void SendEvent(ServerPlayer to, ServerEvent e)
		{
			if (to.Peer == null) return;
			_tmp.Reset();
			e.Write(_tmp);
			to.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		private void SendInventory(ServerPlayer p)
		{
			if (p.Peer == null) return;
			_tmp.Reset();
			Messages.WriteInventory(_tmp, p.Inventory);
			p.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		// ------------------------------------------------------------------ réplication

		private EntityState Pack(ServerPlayer p)
		{
			var s = p.State;
			byte flags = 0;
			if (s.OnGround) flags |= (byte)EntityFlags.OnGround;
			if (s.Stance == Stance.Crouching) flags |= (byte)EntityFlags.Crouching;
			if (s.Stance == Stance.Sliding) flags |= (byte)EntityFlags.Sliding;
			if (p.FireFlagTicks > 0) flags |= (byte)EntityFlags.Firing;
			if (p.Reloading) flags |= (byte)EntityFlags.Reloading;
			if (p.UseId != 0) flags |= (byte)EntityFlags.Using;
			if (!p.Alive) flags |= (byte)EntityFlags.Dead;
			if ((p.LastButtons & Buttons.Aim) != 0) flags |= (byte)EntityFlags.Aiming;
			return new EntityState
			{
				Id = p.Id, Kind = EntityKind.Player,
				X = _quant.QX(s.Pos.X), Z = _quant.QZ(s.Pos.Z), Y = _quant.QY(s.Pos.Y),
				Yaw = EntityState.QuantizeYaw12(p.Yaw), Pitch = EntityState.QuantizePitch8(p.Pitch),
				VelX = EntityState.QuantizeVel(s.Vel.X), VelZ = EntityState.QuantizeVel(s.Vel.Z),
				Flags = flags, Health = (byte)Math.Clamp((int)MathF.Ceiling(p.Health), 0, 100),
				Shield = (byte)Math.Clamp((int)MathF.Ceiling(p.Shield), 0, 127), ShieldTier = (byte)(p.Inventory.ShieldTier + 1),
				Zip = (byte)(s.Stance == Stance.Zipping ? 1 : s.Stance == Stance.Climbing ? 2 : s.Stance == Stance.Plane ? 3 : s.Stance == Stance.Diving ? 4 : s.Stance == Stance.Downed ? 5 : 0),
				Weapon = p.Inventory.ActiveWeaponId, Team = p.Team
			};
		}

		private EntityState PackLoot(int index)
		{
			ref var l = ref _loot[index];
			var d = ItemDefs.Get(l.Item.ItemId);
			return new EntityState
			{
				Id = (ushort)(LootIdBase + index), Kind = EntityKind.Loot,
				X = _quant.QX(l.Pos.X), Z = _quant.QZ(l.Pos.Z), Y = _quant.QY(l.Pos.Y),
				Weapon = l.Item.ItemId,
				// arme : viseur dans l'octet bas, niveau dans les bits 8-9 ; le reste : la quantité
				Count = d != null && d.Kind == ItemKind.Weapon ? (ushort)(l.Item.Optic | (l.Item.Tier << 8))
					: d != null && d.Kind == ItemKind.Shield ? (ushort)(l.Item.Tier << 8) : l.Item.Count
			};
		}

		/// <summary>
		/// Construit le snapshot d'un client : son propre état complet, puis les entités de son voisinage, à une fréquence qui
		/// diminue avec la distance. Les coéquipiers sont toujours suivis, où qu'ils soient.
		/// </summary>
		private void BuildSnapshot(ServerPlayer p)
		{
			var w = p.Out;
			w.Reset();
			var rep = p.Rep;
			rep.Begin();

			w.WriteByte((byte)MsgType.Snapshot);
			w.WriteUInt32(_tick);
			w.WriteUInt16(rep.Seq);
			w.WriteBool(true);
			w.WriteUInt16(p.LastProcessedSeq);
			p.State.Write(w);
			w.WriteBits((uint)Math.Clamp((int)MathF.Ceiling(p.Health), 0, 100), 8);
			w.WriteBits((uint)Math.Clamp((int)MathF.Ceiling(p.Shield), 0, 255), 8);
			w.WriteBits((uint)(p.Inventory.ShieldTier + 1), 3);
			w.WriteByte(p.UseId);
			w.WriteByte((byte)(p.UseId == 0 ? 0 : Math.Clamp((int)((1f - p.UseLeft / p.UseTotal) * 255f), 0, 255)));
			w.WriteBool(p.Alive);
			w.WriteByte((byte)Math.Clamp(p.Inventory.Active?.Magazine ?? 0, 0, 255));
			w.WriteBool(p.Reloading);

			Vec3 pos = p.State.Pos;
			float radius = _m.InterestRadius;
			float r2 = radius * radius;
			var scratch = p.Scratch;
			scratch.Clear();
			_playerGrid.Query(pos.X, pos.Z, radius, scratch);
			for (int i = 0; i < scratch.Count; i++)
			{
				int id = scratch[i];
				if (id == p.Id) continue;
				var o = _slots[id];
				if (o == null) continue;
				float dx = o.State.Pos.X - pos.X, dz = o.State.Pos.Z - pos.Z;
				float d2 = dx * dx + dz * dz;
				if (d2 > r2) continue;
				OfferByDistance(rep, id, d2, o.Team == p.Team, _packed[id]);
			}

			// coéquipiers hors du rayon d'intérêt
			for (int i = 0; i < _players.Count; i++)
			{
				var o = _players[i];
				if (o == p || o.Team != p.Team) continue;
				float dx = o.State.Pos.X - pos.X, dz = o.State.Pos.Z - pos.Z;
				if (dx * dx + dz * dz <= r2) continue; // déjà vu plus haut
				rep.MarkRelevant(o.Id);
				if (rep.Seq % 2 == 0) rep.Offer(_packed[o.Id], 4);
			}

			// butin proche
			scratch.Clear();
			_lootGrid.Query(pos.X, pos.Z, LootRadius, scratch);
			for (int i = 0; i < scratch.Count; i++)
			{
				int id = scratch[i];
				int idx = id - LootIdBase;
				if (idx < 0 || idx >= _lootHigh || !_loot[idx].Active) continue;
				float dx = _loot[idx].Pos.X - pos.X, dz = _loot[idx].Pos.Z - pos.Z;
				float d2 = dx * dx + dz * dz;
				if (d2 > LootRadius * LootRadius) continue;
				rep.MarkRelevant(id);
				bool near = d2 < 25f * 25f;
				if (near || rep.Seq % 4 == 0) rep.Offer(PackLoot(idx), near ? 3 : 1);
			}

			// budget : ce que le transport accepte, moins ce qui est déjà écrit (en-tête et état du joueur)
			int packetMax = Math.Min(Protocol.SnapshotBudgetBytes, p.Peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) - 8);
			rep.Write(w, packetMax - w.ByteLength);

			// beaucoup d'entités ont changé : paquets supplémentaires, sans l'état du joueur
			p.ExtraCount = 0;
			while (rep.HasMore && p.ExtraCount < Protocol.MaxPacketsPerSnapshot - 1)
			{
				var x = p.Extra[p.ExtraCount++];
				x.Reset();
				x.WriteByte((byte)MsgType.Snapshot);
				x.WriteUInt32(_tick);
				x.WriteUInt16((ushort)(rep.Seq + 1));
				x.WriteBool(false);
				rep.WriteContinuation(x, packetMax - x.ByteLength);
			}
		}

		private const float LootRadius = 100f;

		private static void OfferByDistance(ClientReplication rep, int id, float d2, bool teammate, in EntityState state)
		{
			rep.MarkRelevant(id);
			int divisor, weight;
			if (teammate || d2 < 64f * 64f) { divisor = 1; weight = 8; }
			else if (d2 < 192f * 192f) { divisor = 2; weight = 4; }
			else { divisor = 4; weight = 1; }
			if (rep.Seq % divisor == 0) rep.Offer(state, weight);
		}

		// ------------------------------------------------------------------ statistiques

		private void ReportIfDue()
		{
			double now = _uptime.Elapsed.TotalSeconds;
			if (now - _lastReportTime < _opt.StatsIntervalSeconds) return;
			double elapsed = now - _lastReportTime;
			_lastReportTime = now;
			if (elapsed > 60) elapsed = _opt.StatsIntervalSeconds; // premier appel

			var (mean, p50, p99, max) = _metrics.Summary();
			long bytesOut = _net.Statistics.BytesSent, bytesIn = _net.Statistics.BytesReceived;
			long alloc = GC.GetTotalAllocatedBytes(false);
			double outKb = (bytesOut - _lastBytesOut) / 1024.0 / elapsed;
			double inKb = (bytesIn - _lastBytesIn) / 1024.0 / elapsed;
			double allocMb = (alloc - _lastAlloc) / 1048576.0 / elapsed;
			_lastBytesOut = bytesOut; _lastBytesIn = bytesIn; _lastAlloc = alloc;
			double avgSnap = _snapshotCount > 0 ? _snapshotBytesTotal / _snapshotCount : 0;
			_snapshotBytesTotal = 0; _snapshotCount = 0;
			double gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;

			var inv = System.Globalization.CultureInfo.InvariantCulture;
			_lastReport = string.Format(inv,
				"{{\"players\":{0},\"bots\":{16},\"kills\":{17},\"tickHz\":{1},\"tickMsMean\":{2:F3},\"tickMsP50\":{3:F3},\"tickMsP99\":{4:F3},\"tickMsMax\":{5:F3},\"overruns\":{6},\"outKBps\":{7:F1},\"inKBps\":{8:F1},\"avgSnapshotBytes\":{9:F0},\"allocMBps\":{10:F2},\"gc0\":{11},\"gc1\":{12},\"gc2\":{13},\"gcPauseMs\":{14:F1},\"loot\":{15}}}",
				_players.Count, _m.TickRate, mean, p50, p99, max, _metrics.Overruns, outKb, inKb, avgSnap, allocMb,
				GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), gcPause, ActiveLootCount(), BotCount, TotalKills);
			Log($"joueurs {_players.Count} ({BotCount} bots) | tick moy {mean:F2} ms, p99 {p99:F2} ms, max {max:F2} ms (budget {1000.0 / _m.TickRate:F1}) | dépassements {_metrics.Overruns} | sortie {outKb:F0} Ko/s, entrée {inKb:F0} Ko/s | snapshot moyen {avgSnap:F0} o | alloc {allocMb:F2} Mo/s, GC0 {GC.CollectionCount(0)}, pause GC {gcPause:F0} ms");
		}

		private int ActiveLootCount()
		{
			int n = 0;
			for (int i = 0; i < _lootHigh; i++) if (_loot[i].Active) n++;
			return n;
		}

		public static void Log(string message)
		{
			Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
		}
	}
}
