using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenEsport.Accounts
{
	/// <summary>Résultat d'une opération de tournoi : un message d'erreur en français, ou null.</summary>
	public sealed class TournamentResult
	{
		public string Error;
		public long Id;
		public static TournamentResult Fail(string message) => new TournamentResult { Error = message };
		public static TournamentResult Ok(long id = 0) => new TournamentResult { Id = id };
	}

	/// <summary>Le match qu'un joueur doit jouer : sa clé, son camp (0 ou 1) et les noms des deux camps.</summary>
	public sealed class MyMatch
	{
		public string Key;
		public int Side;
		public string Mine, Opponent;
	}

	/// <summary>
	/// Les tournois du jeu. L'organisation (inscrits, manches ou matchs, places ou scores, classement) est faite par ManageComp ; ce service garde le lien
	/// entre un tournoi du jeu et sa compétition, applique les règles (qui peut créer, s'inscrire, lancer) et pousse les résultats des parties.
	/// Deux formats : <c>br</c> (battle royale en N manches, phase « Lobby (série) », points par place) et <c>duel</c> (1 contre 1 ou 2 contre 2 :
	/// poules puis tableau, chaque match est une partie de match à mort par équipe jouée jusqu'à un nombre de victimes).
	/// </summary>
	public sealed class TournamentService
	{
		public const int MaxRaces = 10, MaxOpenPerOwner = 3, DefaultLobbySize = 20, MaxPlayers = 100, DefaultScoreLimit = 10;
		public static readonly TimeSpan MaxAdvance = TimeSpan.FromDays(60);

		private readonly AccountStore _store;
		private readonly ManageCompApi _api;

		public bool Configured => _api.Configured;

		public TournamentService(AccountStore store, ManageCompApi api) { _store = store; _api = api; }

		private static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("dd/MM/yyyy");
		private static bool IsDuel(TournamentRow t) => t.Mode == "duel";

		/// <summary>Crée un tournoi. <paramref name="mode"/> : « br » (N manches) ou « duel » (<paramref name="teamSize"/> 1 ou 2, <paramref name="scoreLimit"/> victimes pour gagner un match).</summary>
		public async Task<TournamentResult> Create(long owner, string title, long startAt, int races, int lobbySize, string mode = "br", int teamSize = 1, int scoreLimit = 0)
		{
			if (!_api.Configured) return TournamentResult.Fail("Les tournois ne sont pas encore ouverts sur ce serveur.");
			title = (title ?? "").Trim();
			if (title.Length < 3 || title.Length > 60) return TournamentResult.Fail("Le nom du tournoi doit faire entre 3 et 60 caractères.");
			long now = AccountStore.Now();
			if (startAt < now - 300) return TournamentResult.Fail("La date de début est déjà passée.");
			if (startAt > now + (long)MaxAdvance.TotalSeconds) return TournamentResult.Fail("Un tournoi se programme au plus 60 jours à l'avance.");
			if (mode != "br" && mode != "duel") return TournamentResult.Fail("Format de tournoi inconnu.");
			bool duel = mode == "duel";
			if (duel)
			{
				if (teamSize < 1 || teamSize > 2) return TournamentResult.Fail("Un duel se joue à 1 contre 1 ou 2 contre 2.");
				scoreLimit = scoreLimit <= 0 ? DefaultScoreLimit : Math.Clamp(scoreLimit, 3, 30);
				races = 0;
			}
			else
			{
				if (races < 1 || races > MaxRaces) return TournamentResult.Fail($"Le nombre de manches va de 1 à {MaxRaces}.");
				teamSize = Math.Clamp(teamSize, 1, 5); scoreLimit = 0;
			}
			lobbySize = lobbySize <= 0 ? DefaultLobbySize : Math.Clamp(lobbySize, 2, 30);
			if (_store.OpenTournamentsOf(owner) >= MaxOpenPerOwner) return TournamentResult.Fail($"Tu organises déjà {MaxOpenPerOwner} tournois : termine-en un d'abord.");
			try
			{
				string contest = duel ? await _api.CreateDuelTournament(title, Date(startAt), scoreLimit) : await _api.CreateTournament(title, Date(startAt), races);
				long id = _store.CreateTournament(contest, title, mode, teamSize, races, lobbySize, startAt, owner, scoreLimit);
				return TournamentResult.Ok(id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>Inscrit un joueur ; en duel 2 contre 2, <paramref name="team"/> est le groupe de deux joueurs (le chef inscrit l'équipe).</summary>
		public async Task<TournamentResult> Join(long tournament, long account, string login, IReadOnlyList<(long id, string login)> team = null)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Les inscriptions sont closes.");
			var players = _store.TournamentPlayers(t.Id);
			if (players.Any(p => p.account == account)) return TournamentResult.Ok(t.Id);
			var members = new List<(long id, string login)> { (account, login) };
			if (t.TeamSize > 1)
			{
				if (team == null || team.Count != t.TeamSize || team[0].id != account) return TournamentResult.Fail($"Ce tournoi se joue par équipes de {t.TeamSize} : forme un groupe de {t.TeamSize} joueurs, le chef inscrit l'équipe.");
				if (team.Skip(1).Any(m => players.Any(p => p.account == m.id))) return TournamentResult.Fail("Un de tes coéquipiers est déjà inscrit.");
				members = team.ToList();
			}
			if (players.Select(p => p.reference).Distinct().Count() >= MaxPlayers) return TournamentResult.Fail("Le tournoi est complet.");
			try
			{
				int reference = await _api.AddPlayer(t.Contest, string.Join(" & ", members.Select(m => m.login)));
				foreach (var m in members) _store.AddTournamentPlayer(t.Id, m.id, reference);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		public async Task<TournamentResult> Leave(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé : tu ne peux plus te désinscrire.");
			var all = _store.TournamentPlayers(t.Id);
			var mine = all.FirstOrDefault(p => p.account == account);
			if (mine.reference == 0) return TournamentResult.Ok(t.Id);
			try { await _api.RemovePlayer(t.Contest, mine.reference); }
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
			foreach (var p in all.Where(p => p.reference == mine.reference)) _store.RemoveTournamentPlayer(t.Id, p.account);       // toute l'équipe
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>L'organisateur ferme les inscriptions : lobbys (battle royale) ou poules (duels) sont composés par ManageComp, les parties peuvent être jouées.</summary>
		public async Task<TournamentResult> Start(long tournament, long who)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.Owner != who) return TournamentResult.Fail("Seul l'organisateur peut lancer le tournoi.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a déjà commencé.");
			int n = _store.TournamentPlayers(t.Id).Select(p => p.reference).Distinct().Count();
			if (IsDuel(t) ? n < 2 : n < 1) return TournamentResult.Fail(IsDuel(t) ? "Il faut au moins deux inscrits (ou équipes)." : "Aucun inscrit.");
			try
			{
				if (IsDuel(t)) await _api.BeginDuels(t.Contest, t.ScoreLimit);
				else await _api.BeginLobby(t.Contest, (n + t.LobbySize - 1) / t.LobbySize);
				_store.SetTournamentState(t.Id, "running", 0);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		// ------------------------------------------------------------------ battle royale

		/// <summary>
		/// Le serveur d'une partie de tournoi rapporte les places d'une manche (1 = vainqueur). Seule la manche en cours est acceptée ; les places sont
		/// saisies dans le lobby de chaque joueur. Un rapport renvoyé deux fois est sans effet.
		/// </summary>
		public async Task<TournamentResult> ReportRace(long tournament, int race, int lobby, IReadOnlyList<(long account, int place)> placements)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (IsDuel(t)) return TournamentResult.Fail("Ce tournoi se joue en duels.");
			if (t.State != "running" || race != t.Race) return TournamentResult.Fail("Cette manche n'est pas en cours.");
			var refs = _store.TournamentPlayers(t.Id).ToDictionary(p => p.account, p => p.reference);
			try
			{
				foreach (var (account, place) in placements)
					if (refs.TryGetValue(account, out int reference)) await _api.SetFinish(t.Contest, lobby, race, reference, place);
				// la manche est jouée pour ce lobby ; le tournoi avance quand tous les lobbys l'ont rapportée (un seul lobby : tout de suite)
				var stage = await _api.Stage(t.Contest);
				bool allDone = stage.GetProperty("lobbies").EnumerateArray().All(l => l.GetProperty("rows").EnumerateArray().All(r => RaceFilled(r, race)));
				if (allDone) _store.SetTournamentState(t.Id, race + 1 >= t.Races ? "done" : "running", race + 1);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		private static bool RaceFilled(JsonElement row, int race)
		{
			var finishes = row.GetProperty("finishes");
			return race < finishes.GetArrayLength() && finishes[race].ValueKind != JsonValueKind.Null;
		}

		/// <summary>Le lobby de chaque inscrit (compte → numéro de lobby) d'après la composition faite par ManageComp ; vide tant que le tournoi n'a pas commencé.</summary>
		public async Task<Dictionary<long, int>> Lobbies(long tournament)
		{
			var result = new Dictionary<long, int>();
			var t = _store.Tournament(tournament);
			if (t == null || t.State == "open" || IsDuel(t)) return result;
			var byRef = _store.TournamentPlayers(t.Id).GroupBy(p => p.reference).ToDictionary(g => g.Key, g => g.Select(x => x.account).ToList());
			try
			{
				var stage = await _api.Stage(t.Contest);
				foreach (var l in stage.GetProperty("lobbies").EnumerateArray())
				{
					int index = l.GetProperty("index").GetInt32();
					foreach (var row in l.GetProperty("rows").EnumerateArray())
						if (byRef.TryGetValue(row.GetProperty("reference").GetInt32(), out var accs)) foreach (var acc in accs) result[acc] = index;
				}
			}
			catch (ManageCompException) { }
			return result;
		}

		// ------------------------------------------------------------------ duels

		/// <summary>Le match en attente d'un joueur (le premier dans l'ordre de ManageComp), ou null s'il n'a rien à jouer pour l'instant.</summary>
		public async Task<MyMatch> NextMatch(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running" || !IsDuel(t)) return null;
			int mine = _store.TournamentPlayers(t.Id).FirstOrDefault(p => p.account == account).reference;
			if (mine == 0) return null;
			try
			{
				var m = (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.RefA == mine || x.RefB == mine);
				if (m == null) return null;
				bool a = m.RefA == mine;
				return new MyMatch { Key = m.Key, Side = a ? 0 : 1, Mine = a ? m.NameA : m.NameB, Opponent = a ? m.NameB : m.NameA };
			}
			catch (ManageCompException) { return null; }
		}

		/// <summary>Les comptes de chaque camp d'un match en attente (null si le match n'existe plus : déjà joué ou pas encore connu) ; sert au serveur de partie.</summary>
		public async Task<(List<long> a, List<long> b, int scoreLimit, int teamSize)?> MatchSides(long tournament, string key)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running" || !IsDuel(t)) return null;
			try
			{
				var m = (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.Key == key);
				if (m == null) return null;
				var players = _store.TournamentPlayers(t.Id);
				return (players.Where(p => p.reference == m.RefA).Select(p => p.account).ToList(), players.Where(p => p.reference == m.RefB).Select(p => p.account).ToList(), t.ScoreLimit, t.TeamSize);
			}
			catch (ManageCompException) { return null; }
		}

		/// <summary>Le serveur rapporte le score d'un match (camp A = camp 0). Une égalité n'est pas enregistrée (le match reste à jouer). Le tournoi avance de phase quand tous les matchs sont joués.</summary>
		public async Task<TournamentResult> ReportMatch(long tournament, string key, int scoreA, int scoreB)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (!IsDuel(t)) return TournamentResult.Fail("Ce tournoi se joue en manches de battle royale.");
			if (t.State != "running") return TournamentResult.Fail("Le tournoi n'est pas en cours.");
			try
			{
				var m = (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.Key == key);
				if (m == null) return TournamentResult.Fail("Ce match n'est pas à jouer (déjà saisi ?).");
				await _api.ScoreMatch(t.Contest, m, scoreA, scoreB);
				// phase terminée : poules → tableau → classement
				for (int guard = 0; guard < 4; guard++)
				{
					if ((await _api.PendingMatches(t.Contest)).Count > 0) break;
					string next = await _api.TryAdvance(t.Contest);
					if (next == null) break;
					if (next == "classification") { _store.SetTournamentState(t.Id, "done", t.Race); break; }
				}
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>Ce que le joueur doit jouer maintenant (manche de battle royale avec son lobby, ou match de duel) ; null s'il n'y a rien à jouer.</summary>
		public async Task<object> MyRace(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running") return null;
			if (IsDuel(t))
			{
				var m = await NextMatch(tournament, account);
				return m == null ? null : new { kind = "duel", tournament = t.Id, match = m.Key, side = m.Side, mine = m.Mine, opponent = m.Opponent, teamSize = t.TeamSize };
			}
			var lobbies = await Lobbies(tournament);
			if (!lobbies.TryGetValue(account, out int lobby)) return null;
			return new { kind = "br", tournament = t.Id, race = t.Race, races = t.Races, lobby, teamSize = t.TeamSize };
		}

		/// <summary>Détails d'un tournoi pour les joueurs : inscrits et classement de ManageComp.</summary>
		public async Task<object> Details(long tournament, long viewer, Func<long, string> login)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return null;
			var players = _store.TournamentPlayers(t.Id);
			JsonElement ranking = default;
			if (t.State != "open" && _api.Configured)
			{
				try { ranking = IsDuel(t) ? (t.State == "done" ? (await _api.FinalRanking(t.Contest)).GetProperty("classification").GetProperty("rows") : default) : (await _api.Stage(t.Contest)).GetProperty("ranking"); }
				catch (ManageCompException) { }
			}
			return new
			{
				id = t.Id, title = t.Title, mode = t.Mode, teamSize = t.TeamSize, scoreLimit = t.ScoreLimit, races = t.Races, race = t.Race, state = t.State, startAt = t.StartAt, lobbySize = t.LobbySize,
				isOwner = t.Owner == viewer, owner = login(t.Owner), joined = players.Any(p => p.account == viewer),
				players = players.Select(p => login(p.account)), ranking = ranking.ValueKind == JsonValueKind.Undefined ? (object)Array.Empty<object>() : ranking,
				standings = Standings(t, ranking),
			};
		}

		/// <summary>Le classement sous une forme commune aux deux formats : place, nom et détail (points cumulés pour le battle royale).</summary>
		private static List<object> Standings(TournamentRow t, JsonElement ranking)
		{
			var list = new List<object>();
			if (ranking.ValueKind != JsonValueKind.Array) return list;
			foreach (var r in ranking.EnumerateArray())
			{
				try
				{
					if (IsDuel(t))
					{
						var cells = r.GetProperty("cells");
						list.Add(new { place = cells[0].GetString(), name = cells[2].GetString(), detail = "" });
					}
					else
					{
						var st = r.GetProperty("standing");
						list.Add(new { place = r.GetProperty("place").GetInt32().ToString(), name = r.GetProperty("name").GetString(), detail = st.GetProperty("points").GetInt32() + " pts" });
					}
				}
				catch (Exception e) when (e is KeyNotFoundException || e is InvalidOperationException || e is IndexOutOfRangeException) { }
			}
			return list;
		}

		public object List(long viewer, Func<long, string> login)
		{
			var mine = viewer == 0 ? new HashSet<long>() : _store.TournamentsOf(viewer).ToHashSet();
			return _store.Tournaments().Select(t => new
			{
				id = t.Id, title = t.Title, mode = t.Mode, teamSize = t.TeamSize, scoreLimit = t.ScoreLimit, races = t.Races, race = t.Race, state = t.State, startAt = t.StartAt, owner = login(t.Owner),
				players = _store.TournamentPlayers(t.Id).Select(p => p.reference).Distinct().Count(), joined = mine.Contains(t.Id), isOwner = viewer != 0 && t.Owner == viewer, lobbySize = t.LobbySize,
			});
		}
	}
}
