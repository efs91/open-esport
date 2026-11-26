using System;
using System.Collections.Generic;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>Un observateur connecté : il ne joue pas, il suit un joueur et reçoit ce qui se passe autour de lui.</summary>
	public sealed class Spectator
	{
		public NetPeer Peer;
		public long AccountId;
		/// <summary>Joueur suivi (0xFFFF : le serveur en choisit un).</summary>
		public ushort Follow = 0xFFFF;
		public ClientReplication Rep = new ClientReplication();
		public readonly BitWriter Out = new BitWriter(1400);
		public readonly BitWriter[] Extra = { new BitWriter(1400), new BitWriter(1400) };
		public int ExtraCount;
		public readonly List<int> Scratch = new List<int>(512);
	}

	/// <summary>
	/// Observateurs des parties de tournoi : l'organisateur et les joueurs qu'il a invités comme observateurs regardent une partie en cours.
	/// Le serveur leur envoie tous les joueurs de la carte (à une fréquence qui baisse loin du joueur suivi), le butin, les tirs et les explosions
	/// proches du joueur suivi, le fil des éliminations, la zone, les scores. Ils n'envoient que leurs acquittements et le joueur à suivre.
	/// Limites : pas d'inventaire ni de visée exacte du joueur suivi (le serveur ne réplique que ce que voient les autres joueurs).
	/// </summary>
	public sealed partial class GameServer
	{
		public const int MaxSpectators = 16;
		private readonly List<Spectator> _spectators = new List<Spectator>();
		public int SpectatorCount => _spectators.Count;

		private void HandleSpectate(NetPeer peer)
		{
			ushort version = _reader.ReadUInt16();
			string token = _reader.ReadString();
			if (_reader.Overflow) return;
			if (version != Protocol.Version) { Reject(peer, "Version du protocole différente"); return; }
			if (_opt.Tournament <= 0 || _accounts == null) { Reject(peer, "Seules les parties de tournoi se regardent."); return; }
			if (_spectators.Count >= MaxSpectators) { Reject(peer, "Trop d'observateurs sur cette partie."); return; }
			if (string.IsNullOrEmpty(token)) { Reject(peer, "Compte requis : connecte-toi."); return; }
			var pending = new object();
			peer.Tag = pending;
			_ = VerifySpectator(peer, pending, token);
		}

		private async System.Threading.Tasks.Task VerifySpectator(NetPeer peer, object pending, string token)
		{
			var (account, unreachable) = await _accounts.VerifyAsync(token);
			bool allowed = account != null && await _accounts.CanObserveAsync(_opt.Tournament, account.AccountId);
			_mainThreadActions.Enqueue(() =>
			{
				if (peer.Tag != pending) return;
				peer.Tag = null;
				if (peer.ConnectionState != ConnectionState.Connected) return;
				if (account == null) { Reject(peer, unreachable ? "Service de comptes injoignable : réessaie dans un instant." : "Session invalide : reconnecte-toi."); return; }
				if (!allowed) { Reject(peer, "Réservé aux observateurs du tournoi."); return; }
				if (_spectators.Count >= MaxSpectators) { Reject(peer, "Trop d'observateurs sur cette partie."); return; }
				CompleteSpectate(peer, account.AccountId);
			});
		}

		private void CompleteSpectate(NetPeer peer, long accountId)
		{
			var s = new Spectator { Peer = peer, AccountId = accountId };
			peer.Tag = s;
			_spectators.Add(s);
			var target = Followed(s);
			var accept = new JoinAccept
			{
				PlayerId = 0xFFFF, Team = target?.Team ?? 0, TickRate = (byte)_m.TickRate, SnapshotDivisor = (byte)_m.SnapshotDivisor,
				InterpTicks = (byte)_m.InterpTicks, TeamSize = (byte)_m.TeamSize, MaxPlayers = (ushort)_m.MaxPlayers,
				MinX = _quant.MinX, MinY = _quant.MinY, MinZ = _quant.MinZ, ServerTick = _tick, WorldChecksum = _worldChecksum,
				Spawn = target?.State ?? PlayerMovement.Spawn(PickSpawn(), _world),
			};
			_tmp.Reset();
			Messages.WriteBalance(_tmp);
			peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
			_tmp.Reset();
			accept.Write(_tmp);
			peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
			for (int i = 0; i < _world.Windows.Length; i++)
				if (!_world.Windows[i].IsNew) SendRaw(peer, WindowEvent(i), DeliveryMethod.ReliableOrdered);
			BroadcastRoster();
			BroadcastZone();
			SendStatusToAll();
			Log($"+ observateur (compte {accountId}) — {_spectators.Count} observateur(s)");
		}

		private void RemoveSpectator(Spectator s)
		{
			if (!_spectators.Remove(s)) return;
			if (s.Peer != null) s.Peer.Tag = null;
			Log($"- observateur (compte {s.AccountId}) — {_spectators.Count} observateur(s)");
		}

		/// <summary>L'observateur choisit le joueur à suivre (0xFFFF : le suivant).</summary>
		private void HandleFollow(Spectator s)
		{
			ushort id = _reader.ReadUInt16();
			if (_reader.Overflow) return;
			if (id != 0xFFFF && GetPlayer(id) != null) s.Follow = id;
			else s.Follow = NextPlayer(s.Follow);
			SendStatusTo(s);
		}

		private void HandleSpectatorAck(Spectator s)
		{
			ushort ackSeq = _reader.ReadUInt16();
			uint ackBits = _reader.ReadUInt32();
			if (_reader.Overflow) return;
			s.Rep.Ack(ackSeq, ackBits);
		}

		/// <summary>Le joueur après <paramref name="current"/> (les humains d'abord, en vie de préférence).</summary>
		private ushort NextPlayer(ushort current)
		{
			if (_players.Count == 0) return 0xFFFF;
			var order = new List<ServerPlayer>(_players);
			order.Sort((a, b) => a.Id.CompareTo(b.Id));
			int start = order.FindIndex(p => p.Id == current);
			for (int k = 1; k <= order.Count; k++)
			{
				var p = order[(start + k + order.Count) % order.Count];
				if (p.Alive || k == order.Count) return p.Id;
			}
			return order[0].Id;
		}

		/// <summary>Le joueur suivi par l'observateur ; s'il n'existe plus, un autre (un humain en vie de préférence).</summary>
		private ServerPlayer Followed(Spectator s)
		{
			var p = s.Follow != 0xFFFF ? GetPlayer(s.Follow) : null;
			if (p != null) return p;
			ServerPlayer best = null;
			foreach (var q in _players)
			{
				int Score(ServerPlayer x) => (x.IsBot ? 0 : 2) + (x.Alive ? 1 : 0);
				if (best == null || Score(q) > Score(best)) best = q;
			}
			s.Follow = best?.Id ?? 0xFFFF;
			return best;
		}

		private Vec3 FocusOf(Spectator s)
		{
			var p = Followed(s);
			return p != null ? p.State.Pos : new Vec3((_data.MinX + _data.MaxX) * 0.5f, 0f, (_data.MinZ + _data.MaxZ) * 0.5f);
		}

		private static void SendRaw(NetPeer peer, ServerEvent e, DeliveryMethod method)
		{
			var w = new BitWriter(64);
			e.Write(w);
			peer.Send(w.Buffer, 0, w.ByteLength, method);
		}

		/// <summary>Un événement destiné à tous (fil des éliminations, joueurs à terre, vitres) : aux observateurs aussi.</summary>
		private void SendToSpectators(ServerEvent e)
		{
			if (_spectators.Count == 0) return;
			_tmp.Reset();
			e.Write(_tmp);
			foreach (var s in _spectators) s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		/// <summary>Les observateurs dont le joueur suivi est à moins de <paramref name="range"/> mètres de ce point (tirs, explosions).</summary>
		private void SendNearSpectators(Vec3 pos, float range, DeliveryMethod method)
		{
			foreach (var s in _spectators)
			{
				var f = FocusOf(s);
				float dx = f.X - pos.X, dz = f.Z - pos.Z;
				if (dx * dx + dz * dz <= range * range) s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, method);
			}
		}

		/// <summary>État de la partie vu par le joueur suivi (vivants, ses éliminations, sa place) et scores du mode.</summary>
		private void SendStatusTo(Spectator s)
		{
			var p = Followed(s);
			if (p == null) return;
			_tmp.Reset();
			StatusFor(p).Write(_tmp);
			s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
			if (!Modal) return;
			_tmp.Reset();
			_mode.BuildState(p).Write(_tmp);
			s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered);
		}

		private void SendSpectatorStatuses() { foreach (var s in _spectators) SendStatusTo(s); }

		/// <summary>
		/// Snapshot d'un observateur : pas d'état propre ; tous les joueurs de la carte (le joueur suivi et son équipe à chaque envoi, les autres d'autant
		/// moins souvent qu'ils sont loin de lui), et le butin proche du joueur suivi.
		/// </summary>
		private void BuildSpectatorSnapshot(Spectator s)
		{
			var w = s.Out;
			w.Reset();
			var rep = s.Rep;
			rep.Begin();
			w.WriteByte((byte)MsgType.Snapshot);
			w.WriteUInt32(_tick);
			w.WriteUInt16(rep.Seq);
			w.WriteBool(false);

			var target = Followed(s);
			Vec3 pos = FocusOf(s);
			for (int i = 0; i < _players.Count; i++)
			{
				var o = _players[i];
				float dx = o.State.Pos.X - pos.X, dz = o.State.Pos.Z - pos.Z;
				OfferByDistance(rep, o.Id, dx * dx + dz * dz, target != null && o.Team == target.Team, _packed[o.Id]);
			}
			var scratch = s.Scratch;
			scratch.Clear();
			_lootGrid.Query(pos.X, pos.Z, LootRadius, scratch);
			for (int i = 0; i < scratch.Count; i++)
			{
				int id = scratch[i];
				int idx = id - LootIdBase;
				if (idx < 0 || idx >= _lootHigh || !_loot[idx].Active) continue;
				float dx = _loot[idx].Pos.X - pos.X, dz = _loot[idx].Pos.Z - pos.Z;
				float d2 = dx * dx + dz * dz;
				if (d2 > LootRadius * LootRadius) continue;
				rep.MarkRelevant(id);
				bool near = d2 < 25f * 25f;
				if (near || rep.Seq % 4 == 0) rep.Offer(PackLoot(idx), near ? 3 : 1);
			}

			int packetMax = Math.Min(Protocol.SnapshotBudgetBytes, s.Peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) - 8);
			rep.Write(w, packetMax - w.ByteLength);
			s.ExtraCount = 0;
			while (rep.HasMore && s.ExtraCount < Protocol.MaxPacketsPerSnapshot - 1)
			{
				var x = s.Extra[s.ExtraCount++];
				x.Reset();
				x.WriteByte((byte)MsgType.Snapshot);
				x.WriteUInt32(_tick);
				x.WriteUInt16((ushort)(rep.Seq + 1));
				x.WriteBool(false);
				rep.WriteContinuation(x, packetMax - x.ByteLength);
			}
		}

		private void SendSpectatorSnapshots()
		{
			if (_spectators.Count == 0) return;
			foreach (var s in _spectators) BuildSpectatorSnapshot(s);
			foreach (var s in _spectators)
			{
				SendSpectatorPacket(s, s.Out);
				for (int k = 0; k < s.ExtraCount; k++) SendSpectatorPacket(s, s.Extra[k]);
			}
		}

		private void SendSpectatorPacket(Spectator s, BitWriter w)
		{
			try { s.Peer.Send(w.Buffer, 0, w.ByteLength, DeliveryMethod.Sequenced); }
			catch (Exception e) { Log($"Envoi impossible à un observateur ({w.ByteLength} octets) : {e.Message}"); }
		}

		/// <summary>Grenades et feux proches du joueur suivi.</summary>
		private void SendSpectatorGrenades(List<GrenadeInfo> all, float range)
		{
			if (_spectators.Count == 0) return;
			var list = new List<GrenadeInfo>();
			foreach (var s in _spectators)
			{
				var f = FocusOf(s);
				list.Clear();
				foreach (var g in all)
				{
					float dx = f.X - g.Pos.X, dz = f.Z - g.Pos.Z;
					if (dx * dx + dz * dz <= range * range && list.Count < Messages.MaxGrenadesPerMessage) list.Add(g);
				}
				_tmp.Reset();
				Messages.WriteGrenades(_tmp, list);
				s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.Unreliable);
			}
		}

		/// <summary>Fin de partie : les observateurs repartent au lobby du tournoi.</summary>
		private void DismissSpectators()
		{
			foreach (var s in _spectators.ToArray())
			{
				try { _tmp.Reset(); Messages.WriteReject(_tmp, "Partie terminée"); s.Peer.Send(_tmp.Buffer, 0, _tmp.ByteLength, DeliveryMethod.ReliableOrdered); s.Peer.Disconnect(); } catch { }
				RemoveSpectator(s);
			}
		}
	}
}
