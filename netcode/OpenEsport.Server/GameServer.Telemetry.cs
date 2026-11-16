using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Télémétrie d'un serveur de partie : ce que le service de matchmaking (et son site web) demande par le message « TELEMETRY » :
	/// état de la partie, nombres de joueurs et de bots, scores (éliminations, dégâts), santé du serveur. Tient dans un paquet UDP :
	/// au plus <see cref="MaxPlayersInReport"/> joueurs, classés par éliminations puis dégâts.
	/// </summary>
	public sealed partial class GameServer
	{
		public const int MaxPlayersInReport = 14;
		private readonly DateTime _startedAt = DateTime.UtcNow;

		private int ZoneRadius() { _zone.CurrentCircle(out _, out _, out float r); return (int)r; }

		public string TelemetryJson()
		{
			var (mean, _, p99, max) = _metrics.Summary();
			var ranked = _players.OrderByDescending(p => p.Kills).ThenByDescending(p => p.DamageDealt).ThenBy(p => p.Id).ToList();
			// un joueur = [id, nom, équipe, drapeaux (1 bot, 2 vivant, 4 à terre), éliminations, dégâts, classement] : compact pour tenir dans un paquet UDP
			var players = ranked.Take(MaxPlayersInReport).Select(p => new object[]
			{
				(int)p.Id, p.Name, (int)p.Team, (p.IsBot ? 1 : 0) | (p.Alive ? 2 : 0) | (p.Downed ? 4 : 0), p.Kills, (int)p.DamageDealt, (int)p.Placement
			}).ToArray();
			var report = new
			{
				phase = (int)Phase, royale = Royale ? 1 : 0, mode = (int)_opt.Mode, teamSize = _m.TeamSize, max = _m.MaxPlayers,
				humans = _humans.Count, bots = BotCount, alive = PlayersAliveCount(), teams = TeamsAliveCount(),
				winnerTeam = (int)_winnerTeam, countdown = Math.Max(0, (int)Math.Ceiling(_countdown)), minPlayers = _opt.MinPlayers,
				uptime = (int)(DateTime.UtcNow - _startedAt).TotalSeconds, tick = (long)_tick, tickRate = _m.TickRate,
				tickMeanMs = Math.Round(mean, 2), tickP99Ms = Math.Round(p99, 2), tickMaxMs = Math.Round(max, 2), overruns = _metrics.Overruns,
				totalKills = TotalKills, totalShots = TotalShots,
				zone = _zone == null ? null : new { stage = _zone.Stage, phase = (int)_zone.Phase, seconds = (int)_zone.SecondsLeft, damage = _zone.Damage, radius = ZoneRadius() },
				players
			};
			return JsonSerializer.Serialize(report);
		}
	}
}
