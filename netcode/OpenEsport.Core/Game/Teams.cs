using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>Réglages d'une partie. Les limites sont celles du cahier des charges : 2 à 100 joueurs, équipes de 1 à 5.</summary>
	public sealed class MatchConfig
	{
		public const int MinPlayers = 2, MaxPlayersLimit = 100;
		public const int MinTeamSize = 1, MaxTeamSize = 10;

		public int MaxPlayers = 100;
		public int TeamSize = 3;
		public int TickRate = 60;
		/// <summary>Un snapshot tous les N ticks (2 = 30 Hz pour un tick de 60 Hz).</summary>
		public int SnapshotDivisor = 2;
		/// <summary>Retard d'affichage des autres joueurs, en ticks du serveur.</summary>
		public int InterpTicks = 5;
		public bool FriendlyFire = false;
		/// <summary>0 = éliminé définitivement. Sinon délai avant réapparition (utile pour les essais).</summary>
		public float RespawnSeconds = 3f;
		public float InterestRadius = 400f;

		public MatchConfig Validated()
		{
			MaxPlayers = Math.Clamp(MaxPlayers, MinPlayers, MaxPlayersLimit);
			TeamSize = Math.Clamp(TeamSize, MinTeamSize, MaxTeamSize);
			TickRate = Math.Clamp(TickRate, 20, 128);
			SnapshotDivisor = Math.Clamp(SnapshotDivisor, 1, 8);
			InterpTicks = Math.Clamp(InterpTicks, 1, 16);
			return this;
		}
	}

	/// <summary>
	/// Répartition des joueurs en équipes : chaque arrivant rejoint la première équipe qui a de la place,
	/// sinon une nouvelle équipe est ouverte. La dernière équipe peut donc rester incomplète.
	/// </summary>
	public sealed class TeamRoster
	{
		private readonly int _teamSize;
		private readonly List<int> _counts = new List<int>();

		public TeamRoster(int teamSize) { _teamSize = Math.Clamp(teamSize, MatchConfig.MinTeamSize, MatchConfig.MaxTeamSize); }

		public int TeamCount => _counts.Count;

		private readonly HashSet<int> _reserved = new HashSet<int>();

		/// <summary>Première équipe avec de la place, sinon une nouvelle. Les équipes réservées à un groupe ne reçoivent que des bots (<paramref name="allowReserved"/>) ou les membres du groupe.</summary>
		public int Join(bool allowReserved = false)
		{
			for (int t = 0; t < _counts.Count; t++)
				if (_counts[t] < _teamSize && (allowReserved || !_reserved.Contains(t))) { _counts[t]++; return t; }
			_counts.Add(1);
			return _counts.Count - 1;
		}

		/// <summary>Ouvre une équipe pour un groupe : elle est vide, et les autres joueurs seuls n'y entrent pas.</summary>
		public int JoinReserved()
		{
			for (int t = 0; t < _counts.Count; t++)
				if (_counts[t] == 0 && !_reserved.Contains(t)) { _counts[t] = 1; _reserved.Add(t); return t; }
			_counts.Add(1);
			_reserved.Add(_counts.Count - 1);
			return _counts.Count - 1;
		}

		/// <summary>Un membre du groupe rejoint l'équipe du groupe, s'il y reste de la place.</summary>
		public bool JoinTeam(int team)
		{
			if (team < 0 || team >= _counts.Count || _counts[team] >= _teamSize) return false;
			_counts[team]++;
			return true;
		}

		public void Leave(int team)
		{
			if (team >= 0 && team < _counts.Count && _counts[team] > 0 && --_counts[team] == 0) _reserved.Remove(team);
		}

		public int CountIn(int team) => team >= 0 && team < _counts.Count ? _counts[team] : 0;
	}
}
