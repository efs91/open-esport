using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenEsport.Accounts
{
	/// <summary>Un tournoi annoncé dans le jeu (publication ManageComp).</summary>
	public sealed class TournamentInfo
	{
		public string Id { get; set; }
		public string Title { get; set; }
		public string Subtitle { get; set; }
		/// <summary>Date et heure de début, telles que publiées par l'organisateur (texte ISO).</summary>
		public string Date { get; set; }
		public string Url { get; set; }
	}

	/// <summary>
	/// Liaison avec l'API de ManageComp (gestionnaire de compétitions : poules, tableaux, lobbys/séries, classements). Le jeu ne réinvente pas les tournois :
	/// les compétitions sont organisées dans ManageComp et publiées ; le service de comptes les relaie aux joueurs. La clé API est donnée
	/// par l'environnement du serveur (<c>MANAGECOMP_API_KEY</c>, <c>MANAGECOMP_URL</c>) et ne sort jamais du serveur. Les réponses sont gardées en cache
	/// une minute : l'API n'est pas interrogée à chaque ouverture du menu.
	/// </summary>
	public sealed class ManageCompLink
	{
		private readonly string _url, _key;
		private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
		private List<TournamentInfo> _cache = new List<TournamentInfo>();
		private DateTime _fetched = DateTime.MinValue;
		private readonly object _gate = new object();
		private Task _refresh;

		public bool Configured => !string.IsNullOrWhiteSpace(_key);

		public ManageCompLink(string url, string key)
		{
			_url = (string.IsNullOrWhiteSpace(url) ? "https://api.managecomp.org" : url).TrimEnd('/');
			_key = key;
		}

		public static ManageCompLink FromEnvironment() => new ManageCompLink(Environment.GetEnvironmentVariable("MANAGECOMP_URL"), Environment.GetEnvironmentVariable("MANAGECOMP_API_KEY"));

		/// <summary>Les tournois publiés (à venir d'abord). Retourne le cache tout de suite et le rafraîchit en arrière-plan quand il est vieux.</summary>
		public List<TournamentInfo> Tournaments()
		{
			lock (_gate)
			{
				if (Configured && DateTime.UtcNow - _fetched > TimeSpan.FromSeconds(60) && (_refresh == null || _refresh.IsCompleted))
					_refresh = Task.Run(Refresh);
				return _cache.ToList();
			}
		}

		private async Task Refresh()
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, _url + "/api/publications");
				req.Headers.Add("X-Api-Key", _key);
				using var res = await _http.SendAsync(req);
				if (!res.IsSuccessStatusCode) return;
				var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
				var list = new List<TournamentInfo>();
				if (root.TryGetProperty("publications", out var pubs))
					foreach (var p in pubs.EnumerateArray())
						list.Add(new TournamentInfo
						{
							Id = Str(p, "id"), Title = Str(p, "title"), Subtitle = Str(p, "subtitle"), Date = Str(p, "date"), Url = Str(p, "url")
						});
				// à venir d'abord (la date est un texte ISO : l'ordre alphabétique est l'ordre chronologique)
				string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
				var ordered = list.Where(t => string.CompareOrdinal(t.Date ?? "", today) >= 0).OrderBy(t => t.Date, StringComparer.Ordinal)
					.Concat(list.Where(t => string.CompareOrdinal(t.Date ?? "", today) < 0).OrderByDescending(t => t.Date, StringComparer.Ordinal)).Take(30).ToList();
				lock (_gate) { _cache = ordered; _fetched = DateTime.UtcNow; }
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException || e is InvalidOperationException)
			{
				lock (_gate) _fetched = DateTime.UtcNow - TimeSpan.FromSeconds(30);       // on réessaie dans 30 s
			}
		}

		private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	}
}
