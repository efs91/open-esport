using Openesport.Utils;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using Openesport.Inventory;
using OpenEsport.Core;
using Openesport.Diagnostics;

namespace Openesport.Online
{
	/// <summary>
	/// Partie en ligne côté Godot. Le serveur décide ; ici on envoie les commandes du joueur, on prédit son déplacement,
	/// et on affiche ce que le serveur renvoie : les autres joueurs en entier, le butin, les événements.
	/// Le joueur local ne voit que ses bras : son corps entier n'existe pas chez lui.
	/// </summary>
	public partial class OnlineGame : Node, IInventoryNetwork
	{
		public const string DefaultWorldPath = "res://assets/maps/frontiere01.world";

		/// <summary>Fichier .world (relief et obstacles pour le serveur et la prédiction) qui accompagne une scène de carte : res://assets/maps/&lt;nom de la scène en minuscules&gt;.world.</summary>
		public static string WorldPathFor(string mapScenePath) => "res://assets/maps/" + mapScenePath.GetFile().GetBaseName().ToLowerInvariant() + ".world";

		public static OnlineGame Instance { get; private set; }

		public GameClient Client { get; private set; }

		private Player _player;
		private WeaponManager _weapons;
		private PlayerInventory _inventory;
		private Node3D _head;
		private OnlineHud _hud;
		private Node _worldRoot;

		private readonly Dictionary<ushort, RemotePlayer> _remotes = new();
		private readonly Dictionary<ushort, GroundItem> _loot = new();
		private readonly Dictionary<ushort, LootChest> _chests = new();
		// obstacles des coffres présents (ils dépendent du tirage de la partie : on les pose dans la collision quand le coffre arrive, comme le serveur)
		private readonly Dictionary<ushort, OpenEsport.Core.Aabb> _chestBoxes = new();
		private bool _chestBoxesDirty;
		
		/// <summary>Rotation d'un coffre : celle du point de la carte (éditeur) posé à cet endroit (le serveur ne la réplique pas : le fichier de carte est le même).</summary>
		private float ChestYawAt(Vec3 pos)
		{
			var spawns = _worldCollision?.Data.LootSpawns;
			if (spawns == null) return 0f;
			foreach (var s in spawns)
				if (s.IsChest && MathF.Abs(s.Pos.X - pos.X) < 0.05f && MathF.Abs(s.Pos.Z - pos.Z) < 0.05f) return s.Yaw;
			return 0f;
		}
		private readonly List<InterpolatedEntity> _seen = new();
		private readonly HashSet<ushort> _live = new();
		private readonly Dictionary<int, RosterEntry> _roster = new();

		private Vec3 _prevView, _currView;
		private bool _hasView;
		private int _interactTicks;
		private bool _aimLocked, _diveHeadingLocked;
		private float _lockedYaw, _lockedPitch;
		private int _slotRequest;
		private bool _wasAlive = true;

		public static StaticWorld LoadWorld(string path, out uint checksum)
		{
			var bytes = Godot.FileAccess.GetFileAsBytes(path);
			if (bytes == null || bytes.Length == 0) throw new FileNotFoundException("Carte introuvable : " + path);
			var data = WorldData.Load(new MemoryStream(bytes));
			checksum = data.Checksum();
			return new StaticWorld(data);
		}

		/// <summary>Se connecte à un serveur et prend le contrôle du joueur local.</summary>
		public void Begin(Player player, Node worldRoot, string host, int port, string playerName, string worldPath = DefaultWorldPath)
		{
			Instance = this;
			System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
			LagTracer.Start(this);       // boîte noire des à-coups : logs/lag_trace.log
			LagTracer.NetState = () => Client?.State.ToString() ?? "";
			LagTracer.SnapshotCount = () => Client?.SnapshotsReceived ?? 0;
			LagTracer.Event("partie en ligne : connexion à " + host + ":" + port);
			_player = player;
			_worldRoot = worldRoot;
			_head = player.GetNode<Node3D>("Head");
			// plan proche plus serré : sur une pente raide, le relief juste devant l'œil n'est plus coupé
			var eye = _head.GetNodeOrNull<Camera3D>("Camera");
			if (eye != null) eye.Near = 0.03f;
			_weapons = player.GetNode<WeaponManager>("Head/Camera/WeaponManager");
			_inventory = player.Inventory;

			// le serveur est seul maître du déplacement, des armes et de l'inventaire
			player.NetworkControlled = true;
			_weapons.Online = true;
			_inventory.Network = this;

			_hud = new OnlineHud { Name = "OnlineHud" };
			AddChild(_hud);
			_grenadeView = new GrenadeView { Name = "Grenades" };
			worldRoot.AddChild(_grenadeView);
			_damageNumbers = new DamageNumbers { Name = "Degats" };
			worldRoot.AddChild(_damageNumbers);
			_slideSound = new SlideSound { Name = "Glissade", Positional = false, VolumeDb = -4f };
			AddChild(_slideSound);
			_zipSound = new ZipSound { Name = "Tyrolienne", Positional = false };
			AddChild(_zipSound);
			// ambiance de la carte (bas mais permanent) et bruit de l'avion tant qu'on est à bord
			_ambience = new AudioStreamPlayer { Name = "Ambiance", Stream = GameAudio.Looping("res://assets/sounds/background/background_forest_loop.ogg"), VolumeDb = AmbienceDb, Bus = "Master" };
			AddChild(_ambience);
			_ambience.Play();
			_planeSound = new AudioStreamPlayer { Name = "Avion", Stream = GameAudio.Looping("res://assets/sounds/avion/avion_2.mp3"), VolumeDb = -6f, Bus = "Master" };
			AddChild(_planeSound);
			_fallSound = new AudioStreamPlayer { Name = "ChuteLibre", Stream = GameAudio.Looping("res://assets/sounds/avion/chute libre.mp3"), VolumeDb = -10f, Bus = "Master" };
			AddChild(_fallSound);
			InventoryUI.HoldNeeded = item => Client != null && GodotObject.IsInstanceValid(item) && NeedsHold(item);
			_flash = new FlashOverlay { Name = "Eclair" };
			AddChild(_flash);
			_radial = new RadialMenu { Name = "MenuCirculaire" };
			AddChild(_radial);
			_preview = new GrenadePreview { Name = "TrajectoireGrenade" };
			worldRoot.AddChild(_preview);
			_pingView = new PingView { Name = "Pings" };
			worldRoot.AddChild(_pingView);
			_pingView.Sync(_pings);
			_hud.OnPingCancel = who => Client.SendPing(255, Vec3.Zero, who);      // clic gauche sur un ping dans la grande carte : on le retire (les bots cessent de l'honorer)
			_hud.SetStatus(I18n.T("Connexion à {0}:{1}…", host, port), 0f);

			var world = LoadWorld(worldPath, out uint checksum);
			_worldCollision = world;
			BindWindows(worldRoot);
			Client = new GameClient(world, checksum, updateTimeMs: 1);
			var mapTex = GD.Load<Texture2D>(worldPath.GetBaseName() + ".png");    // dessin de la carte (tools/map/gen_minimap.py)
			if (mapTex != null) _hud.SetMap(mapTex, world.Data.MinX, world.Data.MaxX, world.Data.MinZ, world.Data.MaxZ);
			_weapons.WarmUp();
			_hud.SetLoading(true, I18n.T("Chargement des armes et des objets…"));
			Client.Joined += OnJoined;
			Client.EventReceived += OnServerEvent;
			// téléporteur : la caméra se tourne vers la sortie
			Client.Teleported += yaw =>
			{
				_player.Rotation = new Vector3(_player.Rotation.X, yaw, _player.Rotation.Z);
				var p = Client.Predicted.Pos;
				GameAudio.Teleport(_worldRoot, new Vector3(p.X, p.Y + 1f, p.Z));
			};
			Client.InventoryChanged += () => _inventory.ApplyServerInventory(Client.Inventory);
			Client.GrenadesChanged += () => _grenadeView.Apply(Client.Grenades);
			Client.RosterChanged += RebuildRoster;
			Client.Disconnected += reason => { _disconnectReason = reason; _hud.SetStatus(I18n.T("Déconnecté : {0}", I18n.T(reason)), 0f); };
			_hud.OnLeave = LeaveToMenu;
			Client.ErrorLog = e => GD.PrintErr("[OnlineGame] erreur de traitement d'un paquet : " + e);
			// reprise de partie : retenue tant qu'on y joue (voir MatchSettings.TryResume)
			Client.Joined += () => { if (!Spectating) MatchSettings.RememberMatch(host, port); _resumeClock = 0f; };
			if (Spectating) Client.ConnectSpectator(host, port, AccountClient.Token);          // observateur d'un tournoi : on regarde sans jouer
			else Client.Connect(host, port, playerName, CharacterAppearance.Current.Pack(), AccountClient.Token);
		}

		private float _resumeClock;

		/// <summary>Reprise de partie : présence notée toutes les 5 s ; oubliée en fin de partie.</summary>
		private void TrackResume(float delta)
		{
			if (Client == null || Client.State != NetState.Playing || Spectating) return;
			if (Client.MatchKnown && Client.Match.Phase == MatchPhase.Over) { MatchSettings.ForgetMatch(); return; }
			_resumeClock -= delta;
			if (_resumeClock <= 0f) { _resumeClock = 5f; MatchSettings.TouchMatch(); }
		}

		public override void _ExitTree()
		{
			// départ volontaire (encore connecté) ou retour refusé : pas de reprise. Coupure réseau : la reprise reste possible.
			if (Client != null && (Client.State == NetState.Playing || Client.State == NetState.Failed)) MatchSettings.ForgetMatch();
			InventoryUI.HoldNeeded = null;
			Client?.Disconnect();
			if (Instance == this) Instance = null;
		}

		// ------------------------------------------------------------------ IInventoryNetwork

		public void RequestInteract()
		{
			// une arme qui remplacerait la sienne (emplacements pleins), ou un bouclier moins bon que le sien : il faut maintenir la touche
			var target = _inventory.Target;
			bool hold = target != null && GodotObject.IsInstanceValid(target) && NeedsHold(target);
			if (hold) { StartHold(target, 0, SwapHoldSeconds, null); return; }
			// vitre visée : réparer (3 s) ou renforcer (5 s) en maintenant la touche, avec son bruit
			ushort aimed = Client?.InteractTarget ?? PlayerInput.NoTarget;
			if ((target == null || !GodotObject.IsInstanceValid(target)) && aimed != PlayerInput.NoTarget && aimed >= WindowState.TargetBase)
			{
				int code = aimed - WindowState.TargetBase, pane = code % WindowState.MaxPanes;
				bool reinforce = code >= WindowState.MaxPanes;
				if (pane < _windowViews.Count)
				{
					StartHold(null, aimed, reinforce ? ReinforceSeconds : RepairSeconds,
						reinforce ? "res://assets/sounds/fenetre/renfort_fenetre.mp3" : "res://assets/sounds/fenetre/reparation.mp3", _windowViews[pane].GlobalPosition);
					return;
				}
			}
			_interactTicks = 2;
		}

		/// <summary>Durée de maintien de la touche d'interaction pour échanger son arme ou prendre un bouclier de niveau inférieur.</summary>
		public const float SwapHoldSeconds = 2f;
		/// <summary>Durées de maintien pour réparer une vitre et pour la renforcer (celles de leurs sons).</summary>
		public const float RepairSeconds = 3f, ReinforceSeconds = 5f;
		private GroundItem _holdItem;
		/// <summary>Vitre en cours de réparation ou de renfort (cible d'interaction), 0 = aucune.</summary>
		private ushort _holdWindow;
		private float _holdTime, _holdDuration;
		private AudioStreamPlayer3D _holdSound;

		private void StartHold(GroundItem item, ushort window, float seconds, string sound, Vector3 at = default)
		{
			StopHold();
			_holdItem = item; _holdWindow = window; _holdTime = 0f; _holdDuration = seconds;
			if (sound == null) return;
			_holdSound = new AudioStreamPlayer3D { Stream = GD.Load<AudioStream>(sound), MaxDistance = 40f, UnitSize = 8f, Bus = "Master" };
			_worldRoot.AddChild(_holdSound);
			_holdSound.GlobalPosition = at;
			_holdSound.Finished += _holdSound.QueueFree;
			_holdSound.Play();
		}

		/// <summary>Fin du maintien ; <paramref name="cancelled"/> : le bruit de l'action s'arrête aussi.</summary>
		private void StopHold(bool cancelled = true)
		{
			if (cancelled && _holdSound != null && GodotObject.IsInstanceValid(_holdSound)) _holdSound.QueueFree();
			_holdSound = null;
			_holdItem = null; _holdWindow = 0;
			_hud.SetHoldProgress(0f, Colors.White);
		}

		/// <summary>
		/// Ramassage à confirmer en maintenant la touche : une arme quand les deux emplacements sont pleins (elle remplace celle qu'on tient),
		/// un bouclier d'un niveau inférieur à celui qu'on porte (même vide).
		/// </summary>
		private bool NeedsHold(GroundItem item)
		{
			var def = item.Definition;
			if (def == null || Client == null) return false;
			var inv = Client.Inventory;
			if (def.Type == ItemType.Weapon) return System.Array.IndexOf(inv.Weapons, null) < 0;
			if (def.Type == ItemType.Shield) return inv.ShieldTier > item.Tier;
			return false;
		}

		/// <summary>
		/// Maintien en cours (échange d'objet, réparation ou renfort d'une vitre) : l'anneau se remplit ; touche relâchée, autre cible visée
		/// ou menu ouvert : annulé ; au bout du temps, l'action part au serveur.
		/// </summary>
		private void TickHold(float dt)
		{
			if (_holdItem == null && _holdWindow == 0) return;
			bool still = Input.IsActionPressed(InputManager.Actions.INTERACT) && Input.MouseMode == Input.MouseModeEnum.Captured
				&& (_holdItem != null ? GodotObject.IsInstanceValid(_holdItem) && _inventory.Target == _holdItem
					: Client != null && Client.InteractTarget == _holdWindow);
			if (!still) { StopHold(); return; }
			_holdTime += dt;
			if (_holdTime >= _holdDuration) { _interactTicks = 2; StopHold(cancelled: false); return; }
			_hud.SetHoldProgress(_holdTime / _holdDuration, _holdItem != null ? ItemTierStyle.ColorOf(_holdItem.Tier) : Colors.White);
		}
		public void RequestSlot(int slot) { _slotRequest = slot < 0 ? 3 : slot + 1; }
		public void SendCommand(CommandType type, int arg) { Client?.SendCommand(type, (byte)arg); }

		// ------------------------------------------------------------------ connexion

		private ulong _joinedAtMs;

		private void OnJoined()
		{
			_joinedAtMs = Time.GetTicksMsec();
			for (int i = 0; i < _windowViews.Count; i++)
				if (!_windowViews[i].Visible || _windowViews[i].GetNode<Node3D>("Reinforcement").Visible) UpdateWindowView(i);
			_prevView = _currView = Client.ViewPosition;
			_hasView = true;
			ShotTracer.WarmUp(_worldRoot);      // compile le shader des balles avant le premier tir
			GameAudio.Shot(_worldRoot, new Vector3(0, -500f, 0), true, false);   // charge les sons de tir
			RebuildRoster();
			_hud.SetStatus(Spectating ? I18n.T("Observateur — tu regardes la partie sans y jouer") : I18n.T("En ligne — équipe {0} — {1} joueurs max", Client.TeamId + 1, Client.MaxPlayers));
		}

		private void RebuildRoster()
		{
			_roster.Clear();
			foreach (var r in Client.Roster) _roster[r.Id] = r;
		}

		private string NameOf(int id) => _roster.TryGetValue(id, out var r) ? r.Name : I18n.T("Joueur {0}", id);

		/// <summary>Durée pendant laquelle un adversaire touché (ou repéré, en Sabotage) reste montré.</summary>
		private const double RevealSeconds = 3.0;
		private readonly Dictionary<ushort, double> _spottedUntil = new();
		/// <summary>Dernier état connu de chaque équipier (il peut sortir du voisinage répliqué) : le HUD le montre en permanence.</summary>
		private readonly Dictionary<ushort, MateView> _mateCache = new();

		private void OnServerEvent(ServerEvent e)
		{
			if (e.Type != EventType.Shot) LagTracer.Event($"événement serveur : {e.Type}");
			switch (e.Type)
			{
				case EventType.WindowChanged:
					if (e.A < _windowViews.Count)
					{
						bool wasUp = _windowViews[e.A].Visible;
						UpdateWindowView(e.A);
						// la vitre vient de voler en éclats (pas l'état des vitres reçu à l'arrivée dans la partie)
						if (wasUp && !_windowViews[e.A].Visible && Time.GetTicksMsec() - _joinedAtMs > 3000)
							GameAudio.GlassBreak(_worldRoot, _windowViews[e.A].GlobalPosition);
					}
					break;
				case EventType.HitConfirm: OnHitConfirm(e); break;
				// Sabotage : un adversaire touché ou vu par notre équipe apparaît 3 s sur les cartes.
				case EventType.TeamHit:
				case EventType.Spotted: _spottedUntil[e.A] = Time.GetTicksMsec() / 1000.0 + RevealSeconds; break;
				case EventType.ArenaPickup: GameAudio.Pickup(_worldRoot, new Vector3(e.Pos.X, e.Pos.Y + 0.5f, e.Pos.Z)); break;
				case EventType.Damaged: _hud.DamageFlash(Client.Shield > 0 && Client.Health >= 100); break;
				case EventType.Shot: OnShot(e); break;
				case EventType.Explosion: GrenadeFx.Explosion(_worldRoot, e.C, new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z)); break;
				case EventType.Flashed: _flash.Trigger(e.C / 10f, e.D / 255f); break;
				case EventType.Ping: OnPing(e); break;
				case EventType.KillFeed:
				{
					var weapon = e.C == 0 ? "poings" : e.C == 250 ? "zone" : ItemDatabase.Get(ItemDefs.Get(e.C)?.Name)?.DisplayName ?? ItemDefs.Get(e.C)?.Name ?? "?";
					_hud.AddFeed($"{NameOf(e.A)}  ➜  {NameOf(e.B)}   [{weapon}]");
					break;
				}
			}
		}

		/// <summary>Dernier coup de feu (ms) de chaque joueur armé d'un fusil à pompe : un seul son pour tous les plombs.</summary>
		private readonly Dictionary<ushort, ulong> _pelletShots = new();

		/// <summary>Une balle part : on la dessine depuis la bouche de l'arme, sur la même trajectoire que celle simulée par le serveur.</summary>
		private void OnShot(ServerEvent e)
		{
			LagTracer.Event(e.A == Client.PlayerId ? "mon tir" : $"tir du joueur {e.A}");
			var origin = new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z);
			var dir = new Vector3(e.Dir.X, e.Dir.Y, e.Dir.Z);
			Vector3 muzzle;
			if (e.D != 0 || Client.Mode.Ride(e.A,out _) != null) muzzle = origin;
			else if (e.A == Client.PlayerId)
			{
				var cam = _head.GetNodeOrNull<Camera3D>("Camera");
				if (cam == null) return;
				muzzle = _weapons.MuzzleGlobalPosition ?? cam.GlobalPosition + cam.GlobalBasis * new Vector3(0.12f, -0.1f, -0.7f);
			}
			else if (_remotes.TryGetValue(e.A, out var remote)) { muzzle = remote.MuzzlePosition; remote.Noise(3.0, 150f); }
			else muzzle = origin;
			// partie de la bouche du canon, dirigée vers le point que visait le tireur à 60 m (comme en partie standard)
			Vector3 aim = origin + dir * 60f;
			Vector3 d = (aim - muzzle).Normalized();
			if (d.Dot(dir) < 0.9f) d = dir;
			ShotTracer.Spawn(_worldRoot, muzzle, d, ShotTracer.ColorFor(WeaponDefs.Get(e.C)?.AmmoType ?? 0), WeaponDefs.Get(e.C)?.Speed ?? 0f, (ShotAppearance)e.D);
			if (e.D != 0) WalkerFired(e.A);
			// un seul coup de feu pour tous les plombs d'une cartouche
			if (WeaponDefs.Get(e.C)?.Pellets > 1)
			{
				ulong now = Time.GetTicksMsec();
				if (_pelletShots.TryGetValue(e.A, out var last) && now - last < 100) return;
				_pelletShots[e.A] = now;
			}
			if (e.C != 0) GameAudio.Shot(_worldRoot, muzzle, (WeaponDefs.Get(e.C)?.AmmoType ?? 0) == 1, e.A == Client.PlayerId);
		}

		// ------------------------------------------------------------------ boucle

		public override void _PhysicsProcess(double delta)
		{
			if (Client == null) return;
			using (LagTracer.Scope("Client.Poll")) Client.Poll();
			if (Client.State == NetState.Failed) { _hud.SetStatus(I18n.T("Connexion impossible : {0}", I18n.T(Client.FailReason)), 0f); _disconnectReason ??= Client.FailReason; return; }
			if (Client.State != NetState.Playing) return;
			if (Spectating) { SpectatorPhysics(); return; }

			bool inLobby = Client.MatchKnown && Client.Match.Phase == MatchPhase.Lobby;
			bool active = Input.MouseMode == Input.MouseModeEnum.Captured && Client.Alive && !inLobby;
			float forward = 0f, strafe = 0f;
			var buttons = Buttons.None;
			int slot = 0;
			if (active)
			{
				VehicleKeys();
				forward = (Input.IsActionPressed(InputManager.Actions.MOVE_FORWARD) ? 1f : 0f) - (Input.IsActionPressed(InputManager.Actions.MOVE_BACK) ? 1f : 0f);
				strafe = (Input.IsActionPressed(InputManager.Actions.MOVE_RIGHT) ? 1f : 0f) - (Input.IsActionPressed(InputManager.Actions.MOVE_LEFT) ? 1f : 0f);
				if (Input.IsActionPressed(InputManager.Actions.JUMP)) buttons |= Buttons.Jump;
				if (Input.IsActionPressed(InputManager.Actions.CROUCH)) buttons |= Buttons.Crouch;
				bool airborne = Client.Predicted.Stance == Stance.Plane || Client.Predicted.Stance == Stance.Diving;
				if (!airborne && (Input.IsActionPressed(InputManager.Actions.SPRINT) || _player.WantsSprint)) buttons |= Buttons.Sprint; // sprint « bascule » comme en solo
				if (airborne && Input.IsKeyPressed(Key.Tab)) buttons |= Buttons.PassLead;     // chef de saut : passer le commandement
				if (Input.IsActionPressed(InputManager.Actions.SHOOT) && !InputManager.ClickGuard && !InputManager.UiBlock && !airborne) buttons |= Buttons.Fire;
				if (Input.IsActionPressed(InputManager.Actions.RELOAD)) buttons |= Buttons.Reload;
				if (Client.Match.CanRevive && Input.IsActionPressed(InputManager.Actions.INTERACT)) buttons |= Buttons.Interact;     // maintenir E : réanimer
				if (_weapons.IsAiming) buttons |= Buttons.Aim;
				if (_interactTicks > 0) { buttons |= Buttons.Interact; _interactTicks--; }
				slot = _slotRequest;
			}
			_slotRequest = 0;

			// Sabotage, phase d'achat : on ne bouge pas (le serveur fait de même, la prédiction reste donc juste)
			Openesport.Inventory.InventoryUI.WalkPickup = Client.ModeKnown && Modes.UsesArenaRules(Client.Mode.Mode);
			bool frozen = Client.ModeKnown && Client.Match.Phase == MatchPhase.Playing
				&& (Client.Mode.Mode == GameMode.Sabotage && Client.Mode.Stage == (byte)SabotageStage.Buy
					|| Client.Mode.Mode == GameMode.Duel && (Client.Mode.Stage == (byte)DuelStage.Pick || Client.Mode.Stage == (byte)DuelStage.Prepare));
			if (frozen) { forward = 0f; strafe = 0f; buttons &= ~(Buttons.Jump | Buttons.Sprint | Buttons.Fire | Buttons.Crouch); }
			InputManager.AirborneLock = Client.Predicted.Stance == Stance.Plane || Client.Predicted.Stance == Stance.Diving;
			_player.NetworkVelocity = new Vector3(Client.Predicted.Vel.X, Client.Predicted.Vel.Y, Client.Predicted.Vel.Z);
			Client.InteractTarget = FindInteractTarget();
			if (Client.Match.CanRevive && Input.IsActionPressed(InputManager.Actions.INTERACT)) Client.InteractTarget = PlayerInput.NoTarget;     // rien à ramasser : on réanime
			// chute : le bouton droit fige la direction (on regarde autour sans changer de cap) ; le serveur reçoit le cap figé, la caméra reste libre
			float sendYaw = _player.GlobalRotation.Y, sendPitch = _head.Rotation.X;
			bool lockAim = active && Client.Predicted.Stance == Stance.Diving && !Client.Passenger && Input.IsActionPressed(InputManager.Actions.AIM);
			if (lockAim)
			{
				if (!_aimLocked) { _aimLocked = true; _lockedYaw = sendYaw; _lockedPitch = sendPitch; }
				sendYaw = _lockedYaw; sendPitch = _lockedPitch;
			}
			else _aimLocked = false;
			_diveHeadingLocked = lockAim;
			_diveHeld = forward > 0.1f; _diveSentPitch = sendPitch;      // pose du corps en chute libre (bras tendus ou le long du corps)
			using (LagTracer.Scope("Client.Tick")) Client.Tick(forward, strafe, buttons, slot, sendYaw, sendPitch);
			_prevView = _currView;
			_currView = Client.ViewPosition;

			// chargeur et rechargement : seulement s'ils parlent bien de l'arme montrée en main
			int magSlot = Client.MagazineSlot;
			if (magSlot >= 0 && magSlot < _inventory.Weapons.Length && _inventory.Weapons[magSlot] is { } held && held == _weapons.HeldWeapon
				&& held.ItemId == ItemDefs.Get(Client.MagazineWeapon)?.Name)
				_weapons.SetServerState(Client.Magazine, Client.Reloading);
			_hud.SetVitals(Client.Health, Client.Shield, Client.ShieldTier, Client.UsingItem, Client.UseProgress);
			_hud.SetHint(VehicleHint() ?? (Client.Match.CanRevive ? (Client.Match.Revive > 0 ? I18n.T("Réanimation…") : I18n.T("Maintenir E : réanimer ton coéquipier")) : Client.Predicted.Stance == Stance.Climbing ? I18n.T("Escalade…") : Client.Predicted.Stance == Stance.Zipping ? I18n.T("Saut : se décrocher   —   tu peux tirer") : Client.ZiplineInReach ? I18n.T("E : prendre la tyrolienne") : _windowHint ?? BombHint()));
			_weapons.SetUsingSupply(Client.UsingItem == 0 ? null : ItemDefs.Get(Client.UsingItem)?.Name);
			if (active && Client.Mode.Ride(Client.PlayerId,out _) == null) HandleSupplyKeys(); else if (_radial.IsOpen) _radial.Close();
			UpdatePings();
			UpdateGrenadeHud();
			if (_wasAlive != Client.Alive)
			{
				_wasAlive = Client.Alive;
				_hud.SetDead(!Client.Alive);
			}
		}

		/// <summary>Touches 4 / 5 / 6 : soin de vie, de bouclier, ou les deux ; G : jeter le bouclier. Même touche pendant un soin : annuler.</summary>
		private void HandleSupplyKeys()
		{
			if (_radial.IsOpen) { UpdateRadial(); return; }
			if (Input.IsActionJustPressed(InputManager.Actions.PING)) SendPing();
			if (Input.IsActionJustPressed(InputManager.Actions.GRENADE_MENU)) { OpenGrenadeRadial(); return; }
			if (Input.IsActionJustPressed(InputManager.Actions.USE_HEAL)) { OpenHealRadial(); return; }
			if (Input.IsActionJustPressed(InputManager.Actions.DROP_SHIELD)) { _inventory.DropShield(); return; }
			if (Input.IsActionJustPressed(InputManager.Actions.THROW_GRENADE)) { ThrowGrenade(); return; }
			if (Input.IsActionJustPressed(InputManager.Actions.NEXT_GRENADE)) CycleGrenade();
			if (Input.IsActionJustPressed(InputManager.Actions.DROP_BOMB) && Client.Inventory.CountOf(ItemIds.Bomb) > 0) Client.SendCommand(CommandType.DropBomb, 0);
			if (Input.IsActionJustPressed(InputManager.Actions.USE_SHIELD) && Client.Inventory.CountOf(ItemIds.Bomb) > 0) Client.SendCommand(CommandType.EquipBomb, (byte)(_inventory.BombInHand ? 0 : 1));     // Sabotage : 5 = la bombe en main
			else if (Input.IsActionJustPressed(InputManager.Actions.USE_SHIELD)) UseSupply(ItemIds.ShieldSmall, ItemIds.ShieldBig, false, true);
			else if (Input.IsActionJustPressed(InputManager.Actions.USE_BOTH)) UseSupply(ItemIds.BothSmall, ItemIds.BothBig, true, true);
		}

		// ------------------------------------------------------------------ menus circulaires

		/// <summary>Menu des soins (touche 4 maintenue) : on pointe un soin à la souris, on relâche pour l'utiliser. Un appui bref garde l'ancien raccourci (soin de vie).</summary>
		private void OpenHealRadial()
		{
			var items = new (byte id, string name, Color color)[]
			{
				(ItemIds.HealSmall, I18n.T("Petit soin"), new Color(1f, 0.35f, 0.3f)),
				(ItemIds.HealBig, I18n.T("Grand soin"), new Color(1f, 0.35f, 0.3f)),
				(ItemIds.BothSmall, I18n.T("Soin + bouclier"), new Color(0.8f, 0.4f, 1f)),
				(ItemIds.BothBig, I18n.T("Grand soin + bouclier"), new Color(0.8f, 0.4f, 1f)),
				(ItemIds.ShieldBig, I18n.T("Grand bouclier"), new Color(0.35f, 0.65f, 1f)),
				(ItemIds.ShieldSmall, I18n.T("Petit bouclier"), new Color(0.35f, 0.65f, 1f)),
			};
			var slices = new List<RadialMenu.Slice>();
			var owned = new List<byte>();
			foreach (var it in items)
			{
				int count = Client.Inventory.CountOf(it.id);
				if (count <= 0) continue;          // le menu ne montre que ce qu'on a dans le sac
				owned.Add(it.id);
				slices.Add(new RadialMenu.Slice { Label = it.name, Detail = $"x{count}", Icon = ItemIcons.Get(ItemDefs.Get(it.id)?.Name), Color = it.color, Enabled = count > 0 && Consumables.Useful(it.id, Client.Health, Client.Shield, Client.ShieldTier) });
			}
			if (slices.Count == 0) { _hud.SetStatus(I18n.T("Aucun soin ni bouclier dans le sac")); return; }
			_radial.OnChoose = (i, click) => Client.SendCommand(CommandType.UseItem, owned[i]);
			_radialKind = 1; _radialAction = InputManager.Actions.USE_HEAL;
			_radial.Open(I18n.T("SOINS"), I18n.T("relâche pour utiliser"), slices);
		}

		/// <summary>Menu des grenades (touche G maintenue) : relâcher choisit la grenade (F la lance) ; un clic gauche la choisit et la lance tout de suite.</summary>
		private void OpenGrenadeRadial()
		{
			var colors = new[] { new Color(1f, 0.55f, 0.2f), new Color(0.4f, 0.8f, 1f), new Color(1f, 0.35f, 0.2f), new Color(0.95f, 0.95f, 0.5f) };
			var slices = new List<RadialMenu.Slice>();
			var owned = new List<int>();
			for (int i = 0; i < GrenadeOrder.Length; i++)
			{
				int count = Client.Inventory.CountOf(GrenadeOrder[i]);
				if (count <= 0) continue;          // seulement les types qu'on a
				owned.Add(i);
				slices.Add(new RadialMenu.Slice { Label = char.ToUpper(I18n.T(GrenadeNames[i])[0]) + I18n.T(GrenadeNames[i]).Substring(1), Detail = $"x{count}", Icon = ItemIcons.Get(ItemDefs.Get(GrenadeOrder[i])?.Name), Color = colors[i], Enabled = count > 0 });
			}
			if (slices.Count == 0) { _hud.SetStatus(I18n.T("Aucune grenade dans le sac")); return; }
			_radial.OnChoose = (i, click) =>
			{
				_grenadePick = GrenadeOrder[owned[i]];
				Client.SendCommand(CommandType.EquipGrenade, _grenadePick);       // en main : le clic gauche la lance
			};
			_radialKind = 2; _radialAction = InputManager.Actions.GRENADE_MENU;
			_radial.Open(I18n.T("GRENADES"), I18n.T("relâche : en main"), slices);
		}

		/// <summary>Tant que la touche du menu est tenue, la souris pilote le menu ; au relâchement on valide.</summary>
		private void UpdateRadial()
		{
			if (Input.IsActionPressed(_radialAction)) return;
			var result = _radial.Release();
			if (result == RadialMenu.Result.Tap && _radialKind == 1) UseSupply(ItemIds.HealSmall, ItemIds.HealBig, true, false);       // appui bref : l'ancien raccourci
			if (result == RadialMenu.Result.Tap && _radialKind == 2) TapGrenade();
		}

		// ------------------------------------------------------------------ pings

		/// <summary>Désigne l'endroit visé (ou l'adversaire visé) à son équipe : clic molette ; Maj = danger.</summary>
		private void SendPing()
		{
			var cam = _head.GetNodeOrNull<Camera3D>("Camera");
			if (cam == null || _worldCollision == null) return;
			Vector3 o = cam.GlobalPosition, d = -cam.GlobalTransform.Basis.Z.Normalized();
			const float MaxRange = 600f;
			bool wall = _worldCollision.Raycast(new Vec3(o.X, o.Y, o.Z), new Vec3(d.X, d.Y, d.Z), MaxRange, out float hit);
			if (!wall) hit = MaxRange;
			byte type = 0;
			ushort target = 0xFFFF;
			// un adversaire sur la ligne de visée, avant le décor : « ennemi »
			foreach (var kv in _remotes)
			{
				if (!GodotObject.IsInstanceValid(kv.Value) || (_roster.TryGetValue(kv.Key, out var r) && r.Team == Client.TeamId)) continue;
				Vector3 oc = o - (kv.Value.GlobalPosition + Vector3.Up * 0.9f);
				float b = oc.Dot(d), disc = b * b - (oc.Dot(oc) - 0.85f * 0.85f);
				if (disc < 0f) continue;
				float t = -b - Mathf.Sqrt(disc);
				if (t > 0f && t < hit) { hit = t; type = 2; wall = true; target = kv.Key; }
			}
			if (!wall) return;      // le ciel ne se désigne pas
			if (type == 0 && Input.IsKeyPressed(Key.Shift)) type = 1;
			Vector3 p = o + d * hit;
			Client.SendPing(type, new Vec3(p.X, p.Y, p.Z), target);
		}

		private void OnPing(ServerEvent e)
		{
			if (e.C == 255) { _pings.RemoveAll(q => q.Owner == e.A); return; }      // ping retiré par son auteur (ou ordre abandonné par le serveur)
			double now = Time.GetTicksMsec() / 1000.0;
			_pings.RemoveAll(q => q.Owner == e.A);                                  // un seul ping par joueur : le nouveau remplace l'ancien
			_pings.Add(new PingData { Owner = e.A, Type = e.C, Pos = new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z), Born = now, Name = e.A == Client.PlayerId ? I18n.T("Moi") : NameOf(e.A) });
			GameAudio.Ping(_worldRoot, _player.GlobalPosition);
		}

		private void UpdatePings()
		{
			var marks = new List<PingMark>(_pings.Count);
			foreach (var p in _pings)
				marks.Add(new PingMark { Pos = p.Pos, Type = p.Type, Alpha = 1f, Owner = p.Owner, Label = $"{p.Name} — {PingView.NameOf(p.Type)}" });
			_hud.SetPings(marks);
		}

		private static readonly byte[] GrenadeOrder = { ItemIds.GrenadeFrag, ItemIds.GrenadeSticky, ItemIds.GrenadeFire, ItemIds.GrenadeFlash };
		private static readonly string[] GrenadeNames = { /*T*/"fragmentation", /*T*/"collante", /*T*/"incendiaire", /*T*/"flash" };
		private byte _grenadePick = ItemIds.GrenadeFrag;

		/// <summary>La grenade choisie, ou la première qu'on possède si celle-ci est épuisée ; 0 s'il n'y en a aucune.</summary>
		private byte CurrentGrenade()
		{
			if (Client.Inventory.CountOf(_grenadePick) > 0) return _grenadePick;
			foreach (var id in GrenadeOrder) if (Client.Inventory.CountOf(id) > 0) return _grenadePick = id;
			return 0;
		}

		/// <summary>Le porteur de la bombe : ce qu'il doit faire pour la poser (la sortir, rejoindre un site, cliquer).</summary>
		private string BombHint()
		{
			if (!Client.ModeKnown || Client.Mode.Mode != GameMode.Sabotage || Client.Mode.Stage != (byte)SabotageStage.Round || !Client.Alive || Client.Inventory.CountOf(ItemIds.Bomb) == 0) return "";
			bool onSite = false;
			if (_worldCollision != null)
				foreach (var site in _worldCollision.Data.PointsOf(MapPointKind.BombSite))
				{
						if (site.InSquare(Client.Predicted.Pos.X, Client.Predicted.Pos.Z)) onSite = true;
					}
			if (_inventory.BombInHand) return onSite ? I18n.T("Maintiens le clic gauche : poser la bombe") : I18n.T("Bombe en main : rejoins un site (A ou B) pour la poser");
			return onSite ? I18n.T("Molette ou [5] : sortir la bombe, puis clic gauche pour la poser") : "";
		}

		/// <summary>Appui bref sur G : met en main la grenade choisie (ou la seule qu'on a) ; si une grenade est déjà en main, la range.</summary>
		private void TapGrenade()
		{
			if (_inventory.GrenadeInHand != 0) { Client.SendCommand(CommandType.EquipGrenade, 0); return; }
			byte id = CurrentGrenade();                       // la grenade choisie, sinon la première qu'on possède (la seule, le plus souvent)
			if (id == 0) { _hud.SetStatus(I18n.T("Aucune grenade dans le sac")); return; }
			Client.SendCommand(CommandType.EquipGrenade, id);
		}

		private void ThrowGrenade()
		{
			byte id = CurrentGrenade();
			if (id == 0) { _hud.SetStatus(I18n.T("Aucune grenade dans le sac")); return; }
			Client.SendCommand(CommandType.ThrowGrenade, id);
		}

		/// <summary>Passe à la grenade suivante que l'on possède.</summary>
		private void CycleGrenade()
		{
			int start = System.Array.IndexOf(GrenadeOrder, _grenadePick);
			for (int k = 1; k <= GrenadeOrder.Length; k++)
			{
				byte id = GrenadeOrder[(start + k) % GrenadeOrder.Length];
				if (Client.Inventory.CountOf(id) > 0)
				{
					_grenadePick = id;
					if (_inventory.GrenadeInHand != 0) Client.SendCommand(CommandType.EquipGrenade, id);
					return;
				}
			}
		}

		private void UpdateGrenadeHud()
		{
			bool onFoot = Client.Mode.Ride(Client.PlayerId,out _) == null;
			byte id = onFoot ? CurrentGrenade() : (byte)0;
			string name = id == 0 ? "" : I18n.T(GrenadeNames[System.Array.IndexOf(GrenadeOrder, id)]);
			_hud.SetGrenade(id == 0 ? "" : _inventory.GrenadeInHand != 0
				? I18n.T("Grenade {0} x{1} en main   —   clic gauche : lancer   [G] ranger   [V] changer", name, Client.Inventory.CountOf(id))
				: I18n.T("Grenade {0} x{1}   —   [G] la sortir   (maintenir G : menu)", name, Client.Inventory.CountOf(id)));
			// la trajectoire s'affiche tant qu'une grenade est en main
			var cam = _head?.GetNodeOrNull<Camera3D>("Camera");
			_preview.Refresh(cam, new Vector3(Client.Predicted.Vel.X, 0f, Client.Predicted.Vel.Z), _inventory.GrenadeInHand, _worldCollision, onFoot && Client.Alive && !_radial.IsOpen);
		}

		private void UseSupply(byte small, byte big, bool heals, bool shields)
		{
			if (Client.UsingItem == small || Client.UsingItem == big) { Client.SendCommand(CommandType.CancelUse, 0); return; }
			float needHealth = heals ? 100f - Client.Health : 0f;
			float maxShield = Consumables.ShieldMaxOf(Client.ShieldTier);
			float needShield = shields && maxShield > 0f ? maxShield - Client.Shield : 0f;
			if (needHealth <= 0.5f && needShield <= 0.5f)
			{
				_hud.SetStatus(heals && shields ? I18n.T("Vie et bouclier déjà pleins") : heals ? I18n.T("Vie déjà pleine") : maxShield <= 0f ? I18n.T("Pas de bouclier équipé") : I18n.T("Bouclier déjà plein"));
				return;
			}
			bool haveSmall = Client.Inventory.CountOf(small) > 0, haveBig = Client.Inventory.CountOf(big) > 0;
			if (!haveSmall && !haveBig) { _hud.SetStatus(I18n.T("Aucun objet de ce type dans le sac")); return; }
			float need = Mathf.Max(needHealth, needShield);
			byte pick = haveBig && (!haveSmall || need > 50f) ? big : small;      // le petit tant qu'il suffit, le grand pour un gros manque
			Client.SendCommand(CommandType.UseItem, pick);
		}

		// ------------------------------------------------------------------ tyrolienne : chariot et poignée au-dessus du joueur

		private readonly Dictionary<int, Node3D> _trolleys = new();
		private readonly HashSet<int> _trolleysUsed = new();
		private PackedScene _trolleyScene;

		/// <summary>Pose (ou crée) le chariot d'un joueur sur le câble, orienté dans le sens de sa course.</summary>
		private void ShowTrolley(int key, Vector3 feet, Vector3 velocity)
		{
			_trolleysUsed.Add(key);
			_trolleyScene ??= GD.Load<PackedScene>("res://maps/frontiere01/models/zip_trolley.glb");
			if (_trolleyScene == null) return;
			if (!_trolleys.TryGetValue(key, out var node) || !GodotObject.IsInstanceValid(node))
			{
				node = _trolleyScene.Instantiate<Node3D>();
				_worldRoot.AddChild(node);
				_trolleys[key] = node;
			}
			node.GlobalPosition = feet + Vector3.Up * Sim.ZipHoldHeight;
			if (velocity.LengthSquared() > 0.25f) node.LookAt(node.GlobalPosition + velocity, Vector3.Up);
		}

		private void CleanTrolleys()
		{
			List<int> gone = null;
			foreach (var kv in _trolleys)
				if (!_trolleysUsed.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
			if (gone != null)
				foreach (var k in gone) { if (GodotObject.IsInstanceValid(_trolleys[k])) _trolleys[k].QueueFree(); _trolleys.Remove(k); }
			_trolleysUsed.Clear();
		}

		private Stance _traceStance;
		private int _traceMag = -1, _traceHealth = -1;
		private ushort _traceUse;

		/// <summary>Journal des changements d'état du joueur local (pour reconstituer ce qui s'est passé juste avant un à-coup).</summary>
		private void TraceStates()
		{
			var st = Client.Predicted.Stance;
			if (st != _traceStance) { LagTracer.Event($"posture : {_traceStance} -> {st}"); _traceStance = st; }
			if (Client.Magazine != _traceMag) { if (_traceMag >= 0) LagTracer.Event($"chargeur : {_traceMag} -> {Client.Magazine}"); _traceMag = Client.Magazine; }
			if (Client.Health != _traceHealth) { if (_traceHealth >= 0) LagTracer.Event($"vie : {_traceHealth} -> {Client.Health} (bouclier {Client.Shield})"); _traceHealth = Client.Health; }
			if (Client.UsingItem != _traceUse) { LagTracer.Event($"soin en cours : {Client.UsingItem}"); _traceUse = Client.UsingItem; }
		}

		private float _eyeY;
		private bool _eyeInit;
		private float _lastFootY, _stepSmooth;
		private bool _stepInit;
		private int _gcSeen;

		public override void _Process(double delta)
		{
			TrackResume((float)delta);
			TickHold((float)delta);
			if (!_ambience.Playing) _ambience.Play();      // toujours présente, quoi qu'il arrive au lecteur
			bool inPlane = Client != null && Client.State == NetState.Playing && Client.Predicted.Stance == Stance.Plane;
			if (inPlane != _planeSound.Playing) { if (inPlane) _planeSound.Play(); else _planeSound.Stop(); }
			// chute libre : le vent, plus fort quand on tombe vite
			bool falling = Client != null && Client.State == NetState.Playing && Client.Predicted.Stance == Stance.Diving;
			if (falling != _fallSound.Playing) { if (falling) _fallSound.Play(); else _fallSound.Stop(); }
			if (falling)
			{
				var fallVel = Client.Predicted.Vel;
				float speed = Mathf.Clamp((fallVel.Length() - 20f) / 50f, 0f, 1f);
				_fallSound.VolumeDb = Mathf.Lerp(_fallSound.VolumeDb, Mathf.Lerp(-10f, 0f, speed) + (_diveHeld ? DiveWindDb : 0f), Mathf.Min(1f, (float)delta * 3f));
			}
			_slideSound?.Set(Client != null && Client.State == NetState.Playing && Client.Alive && Client.Predicted.Stance == Stance.Sliding);
			_zipSound?.Set(Client != null && Client.State == NetState.Playing && Client.Alive && Client.Predicted.Stance == Stance.Zipping, Client != null && Client.Alive);
			// à-coups : on évite les grandes pauses du ramasse-miettes et on journalise les images très longues (cause probable d'un lag d'une seconde)
			if (delta > 0.2)
			{
				int gc2 = System.GC.CollectionCount(2);
				GD.Print($"[lag] image de {delta * 1000:F0} ms (ramasse-miettes gén.2 : {gc2 - _gcSeen} depuis la dernière, mémoire {System.GC.GetTotalMemory(false) / 1048576} Mo)");
				_gcSeen = gc2;
			}
			if (Client != null && Client.State == NetState.Failed)
			{
				// l'écran de chargement masquait l'erreur (carte différente, protocole différent…) : on l'affiche en clair
				string failText = "Connexion impossible : " + Client.FailReason;
				if (!_overlayOn || _overlayText != failText) { _overlayOn = true; _overlayText = failText; _hud.SetLoading(true, failText); }
				return;
			}
			if (Client == null || Client.State != NetState.Playing || !_hasView) return;
			float alpha = (float)Engine.GetPhysicsInterpolationFraction();
			if (Spectating) { SpectatorFrame(alpha, (float)delta); return; }
			var v = Vec3.Lerp(_prevView, _currView, alpha);
			// la position du serveur est celle des pieds ; l'origine du joueur est au centre de sa capsule, qui rétrécit accroupi
			// ou en glissade : à hauteur fixe, la capsule flotterait, le contrôleur se croirait en l'air (« saut ») et couperait la glissade
			_player.GlobalPosition = new Vector3(v.X, v.Y + _player.ColliderHeight * 0.5f + GroundLift(v, delta), v.Z);

			// l'œil est à la hauteur que le serveur utilise pour tirer (pieds + 1,6 m debout, 1,1 accroupi, 0,8 en glissade) : sinon la balle
			// part plus haut ou plus bas que le viseur, et une tête visée est manquée alors qu'un corps (plus gros) est touché
			float eyeWanted = v.Y + Sim.EyeFor(Client.Predicted.Stance) - _player.GlobalPosition.Y;
			// (le contrôleur remet la position de la tête à chaque image physique : on lisse notre propre valeur, pas celle du nœud)
			_eyeY = _eyeInit ? Mathf.Lerp(_eyeY, eyeWanted, 1f - Mathf.Exp(-22f * (float)delta)) : eyeWanted;
			_eyeInit = true;
			// escaliers : le serveur monte d'une marche (0,25 m) en un seul tick, ce qui faisait sauter la caméra à chaque pas.
			// Sur le sol, une variation de hauteur modérée est absorbée : l'œil la rattrape en une fraction de seconde.
			float footDy = v.Y - _lastFootY;
			if (_stepInit && Client.Predicted.OnGround && Mathf.Abs(footDy) > 0.002f && Mathf.Abs(footDy) <= 0.6f) _stepSmooth = Mathf.Clamp(_stepSmooth - footDy, -0.6f, 0.6f);
			_lastFootY = v.Y;
			_stepInit = true;
			_stepSmooth *= Mathf.Exp(-11f * (float)delta);
			_head.Position = new Vector3(_head.Position.X, _eyeY + _stepSmooth, _head.Position.Z);

			using (LagTracer.Scope("UpdateRemotes")) UpdateRemotes(alpha, (float)delta);
			UpdateDropPhase(v, (float)delta);
			UpdateZone(v);
			UpdateMode(v);
			UpdateVehicles((float)delta);
			TraceStates();
		}

		/// <summary>
		/// Ce qu'il y a sous le viseur : l'objet au sol (déjà choisi par l'inventaire, c'est celui de l'invite « Ramasser »),
		/// sinon un coffre fermé. Le serveur ne fait que vérifier ce choix : on prend ce qu'on voit.
		/// </summary>
		private ushort FindInteractTarget()
		{
			_windowHint = null;
			var cam = _head.GetNodeOrNull<Camera3D>("Camera");
			if (cam == null) return PlayerInput.NoTarget;
			Vector3 origin = cam.GlobalPosition, dir = -cam.GlobalBasis.Z;
			ushort best = PlayerInput.NoTarget;
			float bestT = 3.2f;
			int aimedPane = -1;
			if (_worldCollision.Windows.Length > 0)
			{
				_worldCollision.Raycast(new Vec3(origin.X, origin.Y, origin.Z), new Vec3(dir.X, dir.Y, dir.Z),
					WindowState.Reach, out float distance, out aimedPane, includeBroken: true);
				if (aimedPane >= 0) bestT = distance;
			}
			var item = _inventory.Target;
			if (item != null && GodotObject.IsInstanceValid(item) && (aimedPane < 0 || origin.DistanceTo(item.GlobalPosition) < bestT))
				foreach (var kv in _loot)
					if (kv.Value == item) return kv.Key;

			foreach (var kv in _chests)
			{
				var chest = kv.Value;
				if (!GodotObject.IsInstanceValid(chest) || chest.IsOpen) continue;
				// boîte du coffre : 1,5 x 0,63 x 0,83 m, posée au sol
				// le coffre peut être tourné : on teste le rayon dans le repère du coffre
				var inv = chest.GlobalTransform.AffineInverse();
				var box = new Godot.Aabb(new Vector3(-0.75f, 0f, -0.42f), new Vector3(1.5f, 0.63f, 0.84f));
				if (!box.IntersectsSegment(inv * origin, inv * (origin + dir * bestT))) continue;
				float t = origin.DistanceTo(chest.GlobalTransform * box.GetCenter());
				if (t < bestT) { bestT = t; best = kv.Key; }
			}
			if (best == PlayerInput.NoTarget && aimedPane >= 0)
			{
				var window = _worldCollision.Windows[aimedPane];
				string action = window.Health < window.Capacity ? I18n.T("Maintenir E · Réparer") :
					window.Reinforced ? I18n.T("Vitre renforcée") : I18n.T("Maintenir E · Renforcer");
				_windowHint = $"{action}   {window.Health}/{window.Capacity} PV";
				return window.ActionTarget(aimedPane);
			}
			return best;
		}

		/// <summary>Niveau d'un objet au sol : pour une arme, bits 8-9 du champ Count ; fixe pour les sacs à dos ; classique sinon.</summary>
		private static int LootTier(in InterpolatedEntity e)
		{
			var def = ItemDefs.Get(e.Weapon);
			if (def == null) return 0;
			return def.Kind == ItemKind.Weapon || def.Kind == ItemKind.Shield ? (e.Count >> 8) & 3 : Tiers.Of(e.Weapon, 0);
		}

		private string _windowHint;
		private readonly List<CollisionShape3D> _windowViews = new();

		private void UpdateWindowView(int index)
		{
			var pane = _windowViews[index];
			var state = _worldCollision.Windows[index];
			pane.Disabled = state.Health == 0;
			pane.Visible = state.Health > 0;
			var shape = (BoxShape3D)pane.Shape;
			shape.Size = new Vector3(state.Reinforced ? 0.16f : 0.08f, shape.Size.Y, shape.Size.Z);
			pane.GetNode<Node3D>("Glass").Scale = new Vector3(state.Reinforced ? 2f : 1f, 1f, 1f);
			pane.GetNode<Node3D>("Reinforcement").Visible = state.Reinforced;
		}

		private void BindWindows(Node root)
		{
			if (_worldCollision.Windows.Length == 0) return;
			// Same depth-first order as WorldExporter.WalkBoxes. Verify geometry before binding network IDs.
			foreach (var node in root.FindChildren("*", "CollisionShape3D", true, false))
				if (node is CollisionShape3D pane && pane.IsInGroup("window_panes"))
				{
					int i = _windowViews.Count;
					var pos = pane.GlobalPosition;
					if (i >= _worldCollision.Windows.Length ||
						Vec3.Distance(new Vec3(pos.X, pos.Y, pos.Z), (_worldCollision.Data.Panes[i].Min + _worldCollision.Data.Panes[i].Max) * 0.5f) > 0.02f)
						throw new InvalidDataException("Les vitres de la scène et du fichier .world ne correspondent pas.");
					_windowViews.Add(pane);
				}
			if (_windowViews.Count != _worldCollision.Windows.Length)
				throw new InvalidDataException("Nombre de vitres différent entre la scène et le fichier .world.");
		}

		private float _groundLift;

		/// <summary>
		/// Le relief du serveur est une grille de hauteurs interpolée, celui du client est le vrai maillage : sur les pentes raides
		/// les pieds du serveur peuvent être sous le sol visible et la caméra se retrouve dans la montagne. On relève le joueur
		/// jusqu'à la surface visible (jamais on ne l'abaisse), en douceur pour éviter les saccades.
		/// </summary>
		private float GroundLift(Vec3 feet, double delta)
		{
			float want = 0f;
			var space = _player.GetWorld3D()?.DirectSpaceState;
			if (space != null)
			{
				var from = new Vector3(feet.X, feet.Y + 1.2f, feet.Z);
				var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 3f);
				query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
				var hit = space.IntersectRay(query);
				if (hit.Count > 0) want = Mathf.Clamp(hit["position"].AsVector3().Y - feet.Y, 0f, 1.2f);
			}
			// montée immédiate (pas d'entrée dans le sol), descente lente
			_groundLift = want > _groundLift ? want : Mathf.MoveToward(_groundLift, want, 3f * (float)delta);
			return _groundLift;
		}

		// ------------------------------------------------------------------ arrivée : avion, chute libre, parachute

		private bool _loading = true;
		private double _joinedAt = -1;
		private Vec3 _flightFrom, _flightTo;
		private bool _flightKnown, _planeFaced;
		private RemotePlayer _selfBody;
		private float _camBack;
		private StaticWorld _worldCollision;

		/// <summary>
		/// Gère tout ce qui entoure la partie : écran d'attente et briefing (la carte et la trajectoire de l'avion), compte à rebours du largage,
		/// troisième personne en chute libre, état « à terre », statistiques (vivants, éliminations, dégâts) et écran de fin.
		/// </summary>
		private void UpdateDropPhase(Vec3 view, float delta)
		{
			var st = Client.Predicted;
			var m = Client.Match;
			bool royale = Client.MatchKnown;
			bool plane = st.Stance == Stance.Plane, dive = st.Stance == Stance.Diving, down = st.Stance == Stance.Downed;
			double now = Time.GetTicksMsec() / 1000.0;
			if (_joinedAt < 0) _joinedAt = now;

			if (_loading && _weapons.WarmUpDone && now - _joinedAt > 0.8) _loading = false;

			if (plane) { _flightFrom = st.PlaneFrom; _flightTo = st.PlaneTo; _flightKnown = true; }
			// à l'arrivée dans l'avion, on regarde dans le sens du vol : vers la carte
			if (plane && !_planeFaced && (st.PlaneTo.X != st.PlaneFrom.X || st.PlaneTo.Z != st.PlaneFrom.Z))
			{
				_planeFaced = true;
				float yaw = Mathf.Atan2(-(st.PlaneTo.X - st.PlaneFrom.X), -(st.PlaneTo.Z - st.PlaneFrom.Z));
				_player.GlobalRotation = new Vector3(0f, yaw, 0f);
				_head.Rotation = new Vector3(-0.5f, _head.Rotation.Y, _head.Rotation.Z);      // et un peu vers le bas, on voit le sol
			}
			else if (!plane) _planeFaced = false;
			bool showPath = _flightKnown && (plane || dive);
			_hud.SetFlightPath(new Vector3(_flightFrom.X, _flightFrom.Y, _flightFrom.Z), new Vector3(_flightTo.X, _flightTo.Y, _flightTo.Z), showPath);

			// écran plein cadre : chargement, salle d'attente, puis briefing pendant que l'avion approche de la carte (trajectoire en jaune)
			bool lobby = royale && m.Phase == MatchPhase.Lobby;
			bool briefing = plane && st.FlightT < st.PlaneEnter - 1.5f;
			string overlayText = "";
			if (_loading) overlayText = I18n.T("Chargement des armes et des objets…");
			else if (lobby) overlayText = m.Countdown > 0 ? I18n.T("Départ dans {0} s   —   {1}/{2} joueur(s) dans la salle d'attente", m.Countdown, m.Lobby, m.Needed) : I18n.T("En attente de joueurs : {0}/{1}", m.Lobby, m.Needed);
			else if (briefing) overlayText = I18n.T("L'avion arrive sur la carte dans {0} s   —   choisis où sauter", Mathf.Max(0, Mathf.CeilToInt(st.PlaneEnter - st.FlightT)));
			bool overlay = _loading || lobby || briefing;
			if (overlay != _overlayOn || overlayText != _overlayText) { _overlayOn = overlay; _overlayText = overlayText; _hud.SetLoading(overlay, overlayText); }

			// consignes de chute
			string text = "";
			bool team = royale && m.TeamSize > 1;
			bool iLead = !team || m.JumpLeader == Client.PlayerId;
			string leaderName = team && m.JumpLeader != 0xFFFF ? NameOf(m.JumpLeader) : "";
			if (plane && !briefing)
			{
				string eject = I18n.T("éjection automatique dans {0} s", Mathf.Max(0, Mathf.CeilToInt(st.PlaneExit - st.FlightT)));
				text = !team ? I18n.T("SAUT : sauter de l'avion   —   {0}", eject)
					: iLead ? I18n.T("Tu diriges le saut : SAUT = toute l'équipe saute   ·   TAB : passer le commandement   —   {0}", eject)
					: I18n.T("{0} dirige le saut de l'équipe (TAB : il peut te passer le commandement)   —   {1}", leaderName, eject);
			}
			else if (dive)
			{
				float alt = view.Y - (_worldCollision?.HeightAt(view.X, view.Z) ?? 0f);
				string stats = $"altitude {alt:F0} m   ·   {-st.Vel.Y * 3.6f:F0} km/h";
				text = !iLead && Client.Passenger ? I18n.T("{0} dirige la chute   ·   clic droit : regarder autour   ·   {1}", leaderName, stats)
					: team ? I18n.T("Tu diriges l'équipe   ·   Z : plonger (relâche pour ralentir)   ·   clic droit : figer la direction et regarder autour   ·   TAB : passer le commandement   ·   {0}", stats)
					: I18n.T("Z : plonger (relâche pour ralentir)   ·   clic droit : figer la direction et regarder autour   ·   {0}", stats);
			}
			_hud.SetFlight(text);

			// statistiques, état « à terre », fin de partie
			bool modal = Client.ModeKnown;
			_hud.SetMatchStats(royale && !modal && m.Phase != MatchPhase.Lobby, m.PlayersAlive, m.TeamsAlive, m.TeamSize, m.Kills, m.Damage);
			_hud.SetDowned(down, m.DownLeft, m.Revive, m.CanRevive);
			string detail = I18n.T("Éliminations : {0}      Dégâts : {1}", m.Kills, m.Damage);
			if (royale && m.Phase == MatchPhase.Over)
			{
				if (m.Placement == 1) _hud.ShowEnd(true, I18n.T("VICTOIRE !"), detail, new Color(1f, 0.85f, 0.25f));
				else _hud.ShowEnd(true, I18n.T("PARTIE TERMINÉE"), detail + "\n" + I18n.T("Classement : {0}", Mathf.Max(2, (int)m.Placement)), Colors.White);
			}
			else if (royale && !modal && !Client.Alive && m.Phase == MatchPhase.Playing)
				_hud.ShowEnd(true, m.Placement > 0 ? I18n.T("ÉLIMINÉ — #{0}", m.Placement) : I18n.T("ÉLIMINÉ"), detail + (m.Placement > 0 ? "" : "\n" + I18n.T("Ton équipe est toujours en jeu.")), new Color(1f, 0.4f, 0.35f));
			else if (_disconnectReason != null) _hud.ShowEnd(true, I18n.T("PARTIE TERMINÉE"), I18n.T(_disconnectReason), Colors.White);
			else _hud.ShowEnd(false);

			// vue : arme rangée dans l'avion, en chute et à terre ; caméra à la troisième personne en chute
			_weapons.Visible = !(plane || dive || down);
			_weapons.HideViewmodels = plane || dive || down;      // ni arme ni bras avant d'être au sol
			var cam = _head.GetNodeOrNull<Camera3D>("Camera");
			_camBack = Mathf.Lerp(_camBack, dive ? 5.2f : 0f, 1f - Mathf.Exp(-6f * delta));
			if (cam != null) cam.Position = new Vector3(0f, _camBack * 0.12f, _camBack);

			if (dive)
			{
				if (_selfBody == null || !GodotObject.IsInstanceValid(_selfBody))
				{
					_selfBody = new RemotePlayer { Id = 0xFFFE, Name = "SelfBody" };
					_worldRoot.AddChild(_selfBody);
					_selfBody.SetLook(CharacterAppearance.Current.Pack());
				}
				// le corps suit la trajectoire (cap figé compris) : cap = direction de la vitesse, inclinaison = vitesse de chute
				float hs = Mathf.Sqrt(st.Vel.X * st.Vel.X + st.Vel.Z * st.Vel.Z);
				float bodyYaw = hs > 1f ? Mathf.Atan2(-st.Vel.X, -st.Vel.Z) : _player.GlobalRotation.Y;
				var e = new InterpolatedEntity { Id = 0xFFFE, Kind = EntityKind.Player, Pos = view, Yaw = bodyYaw, Pitch = PlayerMovement.DivePitch(PlayerMovement.DiveAmount(_diveSentPitch, _diveHeld)), Vel = st.Vel, Zip = 4, Health = 100, Team = Client.TeamId };
				_selfBody.Apply(e, "", false, delta);
			}
			else if (_selfBody != null)
			{
				if (GodotObject.IsInstanceValid(_selfBody)) _selfBody.QueueFree();
				_selfBody = null;
			}
		}

		private ZoneWall _zoneWall;
		private ModeHud _modeHud;
		private GrenadeView _grenadeView;
		private DamageNumbers _damageNumbers;
		private SlideSound _slideSound;
		private ZipSound _zipSound;
		/// <summary>Chute libre : le vent est plus fort (dB) tant qu'on plonge (« avancer » tenu).</summary>
		private const float DiveWindDb = 8f;
		/// <summary>Le fichier d'ambiance est enregistré très bas (-45 dB en moyenne) : il faut le remonter pour l'entendre.</summary>
		private const float AmbienceDb = 5f;
		private bool _diveHeld;
		private float _diveSentPitch;
		private AudioStreamPlayer _ambience, _planeSound, _fallSound;

		/// <summary>
		/// Un de nos tirs a touché : marqueur au centre et dégâts sur la cible, de la couleur de son bouclier s'il a encaissé le coup
		/// (blanc sur la vie, rouge si elle meurt), et le bruit du bouclier qui casse.
		/// </summary>
		private void OnHitConfirm(ServerEvent e)
		{
			bool kill = (e.D & 0x80) != 0, head = (e.D & 3) == (int)HitZone.Head;
			int tier = (e.B & 7) - 1;
			bool onShield = (e.B & 0x10) != 0 && tier >= 0, broke = (e.B & 0x20) != 0;
			var color = onShield ? Openesport.Inventory.ItemTierStyle.ColorOf(tier) : Colors.White;
			_hud.HitMarker(kill, color, broke);
			if (e.C > 0 && e.Pos.Y != 0f) _damageNumbers.Add(new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z), e.C, kill ? new Color(1f, 0.3f, 0.25f) : color, head);
			if (broke) GameAudio.ShieldBreak(this);
		}
		private RadialMenu _radial;
		private GrenadePreview _preview;
		private PingView _pingView;
		private readonly List<PingData> _pings = new();
		private byte _radialKind;
		private string _radialAction = "";
		private FlashOverlay _flash;
		private bool _loadoutSent;
		private ModeWorld _modeWorld;
		private Vec3 _modeView;
		
		/// <summary>Modes hors battle royale : scores, minuteur, points de contrôle, drapeaux, bombe, boutique et classes.</summary>
		private void UpdateMode(Vec3 view)
		{
			if (!Client.ModeKnown) return;
			_modeView = view;
			if (!_loadoutSent)
			{
				// l'équipement choisi dans le menu : envoyé une fois, dès que le serveur annonce un mode à équipement (hors battle royale)
				_loadoutSent = true;
				if (Client.Mode.Mode != GameMode.BattleRoyale) Client.SendCommand(CommandType.SelectClass, (byte)MatchSettings.Loadout);
			}
			if (_modeHud == null || !GodotObject.IsInstanceValid(_modeHud))
			{
				_modeHud = new ModeHud { Name = "ModeHud" };
				_modeHud.NameOf = id => NameOf(id);
				_modeHud.Send = (t, a) => Client.SendCommand(t, (byte)a);
				_modeHud.LocalPosition = () => _player != null && GodotObject.IsInstanceValid(_player) ? _player.GlobalPosition : null;
				_modeHud.MyId = () => Client.PlayerId;
				if (_worldCollision != null) _modeHud.Stations = Resupply.Stations(_worldCollision.Data);
				_hud.MoveStatusForMode();      // le message d'état ne doit pas recouvrir les scores ni le bandeau d'annonce
				AddChild(_modeHud);
			}
			if (_modeWorld == null || !GodotObject.IsInstanceValid(_modeWorld))
			{
				_modeWorld = new ModeWorld { Name = "ModeWorld" };
				_worldRoot.AddChild(_modeWorld);
				if (_worldCollision != null) _modeWorld.Build(_worldCollision.Data);
				_modeWorld.CarrierPosition = id =>
				{
					if (id == Client.PlayerId) return new Vector3(_modeView.X, _modeView.Y, _modeView.Z);
					return _remotes.TryGetValue(id, out var r) && GodotObject.IsInstanceValid(r) ? r.GlobalPosition : null;
				};
			}
			_modeHud.Visible = !_hud.IsLoading;      // pas de scores par-dessus l'écran de chargement
			_modeHud.Refresh(Client.Mode, Client.Match.Phase, Client.Alive);
			_modeWorld.Refresh(Client.Mode);
			FaceDuelOpponent();
		}

		private int _duelFaced = -1;

		/// <summary>1 VS 1 : au début de chaque manche (et pendant le choix), on se retrouve face au centre de la salle, donc face à l'adversaire.</summary>
		private void FaceDuelOpponent()
		{
			var m = Client.Mode;
			if (m.Mode != GameMode.Duel || Client.Match.Phase != MatchPhase.Playing || _player == null || !GodotObject.IsInstanceValid(_player)) return;
			var stage = (DuelStage)m.Stage;
			if (stage != DuelStage.Pick && stage != DuelStage.Prepare) return;
			int key = stage == DuelStage.Pick ? 0 : m.Round;
			if (key == _duelFaced) return;
			var pos = Client.Predicted.Pos;
			if (MathF.Abs(pos.X) < 4f) return;          // pas encore replacé de son côté : on attend
			_duelFaced = key;
			var r = _player.Rotation; r.Y = MathF.Atan2(pos.X, pos.Z); _player.Rotation = r;
			if (_head != null) { var h = _head.Rotation; h.X = 0f; _head.Rotation = h; }
		}

		private static string Clock(float seconds) { int s = Mathf.CeilToInt(Mathf.Max(0f, seconds)); return $"{s / 60}:{s % 60:00}"; }

		/// <summary>La zone : mur dans le monde, cercles sur les cartes, minuteur, et alerte quand le joueur est hors de la zone (dégâts périodiques).</summary>
		private void UpdateZone(Vec3 view)
		{
			if (_zoneWall == null || !GodotObject.IsInstanceValid(_zoneWall))
			{
				_zoneWall = new ZoneWall { Name = "ZoneWall" };
				_worldRoot.AddChild(_zoneWall);
			}
			bool show = Client.ZoneKnown && Client.Zone.Phase != ZonePhase.None && Client.MatchKnown && Client.Match.Phase == MatchPhase.Playing;
			if (!show)
			{
				_zoneWall.SetZone(false, 0, 0, 0, false, 0, 0, 0, 0);
				_hud.SetZone(false, 0, 0, 0, false, 0, 0, 0, "", false, "");
				return;
			}
			var z = Client.Zone;
			float tick = Client.EstimatedServerTick;
			z.CircleAt(tick, out float cx, out float cz, out float r);
			bool outside = (view.X - cx) * (view.X - cx) + (view.Z - cz) * (view.Z - cz) > r * r;
			// minuteur : numéro de la zone, début et fin du rétrécissement
			float left = z.SecondsLeft(tick);
			float startIn = z.Phase == ZonePhase.Idle ? left + z.WarningSeconds : left;          // avant le début du rétrécissement
			float endIn = z.Phase == ZonePhase.Shrinking ? left : startIn + z.ShrinkSeconds;       // avant la fin du rétrécissement
			string damageLine = I18n.T("Hors zone : {0:0.#} dégâts / {1:0.#} s", z.Damage, z.TickSeconds);
			string timer = z.Phase == ZonePhase.Collapsed
				? I18n.T("Zone n°{0} : refermée, plus aucune place", z.Stage) + "\n" + damageLine
				: I18n.T("Zone n°{0}", z.Stage) + (z.NextVisible ? "  " + I18n.T("(prochaine : n°{0})", z.Stage + 1) : "") + "\n"
					+ (z.Phase == ZonePhase.Shrinking ? I18n.T("Rétrécissement en cours") : I18n.T("Rétrécissement dans {0}", Clock(startIn))) + "\n"
					+ I18n.T("Fin du rétrécissement dans {0}", Clock(endIn)) + "\n" + damageLine;
			string warning = outside ? I18n.T("HORS DE LA ZONE — {0:0.#} dégâts toutes les {1:0.#} s", z.Damage, z.TickSeconds) : "";
			float groundY = _worldCollision?.HeightAt(view.X, view.Z) ?? view.Y;
			_zoneWall.SetZone(true, cx, cz, r, z.NextVisible, z.NextX, z.NextZ, z.NextRadius, groundY - 300f);
			_hud.SetZone(true, cx, cz, r, z.NextVisible, z.NextX, z.NextZ, z.NextRadius, timer, outside, warning);
		}

		private bool _overlayOn;
		private string _overlayText = "";
		private string _disconnectReason;

		/// <summary>Retour au menu principal (fin de partie) : la partie en ligne et son décor sont libérés.</summary>
		private void LeaveToMenu()
		{
			var menu = GetNodeOrNull<MenuManager>(Openesport.Utils.GlobalPaths.Managers.MENU_MANAGER);
			if (menu != null) menu.LeaveGameNow();
		}

		private ulong _budgetStart;
		/// <summary>Plus de 6 ms déjà passées à créer des objets pendant cette image : le reste attend la suivante (pas de gel).</summary>
		private bool OverBudget() => (Time.GetTicksUsec() - _budgetStart) > 6000;

		private void UpdateRemotes(float alpha, float delta)
		{
			_budgetStart = Time.GetTicksUsec();
			Client.SampleEntities(_seen, alpha);
			_live.Clear();
			bool sabotage = Client.ModeKnown && Client.Mode.Mode == GameMode.Sabotage;
			var radar = new List<MapMark>();
			double nowSec = Time.GetTicksMsec() / 1000.0;

			foreach (var e in _seen)
			{
				_live.Add(e.Id);
				if (e.Kind == EntityKind.Player)
				{
					if (!_remotes.TryGetValue(e.Id, out var remote))
					{
						if (OverBudget()) continue;
						LagTracer.Event($"création du joueur {e.Id} ({NameOf(e.Id)})");
						ulong t0 = Time.GetTicksMsec();
						remote = new RemotePlayer { Id = e.Id, Name = "Remote" + e.Id };
						_worldRoot.AddChild(remote);
						_remotes[e.Id] = remote;
						if (Time.GetTicksMsec() - t0 > 40) GD.Print($"[lag] création d'un joueur : {Time.GetTicksMsec() - t0} ms");
					}
					bool friendly = e.Team == MyTeam;
					var carrier = Client.Mode.Ride(e.Id, out _);
					bool insideCabin = carrier != null && !carrier.Hover;
					bool sameCabin = insideCabin && Array.IndexOf(carrier.Seats, Client.PlayerId) >= 0;
					int hoverIndex = carrier?.Hover == true ? Array.IndexOf(Client.Mode.Vehicles, carrier) : -1;
					remote.Apply(e, NameOf(e.Id), friendly && !sameCabin, delta, insideCabin,
						hoverIndex >= 0 && hoverIndex < _vehicleModels.Count ? _vehicleModels[hoverIndex] : null);
					if (!e.Has(EntityFlags.Dead) && e.Zip != 3)
					{
						var wp = new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z);
						if (friendly) radar.Add(new MapMark { Pos = wp, Friendly = true, Alpha = 1f, Name = NameOf(e.Id) });
						else if (sabotage)
						{
							// Sabotage : un adversaire n'apparaît que s'il est vu ou touché par notre équipe, pendant 3 s (pas de détection au bruit)
							if (_spottedUntil.TryGetValue(e.Id, out double su) && su > nowSec)
								radar.Add(new MapMark { Pos = wp, Friendly = false, Alpha = Mathf.Clamp((float)(su - nowSec) / 0.8f, 0.25f, 1f) });
						}
						else if (remote.NoiseUntil > nowSec && new Vector2(wp.X - _player.GlobalPosition.X, wp.Z - _player.GlobalPosition.Z).Length() <= remote.NoiseRange)
							// un adversaire n'apparaît que s'il fait un bruit perceptible, et s'efface avec lui
							radar.Add(new MapMark { Pos = wp, Friendly = false, Alpha = Mathf.Clamp((float)(remote.NoiseUntil - nowSec) / 0.8f, 0.25f, 1f) });
					}
					bool hasLook = _roster.TryGetValue(e.Id, out var look);
					if (hasLook) remote.SetLook(look.Appearance);
					if (friendly)
						_mateCache[e.Id] = new MateView
						{
							Id = e.Id, Name = NameOf(e.Id), Health = e.Health, Shield = e.Shield, ShieldMax = e.ShieldTier > 0 ? Consumables.ShieldMaxOf(e.ShieldTier - 1) : 0f,
							Downed = e.Zip == 5, Dead = e.Has(EntityFlags.Dead), Bot = hasLook && look.IsBot, Appearance = hasLook ? look.Appearance : null
						};
				}
				else
				{
					if (e.Weapon == ItemIds.Chest)
					{
						if (!_chests.TryGetValue(e.Id, out var chest))
						{
							if (OverBudget()) continue;
							// un coffre déjà ouvert quand on arrive reste ouvert, sans animation
							chest = LootChest.Spawn(_worldRoot, new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z), ChestYawAt(e.Pos), open: e.Count > 0);
							_chests[e.Id] = chest;
						}
						if (!_chestBoxes.ContainsKey(e.Id)) { _chestBoxes[e.Id] = WorldData.ChestBox(e.Pos, ChestYawAt(e.Pos)); _chestBoxesDirty = true; }
						else chest.SetOpen(e.Count > 0);
						continue;
					}
					// le serveur réutilise l'identifiant d'un objet ramassé pour un autre objet (ex. l'arme qu'on remplace) :
					// si le contenu ou l'endroit a changé, c'est un autre objet, on refait le modèle
					if (_loot.TryGetValue(e.Id, out var known)
						&& (!GodotObject.IsInstanceValid(known) || known.ItemId != ItemDefs.Get(e.Weapon)?.Name
							|| known.Tier != LootTier(e) || known.GlobalPosition.DistanceSquaredTo(new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z)) > 0.25f))
					{
						if (GodotObject.IsInstanceValid(known)) known.QueueFree();
						_loot.Remove(e.Id);
					}
					if (!_loot.TryGetValue(e.Id, out var item))
					{
						var name = ItemDefs.Get(e.Weapon)?.Name;
						if (name == null || OverBudget()) continue;
						LagTracer.Event($"création de l'objet {name} (id {e.Id})");
						ulong t1 = Time.GetTicksMsec();
						item = GroundItem.Spawn(_worldRoot, name, new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z), 0f, null, 0.5f, e.Count, snapToGround: false, tier: LootTier(e));
						if (item.WeaponState != null && ItemDefs.Get(e.Weapon).Kind == ItemKind.Weapon && e.Count > 0)
							item.WeaponState.OpticId = ItemDefs.Get((byte)e.Count)?.Name;
						_loot[e.Id] = item;
						if (Time.GetTicksMsec() - t1 > 40) GD.Print($"[lag] création de l'objet {name} : {Time.GetTicksMsec() - t1} ms");
					}
				}
			}

			// entités qui ont quitté notre voisinage
			var gone = new List<ushort>();
			foreach (var id in _remotes.Keys) if (!_live.Contains(id)) gone.Add(id);
			foreach (var id in gone) { _remotes[id].QueueFree(); _remotes.Remove(id); }
			gone.Clear();
			foreach (var id in _loot.Keys) if (!_live.Contains(id)) gone.Add(id);
			foreach (var id in gone) { if (GodotObject.IsInstanceValid(_loot[id])) _loot[id].QueueFree(); _loot.Remove(id); }
			gone.Clear();
			foreach (var id in _chests.Keys) if (!_live.Contains(id)) gone.Add(id);
			foreach (var id in gone) { if (GodotObject.IsInstanceValid(_chests[id])) _chests[id].QueueFree(); _chests.Remove(id); if (_chestBoxes.Remove(id)) _chestBoxesDirty = true; }
			if (_chestBoxesDirty) { _chestBoxesDirty = false; _worldCollision?.SetDynamicBoxes(_chestBoxes.Values); }

			// chariots : les autres joueurs sur un câble, puis nous
			foreach (var e in _seen)
				if (e.Kind == EntityKind.Player && e.Zip == 1 && !e.Has(EntityFlags.Dead))
					ShowTrolley(e.Id, new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z), new Vector3(e.Vel.X, e.Vel.Y, e.Vel.Z));
			if (Client.Predicted.Stance == Stance.Zipping)
			{
				var v = Client.ViewPosition;
				ShowTrolley(-1, new Vector3(v.X, v.Y, v.Z), new Vector3(Client.Predicted.Vel.X, Client.Predicted.Vel.Y, Client.Predicted.Vel.Z));
			}
			CleanTrolleys();
			// équipiers : tous ceux de la liste des joueurs, avec leur dernier état connu (même hors de vue)
			var mates = new List<MateView>();
			foreach (var r in Client.Roster)
			{
				if (r.Team != MyTeam || r.Id == Client.PlayerId || r.Id == _follow) continue;
				if (!_mateCache.TryGetValue(r.Id, out var mv)) mv = new MateView { Id = r.Id, Name = r.Name, Health = 100, Bot = r.IsBot };
				mv.Name = r.Name; mv.Appearance ??= r.Appearance;
				mates.Add(mv);
			}
			_hud.SetTeam(mates);
			_hud.SetRadar(_player.GlobalPosition, _player.GlobalRotation.Y, radar);
		}
	}
}
