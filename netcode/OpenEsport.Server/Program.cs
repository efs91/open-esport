using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	public static class Program
	{
		[DllImport("winmm.dll")]
		private static extern uint timeBeginPeriod(uint ms);

		/// <summary>
		/// Charge les réglages de jeu (déplacement, armes…). Sans option, on cherche « balance.json » dans le dossier courant puis à
		/// côté du programme ; s'il n'existe pas on l'écrit avec les valeurs par défaut, commentées, pour qu'il soit facile à modifier.
		/// </summary>
		private static void LoadBalance(string path)
		{
			bool explicitPath = path != null;
			if (path == null)
			{
				string local = System.IO.Path.Combine(Environment.CurrentDirectory, "balance.json");
				string beside = System.IO.Path.Combine(AppContext.BaseDirectory, "balance.json");
				path = System.IO.File.Exists(local) ? local : System.IO.File.Exists(beside) ? beside : local;
			}
			if (!System.IO.File.Exists(path))
			{
				if (explicitPath) { Console.Error.WriteLine("Fichier de réglages introuvable : " + path); Environment.Exit(1); }
				try { System.IO.File.WriteAllText(path, GameBalance.ToFileText()); Console.WriteLine("Réglages : fichier créé avec les valeurs par défaut → " + path); }
				catch (Exception e) { Console.Error.WriteLine("Impossible d'écrire " + path + " : " + e.Message); }
				return;
			}
			var warnings = new System.Collections.Generic.List<string>();
			try { GameBalance.ApplyFileText(System.IO.File.ReadAllText(path), warnings); }
			catch (System.Text.Json.JsonException e) { Console.Error.WriteLine($"Fichier de réglages invalide ({path}) : {e.Message}"); Environment.Exit(1); }
			Console.WriteLine($"Réglages : {path} ({GameBalance.ModifiedCount} valeurs différentes des valeurs par défaut)");
			foreach (var w in warnings) Console.Error.WriteLine("  attention : " + w);
		}

		private static float ParseLevel(string v) => v.ToLowerInvariant() switch
		{
			"easy" or "facile" => 0.2f,
			"normal" or "moyen" => 0.5f,
			"hard" or "difficile" => 0.75f,
			"expert" => 0.95f,
			_ => Math.Clamp(float.Parse(v, System.Globalization.CultureInfo.InvariantCulture), 0f, 1f)
		};

		public static int Main(string[] args)
		{
			var opt = new ServerOptions { Dropship = true, Royale = true, FillBots = true };       // en vrai, on arrive en avion ; les tests créent leurs options eux-mêmes (sans avion)
			string balancePath = null;
			try
			{
				for (int i = 0; i < args.Length; i++)
				{
					string a = args[i];
					string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Valeur manquante pour " + a);
					switch (a)
					{
						case "--port": opt.Port = int.Parse(Next()); break;
						case "--players": opt.Match.MaxPlayers = int.Parse(Next()); break;
						case "--team-size": opt.Match.TeamSize = int.Parse(Next()); break;
						case "--tick": opt.Match.TickRate = int.Parse(Next()); break;
						case "--snap-div": opt.Match.SnapshotDivisor = int.Parse(Next()); break;
						case "--interp": opt.Match.InterpTicks = int.Parse(Next()); break;
						case "--radius": opt.Match.InterestRadius = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--respawn": opt.Match.RespawnSeconds = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--friendly-fire": opt.Match.FriendlyFire = true; break;
						case "--loadout": opt.StartingLoadout = true; break;
						case "--fill-bots": opt.FillBots = true; break;
						case "--no-drop": opt.Dropship = false; break;
						case "--classic": opt.Royale = false; break;
						case "--zones": opt.Zones = ZoneConfig.Load(Next()); break;
						case "--dump-zones": Console.WriteLine(ZoneConfig.Load(Next()).ToJson()); return 0;
						case "--royale": opt.Royale = true; break;
						case "--mode":
							if (!Modes.TryParse(Next(), out var parsedMode)) throw new ArgumentException("Mode inconnu (br, dm, tdm, ctf, sab, ctl)");
							opt.Mode = parsedMode;
							if (parsedMode != GameMode.BattleRoyale) { opt.Royale = false; opt.Dropship = false; }
							break;
						case "--min-players": opt.MinPlayers = int.Parse(Next()); break;
						case "--lobby-countdown": opt.LobbyCountdown = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--down-seconds": opt.DownSeconds = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--revive-seconds": opt.ReviveSeconds = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--revived-health": opt.RevivedHealth = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--end-linger": opt.EndLinger = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--exit-when-over": opt.ExitWhenOver = true; break;
						case "--idle-exit": opt.IdleExitSeconds = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--bot-target": opt.BotFillTarget = int.Parse(Next()); break;
						case "--max-bots": opt.MaxBots = int.Parse(Next()); opt.FillBots = opt.MaxBots != 0; break;
						case "--bot-level": opt.BotSkill = ParseLevel(Next()); break;
						case "--bot-skill": opt.BotSkill = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--no-lag-comp": opt.LagCompensation = false; break;
						case "--world": opt.WorldPath = Next(); break;
						case "--balance": balancePath = Next(); break;
						case "--write-balance": System.IO.File.WriteAllText(Next(), GameBalance.ToFileText()); Console.WriteLine("Réglages par défaut écrits."); return 0;
						case "--procedural": opt.ProceduralSize = int.Parse(Next()); break;
						case "--spawn-radius": opt.SpawnClusterRadius = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
						case "--stats": opt.StatsIntervalSeconds = int.Parse(Next()); break;
						case "--help":
						case "-h":
							PrintHelp();
							return 0;
						default:
							Console.Error.WriteLine("Option inconnue : " + a);
							PrintHelp();
							return 2;
					}
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Arguments invalides : " + e.Message);
				return 2;
			}

			if (OperatingSystem.IsWindows()) timeBeginPeriod(1); // précision de Thread.Sleep sous Windows
			Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

			using var cts = new CancellationTokenSource();
			Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
			AppDomain.CurrentDomain.ProcessExit += (s, e) => cts.Cancel();

			LoadBalance(balancePath);
			var server = new GameServer(opt);
			server.Run(cts.Token);
			return 0;
		}

		private static void PrintHelp()
		{
			Console.WriteLine(@"Serveur de jeu open-esport

Options :
  --port N            port UDP (27015)
  --players N         joueurs maximum, 2 à 100 (100)
  --team-size N       taille des équipes, 1 à 5 (3)
  --tick N            fréquence de simulation en Hz (60)
  --snap-div N        un snapshot tous les N ticks (2)
  --interp N          retard d'interpolation des clients, en ticks (5)
  --radius M          rayon d'intérêt en mètres (400)
  --respawn S         délai de réapparition en secondes, 0 = élimination (3)
  --friendly-fire     active les tirs alliés
  --fill-bots         complète les parties non pleines avec des bots
  --bot-target N      nombre de joueurs visé avec les bots (0 = partie pleine)
  --max-bots N        nombre maximal de bots en même temps (0 = aucun, active --fill-bots sinon)
  --bot-level L       niveau des bots : easy, normal, hard, expert ou un nombre de 0 à 1
  --bot-skill X       niveau des bots, 0 à 1 (0.5)
  --mode M            mode de jeu : br (battle royale, par défaut), dm, tdm, ctf, sab (sabotage), ctl (contrôle)
  --classic           partie classique (réapparition, pas d'avion commun) au lieu du battle royale (par défaut)
  --zones P           zones du battle royale : apex (par défaut), fortnite ou un fichier .json (voir --dump-zones)
  --dump-zones P      affiche en JSON le réglage de zones P (apex, fortnite ou fichier), à modifier puis passer à --zones
  --min-players N     humains nécessaires pour lancer la partie (1)
  --lobby-countdown S compte à rebours avant le départ, en secondes (5)
  --down-seconds S    durée avant de mourir quand on est à terre (60)
  --revive-seconds S  durée d'une réanimation (5)
  --revived-health N  vie d'un joueur relevé (50)
  --end-linger S      délai après la fin de la partie avant de renvoyer les joueurs au menu (15)
  --exit-when-over    le serveur s'arrête une fois la partie terminée (serveur lancé par le matchmaking)
  --idle-exit S       le serveur s'arrête si la salle d'attente reste vide S secondes (0 = jamais)
  --loadout           donne un fusil et des munitions à chaque joueur (essais de charge)
  --no-lag-comp       désactive la compensation de latence (comparaisons)
  --world FICHIER     carte exportée depuis Godot (sinon carte procédurale)
  --balance FICHIER   réglages de jeu : vitesses, armes, balles… (balance.json par défaut, créé s'il manque)
  --write-balance F   écrit un fichier de réglages avec les valeurs par défaut, puis quitte
  --procedural M      taille de la carte procédurale en mètres (2000)
  --spawn-radius M    tous les joueurs apparaissent dans ce rayon (essais de charge)
  --stats S           intervalle des statistiques en secondes (5)");
		}
	}
}
