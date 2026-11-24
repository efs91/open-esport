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
		/// <summary>Base des statistiques d'utilisation anonymes ; par défaut telemetry.db à côté de la base des comptes.</summary>
		public string TelemetryDbPath;
		public int Port = 27090;
		public string Bind = "0.0.0.0";
		/// <summary>Clé que présentent les serveurs de partie (en-tête X-Server-Key) pour vérifier un jeton et rapporter une partie. Vide : ces routes sont désactivées.</summary>
		public string ServerKey;
		/// <summary>API de ManageComp (tournois) : adresse et clé, sinon variables MANAGECOMP_URL et MANAGECOMP_API_KEY. <see cref="ManageCompHandler"/> remplace le réseau (tests).</summary>
		public string ManageCompUrl, ManageCompKey;
		public System.Net.Http.HttpMessageHandler ManageCompHandler;
	}

	public sealed class RegisterRequest { public string Login { get; set; } public string Password { get; set; } }
	public sealed class CharacterRequest { public string Name { get; set; } public string Appearance { get; set; } }
	public sealed class VerifyRequest { public string Token { get; set; } }
	/// <summary>Création d'un tournoi : la configuration complète (page du jeu), ou l'ancienne forme courte (titre, date, manches…).</summary>
	public sealed class TournamentRequest
	{
		public TournamentConfig Config { get; set; }
		/// <summary>L'organisateur s'inscrit aussi (par équipes, il en devient le chef et invite ses coéquipiers).</summary>
		public bool? Participate { get; set; }
		public string Title { get; set; } public long StartAt { get; set; } public int Races { get; set; } public int LobbySize { get; set; } public string Mode { get; set; } public int TeamSize { get; set; } public int ScoreLimit { get; set; }
	}
	/// <summary>Équipe de tournoi : le joueur invité ou retiré (nom de compte), l'équipe dont on accepte ou refuse l'invitation (0 = la première).</summary>
	public sealed class TournamentTeamRequest { public string Login { get; set; } public int Reference { get; set; } }
	public sealed class MatchReportRequest { public long TournamentId { get; set; } public string Key { get; set; } public int ScoreA { get; set; } public int ScoreB { get; set; } }
	public sealed class RaceReport { public long TournamentId { get; set; } public int Race { get; set; } public int Lobby { get; set; } public System.Collections.Generic.List<PlayerResult> Players { get; set; } }
	public sealed class PartyRequest { public string Login { get; set; } public long PartyId { get; set; } public string Host { get; set; } public int Port { get; set; } public string Mode { get; set; } public int TeamSize { get; set; } }
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
			var parties = new PartyBook();
			var comp = ManageCompLink.FromEnvironment();
			string mcUrl = o.ManageCompUrl ?? Environment.GetEnvironmentVariable("MANAGECOMP_URL"), mcKey = o.ManageCompKey ?? Environment.GetEnvironmentVariable("MANAGECOMP_API_KEY");
			var tournaments = new TournamentService(store, new ManageCompApi(o.ManageCompHandler != null ? new System.Net.Http.HttpClient(o.ManageCompHandler) : new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) }, mcUrl, mcKey));
			var seen = new ConcurrentDictionary<long, DateTime>();
			var builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();
			builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
			builder.Logging.SetMinimumLevel(LogLevel.Warning);
			builder.WebHost.UseUrls($"http://{o.Bind}:{o.Port}");
			builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);
			var app = builder.Build();

			var loginThrottle = new Throttle(5, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
			var registerThrottle = new Throttle(10, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
			var telemetryThrottle = new Throttle(120, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

			// statistiques d'utilisation anonymes : base à part, photo du nombre de joueurs chaque minute
			string telemetryPath = o.TelemetryDbPath ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.DbPath)) ?? ".", "telemetry.db");
			var telemetry = new TelemetryStore(telemetryPath);
			var telemetryTimer = new System.Threading.Timer(_ => { try { telemetry.Tick(); } catch (Exception e) { Console.Error.WriteLine("Télémétrie : " + e.Message); } }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
			app.Lifetime.ApplicationStopping.Register(() => telemetryTimer.Dispose());
			// tournois à heure fixe : lancés (ou annulés faute d'inscrits) à l'heure prévue
			int tournamentTickBusy = 0;
			var tournamentTimer = new System.Threading.Timer(_ =>
			{
				if (System.Threading.Interlocked.Exchange(ref tournamentTickBusy, 1) == 1) return;
				try { tournaments.Tick().GetAwaiter().GetResult(); }
				catch (Exception e) { Console.Error.WriteLine("Tournois : " + e.Message); }
				finally { System.Threading.Interlocked.Exchange(ref tournamentTickBusy, 0); }
			}, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
			app.Lifetime.ApplicationStopping.Register(() => tournamentTimer.Dispose());

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
				var last = store.LastMatch(a.Id);
				var lastInfo = last == null ? null : Modes.All.FirstOrDefault(x => x.Key == last.Mode);
				return new
				{
					id = a.Id, login = a.Login, created = a.Created, selectedCharacter = a.SelectedCharacter,
					general = new { rating = general.rating, rank = general.rank, matches = general.matches },
					lastMatch = last == null || lastInfo == null ? null : new
					{
						at = last.At, mode = last.Mode, label = lastInfo.Label, teams = lastInfo.TwoTeams, teamSize = last.TeamSize, seconds = last.Seconds, placement = last.Placement,
						players = last.Players, kills = last.Kills, deaths = last.Deaths, damage = last.Damage, won = last.Won, delta = last.Delta, rating = last.Rating
					},
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


			// tournois annoncés (publiés dans ManageComp) : visibles sans compte, même depuis l'écran de connexion
			app.MapGet("/api/tournaments", (HttpContext ctx) =>
			{
				string Name(long id) => store.FindById(id)?.Login ?? "?";
				return Results.Json(new { configured = tournaments.Configured, tournaments = comp.Tournaments(), games = tournaments.List(Account(ctx), Name) });
			});
			app.MapGet("/api/tournaments/{id:long}", async (HttpContext ctx, long id) =>
			{
				var d = await tournaments.Details(id, Account(ctx), l => store.FindById(l)?.Login ?? "?");
				return d == null ? Error(404, "Tournoi introuvable.") : Results.Json(d);
			});
			app.MapGet("/api/tournaments/{id:long}/me", async (HttpContext ctx, long id) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var r = await tournaments.MyRace(id, me);
				return r == null ? Error(404, "Pas de manche à jouer pour toi.") : Results.Json(r);
			});
			app.MapPost("/api/tournaments", async (HttpContext ctx) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				TournamentRequest req;
				try { req = await ctx.Request.ReadFromJsonAsync<TournamentRequest>() ?? new TournamentRequest(); }
				catch (System.Text.Json.JsonException) { return Error(400, "Configuration illisible."); }
				var r = req.Config != null
					? await tournaments.Create(me, req.Config)
					: await tournaments.Create(me, req.Title, req.StartAt, req.Races, req.LobbySize, string.IsNullOrEmpty(req.Mode) ? "br" : req.Mode, req.TeamSize, req.ScoreLimit);
				if (r.Error != null) return Error(400, r.Error);
				// l'organisateur joue aussi s'il le souhaite (il peut se désinscrire) ; par équipes, il devient chef d'équipe et invite ses coéquipiers
				if (req.Participate ?? true) await tournaments.Join(r.Id, me);
				return Results.Json(new { id = r.Id });
			});
			app.MapPut("/api/tournaments/{id:long}", async (HttpContext ctx, long id) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				TournamentRequest req;
				try { req = await ctx.Request.ReadFromJsonAsync<TournamentRequest>() ?? new TournamentRequest(); }
				catch (System.Text.Json.JsonException) { return Error(400, "Configuration illisible."); }
				var r = tournaments.Update(id, me, req.Config);
				return r.Error != null ? Error(400, r.Error) : Results.Json(new { ok = true });
			});
			app.MapPost("/api/tournaments/{id:long}/{action}", async (HttpContext ctx, long id, string action) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				TournamentTeamRequest team = null;
				if (action is "invite" or "kick" or "accept" or "decline")
				{
					try { team = ctx.Request.ContentLength > 0 ? await ctx.Request.ReadFromJsonAsync<TournamentTeamRequest>() : null; }
					catch (System.Text.Json.JsonException) { return Error(400, "Demande illisible."); }
					team ??= new TournamentTeamRequest();
				}
				TournamentResult r = action switch
				{
					"join" => await tournaments.Join(id, me),
					"invite" => tournaments.Invite(id, me, team.Login),
					"kick" => tournaments.Kick(id, me, team.Login),
					"accept" => tournaments.Accept(id, me, team.Reference),
					"decline" => tournaments.Decline(id, me, team.Reference),
					"leave" => await tournaments.Leave(id, me),
					"start" => await tournaments.Start(id, me),
					"checkin" => tournaments.CheckIn(id, me),
					"cancel" => await tournaments.Cancel(id, me),
					_ => TournamentResult.Fail("Action inconnue."),
				};
				return r.Error != null ? Error(400, r.Error) : Results.Json(new { ok = true });
			});

			// ------------------------------------------------------------------ groupes (squads)
			object PartyJson(long me)
			{
				var v = parties.View(me);
				string Login(long id) => store.FindById(id)?.Login ?? "?";
				return new
				{
					party = v.Party == null ? null : new
					{
						id = v.Party.Id, leader = Login(v.Party.Leader), isLeader = v.Party.Leader == me,
						members = v.Party.Members.Select(m => new { id = m, login = Login(m) }),
						pending = v.Party.Invites.Keys.Select(Login),
						match = v.Party.Match == null ? null : new { seq = v.Party.Match.Seq, host = v.Party.Match.Host, port = v.Party.Match.Port, mode = v.Party.Match.Mode, teamSize = v.Party.Match.TeamSize }
					},
					invites = v.Invites.Select(i => new { partyId = i.partyId, from = Login(i.fromAccount) }),
				};
			}
			IResult PartyReply(long me, string error) => error != null ? Error(400, error) : Results.Json(PartyJson(me));

			app.MapGet("/api/party", (HttpContext ctx) =>
			{
				long me = Account(ctx);
				if (me != 0) seen[me] = DateTime.UtcNow;
				return me == 0 ? Error(401, "Session expirée : reconnecte-toi.") : Results.Json(PartyJson(me));
			});
			app.MapPost("/api/party/{action}", async (HttpContext ctx, string action) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var req = (ctx.Request.ContentLength ?? 0) > 0 ? await ctx.Request.ReadFromJsonAsync<PartyRequest>() : null;
				req ??= new PartyRequest();
				AccountRow Target() => string.IsNullOrWhiteSpace(req.Login) ? null : store.FindByLogin(req.Login.Trim());
				switch (action)
				{
					case "invite": { var t = Target(); return t == null ? Error(404, "Aucun joueur de ce nom.") : PartyReply(me, parties.Invite(me, t.Id)); }
					case "accept": return PartyReply(me, parties.Accept(me, req.PartyId));
					case "decline": parties.Decline(me, req.PartyId); return PartyReply(me, null);
					case "leave": parties.Leave(me); return PartyReply(me, null);
					case "kick": { var t = Target(); return t == null ? Error(404, "Aucun joueur de ce nom.") : PartyReply(me, parties.Kick(me, t.Id)); }
					case "promote": { var t = Target(); return t == null ? Error(404, "Aucun joueur de ce nom.") : PartyReply(me, parties.Promote(me, t.Id)); }
					case "match": return PartyReply(me, parties.SetMatch(me, string.IsNullOrWhiteSpace(req.Host) ? null : req.Host, req.Port, req.Mode, req.TeamSize));
					default: return Error(404, "Action inconnue.");
				}
			});


			// ------------------------------------------------------------------ amis et présence
			// « en ligne » = a interrogé le service dans les 45 dernières secondes (le jeu le fait toutes les quelques secondes)
			bool Online(long id) => seen.TryGetValue(id, out var t) && (DateTime.UtcNow - t).TotalSeconds < 45;
			object FriendsJson(long me) => new
			{
				friends = store.Friends(me).Select(f =>
				{
					var a = store.FindById(f.id);
					return new { login = a?.Login ?? "?", state = f.state, online = f.state == "friend" && Online(f.id), partyId = parties.PartyOf(f.id) };
				}).OrderByDescending(f => f.state == "received").ThenByDescending(f => f.online).ThenBy(f => f.login, StringComparer.OrdinalIgnoreCase),
			};
			app.MapGet("/api/friends", (HttpContext ctx) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				seen[me] = DateTime.UtcNow;
				return Results.Json(FriendsJson(me));
			});
			app.MapPost("/api/friends/{action}", async (HttpContext ctx, string action) =>
			{
				long me = Account(ctx);
				if (me == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var req = (ctx.Request.ContentLength ?? 0) > 0 ? await ctx.Request.ReadFromJsonAsync<PartyRequest>() : null;
				var other = string.IsNullOrWhiteSpace(req?.Login) ? null : store.FindByLogin(req.Login.Trim());
				if (other == null) return Error(404, "Aucun joueur de ce nom.");
				switch (action)
				{
					case "add": { string e = store.AddFriend(me, other.Id); return e != null ? Error(400, e) : Results.Json(FriendsJson(me)); }
					case "accept": return store.AcceptFriend(me, other.Id) ? Results.Json(FriendsJson(me)) : Error(400, "Cette demande n'existe plus.");
					case "remove": store.RemoveFriend(me, other.Id); return Results.Json(FriendsJson(me));
					default: return Error(404, "Action inconnue.");
				}
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
				return store.ReplaceCharacters(acc, list, req?.Selected ?? 0) ? Results.Ok() : Error(409, "Tu as atteint le nombre maximal de personnages.");
			});

			app.MapPost("/api/characters", async (HttpContext ctx) =>
			{
				long acc = Account(ctx); if (acc == 0) return Error(401, "Session expirée : reconnecte-toi.");
				var req = await ctx.Request.ReadFromJsonAsync<CharacterRequest>();
				string name = CleanName(req?.Name);
				if (name == null || !GoodAppearance(req.Appearance)) return Error(400, "Personnage invalide (nom de 1 à 24 caractères).");
				long id = store.AddCharacter(acc, name, req.Appearance);
				if (id == 0) return Error(409, "Tu as atteint le nombre maximal de personnages.");
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

			// statistiques d'utilisation anonymes envoyées par le jeu (sans compte : identifiant aléatoire de l'installation)
			app.MapPost("/api/telemetry", async (HttpContext ctx) =>
			{
				string ip = Ip(ctx);
				if (telemetryThrottle.IsBlocked(ip)) return Error(429, "Trop d'envois.");
				telemetryThrottle.Fail(ip);           // compte chaque envoi : au-delà de 120 en 10 min (plusieurs joueurs derrière la même box), on refuse un moment
				TelemetryBatch batch;
				try { batch = await ctx.Request.ReadFromJsonAsync<TelemetryBatch>(); }
				catch (Exception) { return Error(400, "Paquet invalide."); }
				return telemetry.Ingest(batch) ? Results.Ok() : Error(400, "Paquet invalide.");
			});

			// routes réservées aux serveurs de partie
			app.MapPost("/internal/telemetry/servers", async (HttpContext ctx) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				telemetry.ReportServers(await ctx.Request.ReadFromJsonAsync<ServersSnapshot>());
				return Results.Ok();
			});
			app.MapGet("/internal/telemetry/stats", (HttpContext ctx, int? days) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				return Results.Json(telemetry.Stats(days ?? 1));
			});
			app.MapPost("/internal/verify", async (HttpContext ctx) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var req = await ctx.Request.ReadFromJsonAsync<VerifyRequest>();
				long acc = store.ValidateSession(req?.Token);
				if (acc == 0) return Error(401, "Jeton invalide.");
				var a = store.FindById(acc);
				var ch = store.Characters(acc).FirstOrDefault(c => c.Id == a.SelectedCharacter) ?? store.Characters(acc).FirstOrDefault();
				return Results.Json(new { accountId = a.Id, login = a.Login, partyId = parties.PartyOf(a.Id), partySize = parties.SizeOf(a.Id), character = ch == null ? null : new { id = ch.Id, name = ch.Name, appearance = ch.Appearance } });
			});

			app.MapGet("/internal/tournament/{id:long}/lobby/{lobby:int}", async (HttpContext ctx, long id, int lobby) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var game = await tournaments.LobbyGame(id, lobby);
				return game == null ? Error(404, "Tournoi introuvable ou pas en série de manches.") : Results.Json(game);
			});
			app.MapGet("/internal/tournament/{id:long}/match/{key}", async (HttpContext ctx, long id, string key) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var game = await tournaments.MatchGame(id, key);
				return game == null ? Error(404, "Match introuvable ou déjà joué.") : Results.Json(game);
			});
			app.MapPost("/internal/tournament-match", async (HttpContext ctx) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var rep = await ctx.Request.ReadFromJsonAsync<MatchReportRequest>();
				if (rep == null) return Error(400, "Rapport invalide.");
				var r = await tournaments.ReportMatch(rep.TournamentId, rep.Key, rep.ScoreA, rep.ScoreB);
				return r.Error != null ? Error(400, r.Error) : Results.Json(new { ok = true });
			});
			app.MapPost("/internal/tournament-race", async (HttpContext ctx) =>
			{
				if (!ServerAuth(ctx)) return Error(403, "Clé de serveur invalide.");
				var report = await ctx.Request.ReadFromJsonAsync<RaceReport>();
				if (report?.Players == null) return Error(400, "Rapport invalide.");
				var r = await tournaments.ReportRace(report.TournamentId, report.Race, report.Lobby, report.Players.Select(p => (p.AccountId, p.Placement)).ToList());
				return r.Error != null ? Error(400, r.Error) : Results.Json(new { ok = true });
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
