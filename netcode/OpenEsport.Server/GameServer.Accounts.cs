using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>Comptes : liaison avec le service de comptes (jeton à la connexion, rapport en fin de partie).</summary>
	public sealed partial class GameServer
	{
		private AccountsLink _accounts;
		private readonly string _serverId = Guid.NewGuid().ToString("N").Substring(0, 12);
		private int _matchCounter;

		private void SetupAccounts(ServerOptions options)
		{
			string url = !string.IsNullOrWhiteSpace(options.AccountsUrl) ? options.AccountsUrl : Environment.GetEnvironmentVariable("OE_ACCOUNTS_URL");
			string key = !string.IsNullOrWhiteSpace(options.AccountsKey) ? options.AccountsKey : Environment.GetEnvironmentVariable("OE_ACCOUNTS_KEY");
			if (string.IsNullOrWhiteSpace(url)) return;
			_accounts = new AccountsLink(url, key) { Log = s => Log(s) };
			Log($"Comptes obligatoires : service {url}");
			if (options.Tournament > 0) LoadTournamentLobby();
		}

		// ------------------------------------------------------------------ tournoi

		/// <summary>Comptes admis dans cette partie de tournoi (null tant que le service de comptes n'a pas répondu).</summary>
		private HashSet<long> _tournamentAllowed;
		/// <summary>Duel : le camp (0 ou 1) imposé à chaque compte.</summary>
		private readonly Dictionary<long, int> _tournamentSide = new Dictionary<long, int>();
		private DateTime _tournamentDeadline = DateTime.MaxValue;
		private const double TournamentWaitSeconds = 120;

		private void LoadTournamentLobby()
		{
			_ = System.Threading.Tasks.Task.Run(async () =>
			{
				for (int attempt = 0; attempt < 5 && _tournamentAllowed == null; attempt++)
				{
					if (!string.IsNullOrEmpty(_opt.TournamentMatch))
					{
						var duel = await _accounts.TournamentMatchAsync(_opt.Tournament, _opt.TournamentMatch);
						if (duel != null)
						{
							_mainThreadActions.Enqueue(() =>
							{
								_opt.ScoreLimit = duel.Value.scoreLimit;
								foreach (var a in duel.Value.a) _tournamentSide[a] = 0;
								foreach (var b in duel.Value.b) _tournamentSide[b] = 1;
								_tournamentAllowed = new HashSet<long>(_tournamentSide.Keys);
								_tournamentDeadline = DateTime.UtcNow.AddSeconds(TournamentWaitSeconds);
								Log($"Tournoi {_opt.Tournament}, match {_opt.TournamentMatch} : {duel.Value.a.Count} contre {duel.Value.b.Count}, premier à {duel.Value.scoreLimit}");
							});
							return;
						}
						await System.Threading.Tasks.Task.Delay(2000);
						continue;
					}
					var lobby = await _accounts.TournamentLobbyAsync(_opt.Tournament, _opt.TournamentLobby);
					if (lobby != null)
					{
						_mainThreadActions.Enqueue(() =>
						{
							_tournamentAllowed = lobby.Value.accounts;
							_tournamentDeadline = DateTime.UtcNow.AddSeconds(TournamentWaitSeconds);
							Log($"Tournoi {_opt.Tournament}, manche {_opt.TournamentRace + 1}, lobby {_opt.TournamentLobby} : {_tournamentAllowed.Count} joueur(s) attendu(s)");
						});
						return;
					}
					await System.Threading.Tasks.Task.Delay(2000);
				}
				_mainThreadActions.Enqueue(() => { Log("Tournoi introuvable : arrêt du serveur"); _stopRequested = true; });
			});
		}

		/// <summary>Raison du refus d'un compte dans une partie de tournoi, ou null s'il peut entrer.</summary>
		private string TournamentRefusal(long accountId)
		{
			if (_opt.Tournament <= 0) return null;
			if (_tournamentAllowed == null) return "Le tournoi se prépare : réessaie dans un instant.";
			return _tournamentAllowed.Contains(accountId) ? null : "Cette partie est réservée aux inscrits du tournoi.";
		}

		/// <summary>Vrai tant que des inscrits du lobby n'ont pas rejoint la partie (dans la limite de l'attente) : le départ est retardé.</summary>
		private bool TournamentPending() => _opt.Tournament > 0 && (_tournamentAllowed == null || (DateTime.UtcNow < _tournamentDeadline && _humans.Count(h => _tournamentAllowed.Contains(h.AccountId)) < _tournamentAllowed.Count));

		/// <summary>Joueurs avec compte partis avant la fin de la partie : leur résultat (place au moment du départ) est quand même compté.</summary>
		private readonly List<ReportedPlayer> _departed = new List<ReportedPlayer>();
		private int _departedPlayerCount;

		/// <summary>Résultat d'un joueur au moment où il quitte une partie en cours. Un joueur éliminé qui retourne au menu avant la fin
		/// (cas le plus courant d'une défaite) était oublié : seule la victoire comptait.</summary>
		private void RecordDeparture(ServerPlayer p)
		{
			if (_accounts == null || p.AccountId <= 0 || Phase != MatchPhase.Playing) return;
			if (_departed.Exists(d => d.AccountId == p.AccountId)) return;
			int teamCount = Math.Max(1, _players.Select(q => q.Team).Distinct().Count());
			int placement = 0;
			if (_opt.Mode == GameMode.BattleRoyale) placement = p.Placement > 0 ? p.Placement : teamCount;
			else if (_opt.Mode == GameMode.Deathmatch) placement = _players.OrderByDescending(q => q.Kills).ThenBy(q => q.Deaths).ToList().IndexOf(p) + 1;
			// quitter en vie compte comme une mort (en battle royale, il vient d'être éliminé et sa mort est déjà comptée)
			int deaths = p.Deaths + (p.Alive ? 1 : 0);
			_departed.Add(new ReportedPlayer { AccountId = p.AccountId, Team = p.Team, Placement = placement, Kills = p.Kills, Deaths = deaths, Damage = (long)p.DamageDealt });
			_departedPlayerCount = _opt.Mode == GameMode.BattleRoyale ? teamCount : _players.Count;
		}

		/// <summary>Le dernier humain est parti pendant la partie : sans lui, personne ne rapportera la fin. Son résultat est envoyé
		/// tout de suite : une défaite (en battle royale et match à mort, sa place ; à deux équipes, l'autre équipe gagne).</summary>
		private void ReportAbandoned()
		{
			if (_accounts == null || _departed.Count == 0 || Phase != MatchPhase.Playing) return;
			var info = Modes.Get(_opt.Mode);
			// deux équipes : les partants perdent (l'autre équipe gagne) ; s'ils étaient des deux camps, égalité
			int winner = -1;
			if (info.TwoTeams && _departed.Select(d => d.Team).Distinct().Count() == 1) winner = _departed[0].Team == 0 ? 1 : 0;
			_accounts.ReportMatch($"{_serverId}-{++_matchCounter}", info.Key, winner, Math.Max(_departedPlayerCount, 2), new List<ReportedPlayer>(_departed));
			_departed.Clear();
		}

		/// <summary>
		/// Fin de partie : envoie les résultats des joueurs ayant un compte, y compris ceux partis avant la fin (leur place au moment du départ).
		/// Modes à équipes : l'équipe gagnante. Battle royale : la place de l'équipe. Match à mort : la place selon les éliminations.
		/// </summary>
		private void ReportToAccounts(byte winnerTeam)
		{
			if (_accounts == null) return;
			var info = Modes.Get(_opt.Mode);
			var humans = _players.Where(p => p.AccountId > 0).ToList();
			if (humans.Count == 0 && _departed.Count == 0) return;
			int teamCount = Math.Max(1, _players.Select(p => p.Team).Distinct().Count());
			List<ServerPlayer> byKills = _opt.Mode == GameMode.Deathmatch ? _players.OrderByDescending(p => p.Kills).ThenBy(p => p.Deaths).ToList() : null;
			var list = new List<ReportedPlayer>();
			foreach (var p in humans)
			{
				int placement = _opt.Mode == GameMode.BattleRoyale ? (p.Placement > 0 ? p.Placement : teamCount) : byKills != null ? byKills.IndexOf(p) + 1 : 0;
				list.Add(new ReportedPlayer { AccountId = p.AccountId, Team = p.Team, Placement = placement, Kills = p.Kills, Deaths = p.Deaths, Damage = (long)p.DamageDealt });
			}
			foreach (var d in _departed) if (!list.Exists(r => r.AccountId == d.AccountId)) list.Add(d);
			_departed.Clear();
			int playerCount = _opt.Mode == GameMode.BattleRoyale ? teamCount : _players.Count;
			_accounts.ReportMatch($"{_serverId}-{++_matchCounter}", info.Key, winnerTeam == 255 ? -1 : winnerTeam, playerCount, list);
			if (_opt.Tournament > 0 && !string.IsNullOrEmpty(_opt.TournamentMatch)) _accounts.ReportDuel(_opt.Tournament, _opt.TournamentMatch, ScoreOf(0), ScoreOf(1));
			else if (_opt.Tournament > 0) _accounts.ReportRace(_opt.Tournament, _opt.TournamentRace, _opt.TournamentLobby, list);
		}
	}
}
