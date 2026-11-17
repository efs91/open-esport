using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	[Collection("Network")]
	public class PingTests
	{
		[Fact]
		public void A_Ping_Reaches_The_Whole_Team_Including_The_Sender_And_Nobody_Else()
		{
			using var rig = new Rig(o => { o.Match.TeamSize = 2; o.Match.MaxPlayers = 4; });
			var all = new[] { rig.Connect("A"), rig.Connect("B"), rig.Connect("C"), rig.Connect("D") };
			foreach (var x in all) Assert.True(rig.WaitPlaying(x));
			// deux joueurs d'une même équipe (a, b) et un joueur d'une autre (c)
			var group = all.GroupBy(x => x.C.TeamId).First(g => g.Count() >= 2).ToList();
			var a = group[0]; var b = group[1]; var c = all.First(x => x.C.TeamId != a.C.TeamId);
			rig.Run(0.3);

			a.C.SendPing(1, new Vec3(20f, rig.World.HeightAt(20, 20), 20f));
			Assert.True(rig.WaitUntil(() => b.Events.Any(e => e.Type == EventType.Ping), 2), "l'équipier reçoit le ping");
			var ping = b.Events.First(e => e.Type == EventType.Ping);
			Assert.Equal(a.C.PlayerId, ping.A);
			Assert.Equal(1, ping.C);
			Assert.InRange(ping.Pos.X, 19.9f, 20.1f);
			Assert.True(a.Events.Any(e => e.Type == EventType.Ping), "l'auteur le voit aussi");
			rig.Run(0.4);
			Assert.DoesNotContain(c.Events, e => e.Type == EventType.Ping);       // l'équipe adverse ne voit rien
		}

		[Fact]
		public void Pings_Are_Rate_Limited_And_Checked()
		{
			using var rig = new Rig(o => { o.Match.TeamSize = 2; o.Match.MaxPlayers = 4; });
			var a = rig.Connect("A"); var b = rig.Connect("B");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Run(0.3);
			for (int i = 0; i < 6; i++) a.C.SendPing(0, new Vec3(10f, 100f, 10f));        // rafale : une seule passe
			a.C.SendPing(0, new Vec3(99999f, 0f, 0f));                                      // hors carte : refusé
			rig.Run(0.6);
			Assert.InRange(b.Events.Count(e => e.Type == EventType.Ping), 1, 2);
		}
	}
}
