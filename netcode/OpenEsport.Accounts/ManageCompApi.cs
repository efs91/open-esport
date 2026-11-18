using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenEsport.Accounts
{
	/// <summary>Un match à jouer (poule ou tableau) : deux participants de ManageComp. <see cref="Key"/> l'identifie dans le jeu.</summary>
	public sealed class DuelMatch
	{
		public string Kind;                       // « pool » ou « table »
		public int Stage, Pool, Match, Set, Node;
		public int RefA, RefB;
		public string NameA, NameB;
		public string Key => Kind == "pool" ? $"p{Pool}m{Match}" : $"t{Set}n{Node}";
	}

	/// <summary>Une erreur renvoyée par l'API de ManageComp (ou un service injoignable).</summary>
	public sealed class ManageCompException : Exception
	{
		public ManageCompException(string message) : base(message) { }
	}

	/// <summary>
	/// Appels de l'API de ManageComp utilisés pour les tournois : une compétition par tournoi, ses participants à l'appel, une phase « Lobby (série) »
	/// dont chaque manche est une partie du jeu, la saisie des places et le classement cumulé. Le <see cref="HttpClient"/> est fourni de l'extérieur
	/// pour pouvoir tester sans réseau.
	/// </summary>
	public sealed class ManageCompApi
	{
		/// <summary>Points par place (1er, 2e…), sur le modèle des classements de battle royale : au-delà, 1 point jusqu'à la 15e place.</summary>
		public static readonly int[] PlacementPoints = { 12, 9, 7, 5, 4, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1 };

		private readonly HttpClient _http;
		private readonly string _url, _key;
		private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

		public bool Configured => !string.IsNullOrWhiteSpace(_key);

		public ManageCompApi(HttpClient http, string url, string key)
		{
			_http = http;
			_url = (string.IsNullOrWhiteSpace(url) ? "https://api.managecomp.org" : url).TrimEnd('/');
			_key = key;
		}

		private async Task<JsonElement> Send(HttpMethod method, string path, object body = null)
		{
			if (!Configured) throw new ManageCompException("Les tournois ne sont pas configurés sur ce serveur.");
			using var req = new HttpRequestMessage(method, _url + path);
			req.Headers.Add("X-Api-Key", _key);
			if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
			try
			{
				using var res = await _http.SendAsync(req);
				string text = await res.Content.ReadAsStringAsync();
				if (!res.IsSuccessStatusCode)
				{
					string message = text;
					try { var e = JsonDocument.Parse(text).RootElement; if (e.TryGetProperty("error", out var m)) message = m.GetString(); } catch { }
					throw new ManageCompException($"ManageComp : {message}");
				}
				return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
			{
				throw new ManageCompException("ManageComp est injoignable : " + e.Message);
			}
		}

		/// <summary>Crée la compétition d'un tournoi avec sa phase de lobby (série) de <paramref name="races"/> manches ; retourne l'identifiant de la compétition.</summary>
		public async Task<string> CreateTournament(string name, string date, int races)
		{
			var created = await Send(HttpMethod.Post, "/api/contests", new { name, organizer = "Open Esport", location = "En ligne", weapon = "X", gender = "X", category = 9, date });
			string id = created.GetProperty("id").GetString();
			try
			{
				// la formule par défaut (poules + tableau) est remplacée par une seule phase de lobby
				await Send(HttpMethod.Delete, $"/api/contests/{id}/schedule/stages/3");
				await Send(HttpMethod.Delete, $"/api/contests/{id}/schedule/stages/2");
				var stages = await Send(HttpMethod.Post, $"/api/contests/{id}/schedule/stages", new { kind = "lobby" });
				int lobby = FindStage(stages, "lobby");
				await SetRaces(id, lobby, races);
			}
			catch { try { await Delete(id); } catch { } throw; }
			return id;
		}

		private static int FindStage(JsonElement schedule, string kind)
		{
			foreach (var s in schedule.GetProperty("stages").EnumerateArray())
				if (s.GetProperty("kind").GetString() == kind) return s.GetProperty("index").GetInt32();
			throw new ManageCompException("ManageComp : phase « " + kind + " » introuvable.");
		}

		public async Task SetRaces(string contest, int stage, int races)
		{
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new
			{
				action = "settings",
				settings = new
				{
					abandon_points = 0, distribution = "balanced", lobbies = 1, per_lobby = (int?)null, races, scale = PlacementPoints,
					swapping = new[] { "club" }, team_best = 1, team_mode = "team", team_rule = "sum"
				}
			});
		}

		public async Task Delete(string contest) => await Send(HttpMethod.Delete, $"/api/contests/{contest}");

		/// <summary>Inscrit un joueur (présent à l'appel) ; retourne sa référence dans la compétition.</summary>
		public async Task<int> AddPlayer(string contest, string name)
		{
			var view = await Send(HttpMethod.Post, $"/api/contests/{contest}/checkin/fencers", new { values = new Dictionary<string, string> { ["name"] = name } });
			int reference = view.GetProperty("fencers").EnumerateArray().Select(f => f.GetProperty("reference").GetInt32()).Max();
			await Send(HttpMethod.Put, $"/api/contests/{contest}/checkin/attending", new { reference, attending = true });
			return reference;
		}

		public async Task RemovePlayer(string contest, int reference) => await Send(HttpMethod.Delete, $"/api/contests/{contest}/checkin/fencers/{reference}");

		/// <summary>Passe de l'appel à la phase de lobby et répartit les joueurs en <paramref name="lobbies"/> lobbys (la phase d'appel doit être terminée).</summary>
		public async Task BeginLobby(string contest, int lobbies)
		{
			await Send(HttpMethod.Post, $"/api/contests/{contest}/next");
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int lobby = FindStage(schedule, "lobby");
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{lobby}", new { action = "composition", lobbies = Math.Max(1, lobbies), distribution = "balanced" });
		}

		/// <summary>Saisit la place d'un joueur dans une manche (0 = première manche) du lobby <paramref name="lobbyIndex"/>. Place 0 ou moins : abandon.</summary>
		public async Task SetFinish(string contest, int lobbyIndex, int race, int reference, int place)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int stage = FindStage(schedule, "lobby");
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "finish", lobby = lobbyIndex, race, player = reference, value = place > 0 ? place.ToString() : "A" });
		}

		// ------------------------------------------------------------------ duels (poules puis tableaux)

		/// <summary>Crée la compétition d'un tournoi en duels : appel, poules, tableau et classement (la formule par défaut de ManageComp). Les scores vont jusqu'à <paramref name="scoreLimit"/>.</summary>
		public async Task<string> CreateDuelTournament(string name, string date, int scoreLimit)
		{
			var created = await Send(HttpMethod.Post, "/api/contests", new { name, organizer = "Open Esport", location = "En ligne", weapon = "X", gender = "X", category = 9, date });
			return created.GetProperty("id").GetString();
		}

		/// <summary>Ferme l'appel et passe aux poules ; le score maximum des poules et du tableau est réglé sur celui de la partie.</summary>
		public async Task BeginDuels(string contest, int scoreLimit)
		{
			await Send(HttpMethod.Post, $"/api/contests/{contest}/next");        // appel → composition des poules
			await Send(HttpMethod.Post, $"/api/contests/{contest}/next");        // composition → poules
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int pools = FindStage(schedule, "pool_supervisor"), table = FindStage(schedule, "table");
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{pools}", new { action = "settings", max_score = scoreLimit });
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{table}", new { action = "settings", max_score = scoreLimit });
		}

		public async Task<(string kind, int index)> CurrentStage(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int current = schedule.GetProperty("current").GetInt32();
			foreach (var s in schedule.GetProperty("stages").EnumerateArray())
				if (s.GetProperty("index").GetInt32() == current) return (s.GetProperty("kind").GetString(), current);
			return (null, current);
		}

		/// <summary>Les matchs de la phase en cours dont les deux participants sont connus et qui n'ont pas encore de score.</summary>
		public async Task<List<DuelMatch>> PendingMatches(string contest)
		{
			var list = new List<DuelMatch>();
			var (kind, stage) = await CurrentStage(contest);
			if (kind == "pool_supervisor")
			{
				var data = (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");
				foreach (var pool in data.GetProperty("pools").EnumerateArray())
					foreach (var m in pool.GetProperty("matches").EnumerateArray())
					{
						var o = m.GetProperty("opponents").EnumerateArray().ToList();
						if (o.Count != 2 || o.Any(x => !string.IsNullOrEmpty(x.GetProperty("score").GetString()))) continue;
						list.Add(new DuelMatch { Kind = "pool", Stage = stage, Pool = pool.GetProperty("index").GetInt32(), Match = m.GetProperty("index").GetInt32(),
							RefA = o[0].GetProperty("reference").GetInt32(), RefB = o[1].GetProperty("reference").GetInt32(), NameA = o[0].GetProperty("name").GetString(), NameB = o[1].GetProperty("name").GetString() });
					}
			}
			else if (kind == "table")
			{
				var data = (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");
				if (data.TryGetProperty("drawing", out var drawing) && drawing.ValueKind == JsonValueKind.Object)
				{
					int set = data.TryGetProperty("displayed", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
					var byNode = new Dictionary<int, List<JsonElement>>();
					foreach (var cell in drawing.GetProperty("cells").EnumerateArray())
					{
						if (cell.GetProperty("score").ValueKind != JsonValueKind.Object || cell.GetProperty("name").ValueKind != JsonValueKind.String) continue;
						int node = cell.GetProperty("score").GetProperty("node").GetInt32();
						if (!byNode.TryGetValue(node, out var l)) byNode[node] = l = new List<JsonElement>();
						l.Add(cell);
					}
					foreach (var kv in byNode.OrderBy(k => k.Key))
					{
						if (kv.Value.Count != 2 || kv.Value.Any(c => !string.IsNullOrEmpty(c.GetProperty("score").GetProperty("text").GetString()))) continue;
						list.Add(new DuelMatch { Kind = "table", Stage = stage, Set = set, Node = kv.Key,
							RefA = kv.Value[0].GetProperty("score").GetProperty("fencer").GetInt32(), RefB = kv.Value[1].GetProperty("score").GetProperty("fencer").GetInt32(),
							NameA = kv.Value[0].GetProperty("name").GetString(), NameB = kv.Value[1].GetProperty("name").GetString() });
					}
				}
			}
			return list;
		}

		/// <summary>Saisit le score d'un match ; le vainqueur est celui qui a le plus de points (une égalité est refusée).</summary>
		public async Task ScoreMatch(string contest, DuelMatch m, int scoreA, int scoreB)
		{
			if (scoreA == scoreB) throw new ManageCompException("Égalité : le match est à rejouer.");
			async Task One(int reference, int score, bool winner)
			{
				string value = winner ? "V" + score : score.ToString();
				object body = m.Kind == "pool"
					? new { action = "score", pool = m.Pool, match = m.Match, fencer = reference, value }
					: (object)new { action = "score", set = m.Set, node = m.Node, fencer = reference, value };
				await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{m.Stage}", body);
			}
			bool aWins = scoreA > scoreB;
			await One(m.RefA, scoreA, aWins);
			await One(m.RefB, scoreB, !aWins);
		}

		/// <summary>Passe à la phase suivante si la phase en cours est terminée. Retourne la nouvelle phase (« classification » = tournoi fini), ou null si elle n'est pas terminée.</summary>
		public async Task<string> TryAdvance(string contest)
		{
			try { await Send(HttpMethod.Post, $"/api/contests/{contest}/next"); }
			catch (ManageCompException) { return null; }          // « la phase en cours n'est pas terminée »
			return (await CurrentStage(contest)).kind;
		}

		/// <summary>Le classement final (phase « Classement général ») : lignes {cells:[place, statut, nom…]}.</summary>
		public async Task<JsonElement> FinalRanking(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int stage = FindStage(schedule, "classification");
			return (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");
		}

		/// <summary>Le classement cumulé de la phase de lobby (tel que ManageComp le calcule) et la composition des lobbys.</summary>
		public async Task<JsonElement> Stage(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int stage = FindStage(schedule, "lobby");
			return (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");
		}
	}
}
