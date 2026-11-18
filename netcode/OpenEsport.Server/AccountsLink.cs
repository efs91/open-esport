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

		/// <summary>Les comptes admis dans un lobby de tournoi et la manche en cours (null si le service ne répond pas ou si le tournoi n'est pas en cours).</summary>
		public async Task<(int race, HashSet<long> accounts)?> TournamentLobbyAsync(long tournament, int lobby)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/internal/tournament/{tournament}/lobby/{lobby}");
				req.Headers.Add("X-Server-Key", _key);
				using var res = await _http.SendAsync(req);
				if (!res.IsSuccessStatusCode) return null;
				var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
				var set = new HashSet<long>();
				foreach (var a in root.GetProperty("accounts").EnumerateArray()) set.Add(a.GetInt64());
				return (root.GetProperty("race").GetInt32(), set);
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

		/// <summary>Les camps d'un match de duel (comptes du camp 0 et du camp 1) et son score limite ; null si le match n'existe plus.</summary>
		public async Task<(HashSet<long> a, HashSet<long> b, int scoreLimit)?> TournamentMatchAsync(long tournament, string key)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/internal/tournament/{tournament}/match/{Uri.EscapeDataString(key)}");
				req.Headers.Add("X-Server-Key", _key);
				using var res = await _http.SendAsync(req);
				if (!res.IsSuccessStatusCode) return null;
				var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
				HashSet<long> Read(string name) { var set = new HashSet<long>(); foreach (var x in root.GetProperty(name).EnumerateArray()) set.Add(x.GetInt64()); return set; }
				return (Read("a"), Read("b"), root.GetProperty("scoreLimit").GetInt32());
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
		public void ReportMatch(string matchId, string mode, int winnerTeam, int playerCount, List<ReportedPlayer> players)
		{
			if (players.Count == 0) return;
			var body = new { matchId, mode, winnerTeam, playerCount, players };
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
