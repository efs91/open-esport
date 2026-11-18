using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenEsport.Core;

namespace OpenEsport.Accounts
{
	public sealed class AccountsOptions
	{
		public string DbPath = "accounts.db";
		public int Port = 27090;
		public string Bind = "0.0.0.0";
		/// <summary>Clé que présentent les serveurs de partie (en-tête X-Server-Key) pour vérifier un jeton et rapporter une partie. Vide : ces routes sont désactivées.</summary>
		public string ServerKey;
	}

	public sealed class RegisterRequest { public string Login { get; set; } public string Password { get; set; } }
	public sealed class CharacterRequest { public string Name { get; set; } public string Appearance { get; set; } }
	public sealed class VerifyRequest { public string Token { get; set; } }
	public sealed class CharacterListRequest { public System.Collections.Generic.List<CharacterRequest> Characters { get; set; } public int Selected { get; set; } }

	/// <summary>Limite les essais : au bout de <c>max</c> échecs dans la fenêtre, la clé est bloquée un moment.</summary>
	internal sealed class Throttle
	{
		private readonly int _max; private readonly TimeSpan _window, _block;
		private readonly ConcurrentDictionary<string, (int count, DateTime start, DateTime blockedUntil)> _entries = new ConcurrentDictionary<string, (int, DateTime, DateTime)>();
		public Throttle(int max, TimeSpan window, TimeSpan block) { _max = max; _window = window; _block = block; }
		public bool IsBlocked(string key) => _entries.TryGetValue(key, out var e) && e.blockedUntil > DateTime.UtcNow;
		public void Fail(string key)
		{
			var now = DateTime.UtcNow;
			_entries.AddOrUpdate(key, _ => (1, now, DateTime.MinValue), (_, e) =>
			{
				if (now - e.start > _window) e = (0, now, DateTime.MinValue);
				int count = e.count + 1;
				return (count, e.start, count >= _max ? now + _block : e.blockedUntil);
			});
			if (_entries.Count > 20000) foreach (var kv in _entries.Where(x => DateTime.UtcNow - x.Value.start > _window && x.Value.blockedUntil < DateTime.UtcNow).ToList()) _entries.TryRemove(kv.Key, out _);
		}
		public void Clear(string key) => _entries.TryRemove(key, out _);
	}

	/// <summary>Le service de comptes : inscription, connexion, personnages, statistiques et classements, plus les routes réservées aux serveurs de partie.</summary>
	public static class AccountsApp
	{
		private static readonly Regex LoginRule = new Regex("^[A-Za-z0-9_-]{3,20}$", RegexOptions.Compiled);
		public const string BadCredentials = "Identifiant ou mot de passe incorrect.";
		// condensé factice : on fait le même travail même quand l'identifiant n'existe pas (le temps de réponse ne révèle pas les comptes)
		private static readonly (byte[] hash, byte[] salt) Dummy = Passwords.Hash("dummy-password");

		public static WebApplication Build(AccountsOptions o, AccountStore store = null)
		{
			store ??= new AccountStore(o.DbPath);
			var builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();
			builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
			builder.Logging.SetMinimumLevel(LogLevel.Warning);
			builder.WebHost.UseUrls($"http://{o.Bind}:{o.Port}");
			builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);
			var app = builder.Build();

			var loginThrottle = new Throttle(5, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
			var registerThrottle = new Throttle(10, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

			string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";
			string Bearer(HttpContext c)
			{
				string h = c.Request.Headers.Authorization.ToString();
				return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h.Substring(7).Trim() : null;
			}
			long Account(HttpContext c) => store.ValidateSession(Bearer(c));
			bool ServerAuth(HttpContext c)
			{
				if (string.IsNullOrEmpty(o.ServerKey)) return false;
				string given = c.Request.Headers["X-Server-Key"].ToString();
				return given.Length == o.ServerKey.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(o.ServerKey));
			}
			IResult Error(int code, string message) => Results.Json(new { error = message }, statusCode: code);

			object Profile(AccountRow a)
			{
				var modes = store.ModeStats(a.Id);
				var general = store.General(a.Id);
				return new
				{
					id = a.Id, login = a.Login, created = a.Created, selectedCharacter = a.SelectedCharacter,
					general = new { rating = general.rating, rank = general.rank, matches = general.matches },
					modes = modes.Select(m => new
					{
						mode = m.Mode, label = Modes.All.First(x => x.Key == m.Mode).Label, matches = m.Matches, wins = m.Wins, kills = m.Kills, deaths = m.Deaths,
						damage = m.Damage, rating = m.Rating, rank = m.Rank, bestPlacement = m.BestPlacement, teams = Modes.All.First(x => x.Key == m.Mode).TwoTeams
					}),
					characters = store.Characters(a.Id).Select(c => new { id = c.Id, name = c.Name, appearance = c.Appearance }),
				};
			}

			app.MapGet("/health", () => Results.Text("ok"));

			app.MapPost("/api/register", async (HttpContext ctx) =>
			{
				if (registerThrottle.IsBlocked(Ip(ctx))) return Error(429, "Trop d'inscriptions depuis cette adresse. Réessaie plus tard.");
				var req = await ctx.Request.ReadFromJsonAsync<RegisterRequest>();
				string login = req?.Login?.Trim();
				if (login == null || !LoginRule.IsMatch(login)) return Error(400, "Identifiant : 3 à 20 caractères (lettres, chiffres, _ et -).");
				string pwError = Passwords.Check(req.Password);
				if (pwError != null) return Error(400, pwError);
				var (hash, salt) = Passwords.Hash(req.Password);
				long id = store.CreateAccount(login, hash, salt, Passwords.Iterations);
				registerThrottle.Fail(Ip(ctx));      // chaque inscription compte (limite par adresse)
				if (id == 0) return Error(409, "Cet identifiant est déjà pris.");
				return Results.Json(new { token = store.CreateSession(id), account = Profile(store.FindById(id)) }, statusCode: 201);
			});

			app.MapPost("/api/login", async (HttpContext ctx) =>
			{
				var req = await ctx.Request.ReadFromJsonAsync<RegisterRequest>();
				string login = req?.Login?.Trim() ?? "";
				string keyIp = "ip:" + Ip(ctx), keyLogin = "login:" + login.ToLowerInvariant();
				if (loginThrottle.IsBlocked(keyIp) || loginThrottle.IsBlocked(keyLogin)) return Error(429, "Trop d'essais. Réessaie dans une minute.");
				var acc = login.Length > 0 && login.Length <= 20 ? store.FindByLogin(login) : null;
				bool ok = acc != null ? Passwords.Verify(req?.Password, acc.Hash, acc.Salt, acc.Iterations) : (Passwords.Verify(req?.Password, Dummy.hash, Dummy.salt, Passwords.Iterations) && false);
				if (!ok) { loginThrottle.Fail(keyIp); loginThrottle.Fail(keyLogin); return Error(401, BadCredentials); }
				loginThrottle.Clear(keyLogin);
				return Results.Json(new { token = store.CreateSession(acc.Id), account = Profile(acc) });
			});

			app.MapPost("/api/logout", (HttpContext ctx) => { store.DeleteSession(Bearer(ctx)); return Results.Ok(); });

			app.MapGet("/api/me", (HttpContext ctx) =>
			{
				long id = Account(ctx);
				if (id == 0) return Error(401, "Session expirée : reconnecte-toi.");
				return Results.Json(Profile(store.FindById(id)));
			});

			static string CleanName(string n) { n = (n ?? "").Trim(); return n.Length is >= 1 and <= 24 ? n : null; }
			static bool GoodAppearance(string a) => !string.IsNullOrEmpty(a) && a.Length <= 8000;

			// remplace toute la liste d'un coup (c'est ainsi que le jeu synchronise sa bibliothèque de personnages)
			app.MapPut("/api/characters", async (HttpContext ctx) =>
			{
				long acc = Account(ctx); if (acc == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var req = await ctx.Request.ReadFromJsonAsync<CharacterListRequest>();
				var list = new System.Collections.Generic.List<(string, string)>();
				foreach (var c in req?.Characters ?? new System.Collections.Generic.List<CharacterRequest>())
				{
					string name = CleanName(c?.Name);
					if (name == null || !GoodAppearance(c.Appearance)) return Error(400, "Personnage invalide (nom de 1 à 24 caractères).");
					list.Add((name, c.Appearance));
				}
				return store.ReplaceCharacters(acc, list, req?.Selected ?? 0) ? Results.Ok() : Error(409, $"Tu ne peux pas avoir plus de {AccountStore.MaxCharacters} personnages.");
			});

			app.MapPost("/api/characters", async (HttpContext ctx) =>
			{
				long acc = Account(ctx); if (acc == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var req = await ctx.Request.ReadFromJsonAsync<CharacterRequest>();
				string name = CleanName(req?.Name);
				if (name == null || !GoodAppearance(req.Appearance)) return Error(400, "Personnage invalide (nom de 1 à 24 caractères).");
				long id = store.AddCharacter(acc, name, req.Appearance);
				if (id == 0) return Error(409, $"Tu as déjà {AccountStore.MaxCharacters} personnages.");
				return Results.Json(new { id }, statusCode: 201);
			});

			app.MapPut("/api/characters/{id:long}", async (HttpContext ctx, long id) =>
			{
				long acc = Account(ctx); if (acc == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var req = await ctx.Request.ReadFromJsonAsync<CharacterRequest>();
				string name = CleanName(req?.Name);
				if (name == null || !GoodAppearance(req.Appearance)) return Error(400, "Personnage invalide (nom de 1 à 24 caractères).");
				return store.UpdateCharacter(acc, id, name, req.Appearance) ? Results.Ok() : Error(404, "Personnage introuvable.");
			});

			app.MapDelete("/api/characters/{id:long}", (HttpContext ctx, long id) =>
			{
				long acc = Account(ctx); if (acc == 0) return Error(401, "Session expirée : reconnecte-toi.");
				return store.DeleteCharacter(acc, id) ? Results.Ok() : Error(404, "Personnage introuvable.");
			});

			app.MapPost("/api/characters/{id:long}/select", (HttpContext ctx, long id) =>
			{
				long acc = Account(ctx); if (acc == 0) return Error(401, "Session expirée : reconnecte-toi.");
				return store.SelectCharacter(acc, id) ? Results.Ok() : Error(404, "Personnage introuvable.");
			});

			// classements : publics
			app.MapGet("/api/leaderboard", (string mode, int? top) =>
			{
				string key = null;
				if (!string.IsNullOrEmpty(mode) && mode != "all")
				{
					if (!Modes.TryParse(mode, out var m)) return Error(400, "Mode inconnu.");
					key = Modes.Get(m).Key;
				}
				return Results.Json(new { mode = key ?? "all", rows = store.Leaderboard(key, top ?? 50) });
			});

			// routes réservées aux serveurs de partie
			app.MapPost("/internal/verify", async (HttpContext ctx) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var req = await ctx.Request.ReadFromJsonAsync<VerifyRequest>();
				long acc = store.ValidateSession(req?.Token);
				if (acc == 0) return Error(401, "Jeton invalide.");
				var a = store.FindById(acc);
				var ch = store.Characters(acc).FirstOrDefault(c => c.Id == a.SelectedCharacter) ?? store.Characters(acc).FirstOrDefault();
				return Results.Json(new { accountId = a.Id, login = a.Login, character = ch == null ? null : new { id = ch.Id, name = ch.Name, appearance = ch.Appearance } });
			});

			app.MapPost("/internal/match", async (HttpContext ctx) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var report = await ctx.Request.ReadFromJsonAsync<MatchReport>();
				return store.ApplyMatch(report) ? Results.Json(new { counted = true }) : Results.Json(new { counted = false });
			});

			return app;
		}

		/// <summary>L'adresse sur laquelle écoute l'application démarrée (utile quand le port demandé est 0).</summary>
		public static string BaseAddress(WebApplication app) => app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>().Addresses.First();
	}
}
