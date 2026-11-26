using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenEsport.Core;
using OpenEsport.Matchmaker;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Lanceur de test : au lieu d'un conteneur Docker, un serveur de partie dans le processus de test (mêmes réglages).</summary>
	internal sealed class InProcessLauncher : IServerLauncher
	{
		public readonly List<ServerSpec> Launched = new List<ServerSpec>();
		public readonly List<string> Stopped = new List<string>();
		private readonly Dictionary<string, CancellationTokenSource> _running = new Dictionary<string, CancellationTokenSource>();
		private readonly WorldData _world = WorldData.CreateFlat(300);

		public void Launch(ServerSpec s)
		{
			Launched.Add(s);
			var opt = new ServerOptions
			{
				Port = s.Port, StatsIntervalSeconds = 3600, Royale = s.Mode == OpenEsport.Core.GameMode.BattleRoyale, Mode = s.Mode, FillBots = false, MinPlayers = s.MinPlayers, LobbyCountdown = s.LobbyCountdown,
				IdleExitSeconds = s.IdleExitSeconds, ExitWhenOver = true
			};
			opt.Match.TeamSize = s.TeamSize; opt.Match.MaxPlayers = s.Players;
			if (_world.SpawnPoints.Count == 0) _world.SpawnPoints.Add(new Vec3(0, _world.HeightAt(0, 0), 0));
			var server = new GameServer(opt, _world);
			var cts = new CancellationTokenSource();
			lock (_running) _running[s.Name] = cts;
			Task.Run(() => server.Run(cts.Token));
		}

		public void Stop(string name)
		{
			Stopped.Add(name);
			lock (_running) { if (_running.TryGetValue(name, out var c)) { c.Cancel(); _running.Remove(name); } }
		}

		public void CleanupLeftovers() { }
	}

	public class MatchmakerTests
	{
		private static int _port = 32100;

		private static (MatchmakerService svc, InProcessLauncher launcher, CancellationTokenSource cts, int port) Start(Action<MatchmakerOptions> cfg = null)
		{
			int basePort = Interlocked.Add(ref _port, 20);
			var opt = new MatchmakerOptions { Port = basePort, PortFirst = basePort + 1, PortLast = basePort + 9, Players = 8, MinPlayers = 2, LobbyCountdown = 0.3f, IdleExitSeconds = 0 };
			cfg?.Invoke(opt);
			var launcher = new InProcessLauncher();
			var svc = new MatchmakerService(opt, launcher) { Log = _ => { } };
			var cts = new CancellationTokenSource();
			Task.Run(() => svc.Run(cts.Token));
			Thread.Sleep(200);
			return (svc, launcher, cts, basePort);
		}

		private static MatchmakerClient Ask(int mmPort, int teamSize, double seconds = 20)
		{
			var c = new MatchmakerClient();
			c.Start("127.0.0.1", mmPort, teamSize);
			var sw = Stopwatch.StartNew();
			while (c.State == MatchmakingState.Searching && sw.Elapsed.TotalSeconds < seconds) { c.Poll(); Thread.Sleep(20); }
			return c;
		}

		[Fact]
		public void A_Request_Launches_A_Named_Server_Then_Gives_Its_Address_And_Players_Share_It()
		{
			var (svc, launcher, cts, mm) = Start();
			try
			{
				using var a = Ask(mm, 2);
				Assert.Equal(MatchmakingState.Found, a.State);
				Assert.Single(launcher.Launched);
				var spec = launcher.Launched[0];
				Assert.Equal($"open-esport-duo-{spec.Port}", spec.Name);    // un nom explicite, jamais anonyme
				Assert.Equal(2, spec.TeamSize);
				Assert.Equal(spec.Port, a.ServerPort);

				using var b = Ask(mm, 2);                                    // même mode : même salle d'attente
				Assert.Equal(MatchmakingState.Found, b.State);
				Assert.Equal(a.ServerPort, b.ServerPort);
				Assert.Single(launcher.Launched);

				using var c = Ask(mm, 1);                                    // autre mode : un autre serveur
				Assert.Equal(MatchmakingState.Found, c.State);
				Assert.NotEqual(a.ServerPort, c.ServerPort);
				Assert.Equal($"open-esport-solo-{c.ServerPort}", launcher.Launched[1].Name);
			}
			finally { cts.Cancel(); svc.StopAll(); }
		}

		[Fact]
		public void One_Address_Cannot_Launch_More_Than_Its_Quota_Of_Servers_Per_Minute()
		{
			var (svc, launcher, cts, mm) = Start();
			try
			{
				svc.LaunchesPerMinute = 2;
				string v = Protocol.Version.ToString();
				var attacker = System.Net.IPAddress.Parse("203.0.113.7");
				Assert.StartsWith("MM!WAIT", svc.HandleRequest($"MM?|{v}|1|dm", attacker));
				Assert.StartsWith("MM!WAIT", svc.HandleRequest($"MM?|{v}|4|sab", attacker));
				Assert.StartsWith("MM!ERR", svc.HandleRequest($"MM?|{v}|2|tdm", attacker));          // quota atteint : pas de nouveau serveur
				Assert.StartsWith("MM!ERR", svc.HandleRequest($"MM?|{v}|1|1v1|1|TD5:m1", attacker));
				Assert.Equal(2, launcher.Launched.Count);
				Assert.StartsWith("MM!WAIT", svc.HandleRequest($"MM?|{v}|1|dm", attacker));           // une salle déjà lancée se rejoint toujours
				Assert.StartsWith("MM!WAIT", svc.HandleRequest($"MM?|{v}|2|tdm", System.Net.IPAddress.Parse("198.51.100.9")));      // une autre adresse n'est pas touchée
				Assert.Equal(3, launcher.Launched.Count);
			}
			finally { cts.Cancel(); svc.StopAll(); }
		}

		[Fact]
		public void Each_Mode_Gets_Its_Own_Named_Server_With_Its_Own_Team_Size_And_Player_Count()
		{
			var (svc, launcher, cts, mm) = Start();
			try
			{
				string v = Protocol.Version.ToString();
				Assert.StartsWith("MM!WAIT", svc.HandleRequest($"MM?|{v}|3|ctl"));       // 3 n'existe pas en Contrôle : 5 contre 5
				svc.HandleRequest($"MM?|{v}|1|dm");
				svc.HandleRequest($"MM?|{v}|4|sab");
				svc.HandleRequest($"MM?|{v}|2|tdm");
				Assert.Contains(launcher.Launched, s => s.Mode == GameMode.Control && s.TeamSize == 5 && s.Players == 10 && s.Name.StartsWith("open-esport-ctl-5-"));
				Assert.Contains(launcher.Launched, s => s.Mode == GameMode.Deathmatch && s.TeamSize == 1 && s.Name.StartsWith("open-esport-dm-"));
				Assert.Contains(launcher.Launched, s => s.Mode == GameMode.Sabotage && s.TeamSize == 4 && s.Players == 8);
				Assert.Contains(launcher.Launched, s => s.Mode == GameMode.TeamDeathmatch && s.TeamSize == 2 && s.Players == 4);
				Assert.StartsWith("MM!ERR", svc.HandleRequest($"MM?|{v}|2|inconnu"));
			}
			finally { cts.Cancel(); svc.StopAll(); }
		}

		[Fact]
		public void The_Address_Leads_To_A_Waiting_Room_That_Starts_When_Enough_Players_Join()
		{
			var (svc, launcher, cts, mm) = Start();
			try
			{
				using var m1 = Ask(mm, 1);
				Assert.Equal(MatchmakingState.Found, m1.State);
				var g1 = new GameClient(new StaticWorld(WorldData.CreateFlat(300)), WorldData.CreateFlat(300).Checksum());
				g1.Connect(m1.Host, m1.ServerPort, "A", null);
				var sw = Stopwatch.StartNew();
				while (g1.State != NetState.Playing && sw.Elapsed.TotalSeconds < 5) { g1.Poll(); Thread.Sleep(5); }
				Assert.Equal(NetState.Playing, g1.State);
				sw.Restart();
				while (g1.Match.Phase == MatchPhase.Lobby && sw.Elapsed.TotalSeconds < 1.0) { g1.Poll(); g1.Tick(0, 0, Buttons.None, 0, 0, 0); Thread.Sleep(16); }
				Assert.Equal(MatchPhase.Lobby, g1.Match.Phase);         // 1 joueur sur 2 requis : on attend
				Assert.Equal(2, g1.Match.Needed);
			}
			finally { cts.Cancel(); svc.StopAll(); }
		}

		[Fact]
		public void A_Server_That_Never_Answers_Is_Removed_And_Stopped()
		{
			var (svc, launcher, cts, mm) = Start(o => o.StartTimeoutSeconds = 1.5);
			try
			{
				launcher.Launched.Clear();
				// le lanceur de test démarre vraiment le serveur ; on simule la panne en le stoppant dès son lancement
				using var a = new MatchmakerClient { TimeoutSeconds = 0.5 };
				a.Start("127.0.0.1", mm, 3);
				var sw = Stopwatch.StartNew();
				while (sw.Elapsed.TotalSeconds < 0.4) { a.Poll(); Thread.Sleep(20); }
				Assert.NotEmpty(launcher.Launched);
				launcher.Stop(launcher.Launched[0].Name);
				Assert.True(SpinWait.SpinUntil(() => svc.ServerCount == 0, 15000), "le serveur injoignable doit être retiré");
				Assert.Contains(launcher.Launched[0].Name, launcher.Stopped);
			}
			finally { cts.Cancel(); svc.StopAll(); }
		}

		[Fact]
		public void The_Docker_Launcher_Names_Its_Containers_And_Publishes_The_Udp_Port()
		{
			var l = new DockerLauncher { WorldFile = System.IO.Path.GetFullPath("frontiere01.world"), Image = "open-esport-server:latest" };
			var args = l.BuildRunArgs(new ServerSpec { Name = "open-esport-trio-27105", Port = 27105, TeamSize = 3, Players = 30, MinPlayers = 1, LobbyCountdown = 5, IdleExitSeconds = 120, Zones = "apex" });
			Assert.Equal("open-esport-trio-27105", args[args.IndexOf("--name") + 1]);      // un nom explicite, jamais anonyme
			Assert.Contains("27105:27105/udp", args);
			Assert.Contains("open-esport=1", args);
			Assert.DoesNotContain("--rm", args);
			Assert.Equal("ALL", args[args.IndexOf("--cap-drop") + 1]); Assert.Contains("--read-only", args); Assert.Contains("no-new-privileges", args);      // conteneur durci      // le conteneur reste jusqu'à ce que le matchmaking le retire : on peut lire ses logs s'il plante
			var lNet = new DockerLauncher { Image = "open-esport-server:latest", Network = "open-esport" };
			var netArgs = lNet.BuildRunArgs(new ServerSpec { Name = "open-esport-trio-27105", Port = 27105, TeamSize = 3, Players = 30, MinPlayers = 1, LobbyCountdown = 5, IdleExitSeconds = 120 });
			Assert.Equal("open-esport", netArgs[netArgs.IndexOf("--network") + 1]);
			Assert.Equal("br", args[args.IndexOf("--mode") + 1]); Assert.Contains("--exit-when-over", args);
			Assert.Contains("/data/frontiere01.world", args);
			// un autre mode : sa carte, son nom
			var ctl = l.BuildRunArgs(new ServerSpec { Name = "open-esport-ctl-8-27107", Port = 27107, Mode = OpenEsport.Core.GameMode.Control, TeamSize = 8, Players = 16, MinPlayers = 1, LobbyCountdown = 5, IdleExitSeconds = 120 });
			Assert.Equal("ctl", ctl[ctl.IndexOf("--mode") + 1]);
			Assert.Contains("/data/controle.world", ctl);
			Assert.Contains("open-esport.mode=ctl-8", ctl);
			Assert.Equal("3", args[args.IndexOf("--team-size") + 1]);
			Assert.Equal("apex", args[args.IndexOf("--zones") + 1]);       // préréglage par défaut
			var l2 = new DockerLauncher { ZonesFile = System.IO.Path.GetFullPath("mes_zones.json") };
			var a2 = l2.BuildRunArgs(new ServerSpec { Name = "open-esport-duo-27106", Port = 27106, TeamSize = 2, Players = 30, MinPlayers = 1, LobbyCountdown = 5, IdleExitSeconds = 120, Zones = "apex" });
			Assert.Equal("/app/zones.json", a2[a2.IndexOf("--zones") + 1]);       // un fichier du panneau Godot est monté dans le conteneur
			Assert.Contains(a2, x => x.EndsWith(":/app/zones.json:ro"));
			Assert.True(args.IndexOf("open-esport-server:latest") < args.IndexOf("--port"), "les options du serveur viennent après l'image");
		}

		[Fact]
		public void Servers_Report_Telemetry_And_The_Admin_Api_Lists_Them_And_Can_Stop_Them()
		{
			var (svc, launcher, cts, mm) = Start();
			try
			{
				using var a = Ask(mm, 1);
				Assert.Equal(MatchmakingState.Found, a.State);
				Assert.True(SpinWait.SpinUntil(() => svc.Servers.Any(s => s.TelemetryJson != null), 8000), "le serveur doit envoyer sa télémétrie");

				var admin = new AdminApi(svc, token: "secret");
				using var doc = System.Text.Json.JsonDocument.Parse(admin.ServersJson());
				var root = doc.RootElement;
				Assert.True(root.GetProperty("canStop").GetBoolean());
				Assert.Equal(1, root.GetProperty("totals").GetProperty("servers").GetInt32());
				var srv = root.GetProperty("servers")[0];
				Assert.Equal(launcher.Launched[0].Name, srv.GetProperty("name").GetString());
				Assert.Equal("solo", srv.GetProperty("mode").GetString());
				Assert.Equal(0, srv.GetProperty("telemetry").GetProperty("phase").GetInt32());     // salle d'attente
				Assert.Equal(2, srv.GetProperty("telemetry").GetProperty("minPlayers").GetInt32());

				// arrêt : refusé sans jeton valide, accepté avec
				Assert.Equal(401, admin.StopRequest(launcher.Launched[0].Name, "mauvais").code);
				Assert.Equal(404, admin.StopRequest("open-esport-inconnu", "secret").code);
				Assert.Equal(1, svc.ServerCount);
				Assert.Equal(200, admin.StopRequest(launcher.Launched[0].Name, "secret").code);
				Assert.Equal(0, svc.ServerCount);
				Assert.Contains(launcher.Launched[0].Name, launcher.Stopped);
				// sans jeton configuré, l'arrêt est désactivé
				Assert.Equal(403, new AdminApi(svc, token: null).StopRequest("x", "secret").code);
			}
			finally { cts.Cancel(); svc.StopAll(); }
		}
	}
}
