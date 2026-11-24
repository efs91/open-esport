using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using OpenEsport.Accounts;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Service de comptes : inscription, connexion, personnages, statistiques, classements et routes des serveurs de partie.</summary>
	public class AccountsTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe_accounts_" + Guid.NewGuid().ToString("N") + ".db");
		private readonly WebApplication _app;
		private readonly HttpClient _http;
		private const string Key = "cle-de-test";

		public AccountsTests()
		{
			var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
			_app = AccountsApp.Build(new AccountsOptions { DbPath = _db, Port = port, Bind = "127.0.0.1", ServerKey = Key });
			_app.StartAsync().GetAwaiter().GetResult();
			_http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
		}

		public void Dispose()
		{
			_http.Dispose();
			_app.StopAsync().GetAwaiter().GetResult();
			foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { }
		}

		private async Task<(HttpStatusCode code, JsonElement body)> Send(HttpMethod method, string url, object payload = null, string token = null, string serverKey = null)
		{
			var req = new HttpRequestMessage(method, url);
			if (payload != null) req.Content = JsonContent.Create(payload);
			if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			if (serverKey != null) req.Headers.Add("X-Server-Key", serverKey);
			var r = await _http.SendAsync(req);
			string text = await r.Content.ReadAsStringAsync();
			return (r.StatusCode, text.Length == 0 || text == "ok" ? default : JsonDocument.Parse(text).RootElement.Clone());
		}

		private Task<(HttpStatusCode code, JsonElement body)> Post(string url, object payload, string token = null, string serverKey = null) => Send(HttpMethod.Post, url, payload, token, serverKey);
		private Task<(HttpStatusCode code, JsonElement body)> Get(string url, string token = null) => Send(HttpMethod.Get, url, null, token);

		private async Task<(string token, long id)> Register(string login, string password = "motdepasse1")
		{
			var (code, body) = await Post("/api/register", new { login, password });
			Assert.Equal(HttpStatusCode.Created, code);
			return (body.GetProperty("token").GetString(), body.GetProperty("account").GetProperty("id").GetInt64());
		}

		private static JsonElement Mode(JsonElement me, string mode) => me.GetProperty("modes").EnumerateArray().First(m => m.GetProperty("mode").GetString() == mode);

		[Fact]
		public async Task Register_Login_And_Me_Work_And_Passwords_Are_Never_Stored_In_Clear()
		{
			var (token, id) = await Register("Alice");
			var (code, me) = await Get("/api/me", token);
			Assert.Equal(HttpStatusCode.OK, code);
			Assert.Equal("Alice", me.GetProperty("login").GetString());
			Assert.Equal(1000, me.GetProperty("general").GetProperty("rating").GetInt32());
			Assert.Equal(OpenEsport.Core.Modes.All.Length, me.GetProperty("modes").GetArrayLength());

			var (c2, login) = await Post("/api/login", new { login = "alice", password = "motdepasse1" });          // l'identifiant ne tient pas compte de la casse
			Assert.Equal(HttpStatusCode.OK, c2);
			Assert.False(string.IsNullOrEmpty(login.GetProperty("token").GetString()));

			var store = new AccountStore(_db);
			var row = store.FindById(id);
			Assert.Equal(32, row.Hash.Length);
			Assert.NotEqual("motdepasse1", Convert.ToBase64String(row.Hash));
			Assert.DoesNotContain("motdepasse1", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(_db)));
		}

		[Fact]
		public async Task Bad_Registrations_And_Bad_Logins_Are_Refused_Without_Revealing_Which_Part_Was_Wrong()
		{
			await Register("Bob");
			Assert.Equal(HttpStatusCode.Conflict, (await Post("/api/register", new { login = "BOB", password = "motdepasse1" })).code);        // déjà pris (casse ignorée)
			Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/register", new { login = "x", password = "motdepasse1" })).code);        // trop court
			Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/register", new { login = "a b c", password = "motdepasse1" })).code);    // caractères interdits
			Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/register", new { login = "Carole", password = "court" })).code);         // mot de passe trop court

			var wrongPassword = await Post("/api/login", new { login = "Bob", password = "mauvais-mdp" });
			var unknown = await Post("/api/login", new { login = "Inconnu", password = "motdepasse1" });
			Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.code);
			Assert.Equal(HttpStatusCode.Unauthorized, unknown.code);
			Assert.Equal(wrongPassword.body.GetProperty("error").GetString(), unknown.body.GetProperty("error").GetString());
			Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/me", "jeton-invente")).code);
			Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/me")).code);
		}

		[Fact]
		public async Task Too_Many_Wrong_Passwords_Lock_The_Login_For_A_While()
		{
			await Register("Dave");
			for (int i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/api/login", new { login = "Dave", password = "mauvais" + i })).code);
			Assert.Equal(HttpStatusCode.TooManyRequests, (await Post("/api/login", new { login = "Dave", password = "motdepasse1" })).code);      // même le bon mot de passe est refusé pendant le blocage
		}

		[Fact]
		public async Task Logout_Ends_The_Session()
		{
			var (token, _) = await Register("Eve");
			Assert.Equal(HttpStatusCode.OK, (await Get("/api/me", token)).code);
			await Post("/api/logout", new { }, token);
			Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/me", token)).code);
		}

		[Fact]
		public async Task Characters_Can_Be_Created_Renamed_Selected_And_Deleted_And_Belong_To_Their_Owner()
		{
			var (token, _) = await Register("Fay");
			var (other, _) = await Register("Gus");
			var (c1, made) = await Post("/api/characters", new { name = "Rex", appearance = "{\"skin\":1}" }, token);
			Assert.Equal(HttpStatusCode.Created, c1);
			long id = made.GetProperty("id").GetInt64();
			Assert.Equal(HttpStatusCode.OK, (await Post($"/api/characters/{id}/select", new { }, token)).code);

			var (_, me) = await Get("/api/me", token);
			Assert.Equal(id, me.GetProperty("selectedCharacter").GetInt64());
			Assert.Equal("Rex", me.GetProperty("characters")[0].GetProperty("name").GetString());

			Assert.Equal(HttpStatusCode.NotFound, (await Post($"/api/characters/{id}/select", new { }, other)).code);         // pas le sien
			Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/api/characters/{id}", new { name = "Rexa", appearance = "{\"skin\":2}" }, token)).code);
			var (_, me2) = await Get("/api/me", token);
			Assert.Equal("Rexa", me2.GetProperty("characters")[0].GetProperty("name").GetString());

			Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/characters", new { name = "", appearance = "{}" }, token)).code);
			for (int i = 1; i < AccountStore.MaxCharacters; i++) Assert.Equal(HttpStatusCode.Created, (await Post("/api/characters", new { name = "P" + i, appearance = "{}" }, token)).code);
			Assert.Equal(HttpStatusCode.Conflict, (await Post("/api/characters", new { name = "En trop", appearance = "{}" }, token)).code);

			Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/characters/{id}", null, other)).code);      // on ne supprime pas celui d'un autre
			Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/characters/{id}", null, token)).code);
			var (_, me3) = await Get("/api/me", token);
			Assert.Equal(0, me3.GetProperty("selectedCharacter").GetInt64());
		}

		[Fact]
		public async Task The_Whole_Character_List_Can_Be_Replaced_In_One_Call()
		{
			var (token, _) = await Register("Ivy");
			var list = new[] { new { name = "A", appearance = "{}" }, new { name = "B", appearance = "{\"x\":1}" }, new { name = "C", appearance = "{}" } };
			Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/characters", new { characters = list, selected = 1 }, token)).code);
			var (_, me) = await Get("/api/me", token);
			Assert.Equal(3, me.GetProperty("characters").GetArrayLength());
			Assert.Equal(me.GetProperty("characters")[1].GetProperty("id").GetInt64(), me.GetProperty("selectedCharacter").GetInt64());      // le deuxième est choisi
			Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/characters", new { characters = list.Take(1).ToArray(), selected = 0 }, token)).code);
			var (_, me2) = await Get("/api/me", token);
			Assert.Equal(1, me2.GetProperty("characters").GetArrayLength());                                                              // l'ancienne liste est remplacée
			var tooMany = Enumerable.Range(0, AccountStore.MaxCharacters + 1).Select(i => new { name = "P" + i, appearance = "{}" }).ToArray();
			Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Put, "/api/characters", new { characters = tooMany, selected = 0 }, token)).code);
			Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/api/characters", new { characters = new[] { new { name = "", appearance = "{}" } }, selected = 0 }, token)).code);
		}

		[Fact]
		public async Task Server_Routes_Need_The_Server_Key_And_Verify_Tokens()
		{
			var (token, id) = await Register("Hal");
			await Post("/api/characters", new { name = "Zed", appearance = "{\"a\":1}" }, token);
			Assert.Equal(HttpStatusCode.Forbidden, (await Post("/internal/verify", new { token })).code);                           // sans clé
			Assert.Equal(HttpStatusCode.Forbidden, (await Post("/internal/verify", new { token }, serverKey: "mauvaise")).code);
			var (code, body) = await Post("/internal/verify", new { token }, serverKey: Key);
			Assert.Equal(HttpStatusCode.OK, code);
			Assert.Equal(id, body.GetProperty("accountId").GetInt64());
			Assert.Equal("Zed", body.GetProperty("character").GetProperty("name").GetString());
			Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/internal/verify", new { token = "faux" }, serverKey: Key)).code);
			Assert.Equal(HttpStatusCode.Forbidden, (await Post("/internal/match", new { matchId = "m" })).code);
		}

		[Fact]
		public async Task A_Team_Match_Updates_Stats_And_Elo_Once_And_The_Leaderboards_Follow()
		{
			var (ta, a) = await Register("TeamA"); var (tb, b) = await Register("TeamB");
			var report = new
			{
				matchId = "m-1", mode = "sab", winnerTeam = 0, playerCount = 2,
				players = new[]
				{
					new { accountId = a, team = 0, placement = 0, kills = 5, deaths = 2, damage = 800L },
					new { accountId = b, team = 1, placement = 0, kills = 2, deaths = 5, damage = 300L },
				}
			};
			var (code, body) = await Post("/internal/match", report, serverKey: Key);
			Assert.Equal(HttpStatusCode.OK, code); Assert.True(body.GetProperty("counted").GetBoolean());
			Assert.False((await Post("/internal/match", report, serverKey: Key)).body.GetProperty("counted").GetBoolean());          // la même partie n'est comptée qu'une fois

			var (_, meA) = await Get("/api/me", ta);
			var sab = Mode(meA, "sab");
			Assert.Equal(1, sab.GetProperty("matches").GetInt32()); Assert.Equal(1, sab.GetProperty("wins").GetInt32());
			Assert.Equal(5, sab.GetProperty("kills").GetInt32()); Assert.Equal(800, sab.GetProperty("damage").GetInt32());
			Assert.Equal(1020, sab.GetProperty("rating").GetInt32());                      // Elo : +20 (K = 40 en période d'essai, scores égaux)
			Assert.Equal(1, sab.GetProperty("rank").GetInt32());
			var (_, meB) = await Get("/api/me", tb);
			Assert.Equal(980, Mode(meB, "sab").GetProperty("rating").GetInt32());

			var (_, lb) = await Get("/api/leaderboard?mode=sab");
			var rows = lb.GetProperty("rows");
			Assert.Equal("TeamA", rows[0].GetProperty("login").GetString()); Assert.Equal("TeamB", rows[1].GetProperty("login").GetString());
			var (_, none) = await Get("/api/leaderboard?mode=br");
			Assert.Equal(0, none.GetProperty("rows").GetArrayLength());
			Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/leaderboard?mode=inconnu")).code);
		}

		[Fact]
		public async Task A_Battle_Royale_Rates_The_Final_Placement_And_Keeps_The_Best_One()
		{
			var (t1, p1) = await Register("Premier"); var (t2, p2) = await Register("Dernier");
			await Post("/internal/match", new { matchId = "br-1", mode = "br", playerCount = 30, players = new[] { new { accountId = p1, team = 0, placement = 1, kills = 4, deaths = 0, damage = 600L }, new { accountId = p2, team = 1, placement = 30, kills = 0, deaths = 1, damage = 0L } } }, serverKey: Key);
			var (_, m1) = await Get("/api/me", t1); var (_, m2) = await Get("/api/me", t2);
			var br1 = Mode(m1, "br"); var br2 = Mode(m2, "br");
			Assert.Equal(1000 + 20 + 4, br1.GetProperty("rating").GetInt32());            // premier : +20, plus 4 éliminations
			Assert.Equal(1000 - 20, br2.GetProperty("rating").GetInt32());                // dernier : -20
			Assert.Equal(1, br1.GetProperty("bestPlacement").GetInt32());
			Assert.Equal(1, br1.GetProperty("wins").GetInt32());
			Assert.True(m1.GetProperty("general").GetProperty("rating").GetInt32() > 1000);
		}

		[Fact]
		public void Rating_Formulas_Are_Fair()
		{
			Assert.Equal(20, Ratings.TeamDelta(1000, 1000, 1, 5));                          // période d'essai : K = 40, donc +20 à égalité
			Assert.Equal(12, Ratings.TeamDelta(1000, 1000, 1, 20));                         // ensuite K = 24
			Assert.True(Ratings.TeamDelta(1000, 1400, 1, 20) > Ratings.TeamDelta(1000, 1000, 1, 20), "battre plus fort rapporte plus");
			Assert.True(Ratings.TeamDelta(1400, 1000, 1, 20) < Ratings.TeamDelta(1000, 1000, 1, 20), "battre plus faible rapporte moins");
			Assert.Equal(0, Ratings.TeamDelta(1000, 1000, 0.5, 20));
			Assert.Equal(20, Ratings.PlacementDelta(1, 30, 0));
			Assert.Equal(-20, Ratings.PlacementDelta(30, 30, 0));
			Assert.Equal(26, Ratings.PlacementDelta(1, 30, 99));                            // bonus d'éliminations plafonné à 6
			Assert.Equal(1000, Ratings.General(Array.Empty<(int, int)>()));
			Assert.Equal(1100, Ratings.General(new[] { (1200, 10), (1000, 10) }));          // moyenne pondérée par les parties
			Assert.Equal(1150, Ratings.General(new[] { (1200, 15), (1000, 5) }));
		}
	}
}
