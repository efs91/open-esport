using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Accounts
{
	/// <summary>
	/// Les modèles de tournoi : ceux du jeu (<see cref="BuiltIn"/>), ceux d'un joueur — privés par défaut — et ceux que les autres ont rendus publics.
	/// Un modèle est une configuration sans dates : on le choisit, on donne un nom et une heure, et le tournoi est prêt.
	/// </summary>
	public sealed class TournamentTemplates
	{
		public const int MaxPerAccount = 30;
		private readonly AccountStore _store;

		public TournamentTemplates(AccountStore store) { _store = store; }

		// ------------------------------------------------------------------ modèles du jeu

		/// <summary>Barème « place + éliminations » des grandes compétitions de battle royale par équipes : 12 points au vainqueur, 1 par élimination.</summary>
		private static List<int> Placement => new List<int>(ManageCompApi.PlacementPoints);
		/// <summary>Barème où la victoire pèse lourd : 65 points au vainqueur, 4 par élimination.</summary>
		private static List<int> VictoryHeavy => new List<int> { 65, 54, 48, 44, 40, 36, 33, 30, 27, 24, 20, 16, 12, 8, 4 };
		private static List<int> Arena => new List<int> { 10, 8, 6, 5, 4, 3, 2, 1 };

		private static PhaseConfig Br(int races, int lobby, List<int> scale, int kills, int qualified = 0) => new PhaseConfig { Kind = "lobby", Mode = "br", Races = races, LobbySize = lobby, Scale = scale, KillPoints = kills, Qualified = qualified };
		private static PhaseConfig Match(string kind, string mode, int score, int bestOf, int qualified = 0) => new PhaseConfig { Kind = kind, Mode = mode, MaxScore = score, BestOf = bestOf, PoolSize = kind == "pools" ? 4 : 0, Qualified = qualified };
		private static TournamentConfig Config(int teamSize, int min, int max, params PhaseConfig[] phases) => new TournamentConfig { TeamSize = teamSize, MinEntries = min, MaxEntries = max, CheckInMinutes = 15, Phases = phases.ToList() };

		/// <summary>
		/// Les modèles proposés à tous : séries de battle royale aux barèmes habituels des tournois du genre, poules puis tableau dans les modes à matchs
		/// (Sabotage : premier à 13 manches, matchs en deux parties gagnantes dans le tableau), ligue et championnat en divisions.
		/// Le jeu les nomme d'après leurs modes, dans sa langue.
		/// </summary>
		public static readonly (string key, string name, TournamentConfig config)[] BuiltIn =
		{
			("br-solo", "Battle Royale en solo", Config(1, 4, 60, Br(3, 20, Placement, 1, 20), Br(6, 20, Placement, 1))),
			("br-duo", "Battle Royale en duos", Config(2, 4, 45, Br(3, 15, VictoryHeavy, 4, 15), Br(6, 15, VictoryHeavy, 4))),
			("br-trio", "Battle Royale en trios", Config(3, 4, 30, Br(3, 10, Placement, 1, 10), Br(6, 10, Placement, 1))),
			("br-div", "Battle Royale en divisions", Config(1, 8, 60, Br(3, 20, Placement, 1), new PhaseConfig { Kind = "split", Divisions = 3 }, Br(5, 20, Placement, 1))),
			("dm", "Deathmatch", Config(1, 4, 40,
				new PhaseConfig { Kind = "lobby", Mode = "dm", Races = 3, LobbySize = 10, MaxScore = 20, Scale = Arena, Qualified = 10 },
				new PhaseConfig { Kind = "lobby", Mode = "dm", Races = 5, LobbySize = 10, MaxScore = 20, Scale = Arena })),
			("1v1", "1 VS 1", Config(1, 4, 32, Match("pools", "1v1", 5, 1, 16), Match("table", "1v1", 5, 3))),
			("sab", "Sabotage", Config(5, 2, 16, Match("pools", "sab", 13, 1, 8), Match("table", "sab", 13, 3))),
			("tdm", "Team Deathmatch", Config(4, 2, 16, Match("pools", "tdm", 50, 1, 8), Match("table", "tdm", 50, 3))),
			("ctl", "Contrôle", Config(5, 2, 16, Match("pools", "ctl", 100, 1, 8), Match("table", "ctl", 100, 1))),
			("ctf", "Capture du drapeau", Config(4, 2, 16, Match("pools", "ctf", 3, 1, 8), Match("table", "ctf", 3, 3))),
			("league", "Ligue en divisions", Config(5, 4, 24,
				new PhaseConfig { Kind = "divisions", Mode = "sab", MaxScore = 7, Divisions = 3, Up = 1, Down = 1 },
				new PhaseConfig { Kind = "divisions", Mode = "sab", MaxScore = 7, Divisions = 3, Up = 1, Down = 1 })),
		};

		// ------------------------------------------------------------------ modèles des joueurs

		private object View(TemplateRow t, long viewer) => new { id = t.Id, name = t.Name, owner = _store.FindById(t.Owner)?.Login ?? "?", mine = t.Owner == viewer, shared = t.Public, config = TournamentConfig.FromJson(t.Config) };

		public object List(long viewer) => new
		{
			builtIn = BuiltIn.Select(b => new { id = 0L, key = b.key, name = b.name, config = b.config }),
			mine = viewer == 0 ? new List<object>() : _store.TemplatesOf(viewer).Select(t => View(t, viewer)).ToList(),
			shared = _store.PublicTemplates(viewer).Select(t => View(t, viewer)).ToList(),
		};

		/// <summary>Enregistre un modèle (privé sauf demande contraire), ou remplace un des siens. Un nom déjà pris par un de ses modèles le remplace.</summary>
		public TournamentResult Save(long owner, long id, string name, bool shared, TournamentConfig cfg)
		{
			name = (name ?? "").Trim();
			if (name.Length < 3 || name.Length > 40) return TournamentResult.Fail("Le nom d'un modèle doit faire entre 3 et 40 caractères.");
			if (cfg == null) return TournamentResult.Fail("Configuration manquante.");
			cfg = cfg.Copy();
			cfg.Title = name;
			string error = cfg.ValidateTemplate();
			if (error != null) return TournamentResult.Fail(error);
			cfg.Title = "";
			var mine = _store.TemplatesOf(owner);
			if (id > 0 && !mine.Any(t => t.Id == id)) return TournamentResult.Fail("Ce modèle n'existe plus.");
			if (id <= 0) id = mine.FirstOrDefault(t => string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
			if (id <= 0 && mine.Count >= MaxPerAccount) return TournamentResult.Fail($"Tu as déjà {MaxPerAccount} modèles : supprimes-en un d'abord.");
			return TournamentResult.Ok(_store.SaveTemplate(id, owner, name, cfg.ToJson(), shared));
		}

		/// <summary>Rend un de ses modèles public (visible par tous les joueurs) ou privé.</summary>
		public TournamentResult Share(long owner, long id, bool shared)
		{
			var t = _store.Template(id);
			if (t == null || t.Owner != owner) return TournamentResult.Fail("Ce modèle n'existe plus.");
			return TournamentResult.Ok(_store.SaveTemplate(id, owner, t.Name, t.Config, shared));
		}

		public TournamentResult Delete(long owner, long id)
		{
			var t = _store.Template(id);
			if (t == null || t.Owner != owner) return TournamentResult.Fail("Ce modèle n'existe plus.");
			_store.DeleteTemplate(id);
			return TournamentResult.Ok(id);
		}
	}
}
