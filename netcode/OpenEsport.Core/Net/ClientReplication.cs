using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>
	/// Suivi, côté serveur, de ce que sait un client donné. On compare chaque entité à la dernière version
	/// ACQUITTÉE par ce client (et non à la dernière envoyée) : un paquet perdu est donc renvoyé automatiquement.
	/// Seules les entités modifiées et les départs de zone sont écrits, par ordre de priorité et dans la limite d'un budget d'octets.
	/// </summary>
	public sealed class ClientReplication
	{
		public const int MaxEntityIds = 8192;
		private const int RingSize = 64;
		private const int MaxRemovalsPerSnapshot = 48;

		private struct Candidate { public EntityState State; public int Priority; }

		private sealed class SentRecord
		{
			public ushort Seq;
			public bool Valid;
			public bool Applied;
			public EntityState[] States = new EntityState[64];
			public int StateCount;
			public ushort[] Removed = new ushort[MaxRemovalsPerSnapshot];
			public int RemovedCount;
		}

		private readonly EntityState[] _acked = new EntityState[MaxEntityIds];
		private readonly bool[] _hasAcked = new bool[MaxEntityIds];
		private readonly int[] _lastSentSeq = new int[MaxEntityIds];
		private readonly int[] _relevantStamp = new int[MaxEntityIds];
		private readonly int[] _knownIndex = new int[MaxEntityIds];
		private readonly List<ushort> _known = new List<ushort>();
		private readonly SentRecord[] _ring = new SentRecord[RingSize];
		private readonly List<Candidate> _candidates = new List<Candidate>(256);
		private readonly List<Candidate> _selected = new List<Candidate>(64);
		private readonly List<Candidate> _rest = new List<Candidate>(128);
		private readonly ushort[] _removals = new ushort[MaxRemovalsPerSnapshot];

		private int _stamp = 1;
		private ushort _lastAcked;
		private bool _anyAck;

		/// <summary>Numéro du snapshot en préparation.</summary>
		public ushort Seq { get; private set; }

		public int KnownCount => _known.Count;

		/// <summary>Vrai si le dernier envoi n'a pas pu contenir toutes les entités modifiées (budget plein).</summary>
		public bool HasMore => _rest.Count > 0;

		public ClientReplication()
		{
			for (int i = 0; i < RingSize; i++) _ring[i] = new SentRecord();
			Seq = 0;
		}

		/// <summary>Démarre la préparation d'un snapshot.</summary>
		public void Begin()
		{
			Seq = (ushort)(Seq + 1);
			_stamp++;
			_candidates.Clear();
		}

		/// <summary>Déclare qu'une entité est pertinente pour ce client à cet instant (même si on ne la renvoie pas ce coup-ci).</summary>
		public void MarkRelevant(int id) { _relevantStamp[id] = _stamp; }

		/// <summary>Propose l'état courant d'une entité pertinente. Il n'est écrit que s'il diffère de la version acquittée par le client.</summary>
		public void Offer(in EntityState state, int tierWeight)
		{
			int id = state.Id;
			if (_hasAcked[id] && _acked[id].Equals(state))
				return;
			// plus une entité attend, plus sa priorité monte (évite la famine des entités éloignées)
			int age = Math.Min(Seq - _lastSentSeq[id], 64);
			_candidates.Add(new Candidate { State = state, Priority = tierWeight * (1 + age) });
		}

		/// <summary>
		/// Choisit ce qui tient dans le budget et l'écrit. Retourne le nombre d'entités écrites.
		/// L'appelant écrit l'en-tête avant, et appelle cette méthode juste après.
		/// </summary>
		public int Write(BitWriter w, int maxBytes)
		{
			// départs : entités connues du client mais plus pertinentes (renvoyés jusqu'à acquittement)
			int removed = 0;
			for (int i = 0; i < _known.Count && removed < MaxRemovalsPerSnapshot; i++)
			{
				ushort id = _known[i];
				if (_relevantStamp[id] != _stamp)
					_removals[removed++] = id;
			}

			return WriteSelected(w, maxBytes, removed);
		}

		/// <summary>
		/// Paquet supplémentaire pour le même instant : reprend les entités modifiées qui n'ont pas tenu dans le précédent.
		/// Les départs ne sont pas répétés. À appeler seulement si <see cref="HasMore"/>.
		/// </summary>
		public int WriteContinuation(BitWriter w, int maxBytes)
		{
			Seq = (ushort)(Seq + 1);
			_candidates.Clear();
			_candidates.AddRange(_rest);
			return WriteSelected(w, maxBytes, 0);
		}

		private int WriteSelected(BitWriter w, int maxBytes, int removed)
		{
			_candidates.Sort((a, b) => b.Priority.CompareTo(a.Priority));
			_selected.Clear();
			_rest.Clear();
			int budget = maxBytes - 1 - removed * 2 - 4;
			int used = 0;
			for (int i = 0; i < _candidates.Count; i++)
			{
				int cost = _candidates[i].State.Kind == EntityKind.Player ? EntityState.MaxBytesPlayer : EntityState.MaxBytesLoot;
				if (used + cost > budget) { _rest.Add(_candidates[i]); continue; }
				used += cost;
				_selected.Add(_candidates[i]);
			}

			var rec = _ring[Seq % RingSize];
			rec.Seq = Seq;
			rec.Valid = true;
			rec.Applied = false;
			rec.StateCount = 0;
			rec.RemovedCount = removed;
			if (rec.States.Length < _selected.Count) rec.States = new EntityState[_selected.Count * 2];
			if (removed > 0) Array.Copy(_removals, rec.Removed, removed);

			w.WriteBits((uint)removed, 8);
			for (int i = 0; i < removed; i++) w.WriteUInt16(_removals[i]);
			w.WriteBits((uint)_selected.Count, 16);
			for (int i = 0; i < _selected.Count; i++)
			{
				var st = _selected[i].State;
				st.Write(w);
				rec.States[rec.StateCount++] = st;
				_lastSentSeq[st.Id] = Seq;
			}
			return _selected.Count;
		}

		/// <summary>
		/// Le client indique le dernier snapshot reçu (<paramref name="seq"/>) et, sous forme de bits, lesquels des 32 précédents
		/// lui sont aussi parvenus (bit i = seq - 1 - i). Seuls les snapshots réellement reçus comptent : un paquet perdu n'est jamais
		/// considéré comme connu du client.
		/// </summary>
		public void Ack(ushort seq, uint previousBits)
		{
			if (_anyAck && !Seq_Newer(seq, _lastAcked) && seq != _lastAcked) return;
			if (Seq_Newer(seq, Seq)) return; // numéro du futur : invalide

			for (int i = 31; i >= 0; i--)
				if (((previousBits >> i) & 1u) != 0)
					ApplyOnce((ushort)(seq - 1 - i));
			ApplyOnce(seq);
			_lastAcked = seq;
			_anyAck = true;
		}

		private void ApplyOnce(ushort s)
		{
			var rec = _ring[s % RingSize];
			if (!rec.Valid || rec.Seq != s || rec.Applied) return;
			rec.Applied = true;
			for (int i = 0; i < rec.RemovedCount; i++) Forget(rec.Removed[i]);
			for (int i = 0; i < rec.StateCount; i++) Learn(rec.States[i]);
		}

		private void Learn(in EntityState st)
		{
			int id = st.Id;
			if (!_hasAcked[id])
			{
				_hasAcked[id] = true;
				_knownIndex[id] = _known.Count;
				_known.Add((ushort)id);
			}
			_acked[id] = st;
		}

		private void Forget(ushort id)
		{
			if (!_hasAcked[id]) return;
			_hasAcked[id] = false;
			int idx = _knownIndex[id];
			ushort last = _known[_known.Count - 1];
			_known[idx] = last;
			_knownIndex[last] = idx;
			_known.RemoveAt(_known.Count - 1);
		}

		private static bool Seq_Newer(ushort a, ushort b) => OpenEsport.Core.Seq.Newer(a, b);
	}
}
