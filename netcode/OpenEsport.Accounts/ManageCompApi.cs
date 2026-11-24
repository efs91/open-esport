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
	/// Appels de l'API de ManageComp utilisés pour les tournois : une compétition par tournoi, ses participants à l'appel, une formule de phases
	/// (séries de lobbys dont chaque manche est une partie du jeu, poules et tableau de duels), la saisie des places et des scores, les classements. Le <see cref="HttpClient"/> est fourni de l'extérieur
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

		/// <summary>Crée la compétition d'un tournoi (à l'appel) ; sa formule est construite au lancement, d'après la configuration du moment.</summary>
		public async Task<string> CreateContest(string name, string date)
		{
			var created = await Send(HttpMethod.Post, "/api/contests", new { name, organizer = "Open Esport", location = "En ligne", weapon = "X", gender = "X", category = 9, date });
			return created.GetProperty("id").GetString();
		}

		/// <summary>
		/// Remplace la formule par défaut (poules + tableau) par les phases du tournoi, dans l'ordre : séries de lobbys, poules, tableau.
		/// L'appel et le classement général restent au début et à la fin.
		/// </summary>
		public async Task BuildFormula(string contest, IReadOnlyList<PhaseConfig> phases)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			var removable = new List<int>();
			if (schedule.ValueKind == JsonValueKind.Object && schedule.TryGetProperty("stages", out var stages))
				foreach (var s in stages.EnumerateArray())
				{
					string kind = s.GetProperty("kind").GetString();
					if (kind != "checkin" && kind != "classification" && kind != "pool_allocator") removable.Add(s.GetProperty("index").GetInt32());
				}
			// de la fin vers le début (les indices ne bougent pas) ; une phase de poules emporte sa composition
			foreach (int index in removable.OrderByDescending(i => i)) await Send(HttpMethod.Delete, $"/api/contests/{contest}/schedule/stages/{index}");
			foreach (var p in phases) await Send(HttpMethod.Post, $"/api/contests/{contest}/schedule/stages", new { kind = p.ManageCompKind });
		}

		private static int FindStage(JsonElement schedule, string kind)
		{
			foreach (var s in schedule.GetProperty("stages").EnumerateArray())
				if (s.GetProperty("kind").GetString() == kind) return s.GetProperty("index").GetInt32();
			throw new ManageCompException("ManageComp : phase « " + kind + " » introuvable.");
		}

		/// <summary>Les phases jouées (lobbys, poules, tableau), dans l'ordre de la formule : la n-ième correspond à la n-ième phase du tournoi.</summary>
		public async Task<List<(int index, string kind)>> PlayableStages(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			var list = new List<(int, string)>();
			foreach (var s in schedule.GetProperty("stages").EnumerateArray())
			{
				string kind = s.GetProperty("kind").GetString();
				if (kind == "lobby" || kind == "pool_supervisor" || kind == "table") list.Add((s.GetProperty("index").GetInt32(), kind));
			}
			return list;
		}

		/// <summary>Passe à la phase suivante (« Tour suivant ») ; erreur si la phase en cours n'est pas terminée.</summary>
		public async Task Next(string contest) => await Send(HttpMethod.Post, $"/api/contests/{contest}/next");

		/// <summary>Règle une série de lobbys (manches, points, qualifiés) et répartit ses joueurs en <paramref name="lobbies"/> lobbys.</summary>
		public async Task ConfigureLobby(string contest, int stage, PhaseConfig p, int lobbies)
		{
			bool perLobby = p.QualifyMode == "lobby" && p.Qualified > 0;
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new
			{
				action = "settings",
				settings = new
				{
					abandon_points = p.AbandonPoints, distribution = p.Distribution, lobbies = Math.Max(1, lobbies), per_lobby = perLobby ? p.Qualified : (int?)null, races = p.Races,
					scale = p.Scale, swapping = Array.Empty<string>(), team_best = 1, team_mode = "team", team_rule = "sum"
				},
				nb_qualified = !perLobby && p.Qualified > 0 ? p.Qualified : 0,
			});
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "composition", lobbies = Math.Max(1, lobbies), distribution = p.Distribution });
		}

		/// <summary>Nombre de participants d'une série de lobbys (les qualifiés de la phase précédente).</summary>
		public async Task<int> LobbyCompetitors(string contest, int stage)
		{
			var data = (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");
			return data.TryGetProperty("competitors", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
		}

		/// <summary>Règle des poules : victimes pour gagner, qualifiés pour la suite.</summary>
		public async Task ConfigurePools(string contest, int stage, PhaseConfig p)
			=> await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "settings", max_score = p.MaxScore, nb_qualified = p.Qualified > 0 ? p.Qualified : (int?)null });

		/// <summary>Règle un tableau : victimes pour gagner, places disputées.</summary>
		public async Task ConfigureTable(string contest, int stage, PhaseConfig p)
			=> await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "settings", max_score = p.MaxScore, fenced_places = p.Places });

		/// <summary>Les données d'une phase (lobbys et classement d'une série, poules, tableau).</summary>
		public async Task<JsonElement> StageData(string contest, int stage) => (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");

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

		/// <summary>Saisit la place d'un joueur dans une manche (0 = première manche de la phase) d'un lobby de la phase <paramref name="stage"/>. Place 0 ou moins : abandon.</summary>
		public async Task SetFinish(string contest, int stage, int lobbyIndex, int race, int reference, int place)
			=> await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "finish", lobby = lobbyIndex, race, player = reference, value = place > 0 ? place.ToString() : "A" });

		// ------------------------------------------------------------------ duels (poules puis tableaux)

		public async Task<(string kind, int index)> CurrentStage(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int current = schedule.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
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

	}
}
