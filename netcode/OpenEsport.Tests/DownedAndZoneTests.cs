using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	[Collection("Network")]
	public class DownedAndZoneTests
	{
		[Fact]
		public void A_Downed_Player_Lying_On_The_Ground_Can_Be_Shot()
		{
			var feet = new Vec3(0, 100, -20);
			// tir à hauteur d'épaule de debout, légèrement vers le bas, sur un corps allongé : touché
			var origin = new Vec3(0, 101.5f, 0);
			var dir = (new Vec3(0.3f, 100.2f, -20) - origin).Normalized();
			Assert.True(Hitboxes.RayVsPlayer(feet, Stance.Downed, origin, dir, 60f, out _, out _));
			// à côté : manqué
			dir = (new Vec3(3f, 100.2f, -20) - origin).Normalized();
			Assert.False(Hitboxes.RayVsPlayer(feet, Stance.Downed, origin, dir, 60f, out _, out _));
		}

		[Fact]
		public void The_Zone_State_Carries_The_Announce_And_Shrink_Durations()
		{
			var z = new ZoneRunner(ZonePresets.Apex(), -600, 600, -600, 600, 1, 60);
			z.Start();
			var s = z.ToState(0);
			Assert.Equal(90f, s.WarningSeconds); Assert.Equal(260f, s.ShrinkSeconds);
			var w = new BitWriter(256); s.Write(w);
			var r = new BitReader(w.Buffer, 0, w.ByteLength); r.ReadByte();
			var back = ZoneState.Read(r);
			Assert.Equal(90f, back.WarningSeconds); Assert.Equal(260f, back.ShrinkSeconds);
		}

		[Fact]
		public void When_Every_Living_Teammate_Is_Downed_The_Whole_Team_Dies()
		{
			using var rig = new Rig(o => { o.Royale = true; o.MinPlayers = 3; o.LobbyCountdown = 0.3f; o.Match.TeamSize = 2; o.FillBots = false; o.EndLinger = 100f; o.DownSeconds = 60f; });
			var bots = new[] { rig.Connect("A"), rig.Connect("B"), rig.Connect("C") };
			foreach (var b in bots) Assert.True(rig.WaitPlaying(b));
			rig.Run(1.5);
			var ps = bots.Select(b => rig.Server.GetPlayer(b.C.PlayerId)).ToList();
			var grp = ps.GroupBy(p => p.Team).First(g => g.Count() >= 2).ToList();
			var a = grp[0]; var c = grp[1];
			rig.OnServer(() => { a.Downed = true; a.State.Stance = Stance.Downed; c.Downed = true; c.State.Stance = Stance.Downed; });
			rig.Run(0.5);
			Assert.False(a.Alive); Assert.False(c.Alive);
		}
	}
}
