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
		private int _current;

		private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new StringContent(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json") };

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
		{
			if (Down) throw new HttpRequestException("hors ligne");
			string path = req.RequestUri.AbsolutePath, body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
			Calls.Add(req.Method + " " + path);
			Assert.Equal("clef-test", req.Headers.GetValues("X-Api-Key").Single());
			if (path == "/api/contests" && req.Method == HttpMethod.Post) return Json(new { id = "c1" }, HttpStatusCode.Created);
			if (path.EndsWith("/schedule/stages") && req.Method == HttpMethod.Post) return Json(new { current = _current, stages = new[] { new { index = 0, kind = "checkin" }, new { index = 1, kind = "lobby" } } });
			if (path.EndsWith("/schedule") ) return Json(new { current = _current, stages = new[] { new { index = 0, kind = "checkin" }, new { index = 1, kind = "lobby" } } });
			if (path.EndsWith("/next")) { if (_current >= 1) return Json(new { error = "dernière phase" }, HttpStatusCode.BadRequest); _current++; return Json(new { }); }
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
			Assert.NotNull((await svc.Create(owner, "Nom valable", DateTimeOffset.UtcNow.AddDays(400).ToUnixTimeSeconds(), 2, 20)).Error);
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

		private static TournamentConfig Config(long startAt, string startMode = "fixed", int checkIn = 0, int min = 1, int max = 100, long opensAt = 0) => new TournamentConfig
		{
			Title = "Open configuré", Format = "br", TeamSize = 1, StartAt = startAt, StartMode = startMode, CheckInMinutes = checkIn, MinEntries = min, MaxEntries = max, RegistrationOpensAt = opensAt,
			Phases = new List<PhaseConfig> { new PhaseConfig { Kind = "lobby", Races = 1, LobbySize = 20, Scale = new List<int> { 10, 6, 3 } } },
		};

		[Fact]
		public async Task Periode_d_inscription_et_maximum_d_inscrits()
		{
			var (svc, _, _, owner, a, b) = Setup();
			var later = await svc.Create(owner, Config(Soon + 86400, opensAt: Soon));
			Assert.Null(later.Error);
			Assert.Contains("pas encore ouvertes", (await svc.Join(later.Id, a, "alice")).Error);
			var full = await svc.Create(owner, Config(Soon, max: 2, min: 1));
			Assert.Null((await svc.Join(full.Id, a, "alice")).Error);
			Assert.Null((await svc.Join(full.Id, b, "bob")).Error);
			Assert.Contains("complet", (await svc.Join(full.Id, owner, "orga")).Error);
		}

		[Fact]
		public async Task Depart_automatique_check_in_et_annulation_faute_d_inscrits()
		{
			var (svc, store, _, owner, a, b) = Setup(races: 1);
			// check-in de 15 min, départ dans 10 min : le check-in est ouvert ; seule alice confirme
			var t = await svc.Create(owner, Config(DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), checkIn: 15, min: 1));
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);                       // inscrite pendant le check-in : présente d'office
			store.AddTournamentPlayer(t.Id, b, 99);                                       // bob inscrit avant, sans confirmer
			Assert.Null(svc.CheckIn(t.Id, a).Error);
			store.UpdateTournament(t.Id, Config(AccountStore.Now() - 1, checkIn: 15, min: 1));   // l'heure est venue
			await svc.Tick();
			Assert.Equal("running", store.Tournament(t.Id).State);
			Assert.DoesNotContain(store.TournamentPlayers(t.Id), p => p.account == b);   // absent : retiré

			var empty = await svc.Create(owner, Config(Soon, min: 3));
			store.UpdateTournament(empty.Id, Config(AccountStore.Now() - 1, min: 3));
			await svc.Tick();
			Assert.Equal("cancelled", store.Tournament(empty.Id).State);
			Assert.Contains("Annulé au départ", store.Tournament(empty.Id).Note);
		}

		[Fact]
		public async Task Depart_par_l_organisateur_et_modification_avant_le_depart()
		{
			var (svc, store, _, owner, a, _) = Setup();
			var t = await svc.Create(owner, Config(Soon, startMode: "manual", min: 1));
			await svc.Tick();
			Assert.Equal("open", store.Tournament(t.Id).State);                          // départ manuel : rien d'automatique
			var changed = Config(Soon, startMode: "manual", min: 1); changed.Title = "Nouveau nom";
			Assert.NotNull(svc.Update(t.Id, a, changed).Error);                           // seul l'organisateur
			Assert.Null(svc.Update(t.Id, owner, changed).Error);
			Assert.Equal("Nouveau nom", store.Tournament(t.Id).Title);
			await svc.Join(t.Id, a, "alice");
			var duos = Config(Soon, startMode: "manual"); duos.TeamSize = 2;
			Assert.Contains("taille", svc.Update(t.Id, owner, duos).Error);              // inscrits : la taille des équipes ne change plus
			Assert.Null((await svc.Start(t.Id, owner)).Error);
			Assert.Equal("running", store.Tournament(t.Id).State);
		}

		[Fact]
		public async Task Equipe_par_invitations_accepter_refuser_retirer_et_equipe_incomplete_retiree_au_depart()
		{
			var (svc, store, _, owner, a, b) = Setup(races: 1);
			long c = store.CreateAccount("carol", new byte[] { 1 }, new byte[] { 1 }, 1), d = store.CreateAccount("dave", new byte[] { 1 }, new byte[] { 1 }, 1);
			var cfg = Config(Soon, startMode: "manual", min: 1); cfg.TeamSize = 2; cfg.Phases[0].LobbySize = 15;
			var t = await svc.Create(owner, cfg);
			Assert.Null(t.Error);
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);                       // alice devient cheffe de son équipe
			Assert.NotNull(svc.Invite(t.Id, b, "carol").Error);                           // bob n'est chef de rien
			Assert.Contains("Aucun joueur", svc.Invite(t.Id, a, "personne").Error);
			Assert.Null(svc.Invite(t.Id, a, "bob").Error);
			Assert.Contains("complet", svc.Invite(t.Id, a, "carol").Error);              // 2 places : alice + l'invitation en attente
			Assert.Empty(store.TournamentPlayers(t.Id).Where(p => p.account == b));       // pas dans l'équipe tant qu'il n'a pas accepté
			Assert.Null(svc.Decline(t.Id, b).Error);
			Assert.Empty(store.TournamentInvites(t.Id));
			Assert.Null(svc.Invite(t.Id, a, "bob").Error);
			Assert.Null(svc.Accept(t.Id, b).Error);
			Assert.Equal(2, store.TournamentPlayers(t.Id).Count(p => p.reference == store.TournamentPlayers(t.Id).First(x => x.account == a).reference));
			Assert.NotNull(svc.Accept(t.Id, b).Error);                                    // plus d'invitation
			// un coéquipier qui part ne fait partir que lui ; le chef peut retirer un joueur
			Assert.Null((await svc.Leave(t.Id, b)).Error);
			Assert.Single(store.TournamentPlayers(t.Id));
			Assert.Null(svc.Invite(t.Id, a, "bob").Error); Assert.Null(svc.Accept(t.Id, b).Error);
			Assert.Null(svc.Kick(t.Id, a, "bob").Error);
			Assert.Single(store.TournamentPlayers(t.Id));
			Assert.Null(svc.Invite(t.Id, a, "bob").Error); Assert.Null(svc.Accept(t.Id, b).Error);
			// carol inscrit une équipe qu'elle ne complète pas : retirée au départ
			Assert.Null((await svc.Join(t.Id, c, "carol")).Error);
			Assert.Null(svc.Invite(t.Id, c, "dave").Error);
			Assert.Null((await svc.Start(t.Id, owner)).Error);
			Assert.DoesNotContain(store.TournamentPlayers(t.Id), p => p.account == c);
			Assert.Empty(store.TournamentInvites(t.Id));
			Assert.All(store.TournamentPlayers(t.Id), p => Assert.True(p.reference > 0));          // inscrits dans ManageComp au départ
			Assert.Contains("incomplète", store.Tournament(t.Id).Note);
		}

		[Fact]
		public void Phases_de_modes_differents_et_regles_de_compatibilite()
		{
			long now = AccountStore.Now();
			TournamentConfig Make(int teamSize, params PhaseConfig[] phases) => new TournamentConfig { Title = "Mixte", TeamSize = teamSize, StartAt = now + 3600, Phases = phases.ToList() };
			// solo : battle royale (sélection), puis poules de 1 VS 1
			var mix = Make(1, new PhaseConfig { Kind = "lobby", Mode = "br", Races = 2, LobbySize = 20, Qualified = 8 }, new PhaseConfig { Kind = "pools", Mode = "1v1", MaxScore = 3, Loadout = "choice" });
			Assert.Null(mix.Validate(now, true));
			Assert.Equal("mix", mix.Format);
			Assert.Equal(2, mix.MinEntries);                                                       // des matchs : deux inscrits au moins
			Assert.Contains("solo", Make(2, new PhaseConfig { Kind = "pools", Mode = "1v1", MaxScore = 3 }).Validate(now, true));
			Assert.Contains("poules", Make(1, new PhaseConfig { Kind = "lobby", Mode = "1v1" }).Validate(now, true));
			Assert.Contains("équipement", Make(1, new PhaseConfig { Kind = "pools", Mode = "1v1", MaxScore = 3, Loadout = "bazooka" }).Validate(now, true));
			// Capture du drapeau : équipes inscrites (lobby de deux équipes) ou tirées au sort parmi des solos
			var ctf = Make(4, new PhaseConfig { Kind = "lobby", Mode = "ctf", Races = 3, LobbySize = 20, MaxScore = 3 });
			Assert.Null(ctf.Validate(now, true));
			Assert.Equal(2, ctf.Phases[0].LobbySize);
			var random = Make(1, new PhaseConfig { Kind = "lobby", Mode = "ctf", Teams = "random", TeamSize = 5, Races = 2, MaxScore = 3 });
			Assert.Null(random.Validate(now, true));
			Assert.Equal(10, random.Phases[0].LobbySize);
			Assert.Contains("solo", Make(2, new PhaseConfig { Kind = "lobby", Mode = "ctf", Teams = "random", TeamSize = 4, MaxScore = 3 }).Validate(now, true));
			Assert.Contains("équipes", Make(1, new PhaseConfig { Kind = "lobby", Mode = "ctf", MaxScore = 3 }).Validate(now, true));
			Assert.Contains("lobbys", Make(1, new PhaseConfig { Kind = "pools", Mode = "ctf", Teams = "random", TeamSize = 4, MaxScore = 3 }).Validate(now, true));
			// battle royale par équipes : 30 joueurs au plus par lobby
			Assert.Contains("équipes de 3", Make(3, new PhaseConfig { Kind = "lobby", Mode = "br", LobbySize = 20 }).Validate(now, true));
		}

		[Fact]
		public async Task Capture_du_drapeau_equipes_tirees_au_sort_et_places_des_deux_camps()
		{
			var (svc, store, fake, owner, a, b) = Setup(races: 1);
			long c = store.CreateAccount("carol", new byte[] { 1 }, new byte[] { 1 }, 1), d = store.CreateAccount("dave", new byte[] { 1 }, new byte[] { 1 }, 1);
			var cfg = Config(Soon, startMode: "manual", min: 1);
			cfg.Phases = new List<PhaseConfig> { new PhaseConfig { Kind = "lobby", Mode = "ctf", Teams = "random", TeamSize = 2, Races = 1, MaxScore = 3 } };
			var t = await svc.Create(owner, cfg);
			Assert.Null(t.Error);
			foreach (var (id, name) in new[] { (a, "alice"), (b, "bob"), (c, "carol"), (d, "dave") }) Assert.Null((await svc.Join(t.Id, id, name)).Error);
			Assert.Null((await svc.Start(t.Id, owner)).Error);
			var game = JsonDocument.Parse(JsonSerializer.Serialize(await svc.LobbyGame(t.Id, 0))).RootElement;
			Assert.Equal("ctf", game.GetProperty("mode").GetString());
			Assert.Equal(2, game.GetProperty("teamSize").GetInt32());
			Assert.Equal(3, game.GetProperty("scoreLimit").GetInt32());
			var sides = game.GetProperty("sides").EnumerateObject().ToDictionary(p => long.Parse(p.Name), p => p.Value.GetInt32());
			Assert.Equal(4, sides.Count);
			Assert.Equal(2, sides.Values.Count(s => s == 0));                                      // deux contre deux
			Assert.Equal(JsonSerializer.Serialize(await svc.LobbyGame(t.Id, 0)), JsonSerializer.Serialize(await svc.LobbyGame(t.Id, 0)));   // tirage stable
			var mine = JsonSerializer.Serialize(await svc.MyRace(t.Id, a));
			Assert.Contains("\"mode\":\"ctf\"", mine);
			// le camp gagnant prend la 1re place, l'autre la 2e
			var won = sides.Where(s => s.Value == 0).Select(s => (s.Key, 1)).Concat(sides.Where(s => s.Value == 1).Select(s => (s.Key, 2))).ToList();
			Assert.Null((await svc.ReportRace(t.Id, 0, 0, won)).Error);
			Assert.Equal("done", store.Tournament(t.Id).State);
		}

		[Fact]
		public async Task Un_inscrit_du_lobby_qui_ne_joue_pas_compte_comme_abandon()
		{
			var (svc, store, fake, owner, a, b) = Setup(races: 1);
			var t = await svc.Create(owner, Config(Soon, startMode: "manual", min: 1));
			await svc.Join(t.Id, a, "alice"); await svc.Join(t.Id, b, "bob");
			Assert.Null((await svc.Start(t.Id, owner)).Error);
			Assert.Null((await svc.ReportRace(t.Id, 0, 0, new[] { (a, 1) })).Error);          // bob ne s'est pas présenté
			Assert.Equal("done", store.Tournament(t.Id).State);
			int refB = store.TournamentPlayers(t.Id).First(p => p.account == b).reference;
			Assert.Equal(-1, fake.Finishes[refB][0]);                                          // « A » : abandon
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
		public void Le_mode_de_la_phase_choisit_le_serveur_capture_du_drapeau_ou_1_vs_1()
		{
			var launcher = new StubLauncher();
			var mm = new OpenEsport.Matchmaker.MatchmakerService(new OpenEsport.Matchmaker.MatchmakerOptions { Port = 0 }, launcher);
			Assert.StartsWith("MM!WAIT", mm.HandleRequest($"MM?|{OpenEsport.Core.Protocol.Version}|2|ctf|1|T9:3:0"));
			Assert.Equal(OpenEsport.Core.GameMode.CaptureTheFlag, launcher.Launched[0].Mode);
			Assert.Equal(2, launcher.Launched[0].TeamSize);                                          // pas ramené à 4 : la taille de la phase
			Assert.Equal(4, launcher.Launched[0].Players);
			Assert.StartsWith("MM!WAIT", mm.HandleRequest($"MM?|{OpenEsport.Core.Protocol.Version}|1|1v1|1|TD9:p0m1"));
			Assert.Equal(OpenEsport.Core.GameMode.Duel, launcher.Launched[1].Mode);
			Assert.Equal(2, launcher.Launched[1].Players);
			Assert.StartsWith("MM!WAIT", mm.HandleRequest($"MM?|{OpenEsport.Core.Protocol.Version}|1|dm|1|T9:3:1"));
			Assert.Equal(10, launcher.Launched[2].Players);
			Assert.StartsWith("MM!WAIT", mm.HandleRequest($"MM?|{OpenEsport.Core.Protocol.Version}|5|sab|1|TD9:f1p0m1g2"));
			Assert.Equal(10, launcher.Launched[3].Players);
			Assert.StartsWith("MM!ERR", mm.HandleRequest($"MM?|{OpenEsport.Core.Protocol.Version}|1|1v1|1|T9:3:2"));      // le 1 VS 1 ne se joue pas en série
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
			var sides = JsonDocument.Parse(JsonSerializer.Serialize(await svc.MatchGame(t.Id, "p0m0"))).RootElement;
			Assert.Equal(a, sides.GetProperty("a")[0].GetInt64()); Assert.Equal(b, sides.GetProperty("b")[0].GetInt64()); Assert.Equal(10, sides.GetProperty("scoreLimit").GetInt32());
			Assert.Equal("tdm", sides.GetProperty("mode").GetString());

			Assert.NotNull((await svc.ReportMatch(t.Id, "p0m0", 7, 7)).Error);                  // égalité refusée : le match reste à jouer
			Assert.Null((await svc.ReportMatch(t.Id, "p0m0", 10, 4)).Error);                    // poule jouée → tableau
			Assert.Contains("pool:1=V10", fake.Scored); Assert.Contains("pool:2=4", fake.Scored);
			Assert.Equal("running", store.Tournament(t.Id).State);
			var final = await svc.NextMatch(t.Id, b);
			Assert.Equal("f1t0n2", final.Key);
			Assert.NotNull((await svc.ReportMatch(t.Id, "p0m0", 10, 1)).Error);                 // le match de poule est déjà joué
			Assert.Null((await svc.ReportMatch(t.Id, "f1t0n2", 3, 10)).Error);
			Assert.Contains("table:2=V10", fake.Scored);
			Assert.Equal("done", store.Tournament(t.Id).State);
		}

		[Fact]
		public async Task Duel_a_deux_l_equipe_se_forme_par_invitation_et_le_chef_la_retire_entiere()
		{
			var store = new AccountStore(_db);
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(new FakeDuelManageComp()), "http://fake", "clef"));
			long Make(string l) => store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			long orga = Make("orga"), a = Make("alice"), b = Make("bob");
			var t = await svc.Create(orga, "Duel a deux", Soon, 0, 20, "duel", 2, 10);
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.Null(svc.Invite(t.Id, a, "bob").Error);
			Assert.Null(svc.Accept(t.Id, b).Error);
			Assert.Equal(2, store.TournamentPlayers(t.Id).Count);
			Assert.Single(store.TournamentPlayers(t.Id).Select(p => p.reference).Distinct());   // une seule équipe pour ManageComp
			Assert.Null((await svc.Leave(t.Id, a)).Error);
			Assert.Empty(store.TournamentPlayers(t.Id));                                         // la cheffe part : toute l'équipe part
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
			var t = await svc.Create(orga, "BR en duo", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), 1, 15, "br", 2);
			Assert.Null(t.Error);
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.Null(svc.Invite(t.Id, a, "bob").Error);
			Assert.NotNull(svc.Invite(t.Id, a, "carol").Error);                                                   // équipe de deux : complète
			Assert.Null(svc.Accept(t.Id, b).Error);
			Assert.Null((await svc.Start(t.Id, orga)).Error);
			var game = JsonDocument.Parse(JsonSerializer.Serialize(await svc.LobbyGame(t.Id, 0))).RootElement;
			var groups = game.GetProperty("groups").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
			Assert.Equal(groups[a.ToString()], groups[b.ToString()]);                                              // le serveur les met dans la même équipe
			var mineA = await svc.MyRace(t.Id, a); var mineB = await svc.MyRace(t.Id, b);
			Assert.NotNull(mineA); Assert.NotNull(mineB);
			Assert.Equal(JsonSerializer.Serialize(mineA), JsonSerializer.Serialize(mineB));                         // même manche, même lobby, équipes de 2
			Assert.Contains("\"teamSize\":2", JsonSerializer.Serialize(mineA));
			Assert.Null(await svc.MyRace(t.Id, c));                                                                // pas inscrit
		}
	}
}
