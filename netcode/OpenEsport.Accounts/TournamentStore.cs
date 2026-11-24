using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Accounts
{
	public sealed class TournamentRow
	{
		public long Id, StartAt, Owner;
		public string Contest, Title, Mode, State;
		public int TeamSize, Races, LobbySize, Race, ScoreLimit;
		/// <summary>Configuration complète (voir <see cref="TournamentConfig"/>), en JSON.</summary>
		public string Config = "";
		/// <summary>Phase en cours (0 = la première) et numéro global de sa première manche (battle royale).</summary>
		public int Phase, RaceBase;
		/// <summary>Raison d'une annulation, ou autre remarque à afficher.</summary>
		public string Note = "";
	}

	/// <summary>
	/// Un inscrit : son compte, la référence de son équipe (négative avant le départ : l'équipe n'est pas encore dans ManageComp), sa présence confirmée,
	/// et s'il est le chef de son équipe (celui qui l'a inscrite et invite les autres).
	/// </summary>
	public readonly record struct TournamentEntry(long Account, int Reference, bool CheckedIn, bool Captain = false);

	/// <summary>Invitation dans une équipe de tournoi : l'invité, l'équipe (référence), le chef qui invite.</summary>
	public readonly record struct TournamentInvite(long Account, int Reference, long Inviter, long Created);

	public sealed partial class AccountStore
	{
		private const string TournamentColumns = "id, contest, title, mode, team_size, races, lobby_size, start_at, owner, state, race, score_limit, config, phase, race_base, note";

		private static TournamentRow ReadTournament(Microsoft.Data.Sqlite.SqliteDataReader r) => new TournamentRow
		{
			Id = r.GetInt64(0), Contest = r.GetString(1), Title = r.GetString(2), Mode = r.GetString(3), TeamSize = r.GetInt32(4), Races = r.GetInt32(5),
			LobbySize = r.GetInt32(6), StartAt = r.GetInt64(7), Owner = r.GetInt64(8), State = r.GetString(9), Race = r.GetInt32(10), ScoreLimit = r.GetInt32(11),
			Config = r.GetString(12), Phase = r.GetInt32(13), RaceBase = r.GetInt32(14), Note = r.GetString(15)
		};

		public long CreateTournament(string contest, TournamentConfig cfg, long owner) => Run(c =>
		{
			var first = cfg.Phases[0];
			Exec(c, "INSERT INTO tournaments(contest, title, mode, team_size, races, lobby_size, start_at, owner, state, score_limit, created, config) VALUES(@c, @t, @m, @s, @r, @l, @a, @o, 'open', @sl, @n, @cfg)",
				("@c", contest), ("@t", cfg.Title), ("@m", cfg.Format), ("@s", cfg.TeamSize), ("@r", cfg.TotalRaces), ("@l", first.LobbySize), ("@a", cfg.StartAt), ("@o", owner),
				("@sl", first.MaxScore), ("@n", Now()), ("@cfg", cfg.ToJson()));
			return LastId(c);
		});

		/// <summary>Nouvelle configuration d'un tournoi pas encore commencé.</summary>
		public void UpdateTournament(long id, TournamentConfig cfg) => Run(c =>
		{
			var first = cfg.Phases[0];
			Exec(c, "UPDATE tournaments SET title = @t, mode = @m, team_size = @s, races = @r, lobby_size = @l, start_at = @a, score_limit = @sl, config = @cfg WHERE id = @i",
				("@t", cfg.Title), ("@m", cfg.Format), ("@s", cfg.TeamSize), ("@r", cfg.TotalRaces), ("@l", first.LobbySize), ("@a", cfg.StartAt), ("@sl", first.MaxScore), ("@cfg", cfg.ToJson()), ("@i", id));
			return 0;
		});

		public TournamentRow Tournament(long id) => Run(c => Query(c, "SELECT " + TournamentColumns + " FROM tournaments WHERE id = @i", ReadTournament, ("@i", id)).FirstOrDefault());

		/// <summary>Tournois à venir ou en cours, et ceux terminés ou annulés depuis moins de deux jours, du plus proche au plus lointain.</summary>
		public List<TournamentRow> Tournaments() => Run(c => Query(c, "SELECT " + TournamentColumns + " FROM tournaments WHERE state NOT IN ('done', 'cancelled') OR start_at > @t ORDER BY start_at LIMIT 100", ReadTournament, ("@t", Now() - 2 * 86400)));

		/// <summary>Tournois pas encore commencés à départ fixe dont l'heure est passée (à lancer ou annuler).</summary>
		public List<TournamentRow> TournamentsDue(long now) => Run(c => Query(c, "SELECT " + TournamentColumns + " FROM tournaments WHERE state = 'open' AND start_at <= @t", ReadTournament, ("@t", now)));

		public int OpenTournamentsOf(long owner) => Run(c => Count(c, "SELECT COUNT(*) FROM tournaments WHERE owner = @o AND state NOT IN ('done', 'cancelled')", ("@o", owner)));

		public void SetTournamentState(long id, string state, int race) => Run(c => { Exec(c, "UPDATE tournaments SET state = @s, race = @r WHERE id = @i", ("@s", state), ("@r", race), ("@i", id)); return 0; });

		/// <summary>Passage à une phase : son numéro et le numéro global de sa première manche.</summary>
		public void SetTournamentPhase(long id, int phase, int raceBase) => Run(c => { Exec(c, "UPDATE tournaments SET phase = @p, race_base = @b, race = @b WHERE id = @i", ("@p", phase), ("@b", raceBase), ("@i", id)); return 0; });

		public void SetTournamentNote(long id, string note) => Run(c => { Exec(c, "UPDATE tournaments SET note = @n WHERE id = @i", ("@n", note ?? ""), ("@i", id)); return 0; });

		public void DeleteTournament(long id) => Run(c =>
		{
			Exec(c, "DELETE FROM tournament_players WHERE tid = @i", ("@i", id));
			Exec(c, "DELETE FROM tournament_invites WHERE tid = @i", ("@i", id));
			Exec(c, "DELETE FROM tournaments WHERE id = @i", ("@i", id));
			return 0;
		});

		public List<(long account, int reference)> TournamentPlayers(long id) => Run(c => Query(c, "SELECT account, ref FROM tournament_players WHERE tid = @i ORDER BY ref", r => (r.GetInt64(0), r.GetInt32(1)), ("@i", id)));

		public List<TournamentEntry> TournamentEntries(long id) => Run(c => Query(c, "SELECT account, ref, checked_in, captain FROM tournament_players WHERE tid = @i ORDER BY ref, captain DESC, rowid", r => new TournamentEntry(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2) != 0, r.GetInt32(3) != 0), ("@i", id)));

		public void AddTournamentPlayer(long id, long account, int reference, bool captain = false) => Run(c => { Exec(c, "INSERT OR REPLACE INTO tournament_players(tid, account, ref, captain) VALUES(@i, @a, @r, @k)", ("@i", id), ("@a", account), ("@r", reference), ("@k", captain ? 1 : 0)); return 0; });

		/// <summary>Prochaine référence locale (négative) d'une équipe inscrite avant le départ.</summary>
		public int NextLocalReference(long id) => Run(c => Count(c, "SELECT COALESCE(MIN(ref), 0) FROM tournament_players WHERE tid = @i", ("@i", id))) is int min && min < 0 ? min - 1 : -1;

		/// <summary>Au départ : l'équipe reçoit sa référence ManageComp (joueurs et invitations restantes).</summary>
		public void RenumberTournamentEntry(long id, int from, int to) => Run(c =>
		{
			Exec(c, "UPDATE tournament_players SET ref = @t WHERE tid = @i AND ref = @f", ("@t", to), ("@i", id), ("@f", from));
			Exec(c, "DELETE FROM tournament_invites WHERE tid = @i AND ref = @f", ("@i", id), ("@f", from));
			return 0;
		});

		public List<TournamentInvite> TournamentInvites(long id) => Run(c => Query(c, "SELECT account, ref, inviter, created FROM tournament_invites WHERE tid = @i ORDER BY created", r => new TournamentInvite(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)), ("@i", id)));

		public void AddTournamentInvite(long id, long account, int reference, long inviter) => Run(c => { Exec(c, "INSERT OR REPLACE INTO tournament_invites(tid, account, ref, inviter, created) VALUES(@i, @a, @r, @w, @n)", ("@i", id), ("@a", account), ("@r", reference), ("@w", inviter), ("@n", Now())); return 0; });

		/// <summary>Retire une invitation (une équipe précise), toutes celles d'un invité (<paramref name="reference"/> null), ou toutes celles d'une équipe (<paramref name="account"/> 0).</summary>
		public void RemoveTournamentInvites(long id, long account, int? reference) => Run(c =>
		{
			if (account == 0) Exec(c, "DELETE FROM tournament_invites WHERE tid = @i AND ref = @r", ("@i", id), ("@r", reference ?? 0));
			else if (reference == null) Exec(c, "DELETE FROM tournament_invites WHERE tid = @i AND account = @a", ("@i", id), ("@a", account));
			else Exec(c, "DELETE FROM tournament_invites WHERE tid = @i AND account = @a AND ref = @r", ("@i", id), ("@a", account), ("@r", reference.Value));
			return 0;
		});

		/// <summary>Invitations en attente d'un compte dans les tournois pas encore commencés : (tournoi, équipe, chef).</summary>
		public List<(long tournament, int reference, long inviter)> InvitesFor(long account) => Run(c => Query(c, "SELECT i.tid, i.ref, i.inviter FROM tournament_invites i JOIN tournaments t ON t.id = i.tid WHERE i.account = @a AND t.state = 'open' ORDER BY i.created", r => (r.GetInt64(0), r.GetInt32(1), r.GetInt64(2)), ("@a", account)));

		public void RemoveTournamentPlayer(long id, long account) => Run(c => { Exec(c, "DELETE FROM tournament_players WHERE tid = @i AND account = @a", ("@i", id), ("@a", account)); return 0; });

		/// <summary>Présence confirmée pour toute l'équipe (même référence).</summary>
		public void CheckInTournament(long id, int reference) => Run(c => { Exec(c, "UPDATE tournament_players SET checked_in = 1 WHERE tid = @i AND ref = @r", ("@i", id), ("@r", reference)); return 0; });

		/// <summary>Tournois auxquels le compte est inscrit (non terminés).</summary>
		public List<long> TournamentsOf(long account) => Run(c => Query(c, "SELECT p.tid FROM tournament_players p JOIN tournaments t ON t.id = p.tid WHERE p.account = @a AND t.state NOT IN ('done', 'cancelled')", r => r.GetInt64(0), ("@a", account)));

		/// <summary>Le compte a-t-il confirmé sa présence dans ce tournoi ?</summary>
		public bool CheckedIn(long id, long account) => Run(c => Count(c, "SELECT COUNT(*) FROM tournament_players WHERE tid = @i AND account = @a AND checked_in = 1", ("@i", id), ("@a", account)) > 0);
	}
}
