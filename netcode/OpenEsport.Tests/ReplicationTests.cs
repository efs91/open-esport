using System;
using System.Collections.Generic;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Échange de snapshots simulé en mémoire, avec perte de paquets, pour vérifier que le client converge toujours vers l'état du serveur.</summary>
	public class ReplicationTests
	{
		private sealed class Link
		{
			public readonly ClientReplication Rep = new ClientReplication();
			public readonly Dictionary<ushort, EntityState> ClientView = new Dictionary<ushort, EntityState>();
			public ushort LastSeq; public uint Bits; public bool Any;
			public int MaxBytes, Sent, Lost;
			public long TotalBytes;
			private readonly Random _rng;
			private readonly double _loss, _ackLoss;

			public Link(int seed, double loss, double ackLoss) { _rng = new Random(seed); _loss = loss; _ackLoss = ackLoss; }

			public void Send(Dictionary<ushort, EntityState> world, HashSet<ushort> relevant)
			{
				Rep.Begin();
				foreach (var id in relevant) { Rep.MarkRelevant(id); Rep.Offer(world[id], 4); }
				var w = new BitWriter();
				Rep.Write(w, Protocol.SnapshotBudgetBytes);
				MaxBytes = Math.Max(MaxBytes, w.ByteLength);
				TotalBytes += w.ByteLength;
				Sent++;
				ushort seq = Rep.Seq;

				if (_rng.NextDouble() < _loss) { Lost++; return; }
				var r = new BitReader(w.Buffer, 0, w.ByteLength);
				int removed = (int)r.ReadBits(8);
				var gone = new List<ushort>();
				for (int i = 0; i < removed; i++) gone.Add(r.ReadUInt16());
				int n = (int)r.ReadBits(16);
				var states = new List<EntityState>();
				for (int i = 0; i < n; i++) states.Add(EntityState.Read(r));
				Assert.False(r.Overflow);

				if (!Any) { Any = true; LastSeq = seq; Bits = 0; }
				else if (Seq.Newer(seq, LastSeq))
				{
					int d = (ushort)(seq - LastSeq);
					Bits = d >= 33 ? 0u : ((d >= 32 ? 0u : Bits << d) | (1u << (d - 1)));
					LastSeq = seq;
				}
				else return;
				foreach (var id in gone) ClientView.Remove(id);
				foreach (var s in states) ClientView[s.Id] = s;

				// l'acquittement revient avec les commandes du joueur (aussi sujet à perte)
				if (_rng.NextDouble() >= _ackLoss) Rep.Ack(LastSeq, Bits);
			}
		}

		private static EntityState Ent(int id, uint x, uint z) => new EntityState { Id = (ushort)id, Kind = EntityKind.Player, X = x, Z = z, Y = 1000, Health = 100 };

		[Theory]
		[InlineData(0.0, 0.0)]
		[InlineData(0.2, 0.1)]
		[InlineData(0.5, 0.3)]
		public void Client_Converges_To_Server_State_Despite_Packet_Loss(double loss, double ackLoss)
		{
			var rng = new Random(42);
			var world = new Dictionary<ushort, EntityState>();
			for (int i = 0; i < 60; i++) world[(ushort)i] = Ent(i, (uint)rng.Next(1000, 5000), (uint)rng.Next(1000, 5000));
			var relevant = new HashSet<ushort>(world.Keys);
			var link = new Link(1, loss, ackLoss);

			for (int step = 0; step < 400; step++)
			{
				// la moitié des entités bougent, certaines entrent ou sortent de la zone d'intérêt
				foreach (var id in new List<ushort>(world.Keys))
					if (id % 2 == 0) { var e = world[id]; e.X += (uint)rng.Next(0, 20); e.Z += (uint)rng.Next(0, 20); world[id] = e; }
				if (step % 7 == 0) { ushort id = (ushort)rng.Next(60); if (!relevant.Remove(id)) relevant.Add(id); }
				link.Send(world, relevant);
			}

			// plus aucun changement : après quelques échanges, la vue du client doit être exacte
			var calm = new Link(2, 0, 0); // témoin : même état, sans perte
			for (int i = 0; i < 40; i++) link.Send(world, relevant);

			Assert.Equal(relevant.Count, link.ClientView.Count);
			foreach (var id in relevant)
			{
				Assert.True(link.ClientView.ContainsKey(id), $"entité {id} manquante côté client");
				Assert.Equal(world[id], link.ClientView[id]);
			}
			Assert.True(link.MaxBytes <= Protocol.SnapshotBudgetBytes, $"snapshot trop gros : {link.MaxBytes}");
		}

		[Fact]
		public void Unchanged_Entities_Stop_Being_Sent_Once_Acknowledged()
		{
			var world = new Dictionary<ushort, EntityState>();
			for (int i = 0; i < 50; i++) world[(ushort)i] = Ent(i, (uint)(1000 + i * 10), 2000);
			var relevant = new HashSet<ushort>(world.Keys);
			var link = new Link(1, 0, 0);
			link.Send(world, relevant);
			int firstBytes = link.MaxBytes;
			Assert.True(firstBytes > 500);
			link.Send(world, relevant);
			var w = new BitWriter();
			link.Rep.Begin();
			foreach (var id in relevant) { link.Rep.MarkRelevant(id); link.Rep.Offer(world[id], 4); }
			link.Rep.Write(w, Protocol.SnapshotBudgetBytes);
			Assert.True(w.ByteLength <= 4, $"un monde immobile ne doit presque plus rien coûter ({w.ByteLength} octets)");
		}

		[Fact]
		public void Far_Entities_Are_Not_Starved_When_The_Budget_Is_Full()
		{
			var world = new Dictionary<ushort, EntityState>();
			for (int i = 0; i < 200; i++) world[(ushort)i] = Ent(i, 1000, 1000);
			var relevant = new HashSet<ushort>(world.Keys);
			var link = new Link(1, 0, 0);
			var rng = new Random(9);
			var updatedAt = new int[200];
			for (int step = 1; step <= 60; step++)
			{
				foreach (var id in new List<ushort>(world.Keys)) { var e = world[id]; e.X = (uint)(1000 + step * 3 + id); world[id] = e; }
				link.Send(world, relevant);
				foreach (var kv in link.ClientView) if (kv.Value.X == world[kv.Key].X) updatedAt[kv.Key] = step;
			}
			Assert.True(link.MaxBytes <= Protocol.SnapshotBudgetBytes);
			// 200 entités qui bougent toutes à chaque snapshot dépassent le budget : aucune ne doit rester très en retard
			for (int i = 0; i < 200; i++)
				Assert.True(60 - updatedAt[i] < 20, $"entité {i} en retard de {60 - updatedAt[i]} snapshots");
		}

		[Fact]
		public void Interpolation_Holds_Still_Entities_And_Does_Not_Glide_After_A_Long_Pause()
		{
			var q = new Quantizer(0, 0, 0);
			var e = new RemoteEntity { Id = 1, Kind = EntityKind.Player };
			var a = new EntityState { Id = 1, Kind = EntityKind.Player, X = q.QX(10), Z = q.QZ(10), Y = q.QY(5) };
			var b = new EntityState { Id = 1, Kind = EntityKind.Player, X = q.QX(20), Z = q.QZ(10), Y = q.QY(5) };
			e.Push(100, a, 2);
			e.Push(400, b, 2); // 300 ticks sans rien recevoir : l'entité était immobile
			e.Sample(300f, q, out var mid);
			Assert.Equal(10f, mid.Pos.X, 2); // elle n'a pas commencé à glisser vers sa nouvelle position
			e.Sample(399f, q, out var near);
			Assert.InRange(near.Pos.X, 10f, 20f);
			e.Sample(500f, q, out var after);
			Assert.Equal(20f, after.Pos.X, 2);
		}
	}
}
