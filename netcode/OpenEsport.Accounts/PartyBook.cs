using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Accounts
{
	/// <summary>Partie lancée par le chef du groupe : les autres membres la rejoignent tout seuls. <see cref="Seq"/> change à chaque lancement.</summary>
	public sealed class PartyMatch
	{
		public int Seq;
		public string Host;
		public int Port;
		public string Mode;
		public int TeamSize;
		public DateTime At = DateTime.UtcNow;
	}

	public sealed class Party
	{
		public long Id, Leader;
		public readonly List<long> Members = new List<long>();
		/// <summary>Invitations en attente : compte invité → expiration.</summary>
		public readonly Dictionary<long, DateTime> Invites = new Dictionary<long, DateTime>();
		public PartyMatch Match;
	}

	/// <summary>Instantané d'un groupe pour un de ses membres (ou invitations reçues par un joueur sans groupe).</summary>
	public sealed class PartyView
	{
		public Party Party;
		public List<(long partyId, long fromAccount)> Invites = new List<(long, long)>();
	}

	/// <summary>
	/// Les groupes de joueurs (comme Apex, Valorant ou Warzone) : un chef, jusqu'à 5 membres, des invitations par nom de compte. Gardé en mémoire
	/// (un groupe n'a pas besoin de survivre à un redémarrage du service). Quand le chef lance une partie, il publie l'adresse du serveur ;
	/// les autres membres la voient en interrogeant leur groupe et la rejoignent ; le serveur de partie les range dans la même équipe.
	/// Toutes les méthodes retournent un message d'erreur en français, ou null si tout s'est bien passé.
	/// </summary>
	public sealed class PartyBook
	{
		public const int MaxSize = 5;
		public static readonly TimeSpan InviteLife = TimeSpan.FromMinutes(10), MatchLife = TimeSpan.FromMinutes(3);

		private readonly object _gate = new object();
		private readonly Dictionary<long, Party> _parties = new Dictionary<long, Party>();
		private readonly Dictionary<long, long> _of = new Dictionary<long, long>();      // compte → groupe
		private long _next = 1;
		private int _matchSeq;

		public int SizeOf(long account) { lock (_gate) return Own(account)?.Members.Count ?? 0; }

		public long PartyOf(long account) { lock (_gate) return _of.TryGetValue(account, out var p) ? p : 0; }

		public PartyView View(long account)
		{
			lock (_gate)
			{
				Prune();
				var v = new PartyView();
				if (_of.TryGetValue(account, out long id) && _parties.TryGetValue(id, out var party)) v.Party = Copy(party);
				foreach (var p in _parties.Values)
					if (p.Invites.ContainsKey(account)) v.Invites.Add((p.Id, p.Leader));
				return v;
			}
		}

		private static Party Copy(Party p)
		{
			var c = new Party { Id = p.Id, Leader = p.Leader, Match = p.Match };
			c.Members.AddRange(p.Members);
			foreach (var kv in p.Invites) c.Invites[kv.Key] = kv.Value;
			return c;
		}

		private void Prune()
		{
			var now = DateTime.UtcNow;
			foreach (var p in _parties.Values)
			{
				foreach (var k in p.Invites.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) p.Invites.Remove(k);
				if (p.Match != null && now - p.Match.At > MatchLife) p.Match = null;
			}
		}

		private Party Own(long account) => _of.TryGetValue(account, out long id) && _parties.TryGetValue(id, out var p) ? p : null;

		/// <summary>Le chef invite un joueur (le groupe est créé à la première invitation).</summary>
		public string Invite(long from, long target)
		{
			lock (_gate)
			{
				Prune();
				if (from == target) return "Tu ne peux pas t'inviter toi-même.";
				var p = Own(from);
				if (p == null)
				{
					p = new Party { Id = _next++, Leader = from };
					p.Members.Add(from);
					_parties[p.Id] = p; _of[from] = p.Id;
				}
				if (p.Leader != from) return "Seul le chef du groupe peut inviter.";
				if (p.Members.Contains(target)) return "Ce joueur est déjà dans ton groupe.";
				if (p.Members.Count + p.Invites.Count >= MaxSize) return "Le groupe est complet.";
				if (Own(target) is { } other && other.Members.Count > 1) return "Ce joueur est déjà dans un groupe.";
				p.Invites[target] = DateTime.UtcNow + InviteLife;
				return null;
			}
		}

		public string Accept(long account, long partyId)
		{
			lock (_gate)
			{
				Prune();
				if (!_parties.TryGetValue(partyId, out var p) || !p.Invites.ContainsKey(account)) return "Cette invitation n'est plus valable.";
				if (p.Members.Count >= MaxSize) return "Le groupe est complet.";
				LeaveInternal(account);
				p.Invites.Remove(account);
				p.Members.Add(account);
				_of[account] = p.Id;
				return null;
			}
		}

		public void Decline(long account, long partyId)
		{
			lock (_gate) { if (_parties.TryGetValue(partyId, out var p)) p.Invites.Remove(account); }
		}

		public void Leave(long account) { lock (_gate) LeaveInternal(account); }

		private void LeaveInternal(long account)
		{
			var p = Own(account);
			if (p == null) return;
			p.Members.Remove(account);
			_of.Remove(account);
			if (p.Members.Count == 0) { _parties.Remove(p.Id); return; }
			if (p.Leader == account) p.Leader = p.Members[0];
			// un groupe réduit à un seul joueur n'a plus de raison d'être (ses invitations restent valables tant que le chef est là)
			if (p.Members.Count == 1 && p.Invites.Count == 0) { _of.Remove(p.Members[0]); _parties.Remove(p.Id); }
		}

		public string Kick(long leader, long target)
		{
			lock (_gate)
			{
				var p = Own(leader);
				if (p == null || p.Leader != leader) return "Seul le chef du groupe peut exclure un joueur.";
				if (target == leader) return "Utilise « Quitter » pour partir.";
				if (!p.Members.Contains(target)) { p.Invites.Remove(target); return null; }
				LeaveInternal(target);
				return null;
			}
		}

		public string Promote(long leader, long target)
		{
			lock (_gate)
			{
				var p = Own(leader);
				if (p == null || p.Leader != leader) return "Seul le chef du groupe peut passer la main.";
				if (!p.Members.Contains(target)) return "Ce joueur n'est pas dans ton groupe.";
				p.Leader = target;
				return null;
			}
		}

		/// <summary>Le chef publie la partie lancée (adresse du serveur) ; null efface l'annonce.</summary>
		public string SetMatch(long leader, string host, int port, string mode, int teamSize)
		{
			lock (_gate)
			{
				var p = Own(leader);
				if (p == null) return "Tu n'es dans aucun groupe.";
				if (p.Leader != leader) return "Seul le chef du groupe lance la partie.";
				p.Match = host == null ? null : new PartyMatch { Seq = ++_matchSeq, Host = host, Port = port, Mode = mode, TeamSize = teamSize };
				return null;
			}
		}
	}
}
