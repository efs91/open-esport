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
				return (new VerifiedAccount { AccountId = root.GetProperty("accountId").GetInt64(), Login = root.GetProperty("login").GetString() }, false);
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException)
			{
				Log($"Service de comptes injoignable : {e.Message}");
				return (null, true);
			}
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
