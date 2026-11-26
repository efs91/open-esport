using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenEsport.Core;

namespace OpenEsport.Accounts
{
	/// <summary>
	/// Une étape d'un tournoi, jouée dans une phase ManageComp. Son déroulement (<see cref="Kind"/>) :
	/// <list type="bullet">
	/// <item><c>lobby</c> : série de manches dans un ou plusieurs lobbys, points par place (et par élimination), classement cumulé ;</item>
	/// <item><c>pools</c> : poules, chacun contre chacun ;</item>
	/// <item><c>table</c> : tableau à élimination directe ;</item>
	/// <item><c>divisions</c> : poules de division (D1, D2…), avec montées et descentes d'une étape de division à la suivante ;</item>
	/// <item><c>split</c> : séparation en divisions d'après le classement ; chacune poursuit la suite de la formule de son côté (pas de partie jouée ici).</item>
	/// </list>
	/// Chaque étape jouée a son mode de jeu (<see cref="Mode"/>) : battle royale et deathmatch en séries ; 1 VS 1 en poules et tableau ;
	/// les modes à deux équipes (<see cref="TwoTeamModes"/>) en tout.
	/// Les équipes d'une étape sont celles de l'inscription (<c>entry</c>), ou tirées au sort à chaque manche parmi des inscrits en solo (<c>random</c>).
	/// Les qualifiés d'une étape passent à la suivante (sélection) ; la répartition « par niveau » met les meilleurs ensemble dans le lobby 1.
	/// </summary>
	public sealed class PhaseConfig
	{
		/// <summary>Modes à deux équipes : Team Deathmatch, Capture du drapeau, Sabotage, Contrôle.</summary>
		public static readonly string[] TwoTeamModes = { "tdm", "ctf", "sab", "ctl" };
		public static readonly string[] Kinds = { "lobby", "pools", "table", "divisions", "split" };

		public string Kind { get; set; } = "lobby";
		/// <summary>Mode de jeu : <c>br</c>, <c>dm</c>, <c>1v1</c>, <c>tdm</c>, <c>ctf</c>, <c>sab</c>, <c>ctl</c> (vide : déduit de <see cref="Kind"/>, anciens tournois).</summary>
		public string Mode { get; set; } = "";
		/// <summary>Nom affiché (vide : « Phase 1 », « Finale »…).</summary>
		public string Name { get; set; } = "";

		// ---- équipes de la phase
		/// <summary><c>entry</c> : les équipes inscrites ; <c>random</c> : équipes tirées au sort à chaque manche (inscription en solo).</summary>
		public string Teams { get; set; } = "entry";
		/// <summary>Joueurs par équipe tirée au sort (<see cref="Teams"/> = <c>random</c>).</summary>
		public int TeamSize { get; set; } = 1;

		// ---- série de lobbys (battle royale, Capture du drapeau)
		public int Races { get; set; } = 5;
		/// <summary>Inscrits par lobby : le nombre de lobbys en découle. Dans un mode à deux équipes, il est fixé par la taille des équipes (deux équipes par lobby).</summary>
		public int LobbySize { get; set; } = 20;
		/// <summary>Points par place (1er, 2e…) ; au-delà, 0.</summary>
		public List<int> Scale { get; set; } = new List<int>(ManageCompApi.PlacementPoints);
		public int AbandonPoints { get; set; }
		/// <summary>Points par élimination, ajoutés aux points de la place (battle royale, deathmatch ; une équipe additionne celles de ses joueurs). 0 = aucun.</summary>
		public int KillPoints { get; set; }
		/// <summary><c>balanced</c> : lobbys de force égale (serpentin) ; <c>level</c> : divisions, les meilleurs ensemble dans le lobby 1.</summary>
		public string Distribution { get; set; } = "balanced";

		// ---- qualification vers la phase suivante
		/// <summary><c>total</c> : les N meilleurs au classement fusionné ; <c>lobby</c> : les N meilleurs de chaque lobby (ou de chaque poule).</summary>
		public string QualifyMode { get; set; } = "total";
		/// <summary>Qualifiés pour la phase suivante (0 = tous).</summary>
		public int Qualified { get; set; }

		// ---- matchs (poules, tableau) et Capture du drapeau
		/// <summary>Score pour gagner une partie : manches (1 VS 1, Sabotage), prises de drapeau, éliminations (deathmatch), points (Contrôle).</summary>
		public int MaxScore { get; set; } = 10;
		/// <summary>Parties d'un match de poule ou de tableau : 1, ou le premier à 2 sur 3, à 3 sur 5.</summary>
		public int BestOf { get; set; } = 1;
		/// <summary>Poules : participants par poule (0 : choisi par ManageComp).</summary>
		public int PoolSize { get; set; }
		/// <summary>Tableau : places disputées (<c>third</c> : match pour la 3e place, <c>all</c> : toutes, <c>none</c> : aucune).</summary>
		public string Places { get; set; } = "third";
		/// <summary>1 VS 1 : équipement (<c>random</c> au hasard à chaque manche, <c>choice</c> choisi au début, <c>fixed:…</c> imposé).</summary>
		public string Loadout { get; set; } = "random";

		// ---- divisions (poules de division, séparation)
		/// <summary>Nombre de divisions (au plus une pour deux participants).</summary>
		public int Divisions { get; set; } = 2;
		/// <summary>Séparation : participants de chaque division, de la première à la dernière (vide : parts égales).</summary>
		public List<int> Sizes { get; set; } = new List<int>();
		/// <summary>Poules de division : montées et descentes d'une étape de division à la suivante, répartition de départ (<c>ranking</c>, <c>random</c>).</summary>
		public int Up { get; set; } = 1;
		public int Down { get; set; } = 1;
		public string Start { get; set; } = "ranking";

		// ---- bots
		/// <summary>Des bots complètent les parties de l'étape (vide : battle royale et équipes tirées au sort oui, le reste non — tournois créés avant ce réglage).</summary>
		public bool? Bots { get; set; }
		/// <summary>Bots au plus par partie (0 : autant qu'il faut pour remplir la partie).</summary>
		public int MaxBots { get; set; }

		/// <summary>Des bots complètent-ils les parties de l'étape ?</summary>
		public bool FillBots => Bots ?? (GameMode == "br" || RandomTeams);

		public bool IsLobby => Kind == "lobby";
		public bool IsSplit => Kind == "split";
		/// <summary>L'étape se joue en matchs (poules, tableau, poules de division).</summary>
		public bool IsMatches => Kind == "pools" || Kind == "table" || Kind == "divisions";
		/// <summary>Parties à gagner pour remporter un match.</summary>
		public int WinsNeeded => Math.Max(1, BestOf) / 2 + 1;
		/// <summary>Score maximum d'un match dans ManageComp : le score de la partie, ou les parties gagnées d'un match en plusieurs parties.</summary>
		public int MatchScore => BestOf > 1 ? WinsNeeded : MaxScore;
		public bool RandomTeams => Teams == "random";
		public string ManageCompKind => Kind switch { "pools" => "pool_supervisor", "table" => "table", "divisions" => "divisions", "split" => "splitting", _ => "lobby" };
		/// <summary>Mode de jeu effectif (anciens tournois : lobby = battle royale, poules et tableau = match à mort).</summary>
		public string GameMode => IsSplit ? "" : string.IsNullOrEmpty(Mode) ? (IsLobby ? "br" : "tdm") : Mode;
		public bool TwoTeams => TwoTeamModes.Contains(GameMode);
		/// <summary>Joueurs par équipe en jeu, pour des inscrits de <paramref name="entrySize"/> joueurs.</summary>
		public int PlayTeamSize(int entrySize) => GameMode == "1v1" || GameMode == "dm" ? 1 : RandomTeams ? TeamSize : entrySize;
		/// <summary>Lobby d'un mode à deux équipes : chaque manche est un match entre deux équipes.</summary>
		public bool TwoSideLobby => IsLobby && TwoTeams;

		/// <summary>Bornes du score d'une partie, selon le mode.</summary>
		public static (int min, int max) ScoreRange(string mode) => mode switch
		{
			"dm" => (5, 50), "tdm" => (3, 100), "sab" => (1, 16), "ctl" => (20, 300), _ => (1, 10),
		};
	}

	/// <summary>
	/// Configuration d'un tournoi, choisie par l'organisateur sur la page de création et modifiable jusqu'au départ.
	/// On s'inscrit en solo ou par équipes de <see cref="TeamSize"/> (le chef propose ses coéquipiers, qui acceptent ou refusent) ; chaque phase a son mode de jeu.
	/// Départ <c>fixed</c> (lancé automatiquement à l'heure, annulé s'il manque des inscrits) ou <c>manual</c> (lancé par l'organisateur ; l'heure est indicative).
	/// </summary>
	public sealed class TournamentConfig
	{
		public const int MaxPhases = 8, MaxObservers = 20, MaxEntriesLimit = 100, MaxRaces = 12, MaxScale = 30, MaxBrPlayers = 30, MaxDmPlayers = 10, MaxDivisions = 8;
		public static readonly int[] CheckInChoices = { 0, 5, 10, 15, 30, 60 };
		public static readonly TimeSpan MaxAdvance = TimeSpan.FromDays(365);
		public static readonly string[] GameModes = { "br", "dm", "1v1", "tdm", "ctf", "sab", "ctl" };

		public string Title { get; set; } = "";
		public string Description { get; set; } = "";
		/// <summary>Résumé des modes (calculé) : <c>br</c> (que du battle royale), <c>duel</c> (anciens tournois en match à mort), <c>mix</c> sinon.</summary>
		public string Format { get; set; } = "br";
		/// <summary>Joueurs par inscription : 1 (solo) à 5 (équipe formée par invitations).</summary>
		public int TeamSize { get; set; } = 1;
		/// <summary>Inscrits (joueurs ou équipes) : minimum pour lancer, maximum accepté.</summary>
		public int MinEntries { get; set; } = 2;
		public int MaxEntries { get; set; } = MaxEntriesLimit;
		public string StartMode { get; set; } = "fixed";
		/// <summary>Départ (heure fixe) ou date prévue (départ par l'organisateur), en secondes Unix.</summary>
		public long StartAt { get; set; }
		/// <summary>Ouverture et clôture des inscriptions (0 : dès la création ; jusqu'au départ).</summary>
		public long RegistrationOpensAt { get; set; }
		public long RegistrationClosesAt { get; set; }
		/// <summary>Check-in : les inscrits confirment leur présence dans les N minutes avant le départ ; sans confirmation, ils sont retirés. 0 = pas de check-in.</summary>
		public int CheckInMinutes { get; set; }
		/// <summary>Check-in ouvert à la main par l'organisateur (secondes Unix, 0 : non) : il vaut alors quelle que soit l'heure prévue.</summary>
		public long CheckInOpenedAt { get; set; }
		/// <summary>Observateurs invités par l'organisateur (noms de compte) : ils suivent le tournoi depuis son lobby et regardent les parties en cours.</summary>
		public List<string> Observers { get; set; } = new List<string>();
		public List<PhaseConfig> Phases { get; set; } = new List<PhaseConfig>();

		public bool FixedStart => StartMode == "fixed";
		public int TotalRaces => Phases.Where(p => p.IsLobby).Sum(p => p.Races);
		/// <summary>Une phase se joue en matchs (poules, tableau) : il faut au moins deux inscrits.</summary>
		public bool HasMatches => Phases.Any(p => p.IsMatches);
		/// <summary>Fin de la période d'inscription effective.</summary>
		public long ClosesAt => RegistrationClosesAt > 0 ? RegistrationClosesAt : StartAt;
		/// <summary>Ouverture du check-in (0 : pas de check-in).</summary>
		public long CheckInOpensAt
		{
			get
			{
				long planned = FixedStart && CheckInMinutes > 0 ? StartAt - CheckInMinutes * 60L : 0;
				return CheckInOpenedAt > 0 && (planned == 0 || CheckInOpenedAt < planned) ? CheckInOpenedAt : planned;
			}
		}

		private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		public string ToJson() => JsonSerializer.Serialize(this, Json);
		public static TournamentConfig FromJson(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return null;
			try { return JsonSerializer.Deserialize<TournamentConfig>(json, Json); } catch (JsonException) { return null; }
		}

		public TournamentConfig Copy() => FromJson(ToJson());

		/// <summary>Formule par défaut : une série de manches de battle royale (ou poules puis tableau en match à mort, anciens duels).</summary>
		public static List<PhaseConfig> DefaultPhases(string format, int races = 5, int lobbySize = 20, int scoreLimit = 10) => format == "duel"
			? new List<PhaseConfig> { new PhaseConfig { Kind = "pools", Mode = "tdm", MaxScore = scoreLimit }, new PhaseConfig { Kind = "table", Mode = "tdm", MaxScore = scoreLimit } }
			: new List<PhaseConfig> { new PhaseConfig { Kind = "lobby", Mode = "br", Races = races, LobbySize = lobbySize } };

		/// <summary>Vérifie et complète la configuration (valeurs par défaut, bornes, modes compatibles avec la taille d'inscription). Retourne un message d'erreur en français, ou null.</summary>
		public string Validate(long now, bool creating)
		{
			Title = (Title ?? "").Trim();
			Description = (Description ?? "").Trim();
			if (Title.Length < 3 || Title.Length > 60) return "Le nom du tournoi doit faire entre 3 et 60 caractères.";
			if (Description.Length > 500) return "La description fait au plus 500 caractères.";
			if (TeamSize < 1 || TeamSize > 5) return "On s'inscrit seul ou par équipes de 2 à 5 joueurs.";
			if (MaxEntries <= 0) MaxEntries = MaxEntriesLimit;
			if (MaxEntries < 2 || MaxEntries > MaxEntriesLimit) return $"Le nombre maximum d'inscrits va de 2 à {MaxEntriesLimit}.";
			if (StartMode != "fixed" && StartMode != "manual") return "Mode de départ inconnu.";
			if (StartAt <= 0) return "Indique la date et l'heure du départ.";
			if (creating && StartAt < now - 300) return "La date de début est déjà passée.";
			if (StartAt > now + (long)MaxAdvance.TotalSeconds) return "Un tournoi se programme au plus un an à l'avance.";
			if (RegistrationOpensAt <= 0) RegistrationOpensAt = now;
			if (RegistrationClosesAt > 0 && RegistrationClosesAt <= RegistrationOpensAt) return "Les inscriptions doivent fermer après leur ouverture.";
			if (FixedStart && RegistrationClosesAt > StartAt) return "Les inscriptions doivent fermer au plus tard au départ.";
			if (RegistrationOpensAt > StartAt) return "Les inscriptions doivent ouvrir avant le départ.";
			if (!CheckInChoices.Contains(CheckInMinutes)) return "Durée de check-in non prévue.";
			if (!FixedStart) CheckInMinutes = 0;          // départ par l'organisateur : il lance quand tout le monde est là (il peut ouvrir le check-in à la main)
			Observers = (Observers ?? new List<string>()).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (Observers.Count > MaxObservers) return $"Un tournoi compte au plus {MaxObservers} observateurs.";

			if (Phases == null || Phases.Count == 0) Phases = DefaultPhases(Format);
			if (Phases.Count > MaxPhases) return $"Un tournoi compte au plus {MaxPhases} phases.";
			for (int i = 0; i < Phases.Count; i++)
			{
				string error = ValidatePhase(Phases[i], i, i == Phases.Count - 1);
				if (error != null) return Phases.Count > 1 ? $"Phase {i + 1} : {error}" : error;
			}
			if (MinEntries <= 0) MinEntries = HasMatches ? 2 : 1;
			if (MinEntries > MaxEntries) return "Le minimum d'inscrits dépasse le maximum.";
			if (HasMatches && MinEntries < 2) return "Un tournoi avec des matchs (poules, tableau) demande au moins deux inscrits.";
			Format = Phases.All(p => p.IsSplit || p.GameMode == "br") ? "br" : Phases.All(p => p.GameMode == "tdm" && TeamSize <= 2) ? "duel" : "mix";
			return null;
		}

		/// <summary>Vérifie un modèle : la même configuration, sans ses dates (elles sont choisies quand on s'en sert).</summary>
		public string ValidateTemplate()
		{
			long now = AccountStore.Now();
			StartAt = now + 86400; RegistrationOpensAt = RegistrationClosesAt = 0;
			string error = Validate(now, creating: true);
			StartAt = RegistrationOpensAt = RegistrationClosesAt = 0;
			return error;
		}

		private string ValidatePhase(PhaseConfig p, int index, bool last)
		{
			p.Mode = p.GameMode;
			p.Name = (p.Name ?? "").Trim();
			if (p.Name.Length > 40) return "le nom d'une phase fait au plus 40 caractères.";
			if (!PhaseConfig.Kinds.Contains(p.Kind)) return "déroulement inconnu.";
			if (!p.IsSplit && !GameModes.Contains(p.Mode)) return "mode de jeu inconnu.";
			if (p.QualifyMode != "total" && p.QualifyMode != "lobby") return "mode de qualification inconnu.";
			if (p.Qualified < 0 || p.Qualified > MaxEntriesLimit) return "nombre de qualifiés invalide.";
			if (last || p.IsSplit) p.Qualified = 0;
			if (!p.IsLobby) p.QualifyMode = "total";          // poules et tableau : les qualifiés se comptent au classement de l'étape
			if (p.Teams != "entry" && p.Teams != "random") return "choix des équipes inconnu.";
			if (p.RandomTeams && p.TeamSize <= 1) p.Teams = "entry";          // tirage en équipes d'un joueur : chacun pour soi
			p.Sizes ??= new List<int>();
			if (p.MaxBots < 0 || p.MaxBots > 99) return "bots par partie : de 0 (sans limite) à 99.";
			if (p.IsSplit) { p.Bots = null; p.MaxBots = 0; }
			else if (p.Bots == false) p.MaxBots = 0;
			if (p.IsSplit || p.Kind == "divisions")
			{
				if (p.Divisions < 2 || p.Divisions > MaxDivisions) return $"le nombre de divisions va de 2 à {MaxDivisions}.";
				if (p.Up < 0 || p.Up > 20 || p.Down < 0 || p.Down > 20) return "montées et descentes : de 0 à 20.";
				if (p.Start != "ranking" && p.Start != "random") return "répartition des divisions inconnue.";
				if (!p.IsSplit) p.Sizes.Clear();
				if (p.Sizes.Count != 0 && (p.Sizes.Count != p.Divisions || p.Sizes.Any(v => v < 2 || v > MaxEntriesLimit))) return "une division compte au moins deux participants.";
			}
			if (p.IsSplit)
			{
				if (index == 0 || Phases[index - 1].IsSplit) return "une séparation en divisions se place après une étape jouée.";
				if (last) return "une séparation en divisions est suivie d'au moins une étape.";
				return null;
			}

			var (min, max) = PhaseConfig.ScoreRange(p.Mode);
			if (p.Mode == "br" || p.Mode == "dm")
			{
				if (!p.IsLobby) return p.Mode == "br" ? "le battle royale se joue en séries de manches (lobbys)." : "le deathmatch se joue en séries de manches (lobbys).";
				if (p.KillPoints < 0 || p.KillPoints > 20) return "points par élimination : de 0 à 20.";
			}
			else p.KillPoints = 0;
			switch (p.Mode)
			{
				case "br":
					if (p.RandomTeams && TeamSize > 1) return "les équipes tirées au sort se forment avec des inscrits en solo.";
					if (p.RandomTeams && p.TeamSize > 5) return "une équipe de battle royale compte au plus 5 joueurs.";
					if (p.LobbySize < 2 || p.LobbySize * TeamSize > MaxBrPlayers) return TeamSize > 1 ? $"un lobby de battle royale compte de 2 à {MaxBrPlayers / TeamSize} équipes de {TeamSize}." : $"un lobby compte de 2 à {MaxBrPlayers} joueurs.";
					break;
				case "dm":
					if (TeamSize != 1) return "le deathmatch demande une inscription en solo.";
					if (p.LobbySize < 2 || p.LobbySize > MaxDmPlayers) return $"un lobby de deathmatch compte de 2 à {MaxDmPlayers} joueurs.";
					if (p.MaxScore < min || p.MaxScore > max) return $"une partie se gagne en {min} à {max} éliminations.";
					p.Teams = "entry";
					break;
				case "1v1":
					if (TeamSize != 1) return "le 1 VS 1 demande une inscription en solo.";
					if (p.IsLobby) return "le 1 VS 1 se joue en poules et en tableau.";
					if (p.MaxScore < min || p.MaxScore > max) return $"une partie se gagne en {min} à {max} manches.";
					if (!DuelRules.TryParsePolicy(p.Loadout, out _, out _)) return "équipement du 1 VS 1 inconnu.";
					p.Teams = "entry";
					break;
				default:
					if (p.RandomTeams)
					{
						if (TeamSize > 1) return "les équipes tirées au sort se forment avec des inscrits en solo.";
						if (!p.IsLobby) return "avec des équipes tirées au sort, ce mode se joue en série de matchs (lobbys).";
						if (p.TeamSize < 2 || p.TeamSize > 5) return "une équipe tirée au sort compte de 2 à 5 joueurs.";
						p.LobbySize = 2 * p.TeamSize;
					}
					else
					{
						// match à mort à 1 contre 1 : anciens tournois « duels »
						if (TeamSize < 2 && !(p.Mode == "tdm" && p.IsMatches)) return "ce mode se joue en équipes : inscription par équipes, ou équipes tirées au sort.";
						if (p.IsLobby) p.LobbySize = 2;
					}
					if (p.MaxScore < min || p.MaxScore > max) return p.Mode switch
					{
						"ctf" => $"une partie se gagne en {min} à {max} prises.", "sab" => $"une partie se gagne en {min} à {max} manches.",
						"ctl" => $"une partie se gagne en {min} à {max} points.", _ => $"une partie se gagne en {min} à {max} éliminations.",
					};
					break;
			}
			if (p.IsLobby)
			{
				if (p.Races < 1 || p.Races > MaxRaces) return $"le nombre de manches va de 1 à {MaxRaces}.";
				p.Scale ??= new List<int>(ManageCompApi.PlacementPoints);
				if (p.Scale.Count == 0) p.Scale = new List<int>(ManageCompApi.PlacementPoints);
				if (p.Scale.Count > MaxScale || p.Scale.Any(v => v < 0 || v > 100)) return $"points par place : au plus {MaxScale} valeurs, de 0 à 100.";
				if (p.AbandonPoints < 0 || p.AbandonPoints > 100) return "points d'un abandon : de 0 à 100.";
				if (p.Distribution != "balanced" && p.Distribution != "level") return "répartition des lobbys inconnue.";
				p.BestOf = 1;
			}
			else
			{
				if (p.BestOf != 1 && p.BestOf != 3 && p.BestOf != 5) return "un match se joue en 1, 3 ou 5 parties.";
				if (p.PoolSize != 0 && (p.PoolSize < 2 || p.PoolSize > 12)) return "une poule compte de 2 à 12 participants.";
				if (p.Places != "third" && p.Places != "all" && p.Places != "none") return "places disputées du tableau inconnues.";
			}
			return null;
		}

		/// <summary>Configuration d'un ancien tournoi (créé avant la page de configuration) : départ à heure fixe, formule par défaut.</summary>
		public static TournamentConfig Legacy(TournamentRow t) => new TournamentConfig
		{
			Title = t.Title, Format = t.Mode, TeamSize = t.TeamSize, MinEntries = t.Mode == "duel" ? 2 : 1, MaxEntries = MaxEntriesLimit, StartMode = "fixed", StartAt = t.StartAt,
			Phases = DefaultPhases(t.Mode, Math.Max(1, t.Races), Math.Max(2, t.LobbySize), t.ScoreLimit <= 0 ? 10 : t.ScoreLimit),
		};
	}
}
