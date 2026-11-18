using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenEsport.Core;

namespace OpenEsport.Matchmaker
{
	/// <summary>Arguments de la ligne de commande du serveur de partie, communs aux deux lanceurs.</summary>
	internal static class ServerArgs
	{
		public static List<string> For(ServerSpec s, string worldDir, bool proceduralWorld)
		{
			var a = new List<string>
			{
				"--port", s.Port.ToString(CultureInfo.InvariantCulture),
				"--team-size", s.TeamSize.ToString(CultureInfo.InvariantCulture),
				"--players", s.Players.ToString(CultureInfo.InvariantCulture),
				"--mode", Modes.Get(s.Mode).Key,
				"--min-players", s.MinPlayers.ToString(CultureInfo.InvariantCulture),
				"--lobby-countdown", s.LobbyCountdown.ToString(CultureInfo.InvariantCulture),
				"--idle-exit", s.IdleExitSeconds.ToString(CultureInfo.InvariantCulture),
				"--exit-when-over",
				"--stats", "3600",
			};
			if (string.IsNullOrEmpty(s.TournamentMatch)) a.Add("--fill-bots");        // un duel de tournoi se joue sans bots
			if (!string.IsNullOrEmpty(s.Zones)) { a.Add("--zones"); a.Add(s.Zones); }
			if (s.Tournament > 0) { a.Add("--tournament"); a.Add(s.Tournament.ToString(CultureInfo.InvariantCulture)); a.Add("--race"); a.Add(s.TournamentRace.ToString(CultureInfo.InvariantCulture)); a.Add("--lobby"); a.Add(s.TournamentLobby.ToString(CultureInfo.InvariantCulture)); if (!string.IsNullOrEmpty(s.TournamentMatch)) { a.Add("--match"); a.Add(s.TournamentMatch); } }
			if (!proceduralWorld) { a.Add("--world"); a.Add(worldDir.TrimEnd('/', (char)92) + "/" + Modes.Get(s.Mode).MapFile); }
			return a;
		}
	}

	/// <summary>
	/// Lance chaque serveur de partie dans son propre conteneur Docker, avec un nom explicite (<c>open-esport-duo-27101</c>…) et des étiquettes
	/// pour retrouver les conteneurs du jeu. Le port UDP du serveur est publié sur la machine hôte, au même numéro.
	/// </summary>
	public sealed class DockerLauncher : IServerLauncher
	{
		public string Image = "open-esport-server:latest";
		/// <summary>Carte du jeu (fichier .world sur la machine hôte) : son dossier est monté en lecture seule dans le conteneur.</summary>
		public string WorldFile;
		/// <summary>Réglages du jeu (balance.json) à monter dans le conteneur, ou vide pour les valeurs par défaut.</summary>
		public string BalanceFile;
		/// <summary>Réglage de zones (fichier .json sur la machine hôte) monté dans le conteneur ; sinon le préréglage nommé dans <see cref="ServerSpec.Zones"/>.</summary>
		public string ZonesFile;
		public string Cpus = "2", Memory = "1g";
		/// <summary>Réseau Docker à rejoindre (voir <see cref="MatchmakerOptions.Network"/>) ; vide = réseau par défaut.</summary>
		public string Network;
		/// <summary>Service de comptes transmis aux serveurs de partie (adresse en clair ; la clé passe par la variable d'environnement OE_ACCOUNTS_KEY, sans apparaître dans une ligne de commande).</summary>
		public string AccountsUrl;
		public string DockerCommand = "docker";
		public Action<string> Log = s => Console.WriteLine(s);

		/// <summary>Arguments de « docker run » pour un serveur (public pour pouvoir les vérifier sans Docker).</summary>
		public List<string> BuildRunArgs(ServerSpec s)
		{
			var args = new List<string> { "run", "-d", "--name", s.Name,       // sans --rm : un serveur qui plante garde ses logs et son code de sortie jusqu'à ce que le matchmaking le retire (voir Diagnose)
				"--label", "open-esport=1", "--label", "open-esport.mode=" + Modes.Name(s.Mode, s.TeamSize), "--label", "open-esport.port=" + s.Port,
				"--cpus", Cpus, "--memory", Memory, "--restart", "no",
				"-p", $"{s.Port}:{s.Port}/udp" };
			if (!string.IsNullOrEmpty(Network)) { args.Add("--network"); args.Add(Network); }
			if (!string.IsNullOrEmpty(AccountsUrl))
			{
				args.Add("-e"); args.Add("OE_ACCOUNTS_URL=" + AccountsUrl);
				if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OE_ACCOUNTS_KEY"))) { args.Add("-e"); args.Add("OE_ACCOUNTS_KEY"); }      // sans valeur : Docker la prend dans l'environnement du matchmaking
			}
			string world = null;
			if (!string.IsNullOrEmpty(WorldFile))
			{
				string dir = Path.GetDirectoryName(Path.GetFullPath(WorldFile));
				args.Add("-v"); args.Add($"{dir}:/data:ro");
				world = "/data";            // le dossier des cartes : le serveur y prend celle de son mode
			}
			if (!string.IsNullOrEmpty(BalanceFile)) { args.Add("-v"); args.Add($"{Path.GetFullPath(BalanceFile)}:/app/balance.json:ro"); }
			var spec = s;
			if (!string.IsNullOrEmpty(ZonesFile))
			{
				args.Add("-v"); args.Add($"{Path.GetFullPath(ZonesFile)}:/app/zones.json:ro");
				spec = new ServerSpec { Name = s.Name, Port = s.Port, Mode = s.Mode, TeamSize = s.TeamSize, Players = s.Players, MinPlayers = s.MinPlayers, LobbyCountdown = s.LobbyCountdown, IdleExitSeconds = s.IdleExitSeconds, Zones = "/app/zones.json" };
			}
			args.Add(Image);
			args.AddRange(ServerArgs.For(spec, world, proceduralWorld: world == null));
			return args;
		}

		public void Launch(ServerSpec s)
		{
			var (code, output) = Run(BuildRunArgs(s));
			if (code != 0) throw new InvalidOperationException("docker run a échoué : " + output.Trim());
			Log($"Conteneur {s.Name} démarré ({output.Trim().Substring(0, Math.Min(12, output.Trim().Length))})");
		}

		public void Stop(string name) { Run(new List<string> { "rm", "-f", name }); }

		/// <summary>État du conteneur et fin de ses logs : pour comprendre pourquoi un serveur n'a pas démarré ou s'est arrêté.</summary>
		public string Diagnose(string name)
		{
			var (c1, state) = Run(new List<string> { "inspect", "-f", "{{.State.Status}} code={{.State.ExitCode}} oom={{.State.OOMKilled}}", name });
			if (c1 != 0) return "conteneur introuvable";
			var (_, logs) = Run(new List<string> { "logs", "--tail", "15", name });
			return state.Trim() + (string.IsNullOrWhiteSpace(logs) ? "  (aucun log)" : Environment.NewLine + logs.TrimEnd());
		}

		public void CleanupLeftovers()
		{
			var (code, ids) = Run(new List<string> { "ps", "-aq", "--filter", "label=open-esport=1" });
			if (code != 0) return;
			var list = ids.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
			if (list.Count == 0) return;
			var args = new List<string> { "rm", "-f" }; args.AddRange(list);
			Run(args);
			Log($"{list.Count} conteneur(s) d'une exécution précédente supprimé(s)");
		}

		/// <summary>Construit (ou met à jour) l'image du serveur à chaque démarrage (contexte : le dossier netcode).</summary>
		public void EnsureImage(string netcodeDir)
		{
			// toujours « docker build » : le cache de Docker rend l'opération instantanée si le code n'a pas changé, et l'image suit les modifications
			Log($"Image {Image} : construction / mise à jour (rapide si rien n'a changé, quelques minutes sinon)…");
			var (c2, o2) = Run(new List<string> { "build", "-t", Image, "-f", Path.Combine(netcodeDir, "Dockerfile"), netcodeDir });
			if (c2 != 0) throw new InvalidOperationException("docker build a échoué : " + o2);
		}

		private static string Matchmaking_ModeName(int teamSize) => OpenEsport.Core.Matchmaking.ModeName(teamSize);

		private (int code, string output) Run(List<string> args)
		{
			var psi = new ProcessStartInfo(DockerCommand) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
			foreach (var a in args) psi.ArgumentList.Add(a);
			using var p = Process.Start(psi);
			string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
			p.WaitForExit();
			return (p.ExitCode, o);
		}
	}

	/// <summary>Lanceur de développement, sans Docker : un processus .NET par serveur de partie.</summary>
	public sealed class ProcessLauncher : IServerLauncher
	{
		/// <summary>OpenEsport.Server.dll (lancé avec « dotnet »).</summary>
		public string ServerDll;
		public string WorldFile;
		public string ZonesFile;
		public string AccountsUrl;
		public Action<string> Log = s => Console.WriteLine(s);
		private readonly Dictionary<string, Process> _procs = new Dictionary<string, Process>();

		public void Launch(ServerSpec s)
		{
			var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(ServerDll)) };
			psi.ArgumentList.Add(ServerDll);
			if (!string.IsNullOrEmpty(AccountsUrl)) psi.Environment["OE_ACCOUNTS_URL"] = AccountsUrl;      // la clé (OE_ACCOUNTS_KEY) est héritée de l'environnement
			if (!string.IsNullOrEmpty(ZonesFile)) s.Zones = Path.GetFullPath(ZonesFile);
			foreach (var a in ServerArgs.For(s, WorldFile == null ? null : Path.GetDirectoryName(Path.GetFullPath(WorldFile)), WorldFile == null)) psi.ArgumentList.Add(a);
			var p = Process.Start(psi);
			lock (_procs) _procs[s.Name] = p;
		}

		public void Stop(string name)
		{
			Process p;
			lock (_procs) { if (!_procs.TryGetValue(name, out p)) return; _procs.Remove(name); }
			try { if (!p.HasExited) p.Kill(true); } catch { }
		}

		public void CleanupLeftovers() { }
	}
}
