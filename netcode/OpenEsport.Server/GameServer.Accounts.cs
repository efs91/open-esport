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
		/// <summary>Modes à deux équipes (match de poule ou de tableau, lobby de Capture du drapeau) : le camp (0 ou 1) imposé à chaque compte.</summary>
		private readonly Dictionary<long, int> _tournamentSide = new Dictionary<long, int>();
		/// <summary>Battle royale : l'équipe de chaque compte (inscrits d'une même équipe, ou équipe tirée au sort), traitée comme un groupe.</summary>
		private readonly Dictionary<long, long> _tournamentGroup = new Dictionary<long, long>();
		/// <summary>Numéros de groupe des équipes de tournoi : loin des identifiants de groupes du service de comptes.</summary>
		private const long TournamentGroupBase = 4_000_000_000_000L;
		private DateTime _tournamentDeadline = DateTime.MaxValue;
		private const double TournamentWaitSeconds = 120;

		private void LoadTournamentLobby()
		{
			_ = System.Threading.Tasks.Task.Run(async () =>
			{
				for (int attempt = 0; attempt < 5 && _tournamentAllowed == null; attempt++)
				{
					bool match = !string.IsNullOrEmpty(_opt.TournamentMatch);
					var game = match ? await _accounts.TournamentMatchAsync(_opt.Tournament, _opt.TournamentMatch) : await _accounts.TournamentLobbyAsync(_opt.Tournament, _opt.TournamentLobby);
					if (game != null)
					{
						_mainThreadActions.Enqueue(() => ApplyTournamentGame(game, match));
						return;
					}
					await System.Threading.Tasks.Task.Delay(2000);
				}
				_mainThreadActions.Enqueue(() => { Log("Tournoi introuvable : arrêt du serveur"); _stopRequested = true; });
			});
		}

		/// <summary>
		/// Règle la partie d'après le tournoi : comptes admis, équipes ou camps, score à atteindre, équipement du 1 VS 1. Si le mode de la phase
		/// n'est pas celui de ce serveur (demande erronée), le serveur s'arrête : le matchmaking en relancera un bon.
		/// </summary>
		private void ApplyTournamentGame(AccountsLink.TournamentGame game, bool match)
		{
			if (Modes.TryParse(game.Mode, out var mode) && mode != _opt.Mode)
			{
				Log($"Tournoi {_opt.Tournament} : la phase se joue en {game.Mode}, pas en {Modes.Get(_opt.Mode).Key} : arrêt du serveur");
				_tournamentAllowed = new HashSet<long>();
				_stopRequested = true;
				return;
			}
			if (game.ScoreLimit > 0) _opt.ScoreLimit = game.ScoreLimit;
			if (_opt.Mode == GameMode.Duel && DuelRules.TryParsePolicy(game.Loadout, out var policy, out var fixedLoadout)) { _opt.DuelPolicy = policy; _opt.DuelFixed = fixedLoadout; }
			foreach (var kv in game.Sides) _tournamentSide[kv.Key] = kv.Value;
			foreach (var kv in game.Groups) _tournamentGroup[kv.Key] = TournamentGroupBase + kv.Value;
			_tournamentAllowed = new HashSet<long>(game.Accounts);
			_tournamentDeadline = DateTime.UtcNow.AddSeconds(TournamentWaitSeconds);
			Log(match
				? $"Tournoi {_opt.Tournament}, match {_opt.TournamentMatch} ({game.Mode}) : {game.Sides.Count(s => s.Value == 0)} contre {game.Sides.Count(s => s.Value == 1)}, premier à {game.ScoreLimit}"
				: $"Tournoi {_opt.Tournament}, manche {_opt.TournamentRace + 1}, lobby {_opt.TournamentLobby} ({game.Mode}) : {_tournamentAllowed.Count} joueur(s) attendu(s)");
		}

		/// <summary>Équipe de tournoi d'un compte (traitée comme un groupe) : (identifiant de groupe, 0) ; sinon le groupe du service de comptes.</summary>
		private (long partyId, int partySize) TournamentParty(long accountId, long partyId, int partySize)
			=> _tournamentGroup.TryGetValue(accountId, out long g) ? (g, 0) : _opt.Tournament > 0 ? (0, 0) : (partyId, partySize);

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
		/// <summary>Début de la phase de jeu (tick) : le temps de jeu de chacun se compte à partir de là (ou de son arrivée).</summary>
		private uint _playStartTick;

		/// <summary>Temps passé dans la partie (s) : depuis le départ (ou son arrivée) jusqu'à son élimination en battle royale, sinon jusqu'à maintenant.</summary>
		private int SecondsPlayed(ServerPlayer p)
		{
			uint from = Math.Max(_playStartTick, p.JoinTick), to = p.OutTick != 0 ? p.OutTick : _tick;
			return to > from ? (int)((to - from) / (uint)_m.TickRate) : 0;
		}

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
			_departed.Add(new ReportedPlayer { AccountId = p.AccountId, Team = p.Team, Placement = placement, Kills = p.Kills, Deaths = deaths, Damage = (long)p.DamageDealt, Seconds = SecondsPlayed(p) });
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
			_accounts.ReportMatch($"{_serverId}-{++_matchCounter}", info.Key, _m.TeamSize, winner, Math.Max(_departedPlayerCount, 2), new List<ReportedPlayer>(_departed));
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
				int placement = _opt.Mode == GameMode.BattleRoyale ? (p.Placement > 0 ? p.Placement : teamCount) : byKills != null ? byKills.IndexOf(p) + 1
					: info.TwoTeams ? (winnerTeam == 255 || p.Team == winnerTeam ? 1 : 2) : 0;
				list.Add(new ReportedPlayer { AccountId = p.AccountId, Team = p.Team, Placement = placement, Kills = p.Kills, Deaths = p.Deaths, Damage = (long)p.DamageDealt, Seconds = SecondsPlayed(p) });
			}
			foreach (var d in _departed)
			{
				if (list.Exists(r => r.AccountId == d.AccountId)) continue;
				if (info.TwoTeams) d.Placement = winnerTeam == 255 || d.Team == winnerTeam ? 1 : 2;
				list.Add(d);
			}
			_departed.Clear();
			int playerCount = _opt.Mode == GameMode.BattleRoyale ? teamCount : _players.Count;
			_accounts.ReportMatch($"{_serverId}-{++_matchCounter}", info.Key, _m.TeamSize, winnerTeam == 255 ? -1 : winnerTeam, playerCount, list);
			if (_opt.Tournament > 0 && !string.IsNullOrEmpty(_opt.TournamentMatch)) _accounts.ReportDuel(_opt.Tournament, _opt.TournamentMatch, ScoreOf(0), ScoreOf(1));
			else if (_opt.Tournament > 0) _accounts.ReportRace(_opt.Tournament, _opt.TournamentRace, _opt.TournamentLobby, list);
		}
	}
}
