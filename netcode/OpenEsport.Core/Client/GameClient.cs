using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using LiteNetLib;

namespace OpenEsport.Core
{
	public enum NetState { Disconnected, Connecting, Joining, Playing, Failed }

	/// <summary>
	/// Client réseau, indépendant du moteur : connexion, prédiction du déplacement, réconciliation avec le serveur,
	/// interpolation des autres joueurs. Godot ne fait que fournir les entrées et afficher le résultat.
	/// </summary>
	public sealed class GameClient
	{
		private readonly IWorld _world;
		private readonly uint _worldChecksum;
		private readonly EventBasedNetListener _listener = new EventBasedNetListener();
		private readonly NetManager _net;
		private NetPeer _peer;
		private readonly BitWriter _writer = new BitWriter(1400);
		private readonly BitReader _reader = new BitReader();
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private string _joinName = "Joueur";
		private string _joinToken;
		private byte[] _joinAppearance;

		private readonly Dictionary<ushort, RemoteEntity> _entities = new Dictionary<ushort, RemoteEntity>();
		private readonly List<ushort> _scratchIds = new List<ushort>();
		private readonly List<PlayerInput> _pending = new List<PlayerInput>();
		private ushort _nextSeq = 1;

		// acquittement des snapshots
		private ushort _lastSnapSeq;
		private uint _recvBits;
		private bool _anySnap;

		// horloge d'affichage
		private uint _latestServerTick;
		private double _latestArrival;
		private float _renderTick;
		private bool _clockInit;

		public NetState State { get; private set; } = NetState.Disconnected;
		/// <summary>Observateur d'une partie de tournoi : il ne joue pas, il suit un joueur (voir <see cref="Follow"/>).</summary>
		public bool Spectating { get; private set; }
		public string FailReason { get; private set; } = "";

		// session
		public ushort PlayerId { get; private set; }
		public byte TeamId { get; private set; }
		public int TickRate { get; private set; } = 60;
		public int SnapshotDivisor { get; private set; } = 2;
		public int InterpTicks { get; private set; } = 4;
		public int TeamSize { get; private set; }
		public int MaxPlayers { get; private set; }
		public Quantizer Quant { get; private set; }
		public float Dt => 1f / TickRate;

		// état du joueur local
		public PlayerState Predicted;
		public PlayerState Authoritative;
		public Vec3 VisualOffset;
		public byte Health { get; private set; } = 100;
		/// <summary>Points de bouclier, niveau du bouclier équipé (−1 = aucun), soin en cours d'utilisation (0 = aucun) et son avancement de 0 à 1.</summary>
		/// <summary>Un câble est à portée de main (touche E pour le saisir) ?</summary>
		public bool ZiplineInReach
		{
			get
			{
				var s = Predicted;
				if (!Alive || s.Zip != 0 || s.ZipCooldown > 0f) return false;
				var hand = new Vec3(s.Pos.X, s.Pos.Y + Sim.ZipHoldHeight, s.Pos.Z);
				return _world.FindZipline(hand, Sim.ZipGrabHorizontal, Sim.ZipGrabVertical, -1, out _, out _);
			}
		}

		public byte Shield { get; private set; }
		public int ShieldTier { get; private set; }
		public byte UsingItem { get; private set; }
		public float UseProgress { get; private set; }
		/// <summary>Multiplicateur de vitesse de déplacement : à mains nues on court plus vite (comme le calcule le serveur d'après l'arme en main).</summary>
		private float MoveScale => BodyBalance.Speed(_joinAppearance) * (Inventory.Active == null ? GameBalance.UnarmedMultiplier : 1f);
		public bool Alive { get; private set; } = true;
		/// <summary>Dernier état de partie reçu (phase, vivants, statistiques…).</summary>
		public MatchStatus Match { get; private set; }
		/// <summary>État propre au mode de jeu (hors battle royale) : scores, minuteur, points de contrôle, drapeaux, bombe, argent.</summary>
		public ModeState Mode { get; private set; } = ModeState.Empty;
		public bool ModeKnown { get; private set; }
		/// <summary>Passager d'une chute en équipe : le serveur dirige ma chute avec les commandes du chef de saut ; je ne prédis rien, je suis l'état du serveur.</summary>
		public bool Passenger => MatchKnown && Match.ControlledBy != 0xFFFF && (Predicted.Stance == Stance.Plane || Predicted.Stance == Stance.Diving);
		/// <summary>Dernier état de la zone reçu (voir <see cref="ZoneState.CircleAt"/> avec <see cref="EstimatedServerTick"/>).</summary>
		public ZoneState Zone { get; private set; }
		public bool ZoneKnown { get; private set; }
		/// <summary>Tick du serveur estimé à cet instant (fractionnaire) : sert à calculer la réduction de la zone image par image.</summary>
		public float EstimatedServerTick => _latestServerTick + (float)((_clock.Elapsed.TotalSeconds - _latestArrival) * TickRate);
		/// <summary>Vrai dès que le serveur a envoyé un état de partie (serveur « battle royale ») ; sinon partie classique.</summary>
		public bool MatchKnown { get; private set; }
		public event Action MatchChanged;
		/// <summary>Chargeur de l'arme en main, tel que le serveur le compte.</summary>
		public byte Magazine { get; private set; }
		public bool Reloading { get; private set; }
		/// <summary>Emplacement (-1 : aucun) et arme auxquels se rapportent <see cref="Magazine"/> et <see cref="Reloading"/> : pendant un changement d'arme, l'inventaire
		/// et les instantanés n'arrivent pas ensemble.</summary>
		public int MagazineSlot { get; private set; } = -1;
		public byte MagazineWeapon { get; private set; }
		public InventoryState Inventory { get; private set; } = new InventoryState();
		/// <summary>Grenades et zones de feu proches, telles que le serveur vient de les décrire (liste vide : plus rien).</summary>
		public List<GrenadeInfo> Grenades { get; private set; } = new List<GrenadeInfo>();
		public List<RosterEntry> Roster { get; private set; } = new List<RosterEntry>();

		/// <summary>Position à afficher pour le joueur local : prédiction + correction lissée.</summary>
		public Vec3 ViewPosition => Predicted.Pos + VisualOffset;

		// statistiques
		public uint ClientTick { get; private set; }
		public int SnapshotsReceived { get; private set; }
		public int Corrections { get; private set; }
		/// <summary>Corrections de moins de 3 m : de vraies divergences de prédiction (les plus grandes sont des mort/réapparitions).</summary>
		public int SmallCorrections { get; private set; }
		public float MaxSmallCorrection { get; private set; }
		public float MaxCorrection { get; private set; }
		public int Ping => _peer?.Ping ?? 0;
		public long BytesReceived => _net.Statistics.BytesReceived;
		public long BytesSent => _net.Statistics.BytesSent;
		public int EntityCount => _entities.Count;

		/// <summary>Diagnostic : appelé à chaque divergence de prédiction.</summary>
		public Action<string> DebugLog;
		/// <summary>Appelé quand le traitement d'un paquet a échoué.</summary>
		public Action<Exception> ErrorLog;
		public event Action Joined;
		public event Action<ServerEvent> EventReceived;
		public event Action RosterChanged;
		public event Action InventoryChanged;
		public event Action GrenadesChanged;
		public event Action<string> Disconnected;

		public float RenderTick => _renderTick;
		/// <summary>Tick du serveur que le joueur voit pour les autres joueurs (envoyé avec ses tirs pour la compensation de latence).</summary>
		public uint ViewTick => (uint)MathF.Max(0, MathF.Round(_renderTick));

		public GameClient(IWorld world, uint worldChecksum, int updateTimeMs = 1)
		{
			_world = world;
			_worldChecksum = worldChecksum;
			_net = new NetManager(_listener)
			{
				UpdateTime = updateTimeMs,
				AutoRecycle = true,
				EnableStatistics = true,
				DisconnectTimeout = 5000,
				UnsyncedEvents = false,
			};
			_listener.PeerConnectedEvent += OnConnected;
			_listener.PeerDisconnectedEvent += OnDisconnected;
			_listener.NetworkReceiveEvent += OnReceive;
		}

		/// <summary>Rejoint une partie de tournoi en observateur (organisateur ou observateur invité) : le serveur vérifie le compte du jeton.</summary>
		public void ConnectSpectator(string host, int port, string token)
		{
			Spectating = true;
			Connect(host, port, "", null, token);
		}

		/// <summary>Observateur : le joueur à suivre (0xFFFF : le suivant). Le serveur envoie alors ce qui se passe autour de lui.</summary>
		public void Follow(ushort player)
		{
			if (State != NetState.Playing || !Spectating) return;
			_writer.Reset();
			Messages.WriteFollow(_writer, player);
			_peer?.Send(_writer.Buffer, 0, _writer.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		/// <summary>Observateur : un tick sans commande, qui acquitte les snapshots reçus et avance l'horloge d'affichage.</summary>
		public void TickSpectator()
		{
			if (State != NetState.Playing) return;
			_pending.Clear();
			_writer.Reset();
			Messages.WriteInputs(_writer, _lastSnapSeq, _recvBits, _pending);
			_peer?.Send(_writer.Buffer, 0, _writer.ByteLength, DeliveryMethod.Sequenced);
			ClientTick++;
			AdvanceRenderClock();
		}

		public void Connect(string host, int port, string name, byte[] appearance = null, string token = null)
		{
			_joinToken = token;
			_joinName = name;
			_joinAppearance = appearance == null ? null : (byte[])appearance.Clone();
			State = NetState.Connecting;
			_net.Start();
			_peer = _net.Connect(host, port, Protocol.ConnectionKey);
		}

		public void Disconnect()
		{
			_net.Stop();
			State = NetState.Disconnected;
		}

		/// <summary>À appeler à chaque tick, avant <see cref="Tick"/> : traite les paquets reçus.</summary>
		public void Poll() { _net.PollEvents(); }

		// ------------------------------------------------------------------ réseau

		private void OnConnected(NetPeer peer)
		{
			State = NetState.Joining;
			_writer.Reset();
			if (Spectating) Messages.WriteSpectate(_writer, _joinToken);
			else Messages.WriteJoin(_writer, _joinName, _joinAppearance, _joinToken);
			peer.Send(_writer.Buffer, 0, _writer.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		private void OnDisconnected(NetPeer peer, DisconnectInfo info)
		{
			// le serveur joint le motif d'un refus au paquet de déconnexion
			if (State != NetState.Failed && info.AdditionalData.AvailableBytes > 0)
			{
				try
				{
					var data = info.AdditionalData.GetRemainingBytes();
					_reader.Init(data, 0, data.Length);
					if ((MsgType)_reader.ReadByte() == MsgType.JoinReject) { string why = _reader.ReadString(); if (!_reader.Overflow) { FailReason = why; State = NetState.Failed; } }
				}
				catch (Exception) { }
			}
			if (State != NetState.Failed) State = NetState.Disconnected;
			Disconnected?.Invoke(info.Reason.ToString());
		}

		private void Fail(string reason)
		{
			FailReason = reason;
			State = NetState.Failed;
			_peer?.Disconnect();
		}

		private void OnReceive(NetPeer peer, NetPacketReader packet, byte channel, DeliveryMethod method)
		{
			if (packet.UserDataSize < 1) return;
			_reader.Init(packet.RawData, packet.UserDataOffset, packet.UserDataSize);
			try
			{
				switch ((MsgType)_reader.ReadByte())
				{
					case MsgType.ModeState: { var mo = ModeState.Read(_reader); if (!_reader.Overflow) { Mode = mo; ModeKnown = true; } break; }
					case MsgType.Zone: { var zs = ZoneState.Read(_reader); if (!_reader.Overflow) { Zone = zs; ZoneKnown = true; } break; }
					case MsgType.Match: { var ms = MatchStatus.Read(_reader); if (!_reader.Overflow) { Match = ms; MatchKnown = true; MatchChanged?.Invoke(); } break; }
					case MsgType.Balance: { var v = Messages.ReadBalance(_reader); if (v != null) GameBalance.ApplyWire(v); break; } // réglages du serveur
					case MsgType.JoinAccept: HandleJoinAccept(); break;
					case MsgType.JoinReject: Fail(_reader.ReadString()); break;
					case MsgType.Snapshot: HandleSnapshot(); break;
					case MsgType.Event:
					{
						var e = ServerEvent.Read(_reader);
						if (!_reader.Overflow)
						{
							if (e.Type == EventType.WindowChanged && _world is StaticWorld world && e.A < world.Windows.Length)
								world.SetWindow(e.A, new WindowState(e.B, e.C != 0));
							EventReceived?.Invoke(e);
						}
						break;
					}
					case MsgType.Roster: { var r = Messages.ReadRoster(_reader); if (!_reader.Overflow) { Roster = r; RosterChanged?.Invoke(); } break; }
					case MsgType.Grenades: { var gl = Messages.ReadGrenades(_reader); if (!_reader.Overflow) { Grenades = gl; GrenadesChanged?.Invoke(); } break; }
					case MsgType.Inventory: { var inv = InventoryState.Read(_reader); if (!_reader.Overflow) { Inventory = inv; InventoryChanged?.Invoke(); } break; }
				}
			}
			catch (Exception e)
			{
				// un paquet invalide ne doit jamais faire tomber le client, mais l'erreur est signalée
				ErrorLog?.Invoke(e);
			}
		}

		private void HandleJoinAccept()
		{
			var m = JoinAccept.Read(_reader);
			if (_reader.Overflow) return;
			if (m.WorldChecksum != _worldChecksum)
			{
				Fail("La carte du serveur ne correspond pas à celle du client");
				return;
			}
			// The initial state is intact. The server follows JoinAccept with changes for this match.
			if (_world is StaticWorld world)
				for (int i = 0; i < world.Windows.Length; i++)
					if (!world.Windows[i].IsNew) world.SetWindow(i, WindowState.New);
			PlayerId = m.PlayerId;
			TeamId = m.Team;
			TickRate = m.TickRate;
			SnapshotDivisor = m.SnapshotDivisor;
			InterpTicks = m.InterpTicks;
			TeamSize = m.TeamSize;
			MaxPlayers = m.MaxPlayers;
			Quant = new Quantizer(m.MinX, m.MinY, m.MinZ);
			Predicted = Authoritative = Spectating ? default : m.Spawn;          // l'observateur n'a pas de personnage
			_latestServerTick = m.ServerTick;
			_latestArrival = _clock.Elapsed.TotalSeconds;
			_renderTick = m.ServerTick;
			_clockInit = false;
			State = NetState.Playing;
			Joined?.Invoke();
		}

		private void HandleSnapshot()
		{
			uint tick = _reader.ReadUInt32();
			ushort seq = _reader.ReadUInt16();
			bool hasOwn = _reader.ReadBool();
			ushort lastProcessed = 0;
			PlayerState own = default;
			byte health = 0, shield = 0, usingItem = 0, useProgress = 0;
			int shieldTier = 0;
			bool alive = true;
			byte magazine = 0;
			bool reloading = false;
			int magazineSlot = -1;
			byte magazineWeapon = 0;
			if (hasOwn)
			{
				lastProcessed = _reader.ReadUInt16();
				own = PlayerState.Read(_reader);
				health = (byte)_reader.ReadBits(8);
				shield = (byte)_reader.ReadBits(8);
				shieldTier = (int)_reader.ReadBits(3) - 1;
				usingItem = _reader.ReadByte();
				useProgress = _reader.ReadByte();
				alive = _reader.ReadBool();
				magazine = _reader.ReadByte();
				reloading = _reader.ReadBool();
				magazineSlot = (int)_reader.ReadBits(2) - 1;
				magazineWeapon = _reader.ReadByte();
			}
			int removedCount = (int)_reader.ReadBits(8);
			_scratchIds.Clear();
			for (int i = 0; i < removedCount; i++) _scratchIds.Add(_reader.ReadUInt16());
			int entityCount = (int)_reader.ReadBits(16);
			if (_reader.Overflow || entityCount > ClientReplication.MaxEntityIds) return;

			// on lit tout avant d'appliquer, pour ignorer un paquet tronqué
			var states = new EntityState[entityCount];
			for (int i = 0; i < entityCount; i++)
			{
				states[i] = EntityState.Read(_reader);
				if (_reader.Overflow || states[i].Id >= ClientReplication.MaxEntityIds) return;
			}

			// acquittement (champ de bits des 32 snapshots précédents)
			if (!_anySnap)
			{
				_recvBits = 0;
				_lastSnapSeq = seq;
				_anySnap = true;
			}
			else if (Seq.Newer(seq, _lastSnapSeq))
			{
				int d = (ushort)(seq - _lastSnapSeq);
				_recvBits = d >= 33 ? 0u : ((d >= 32 ? 0u : _recvBits << d) | (1u << (d - 1)));
				_lastSnapSeq = seq;
			}
			else return; // snapshot plus ancien que le dernier : ignoré

			SnapshotsReceived++;
			_latestServerTick = tick;
			_latestArrival = _clock.Elapsed.TotalSeconds;

			for (int i = 0; i < _scratchIds.Count; i++) _entities.Remove(_scratchIds[i]);
			for (int i = 0; i < entityCount; i++)
			{
				if (!_entities.TryGetValue(states[i].Id, out var e))
				{
					e = new RemoteEntity { Id = states[i].Id, Kind = states[i].Kind };
					_entities[e.Id] = e;
				}
				e.Push(tick, states[i], SnapshotDivisor);
			}

			if (hasOwn)
			{
				bool aliveChanged = alive != Alive;
				Health = health;
				Shield = shield;
				ShieldTier = shieldTier;
				UsingItem = usingItem;
				UseProgress = useProgress / 255f;
				Alive = alive;
				Magazine = magazine;
				Reloading = reloading;
				MagazineSlot = magazineSlot;
				MagazineWeapon = magazineWeapon;
				Reconcile(lastProcessed, own, aliveChanged);
			}
		}

		// ------------------------------------------------------------------ prédiction

		private void Reconcile(ushort lastProcessed, in PlayerState authoritative, bool aliveChanged)
		{
			Authoritative = authoritative;
			if (!Alive)
			{
				// mort : le serveur ignore nos commandes, l'état est figé
				_pending.Clear();
				Predicted = authoritative;
				VisualOffset = Vec3.Zero;
				return;
			}

			// passager : pas de prédiction possible (le serveur utilise les commandes du chef) ; on prolonge l'état du serveur du temps qu'il met à nous parvenir
			if (Passenger)
			{
				int dr = 0;
				while (dr < _pending.Count && !Seq.Newer(_pending[dr].Seq, lastProcessed)) dr++;
				if (dr > 0) _pending.RemoveRange(0, dr);
				var ps = authoritative;
				for (int i = 0; i < _pending.Count; i++) ExtrapolatePassenger(ref ps);
				Vec3 pd = Predicted.Pos - ps.Pos;
				if (pd.Length() > 0.002f) VisualOffset = pd.Length() > 3f ? Vec3.Zero : VisualOffset + pd;
				Predicted = ps;
				return;
			}

			// les commandes déjà traitées par le serveur ne servent plus
			int drop = 0;
			while (drop < _pending.Count && !Seq.Newer(_pending[drop].Seq, lastProcessed)) drop++;
			if (drop > 0) _pending.RemoveRange(0, drop);

			// on repart de l'état du serveur et on rejoue ce qu'il n'a pas encore vu
			var s = authoritative;
			if (Alive && Mode.Ride(PlayerId, out _) == null)
				for (int i = 0; i < _pending.Count; i++)
					{
						PlayerMovement.Step(ref s, _pending[i], _world, Dt, MoveScale);
						ControlVehicle.Separate(Mode.Vehicles,ref s);
					}

			if (aliveChanged)
			{
				// réapparition : on adopte l'état du serveur, en rejouant tout de même les commandes qu'il n'a pas encore vues
				Predicted = s;
				VisualOffset = Vec3.Zero;
				return;
			}

			Vec3 diff = Predicted.Pos - s.Pos;
			float err = diff.Length();
			if (err > 0.002f)
			{
				Corrections++;
				if (err > MaxCorrection) MaxCorrection = err;
				if (err <= 3f) { SmallCorrections++; if (err > MaxSmallCorrection) MaxSmallCorrection = err; }
				DebugLog?.Invoke($"err={err:F3} pending={_pending.Count} authStance={authoritative.Stance} predStance={Predicted.Stance} authGround={authoritative.OnGround} predGround={Predicted.OnGround} authVel={authoritative.Vel} predVel={Predicted.Vel} lastProc={lastProcessed} nextSeq={_nextSeq} slide={authoritative.SlideSpeed:F1}");
				// grosse différence (téléportation, réapparition) : on s'y place ; sinon on lisse
				VisualOffset = err > 3f ? Vec3.Zero : VisualOffset + diff;
			}
			Predicted = s;
		}

		/// <summary>
		/// Un tick de simulation locale : prédit le mouvement, mémorise la commande et l'envoie (avec les précédentes, au cas où un paquet se perde).
		/// </summary>
		/// <summary>Objet sous le viseur au moment d'interagir (voir <see cref="PlayerInput.Target"/>) : le client choisit ce qu'il montre, le serveur vérifie.</summary>
		public ushort InteractTarget;
		/// <summary>Le joueur vient de passer un téléporteur : direction (lacet) de la sortie, vers laquelle tourner la caméra.</summary>
		public event Action<float> Teleported;

		public void Tick(float forward, float strafe, Buttons buttons, int slot, float yaw, float pitch)
		{
			if (State != NetState.Playing) return;
			if (Spectating) { TickSpectator(); return; }          // un observateur n'envoie pas de commande

			var input = PlayerInput.Create(_nextSeq++, forward, strafe, buttons, slot, yaw, pitch, ViewTick, InteractTarget);
			if (Alive && Mode.Ride(PlayerId, out _) == null)
			{
				if (Passenger) ExtrapolatePassenger(ref Predicted);
				else
				{
					var before = Predicted.Pos;
					PlayerMovement.Step(ref Predicted, input, _world, Dt, MoveScale);
					ControlVehicle.Separate(Mode.Vehicles,ref Predicted);
					// téléporteur : la caméra se tourne vers la sortie (la vitesse, elle, a déjà été orientée par la simulation)
					if (Vec3.Distance(before, Predicted.Pos) > 4f && _world is StaticWorld sw && sw.TeleportExit(Predicted.Pos, out float exitYaw))
					{
						VisualOffset = Vec3.Zero;
						Teleported?.Invoke(exitYaw);
					}
				}
			}
			_pending.Add(input);
			if (_pending.Count > 120) _pending.RemoveRange(0, _pending.Count - 120);
			VisualOffset = VisualOffset * MathF.Exp(-Dt * 14f);

			_writer.Reset();
			Messages.WriteInputs(_writer, _lastSnapSeq, _recvBits, _pending);
			_peer?.Send(_writer.Buffer, 0, _writer.ByteLength, DeliveryMethod.Sequenced);
			ClientTick++;
			// l'horloge d'affichage avance APRÈS l'envoi : la commande porte le tick que le joueur vient de voir à l'écran
			AdvanceRenderClock();
		}

		private void ExtrapolatePassenger(ref PlayerState s)
		{
			if (s.Stance == Stance.Plane) PlayerMovement.Step(ref s, PlayerInput.Create(0, 0f, 0f, Buttons.None, 0, 0f, 0f, 0), _world, Dt, 1f);
			else s.Pos = s.Pos + s.Vel * Dt;
		}

		private void AdvanceRenderClock()
		{
			double now = _clock.Elapsed.TotalSeconds;
			float target = _latestServerTick + (float)((now - _latestArrival) * TickRate) - InterpTicks;
			if (!_clockInit || MathF.Abs(target - _renderTick) > 10f)
			{
				_renderTick = target;
				_clockInit = true;
				return;
			}
			_renderTick += 1f;                          // avance d'un tick
			_renderTick += (target - _renderTick) * 0.1f; // se recale doucement
		}

		/// <summary>Désigne un endroit à son équipe (0 ici, 1 danger, 2 ennemi) : le serveur le renvoie aux équipiers.</summary>
		public void SendPing(byte type, Vec3 pos, ushort target = 0xFFFF)
		{
			if (State != NetState.Playing) return;
			_writer.Reset();
			Messages.WritePing(_writer, type, pos, target);
			_peer?.Send(_writer.Buffer, 0, _writer.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		/// <summary>Envoie une commande fiable (jeter un objet, équiper un viseur…).</summary>
		public void SendCommand(CommandType type, byte arg)
		{
			if (State != NetState.Playing) return;
			_writer.Reset();
			Messages.WriteCommand(_writer, type, arg);
			_peer?.Send(_writer.Buffer, 0, _writer.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		/// <summary>Remplit la liste avec les autres entités, interpolées à l'instant d'affichage. Le joueur local n'y figure pas.</summary>
		/// <param name="alpha">Fraction de tick écoulée depuis le dernier <see cref="Tick"/> (0 à 1), pour un affichage continu entre deux ticks.</param>
		public void SampleEntities(List<InterpolatedEntity> output, float alpha = 0f)
		{
			output.Clear();
			var q = Quant;
			bool airborne = Predicted.Stance == Stance.Plane || Predicted.Stance == Stance.Diving;
			foreach (var e in _entities.Values)
			{
				if (e.Id == PlayerId && e.Kind == EntityKind.Player) continue;
				if (!e.Sample(_renderTick + alpha, q, out var s)) continue;
				// en chute : le joueur local est prédit « maintenant », les autres sont vus dans le passé (retard d'interpolation + trajet réseau). À 50 m/s, ça les laissait loin derrière :
				// on avance les équipiers en vol de ce retard pour qu'ils apparaissent à côté
				if (airborne && s.Kind == EntityKind.Player && s.Team == TeamId && !s.Has(EntityFlags.OnGround) && !s.Has(EntityFlags.Dead))
					s.Pos = s.Pos + s.Vel * (InterpTicks * Dt + (Passenger ? 0f : Ping / 1000f));
				output.Add(s);
			}
		}

		public void RequestSendNow() { _net.TriggerUpdate(); }
	}
}
