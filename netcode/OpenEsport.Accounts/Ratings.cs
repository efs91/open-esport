using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Accounts
{
	/// <summary>
	/// Classement. Chaque joueur a un score par mode de jeu, qui démarre à 1000.
	/// <list type="bullet">
	/// <item>Modes à deux équipes (drapeau, sabotage, contrôle, match à mort par équipe) : Elo d'équipe. On compare la moyenne de chaque équipe ; victoire contre plus fort = gain plus grand.</item>
	/// <item>Battle royale et match à mort (chacun pour soi) : selon la place finale. Le premier gagne autant que le dernier perd, et chaque élimination ajoute un petit bonus.</item>
	/// </list>
	/// Le classement général est la moyenne des scores par mode, pondérée par le nombre de parties jouées dans chacun : on ne peut pas monter en ne jouant qu'à un mode facile sans que ce soit visible.
	/// </summary>
	public static class Ratings
	{
		public const int Start = 1000;
		public const int ProvisionalMatches = 10;
		public const double KTeam = 24, KProvisional = 40, KPlacement = 20;
		public const int KillBonusCap = 6;

		/// <summary>Variation du score d'une équipe (Elo) : <paramref name="own"/> et <paramref name="other"/> sont les moyennes des deux équipes, <paramref name="won"/> le résultat (1 victoire, 0 défaite, 0,5 égalité).</summary>
		public static int TeamDelta(double own, double other, double won, int matchesPlayed)
		{
			double expected = 1.0 / (1.0 + Math.Pow(10.0, (other - own) / 400.0));
			double k = matchesPlayed < ProvisionalMatches ? KProvisional : KTeam;
			return (int)Math.Round(k * (won - expected));
		}

		/// <summary>Variation selon la place : +20 pour le premier, −20 pour le dernier, 0 au milieu, plus un bonus par élimination (6 au plus).</summary>
		public static int PlacementDelta(int placement, int players, int kills)
		{
			int n = Math.Max(2, players);
			placement = Math.Clamp(placement, 1, n);
			double percentile = (n - placement) / (double)(n - 1);       // 1 pour le premier, 0 pour le dernier
			int bonus = Math.Min(Math.Max(0, kills), KillBonusCap);
			return (int)Math.Round(KPlacement * (percentile - 0.5) * 2.0) + bonus;
		}

		/// <summary>Classement général : moyenne des scores par mode pondérée par le nombre de parties de chacun (1000 s'il n'a rien joué).</summary>
		public static int General(IEnumerable<(int rating, int matches)> modes)
		{
			double sum = 0; int total = 0;
			foreach (var (rating, matches) in modes) { sum += (double)rating * matches; total += matches; }
			return total == 0 ? Start : (int)Math.Round(sum / total);
		}
	}
}
