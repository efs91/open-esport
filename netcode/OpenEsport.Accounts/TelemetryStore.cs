using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenEsport.Accounts
{
	/// <summary>Un événement envoyé par le jeu : instant (s Unix), sorte (screen, menu, char_save…), nom, valeur (souvent une durée en s), précision libre.</summary>
	public sealed class TelemetryEvent
	{
		public long T { get; set; }
		public string K { get; set; }
		public string N { get; set; }
		public double V { get; set; }
		public string X { get; set; }
	}

	/// <summary>Paquet d'événements d'une session de jeu. <see cref="Anon"/> est un identifiant aléatoire de l'installation (renouvelé tous les 13 mois), jamais lié à un compte.</summary>
	public sealed class TelemetryBatch
	{
		public string Anon { get; set; }
		public string Session { get; set; }
		public string Version { get; set; }
		public string Os { get; set; }
		public string Lang { get; set; }
		public List<TelemetryEvent> Events { get; set; }
	}

	/// <summary>Joueurs en partie et en salle d'attente, par mode, tels que les rapporte le matchmaking chaque minute.</summary>
	public sealed class ServersSnapshot
	{
		public Dictionary<string, int> Playing { get; set; } = new();
		public Dictionary<string, int> Lobby { get; set; } = new();
	}

	/// <summary>
	/// Statistiques d'utilisation anonymes (mesure d'audience) : base SQLite à part (telemetry.db, à côté de celle des comptes).
	/// Rien n'y relie un événement à un compte. Les données de plus de 25 mois sont effacées (durée maximale admise par la CNIL pour la mesure d'audience).
	/// Chaque minute, une photo du nombre de joueurs : au menu (sessions qui ont donné signe de vie depuis 150 s), en salle d'attente et en partie (matchmaking).
	/// </summary>
	public sealed class TelemetryStore
	{
		public const int RetentionDays = 760;                // 25 mois
		public const int MenuAliveSeconds = 150;
		public const int MaxEventsPerBatch = 300;
		private readonly string _connection;
		private readonly object _gate = new object();
		private ServersSnapshot _servers = new ServersSnapshot();
		private long _serversAt;
		private long _lastPurge;

		public TelemetryStore(string path)
		{
			_connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
			Run(c =>
			{
				Exec(c, "PRAGMA journal_mode=WAL;");
				Exec(c, "CREATE TABLE IF NOT EXISTS sessions(session TEXT PRIMARY KEY, anon TEXT NOT NULL, first INTEGER NOT NULL, last INTEGER NOT NULL, version TEXT, os TEXT, lang TEXT)");
				Exec(c, "CREATE INDEX IF NOT EXISTS sessions_last ON sessions(last)");
				Exec(c, "CREATE INDEX IF NOT EXISTS sessions_first ON sessions(first)");
				Exec(c, "CREATE TABLE IF NOT EXISTS events(at INTEGER NOT NULL, session TEXT NOT NULL, kind TEXT NOT NULL, name TEXT, value REAL, extra TEXT)");
				Exec(c, "CREATE INDEX IF NOT EXISTS events_at ON events(at)");
				Exec(c, "CREATE TABLE IF NOT EXISTS minutes(at INTEGER PRIMARY KEY, menu INTEGER NOT NULL, lobby INTEGER NOT NULL, playing INTEGER NOT NULL, modes TEXT)");
				return 0;
			});
		}

		public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

		private static string Clip(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);
		private static bool Token(string s) => !string.IsNullOrEmpty(s) && s.Length >= 8 && s.Length <= 64 && s.All(ch => char.IsLetterOrDigit(ch) || ch == '-');

		/// <summary>Enregistre un paquet du jeu. Faux si le paquet est invalide.</summary>
		public bool Ingest(TelemetryBatch b)
		{
			if (b == null || !Token(b.Anon) || !Token(b.Session)) return false;
			long now = Now();
			var events = (b.Events ?? new List<TelemetryEvent>()).Take(MaxEventsPerBatch).ToList();
			return Run(c =>
			{
				using var tx = c.BeginTransaction();
				Exec(c, "INSERT INTO sessions(session, anon, first, last, version, os, lang) VALUES($s, $a, $n, $n, $v, $o, $l) ON CONFLICT(session) DO UPDATE SET last = MAX(last, $n)",
					("$s", b.Session), ("$a", b.Anon), ("$n", now), ("$v", Clip(b.Version, 16)), ("$o", Clip(b.Os, 16)), ("$l", Clip(b.Lang, 24)));
				foreach (var e in events)
				{
					if (string.IsNullOrEmpty(e?.K)) continue;
					long at = e.T > now - 86400 * 2 && e.T <= now + 60 ? e.T : now;      // horloge du joueur fausse : on prend l'heure d'arrivée
					double v = double.IsFinite(e.V) ? Math.Clamp(e.V, -1e7, 1e7) : 0;
					Exec(c, "INSERT INTO events(at, session, kind, name, value, extra) VALUES($t, $s, $k, $n, $v, $x)",
						("$t", at), ("$s", b.Session), ("$k", Clip(e.K, 24)), ("$n", Clip(e.N, 64)), ("$v", v), ("$x", Clip(e.X, 128)));
				}
				tx.Commit();
				return true;
			});
		}

		public void ReportServers(ServersSnapshot s)
		{
			lock (_gate) { _servers = s ?? new ServersSnapshot(); _serversAt = Now(); }
		}

		private ServersSnapshot Servers()
		{
			lock (_gate) return Now() - _serversAt <= 180 ? _servers : new ServersSnapshot();      // matchmaking muet depuis 3 min : personne en partie connu
		}

		private int MenuNow(SqliteConnection c) => Query(c, "SELECT COUNT(DISTINCT anon) FROM sessions WHERE last >= $t", r => r.GetInt32(0), ("$t", Now() - MenuAliveSeconds))[0];

		/// <summary>À appeler chaque minute : photo du nombre de joueurs, et ménage des données trop anciennes une fois par jour.</summary>
		public void Tick()
		{
			long now = Now();
			var s = Servers();
			int lobby = s.Lobby.Values.Sum(), playing = s.Playing.Values.Sum();
			var modes = s.Playing.Keys.Union(s.Lobby.Keys).ToDictionary(k => k, k => new { p = s.Playing.GetValueOrDefault(k), l = s.Lobby.GetValueOrDefault(k) });
			Run(c =>
			{
				Exec(c, "INSERT OR REPLACE INTO minutes(at, menu, lobby, playing, modes) VALUES($t, $m, $l, $p, $x)",
					("$t", now / 60 * 60), ("$m", MenuNow(c)), ("$l", lobby), ("$p", playing), ("$x", JsonSerializer.Serialize(modes)));
				if (now - _lastPurge > 86400)
				{
					_lastPurge = now;
					long old = now - RetentionDays * 86400L;
					Exec(c, "DELETE FROM events WHERE at < $o", ("$o", old));
					Exec(c, "DELETE FROM sessions WHERE last < $o", ("$o", old));
					Exec(c, "DELETE FROM minutes WHERE at < $o", ("$o", old));
				}
				return 0;
			});
		}

		/// <summary>Tableau de bord : tout ce qu'il faut sur les <paramref name="days"/> derniers jours.</summary>
		public object Stats(int days)
		{
			days = Math.Clamp(days, 1, RetentionDays);
			long now = Now(), from = now - days * 86400L;
			long bucket = days <= 1 ? 600 : days <= 7 ? 3600 : 86400;
			var s = Servers();
			return Run(c =>
			{
				int menu = MenuNow(c);
				var series = Query(c, "SELECT (at / $b) * $b AS t, AVG(menu + lobby + playing), MAX(menu + lobby + playing), AVG(menu), AVG(lobby), AVG(playing) FROM minutes WHERE at >= $f GROUP BY t ORDER BY t",
					r => new { t = r.GetInt64(0), avg = Math.Round(r.GetDouble(1), 1), max = r.GetInt32(2), menu = Math.Round(r.GetDouble(3), 1), lobby = Math.Round(r.GetDouble(4), 1), playing = Math.Round(r.GetDouble(5), 1) }, ("$b", bucket), ("$f", from));
				var daily = Query(c, "SELECT (first / 86400) * 86400 AS d, COUNT(DISTINCT anon), COUNT(*), AVG(last - first) FROM sessions WHERE first >= $f GROUP BY d ORDER BY d",
					r => new { day = r.GetInt64(0), players = r.GetInt32(1), sessions = r.GetInt32(2), avgSession = Math.Round(r.IsDBNull(3) ? 0 : r.GetDouble(3)) }, ("$f", from));
				var totals = Query(c, "SELECT COUNT(DISTINCT anon), COUNT(*), AVG(last - first) FROM sessions WHERE first >= $f",
					r => new { players = r.GetInt32(0), sessions = r.GetInt32(1), avgSession = Math.Round(r.IsDBNull(2) ? 0 : r.GetDouble(2)) }, ("$f", from))[0];
				int peak = Query(c, "SELECT COALESCE(MAX(menu + lobby + playing), 0) FROM minutes WHERE at >= $f", r => r.GetInt32(0), ("$f", from))[0];
				var screens = Query(c, "SELECT name, COUNT(*), AVG(value), SUM(value) FROM events WHERE kind = 'screen' AND at >= $f GROUP BY name ORDER BY SUM(value) DESC",
					r => new { name = r.GetString(0), visits = r.GetInt32(1), avgSeconds = Math.Round(r.GetDouble(2), 1), totalSeconds = Math.Round(r.GetDouble(3)) }, ("$f", from));
				var menuClicks = Query(c, "SELECT name, COUNT(*) FROM events WHERE kind = 'menu' AND at >= $f GROUP BY name ORDER BY COUNT(*) DESC LIMIT 40",
					r => new { name = r.GetString(0), clicks = r.GetInt32(1) }, ("$f", from));
				int Count(string kind) => Query(c, "SELECT COUNT(*) FROM events WHERE kind = $k AND at >= $f", r => r.GetInt32(0), ("$k", kind), ("$f", from))[0];
				double Avg(string kind) => Query(c, "SELECT COALESCE(AVG(value), 0) FROM events WHERE kind = $k AND at >= $f", r => r.GetDouble(0), ("$k", kind), ("$f", from))[0];
				var character = new { saved = Count("char_save"), cancelled = Count("char_cancel"), avgSavedSeconds = Math.Round(Avg("char_save")), avgCancelledSeconds = Math.Round(Avg("char_cancel")) };
				var matchmaking = Query(c, @"SELECT name,
						SUM(kind = 'mm_start' AND extra = 'quick'), SUM(kind = 'mm_start' AND extra = 'join'), SUM(kind = 'mm_found'), SUM(kind = 'mm_cancel'), SUM(kind = 'mm_fail'),
						AVG(CASE WHEN kind = 'mm_found' THEN value END)
					FROM events WHERE kind IN ('mm_start', 'mm_found', 'mm_cancel', 'mm_fail') AND at >= $f GROUP BY name ORDER BY COUNT(*) DESC",
					r => new { mode = r.GetString(0), quick = r.GetInt32(1), join = r.GetInt32(2), found = r.GetInt32(3), cancelled = r.GetInt32(4), failed = r.GetInt32(5), avgWait = r.IsDBNull(6) ? 0 : Math.Round(r.GetDouble(6), 1) }, ("$f", from));
				var games = Query(c, "SELECT name, COUNT(*), AVG(value), SUM(value) FROM events WHERE kind = 'game' AND at >= $f GROUP BY name ORDER BY COUNT(*) DESC",
					r => new { mode = r.GetString(0), games = r.GetInt32(1), avgSeconds = Math.Round(r.GetDouble(2)), totalSeconds = Math.Round(r.GetDouble(3)) }, ("$f", from));
				// parcours : sessions qui ont atteint chaque étape
				int Reached(string where) => Query(c, $"SELECT COUNT(DISTINCT e.session) FROM events e JOIN sessions s ON s.session = e.session WHERE s.first >= $f AND ({where})", r => r.GetInt32(0), ("$f", from))[0];
				var funnel = new[]
				{
					new { step = "Lancement du jeu", sessions = totals.sessions },
					new { step = "Connecté", sessions = Reached("e.kind = 'login'") },
					new { step = "Choix du mode", sessions = Reached("e.kind = 'screen' AND e.name = 'ModeScreen'") },
					new { step = "Recherche de partie", sessions = Reached("e.kind = 'mm_start'") },
					new { step = "Partie jouée", sessions = Reached("e.kind = 'game'") },
				};
				var versions = Query(c, "SELECT COALESCE(version, '?'), COUNT(*) FROM sessions WHERE first >= $f GROUP BY version ORDER BY COUNT(*) DESC", r => new { name = r.GetString(0), sessions = r.GetInt32(1) }, ("$f", from));
				var systems = Query(c, "SELECT COALESCE(os, '?'), COUNT(*) FROM sessions WHERE first >= $f GROUP BY os ORDER BY COUNT(*) DESC", r => new { name = r.GetString(0), sessions = r.GetInt32(1) }, ("$f", from));
				var langs = Query(c, "SELECT COALESCE(lang, '?'), COUNT(*) FROM sessions WHERE first >= $f GROUP BY lang ORDER BY COUNT(*) DESC", r => new { name = r.GetString(0), sessions = r.GetInt32(1) }, ("$f", from));
				return (object)new
				{
					days, bucket,
					now = new { menu, lobby = s.Lobby.Values.Sum(), playing = s.Playing.Values.Sum(), total = menu + s.Lobby.Values.Sum() + s.Playing.Values.Sum(), playingByMode = s.Playing, lobbyByMode = s.Lobby },
					totals = new { totals.players, totals.sessions, totals.avgSession, peak },
					series, daily, screens, menu = menuClicks, character, matchmaking, games, funnel, versions, systems, langs,
				};
			});
		}
	}
}
