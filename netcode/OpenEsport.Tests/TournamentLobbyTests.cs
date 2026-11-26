using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenEsport.Accounts;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Lobby d'un tournoi : observateurs, check-in ouvert à la main, tchat, présents, parties en cours, bots par étape, liste « inchangée ».</summary>
	public class TournamentLobbyTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe-lobby-" + Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { } }

		private (TournamentService svc, AccountStore store, long owner, long a, long b, long watcher) Setup()
		{
			var store = new AccountStore(_db);
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(new FakeManageComp { Races = 2 }), "http://fake", "clef-test"));
			long Make(string l) => store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			return (svc, store, Make("orga"), Make("alice"), Make("bob"), Make("arbitre"));
		}

		private static long Soon => DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();

		private static TournamentConfig Config(params string[] observers) => new TournamentConfig
		{
			Title = "Open du lobby", TeamSize = 1, StartAt = Soon, StartMode = "fixed", MinEntries = 1, MaxEntries = 50, Observers = observers.ToList(),
			Phases = new List<PhaseConfig> { new PhaseConfig { Kind = "lobby", Mode = "br", Races = 2, LobbySize = 20, Bots = true, MaxBots = 6 } },
		};

		private static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

		[Fact]
		public async Task Observateurs_invites_reconnus_et_inconnus_refuses()
		{
			var (svc, store, owner, a, _, watcher) = Setup();
			Assert.Contains("inconnu", (await svc.Create(owner, Config("personne"))).Error);
			var t = await svc.Create(owner, Config("ARBITRE", "orga"));
			Assert.Null(t.Error);
			var row = store.Tournament(t.Id);
			Assert.Equal(new[] { "arbitre" }, TournamentService.ConfigOf(row).Observers);          // nom remis comme le compte l'écrit, l'organisateur l'est d'office
			Assert.True(svc.IsObserver(row, watcher));
			Assert.True(svc.IsObserver(row, owner));
			Assert.False(svc.IsObserver(row, a));
		}

		[Fact]
		public async Task L_organisateur_ouvre_le_check_in_a_tout_moment()
		{
			var (svc, store, owner, a, b, _) = Setup();
			var t = await svc.Create(owner, Config());
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.Contains("pas de check-in", svc.CheckIn(t.Id, a).Error);
			Assert.NotNull(svc.OpenCheckIn(t.Id, a).Error);                    // seul l'organisateur
			Assert.Null(svc.OpenCheckIn(t.Id, owner).Error);
			Assert.Contains("déjà ouvert", svc.OpenCheckIn(t.Id, owner).Error);
			Assert.Null(svc.CheckIn(t.Id, a).Error);
			Assert.Null((await svc.Join(t.Id, b, "bob")).Error);                // inscrit pendant le check-in : présent d'office
			Assert.All(store.TournamentEntries(t.Id), e => Assert.True(e.CheckedIn));
			// une modification de la configuration ne referme pas le check-in
			var cfg = TournamentService.ConfigOf(store.Tournament(t.Id));
			cfg.CheckInOpenedAt = 0; cfg.Description = "nouveau règlement";
			Assert.Null(svc.Update(t.Id, owner, cfg).Error);
			Assert.True(TournamentService.ConfigOf(store.Tournament(t.Id)).CheckInOpensAt > 0);
		}

		[Fact]
		public async Task Tchat_presents_et_parties_en_cours_du_lobby()
		{
			var (svc, store, owner, a, b, watcher) = Setup();
			var t = await svc.Create(owner, Config("arbitre"));
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.Null(svc.Chat(t.Id, a, "  Bonne chance à tous  ").Error);
			Assert.Contains("Doucement", svc.Chat(t.Id, a, "trop vite").Error);
			Assert.Contains("réservé", svc.Chat(t.Id, b, "je ne suis pas inscrit").Error);
			Assert.Null(svc.Chat(t.Id, watcher, "L'arbitre est là").Error);
			string Login(long id) => store.FindById(id)?.Login ?? "?";
			var lobby = Json(await svc.Lobby(t.Id, b, 0, Login));
			Assert.Equal(new[] { "Bonne chance à tous", "L'arbitre est là" }, lobby.GetProperty("chat").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
			Assert.False(lobby.GetProperty("canChat").GetBoolean());
			Assert.Contains("bob", lobby.GetProperty("present").EnumerateArray().Select(p => p.GetString()));
			Assert.Equal(new[] { "orga", "arbitre" }, lobby.GetProperty("observers").EnumerateArray().Select(p => p.GetString()));
			long last = lobby.GetProperty("chat").EnumerateArray().Last().GetProperty("id").GetInt64();
			Assert.Empty(Json(await svc.Lobby(t.Id, a, last, Login)).GetProperty("chat").EnumerateArray());

			// parties en cours rapportées par le matchmaking : l'adresse n'est donnée qu'aux observateurs
			svc.Room.ReportLive(new[] { new LiveGame { Tournament = t.Id, Race = 0, Lobby = 0, Mode = "br", Host = "jeu.exemple", Port = 27105, Phase = 1, Humans = 12 } });
			var seen = Json(await svc.Lobby(t.Id, a, 0, Login)).GetProperty("live")[0];
			Assert.Equal(12, seen.GetProperty("humans").GetInt32());
			Assert.Equal(0, seen.GetProperty("port").GetInt32());
			var watched = Json(await svc.Lobby(t.Id, watcher, 0, Login)).GetProperty("live")[0];
			Assert.Equal("jeu.exemple", watched.GetProperty("host").GetString());
			Assert.Equal(27105, watched.GetProperty("port").GetInt32());
		}

		[Fact]
		public async Task Bots_par_etape_transmis_au_serveur_de_la_manche()
		{
			var (svc, store, owner, a, b, _) = Setup();
			var t = await svc.Create(owner, Config());
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.Null((await svc.Join(t.Id, b, "bob")).Error);
			Assert.Null((await svc.Start(t.Id, owner)).Error);
			var game = Json(await svc.LobbyGame(t.Id, 0));
			Assert.True(game.GetProperty("bots").GetBoolean());
			Assert.Equal(6, game.GetProperty("maxBots").GetInt32());
			// sans bots : la manche se joue entre inscrits
			var cfg = Config(); cfg.Phases[0].Bots = false; cfg.Phases[0].MaxBots = 4;
			Assert.Null(cfg.Validate(AccountStore.Now(), creating: true));
			Assert.Equal(0, cfg.Phases[0].MaxBots);
			Assert.False(cfg.Phases[0].FillBots);
			Assert.True(new PhaseConfig { Kind = "lobby", Mode = "br" }.FillBots);             // tournois d'avant ce réglage : battle royale avec bots
			Assert.False(new PhaseConfig { Kind = "pools", Mode = "tdm" }.FillBots);
		}

		[Fact]
		public async Task La_liste_ne_change_de_revision_qu_apres_une_ecriture()
		{
			var (svc, store, owner, a, _, _) = Setup();
			var t = await svc.Create(owner, Config());
			long before = store.TournamentRevision;
			store.Tournament(t.Id); store.TournamentEntries(t.Id);
			Assert.Equal(before, store.TournamentRevision);                     // une lecture ne change rien
			Assert.Null((await svc.Join(t.Id, a, "alice")).Error);
			Assert.True(store.TournamentRevision > before);
		}
	}

	/// <summary>Un observateur regarde une vraie partie de tournoi : il entre sans personnage, reçoit les joueurs et les suit ; un intrus est refusé.</summary>
	[Collection("Network")]
	public class SpectatorTests : IDisposable
	{
		private const string Key = "cle-observateurs";
		private readonly WebApplication _accounts;
		private readonly string _url;

		/// <summary>Un faux service de comptes : trois jetons, une manche de tournoi pour un joueur, un seul observateur.</summary>
		public SpectatorTests()
		{
			var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
			_url = $"http://127.0.0.1:{port}";
			var builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls(_url);
			_accounts = builder.Build();
			_accounts.MapPost("/internal/verify", async (HttpContext ctx) =>
			{
				var token = JsonDocument.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync()).RootElement.GetProperty("token").GetString();
				return token switch
				{
					"joueur" => Results.Json(new { accountId = 1L, login = "Joueur" }),
					"arbitre" => Results.Json(new { accountId = 2L, login = "Arbitre" }),
					"intrus" => Results.Json(new { accountId = 3L, login = "Intrus" }),
					_ => Results.StatusCode(401),
				};
			});
			_accounts.MapGet("/internal/tournament/{id:long}/lobby/{lobby:int}", () => Results.Json(new { race = 0, races = 1, mode = "br", teamSize = 1, scoreLimit = 0, bots = false, accounts = new[] { 1L } }));
			_accounts.MapGet("/internal/tournament/{id:long}/observer/{account:long}", (long account) => account == 2 ? Results.Json(new { ok = true }) : Results.StatusCode(403));
			_accounts.StartAsync().GetAwaiter().GetResult();
		}

		public void Dispose() { _accounts.StopAsync().GetAwaiter().GetResult(); }

		private static Rig.Bot Spectator(Rig rig, string token)
		{
			var bot = new Rig.Bot { C = new GameClient(new StaticWorld(rig.World), rig.World.Checksum()) };
			bot.C.ConnectSpectator("127.0.0.1", rig.Port, token);
			rig.Bots.Add(bot);
			return bot;
		}

		[Fact]
		public void Un_observateur_regarde_la_partie_sans_y_jouer_et_un_intrus_est_refuse()
		{
			using var rig = new Rig(o => { o.AccountsUrl = _url; o.AccountsKey = Key; o.Tournament = 7; o.TournamentLobby = 0; o.FillBots = false; o.MinPlayers = 5; });
			rig.WaitUntil(() => false, 1.0);                                   // le serveur lit la manche auprès du service de comptes
			var player = rig.Connect("x", token: "joueur");
			Assert.True(rig.WaitPlaying(player), player.C.FailReason);

			var watcher = Spectator(rig, "arbitre");
			Assert.True(rig.WaitPlaying(watcher), watcher.C.FailReason);
			Assert.Equal(0xFFFF, watcher.C.PlayerId);
			Assert.Equal(1, rig.Server.PlayerCount);                          // l'observateur n'a pas de personnage
			Assert.Equal(1, rig.Server.SpectatorCount);
			rig.Run(1.0);
			Assert.Contains(watcher.C.Roster, r => r.Name == "Joueur");
			var seen = new List<InterpolatedEntity>();
			watcher.C.SampleEntities(seen);
			Assert.Contains(seen, e => e.Kind == EntityKind.Player && e.Id == player.C.PlayerId);

			watcher.C.Follow(player.C.PlayerId);
			rig.Run(0.3);
			Assert.Equal(1, rig.Server.PlayerCount);

			var intruder = Spectator(rig, "intrus");
			Assert.True(rig.WaitUntil(() => intruder.C.State == NetState.Failed, 5));
			Assert.Contains("observateurs", intruder.C.FailReason);
		}
	}
}
