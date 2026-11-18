using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Accounts
{
	public sealed class TournamentRow
	{
		public long Id, StartAt, Owner;
		public string Contest, Title, Mode, State;
		public int TeamSize, Races, LobbySize, Race, ScoreLimit;
	}

	public sealed partial class AccountStore
	{
		private const string TournamentColumns = "id, contest, title, mode, team_size, races, lobby_size, start_at, owner, state, race, score_limit";

		private static TournamentRow ReadTournament(Microsoft.Data.Sqlite.SqliteDataReader r) => new TournamentRow
		{
			Id = r.GetInt64(0), Contest = r.GetString(1), Title = r.GetString(2), Mode = r.GetString(3), TeamSize = r.GetInt32(4), Races = r.GetInt32(5),
			LobbySize = r.GetInt32(6), StartAt = r.GetInt64(7), Owner = r.GetInt64(8), State = r.GetString(9), Race = r.GetInt32(10), ScoreLimit = r.GetInt32(11)
		};

		public long CreateTournament(string contest, string title, string mode, int teamSize, int races, int lobbySize, long startAt, long owner, int scoreLimit = 0) => Run(c =>
		{
			Exec(c, "INSERT INTO tournaments(contest, title, mode, team_size, races, lobby_size, start_at, owner, state, score_limit, created) VALUES(@c, @t, @m, @s, @r, @l, @a, @o, 'open', @sl, @n)",
				("@c", contest), ("@t", title), ("@m", mode), ("@s", teamSize), ("@r", races), ("@l", lobbySize), ("@a", startAt), ("@o", owner), ("@sl", scoreLimit), ("@n", Now()));
			return LastId(c);
		});

		public TournamentRow Tournament(long id) => Run(c => Query(c, "SELECT " + TournamentColumns + " FROM tournaments WHERE id = @i", ReadTournament, ("@i", id)).FirstOrDefault());

		/// <summary>Tournois non terminés (ou terminés depuis moins de deux jours), du plus proche au plus lointain.</summary>
		public List<TournamentRow> Tournaments() => Run(c => Query(c, "SELECT " + TournamentColumns + " FROM tournaments WHERE state <> 'done' OR start_at > @t ORDER BY start_at LIMIT 50", ReadTournament, ("@t", Now() - 2 * 86400)));

		public int OpenTournamentsOf(long owner) => Run(c => Count(c, "SELECT COUNT(*) FROM tournaments WHERE owner = @o AND state <> 'done'", ("@o", owner)));

		public void SetTournamentState(long id, string state, int race) => Run(c => { Exec(c, "UPDATE tournaments SET state = @s, race = @r WHERE id = @i", ("@s", state), ("@r", race), ("@i", id)); return 0; });

		public void DeleteTournament(long id) => Run(c =>
		{
			Exec(c, "DELETE FROM tournament_players WHERE tid = @i", ("@i", id));
			Exec(c, "DELETE FROM tournaments WHERE id = @i", ("@i", id));
			return 0;
		});

		public List<(long account, int reference)> TournamentPlayers(long id) => Run(c => Query(c, "SELECT account, ref FROM tournament_players WHERE tid = @i ORDER BY ref", r => (r.GetInt64(0), r.GetInt32(1)), ("@i", id)));

		public void AddTournamentPlayer(long id, long account, int reference) => Run(c => { Exec(c, "INSERT OR REPLACE INTO tournament_players(tid, account, ref) VALUES(@i, @a, @r)", ("@i", id), ("@a", account), ("@r", reference)); return 0; });

		public void RemoveTournamentPlayer(long id, long account) => Run(c => { Exec(c, "DELETE FROM tournament_players WHERE tid = @i AND account = @a", ("@i", id), ("@a", account)); return 0; });

		/// <summary>Tournois auxquels le compte est inscrit (non terminés).</summary>
		public List<long> TournamentsOf(long account) => Run(c => Query(c, "SELECT p.tid FROM tournament_players p JOIN tournaments t ON t.id = p.tid WHERE p.account = @a AND t.state <> 'done'", r => r.GetInt64(0), ("@a", account)));
	}
}
