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
		}

		/// <summary>
		/// Fin de partie : envoie les résultats des joueurs ayant un compte. Modes à équipes : l'équipe gagnante. Battle royale : la place de l'équipe.
		/// Match à mort : la place selon les éliminations. Les joueurs partis avant la fin n'y figurent pas.
		/// </summary>
		private void ReportToAccounts(byte winnerTeam)
		{
			if (_accounts == null) return;
			var info = Modes.Get(_opt.Mode);
			var humans = _players.Where(p => p.AccountId > 0).ToList();
			if (humans.Count == 0) return;
			int teamCount = Math.Max(1, _players.Select(p => p.Team).Distinct().Count());
			List<ServerPlayer> byKills = _opt.Mode == GameMode.Deathmatch ? _players.OrderByDescending(p => p.Kills).ThenBy(p => p.Deaths).ToList() : null;
			var list = new List<ReportedPlayer>();
			foreach (var p in humans)
			{
				int placement = _opt.Mode == GameMode.BattleRoyale ? (p.Placement > 0 ? p.Placement : teamCount) : byKills != null ? byKills.IndexOf(p) + 1 : 0;
				list.Add(new ReportedPlayer { AccountId = p.AccountId, Team = p.Team, Placement = placement, Kills = p.Kills, Deaths = p.Deaths, Damage = (long)p.DamageDealt });
			}
			int playerCount = _opt.Mode == GameMode.BattleRoyale ? teamCount : _players.Count;
			_accounts.ReportMatch($"{_serverId}-{++_matchCounter}", info.Key, winnerTeam == 255 ? -1 : winnerTeam, playerCount, list);
		}
	}
}
