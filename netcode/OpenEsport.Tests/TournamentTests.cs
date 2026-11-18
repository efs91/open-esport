using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenEsport.Accounts;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Un ManageComp de poche : même forme de réponses que l'API réelle pour ce que le jeu utilise (aucun réseau).</summary>
	internal sealed class FakeManageComp : HttpMessageHandler
	{
		public readonly List<string> Calls = new List<string>();
		private int _players;
		public readonly Dictionary<int, int?[]> Finishes = new Dictionary<int, int?[]>();
		public int Races = 1;
		public bool Down;

		private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new StringContent(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json") };

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
		{
			if (Down) throw new HttpRequestException("hors ligne");
			string path = req.RequestUri.AbsolutePath, body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
			Calls.Add(req.Method + " " + path);
			Assert.Equal("clef-test", req.Headers.GetValues("X-Api-Key").Single());
			if (path == "/api/contests" && req.Method == HttpMethod.Post) return Json(new { id = "c1" }, HttpStatusCode.Created);
			if (path.EndsWith("/schedule/stages") && req.Method == HttpMethod.Post) return Json(new { stages = new[] { new { index = 0, kind = "checkin" }, new { index = 1, kind = "lobby" } } });
			if (path.EndsWith("/schedule") ) return Json(new { stages = new[] { new { index = 0, kind = "checkin" }, new { index = 1, kind = "lobby" } } });
			if (path.EndsWith("/checkin/fencers") && req.Method == HttpMethod.Post)
			{
				int r = ++_players; Finishes[r] = new int?[Races];
				return Json(new { fencers = Enumerable.Range(1, _players).Select(i => new { reference = i }) }, HttpStatusCode.Created);
			}
			if (path.Contains("/checkin/fencers/") && req.Method == HttpMethod.Delete) { Finishes.Remove(int.Parse(path.Split('/').Last())); return Json(new { }); }
			if (path.EndsWith("/stages/1") && req.Method == HttpMethod.Post)
			{
				var d = JsonDocument.Parse(body).RootElement;
				if (d.GetProperty("action").GetString() == "finish") { Finishes[d.GetProperty("player").GetInt32()][d.GetProperty("race").GetInt32()] = int.TryParse(d.GetProperty("value").GetString(), out int p) ? p : -1; }
				return Json(new { });
			}
			if (path.EndsWith("/stages/1") && req.Method == HttpMethod.Get)
				return Json(new { data = new { lobbies = new[] { new { index = 0, rows = Finishes.Select(f => new { reference = f.Key, finishes = f.Value }) } }, ranking = Finishes.Select(f => new { reference = f.Key }) } });
			return Json(new { });
		}
	}

	public class TournamentTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe-tour-" + Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { try { File.Delete(_db); } catch { } }

		private (TournamentService svc, AccountStore store, FakeManageComp fake, long owner, long a, long b) Setup(string key = "clef-test", int races = 2)
		{
			var store = new AccountStore(_db);
			var fake = new FakeManageComp { Races = races };
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(fake), "http://fake", key));
			long Make(string l) => store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			return (svc, store, fake, Make("orga"), Make("alice"), Make("bob"));
		}

		private static long Soon => DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();

		[Fact]
		public async Task Cycle_complet_inscription_lancement_resultats()
		{
			var (svc, store, fake, owner, a, b) = Setup(races: 2);
			var created = await svc.Create(owner, "Open de test", Soon, 2, 20);
			Assert.Null(created.Error);
			Assert.Null((await svc.Join(created.Id, a, "alice")).Error);
			Assert.Null((await svc.Join(created.Id, b, "bob")).Error);
			Assert.Null((await svc.Join(created.Id, b, "bob")).Error);               // idempotent
			Assert.Equal(2, store.TournamentPlayers(created.Id).Count);
			Assert.NotNull((await svc.Start(created.Id, a)).Error);                   // seul l'organisateur lance
			Assert.Null((await svc.Start(created.Id, owner)).Error);
			Assert.NotNull((await svc.Join(created.Id, owner, "orga")).Error);       // inscriptions closes

			Assert.NotNull((await svc.ReportRace(created.Id, 1, 0, new[] { (a, 1) })).Error);   // pas la manche en cours
			Assert.Null((await svc.ReportRace(created.Id, 0, 0, new[] { (a, 1), (b, 2) })).Error);
			Assert.Equal("running", store.Tournament(created.Id).State);
			Assert.Equal(1, store.Tournament(created.Id).Race);
			Assert.Null((await svc.ReportRace(created.Id, 1, 0, new[] { (a, 2), (b, 1) })).Error);
			Assert.Equal("done", store.Tournament(created.Id).State);
			Assert.Equal(new int?[] { 1, 2 }, fake.Finishes[1]);
			Assert.Equal(new int?[] { 2, 1 }, fake.Finishes[2]);
		}

		[Fact]
		public async Task Regles_de_creation()
		{
			var (svc, _, _, owner, _, _) = Setup();
			Assert.NotNull((await svc.Create(owner, "ab", Soon, 2, 20)).Error);
			Assert.NotNull((await svc.Create(owner, "Nom valable", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), 2, 20)).Error);
			Assert.NotNull((await svc.Create(owner, "Nom valable", DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds(), 2, 20)).Error);
			Assert.NotNull((await svc.Create(owner, "Nom valable", Soon, 99, 20)).Error);
			for (int i = 0; i < TournamentService.MaxOpenPerOwner; i++) Assert.Null((await svc.Create(owner, "Tournoi " + i, Soon, 1, 20)).Error);
			Assert.NotNull((await svc.Create(owner, "Un de trop", Soon, 1, 20)).Error);
		}

		[Fact]
		public async Task Sans_cle_ou_manageComp_hors_ligne_une_erreur_claire_sans_creer_de_tournoi()
		{
			var (svc, store, fake, owner, _, _) = Setup(key: "");
			Assert.Contains("pas encore ouverts", (await svc.Create(owner, "Open de test", Soon, 2, 20)).Error);
			var (svc2, store2, fake2, owner2, _, _) = Setup();
			fake2.Down = true;
			Assert.Contains("injoignable", (await svc2.Create(owner2, "Open de test", Soon, 2, 20)).Error);
			Assert.Empty(store2.Tournaments());
		}

		[Fact]
		public async Task Se_desinscrire_avant_le_depart_retire_le_joueur()
		{
			var (svc, store, _, owner, a, _) = Setup();
			var t = await svc.Create(owner, "Open de test", Soon, 1, 20);
			await svc.Join(t.Id, a, "alice");
			Assert.Null((await svc.Leave(t.Id, a)).Error);
			Assert.Empty(store.TournamentPlayers(t.Id));
		}
	}
}

namespace OpenEsport.Tests
{
	public class TournamentMatchmakingTests
	{
		private sealed class StubLauncher : OpenEsport.Matchmaker.IServerLauncher
		{
			public readonly List<OpenEsport.Matchmaker.ServerSpec> Launched = new List<OpenEsport.Matchmaker.ServerSpec>();
			public void Launch(OpenEsport.Matchmaker.ServerSpec spec) => Launched.Add(spec);
			public void Stop(string name) { }
			public void CleanupLeftovers() { }
		}

		[Fact]
		public void La_manche_d_un_tournoi_lance_un_serveur_reserve_une_seule_fois_par_lobby()
		{
			var launcher = new StubLauncher();
			var mm = new OpenEsport.Matchmaker.MatchmakerService(new OpenEsport.Matchmaker.MatchmakerOptions { Port = 0 }, launcher);
			string req(string tag) => $"MM?|{OpenEsport.Core.Protocol.Version}|1|br|1|T{tag}";
			Assert.StartsWith("MM!WAIT", mm.HandleRequest(req("7:0:0")));
			Assert.StartsWith("MM!WAIT", mm.HandleRequest(req("7:0:0")));            // même lobby : pas un deuxième serveur
			Assert.Single(launcher.Launched);
			Assert.Equal((7L, 0, 0), (launcher.Launched[0].Tournament, launcher.Launched[0].TournamentRace, launcher.Launched[0].TournamentLobby));
			Assert.StartsWith("MM!WAIT", mm.HandleRequest(req("7:0:1")));            // autre lobby : un autre serveur
			Assert.Equal(2, launcher.Launched.Count);
			Assert.StartsWith("MM!ERR", mm.HandleRequest(req("x:y")));
		}

		[Fact]
		public void Les_arguments_du_serveur_portent_le_tournoi()
		{
			var spec = new OpenEsport.Matchmaker.ServerSpec { Name = "n", Port = 27100, TeamSize = 1, Players = 30, Tournament = 7, TournamentRace = 2, TournamentLobby = 1, Mode = OpenEsport.Core.GameMode.BattleRoyale };
			var args = new OpenEsport.Matchmaker.DockerLauncher().BuildRunArgs(spec);
			string joined = string.Join(" ", args);
			Assert.Contains("--tournament 7 --race 2 --lobby 1", joined);
		}
	}
}

namespace OpenEsport.Tests
{
	/// <summary>ManageComp de poche pour un duel entre deux participants : une poule d'un match, puis une finale, puis le classement.</summary>
	internal sealed class FakeDuelManageComp : HttpMessageHandler
	{
		private int _players, _current = 0;                     // phases : 0 appel, 1 composition, 2 poules, 3 tableau, 4 classement
		private readonly Dictionary<int, string> _poolScores = new Dictionary<int, string>(), _tableScores = new Dictionary<int, string>();
		public readonly List<string> Scored = new List<string>();
		private static readonly string[] Kinds = { "checkin", "pool_allocator", "pool_supervisor", "table", "classification" };

		private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new StringContent(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json") };
		private object Schedule() => new { current = _current, stages = Kinds.Select((k, i) => new { index = i, kind = k }) };

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
		{
			string path = req.RequestUri.AbsolutePath, body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
			if (path == "/api/contests" && req.Method == HttpMethod.Post) return Json(new { id = "d1" }, HttpStatusCode.Created);
			if (path.EndsWith("/checkin/fencers") && req.Method == HttpMethod.Post) { _players++; return Json(new { fencers = Enumerable.Range(1, _players).Select(i => new { reference = i }) }, HttpStatusCode.Created); }
			if (path.EndsWith("/schedule")) return Json(Schedule());
			if (path.EndsWith("/next"))
			{
				bool done = _current switch { 2 => _poolScores.Count == 2, 3 => _tableScores.Count == 2, _ => true };
				if (!done) return Json(new { error = "la phase en cours n'est pas terminée" }, HttpStatusCode.BadRequest);
				_current++;
				return Json(Schedule());
			}
			if (path.EndsWith("/stages/2") && req.Method == HttpMethod.Get)
				return Json(new { data = new { pools = new[] { new { index = 0, matches = new[] { new { index = 0, opponents = new[] { new { reference = 1, name = "alice", score = _poolScores.GetValueOrDefault(1, "") }, new { reference = 2, name = "bob", score = _poolScores.GetValueOrDefault(2, "") } } } } } } } });
			if (path.EndsWith("/stages/3") && req.Method == HttpMethod.Get)
				return Json(new { data = new { displayed = 0, drawing = new { cells = new object[] {
					new { name = "alice", score = new { node = 2, fencer = 1, text = _tableScores.GetValueOrDefault(1, "") } },
					new { name = "bob", score = new { node = 2, fencer = 2, text = _tableScores.GetValueOrDefault(2, "") } } } } } });
			if (req.Method == HttpMethod.Post && path.Contains("/stages/"))
			{
				var d = JsonDocument.Parse(body).RootElement;
				if (d.GetProperty("action").GetString() == "score")
				{
					int f = d.GetProperty("fencer").GetInt32(); string v = d.GetProperty("value").GetString();
					Scored.Add($"{(path.EndsWith("/2") ? "pool" : "table")}:{f}={v}");
					(path.EndsWith("/2") ? _poolScores : _tableScores)[f] = v;
				}
				return Json(new { });
			}
			return Json(new { });
		}
	}

	public class DuelTournamentTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe-duel-" + Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { try { File.Delete(_db); } catch { } }
		private static long Soon => DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();

		[Fact]
		public async Task Duel_poule_puis_finale_puis_tournoi_termine()
		{
			var store = new AccountStore(_db);
			var fake = new FakeDuelManageComp();
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(fake), "http://fake", "clef"));
			long Make(string l) => store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			long orga = Make("orga"), a = Make("alice"), b = Make("bob");
			var t = await svc.Create(orga, "Duel de test", Soon, 0, 20, "duel", 1, 10);
			Assert.Null(t.Error);
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.Null((await svc.Join(t.Id, b, "bob")).Error);
			Assert.Null((await svc.Start(t.Id, orga)).Error);

			var mine = await svc.NextMatch(t.Id, a);
			Assert.Equal("p0m0", mine.Key); Assert.Equal(0, mine.Side); Assert.Equal("bob", mine.Opponent);
			var sides = await svc.MatchSides(t.Id, "p0m0");
			Assert.Equal(new[] { a }, sides.Value.a); Assert.Equal(new[] { b }, sides.Value.b); Assert.Equal(10, sides.Value.scoreLimit);

			Assert.NotNull((await svc.ReportMatch(t.Id, "p0m0", 7, 7)).Error);                  // égalité refusée : le match reste à jouer
			Assert.Null((await svc.ReportMatch(t.Id, "p0m0", 10, 4)).Error);                    // poule jouée → tableau
			Assert.Contains("pool:1=V10", fake.Scored); Assert.Contains("pool:2=4", fake.Scored);
			Assert.Equal("running", store.Tournament(t.Id).State);
			var final = await svc.NextMatch(t.Id, b);
			Assert.Equal("t0n2", final.Key);
			Assert.NotNull((await svc.ReportMatch(t.Id, "p0m0", 10, 1)).Error);                 // le match de poule est déjà joué
			Assert.Null((await svc.ReportMatch(t.Id, "t0n2", 3, 10)).Error);
			Assert.Contains("table:2=V10", fake.Scored);
			Assert.Equal("done", store.Tournament(t.Id).State);
		}

		[Fact]
		public async Task Duel_a_deux_l_equipe_est_le_groupe_de_deux_joueurs()
		{
			var store = new AccountStore(_db);
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(new FakeDuelManageComp()), "http://fake", "clef"));
			long Make(string l) => store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			long orga = Make("orga"), a = Make("alice"), b = Make("bob");
			var t = await svc.Create(orga, "Duel a deux", Soon, 0, 20, "duel", 2, 10);
			Assert.NotNull((await svc.Join(t.Id, a, "alice")).Error);                           // il faut un groupe de deux
			Assert.Null((await svc.Join(t.Id, a, "alice", new[] { (a, "alice"), (b, "bob") })).Error);
			Assert.Equal(2, store.TournamentPlayers(t.Id).Count);
			Assert.Single(store.TournamentPlayers(t.Id).Select(p => p.reference).Distinct());   // une seule équipe pour ManageComp
			Assert.Null((await svc.Leave(t.Id, b)).Error);
			Assert.Empty(store.TournamentPlayers(t.Id));                                         // toute l'équipe part
		}
	}
}

namespace OpenEsport.Tests
{
	public class TeamBattleRoyaleTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe-teambr-" + Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { try { File.Delete(_db); } catch { } }

		[Fact]
		public async Task Battle_royale_par_equipes_le_groupe_entier_joue_dans_le_meme_lobby()
		{
			var store = new AccountStore(_db);
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(new FakeManageComp { Races = 1 }), "http://fake", "clef-test"));
			long Make(string l) => store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			long orga = Make("orga"), a = Make("alice"), b = Make("bob"), c = Make("carol");
			var t = await svc.Create(orga, "BR en duo", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), 1, 20, "br", 2);
			Assert.Null(t.Error);
			Assert.NotNull((await svc.Join(t.Id, a, "alice")).Error);                                              // seul : refusé
			Assert.NotNull((await svc.Join(t.Id, a, "alice", new[] { (a, "alice"), (b, "bob"), (c, "carol") })).Error); // trois : refusé
			Assert.Null((await svc.Join(t.Id, a, "alice", new[] { (a, "alice"), (b, "bob") })).Error);
			Assert.Null((await svc.Start(t.Id, orga)).Error);
			var mineA = await svc.MyRace(t.Id, a); var mineB = await svc.MyRace(t.Id, b);
			Assert.NotNull(mineA); Assert.NotNull(mineB);
			Assert.Equal(JsonSerializer.Serialize(mineA), JsonSerializer.Serialize(mineB));                         // même manche, même lobby, équipes de 2
			Assert.Contains("\"teamSize\":2", JsonSerializer.Serialize(mineA));
			Assert.Null(await svc.MyRace(t.Id, c));                                                                // pas inscrit
		}
	}
}
