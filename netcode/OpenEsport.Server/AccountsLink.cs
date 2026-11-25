using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenEsport.Server
{
	/// <summary>Un joueur dont le jeton a été vérifié par le service de comptes.</summary>
	public sealed class VerifiedAccount
	{
		public long AccountId;
		public string Login;
		/// <summary>Groupe du joueur (0 = aucun) : les membres d'un même groupe sont rangés dans la même équipe.</summary>
		public long PartyId;
		public int PartySize;
	}

	/// <summary>Un joueur dans le rapport de fin de partie envoyé au service de comptes.</summary>
	public sealed class ReportedPlayer
	{
		public long AccountId { get; set; }
		public int Team { get; set; }
		public int Placement { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
		public long Damage { get; set; }
		/// <summary>Temps passé dans la partie (s) : jusqu'à son élimination en battle royale, jusqu'à son départ ou la fin sinon.</summary>
		public int Seconds { get; set; }
	}

	/// <summary>
	/// Liaison du serveur de partie avec le service de comptes : vérification du jeton d'un joueur qui se connecte, et rapport de fin de partie (statistiques et classement).
	/// Les appels sont asynchrones et ne bloquent jamais la boucle du serveur. Le rapport est renvoyé plusieurs fois en cas d'échec ; le service ne compte une partie qu'une fois.
	/// </summary>
	public sealed class AccountsLink
	{
		private readonly string _url, _key;
		private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
		private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		public Action<string> Log = s => Console.WriteLine(s);

		public AccountsLink(string url, string key)
		{
			_url = url.TrimEnd('/');
			_key = key ?? "";
		}

		private HttpRequestMessage Request(string path, object body)
		{
			var req = new HttpRequestMessage(HttpMethod.Post, _url + path) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };
			req.Headers.Add("X-Server-Key", _key);
			return req;
		}

		/// <summary>Le compte d'un jeton, ou null si le jeton est invalide (ou si le service est injoignable : <paramref name="unreachable"/> vaut alors vrai).</summary>
		public async Task<(VerifiedAccount account, bool unreachable)> VerifyAsync(string token)
		{
			try
			{
				using var res = await _http.SendAsync(Request("/internal/verify", new { token }));
				if (!res.IsSuccessStatusCode) return (null, false);
				var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
				return (new VerifiedAccount { AccountId = root.GetProperty("accountId").GetInt64(), Login = root.GetProperty("login").GetString(), PartyId = root.TryGetProperty("partyId", out var pid) ? pid.GetInt64() : 0, PartySize = root.TryGetProperty("partySize", out var ps) ? ps.GetInt32() : 0 }, false);
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException)
			{
				Log($"Service de comptes injoignable : {e.Message}");
				return (null, true);
			}
		}

		/// <summary>
		/// Une partie de tournoi telle que la décrit le service de comptes : comptes admis, mode de jeu de la phase, taille des équipes en jeu,
		/// score à atteindre (0 = valeur du jeu), équipe de chaque compte (<see cref="Groups"/> : même numéro = même équipe ; battle royale)
		/// ou camp imposé (<see cref="Sides"/> : 0 ou 1 ; modes à deux équipes), règle d'équipement du 1 VS 1.
		/// </summary>
		public sealed class TournamentGame
		{
			public int Race;
			public HashSet<long> Accounts = new HashSet<long>();
			public string Mode = "br", Loadout = "";
			public int TeamSize = 1, ScoreLimit;
			/// <summary>Des bots complètent la partie (non : on joue entre inscrits ; null : réglage du serveur).</summary>
			public bool? Bots;
			public Dictionary<long, long> Groups = new Dictionary<long, long>();
			public Dictionary<long, int> Sides = new Dictionary<long, int>();

			public static TournamentGame Parse(JsonElement root)
			{
				var g = new TournamentGame();
				if (root.TryGetProperty("race", out var r) && r.ValueKind == JsonValueKind.Number) g.Race = r.GetInt32();
				if (root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String) g.Mode = m.GetString();
				if (root.TryGetProperty("loadout", out var l) && l.ValueKind == JsonValueKind.String) g.Loadout = l.GetString();
				if (root.TryGetProperty("teamSize", out var ts) && ts.ValueKind == JsonValueKind.Number) g.TeamSize = ts.GetInt32();
				if (root.TryGetProperty("scoreLimit", out var sl) && sl.ValueKind == JsonValueKind.Number) g.ScoreLimit = sl.GetInt32();
				if (root.TryGetProperty("bots", out var bots) && bots.ValueKind is JsonValueKind.True or JsonValueKind.False) g.Bots = bots.GetBoolean();
				if (root.TryGetProperty("accounts", out var acc)) foreach (var a in acc.EnumerateArray()) g.Accounts.Add(a.GetInt64());
				// match à deux camps : « a » et « b »
				if (root.TryGetProperty("a", out var sa)) foreach (var a in sa.EnumerateArray()) { g.Sides[a.GetInt64()] = 0; g.Accounts.Add(a.GetInt64()); }
				if (root.TryGetProperty("b", out var sb)) foreach (var b in sb.EnumerateArray()) { g.Sides[b.GetInt64()] = 1; g.Accounts.Add(b.GetInt64()); }
				if (root.TryGetProperty("groups", out var gr) && gr.ValueKind == JsonValueKind.Object)
					foreach (var p in gr.EnumerateObject()) if (long.TryParse(p.Name, out long id)) g.Groups[id] = p.Value.GetInt64();
				if (root.TryGetProperty("sides", out var sd) && sd.ValueKind == JsonValueKind.Object)
					foreach (var p in sd.EnumerateObject()) if (long.TryParse(p.Name, out long id)) g.Sides[id] = p.Value.GetInt32();
				return g;
			}
		}

		/// <summary>La partie d'un lobby de tournoi (null si le service ne répond pas ou si le tournoi n'est pas en cours).</summary>
		public async Task<TournamentGame> TournamentLobbyAsync(long tournament, int lobby)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/internal/tournament/{tournament}/lobby/{lobby}");
				req.Headers.Add("X-Server-Key", _key);
				using var res = await _http.SendAsync(req);
				if (!res.IsSuccessStatusCode) return null;
				return TournamentGame.Parse(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement);
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException) { Log($"Tournoi : service de comptes injoignable ({e.Message})"); return null; }
		}

		/// <summary>Envoie les places d'une manche de tournoi (même principe que le rapport de partie : plusieurs essais).</summary>
		public void ReportRace(long tournament, int race, int lobby, List<ReportedPlayer> players)
		{
			var body = new { tournamentId = tournament, race, lobby, players };
			_ = Task.Run(async () =>
			{
				foreach (int wait in new[] { 0, 3, 10, 30 })
				{
					if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
					try
					{
						using var res = await _http.SendAsync(Request("/internal/tournament-race", body));
						if (res.IsSuccessStatusCode) { Log($"Manche {race + 1} du tournoi {tournament} rapportée ({players.Count} joueur(s))"); return; }
						Log($"Manche de tournoi refusée par le service de comptes ({(int)res.StatusCode})");
						if ((int)res.StatusCode < 500) return;
					}
					catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException) { Log($"Manche de tournoi : service injoignable ({e.Message})"); }
				}
			});
		}

		/// <summary>Un match de poule ou de tableau : les deux camps, le mode, le score à atteindre ; null si le match n'existe plus.</summary>
		public async Task<TournamentGame> TournamentMatchAsync(long tournament, string key)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/internal/tournament/{tournament}/match/{Uri.EscapeDataString(key)}");
				req.Headers.Add("X-Server-Key", _key);
				using var res = await _http.SendAsync(req);
				if (!res.IsSuccessStatusCode) return null;
				return TournamentGame.Parse(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement);
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException) { Log($"Tournoi : service de comptes injoignable ({e.Message})"); return null; }
		}

		/// <summary>Envoie le score d'un match de duel (plusieurs essais). Une égalité est refusée par le service : le match reste à jouer.</summary>
		public void ReportDuel(long tournament, string key, int scoreA, int scoreB)
		{
			var body = new { tournamentId = tournament, key, scoreA, scoreB };
			_ = Task.Run(async () =>
			{
				foreach (int wait in new[] { 0, 3, 10, 30 })
				{
					if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
					try
					{
						using var res = await _http.SendAsync(Request("/internal/tournament-match", body));
						if (res.IsSuccessStatusCode) { Log($"Match {key} du tournoi {tournament} rapporté ({scoreA}-{scoreB})"); return; }
						Log($"Match de tournoi refusé par le service de comptes ({(int)res.StatusCode})");
						if ((int)res.StatusCode < 500) return;
					}
					catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException) { Log($"Match de tournoi : service injoignable ({e.Message})"); }
				}
			});
		}

		/// <summary>Envoie le rapport d'une partie en arrière-plan (jusqu'à 4 essais espacés).</summary>
		public void ReportMatch(string matchId, string mode, int teamSize, int winnerTeam, int playerCount, List<ReportedPlayer> players)
		{
			if (players.Count == 0) return;
			var body = new { matchId, mode, teamSize, winnerTeam, playerCount, players };
			_ = Task.Run(async () =>
			{
				int[] waits = { 0, 3, 10, 30 };
				foreach (int wait in waits)
				{
					if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
					try
					{
						using var res = await _http.SendAsync(Request("/internal/match", body));
						if (res.IsSuccessStatusCode) { Log($"Rapport de partie {matchId} envoyé ({players.Count} joueur(s) avec compte)"); return; }
						Log($"Rapport de partie refusé par le service de comptes ({(int)res.StatusCode})");
						if ((int)res.StatusCode < 500) return;      // une erreur de notre côté ne se corrige pas en réessayant
					}
					catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException) { Log($"Rapport de partie : service injoignable ({e.Message})"); }
				}
			});
		}
	}
}
