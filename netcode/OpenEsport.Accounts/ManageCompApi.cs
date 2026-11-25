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
		/// <summary>Scores saisis (vides : match à jouer) et vainqueur (0, 1, ou -1).</summary>
		public string ScoreA = "", ScoreB = "";
		public int Winner = -1;
		/// <summary>Poules : division de la poule (0 : aucune). Tableau : taille du tour (2 : finale, 4 : demi-finales…) et place jouée (1 : tableau principal, 3 : match pour la 3e place…).</summary>
		public int Division, RoundSize, Place = 1;
		public bool Played => ScoreA.Length > 0 || ScoreB.Length > 0;
		public bool Known => RefA > 0 && RefB > 0;
		public string Key => Kind == "pool" ? $"p{Pool}m{Match}" : $"t{Set}n{Node}";
	}

	/// <summary>Une erreur renvoyée par l'API de ManageComp (ou un service injoignable).</summary>
	public sealed class ManageCompException : Exception
	{
		public ManageCompException(string message) : base(message) { }
	}

	/// <summary>
	/// Appels de l'API de ManageComp utilisés pour les tournois : une compétition par tournoi, ses participants à l'appel, une formule de phases
	/// (séries de lobbys dont chaque manche est une partie du jeu, poules, poules de division, tableaux, séparations en divisions), la saisie des places
	/// et des scores, les classements. Le <see cref="HttpClient"/> est fourni de l'extérieur pour pouvoir tester sans réseau.
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
		/// Remplace la formule par défaut (poules + tableau) par les phases du tournoi, dans l'ordre.
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

		/// <summary>
		/// Les phases du tournoi (lobbys, poules, tableaux, poules de division, séparations), dans l'ordre de la formule : la n-ième correspond à la n-ième étape.
		/// Des poules de division (« divisions ») sont désignées par le tour de poules qu'elles jouent ; leur répartition et sa composition sont juste avant.
		/// </summary>
		public async Task<List<(int index, string kind)>> PlayableStages(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			var list = new List<(int, string)>();
			bool division = false;
			foreach (var s in schedule.GetProperty("stages").EnumerateArray())
			{
				string kind = s.GetProperty("kind").GetString();
				int index = s.GetProperty("index").GetInt32();
				if (kind == "divisions") division = true;
				else if (kind == "pool_supervisor") { list.Add((index, division ? "divisions" : kind)); division = false; }
				else if (kind == "lobby" || kind == "table" || kind == "splitting") list.Add((index, kind));
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
					scale = EncodedScale(p), swapping = Array.Empty<string>(), team_best = 1, team_mode = "team", team_rule = "sum"
				},
				nb_qualified = !perLobby && p.Qualified > 0 ? p.Qualified : 0,
			});
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "composition", lobbies = Math.Max(1, lobbies), distribution = p.Distribution });
		}

		private const int MaxKills = 60;

		/// <summary>
		/// Barème d'une série. Avec des points par élimination, ManageComp (qui ne connaît que des places) reçoit un barème « une place = un score » :
		/// la place 1 vaut le score maximum, la suivante un point de moins… (<see cref="EncodedPlace"/> donne la place d'un score).
		/// </summary>
		public static List<int> EncodedScale(PhaseConfig p)
		{
			if (p.KillPoints <= 0) return p.Scale;
			int top = TopScore(p);
			return Enumerable.Range(0, top + 1).Select(i => top - i).ToList();
		}

		private static int TopScore(PhaseConfig p) => p.Scale.Max() + p.KillPoints * MaxKills;

		/// <summary>Points d'une manche : la place, et les éliminations.</summary>
		public static int RacePoints(PhaseConfig p, int place, int kills) => (place >= 1 && place <= p.Scale.Count ? p.Scale[place - 1] : 0) + p.KillPoints * Math.Clamp(kills, 0, MaxKills);

		/// <summary>La place à saisir dans ManageComp pour un résultat : la place elle-même, ou celle qui vaut son score.</summary>
		public static int EncodedPlace(PhaseConfig p, int place, int kills) => p.KillPoints <= 0 ? place : TopScore(p) - RacePoints(p, place, kills) + 1;

		/// <summary>Nombre de participants d'une série de lobbys (les qualifiés de la phase précédente).</summary>
		public async Task<int> LobbyCompetitors(string contest, int stage)
		{
			var data = (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data");
			return data.TryGetProperty("competitors", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
		}

		/// <summary>Composition des poules : la formule proposée dont le nombre de poules est le plus proche de celui voulu.</summary>
		public async Task ConfigurePoolSize(string contest, int stage, int poolSize)
		{
			var data = await StageData(contest, stage);
			int n = data.GetProperty("pools").EnumerateArray().Sum(x => x.GetProperty("fencers").GetArrayLength());
			int wanted = Math.Max(1, (n + poolSize - 1) / poolSize);
			int best = -1, distance = int.MaxValue;
			foreach (var c in data.GetProperty("configs").EnumerateArray())
			{
				int d = Math.Abs(c.GetProperty("nb_pools").GetInt32() - wanted);
				if (d < distance) { distance = d; best = c.GetProperty("index").GetInt32(); }
			}
			if (best >= 0) await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "config", index = best });
		}

		/// <summary>Poules de division : nombre de divisions (au moins deux participants chacune), poules par division, montées et descentes.</summary>
		public async Task ConfigureDivisions(string contest, int stage, PhaseConfig p)
		{
			var data = await StageData(contest, stage);
			int n = data.GetProperty("list").GetProperty("rows").GetArrayLength();
			int divisions = Math.Clamp(p.Divisions, 1, Math.Max(1, n / 2));
			int pools = p.PoolSize > 0 ? Math.Max(1, (n / divisions + p.PoolSize - 1) / p.PoolSize) : 1;
			await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new
			{
				action = "settings", settings = new { divisions, pools, up = p.Up, down = p.Down, start = p.Start, swapping = Array.Empty<string>() },
			});
		}

		/// <summary>
		/// Séparation en divisions au classement : la première division reste dans la compétition, chacune des autres en deviendra une nouvelle au tour suivant.
		/// Retourne les noms des divisions créées (la 2e, la 3e…) et, pour chaque participant, sa division (0 = la première) et son nom.
		/// </summary>
		public async Task<(List<string> names, Dictionary<int, (int division, string name)> players)> ConfigureSplit(string contest, int stage, PhaseConfig p)
		{
			int n = (await StageData(contest, stage)).GetProperty("list").GetProperty("rows").GetArrayLength();
			int count = Math.Clamp(p.Divisions, 1, Math.Max(1, n / 2));
			var sizes = new List<int>();
			for (int i = 0, left = n; i < count; i++)
			{
				int rest = count - i;
				int size = i == count - 1 ? left : Math.Min(p.Sizes.Count > i ? p.Sizes[i] : (left + rest - 1) / rest, left - 2 * (rest - 1));
				sizes.Add(size);
				left -= size;
			}
			var names = Enumerable.Range(2, Math.Max(1, count - 1)).Select(i => "Division " + i).ToList();
			var divisions = names.Select((name, i) => i + 1 < sizes.Count && i + 1 < count - 1 ? new { name, size = (object)new { count = sizes[i + 1] } } : new { name, size = (object)null }).ToList();
			var view = await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "config", config = new { mode = "ranking", keep = new { count = sizes[0] }, divisions } });
			var data = view.GetProperty("data");
			var list = data.GetProperty("list");
			int nameAt = ColumnOf(list, "name");
			var assignments = data.GetProperty("assignments");
			var players = new Dictionary<int, (int, string)>();
			foreach (var r in list.GetProperty("rows").EnumerateArray())
			{
				int reference = r.GetProperty("reference").GetInt32();
				players[reference] = (assignments.GetProperty(reference.ToString()).GetInt32(), r.GetProperty("cells")[nameAt].GetString());
			}
			return (names, players);
		}

		private static int ColumnOf(JsonElement list, string code)
		{
			int i = 0;
			foreach (var c in list.GetProperty("columns").EnumerateArray())
			{
				if (c.GetProperty("code").GetString() == code) return i;
				i++;
			}
			throw new ManageCompException("ManageComp : colonne « " + code + " » introuvable.");
		}

		/// <summary>Les compétitions du compte ManageComp : identifiant et nom.</summary>
		public async Task<List<(string id, string name)>> Contests()
			=> (await Send(HttpMethod.Get, "/api/contests")).EnumerateArray().Select(c => (c.GetProperty("id").GetString(), c.GetProperty("name").GetString())).ToList();

		/// <summary>Les participants à l'appel : nom → référence.</summary>
		public async Task<Dictionary<string, int>> CheckinNames(string contest)
		{
			var view = await Send(HttpMethod.Get, $"/api/contests/{contest}/checkin");
			var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			if (view.ValueKind != JsonValueKind.Object || !view.TryGetProperty("columns", out _)) return result;
			int nameAt = ColumnOf(view, "name");
			foreach (var f in view.GetProperty("fencers").EnumerateArray()) result[f.GetProperty("cells")[nameAt].GetString()] = f.GetProperty("reference").GetInt32();
			return result;
		}

		/// <summary>Règle des poules : score d'un match, qualifiés pour la suite.</summary>
		public async Task ConfigurePools(string contest, int stage, PhaseConfig p)
			=> await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "settings", max_score = p.MatchScore, nb_qualified = p.Qualified > 0 ? p.Qualified : (int?)null });

		/// <summary>Règle un tableau : score d'un match, places disputées, qualifiés pour la suite.</summary>
		public async Task ConfigureTable(string contest, int stage, PhaseConfig p)
			=> await Send(HttpMethod.Post, $"/api/contests/{contest}/stages/{stage}", new { action = "settings", max_score = p.MatchScore, fenced_places = p.Places, nb_qualified = p.Qualified > 0 ? p.Qualified : (int?)null });

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
			var (kind, stage) = await CurrentStage(contest);
			if (kind != "pool_supervisor" && kind != "table") return new List<DuelMatch>();
			return (await Matches(contest, stage, kind)).Where(m => m.Known && !m.Played).ToList();
		}

		/// <summary>Lecture d'un tableau : ManageComp n'en montre que trois tours à la fois et un seul tableau, on les fait défiler (une lecture à la fois).</summary>
		private readonly System.Threading.SemaphoreSlim _tableGate = new System.Threading.SemaphoreSlim(1);

		/// <summary>Tous les matchs d'un tour de poules ou d'un tableau (avec ses matchs de classement), joués ou non.</summary>
		public async Task<List<DuelMatch>> Matches(string contest, int stage, string kind)
		{
			string path = $"/api/contests/{contest}/stages/{stage}";
			if (kind != "table") return PoolMatches((await Send(HttpMethod.Get, path)).GetProperty("data"), stage);
			await _tableGate.WaitAsync();
			try
			{
				var found = new Dictionary<(int set, int node), DuelMatch>();
				var data = (await Send(HttpMethod.Get, path)).GetProperty("data");
				int shown = data.TryGetProperty("displayed", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
				var sets = data.TryGetProperty("sets", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(s => s.GetProperty("index").GetInt32()).ToList() : new List<int>();
				// le tableau principal, puis le match pour la 3e place et les matchs de classement (d'autres tableaux de la même phase)
				foreach (int set in sets.Where(s => s != shown).Prepend(shown))
				{
					if (set != shown) data = (await Send(HttpMethod.Post, path, new { action = "select", set })).GetProperty("data");
					TableMatches(data, stage, set, found);
					foreach (var (can, action) in new[] { ("can_previous", "previous"), ("can_next", "next") })
						for (int guard = 0; guard < 8 && Can(data, can); guard++)
						{
							data = (await Send(HttpMethod.Post, path, new { action, set })).GetProperty("data");
							TableMatches(data, stage, set, found);
						}
				}
				return found.Values.OrderBy(m => m.Place).ThenBy(m => m.Set).ThenBy(m => m.Node).ToList();
			}
			finally { _tableGate.Release(); }
		}

		private static bool Can(JsonElement data, string what)
			=> data.TryGetProperty("drawing", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty(what, out var v) && v.ValueKind == JsonValueKind.True;

		private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : "";

		/// <summary>Le vainqueur d'un match d'après les scores saisis (« V » ou « V8 » pour lui).</summary>
		private static int WinnerOf(string a, string b) => a.StartsWith("V", StringComparison.Ordinal) ? 0 : b.StartsWith("V", StringComparison.Ordinal) ? 1 : -1;

		private static List<DuelMatch> PoolMatches(JsonElement data, int stage)
		{
			var list = new List<DuelMatch>();
			foreach (var pool in data.GetProperty("pools").EnumerateArray())
			{
				// poules de division : « D2 · Poule n° 01 »
				string name = pool.TryGetProperty("name", out var pn) ? Text(pn) : "";
				int division = name.Length > 1 && name[0] == 'D' && char.IsDigit(name[1]) && int.TryParse(new string(name.Skip(1).TakeWhile(char.IsDigit).ToArray()), out int dv) ? dv : 0;
				foreach (var m in pool.GetProperty("matches").EnumerateArray())
				{
					var o = m.GetProperty("opponents").EnumerateArray().ToList();
					if (o.Count != 2 || o.Any(x => x.ValueKind != JsonValueKind.Object)) continue;
					var match = new DuelMatch
					{
						Kind = "pool", Stage = stage, Pool = pool.GetProperty("index").GetInt32(), Match = m.GetProperty("index").GetInt32(), Division = division,
						RefA = o[0].GetProperty("reference").GetInt32(), RefB = o[1].GetProperty("reference").GetInt32(), NameA = o[0].GetProperty("name").GetString(), NameB = o[1].GetProperty("name").GetString(),
						ScoreA = Text(o[0].GetProperty("score")), ScoreB = Text(o[1].GetProperty("score")),
					};
					match.Winner = WinnerOf(match.ScoreA, match.ScoreB);
					list.Add(match);
				}
			}
			return list;
		}

		private static void TableMatches(JsonElement data, int stage, int set, Dictionary<(int set, int node), DuelMatch> found)
		{
			if (!data.TryGetProperty("drawing", out var drawing) || drawing.ValueKind != JsonValueKind.Object) return;
			var byNode = new Dictionary<int, List<JsonElement>>();
			// le nom d'un match (« 1-8.3 ») dit la place jouée et la taille du tour
			var labels = new Dictionary<int, string>();
			foreach (var cell in drawing.GetProperty("cells").EnumerateArray())
			{
				if (cell.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object && cell.TryGetProperty("node", out var own) && own.ValueKind == JsonValueKind.Number) labels[own.GetInt32()] = Text(label.GetProperty("name"));
				if (cell.GetProperty("score").ValueKind != JsonValueKind.Object || cell.GetProperty("name").ValueKind != JsonValueKind.String) continue;
				int node = cell.GetProperty("score").GetProperty("node").GetInt32();
				if (!byNode.TryGetValue(node, out var l)) byNode[node] = l = new List<JsonElement>();
				l.Add(cell);
			}
			foreach (var kv in byNode)
			{
				if (kv.Value.Count != 2) continue;
				JsonElement a = kv.Value[0], b = kv.Value[1];
				var match = new DuelMatch
				{
					Kind = "table", Stage = stage, Set = set, Node = kv.Key,
					RefA = a.GetProperty("score").GetProperty("fencer").GetInt32(), RefB = b.GetProperty("score").GetProperty("fencer").GetInt32(), NameA = a.GetProperty("name").GetString(), NameB = b.GetProperty("name").GetString(),
					ScoreA = Text(a.GetProperty("score").GetProperty("text")), ScoreB = Text(b.GetProperty("score").GetProperty("text")),
				};
				if (labels.TryGetValue(kv.Key, out string name))
				{
					var parts = name.Split('-', '.');
					if (parts.Length == 3 && int.TryParse(parts[0], out int place) && int.TryParse(parts[1], out int size)) { match.Place = place; match.RoundSize = size; }
				}
				match.Winner = WinnerOf(match.ScoreA, match.ScoreB);
				found[(set, kv.Key)] = match;
			}
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

		/// <summary>Le classement final (phase « Classement général ») : place et nom.</summary>
		public async Task<List<(string place, string name)>> FinalRanking(string contest)
		{
			var schedule = await Send(HttpMethod.Get, $"/api/contests/{contest}/schedule");
			int stage = FindStage(schedule, "classification");
			var list = (await Send(HttpMethod.Get, $"/api/contests/{contest}/stages/{stage}")).GetProperty("data").GetProperty("list");
			int placeAt = ColumnOf(list, "final_rank"), nameAt = ColumnOf(list, "name");
			return list.GetProperty("rows").EnumerateArray().Select(r => (Text(r.GetProperty("cells")[placeAt]), Text(r.GetProperty("cells")[nameAt]))).ToList();
		}
	}
}
