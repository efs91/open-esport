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
	public sealed class GameTournamentDto
	{
		public long Id { get; set; }
		public string Title { get; set; }
		public string Mode { get; set; }
		public int TeamSize { get; set; }
		public int ScoreLimit { get; set; }
		public int Races { get; set; }
		public int Race { get; set; }
		public string State { get; set; }
		public long StartAt { get; set; }
		public string Owner { get; set; }
		public int Players { get; set; }
		public bool Joined { get; set; }
		public bool IsOwner { get; set; }
	}
	public sealed class TournamentsDto
	{
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

		public static async Task<string> RefreshTournamentsAsync()
		{
			var (e, v) = await Get<TournamentsDto>(HttpMethod.Get, "/api/tournaments");
			if (v != null) Tournaments = v;
			return e;
		}

		/// <summary>Crée un tournoi (battle royale en <paramref name="races"/> manches) qui commence à <paramref name="startAtUnix"/> ; l'organisateur y est inscrit.</summary>
		public static async Task<string> CreateTournamentAsync(string title, long startAtUnix, int races, string mode = "br", int teamSize = 1, int scoreLimit = 0)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, "/api/tournaments", new { title, startAt = startAtUnix, races, mode, teamSize, scoreLimit });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		/// <summary>join, leave ou start (réservé à l'organisateur).</summary>
		public static async Task<string> TournamentActionAsync(long id, string action)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Post, $"/api/tournaments/{id}/{action}", new { });
			return code == 200 ? null : AccountClient.ErrorOf(code, body);
		}

		/// <summary>Ce que le joueur doit jouer maintenant dans un tournoi : « tag » pour le matchmaking (manche de battle royale « tournoi:manche:lobby » ou duel « Dtournoi:match »), mode et taille d'équipe.</summary>
		public static async Task<(string error, string tag, string mode, int teamSize)> MyRaceAsync(long id)
		{
			var (code, body) = await AccountClient.SendAsync(HttpMethod.Get, $"/api/tournaments/{id}/me");
			if (code != 200) return (AccountClient.ErrorOf(code, body), null, null, 1);
			try
			{
				var r = JsonDocument.Parse(body).RootElement;
				if (r.GetProperty("kind").GetString() == "duel")
					return (null, $"D{r.GetProperty("tournament").GetInt64()}:{r.GetProperty("match").GetString()}", "tdm", r.GetProperty("teamSize").GetInt32());
				return (null, $"{r.GetProperty("tournament").GetInt64()}:{r.GetProperty("race").GetInt32()}:{r.GetProperty("lobby").GetInt32()}", "br", r.TryGetProperty("teamSize", out var ts) ? ts.GetInt32() : 1);
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
