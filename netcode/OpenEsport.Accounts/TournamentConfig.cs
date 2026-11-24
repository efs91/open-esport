using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenEsport.Core;

namespace OpenEsport.Accounts
{
	/// <summary>
	/// Une phase d'un tournoi, jouée dans une phase ManageComp, dans un mode de jeu :
	/// <list type="bullet">
	/// <item><c>br</c> (battle royale) : série de manches dans un ou plusieurs lobbys (<see cref="Kind"/> <c>lobby</c>), points par place, classement cumulé ;</item>
	/// <item><c>ctf</c> (Capture du drapeau) : série de matchs à deux équipes en lobbys (1re et 2e place à chaque match), ou poules et tableau entre équipes inscrites ;</item>
	/// <item><c>1v1</c> : poules (chacun contre chacun) et tableau à élimination directe, en manches de 1 VS 1 ;</item>
	/// <item><c>tdm</c> : match à mort par équipe en poules et tableau (anciens tournois « duels »).</item>
	/// </list>
	/// Les équipes d'une phase sont celles de l'inscription (<c>entry</c>), ou tirées au sort à chaque manche parmi des inscrits en solo (<c>random</c>).
	/// Les qualifiés d'une phase passent à la suivante (sélection) ; la répartition « par niveau » fait des divisions.
	/// </summary>
	public sealed class PhaseConfig
	{
		public string Kind { get; set; } = "lobby";
		/// <summary>Mode de jeu : <c>br</c>, <c>ctf</c>, <c>1v1</c>, <c>tdm</c> (vide : déduit de <see cref="Kind"/>, anciens tournois).</summary>
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
		/// <summary>Inscrits par lobby : le nombre de lobbys en découle. En Capture du drapeau, il est fixé par la taille des équipes (deux équipes par lobby).</summary>
		public int LobbySize { get; set; } = 20;
		/// <summary>Points par place (1er, 2e…) ; au-delà, 0.</summary>
		public List<int> Scale { get; set; } = new List<int>(ManageCompApi.PlacementPoints);
		public int AbandonPoints { get; set; }
		/// <summary><c>balanced</c> : lobbys de force égale (serpentin) ; <c>level</c> : divisions, les meilleurs ensemble dans le lobby 1.</summary>
		public string Distribution { get; set; } = "balanced";

		// ---- qualification vers la phase suivante
		/// <summary><c>total</c> : les N meilleurs au classement fusionné ; <c>lobby</c> : les N meilleurs de chaque lobby (ou de chaque poule).</summary>
		public string QualifyMode { get; set; } = "total";
		/// <summary>Qualifiés pour la phase suivante (0 = tous).</summary>
		public int Qualified { get; set; }

		// ---- matchs (poules, tableau) et Capture du drapeau
		/// <summary>Score pour gagner un match : manches gagnées (1 VS 1), prises de drapeau (Capture du drapeau), victimes (match à mort).</summary>
		public int MaxScore { get; set; } = 10;
		/// <summary>Tableau : places disputées (<c>third</c> : match pour la 3e place, <c>all</c> : toutes, <c>none</c> : aucune).</summary>
		public string Places { get; set; } = "third";
		/// <summary>1 VS 1 : équipement (<c>random</c> au hasard à chaque manche, <c>choice</c> choisi au début, <c>fixed:…</c> imposé).</summary>
		public string Loadout { get; set; } = "random";

		public bool IsLobby => Kind == "lobby";
		public bool RandomTeams => Teams == "random";
		public string ManageCompKind => Kind switch { "pools" => "pool_supervisor", "table" => "table", _ => "lobby" };
		/// <summary>Mode de jeu effectif (anciens tournois : lobby = battle royale, poules et tableau = match à mort).</summary>
		public string GameMode => string.IsNullOrEmpty(Mode) ? (IsLobby ? "br" : "tdm") : Mode;
		/// <summary>Joueurs par équipe en jeu, pour des inscrits de <paramref name="entrySize"/> joueurs.</summary>
		public int PlayTeamSize(int entrySize) => GameMode == "1v1" ? 1 : RandomTeams ? TeamSize : entrySize;
		/// <summary>Lobby de Capture du drapeau : chaque manche est un match entre deux équipes.</summary>
		public bool TwoSideLobby => IsLobby && GameMode == "ctf";
	}

	/// <summary>
	/// Configuration d'un tournoi, choisie par l'organisateur sur la page de création et modifiable jusqu'au départ.
	/// On s'inscrit en solo ou par équipes de <see cref="TeamSize"/> (le chef propose ses coéquipiers, qui acceptent ou refusent) ; chaque phase a son mode de jeu.
	/// Départ <c>fixed</c> (lancé automatiquement à l'heure, annulé s'il manque des inscrits) ou <c>manual</c> (lancé par l'organisateur ; l'heure est indicative).
	/// </summary>
	public sealed class TournamentConfig
	{
		public const int MaxPhases = 6, MaxEntriesLimit = 100, MaxLobbySize = 30, MaxRaces = 10, MaxScale = 30, MaxBrPlayers = 30;
		public static readonly int[] CheckInChoices = { 0, 5, 10, 15, 30, 60 };
		public static readonly TimeSpan MaxAdvance = TimeSpan.FromDays(365);
		public static readonly string[] GameModes = { "br", "ctf", "1v1", "tdm" };

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
		public List<PhaseConfig> Phases { get; set; } = new List<PhaseConfig>();

		public bool FixedStart => StartMode == "fixed";
		public int TotalRaces => Phases.Where(p => p.IsLobby).Sum(p => p.Races);
		/// <summary>Une phase se joue en matchs (poules, tableau) : il faut au moins deux inscrits.</summary>
		public bool HasMatches => Phases.Any(p => !p.IsLobby);
		/// <summary>Fin de la période d'inscription effective.</summary>
		public long ClosesAt => RegistrationClosesAt > 0 ? RegistrationClosesAt : StartAt;
		/// <summary>Ouverture du check-in (0 : pas de check-in).</summary>
		public long CheckInOpensAt => FixedStart && CheckInMinutes > 0 ? StartAt - CheckInMinutes * 60L : 0;

		private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		public string ToJson() => JsonSerializer.Serialize(this, Json);
		public static TournamentConfig FromJson(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return null;
			try { return JsonSerializer.Deserialize<TournamentConfig>(json, Json); } catch (JsonException) { return null; }
		}

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
			if (!FixedStart) CheckInMinutes = 0;          // départ par l'organisateur : il lance quand tout le monde est là

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
			Format = Phases.All(p => p.GameMode == "br") ? "br" : Phases.All(p => p.GameMode == "tdm") ? "duel" : "mix";
			return null;
		}

		private string ValidatePhase(PhaseConfig p, int index, bool last)
		{
			p.Mode = p.GameMode;
			p.Name = (p.Name ?? "").Trim();
			if (p.Name.Length > 40) return "le nom d'une phase fait au plus 40 caractères.";
			if (!GameModes.Contains(p.Mode)) return "mode de jeu inconnu.";
			if (p.Kind != "lobby" && p.Kind != "pools" && p.Kind != "table") return "déroulement inconnu.";
			if (p.QualifyMode != "total" && p.QualifyMode != "lobby") return "mode de qualification inconnu.";
			if (p.Qualified < 0 || p.Qualified > MaxEntriesLimit) return "nombre de qualifiés invalide.";
			if (last) p.Qualified = 0;
			if (p.Kind == "table" && !last) return "le tableau (élimination directe) est forcément la dernière phase.";
			if (p.Teams != "entry" && p.Teams != "random") return "choix des équipes inconnu.";
			if (p.RandomTeams && p.TeamSize <= 1) p.Teams = "entry";          // tirage en équipes d'un joueur : chacun pour soi

			switch (p.Mode)
			{
				case "br":
					if (!p.IsLobby) return "le battle royale se joue en séries de manches (lobbys).";
					if (p.RandomTeams && TeamSize > 1) return "les équipes tirées au sort se forment avec des inscrits en solo.";
					if (p.RandomTeams && p.TeamSize > 5) return "une équipe de battle royale compte au plus 5 joueurs.";
					if (p.LobbySize < 2 || p.LobbySize * TeamSize > MaxBrPlayers) return TeamSize > 1 ? $"un lobby de battle royale compte de 2 à {MaxBrPlayers / TeamSize} équipes de {TeamSize}." : $"un lobby compte de 2 à {MaxBrPlayers} joueurs.";
					break;
				case "ctf":
					if (p.RandomTeams)
					{
						if (TeamSize > 1) return "les équipes tirées au sort se forment avec des inscrits en solo.";
						if (!p.IsLobby) return "avec des équipes tirées au sort, la Capture du drapeau se joue en série de matchs (lobbys).";
						if (p.TeamSize < 2 || p.TeamSize > 5) return "une équipe de Capture du drapeau compte de 2 à 5 joueurs.";
						p.LobbySize = 2 * p.TeamSize;
					}
					else
					{
						if (TeamSize < 2) return "la Capture du drapeau se joue en équipes : inscription par équipes, ou équipes tirées au sort.";
						if (p.IsLobby) p.LobbySize = 2;
					}
					if (p.MaxScore < 1 || p.MaxScore > 10) return "un match de Capture du drapeau se gagne en 1 à 10 prises.";
					break;
				case "1v1":
					if (TeamSize != 1) return "le 1 VS 1 demande une inscription en solo.";
					if (p.IsLobby) return "le 1 VS 1 se joue en poules et en tableau.";
					if (p.MaxScore < 1 || p.MaxScore > 10) return "un match de 1 VS 1 se gagne en 1 à 10 manches.";
					if (!DuelRules.TryParsePolicy(p.Loadout, out _, out _)) return "équipement du 1 VS 1 inconnu.";
					p.Teams = "entry";
					break;
				case "tdm":
					if (p.IsLobby) return "le match à mort par équipe se joue en poules et en tableau.";
					if (TeamSize > 2) return "le match à mort se joue à 1 contre 1 ou 2 contre 2.";
					if (p.MaxScore < 3 || p.MaxScore > 30) return "un match se gagne en 3 à 30 victimes.";
					p.Teams = "entry";
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
			}
			else if (p.Places != "third" && p.Places != "all" && p.Places != "none") return "places disputées du tableau inconnues.";
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
