using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Openesport.Utils;

namespace Openesport.Online
{
	public sealed class PartyMemberDto { public long Id { get; set; } public string Login { get; set; } }
	public sealed class PartyMatchDto { public int Seq { get; set; } public string Host { get; set; } public int Port { get; set; } public string Mode { get; set; } public int TeamSize { get; set; } }
	public sealed class PartyDto
	{
		public long Id { get; set; }
		public string Leader { get; set; }
		public bool IsLeader { get; set; }
		public List<PartyMemberDto> Members { get; set; } = new List<PartyMemberDto>();
		public List<string> Pending { get; set; } = new List<string>();
		public PartyMatchDto Match { get; set; }
	}
	public sealed class PartyInviteDto { public long PartyId { get; set; } public string From { get; set; } }
	public sealed class PartyStateDto { public PartyDto Party { get; set; } public List<PartyInviteDto> Invites { get; set; } = new List<PartyInviteDto>(); }
	public sealed class FriendDto { public string Login { get; set; } public string State { get; set; } public bool Online { get; set; } public long PartyId { get; set; } }
	public sealed class FriendsDto { public List<FriendDto> Friends { get; set; } = new List<FriendDto>(); }
	public sealed class TournamentDto { public string Id { get; set; } public string Title { get; set; } public string Subtitle { get; set; } public string Date { get; set; } public string Url { get; set; } }
	/// <summary>Un tournoi du jeu tel que la liste le montre : configuration principale, état, phase en cours, inscription du joueur.</summary>
	public sealed class GameTournamentDto
	{
		public long Id { get; set; }
		public string Title { get; set; }
		public string Description { get; set; }
		public string Mode { get; set; }
		public int TeamSize { get; set; }
		public int ScoreLimit { get; set; }
		public int Races { get; set; }
		public int Race { get; set; }
		/// <summary>open (inscriptions), running, done, cancelled.</summary>
		public string State { get; set; }
		public string Note { get; set; }
		public long StartAt { get; set; }
		/// <summary>fixed : lancé à l'heure ; manual : lancé par l'organisateur (l'heure est indicative).</summary>
		public string StartMode { get; set; }
		public long RegistrationOpensAt { get; set; }
		public long RegistrationClosesAt { get; set; }
		public bool RegistrationOpen { get; set; }
		public int CheckInMinutes { get; set; }
		public long CheckInOpensAt { get; set; }
		public int MinEntries { get; set; }
		public int MaxEntries { get; set; }
		public string Owner { get; set; }
		public int Players { get; set; }
		public bool Joined { get; set; }
		public bool CheckedIn { get; set; }
		public bool IsOwner { get; set; }
		/// <summary>Organisateur ou observateur invité : il regarde les parties en cours depuis le lobby du tournoi.</summary>
		public bool IsObserver { get; set; }
		public int Phase { get; set; }
		public int Phases { get; set; }
		public string PhaseName { get; set; }
		public int PhaseRace { get; set; }
		public int PhaseRaces { get; set; }
		public int LobbySize { get; set; }
		/// <summary>Division de ce tournoi après une séparation (0 : pas de séparation) ; ma référence dans la compétition (0 : pas inscrit).</summary>
		public int Division { get; set; }
		public int MyReference { get; set; }
		/// <summary>Modes de jeu des phases (br, dm, 1v1, tdm, ctf, sab, ctl) ; mode et déroulement (lobby, pools, table, divisions) de la phase en cours.</summary>
		public List<string> Modes { get; set; } = new List<string>();
		public string PhaseMode { get; set; } = "";
		public string PhaseKind { get; set; } = "";
		/// <summary>Équipes complètes (par équipes, une inscription compte dès que le chef s'inscrit).</summary>
		public int CompleteEntries { get; set; }
		/// <summary>Mon équipe (si je suis inscrit) et les invitations que j'ai reçues dans ce tournoi.</summary>
		public MyTournamentTeamDto MyTeam { get; set; }
		public List<TournamentInvitationDto> Invitations { get; set; } = new List<TournamentInvitationDto>();

		/// <summary>La phase en cours se joue en matchs (poules, tableau) : « Jouer mon match » plutôt que « Jouer ma manche ».</summary>
		public bool Duel => State == "running" ? PhaseKind != "" && PhaseKind != "lobby" : Mode == "duel";
		public bool Teams => TeamSize > 1;
		public bool CheckInOpen => State == "open" && CheckInOpensAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= CheckInOpensAt;
	}

	/// <summary>Mon équipe dans un tournoi : chef, joueurs, invitations en attente, complète ou non.</summary>
	public sealed class MyTournamentTeamDto
	{
		public bool Captain { get; set; }
		public string CaptainName { get; set; }
		public List<string> Members { get; set; } = new List<string>();
		public List<string> Invites { get; set; } = new List<string>();
		public bool Complete { get; set; }
	}

	/// <summary>Une invitation reçue : l'équipe (référence) et son chef.</summary>
	public sealed class TournamentInvitationDto { public int Reference { get; set; } public string Captain { get; set; } }

	/// <summary>
	/// Une étape d'un tournoi (même forme que côté service) : mode de jeu, déroulement (série de lobbys, poules, tableau, poules de division,
	/// séparation en divisions), équipes inscrites ou tirées au sort.
	/// </summary>
	public sealed class PhaseConfigDto
	{
		/// <summary>lobby, pools, table, divisions, split.</summary>
		public string Kind { get; set; } = "lobby";
		/// <summary>br, dm, 1v1, tdm, ctf, sab, ctl (vide pour une séparation).</summary>
		public string Mode { get; set; } = "br";
		/// <summary>entry : équipes inscrites ; random : tirées au sort à chaque manche (inscription en solo), de <see cref="TeamSize"/> joueurs.</summary>
		public string Teams { get; set; } = "entry";
		public int TeamSize { get; set; } = 2;
		/// <summary>1 VS 1 : random, choice ou fixed:arme,viseur,arme2,viseur2,grenade,soin.</summary>
		public string Loadout { get; set; } = "random";
		public string Name { get; set; } = "";
		public int Races { get; set; } = 5;
		public int LobbySize { get; set; } = 20;
		public List<int> Scale { get; set; } = new List<int> { 12, 9, 7, 5, 4, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1 };
		public int AbandonPoints { get; set; }
		/// <summary>Points par élimination (battle royale, deathmatch).</summary>
		public int KillPoints { get; set; }
		/// <summary>Parties d'un match (1, 3 ou 5) ; participants par poule (0 : automatique).</summary>
		public int BestOf { get; set; } = 1;
		public int PoolSize { get; set; }
		/// <summary>Divisions : nombre, participants de chacune (vide : parts égales), montées et descentes, répartition de départ (ranking, random).</summary>
		public int Divisions { get; set; } = 2;
		public List<int> Sizes { get; set; } = new List<int>();
		public int Up { get; set; } = 1;
		public int Down { get; set; } = 1;
		public string Start { get; set; } = "ranking";
		public string Distribution { get; set; } = "balanced";
		public string QualifyMode { get; set; } = "total";
		public int Qualified { get; set; }
		public int MaxScore { get; set; } = 10;
		public string Places { get; set; } = "third";
		/// <summary>Des bots complètent les parties de l'étape (vide : battle royale et équipes tirées au sort oui, le reste non) ; au plus <see cref="MaxBots"/> par partie (0 : sans limite).</summary>
		public bool? Bots { get; set; }
		public int MaxBots { get; set; }

		/// <summary>Des bots complètent-ils les parties ? (même règle que le service)</summary>
		[System.Text.Json.Serialization.JsonIgnore] public bool FillBots => Bots ?? (Mode == "br" || Teams == "random");

		public PhaseConfigDto Copy() => JsonSerializer.Deserialize<PhaseConfigDto>(JsonSerializer.Serialize(this));
	}

	/// <summary>Configuration complète d'un tournoi (page de création et de modification).</summary>
	public sealed class TournamentConfigDto
	{
		public string Title { get; set; } = "";
		public string Description { get; set; } = "";
		public string Format { get; set; } = "br";
		public int TeamSize { get; set; } = 1;
		public int MinEntries { get; set; } = 2;
		public int MaxEntries { get; set; } = 100;
		public string StartMode { get; set; } = "fixed";
		public long StartAt { get; set; }
		public long RegistrationOpensAt { get; set; }
		public long RegistrationClosesAt { get; set; }
		public int CheckInMinutes { get; set; } = 15;
		/// <summary>Observateurs invités (noms de compte) : ils suivent le tournoi depuis son lobby et regardent les parties en cours.</summary>
		public List<string> Observers { get; set; } = new List<string>();
		public List<PhaseConfigDto> Phases { get; set; } = new List<PhaseConfigDto>();

		/// <summary>Une copie indépendante : modifier un modèle choisi ne change pas le modèle.</summary>
		public TournamentConfigDto Copy() => JsonSerializer.Deserialize<TournamentConfigDto>(JsonSerializer.Serialize(this));
	}

	/// <summary>Un modèle de tournoi : du jeu (<see cref="Id"/> 0), à soi (privé ou partagé), ou partagé par un autre joueur.</summary>
	public sealed class TournamentTemplateDto
	{
		public long Id { get; set; }
		public string Name { get; set; } = "";
		public string Owner { get; set; } = "";
		public bool Mine { get; set; }
		public bool Shared { get; set; }
		public TournamentConfigDto Config { get; set; }
	}
	public sealed class TournamentTemplatesDto
	{
		public List<TournamentTemplateDto> BuiltIn { get; set; } = new List<TournamentTemplateDto>();
		public List<TournamentTemplateDto> Mine { get; set; } = new List<TournamentTemplateDto>();
		public List<TournamentTemplateDto> Shared { get; set; } = new List<TournamentTemplateDto>();
	}

	// ---- déroulé d'un tournoi lancé : les résultats de chaque étape
	public sealed class FlowRaceDto { public int Place { get; set; } public int Kills { get; set; } public int Points { get; set; } }
	public sealed class FlowRowDto { public int Place { get; set; } public string Name { get; set; } public int Reference { get; set; } public int Points { get; set; } public string Diff { get; set; } public List<FlowRaceDto> Races { get; set; } = new List<FlowRaceDto>(); }
	public sealed class FlowMatchDto
	{
		/// <summary>Clé du match dans ManageComp (p0m2, t1n3) : retrouve le match d'une partie en cours.</summary>
		public string Key { get; set; } = "";
		public string A { get; set; }
		public string B { get; set; }
		public int RefA { get; set; }
		public int RefB { get; set; }
		public string ScoreA { get; set; } = "";
		public string ScoreB { get; set; } = "";
		/// <summary>0 : A gagne ; 1 : B ; -1 : à jouer.</summary>
		public int Winner { get; set; } = -1;
		/// <summary>Parties jouées d'un match en plusieurs parties (« 13-7 »).</summary>
		public List<string> Games { get; set; } = new List<string>();
	}
	/// <summary>Un lobby, une poule, ou un tour de tableau (type lobby, pool, round).</summary>
	public sealed class FlowGroupDto
	{
		public string Type { get; set; }
		public int Number { get; set; }
		public int Division { get; set; }
		public int Size { get; set; }
		public int Place { get; set; } = 1;
		public List<FlowRowDto> Rows { get; set; } = new List<FlowRowDto>();
		public List<FlowMatchDto> Matches { get; set; } = new List<FlowMatchDto>();
	}
	public sealed class FlowPhaseDto { public int Index { get; set; } public string State { get; set; } public List<FlowGroupDto> Groups { get; set; } = new List<FlowGroupDto>(); }
	public sealed class TournamentDivisionDto { public long Id { get; set; } public int Division { get; set; } public string State { get; set; } public bool Mine { get; set; } }

	public sealed class TournamentTeamDto { public string Name { get; set; } public bool CheckedIn { get; set; } public bool Complete { get; set; } = true; public List<string> Invites { get; set; } = new List<string>(); }
	public sealed class StandingDto { public string Place { get; set; } public string Name { get; set; } public string Detail { get; set; } }

	/// <summary>Détail d'un tournoi : résumé, configuration, inscrits (équipes) et classement de la phase en cours (ou final).</summary>
	public sealed class TournamentDetailsDto
	{
		public GameTournamentDto Summary { get; set; }
		public TournamentConfigDto Config { get; set; }
		public List<TournamentTeamDto> Teams { get; set; } = new List<TournamentTeamDto>();
		public List<StandingDto> Standings { get; set; } = new List<StandingDto>();
		/// <summary>Les divisions du tournoi, s'il a été séparé ; le déroulé, étape par étape, une fois lancé.</summary>
		public List<TournamentDivisionDto> Divisions { get; set; } = new List<TournamentDivisionDto>();
		public List<FlowPhaseDto> Flow { get; set; } = new List<FlowPhaseDto>();
	}
	/// <summary>Une partie de tournoi en cours (rapportée par le matchmaking) : manche et lobby, ou match ; adresse du serveur pour les observateurs.</summary>
	public sealed class LiveGameDto
	{
		public int Race { get; set; }
		public int Lobby { get; set; }
		public string Match { get; set; } = "";
		public string Mode { get; set; } = "";
		/// <summary>0 : salle d'attente, 1 : en cours, 2 : terminée.</summary>
		public int Phase { get; set; }
		public int Humans { get; set; }
		public int TimeLeft { get; set; }
		public int Score0 { get; set; }
		public int Score1 { get; set; }
		public string Host { get; set; } = "";
		public int Port { get; set; }
	}
	public sealed class ChatLineDto { public long Id { get; set; } public long At { get; set; } public string Login { get; set; } = ""; public string Text { get; set; } = ""; }
	/// <summary>Le lobby d'un tournoi : détail (règles, classement provisoire, déroulé), parties en cours, tchat, présents, observateurs.</summary>
	public sealed class TournamentLobbyDto
	{
		public TournamentDetailsDto Details { get; set; }
		public List<LiveGameDto> Live { get; set; } = new List<LiveGameDto>();
		public List<ChatLineDto> Chat { get; set; } = new List<ChatLineDto>();
		public List<string> Present { get; set; } = new List<string>();
		public List<string> Observers { get; set; } = new List<string>();
		public bool Observer { get; set; }
		public bool CanChat { get; set; }
	}

	public sealed class TournamentsDto
	{
		/// <summary>Révision de la liste côté service : redonnée à la demande suivante, le service répond « inchangé » s'il n'y a rien de neuf.</summary>
		public long Rev { get; set; }
		public bool Unchanged { get; set; }
		public bool Configured { get; set; }
		public List<TournamentDto> Tournaments { get; set; } = new List<TournamentDto>();
		/// <summary>Tournois organisés dans le jeu (inscription, manches, classement).</summary>
		public List<GameTournamentDto> Games { get; set; } = new List<GameTournamentDto>();
	}

	/// <summary>
	/// Le côté social du compte : groupe de joueurs (squad), liste d'amis avec présence en ligne et tournois annoncés. Tout passe par le service de comptes ;
	/// chaque méthode retourne un message d'erreur en français (ou null si tout s'est bien passé) et met à jour les dernières données connues.
	/// </summary>
	public static class SocialClient
	{
		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

		public static PartyStateDto Party { get; private set; } = new PartyStateDto();
		public static FriendsDto Friends { get; private set; } = new FriendsDto();
		public static TournamentsDto Tournaments { get; private set; } = new TournamentsDto();

		/// <summary>Nombre de joueurs du groupe (1 si on est seul) : le matchmaking réserve autant de places.</summary>
		public static int PartySize => Math.Max(1, Party?.Party?.Members.Count ?? 1);
		public static bool InParty => PartySize > 1;
		/// <summary>Vrai si on n'est pas dans un groupe, ou si on en est le chef (seul le chef lance la partie).</summary>
		public static bool IsLeaderOrAlone => Party?.Party == null || Party.Party.IsLeader || Party.Party.Members.Count <= 1;

		private static async Task<(string error, T value)> Get<T>(HttpMethod method, string path, object payload = null) where T : class
		{
			var (code, body) = await AccountClient.SendAsync(method, path, payload);
			if (code != 200) return (AccountClient.ErrorOf(code, body), null);
			try { return (null, JsonSerializer.Deserialize<T>(body, Options)); }
			catch (Exception e) when (e is JsonException) { return (I18n.T("Réponse inattendue du service de comptes : {0}", e.Message), null); }
		}

		public static async Task<string> RefreshPartyAsync()
		{
			if (!AccountClient.LoggedIn) { Party = new PartyStateDto(); return null; }
			var (e, v) = await Get<PartyStateDto>(HttpMethod.Get, "/api/party");
			if (v != null) Party = v;
			return e;
		}

		/// <summary>invite, accept, decline, leave, kick, promote, match.</summary>
		public static async Task<string> PartyActionAsync(string action, string login = null, long partyId = 0, string host = null, int port = 0, string mode = null, int teamSize = 0)
		{
			var (e, v) = await Get<PartyStateDto>(HttpMethod.Post, "/api/party/" + action, new { login, partyId, host, port, mode, teamSize });
			if (v != null) Party = v;
			return e;
		}

		public static async Task<string> RefreshFriendsAsync()
		{
			if (!AccountClient.LoggedIn) { Friends = new FriendsDto(); return null; }
			var (e, v) = await Get<FriendsDto>(HttpMethod.Get, "/api/friends");
			if (v != null) Friends = v;
			return e;
		}

		/// <summary>add, accept, remove.</summary>
		public static async Task<string> FriendActionAsync(string action, string login)
		{
			var (e, v) = await Get<FriendsDto>(HttpMethod.Post, "/api/friends/" + action, new { login });
			if (v != null) Friends = v;
			return e;
		}

		/// <summary>Augmente à chaque nouvelle liste de tournois reçue (les écrans se reconstruisent quand il bouge).</summary>
		public static int TournamentsVersion { get; private set; }
		private static string _tournamentsLogin = "";

		/// <summary>
		/// Recharge la liste des tournois. Avec <paramref name="full"/> faux, on donne au service la révision connue : s'il n'y a rien de neuf, il répond
		/// en une ligne et rien ne change ici (le jeu peut donc la demander toutes les quelques secondes).
		/// </summary>
		public static async Task<string> RefreshTournamentsAsync(bool full = true)
		{
			string login = AccountClient.LoggedIn ? AccountClient.Login : "";
			if (login != _tournamentsLogin) full = true;          // autre compte : la liste n'est plus la même (inscriptions, invitations)
			var (e, v) = await Get<TournamentsDto>(HttpMethod.Get, "/api/tournaments" + (full || Tournaments.Rev == 0 ? "" : "?rev=" + Tournaments.Rev));
			if (v != null && !v.Unchanged) { Tournaments = v; _tournamentsLogin = login; TournamentsVersion++; }
			return e;
		}

		/// <summary>Le lobby d'un tournoi, avec les messages du tchat après <paramref name="since"/>.</summary>
		public static Task<(string error, TournamentLobbyDto lobby)> LobbyAsync(long id, long since) => Get<TournamentLobbyDto>(HttpMethod.Get, $"/api/tournaments/{id}/lobby?since={since}");

		public static async Task<string> ChatAsync(long id, string text)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, $"/api/tournaments/{id}/chat", new { text });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		/// <summary>Crée un tournoi d'après sa configuration ; <paramref name="participate"/> : l'organisateur s'inscrit aussi (en solo). Retourne (erreur, identifiant).</summary>
		public static async Task<(string error, long id)> CreateTournamentAsync(TournamentConfigDto config, bool participate)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, "/api/tournaments", new { config, participate });
			if (code != 200) return (AccountClient.ErrorOf(code, body), 0);
			try { return (null, JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64()); }
			catch (Exception e) when (e is JsonException || e is KeyNotFoundException) { return (null, 0); }
		}

		/// <summary>L'organisateur modifie son tournoi avant le départ.</summary>
		public static async Task<string> UpdateTournamentAsync(long id, TournamentConfigDto config)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Put, $"/api/tournaments/{id}", new { config });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		public static async Task<(string error, TournamentDetailsDto details)> TournamentDetailsAsync(long id) => await Get<TournamentDetailsDto>(HttpMethod.Get, $"/api/tournaments/{id}");

		/// <summary>Les modèles de tournoi : du jeu, les siens, ceux que les autres ont rendus publics.</summary>
		public static Task<(string error, TournamentTemplatesDto templates)> TemplatesAsync() => Get<TournamentTemplatesDto>(HttpMethod.Get, "/api/tournament-templates");

		/// <summary>Enregistre une configuration comme modèle, privé ou public ; un modèle à soi du même nom est remplacé.</summary>
		public static async Task<string> SaveTemplateAsync(string name, bool shared, TournamentConfigDto config)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, "/api/tournament-templates", new { name, shared, config });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		public static async Task<string> ShareTemplateAsync(long id, bool shared)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, $"/api/tournament-templates/{id}/share", new { shared });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		public static async Task<string> DeleteTemplateAsync(long id)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Delete, $"/api/tournament-templates/{id}");
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		/// <summary>join, leave, checkin ; start, cancel et opencheckin (organisateur) ; invite et kick (chef d'équipe, <paramref name="login"/>) ; accept et decline (invité, <paramref name="reference"/> : l'équipe).</summary>
		public static async Task<string> TournamentActionAsync(long id, string action, string login = null, int reference = 0)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, $"/api/tournaments/{id}/{action}", new { login, reference });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		/// <summary>Ce que le joueur doit jouer maintenant dans un tournoi : « tag » pour le matchmaking (manche d'une série « tournoi:manche:lobby » ou match « Dtournoi:match »), mode de la phase et taille des équipes en jeu.</summary>
		public static async Task<(string error, string tag, string mode, int teamSize)> MyRaceAsync(long id)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Get, $"/api/tournaments/{id}/me");
			if (code != 200) return (AccountClient.ErrorOf(code, body), null, null, 1);
			try
			{
				var r = JsonDocument.Parse(body).RootElement;
				bool match = r.GetProperty("kind").GetString() == "duel";
				string mode = r.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : match ? "tdm" : "br";
				int teamSize = r.TryGetProperty("teamSize", out var ts) ? ts.GetInt32() : 1;
				if (match) return (null, $"D{r.GetProperty("tournament").GetInt64()}:{r.GetProperty("match").GetString()}", mode, teamSize);
				return (null, $"{r.GetProperty("tournament").GetInt64()}:{r.GetProperty("race").GetInt32()}:{r.GetProperty("lobby").GetInt32()}", mode, teamSize);
			}
			catch (Exception e) when (e is JsonException || e is KeyNotFoundException) { return (I18n.T("Réponse inattendue du service de comptes : {0}", e.Message), null, null, 1); }
		}

		/// <summary>Le classement d'un tournoi, une ligne par joueur (« 1. alice — 21 pts »), ou un message d'erreur.</summary>
		public static async Task<(string error, List<string> lines)> StandingsAsync(long id)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Get, $"/api/tournaments/{id}");
			if (code != 200) return (AccountClient.ErrorOf(code, body), null);
			var lines = new List<string>();
			try
			{
				foreach (var s in JsonDocument.Parse(body).RootElement.GetProperty("standings").EnumerateArray())
				{
					string detail = s.GetProperty("detail").GetString();
					lines.Add($"{s.GetProperty("place").GetString()}. {s.GetProperty("name").GetString()}" + (string.IsNullOrEmpty(detail) ? "" : " — " + detail));
				}
			}
			catch (Exception e) when (e is JsonException || e is KeyNotFoundException) { return (I18n.T("Réponse inattendue du service de comptes : {0}", e.Message), null); }
			return (null, lines);
		}

		public static void Clear() { Party = new PartyStateDto(); Friends = new FriendsDto(); }

		/// <summary>Appelé quand le chef a lancé une partie : le jeu rejoint le serveur (branché par le module de parties).</summary>
		public static Action<string, int, string, int> JoinPartyGame;
	}
}
