using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenEsport.Core;

namespace OpenEsport.Accounts
{
	public sealed class AccountRow { public long Id; public string Login; public byte[] Hash, Salt; public int Iterations; public long Created, SelectedCharacter; }
	public sealed class CharacterRow { public long Id; public string Name, Appearance; }
	public sealed class ModeStatsRow
	{
		public string Mode; public int Matches, Wins, Kills, Deaths, Rating = Ratings.Start, BestPlacement, Rank;
		public long Damage;
	}
	/// <summary>Une partie jouée par un compte (historique) : la page profil et le menu affichent la dernière.</summary>
	public sealed class MatchHistoryRow
	{
		public long At; public string Mode; public int TeamSize, Seconds, Placement, Players, Kills, Deaths, Delta, Rating; public long Damage; public bool Won;
	}
	public sealed class LeaderboardRow
	{
		public int Rank { get; set; }
		public long AccountId { get; set; }
		public string Login { get; set; }
		public int Rating { get; set; }
		public int Matches { get; set; }
		public int Wins { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
	}

	/// <summary>Résultat d'un joueur à la fin d'une partie, tel que le rapporte le serveur de partie.</summary>
	public sealed class PlayerResult
	{
		public long AccountId { get; set; }
		public int Team { get; set; }
		public int Placement { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
		public long Damage { get; set; }
		/// <summary>Temps passé dans la partie (s).</summary>
		public int Seconds { get; set; }
	}

	/// <summary>
	/// Rapport de fin de partie. <see cref="MatchId"/> sert à ne compter une partie qu'une fois (le serveur peut renvoyer son rapport).
	/// <see cref="WinnerTeam"/> : l'équipe gagnante (−1 = égalité). <see cref="PlayerCount"/> : joueurs de la partie, bots compris (pour les classements par place).
	/// </summary>
	public sealed class MatchReport
	{
		public string MatchId { get; set; }
		public string Mode { get; set; }
		public int WinnerTeam { get; set; } = -1;
		public int PlayerCount { get; set; }
		public int TeamSize { get; set; } = 1;
		public List<PlayerResult> Players { get; set; } = new List<PlayerResult>();
	}

	/// <summary>Base SQLite : comptes, sessions, personnages, statistiques par mode.</summary>
	public sealed partial class AccountStore
	{
		public const int MaxCharacters = 12, SessionDays = 30, MinGeneralMatches = 3;
		private readonly string _connection;
		private readonly object _gate = new object();

		public AccountStore(string path)
		{
			_connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
			Run(c =>
			{
				Exec(c, "PRAGMA journal_mode=WAL;");
				Exec(c, "CREATE TABLE IF NOT EXISTS accounts(id INTEGER PRIMARY KEY AUTOINCREMENT, login TEXT NOT NULL UNIQUE COLLATE NOCASE, hash BLOB NOT NULL, salt BLOB NOT NULL, iterations INTEGER NOT NULL, created INTEGER NOT NULL, selected_character INTEGER NOT NULL DEFAULT 0)");
				Exec(c, "CREATE TABLE IF NOT EXISTS sessions(token_hash BLOB PRIMARY KEY, account_id INTEGER NOT NULL, expires INTEGER NOT NULL)");
				Exec(c, "CREATE TABLE IF NOT EXISTS characters(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, name TEXT NOT NULL, appearance TEXT NOT NULL, created INTEGER NOT NULL)");
				Exec(c, "CREATE INDEX IF NOT EXISTS characters_account ON characters(account_id)");
				Exec(c, "CREATE TABLE IF NOT EXISTS mode_stats(account_id INTEGER NOT NULL, mode TEXT NOT NULL, matches INTEGER NOT NULL DEFAULT 0, wins INTEGER NOT NULL DEFAULT 0, kills INTEGER NOT NULL DEFAULT 0, deaths INTEGER NOT NULL DEFAULT 0, damage INTEGER NOT NULL DEFAULT 0, rating INTEGER NOT NULL DEFAULT 1000, best_placement INTEGER NOT NULL DEFAULT 0, last_played INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(account_id, mode))");
				Exec(c, "CREATE INDEX IF NOT EXISTS mode_stats_rank ON mode_stats(mode, rating DESC)");
				Exec(c, "CREATE TABLE IF NOT EXISTS matches(id TEXT PRIMARY KEY, at INTEGER NOT NULL)");
				Exec(c, "CREATE TABLE IF NOT EXISTS match_history(account_id INTEGER NOT NULL, at INTEGER NOT NULL, mode TEXT NOT NULL, team_size INTEGER NOT NULL, seconds INTEGER NOT NULL, placement INTEGER NOT NULL, players INTEGER NOT NULL, kills INTEGER NOT NULL, deaths INTEGER NOT NULL, damage INTEGER NOT NULL, won INTEGER NOT NULL, delta INTEGER NOT NULL, rating INTEGER NOT NULL)");
				Exec(c, "CREATE INDEX IF NOT EXISTS match_history_account ON match_history(account_id, at DESC)");
				Exec(c, "CREATE TABLE IF NOT EXISTS friends(a INTEGER NOT NULL, b INTEGER NOT NULL, accepted INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL, PRIMARY KEY(a, b))");
				Exec(c, "CREATE INDEX IF NOT EXISTS friends_b ON friends(b)");
				Exec(c, "CREATE TABLE IF NOT EXISTS tournaments(id INTEGER PRIMARY KEY AUTOINCREMENT, contest TEXT NOT NULL, title TEXT NOT NULL, mode TEXT NOT NULL, team_size INTEGER NOT NULL, races INTEGER NOT NULL, lobby_size INTEGER NOT NULL, start_at INTEGER NOT NULL, owner INTEGER NOT NULL, state TEXT NOT NULL, race INTEGER NOT NULL DEFAULT 0, score_limit INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL)");
				Exec(c, "CREATE TABLE IF NOT EXISTS tournament_players(tid INTEGER NOT NULL, account INTEGER NOT NULL, ref INTEGER NOT NULL, PRIMARY KEY(tid, account))");
				// tournois : configuration complète (JSON), phase en cours et numéro de la première manche de cette phase ; présence confirmée (check-in)
				AddColumn(c, "tournaments", "config", "TEXT NOT NULL DEFAULT ''");
				AddColumn(c, "tournaments", "phase", "INTEGER NOT NULL DEFAULT 0");
				AddColumn(c, "tournaments", "race_base", "INTEGER NOT NULL DEFAULT 0");
				AddColumn(c, "tournaments", "note", "TEXT NOT NULL DEFAULT ''");
				AddColumn(c, "tournament_players", "checked_in", "INTEGER NOT NULL DEFAULT 0");
				AddColumn(c, "tournament_players", "captain", "INTEGER NOT NULL DEFAULT 0");
				// invitations dans une équipe de tournoi : le chef propose, l'invité accepte ou refuse
				Exec(c, "CREATE TABLE IF NOT EXISTS tournament_invites(tid INTEGER NOT NULL, account INTEGER NOT NULL, ref INTEGER NOT NULL, inviter INTEGER NOT NULL, created INTEGER NOT NULL, PRIMARY KEY(tid, account, ref))");
				return 0;
			});
		}

		public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		/// <summary>Ajoute une colonne à une table existante si elle n'y est pas encore (mise à jour d'une base déjà en service).</summary>
		private static void AddColumn(SqliteConnection c, string table, string column, string definition)
		{
			using (var cmd = c.CreateCommand())
			{
				cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
				if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return;
			}
			Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
		}

		private T Run<T>(Func<SqliteConnection, T> body)
		{
			lock (_gate)
			{
				using var c = new SqliteConnection(_connection);
				c.Open();
				return body(c);
			}
		}

		private static void Exec(SqliteConnection c, string sql, params (string, object)[] args)
		{
			using var cmd = c.CreateCommand();
			cmd.CommandText = sql;
			foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
			cmd.ExecuteNonQuery();
		}

		private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string, object)[] args)
		{
			using var cmd = c.CreateCommand();
			cmd.CommandText = sql;
			foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
			using var r = cmd.ExecuteReader();
			var list = new List<T>();
			while (r.Read()) list.Add(read(r));
			return list;
		}

		private static long LastId(SqliteConnection c) => Query(c, "SELECT last_insert_rowid()", r => r.GetInt64(0))[0];
		private static int Count(SqliteConnection c, string sql, params (string, object)[] args) => Query(c, sql, r => r.GetInt32(0), args)[0];

		// ------------------------------------------------------------------ comptes

		/// <summary>Crée un compte ; retourne son identifiant, ou 0 si l'identifiant de connexion est déjà pris.</summary>
		public long CreateAccount(string login, byte[] hash, byte[] salt, int iterations) => Run(c =>
		{
			try
			{
				Exec(c, "INSERT INTO accounts(login, hash, salt, iterations, created) VALUES(@l, @h, @s, @i, @t)", ("@l", login), ("@h", hash), ("@s", salt), ("@i", iterations), ("@t", Now()));
				return LastId(c);
			}
			catch (SqliteException e) when (e.SqliteErrorCode == 19) { return 0L; }       // contrainte d'unicité
		});

		private static AccountRow ReadAccount(SqliteDataReader r) => new AccountRow
		{
			Id = r.GetInt64(0), Login = r.GetString(1), Hash = (byte[])r[2], Salt = (byte[])r[3], Iterations = r.GetInt32(4), Created = r.GetInt64(5), SelectedCharacter = r.GetInt64(6)
		};
		private const string AccountColumns = "id, login, hash, salt, iterations, created, selected_character";

		public AccountRow FindByLogin(string login) => Run(c => Query(c, "SELECT " + AccountColumns + " FROM accounts WHERE login = @l", ReadAccount, ("@l", login)).FirstOrDefault());
		public AccountRow FindById(long id) => Run(c => Query(c, "SELECT " + AccountColumns + " FROM accounts WHERE id = @i", ReadAccount, ("@i", id)).FirstOrDefault());

		// ------------------------------------------------------------------ sessions

		private static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

		/// <summary>Ouvre une session : retourne le jeton (seul son condensé est conservé en base).</summary>
		public string CreateSession(long accountId) => Run(c =>
		{
			string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
			Exec(c, "DELETE FROM sessions WHERE expires < @n", ("@n", Now()));
			Exec(c, "INSERT INTO sessions(token_hash, account_id, expires) VALUES(@h, @a, @e)", ("@h", HashToken(token)), ("@a", accountId), ("@e", Now() + SessionDays * 86400L));
			return token;
		});

		/// <summary>Le compte d'un jeton valide, ou 0.</summary>
		public long ValidateSession(string token)
		{
			if (string.IsNullOrEmpty(token) || token.Length > 128) return 0;
			return Run(c => Query(c, "SELECT account_id FROM sessions WHERE token_hash = @h AND expires >= @n", r => r.GetInt64(0), ("@h", HashToken(token)), ("@n", Now())).FirstOrDefault());
		}

		public void DeleteSession(string token) { if (!string.IsNullOrEmpty(token)) Run(c => { Exec(c, "DELETE FROM sessions WHERE token_hash = @h", ("@h", HashToken(token))); return 0; }); }

		// ------------------------------------------------------------------ personnages

		public List<CharacterRow> Characters(long accountId) => Run(c => Query(c, "SELECT id, name, appearance FROM characters WHERE account_id = @a ORDER BY id", r => new CharacterRow { Id = r.GetInt64(0), Name = r.GetString(1), Appearance = r.GetString(2) }, ("@a", accountId)));

		/// <summary>Ajoute un personnage ; retourne son identifiant, ou 0 si le compte en a déjà le maximum.</summary>
		public long AddCharacter(long accountId, string name, string appearance) => Run(c =>
		{
			if (Count(c, "SELECT COUNT(*) FROM characters WHERE account_id = @a", ("@a", accountId)) >= MaxCharacters) return 0L;
			Exec(c, "INSERT INTO characters(account_id, name, appearance, created) VALUES(@a, @n, @p, @t)", ("@a", accountId), ("@n", name), ("@p", appearance), ("@t", Now()));
			return LastId(c);
		});

		/// <summary>Remplace toute la liste des personnages du compte (le jeu synchronise sa bibliothèque d'un coup) et choisit celui d'indice <paramref name="selected"/>. Retourne faux si la liste dépasse le maximum.</summary>
		public bool ReplaceCharacters(long accountId, List<(string name, string appearance)> list, int selected) => Run(c =>
		{
			if (list.Count > MaxCharacters) return false;
			using var tx = c.BeginTransaction();
			Exec(c, "DELETE FROM characters WHERE account_id = @a", ("@a", accountId));
			long chosen = 0;
			for (int i = 0; i < list.Count; i++)
			{
				Exec(c, "INSERT INTO characters(account_id, name, appearance, created) VALUES(@a, @n, @p, @t)", ("@a", accountId), ("@n", list[i].name), ("@p", list[i].appearance), ("@t", Now()));
				if (i == selected) chosen = LastId(c);
			}
			Exec(c, "UPDATE accounts SET selected_character = @s WHERE id = @a", ("@s", chosen), ("@a", accountId));
			tx.Commit();
			return true;
		});

		public bool UpdateCharacter(long accountId, long id, string name, string appearance) => Run(c =>
		{
			using var cmd = c.CreateCommand();
			cmd.CommandText = "UPDATE characters SET name = @n, appearance = @p WHERE id = @i AND account_id = @a";
			cmd.Parameters.AddWithValue("@n", name); cmd.Parameters.AddWithValue("@p", appearance); cmd.Parameters.AddWithValue("@i", id); cmd.Parameters.AddWithValue("@a", accountId);
			return cmd.ExecuteNonQuery() > 0;
		});

		public bool DeleteCharacter(long accountId, long id) => Run(c =>
		{
			using var cmd = c.CreateCommand();
			cmd.CommandText = "DELETE FROM characters WHERE id = @i AND account_id = @a";
			cmd.Parameters.AddWithValue("@i", id); cmd.Parameters.AddWithValue("@a", accountId);
			bool done = cmd.ExecuteNonQuery() > 0;
			if (done) Exec(c, "UPDATE accounts SET selected_character = 0 WHERE id = @a AND selected_character = @i", ("@a", accountId), ("@i", id));
			return done;
		});

		public bool SelectCharacter(long accountId, long id) => Run(c =>
		{
			if (Count(c, "SELECT COUNT(*) FROM characters WHERE id = @i AND account_id = @a", ("@i", id), ("@a", accountId)) == 0) return false;
			Exec(c, "UPDATE accounts SET selected_character = @i WHERE id = @a", ("@i", id), ("@a", accountId));
			return true;
		});


		// ------------------------------------------------------------------ amis

		public const int MaxFriends = 200;

		/// <summary>Ajoute un ami : demande envoyée, ou acceptée tout de suite si l'autre joueur nous avait déjà demandé. Retourne un message d'erreur ou null.</summary>
		public string AddFriend(long me, long other) => Run(c =>
		{
			if (me == other) return "Tu ne peux pas t'ajouter toi-même.";
			if (Count(c, "SELECT COUNT(*) FROM friends WHERE (a = @m OR b = @m)", ("@m", me)) >= MaxFriends) return "Ta liste d'amis est pleine.";
			if (Count(c, "SELECT COUNT(*) FROM friends WHERE a = @a AND b = @b", ("@a", me), ("@b", other)) > 0) return null;       // déjà demandé ou déjà amis
			if (Count(c, "SELECT COUNT(*) FROM friends WHERE a = @a AND b = @b", ("@a", other), ("@b", me)) > 0)
			{
				Exec(c, "UPDATE friends SET accepted = 1 WHERE a = @a AND b = @b", ("@a", other), ("@b", me));
				return null;
			}
			Exec(c, "INSERT INTO friends(a, b, accepted, created) VALUES(@a, @b, 0, @t)", ("@a", me), ("@b", other), ("@t", Now()));
			return null;
		});

		/// <summary>Accepte une demande reçue (refuser = <see cref="RemoveFriend"/>).</summary>
		public bool AcceptFriend(long me, long from) => Run(c =>
		{
			if (Count(c, "SELECT COUNT(*) FROM friends WHERE a = @a AND b = @b AND accepted = 0", ("@a", from), ("@b", me)) == 0) return false;
			Exec(c, "UPDATE friends SET accepted = 1 WHERE a = @a AND b = @b", ("@a", from), ("@b", me));
			return true;
		});

		/// <summary>Retire un ami, annule une demande envoyée ou refuse une demande reçue.</summary>
		public void RemoveFriend(long me, long other) => Run(c =>
		{
			Exec(c, "DELETE FROM friends WHERE (a = @m AND b = @o) OR (a = @o AND b = @m)", ("@m", me), ("@o", other));
			return 0;
		});

		/// <summary>Amis du compte : (autre compte, état) avec état « friend », « sent » (demande envoyée) ou « received » (demande à accepter).</summary>
		public List<(long id, string state)> Friends(long me) => Run(c =>
			Query(c, "SELECT a, b, accepted FROM friends WHERE a = @m OR b = @m ORDER BY created", r =>
			{
				long a = r.GetInt64(0), b = r.GetInt64(1); bool ok = r.GetInt32(2) != 0;
				return (a == me ? b : a, ok ? "friend" : a == me ? "sent" : "received");
			}, ("@m", me)));

		// ------------------------------------------------------------------ statistiques et classements

		/// <summary>Statistiques du compte pour chaque mode (les modes jamais joués ont des zéros et le score de départ), avec son rang dans chaque mode.</summary>
		public List<ModeStatsRow> ModeStats(long accountId) => Run(c =>
		{
			var rows = Query(c, "SELECT mode, matches, wins, kills, deaths, damage, rating, best_placement FROM mode_stats WHERE account_id = @a", r => new ModeStatsRow
			{
				Mode = r.GetString(0), Matches = r.GetInt32(1), Wins = r.GetInt32(2), Kills = r.GetInt32(3), Deaths = r.GetInt32(4), Damage = r.GetInt64(5), Rating = r.GetInt32(6), BestPlacement = r.GetInt32(7)
			}, ("@a", accountId)).ToDictionary(x => x.Mode);
			var list = new List<ModeStatsRow>();
			foreach (var m in Modes.All)
			{
				if (!rows.TryGetValue(m.Key, out var row)) row = new ModeStatsRow { Mode = m.Key };
				row.Rank = row.Matches > 0 ? Count(c, "SELECT COUNT(*) FROM mode_stats WHERE mode = @m AND matches > 0 AND (rating > @r OR (rating = @r AND matches > @n))", ("@m", m.Key), ("@r", row.Rating), ("@n", row.Matches)) + 1 : 0;
				list.Add(row);
			}
			return list;
		});

		/// <summary>La dernière partie jouée par le compte (null s'il n'en a joué aucune depuis la tenue de l'historique).</summary>
		public MatchHistoryRow LastMatch(long accountId) => Run(c =>
			Query(c, "SELECT at, mode, team_size, seconds, placement, players, kills, deaths, damage, won, delta, rating FROM match_history WHERE account_id = @a ORDER BY at DESC, rowid DESC LIMIT 1",
				r => new MatchHistoryRow
				{
					At = r.GetInt64(0), Mode = r.GetString(1), TeamSize = r.GetInt32(2), Seconds = r.GetInt32(3), Placement = r.GetInt32(4), Players = r.GetInt32(5),
					Kills = r.GetInt32(6), Deaths = r.GetInt32(7), Damage = r.GetInt64(8), Won = r.GetInt32(9) != 0, Delta = r.GetInt32(10), Rating = r.GetInt32(11)
				}, ("@a", accountId)).FirstOrDefault());

		/// <summary>Classement général du compte : (score, rang, parties). Le rang est 0 tant que le joueur a moins de <see cref="MinGeneralMatches"/> parties.</summary>
		public (int rating, int rank, int matches) General(long accountId) => Run(c =>
		{
			var all = Query(c, "SELECT account_id, SUM(rating * matches) * 1.0 / SUM(matches), SUM(matches) FROM mode_stats WHERE matches > 0 GROUP BY account_id", r => (id: r.GetInt64(0), g: r.GetDouble(1), m: r.GetInt32(2)));
			var mine = all.FirstOrDefault(x => x.id == accountId);
			if (mine.m == 0) return (Ratings.Start, 0, 0);
			int rating = (int)Math.Round(mine.g);
			if (mine.m < MinGeneralMatches) return (rating, 0, mine.m);
			int rank = all.Count(x => x.m >= MinGeneralMatches && (Math.Round(x.g) > rating || (Math.Round(x.g) == rating && x.m > mine.m))) + 1;
			return (rating, rank, mine.m);
		});

		/// <summary>Meilleurs joueurs d'un mode (mode = null : classement général).</summary>
		public List<LeaderboardRow> Leaderboard(string mode, int top) => Run(c =>
		{
			top = Math.Clamp(top, 1, 100);
			List<LeaderboardRow> rows;
			Func<SqliteDataReader, LeaderboardRow> read = r => new LeaderboardRow { AccountId = r.GetInt64(0), Login = r.GetString(1), Rating = r.GetInt32(2), Matches = r.GetInt32(3), Wins = r.GetInt32(4), Kills = r.GetInt32(5), Deaths = r.GetInt32(6) };
			if (mode == null)
				rows = Query(c, "SELECT s.account_id, a.login, CAST(ROUND(SUM(s.rating * s.matches) * 1.0 / SUM(s.matches)) AS INTEGER) AS g, SUM(s.matches) AS m, SUM(s.wins), SUM(s.kills), SUM(s.deaths) "
					+ "FROM mode_stats s JOIN accounts a ON a.id = s.account_id WHERE s.matches > 0 GROUP BY s.account_id HAVING SUM(s.matches) >= @min ORDER BY g DESC, m DESC LIMIT @top", read, ("@min", MinGeneralMatches), ("@top", top));
			else
				rows = Query(c, "SELECT s.account_id, a.login, s.rating, s.matches, s.wins, s.kills, s.deaths FROM mode_stats s JOIN accounts a ON a.id = s.account_id "
					+ "WHERE s.mode = @m AND s.matches > 0 ORDER BY s.rating DESC, s.matches DESC LIMIT @top", read, ("@m", mode), ("@top", top));
			for (int i = 0; i < rows.Count; i++) rows[i].Rank = i + 1;
			return rows;
		});

		/// <summary>Applique le rapport d'une partie : statistiques et scores des joueurs ayant un compte. Retourne faux si cette partie était déjà comptée ou si le mode est inconnu.</summary>
		public bool ApplyMatch(MatchReport report)
		{
			if (report == null || string.IsNullOrEmpty(report.MatchId) || report.MatchId.Length > 64 || !Modes.TryParse(report.Mode, out var mode)) return false;
			string key = Modes.Get(mode).Key;
			bool teams = Modes.Get(mode).TwoTeams;
			return Run(c =>
			{
				using var tx = c.BeginTransaction();
				if (Count(c, "SELECT COUNT(*) FROM matches WHERE id = @i", ("@i", report.MatchId)) > 0) return false;
				Exec(c, "INSERT INTO matches(id, at) VALUES(@i, @t)", ("@i", report.MatchId), ("@t", Now()));

				var players = (report.Players ?? new List<PlayerResult>()).Where(p => p != null && p.AccountId > 0).GroupBy(p => p.AccountId).Select(g => g.First()).ToList();
				var current = new Dictionary<long, ModeStatsRow>();
				foreach (var p in players)
				{
					var row = Query(c, "SELECT matches, wins, kills, deaths, damage, rating, best_placement FROM mode_stats WHERE account_id = @a AND mode = @m",
						r => new ModeStatsRow { Mode = key, Matches = r.GetInt32(0), Wins = r.GetInt32(1), Kills = r.GetInt32(2), Deaths = r.GetInt32(3), Damage = r.GetInt64(4), Rating = r.GetInt32(5), BestPlacement = r.GetInt32(6) }, ("@a", p.AccountId), ("@m", key)).FirstOrDefault();
					current[p.AccountId] = row ?? new ModeStatsRow { Mode = key };
				}
				// moyennes de score par équipe (modes à deux équipes), calculées avant toute mise à jour
				double Avg(IEnumerable<PlayerResult> group) { var l = group.Select(p => (double)current[p.AccountId].Rating).ToList(); return l.Count == 0 ? Ratings.Start : l.Average(); }
				var deltas = new Dictionary<long, (int delta, bool won)>();
				foreach (var p in players)
				{
					var s = current[p.AccountId];
					if (teams)
					{
						bool won = report.WinnerTeam == p.Team;
						double result = report.WinnerTeam < 0 ? 0.5 : (won ? 1.0 : 0.0);
						deltas[p.AccountId] = (Ratings.TeamDelta(Avg(players.Where(q => q.Team == p.Team)), Avg(players.Where(q => q.Team != p.Team)), result, s.Matches), won);
					}
					else deltas[p.AccountId] = (Ratings.PlacementDelta(p.Placement, Math.Max(report.PlayerCount, p.Placement), p.Kills), p.Placement == 1);
				}
				foreach (var p in players)
				{
					var s = current[p.AccountId];
					var (delta, won) = deltas[p.AccountId];
					s.Matches++; if (won) s.Wins++;
					s.Kills += Math.Max(0, p.Kills); s.Deaths += Math.Max(0, p.Deaths); s.Damage += Math.Max(0, p.Damage);
					s.Rating = Math.Max(0, s.Rating + delta);
					if (!teams && p.Placement > 0 && (s.BestPlacement == 0 || p.Placement < s.BestPlacement)) s.BestPlacement = p.Placement;
					Exec(c, "INSERT INTO mode_stats(account_id, mode, matches, wins, kills, deaths, damage, rating, best_placement, last_played) VALUES(@a, @m, @n, @w, @k, @d, @g, @r, @b, @t) "
						+ "ON CONFLICT(account_id, mode) DO UPDATE SET matches = @n, wins = @w, kills = @k, deaths = @d, damage = @g, rating = @r, best_placement = @b, last_played = @t",
						("@a", p.AccountId), ("@m", key), ("@n", s.Matches), ("@w", s.Wins), ("@k", s.Kills), ("@d", s.Deaths), ("@g", s.Damage), ("@r", s.Rating), ("@b", s.BestPlacement), ("@t", Now()));
					// historique : cette partie telle que le joueur l'a vécue
					Exec(c, "INSERT INTO match_history(account_id, at, mode, team_size, seconds, placement, players, kills, deaths, damage, won, delta, rating) VALUES(@a, @t, @m, @s, @sec, @p, @n, @k, @d, @g, @w, @dl, @r)",
						("@a", p.AccountId), ("@t", Now()), ("@m", key), ("@s", Math.Clamp(report.TeamSize, 1, 10)), ("@sec", Math.Max(0, p.Seconds)), ("@p", Math.Max(0, p.Placement)), ("@n", Math.Max(0, report.PlayerCount)),
						("@k", Math.Max(0, p.Kills)), ("@d", Math.Max(0, p.Deaths)), ("@g", Math.Max(0, p.Damage)), ("@w", won ? 1 : 0), ("@dl", delta), ("@r", s.Rating));
				}
				tx.Commit();
				return true;
			});
		}
	}
}
