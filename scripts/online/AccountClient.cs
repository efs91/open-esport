using Openesport.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Openesport.Online
{
	public sealed class ModeStat
	{
		public string Mode { get; set; }
		public string Label { get; set; }
		public int Matches { get; set; }
		public int Wins { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
		public long Damage { get; set; }
		public int Rating { get; set; } = 1000;
		public int Rank { get; set; }
		public int BestPlacement { get; set; }
		public bool Teams { get; set; }
	}

	/// <summary>La dernière partie jouée (historique du service de comptes).</summary>
	public sealed class LastMatchStat
	{
		public long At { get; set; }
		public string Mode { get; set; }
		public string Label { get; set; }
		public bool Teams { get; set; }
		public int TeamSize { get; set; }
		public int Seconds { get; set; }
		public int Placement { get; set; }
		public int Players { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
		public long Damage { get; set; }
		public bool Won { get; set; }
		public int Delta { get; set; }
		public int Rating { get; set; }

		/// <summary>« Battle Royale — Solo », « Team Deathmatch — 3 contre 3 »…</summary>
		public string ModeText => OpenEsport.Core.Modes.TryParse(Mode, out var m) ? GameText.Label(m, TeamSize) : I18n.T(Label ?? "");
		public string Duration => $"{Seconds / 60}:{Seconds % 60:00}";
		/// <summary>Place (« 3e / 20 ») dans les modes au classement, victoire ou défaite dans les modes à deux équipes.</summary>
		public string Result => Teams ? (Won ? I18n.T("Victoire") : I18n.T("Défaite"))
			: Placement > 0 ? (Placement == 1 ? I18n.T("1re place") : I18n.T("{0}e place", Placement)) + (Players > 0 ? $" / {Players}" : "") : "—";
		public string DeltaText => Delta >= 0 ? $"+{Delta}" : Delta.ToString();
		/// <summary>« il y a 5 min », « il y a 2 h », « il y a 3 j ».</summary>
		public string Ago
		{
			get
			{
				long s = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - At);
				return s < 60 ? I18n.T("à l'instant") : s < 3600 ? I18n.T("il y a {0} min", s / 60) : s < 86400 ? I18n.T("il y a {0} h", s / 3600) : I18n.T("il y a {0} j", s / 86400);
			}
		}
	}

	public sealed class GeneralStat { public int Rating { get; set; } = 1000; public int Rank { get; set; } public int Matches { get; set; } }
	public sealed class AccountCharacter { public long Id { get; set; } public string Name { get; set; } public string Appearance { get; set; } }

	public sealed class AccountProfile
	{
		public long Id { get; set; }
		public string Login { get; set; }
		public long SelectedCharacter { get; set; }
		public GeneralStat General { get; set; } = new GeneralStat();
		public LastMatchStat LastMatch { get; set; }
		public List<ModeStat> Modes { get; set; } = new List<ModeStat>();
		public List<AccountCharacter> Characters { get; set; } = new List<AccountCharacter>();
	}

	public sealed class LeaderRow { public int Rank { get; set; } public string Login { get; set; } public int Rating { get; set; } public int Matches { get; set; } public int Wins { get; set; } public int Kills { get; set; } public int Deaths { get; set; } }

	/// <summary>
	/// Le compte du joueur connecté : connexion et inscription auprès du service de comptes, session mémorisée, synchronisation des personnages,
	/// statistiques et classements. Toutes les méthodes asynchrones retournent un message d'erreur en français (ou null si tout s'est bien passé).
	/// </summary>
	public static class AccountClient
	{
		private const string SessionFile = "user://session.cfg";
		private static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		private static readonly SemaphoreSlim PushLock = new SemaphoreSlim(1, 1);

		public static string Token { get; private set; }
		public static AccountProfile Profile { get; private set; }
		public static bool LoggedIn => Token != null && Profile != null;

		/// <summary>Raccourci : l'identifiant de connexion du compte actuel.</summary>
		public static string Login => Profile?.Login ?? "";

		/// <summary>Dernier identifiant utilisé (pour préremplir le formulaire).</summary>
		public static string LastLogin
		{
			get { var cfg = new ConfigFile(); return cfg.Load(SessionFile) == Error.Ok ? (string)cfg.GetValue("session", "login", "") : ""; }
		}

		private static string Url(string path) => MatchSettings.AccountsUrl().TrimEnd('/') + path;

		internal static async Task<(int code, string body)> SendAsync(HttpMethod method, string path, object payload = null, bool auth = true)
		{
			try
			{
				var req = new HttpRequestMessage(method, Url(path));
				if (payload != null) req.Content = new StringContent(JsonSerializer.Serialize(payload, Options), Encoding.UTF8, "application/json");
				if (auth && Token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
				using var res = await Http.SendAsync(req);
				return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
			}
			catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is InvalidOperationException || e is UriFormatException)
			{
				return (0, I18n.T("Impossible de joindre le service de comptes ({0}).", MatchSettings.AccountsUrl()));
			}
		}

		internal static string ErrorOf(int code, string body)
		{
			if (code == 0) return body;
			try { var e = JsonDocument.Parse(body).RootElement; if (e.TryGetProperty("error", out var m)) return I18n.Server(m.GetString()); } catch { }
			return I18n.T("Erreur du service de comptes ({0}).", code);
		}

		private static void Remember(string login, bool remember)
		{
			var cfg = new ConfigFile();
			cfg.SetValue("session", "login", login);
			if (remember && Token != null) cfg.SetValue("session", "token", Token);
			cfg.Save(SessionFile);
		}

		private static string Enter(int code, string body, string login, bool remember)
		{
			if (code != 200 && code != 201) return ErrorOf(code, body);
			try
			{
				var root = JsonDocument.Parse(body).RootElement;
				Token = root.GetProperty("token").GetString();
				Profile = JsonSerializer.Deserialize<AccountProfile>(root.GetProperty("account").GetRawText(), Options);
			}
			catch (Exception e) { return I18n.T("Réponse inattendue du service de comptes : {0}", e.Message); }
			Remember(Profile.Login ?? login, remember);
			CharacterLibrary.UseAccount(Profile);
			return null;
		}

		public static async Task<string> LoginAsync(string login, string password, bool remember)
		{
			var (code, body) = await SendAsync(HttpMethod.Post, "/api/login", new { login, password }, auth: false);
			return Enter(code, body, login, remember);
		}

		public static async Task<string> RegisterAsync(string login, string password)
		{
			var (code, body) = await SendAsync(HttpMethod.Post, "/api/register", new { login, password }, auth: false);
			return Enter(code, body, login, true);
		}

		/// <summary>Reprend la session mémorisée (jeton enregistré lors d'une connexion précédente). Retourne faux s'il n'y en a pas ou si elle a expiré.</summary>
		public static async Task<bool> ResumeAsync()
		{
			var cfg = new ConfigFile();
			if (cfg.Load(SessionFile) != Error.Ok || !cfg.HasSectionKey("session", "token")) return false;
			Token = (string)cfg.GetValue("session", "token");
			var (code, body) = await SendAsync(HttpMethod.Get, "/api/me");
			if (code != 200) { Token = null; return false; }
			try { Profile = JsonSerializer.Deserialize<AccountProfile>(body, Options); }
			catch { Token = null; return false; }
			CharacterLibrary.UseAccount(Profile);
			return true;
		}

		/// <summary>Recharge le profil (statistiques à jour).</summary>
		public static async Task<string> RefreshAsync()
		{
			if (Token == null) return I18n.T("Non connecté.");
			var (code, body) = await SendAsync(HttpMethod.Get, "/api/me");
			if (code != 200) return ErrorOf(code, body);
			try { Profile = JsonSerializer.Deserialize<AccountProfile>(body, Options); }
			catch (Exception e) { return I18n.T("Réponse inattendue du service de comptes : {0}", e.Message); }
			return null;
		}

		public static async Task LogoutAsync()
		{
			if (Token != null) await SendAsync(HttpMethod.Post, "/api/logout", new { });
			Token = null; Profile = null;
			var cfg = new ConfigFile();
			cfg.SetValue("session", "login", LastLogin);          // on garde l'identifiant, pas le jeton
			cfg.Save(SessionFile);
			CharacterLibrary.LeaveAccount();
		}

		/// <summary>Envoie la bibliothèque de personnages au compte (sans attendre : un échec est seulement journalisé).</summary>
		public static void PushCharacters(IReadOnlyList<SavedCharacter> characters, int selected)
		{
			if (Token == null) return;
			var list = characters.Select(c => new { name = c.Name, appearance = Godot.Json.Stringify(c.Appearance.ToDict()) }).ToList();
			_ = PushAsync(list, selected);
		}

		private static async Task PushAsync(object list, int selected)
		{
			await PushLock.WaitAsync();
			try
			{
				var (code, body) = await SendAsync(HttpMethod.Put, "/api/characters", new { characters = list, selected });
				if (code != 200) GD.PrintErr("[Compte] synchronisation des personnages impossible : " + ErrorOf(code, body));
			}
			finally { PushLock.Release(); }
		}

		/// <summary>Meilleurs joueurs d'un mode (mode = « all » : classement général).</summary>
		public static async Task<List<LeaderRow>> LeaderboardAsync(string mode, int top = 10)
		{
			var (code, body) = await SendAsync(HttpMethod.Get, $"/api/leaderboard?mode={Uri.EscapeDataString(mode)}&top={top}", auth: false);
			if (code != 200) return null;
			try { return JsonSerializer.Deserialize<List<LeaderRow>>(JsonDocument.Parse(body).RootElement.GetProperty("rows").GetRawText(), Options); }
			catch { return null; }
		}
	}
}
