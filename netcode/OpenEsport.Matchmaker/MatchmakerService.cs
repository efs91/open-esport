using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.Matchmaker
{
	public sealed class MatchmakerOptions
	{
		/// <summary>Port UDP sur lequel le service écoute les demandes des joueurs.</summary>
		public int Port = Matchmaking.DefaultPort;
		/// <summary>Adresse donnée aux joueurs pour rejoindre un serveur de partie (le nom ou l'IP de la machine, vu d'internet).</summary>
		public string PublicHost = "127.0.0.1";
		/// <summary>Adresse à laquelle le service interroge ses serveurs (par défaut la même ; « host.docker.internal » si le service tourne lui-même dans Docker).</summary>
		public string ProbeHost;
		/// <summary>Réseau Docker commun au matchmaking et aux serveurs de partie : les serveurs y sont sondés par leur nom de conteneur, sans passer par les ports publiés de la machine (traduction d'adresses de Docker, peu fiable en UDP).</summary>
		public string Network;
		/// <summary>Ports UDP des serveurs de partie (un par partie en cours ou en attente).</summary>
		public int PortFirst = 27100, PortLast = 27199;
		/// <summary>Joueurs par partie (humains + bots) et humains nécessaires pour la lancer.</summary>
		public int Players = 30, MinPlayers = 1;
		public float LobbyCountdown = 5f;
		/// <summary>Un serveur dont la salle d'attente reste vide ce nombre de secondes s'arrête tout seul.</summary>
		public float IdleExitSeconds = 120f;
		/// <summary>Combien de temps (s) un serveur lancé a pour répondre avant d'être détruit.</summary>
		public double StartTimeoutSeconds = 90;
		/// <summary>Tableau de bord web et API d'administration : port (0 = désactivé), adresse d'écoute et jeton nécessaire pour arrêter un serveur.</summary>
		/// <summary>Zones du battle royale : nom d'un préréglage (apex, fortnite) ou fichier JSON créé avec le panneau Godot (data/zones/*.json).</summary>
		public string Zones = "apex";
		public string ZonesFile;
		public int AdminPort = 27080;
		public string AdminBind = "127.0.0.1";
		public string AdminToken;
		public string ProbeHostOrPublic => string.IsNullOrEmpty(ProbeHost) ? PublicHost : ProbeHost;
	}

	/// <summary>Ce qu'il faut pour lancer un serveur de partie.</summary>
	public sealed class ServerSpec
	{
		public string Name;
		public int Port, TeamSize, Players, MinPlayers;
		/// <summary>Mode de jeu du serveur (la carte et les règles en dépendent).</summary>
		public GameMode Mode = GameMode.BattleRoyale;
		public float LobbyCountdown, IdleExitSeconds;
		/// <summary>Valeur de « --zones » passée au serveur (nom d'un préréglage, ou chemin du fichier vu depuis le serveur).</summary>
		public string Zones;
		/// <summary>Partie d'un tournoi (0 = partie ordinaire) : identifiant, manche et lobby. Une telle partie est réservée aux inscrits du lobby.</summary>
		public long Tournament; public int TournamentRace, TournamentLobby;
		/// <summary>Match de duel (clé ManageComp) : partie par équipes réservée aux deux camps, sans bots.</summary>
		public string TournamentMatch;
	}

	/// <summary>Lance et arrête des serveurs de partie : dans des conteneurs Docker (<see cref="DockerLauncher"/>) ou, pour le développement, en processus.</summary>
	public interface IServerLauncher
	{
		/// <summary>Démarre le serveur (retourne dès que la commande de lancement est acceptée).</summary>
		void Launch(ServerSpec spec);
		void Stop(string name);
		/// <summary>Texte de diagnostic (état, code de sortie, logs) d'un serveur qu'on va retirer ; null si le lanceur n'en fournit pas.</summary>
		string Diagnose(string name) => null;
		/// <summary>Supprime les serveurs restés d'une exécution précédente.</summary>
		void CleanupLeftovers();
	}

	public sealed class GameServerInfo
	{
		public ServerSpec Spec;
		public DateTime LaunchedAt = DateTime.UtcNow;
		public DateTime LastReply = DateTime.MinValue;
		public bool Ready;
		public MatchPhase Phase;
		public int Humans, Needed, Max;
		/// <summary>Partie en cours (modes hors battle royale) : temps restant (s) et scores des deux équipes.</summary>
		public int TimeLeft, Score0, Score1;
		/// <summary>Dernier rapport de télémétrie du serveur (JSON) et instant de sa réception.</summary>
		public string TelemetryJson;
		public DateTime TelemetryAt = DateTime.MinValue;
		public readonly List<DateTime> Pending = new List<DateTime>();
		public int PendingCount { get { Pending.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 20); return Pending.Count; } }
	}

	/// <summary>
	/// Le service qui tourne en permanence : il reçoit les demandes des joueurs, les range dans une salle d'attente existante du bon mode
	/// (solo, 2, 3, 4 ou 5) ou lance un nouveau serveur de partie, et surveille tous ses serveurs.
	/// </summary>
	public sealed class MatchmakerService
	{
		private readonly MatchmakerOptions _opt;
		private readonly IServerLauncher _launcher;
		private readonly EventBasedNetListener _listener = new EventBasedNetListener();
		private readonly NetManager _net;
		private readonly List<GameServerInfo> _servers = new List<GameServerInfo>();
		private DateTime _nextProbe = DateTime.MinValue, _nextReport = DateTime.MinValue;
		/// <summary>Service de comptes : le matchmaking lui envoie chaque minute le nombre de joueurs en partie (statistiques d'utilisation). Vide : pas d'envoi.</summary>
		public string AccountsUrl, AccountsKey;
		private static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		public Action<string> Log = s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

		public int ServerCount { get { lock (_servers) return _servers.Count; } }
		public IReadOnlyList<GameServerInfo> Servers { get { lock (_servers) return _servers.ToList(); } }

		public MatchmakerService(MatchmakerOptions options, IServerLauncher launcher)
		{
			_opt = options;
			_launcher = launcher;
			_net = new NetManager(_listener) { UnconnectedMessagesEnabled = true, UnsyncedEvents = false };
			_listener.NetworkReceiveUnconnectedEvent += OnMessage;
		}

		public void Run(CancellationToken ct)
		{
			if (!_net.Start(_opt.Port)) throw new InvalidOperationException($"Impossible d'ouvrir le port UDP {_opt.Port} (déjà utilisé ?)");
			Log($"Matchmaking démarré : port {_opt.Port}, serveurs de partie sur les ports {_opt.PortFirst}-{_opt.PortLast}, adresse publique {_opt.PublicHost}");
			try { _launcher.CleanupLeftovers(); } catch (Exception e) { Log("Nettoyage impossible : " + e.Message); }
			while (!ct.IsCancellationRequested)
			{
				_net.PollEvents();
				Probe();
				ReportPlayers();
				Thread.Sleep(10);
			}
			_net.Stop();
			Log("Matchmaking arrêté");
		}

		/// <summary>Arrête un serveur de partie (administration) : les joueurs connectés sont déconnectés. Retourne faux si le nom est inconnu.</summary>
		public bool StopServer(string name)
		{
			GameServerInfo found;
			lock (_servers)
			{
				found = _servers.FirstOrDefault(s => s.Spec.Name == name);
				if (found == null) return false;
				_servers.Remove(found);
			}
			try { _launcher.Stop(name); } catch (Exception e) { Log($"Arrêt de {name} : {e.Message}"); }
			Log($"Serveur {name} arrêté (administration)");
			return true;
		}

		/// <summary>Arrête tous les serveurs lancés (à la fermeture du service).</summary>
		public void StopAll()
		{
			lock (_servers)
			{
				foreach (var s in _servers) { try { _launcher.Stop(s.Spec.Name); } catch { } }
				_servers.Clear();
			}
		}

		// ------------------------------------------------------------------ surveillance des serveurs

		private void Probe()
		{
			var now = DateTime.UtcNow;
			if (now < _nextProbe) return;
			_nextProbe = now.AddSeconds(1);
			List<GameServerInfo> snapshot;
			lock (_servers) snapshot = _servers.ToList();
			var status = Encoding.ASCII.GetBytes("STATUS");
			var telemetry = Encoding.ASCII.GetBytes("TELEMETRY");
			foreach (var s in snapshot)
			{
				try
				{
					var ep = NetUtils.MakeEndPoint(string.IsNullOrEmpty(_opt.Network) ? _opt.ProbeHostOrPublic : s.Spec.Name, s.Spec.Port);
					_net.SendUnconnectedMessage(status, 0, status.Length, ep);
					_net.SendUnconnectedMessage(telemetry, 0, telemetry.Length, ep);
				}
				catch { }
				double sinceLaunch = (now - s.LaunchedAt).TotalSeconds;
				double sinceReply = (now - s.LastReply).TotalSeconds;
				bool dead = s.Ready ? sinceReply > 10 : sinceLaunch > _opt.StartTimeoutSeconds;
				if (dead)
				{
					Log($"Serveur {s.Spec.Name} {(s.Ready ? "ne répond plus" : "n'a pas démarré")} : retiré");
					try { var why = _launcher.Diagnose(s.Spec.Name); if (!string.IsNullOrEmpty(why)) Log($"  diagnostic {s.Spec.Name} : {why}"); } catch { }
					try { _launcher.Stop(s.Spec.Name); } catch { }
					lock (_servers) _servers.Remove(s);
				}
			}
		}

		/// <summary>Chaque minute : humains en salle d'attente et en partie, par mode, envoyés au service de comptes (en tâche de fond, sans bloquer le matchmaking).</summary>
		private void ReportPlayers()
		{
			if (string.IsNullOrEmpty(AccountsUrl) || string.IsNullOrEmpty(AccountsKey) || DateTime.UtcNow < _nextReport) return;
			_nextReport = DateTime.UtcNow.AddSeconds(60);
			var playing = new Dictionary<string, int>(); var lobby = new Dictionary<string, int>();
			lock (_servers)
				foreach (var s in _servers.Where(x => x.Ready))
				{
					var d = s.Phase == MatchPhase.Lobby ? lobby : playing;
					string key = Modes.Get(s.Spec.Mode).Key;
					d[key] = d.GetValueOrDefault(key) + s.Humans;
				}
			string json = System.Text.Json.JsonSerializer.Serialize(new { playing, lobby });
			_ = System.Threading.Tasks.Task.Run(async () =>
			{
				try
				{
					using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, AccountsUrl.TrimEnd('/') + "/internal/telemetry/servers") { Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json") };
					req.Headers.Add("X-Server-Key", AccountsKey);
					using var _ = await Http.SendAsync(req);
				}
				catch (Exception) { }
			});
		}

		/// <summary>Statistiques d'utilisation (JSON) demandées au service de comptes pour le tableau de bord ; null s'il ne répond pas.</summary>
		public async System.Threading.Tasks.Task<string> UsageStatsAsync(int days)
		{
			if (string.IsNullOrEmpty(AccountsUrl) || string.IsNullOrEmpty(AccountsKey)) return null;
			try
			{
				using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{AccountsUrl.TrimEnd('/')}/internal/telemetry/stats?days={days}");
				req.Headers.Add("X-Server-Key", AccountsKey);
				using var res = await Http.SendAsync(req);
				return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync() : null;
			}
			catch (Exception) { return null; }
		}

		// ------------------------------------------------------------------ messages

		private void OnMessage(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
		{
			string text;
			try { text = Encoding.UTF8.GetString(reader.RawData, reader.UserDataOffset, reader.UserDataSize); }
			catch (Exception) { return; }
			if (text.StartsWith("OE|", StringComparison.Ordinal)) { OnServerStatus(remote, text); return; }
			if (text.StartsWith("{", StringComparison.Ordinal))
			{
				lock (_servers) { var s = _servers.FirstOrDefault(x => x.Spec.Port == remote.Port); if (s != null) { s.TelemetryJson = text; s.TelemetryAt = DateTime.UtcNow; } }
				return;
			}
			if (!text.StartsWith(Matchmaking.RequestTag, StringComparison.Ordinal)) return;
			string reply;
			try { reply = HandleRequest(text); }
			catch (Exception e) { Log("Demande invalide : " + e.Message); reply = Matchmaking.ReplyTag + "ERR|Demande invalide"; }
			var bytes = Encoding.UTF8.GetBytes(reply);
			_net.SendUnconnectedMessage(bytes, 0, bytes.Length, remote);
		}

		private void OnServerStatus(IPEndPoint remote, string text)
		{
			var p = text.Split('|');
			if (p.Length < 7) return;
			lock (_servers)
			{
				var s = _servers.FirstOrDefault(x => x.Spec.Port == remote.Port);
				if (s == null) return;
				s.Ready = true;
				s.LastReply = DateTime.UtcNow;
				s.Phase = (MatchPhase)int.Parse(p[1]);
				s.Humans = int.Parse(p[2]); s.Needed = int.Parse(p[3]); s.Max = int.Parse(p[5]);
				if (p.Length >= 10) { int.TryParse(p[7], out s.TimeLeft); int.TryParse(p[8], out s.Score0); int.TryParse(p[9], out s.Score1); }
			}
		}

		/// <summary>Une demande de partie : salle d'attente du bon mode avec de la place, sinon lancement d'un serveur.</summary>
		public string HandleRequest(string text)
		{
			var p = text.Split('|');
			if (p.Length < 3 || p[0] != Matchmaking.RequestTag) return Matchmaking.ReplyTag + "ERR|Demande invalide";
			if (int.Parse(p[1]) != Protocol.Version) return Matchmaking.ReplyTag + "ERR|Version du jeu différente de celle du service";
			GameMode mode = GameMode.BattleRoyale;
			if (p.Length >= 4 && !Modes.TryParse(p[3], out mode)) return Matchmaking.ReplyTag + "ERR|Mode de jeu inconnu";
			int rawTeamSize = Math.Clamp(int.Parse(p[2]), MatchConfig.MinTeamSize, MatchConfig.MaxTeamSize);
			int teamSize = Modes.ValidTeamSize(mode, rawTeamSize);
			// un groupe réserve autant de places que de membres (le chef demande pour tout le monde)
			int party = p.Length >= 5 && int.TryParse(p[4], out int n) ? Math.Clamp(n, 1, MatchConfig.MaxTeamSize) : 1;
			string label = Modes.Label(mode, teamSize);

			// partie de tournoi : « MM?|version|taille|mode|groupe|T tournoi:manche:lobby » — tous les joueurs du lobby vont sur le même serveur, réservé à eux ;
			// « …|TD tournoi:clé » : un match de poule ou de tableau. Le mode et la taille des équipes sont ceux de la phase (le serveur les vérifie auprès du service de comptes).
			if (p.Length >= 6 && p[5].StartsWith("T", StringComparison.Ordinal))
			{
				if (p[5].StartsWith("TD", StringComparison.Ordinal))
				{
					var d = p[5].Substring(2).Split(':');
					if (d.Length != 2 || !long.TryParse(d[0], out long did) || did <= 0 || d[1].Length == 0 || d[1].Length > 12 || !d[1].All(char.IsLetterOrDigit)) return Matchmaking.ReplyTag + "ERR|Demande de duel invalide";
					return HandleDuelRequest(did, d[1], mode, Math.Clamp(rawTeamSize, 1, 5));
				}
				var t = p[5].Substring(1).Split(':');
				if (t.Length != 3 || !long.TryParse(t[0], out long tid) || !int.TryParse(t[1], out int trace) || !int.TryParse(t[2], out int tlobby) || tid <= 0 || trace < 0 || tlobby < 0)
					return Matchmaking.ReplyTag + "ERR|Demande de tournoi invalide";
				return HandleTournamentRequest(tid, trace, tlobby, mode, Math.Clamp(rawTeamSize, 1, 5));
			}

			lock (_servers)
			{
				// 1) une salle d'attente du bon mode, prête, avec de la place : on remplit la plus pleine ;
				//    sinon (Deathmatch, Team Deathmatch, Capture du drapeau) une partie en cours avec de la place et au moins une minute à jouer
				var open = _servers.Where(s => s.Spec.Mode == mode && s.Spec.TeamSize == teamSize && Joinable(s, party) == null && (s.Phase == MatchPhase.Lobby || s.TimeLeft >= 60))
					.OrderBy(s => s.Phase == MatchPhase.Lobby ? 0 : 1).ThenByDescending(s => s.Humans + s.PendingCount).FirstOrDefault();
				if (open != null)
				{
					for (int i = 0; i < party; i++) open.Pending.Add(DateTime.UtcNow);
					return $"{Matchmaking.ReplyTag}OK|{_opt.PublicHost}|{open.Spec.Port}|{open.Humans + open.PendingCount}|{open.Needed}|{teamSize}";
				}
				// 2) un serveur du bon mode est déjà en train de démarrer : on attend
				if (_servers.Any(s => !s.Ready && s.Spec.Mode == mode && s.Spec.TeamSize == teamSize))
					return Matchmaking.ReplyTag + "WAIT|Préparation d'une partie " + label + "…";
				// 3) sinon on en lance un
				int port = FreePort();
				if (port < 0) return Matchmaking.ReplyTag + "ERR|Tous les serveurs sont occupés, réessaie dans un instant";
				var spec = new ServerSpec
				{
					Name = $"open-esport-{Modes.Name(mode, teamSize)}-{port}", Port = port, Mode = mode, TeamSize = teamSize, Players = mode == GameMode.BattleRoyale ? _opt.Players : Modes.Get(mode).PlayersFor(teamSize),
					MinPlayers = _opt.MinPlayers, LobbyCountdown = _opt.LobbyCountdown, IdleExitSeconds = _opt.IdleExitSeconds, Zones = _opt.Zones
				};
				try { _launcher.Launch(spec); }
				catch (Exception e) { Log($"Lancement de {spec.Name} impossible : {e.Message}"); return Matchmaking.ReplyTag + "ERR|Impossible de lancer un serveur de partie : " + e.Message.Replace('|', '/'); }
				_servers.Add(new GameServerInfo { Spec = spec });
				Log($"Serveur lancé : {spec.Name} ({label}, {spec.Players} joueurs)");
				return Matchmaking.ReplyTag + "WAIT|Lancement d'une partie " + label + "…";
			}
		}

		/// <summary>
		/// La partie d'une manche de tournoi : le serveur du lobby s'il existe, sinon on le lance — battle royale, ou Capture du drapeau (deux équipes),
		/// avec les joueurs du lobby complétés par des bots.
		/// </summary>
		private string HandleTournamentRequest(long tournament, int race, int lobby, GameMode mode, int teamSize)
		{
			if (mode != GameMode.BattleRoyale && mode != GameMode.CaptureTheFlag) return Matchmaking.ReplyTag + "ERR|Mode de jeu inattendu pour une manche de tournoi";
			lock (_servers)
			{
				var existing = _servers.FirstOrDefault(s => s.Spec.Tournament == tournament && s.Spec.TournamentRace == race && s.Spec.TournamentLobby == lobby);
				if (existing != null)
					return existing.Ready ? $"{Matchmaking.ReplyTag}OK|{_opt.PublicHost}|{existing.Spec.Port}|{existing.Humans}|{existing.Needed}|1" : Matchmaking.ReplyTag + "WAIT|Préparation de ta manche de tournoi…";
				int port = FreePort();
				if (port < 0) return Matchmaking.ReplyTag + "ERR|Tous les serveurs sont occupés, réessaie dans un instant";
				var spec = new ServerSpec
				{
					Name = $"open-esport-tournoi-{Modes.Get(mode).Key}-{tournament}-{race}-{lobby}-{port}", Port = port, Mode = mode, TeamSize = teamSize, Players = mode == GameMode.BattleRoyale ? _opt.Players : 2 * teamSize,
					MinPlayers = _opt.MinPlayers, LobbyCountdown = _opt.LobbyCountdown, IdleExitSeconds = _opt.IdleExitSeconds, Zones = _opt.Zones,
					Tournament = tournament, TournamentRace = race, TournamentLobby = lobby
				};
				try { _launcher.Launch(spec); }
				catch (Exception e) { Log($"Lancement de {spec.Name} impossible : {e.Message}"); return Matchmaking.ReplyTag + "ERR|Impossible de lancer un serveur de partie : " + e.Message.Replace('|', '/'); }
				_servers.Add(new GameServerInfo { Spec = spec });
				Log($"Serveur de tournoi lancé : {spec.Name}");
				return Matchmaking.ReplyTag + "WAIT|Lancement de ta manche de tournoi…";
			}
		}

		/// <summary>
		/// Un match de poule ou de tableau : un serveur réservé aux deux camps (<paramref name="teamSize"/> joueurs chacun), sans bots —
		/// 1 VS 1, Capture du drapeau, ou match à mort par équipe (anciens tournois en duels).
		/// </summary>
		private string HandleDuelRequest(long tournament, string key, GameMode mode, int teamSize)
		{
			if (mode != GameMode.Duel && mode != GameMode.CaptureTheFlag && mode != GameMode.TeamDeathmatch) return Matchmaking.ReplyTag + "ERR|Mode de jeu inattendu pour un match de tournoi";
			if (mode == GameMode.Duel) teamSize = 1;
			lock (_servers)
			{
				var existing = _servers.FirstOrDefault(s => s.Spec.Tournament == tournament && s.Spec.TournamentMatch == key);
				if (existing != null)
					return existing.Ready ? $"{Matchmaking.ReplyTag}OK|{_opt.PublicHost}|{existing.Spec.Port}|{existing.Humans}|{existing.Needed}|{teamSize}" : Matchmaking.ReplyTag + "WAIT|Préparation de ton match de tournoi…";
				int port = FreePort();
				if (port < 0) return Matchmaking.ReplyTag + "ERR|Tous les serveurs sont occupés, réessaie dans un instant";
				var spec = new ServerSpec
				{
					Name = $"open-esport-match-{Modes.Get(mode).Key}-{tournament}-{key}-{port}", Port = port, Mode = mode, TeamSize = teamSize, Players = teamSize * 2,
					MinPlayers = 1, LobbyCountdown = _opt.LobbyCountdown, IdleExitSeconds = _opt.IdleExitSeconds, Zones = _opt.Zones, Tournament = tournament, TournamentMatch = key
				};
				try { _launcher.Launch(spec); }
				catch (Exception e) { Log($"Lancement de {spec.Name} impossible : {e.Message}"); return Matchmaking.ReplyTag + "ERR|Impossible de lancer un serveur de partie : " + e.Message.Replace('|', '/'); }
				_servers.Add(new GameServerInfo { Spec = spec });
				Log($"Serveur de duel lancé : {spec.Name}");
				return Matchmaking.ReplyTag + "WAIT|Lancement de ton match de tournoi…";
			}
		}

		/// <summary>Pourquoi on ne peut pas rejoindre ce serveur (null = on peut) : pas prêt, tournoi réservé, partie commencée dans un mode qui ne l'accepte pas, terminée, pleine.</summary>
		private static string Joinable(GameServerInfo s, int party)
		{
			if (!s.Ready) return "Partie en préparation";
			if (s.Spec.Tournament != 0) return "Partie de tournoi réservée aux inscrits";
			if (s.Phase == MatchPhase.Over) return "Partie terminée";
			if (s.Phase == MatchPhase.Playing && !Modes.JoinInProgress(s.Spec.Mode)) return "Partie déjà commencée";
			if (s.Humans + s.PendingCount + party > s.Max) return "Partie pleine";
			return null;
		}

		private int FreePort()
		{
			for (int port = _opt.PortFirst; port <= _opt.PortLast; port++)
				if (_servers.All(s => s.Spec.Port != port)) return port;
			return -1;
		}
	}
}
