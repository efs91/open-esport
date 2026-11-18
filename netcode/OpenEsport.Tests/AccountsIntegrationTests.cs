using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using OpenEsport.Accounts;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Un serveur de partie branché sur un vrai service de comptes : jeton exigé à l'entrée, pseudo = identifiant du compte, résultats rapportés en fin de partie.</summary>
	[Collection("Network")]
	public class AccountsIntegrationTests : IDisposable
	{
		private const string Key = "cle-integration";
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe_accounts_int_" + Guid.NewGuid().ToString("N") + ".db");
		private readonly WebApplication _app;
		private readonly HttpClient _http;
		private readonly string _url;

		public AccountsIntegrationTests()
		{
			var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
			_url = $"http://127.0.0.1:{port}";
			_app = AccountsApp.Build(new AccountsOptions { DbPath = _db, Port = port, Bind = "127.0.0.1", ServerKey = Key });
			_app.StartAsync().GetAwaiter().GetResult();
			_http = new HttpClient { BaseAddress = new Uri(_url) };
		}

		public void Dispose()
		{
			_http.Dispose();
			_app.StopAsync().GetAwaiter().GetResult();
			foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { }
		}

		private async Task<(string token, long id)> Register(string login)
		{
			var r = await _http.PostAsJsonAsync("/api/register", new { login, password = "motdepasse1" });
			var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
			return (root.GetProperty("token").GetString(), root.GetProperty("account").GetProperty("id").GetInt64());
		}

		private async Task<JsonElement> Me(string token)
		{
			var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
			req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
			var r = await _http.SendAsync(req);
			return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
		}

		private Rig Server(Action<ServerOptions> more = null) => new Rig(o => { o.AccountsUrl = _url; o.AccountsKey = Key; more?.Invoke(o); });

		[Fact]
		public async Task A_Server_With_Accounts_Refuses_Players_Without_A_Valid_Token_And_Names_The_Others_After_Their_Account()
		{
			var (token, _) = await Register("Joueuse");
			using var rig = Server();
			var anonymous = rig.Connect("Anonyme");
			Assert.True(rig.WaitUntil(() => anonymous.C.State == NetState.Failed, 5));
			Assert.Contains("Compte requis", anonymous.C.FailReason);

			var fake = rig.Connect("Faux", token: "jeton-invente");
			Assert.True(rig.WaitUntil(() => fake.C.State == NetState.Failed, 5));
			Assert.Contains("Session invalide", fake.C.FailReason);

			var good = rig.Connect("PseudoQuiNeCompteRien", token: token);
			Assert.True(rig.WaitPlaying(good), $"le joueur avec compte doit entrer : {good.C.FailReason}");
			Assert.True(rig.WaitUntil(() => good.C.Roster.Any(r => r.Name == "Joueuse"), 3), "son pseudo est l'identifiant de son compte");

			var twice = rig.Connect("Double", token: token);                         // le même compte une deuxième fois
			Assert.True(rig.WaitUntil(() => twice.C.State == NetState.Failed, 5));
			Assert.Contains("déjà connecté", twice.C.FailReason);
		}

		[Fact]
		public async Task A_Finished_Match_Is_Reported_And_Updates_The_Accounts_Statistics()
		{
			var (ta, a) = await Register("Gagnante"); var (tb, b) = await Register("Perdant");
			GameBalance.ApplyFileText("{\"mode.tdmKills\": 2}", new System.Collections.Generic.List<string>());
			var world = WorldData.CreateFlat(300, 100f);
			world.Points.Add(new MapPoint { Kind = MapPointKind.SpawnTeam, Team = 0, Pos = new Vec3(-40, 100f, 0) });
			world.Points.Add(new MapPoint { Kind = MapPointKind.SpawnTeam, Team = 1, Pos = new Vec3(40, 100f, 0) });
			try
			{
				using var rig = new Rig(o =>
				{
					o.AccountsUrl = _url; o.AccountsKey = Key;
					o.Mode = GameMode.TeamDeathmatch; o.Match.TeamSize = 1; o.MinPlayers = 2; o.LobbyCountdown = 0.3f; o.EndLinger = 100f;
					o.Match.MaxPlayers = 2;
				}, world);
				var p1 = rig.Connect("x", token: ta); var p2 = rig.Connect("y", token: tb);
				Assert.True(rig.WaitPlaying(p1)); Assert.True(rig.WaitPlaying(p2));
				Assert.True(rig.WaitUntil(() => rig.Server.Phase == MatchPhase.Playing, 6));
				int id1 = p1.C.PlayerId, id2 = p2.C.PlayerId;
				// les deux joueurs sont dans deux équipes : p1 élimine p2 deux fois
				for (int i = 0; i < 2; i++)
				{
					rig.OnServer(() => rig.Server.AdminKill(id1, id2));
					rig.WaitUntil(() => rig.Server.GetPlayer(id2).Alive || rig.Server.Phase == MatchPhase.Over, 6);
					rig.Run(0.2);
				}
				Assert.Equal(MatchPhase.Over, rig.Server.Phase);

				// le rapport part en arrière-plan : on attend qu'il soit compté
				JsonElement me = default;
				for (int i = 0; i < 40; i++)
				{
					me = await Me(ta);
					if (me.GetProperty("modes").EnumerateArray().First(m => m.GetProperty("mode").GetString() == "tdm").GetProperty("matches").GetInt32() > 0) break;
					await Task.Delay(100);
				}
				var tdm = me.GetProperty("modes").EnumerateArray().First(m => m.GetProperty("mode").GetString() == "tdm");
				Assert.Equal(1, tdm.GetProperty("matches").GetInt32());
				Assert.Equal(1, tdm.GetProperty("wins").GetInt32());
				Assert.Equal(2, tdm.GetProperty("kills").GetInt32());
				Assert.True(tdm.GetProperty("rating").GetInt32() > 1000);
				var loser = (await Me(tb)).GetProperty("modes").EnumerateArray().First(m => m.GetProperty("mode").GetString() == "tdm");
				Assert.Equal(0, loser.GetProperty("wins").GetInt32());
				Assert.Equal(2, loser.GetProperty("deaths").GetInt32());
				Assert.True(loser.GetProperty("rating").GetInt32() < 1000);
			}
			finally { GameBalance.ApplyFileText("{\"mode.tdmKills\": " + GameBalance.Entries.First(e => e.Key == "mode.tdmKills").Default.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", new System.Collections.Generic.List<string>()); }
		}
	}
}
