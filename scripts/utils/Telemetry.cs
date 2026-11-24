using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Openesport.Utils
{
	/// <summary>
	/// Statistiques d'utilisation anonymes (mesure d'audience) : écrans du menu et temps passé, boutons du menu, création de personnage, matchmaking, parties jouées.
	/// <list type="bullet">
	/// <item>Anonyme : un identifiant aléatoire de l'installation (user://telemetry.cfg), renouvelé tous les 13 mois, jamais lié au compte.</item>
	/// <item>Sans lag : les événements sont notés en mémoire ; l'envoi se fait en tâche de fond, une fois par minute et seulement hors partie (en partie : rien n'est envoyé,
	/// et la surveillance des écrans est coupée). Si le service est injoignable, les événements sont perdus, sans erreur ni blocage.</item>
	/// <item>Désactivable dans les réglages (<see cref="Enabled"/>).</item>
	/// </list>
	/// </summary>
	public partial class Telemetry : Node
	{
		private const string ConfigPath = "user://telemetry.cfg";
		private const int RotateDays = 395;          // 13 mois
		private const double FlushSeconds = 60;
		private const int MaxQueued = 500;

		private struct Ev { public long t; public string k, n, x; public double v; }

		private static Telemetry _instance;
		private static readonly object _gate = new object();
		private static readonly List<Ev> _queue = new List<Ev>();
		private static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
		private static bool? _enabled;
		private static string _anon;
		private static readonly string _session = Guid.NewGuid().ToString("N");

		private string _url;
		private double _nextFlush;
		private bool _inGame, _watching;
		private readonly Dictionary<ulong, (string name, ulong since)> _screens = new();
		private string _gameMode;
		private ulong _gameSince;

		/// <summary>L'envoi est-il autorisé ? (case des réglages, cochée par défaut)</summary>
		public static bool Enabled
		{
			get
			{
				if (_enabled == null) { var cfg = new ConfigFile(); _enabled = cfg.Load(ConfigPath) != Error.Ok || (bool)cfg.GetValue("telemetry", "enabled", true); }
				return _enabled.Value;
			}
			set
			{
				_enabled = value;
				var cfg = new ConfigFile(); cfg.Load(ConfigPath);
				cfg.SetValue("telemetry", "enabled", value);
				cfg.Save(ConfigPath);
				if (!value) lock (_gate) _queue.Clear();
			}
		}

		/// <summary>Démarre la télémétrie (une fois, au lancement du jeu).</summary>
		public static void Install(Node root)
		{
			if (_instance != null || DisplayServer.GetName() == "headless") return;
			_instance = new Telemetry { Name = "Telemetry", ProcessMode = ProcessModeEnum.Always };
			root.CallDeferred(Node.MethodName.AddChild, _instance);
		}

		/// <summary>Note un événement (sorte, nom, valeur — souvent une durée en secondes —, précision). Ne coûte qu'un ajout en mémoire.</summary>
		public static void Track(string kind, string name = "", double value = 0, string extra = "")
		{
			if (_instance == null || !Enabled) return;
			lock (_gate)
			{
				if (_queue.Count >= MaxQueued) return;
				_queue.Add(new Ev { t = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), k = kind, n = name ?? "", v = value, x = extra ?? "" });
			}
		}

		/// <summary>Une partie commence (mode « clé:taille ») : la surveillance des écrans et l'envoi s'arrêtent jusqu'au retour au menu.</summary>
		public static void GameStarted(string mode)
		{
			if (_instance == null) return;
			_instance._gameMode = mode;
			_instance._gameSince = Time.GetTicksMsec();
		}

		public override void _Ready()
		{
			_url = Openesport.Online.MatchSettings.AccountsUrl().TrimEnd('/') + "/api/telemetry";
			LoadId();
			var gm = GetNodeOrNull<global::GameManager>(GlobalPaths.Managers.GAME_MANAGER);
			if (gm != null) gm.GameStateChanged += OnGameState;
			Watch(true);
			Track("start");
			_nextFlush = 3;              // premier envoi peu après le lancement : on compte aussi ceux qui repartent vite
		}

		private static void LoadId()
		{
			var cfg = new ConfigFile();
			cfg.Load(ConfigPath);
			string id = (string)cfg.GetValue("telemetry", "id", "");
			long created = (long)cfg.GetValue("telemetry", "created", 0L);
			long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			if (string.IsNullOrEmpty(id) || now - created > RotateDays * 86400L)
			{
				id = Guid.NewGuid().ToString("N"); created = now;
				cfg.SetValue("telemetry", "id", id);
				cfg.SetValue("telemetry", "created", created);
				cfg.Save(ConfigPath);
			}
			_anon = id;
		}

		/// <summary>Surveille l'ouverture et la fermeture des écrans du menu (coupé en partie : aucun coût pendant le jeu).</summary>
		private void Watch(bool on)
		{
			if (on == _watching) return;
			_watching = on;
			var tree = GetTree();
			if (on) { tree.NodeAdded += OnNodeAdded; tree.NodeRemoved += OnNodeRemoved; }
			else { tree.NodeAdded -= OnNodeAdded; tree.NodeRemoved -= OnNodeRemoved; }
		}

		private static string ScreenName(Node n)
		{
			if (n is not CanvasLayer) return null;
			var type = n.GetType();
			if (type.Name == "CharacterCreator") return type.Name;
			return type.Namespace == "Openesport.Menus" ? type.Name : null;
		}

		private void OnNodeAdded(Node n)
		{
			string name = ScreenName(n);
			if (name != null) _screens[n.GetInstanceId()] = (name, Time.GetTicksMsec());
		}

		private void OnNodeRemoved(Node n)
		{
			if (n is not CanvasLayer || !_screens.Remove(n.GetInstanceId(), out var s)) return;
			Track("screen", s.name, Math.Round((Time.GetTicksMsec() - s.since) / 1000.0, 1));
		}

		/// <summary>Menu principal : ouvert, il compte comme un écran (« MainMenu »).</summary>
		public static void MainMenuShown(bool shown)
		{
			if (_instance == null) return;
			const ulong key = 0;
			if (shown) { if (!_instance._screens.ContainsKey(key)) _instance._screens[key] = ("MainMenu", Time.GetTicksMsec()); }
			else if (_instance._screens.Remove(key, out var s)) Track("screen", s.name, Math.Round((Time.GetTicksMsec() - s.since) / 1000.0, 1));
		}

		private void OnGameState(int state)
		{
			var st = (global::GameManager.GameState)state;
			if (st == global::GameManager.GameState.Gameplay || st == global::GameManager.GameState.Pause)
			{
				if (_inGame) return;
				_inGame = true;
				Watch(false);
			}
			else if (st == global::GameManager.GameState.MainMenu && _inGame)
			{
				_inGame = false;
				Watch(true);
				if (_gameMode != null) { Track("game", _gameMode, Math.Round((Time.GetTicksMsec() - _gameSince) / 1000.0)); _gameMode = null; }
				_nextFlush = 0;          // retour au menu : on envoie ce qui s'est accumulé
			}
		}

		public override void _Process(double delta)
		{
			if (_inGame) return;
			_nextFlush -= delta;
			if (_nextFlush > 0) return;
			_nextFlush = FlushSeconds;
			Flush();
		}

		public override void _Notification(int what)
		{
			if (what == NotificationWMCloseRequest)
			{
				foreach (var kv in _screens) Track("screen", kv.Value.name, Math.Round((Time.GetTicksMsec() - kv.Value.since) / 1000.0, 1));
				_screens.Clear();
				Track("quit");
				Flush();                 // au mieux : l'envoi peut ne pas aboutir si le jeu se ferme avant
			}
		}

		/// <summary>Envoie les événements en attente (même aucun : signe de vie au menu), en tâche de fond.</summary>
		private void Flush()
		{
			if (!Enabled || string.IsNullOrEmpty(_anon)) return;
			List<Ev> batch;
			lock (_gate) { batch = new List<Ev>(_queue); _queue.Clear(); }
			var events = new List<object>(batch.Count);
			foreach (var e in batch) events.Add(new { t = e.t, k = e.k, n = e.n, v = e.v, x = e.x });
			string json = System.Text.Json.JsonSerializer.Serialize(new
			{
				anon = _anon, session = _session, version = Openesport.Menus.GameVersion.Current, os = OS.GetName(), lang = Main.language, events,
			});
			string url = _url;
			Task.Run(async () =>
			{
				try { using var res = await Http.PostAsync(url, new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")); }
				catch (Exception) { }
			});
		}
	}
}
