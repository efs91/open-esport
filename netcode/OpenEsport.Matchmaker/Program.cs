using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OpenEsport.Matchmaker
{
	/// <summary>Service de matchmaking : toujours allumé, il lance les serveurs de partie dans des conteneurs Docker à la demande des joueurs.</summary>
	public static class Program
	{
		public static int Main(string[] args)
		{
			var opt = new MatchmakerOptions();
			string launcherKind = "docker", image = "open-esport-server:latest", world = null, balance = null, serverDll = null, netcodeDir = null;
			string cpus = "2", memory = "1g";
			bool noBuild = false;
			int ask = 0; string askHost = "127.0.0.1";
			try
			{
				for (int i = 0; i < args.Length; i++)
				{
					string a = args[i];
					string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Valeur manquante pour " + a);
					float F() => float.Parse(Next(), CultureInfo.InvariantCulture);
					switch (a)
					{
						case "--port": opt.Port = int.Parse(Next()); break;
						case "--public-host": opt.PublicHost = Next(); break;
						case "--probe-host": opt.ProbeHost = Next(); break;
						case "--network": opt.Network = Next(); break;
						case "--ports":
							{ var r = Next().Split('-'); opt.PortFirst = int.Parse(r[0]); opt.PortLast = int.Parse(r[1]); break; }
						case "--players": opt.Players = int.Parse(Next()); break;
						case "--min-players": opt.MinPlayers = int.Parse(Next()); break;
						case "--lobby-countdown": opt.LobbyCountdown = F(); break;
						case "--idle-exit": opt.IdleExitSeconds = F(); break;
						case "--launcher": launcherKind = Next(); break;
						case "--image": image = Next(); break;
						case "--world": world = Next(); break;
						case "--balance": balance = Next(); break;
						case "--zones": opt.Zones = Next(); break;
						case "--zones-file": opt.ZonesFile = Next(); break;
						case "--cpus": cpus = Next(); break;
						case "--memory": memory = Next(); break;
						case "--server-dll": serverDll = Next(); break;
						case "--netcode-dir": netcodeDir = Next(); break;
						case "--no-build": noBuild = true; break;
						case "--admin-port": opt.AdminPort = int.Parse(Next()); break;
						case "--admin-bind": opt.AdminBind = Next(); break;
						case "--admin-token": opt.AdminToken = Next(); break;
						case "--ask": ask = int.Parse(Next()); break;
						case "--ask-host": askHost = Next(); break;
						case "--help":
						case "-h": PrintHelp(); return 0;
						default: Console.Error.WriteLine("Option inconnue : " + a); PrintHelp(); return 2;
					}
				}
			}
			catch (Exception e) { Console.Error.WriteLine("Arguments invalides : " + e.Message); return 2; }

			if (ask > 0) return Ask(askHost, opt.Port, ask);

			IServerLauncher launcher;
			if (launcherKind == "docker")
			{
				var d = new DockerLauncher { Image = image, WorldFile = world, BalanceFile = balance, ZonesFile = opt.ZonesFile, Cpus = cpus, Memory = memory, Network = opt.Network };
				// sans --netcode-dir : on cherche le Dockerfile à partir du dossier courant et de l'exécutable, en remontant les dossiers
				if (netcodeDir == null)
				{
					foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
					{
						for (var dir = new DirectoryInfo(start); dir != null && netcodeDir == null; dir = dir.Parent)
						{
							if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))) netcodeDir = dir.FullName;
							else if (File.Exists(Path.Combine(dir.FullName, "netcode", "Dockerfile"))) netcodeDir = Path.Combine(dir.FullName, "netcode");
						}
						if (netcodeDir != null) break;
					}
				}
				if (!noBuild && netcodeDir != null)
				{
					try { d.EnsureImage(netcodeDir); }
					catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
				}
				launcher = d;
			}
			else if (launcherKind == "process")
			{
				if (serverDll == null) { Console.Error.WriteLine("--server-dll est requis avec --launcher process"); return 2; }
				launcher = new ProcessLauncher { ServerDll = serverDll, WorldFile = world, ZonesFile = opt.ZonesFile };
			}
			else { Console.Error.WriteLine("--launcher : docker ou process"); return 2; }

			using var cts = new CancellationTokenSource();
			Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
			AppDomain.CurrentDomain.ProcessExit += (s, e) => cts.Cancel();
			var service = new MatchmakerService(opt, launcher);
			AdminApi admin = null;
			if (opt.AdminPort > 0)
			{
				admin = new AdminApi(service, opt.AdminToken ?? Environment.GetEnvironmentVariable("OE_ADMIN_TOKEN"));
				try { admin.Start(opt.AdminBind, opt.AdminPort); }
				catch (Exception e) { Console.Error.WriteLine($"Tableau de bord indisponible (port {opt.AdminPort}) : {e.Message}"); admin = null; }
			}
			try { service.Run(cts.Token); }
			finally { admin?.Stop(); service.StopAll(); }
			return 0;
		}

		/// <summary>Outil de diagnostic : demande une partie comme le ferait le jeu et affiche la réponse.</summary>
		private static int Ask(string host, int port, int teamSize)
		{
			using var c = new OpenEsport.Core.MatchmakerClient { TimeoutSeconds = 120 };
			c.Start(host, port, teamSize);
			string last = null;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			while (c.State == OpenEsport.Core.MatchmakingState.Searching)
			{
				c.Poll();
				if (c.Message != last) { last = c.Message; Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1} s] {last}"); }
				Thread.Sleep(20);
			}
			Console.WriteLine(c.State == OpenEsport.Core.MatchmakingState.Found ? $"Serveur : {c.Host}:{c.ServerPort} ({c.Humans}/{c.Needed} humains)" : "Échec : " + c.Message);
			return c.State == OpenEsport.Core.MatchmakingState.Found ? 0 : 1;
		}

		private static void PrintHelp()
		{
			Console.WriteLine(@"Matchmaking open-esport : reçoit les demandes des joueurs et lance un serveur de partie par salle d'attente (dans Docker).

Options :
  --port N              port UDP des demandes des joueurs (27000)
  --public-host H       adresse donnée aux joueurs pour rejoindre un serveur (127.0.0.1) : le nom ou l'IP de cette machine vue d'internet
  --probe-host H        adresse pour interroger les serveurs (par défaut la même ; host.docker.internal si ce service tourne dans Docker)
  --ports A-B           ports UDP des serveurs de partie (27100-27199)
  --players N           joueurs par partie, bots compris (30)
  --min-players N       humains nécessaires pour lancer la partie (1)
  --lobby-countdown S   compte à rebours avant le départ (5)
  --idle-exit S         un serveur dont la salle d'attente reste vide S secondes s'arrête (120)
  --launcher K          docker (par défaut) ou process (développement, sans Docker)
  --image NOM           image Docker du serveur (open-esport-server:latest)
  --world FICHIER       carte (.world) ; sans option : carte procédurale
  --balance FICHIER     réglages du jeu (balance.json) montés dans chaque conteneur
  --zones NOM           zones du battle royale : apex (par défaut) ou fortnite
  --zones-file FICHIER  zones créées avec le panneau Godot (data/zones/*.json), montées dans chaque conteneur (prioritaire sur --zones)
  --network NOM         réseau Docker du matchmaking : les serveurs de partie le rejoignent et sont sondés par leur nom (fiable) au lieu des ports publiés
  --cpus N --memory M   limites de chaque conteneur (2 / 1g)
  --netcode-dir DOSSIER construit l'image si elle n'existe pas (dossier contenant le Dockerfile)
  --no-build            ne construit jamais l'image
  --server-dll FICHIER  OpenEsport.Server.dll (avec --launcher process)
  --admin-port N        port HTTP du tableau de bord et de l'API d'administration (27080, 0 = désactivé)
  --admin-bind H        adresse d'écoute HTTP (127.0.0.1 ; mettre un proxy HTTPS devant pour un service en ligne)
  --admin-token T       jeton nécessaire pour arrêter un serveur depuis le site (ou variable OE_ADMIN_TOKEN) ; sans jeton, l'arrêt est désactivé
  --ask N               diagnostic : demande une partie en équipe de N au service (--port, --ask-host) et affiche l'adresse reçue

Chaque conteneur porte un nom explicite : open-esport-<mode>-<port> (mode : solo, duo, trio, quad, quint).");
		}
	}
}
