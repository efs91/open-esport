using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Réglages de la partie en ligne : mode de jeu choisi (solo, équipe de 2 à 5) et adresse du service de matchmaking.
	/// Le mode est mémorisé d'une session à l'autre. L'adresse du matchmaking vient, par ordre de priorité, de l'option
	/// <c>--matchmaker=hôte:port</c>, de la variable d'environnement <c>OE_MATCHMAKER</c>, du fichier <c>user://network.cfg</c>, sinon 127.0.0.1:27000.
	/// </summary>
	public static class MatchSettings
	{
		private const string Path = "user://network.cfg";
		private const string ProjectCfg = "res://data/network.cfg";
		private static int _teamSize = -1;

		private static int _mode = -1;

		/// <summary>
		/// Reprise de partie : la partie en cours est retenue tant qu'on y joue (instant de la dernière présence mis à jour régulièrement).
		/// Après une coupure ou un plantage, « Jouer » y ramène directement tant que le serveur garde le personnage (180 s côté serveur).
		/// Oubliée quand on quitte volontairement, en fin de partie, ou si le serveur refuse le retour.
		/// </summary>
		public const double ResumeSeconds = 170;

		public static void RememberMatch(string host, int port)
		{
			var cfg = new ConfigFile();
			cfg.Load(Path);
			cfg.SetValue("resume", "host", host);
			cfg.SetValue("resume", "port", port);
			cfg.SetValue("resume", "mode", (int)Mode);
			cfg.SetValue("resume", "team_size", TeamSize);
			cfg.SetValue("resume", "seen", Time.GetUnixTimeFromSystem());
			cfg.Save(Path);
		}

		/// <summary>Toujours dans la partie : met à jour l'instant de la dernière présence.</summary>
		public static void TouchMatch()
		{
			var cfg = new ConfigFile();
			if (cfg.Load(Path) != Error.Ok || !cfg.HasSection("resume")) return;
			cfg.SetValue("resume", "seen", Time.GetUnixTimeFromSystem());
			cfg.Save(Path);
		}

		public static void ForgetMatch()
		{
			var cfg = new ConfigFile();
			if (cfg.Load(Path) != Error.Ok || !cfg.HasSection("resume")) return;
			cfg.EraseSection("resume");
			cfg.Save(Path);
		}

		/// <summary>Une partie quittée sur une coupure il y a peu : on peut y revenir.</summary>
		public static bool TryResume(out string host, out int port, out GameMode mode, out int teamSize)
		{
			host = null; port = 0; mode = GameMode.BattleRoyale; teamSize = 1;
			var cfg = new ConfigFile();
			if (cfg.Load(Path) != Error.Ok || !cfg.HasSection("resume")) return false;
			double seen = (double)cfg.GetValue("resume", "seen", 0.0);
			if (Time.GetUnixTimeFromSystem() - seen > ResumeSeconds) { ForgetMatch(); return false; }
			host = (string)cfg.GetValue("resume", "host", "");
			port = (int)cfg.GetValue("resume", "port", 0);
			mode = (GameMode)Mathf.Clamp((int)cfg.GetValue("resume", "mode", 0), 0, 5);
			teamSize = (int)cfg.GetValue("resume", "team_size", 1);
			return host.Length > 0 && port > 0;
		}

		/// <summary>Mode de jeu choisi dans le menu Multijoueur (mémorisé).</summary>
		public static GameMode Mode
		{
			get
			{
				if (_mode < 0)
				{
					var cfg = new ConfigFile();
					_mode = cfg.Load(Path) == Error.Ok ? Mathf.Clamp((int)cfg.GetValue("match", "mode", 0), 0, 5) : 0;
				}
				return (GameMode)_mode;
			}
			set
			{
				_mode = (int)value;
				var cfg = new ConfigFile();
				cfg.Load(Path);
				cfg.SetValue("match", "mode", _mode);
				cfg.Save(Path);
			}
		}

		private static int _loadout = -1;

		/// <summary>Équipement de départ choisi dans le menu (hors battle royale) : numéro dans Loadouts.Kits (mémorisé).</summary>
		public static int Loadout
		{
			get
			{
				if (_loadout < 0)
				{
					var cfg = new ConfigFile();
					_loadout = cfg.Load(Path) == Error.Ok ? (int)cfg.GetValue("match", "loadout", 0) : 0;
				}
				return Mathf.Clamp(_loadout, 0, OpenEsport.Core.Loadouts.Kits.Length - 1);
			}
			set
			{
				_loadout = Mathf.Clamp(value, 0, OpenEsport.Core.Loadouts.Kits.Length - 1);
				var cfg = new ConfigFile();
				cfg.Load(Path);
				cfg.SetValue("match", "loadout", _loadout);
				cfg.Save(Path);
			}
		}

		/// <summary>Taille de l'équipe : 1 (solo) à 10 selon le mode.</summary>
		public static int TeamSize
		{
			get
			{
				if (_teamSize < 0)
				{
					var cfg = new ConfigFile();
					_teamSize = cfg.Load(Path) == Error.Ok ? Mathf.Clamp((int)cfg.GetValue("match", "team_size", 1), 1, 10) : 1;
				}
				return _teamSize;
			}
			set
			{
				_teamSize = Mathf.Clamp(value, 1, 10);
				var cfg = new ConfigFile();
				cfg.Load(Path);
				cfg.SetValue("match", "team_size", _teamSize);
				cfg.Save(Path);
			}
		}

		/// <summary>Serveur de partie imposé (<c>--server=hôte:port</c>, pour le développement) : on saute alors le matchmaking.</summary>
		public static bool TryDirectServer(out string host, out int port)
		{
			host = null; port = 27015;
			foreach (var arg in OS.GetCmdlineUserArgs())
			{
				if (!arg.StartsWith("--server=")) continue;
				Split(arg.Substring(9), 27015, out host, out port);
				return true;
			}
			return false;
		}

		public static void MatchmakerAddress(out string host, out int port)
		{
			host = "127.0.0.1"; port = Matchmaking.DefaultPort;
			foreach (var arg in OS.GetCmdlineUserArgs())
				if (arg.StartsWith("--matchmaker=")) { Split(arg.Substring(13), Matchmaking.DefaultPort, out host, out port); return; }
			string env = OS.GetEnvironment("OE_MATCHMAKER");
			if (!string.IsNullOrWhiteSpace(env)) { Split(env, Matchmaking.DefaultPort, out host, out port); return; }
			var cfg = new ConfigFile();
			if (cfg.Load(Path) == Error.Ok && cfg.HasSectionKey("network", "matchmaker"))
				Split((string)cfg.GetValue("network", "matchmaker"), Matchmaking.DefaultPort, out host, out port);
			else if (cfg.Load(ProjectCfg) == Error.Ok && cfg.HasSectionKey("network", "host"))       // adresse réglée dans le panneau Open Esport (onglet Réseau), exportée avec le jeu
			{
				host = (string)cfg.GetValue("network", "host"); port = (int)cfg.GetValue("network", "port", Matchmaking.DefaultPort);
			}
		}

		/// <summary>Adresse du service de comptes (http://hôte:27090). Option <c>--accounts=url</c>, variable <c>OE_ACCOUNTS</c>, fichier <c>user://network.cfg</c> ou <c>res://data/network.cfg</c> (clé accounts) ; sinon l'hôte du matchmaking, port 27090.</summary>
		public static string AccountsUrl()
		{
			foreach (var arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith("--accounts=")) return arg.Substring(11);
			string env = OS.GetEnvironment("OE_ACCOUNTS");
			if (!string.IsNullOrWhiteSpace(env)) return env;
			foreach (var file in new[] { Path, ProjectCfg })
			{
				var cfg = new ConfigFile();
				if (cfg.Load(file) == Error.Ok && cfg.HasSectionKey("network", "accounts") && !string.IsNullOrWhiteSpace((string)cfg.GetValue("network", "accounts"))) return (string)cfg.GetValue("network", "accounts");
			}
			MatchmakerAddress(out string host, out _);
			return $"http://{host}:27090";
		}

		private static void Split(string value, int defaultPort, out string host, out int port)
		{
			host = value; port = defaultPort;
			int colon = value.LastIndexOf(':');
			if (colon > 0 && int.TryParse(value.Substring(colon + 1), out int p)) { host = value.Substring(0, colon); port = p; }
		}

		/// <summary>Serveur attribué par le matchmaking pour la partie qui va démarrer.</summary>
		public static string AssignedHost;
		public static int AssignedPort;
	}
}
