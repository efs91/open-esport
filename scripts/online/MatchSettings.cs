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
		private static int _teamSize = -1;

		private static int _mode = -1;

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
