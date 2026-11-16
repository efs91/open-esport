using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Chute en équipe : un chef de saut dirige tout le monde, TAB passe la main, un bot n'est jamais chef d'une équipe qui a un humain.</summary>
	[Collection("Network")]
	public class TeamDropTests
	{
		private static Rig Make(int humans, int teamSize, bool bots = false)
			=> new Rig(o =>
			{
				o.Royale = true; o.MinPlayers = humans; o.LobbyCountdown = 0.3f; o.Match.TeamSize = teamSize; o.FillBots = bots;
				o.EndLinger = 100f;
			});

		private static Rig.Bot[] Join(Rig rig, int n)
		{
			var list = new Rig.Bot[n];
			for (int i = 0; i < n; i++) list[i] = rig.Connect("J" + i);
			foreach (var b in list) Assert.True(rig.WaitPlaying(b));
			return list;
		}

		private static Rig.Bot Leader(Rig rig, Rig.Bot[] team) => team.First(b => b.C.Match.JumpLeader == b.C.PlayerId);

		[Fact]
		public void The_Leader_Jumps_And_The_Whole_Team_Follows_Spread_Out_And_Steered_By_The_Leader()
		{
			using var rig = Make(2, 2);
			var team = Join(rig, 2);
			rig.Run(8.2);
			Assert.All(team, b => Assert.Equal(Stance.Plane, rig.Server.GetPlayer(b.C.PlayerId).State.Stance));
			var lead = Leader(rig, team); var other = team.First(b => b != lead);
			Assert.Equal(lead.C.PlayerId, other.C.Match.JumpLeader);          // tout le monde sait qui est le chef
			// le coéquipier appuie sur saut : rien ne se passe
			other.Buttons = Buttons.Jump; rig.Run(0.4); other.Buttons = Buttons.None;
			Assert.Equal(Stance.Plane, rig.Server.GetPlayer(other.C.PlayerId).State.Stance);
			// le chef saute : toute l'équipe saute
			lead.Buttons = Buttons.Jump; rig.Run(0.4); lead.Buttons = Buttons.None;
			var sl = rig.Server.GetPlayer(lead.C.PlayerId).State; var so = rig.Server.GetPlayer(other.C.PlayerId).State;
			Assert.Equal(Stance.Diving, sl.Stance); Assert.Equal(Stance.Diving, so.Stance);
			Assert.InRange(Vec3.Distance(sl.Pos, so.Pos), 2f, 8f);            // décalés, pas au même point
			Assert.NotEqual(0xFFFF, (int)other.C.Match.ControlledBy);          // le coéquipier est passager
			Assert.Equal(0xFFFF, (int)lead.C.Match.ControlledBy);
			// seul le chef dirige : il tourne, le passager tourne avec lui, quoi que fasse ce dernier
			lead.Yaw = MathF.PI / 2f; other.Yaw = -MathF.PI / 2f; other.Forward = 1f; lead.Forward = 0f;
			rig.Run(2.0);
			var vl = rig.Server.GetPlayer(lead.C.PlayerId).State.Vel; var vo = rig.Server.GetPlayer(other.C.PlayerId).State.Vel;
			Assert.True(vl.X * vo.X > 0f, $"même direction : {vl} / {vo}");
			Assert.InRange(Vec3.Distance(rig.Server.GetPlayer(lead.C.PlayerId).State.Pos, rig.Server.GetPlayer(other.C.PlayerId).State.Pos), 2f, 12f);
		}

		[Fact]
		public void Tab_Passes_The_Lead_To_The_Next_Human()
		{
			using var rig = Make(2, 2);
			var team = Join(rig, 2);
			rig.Run(8.2);
			var lead = Leader(rig, team); var other = team.First(b => b != lead);
			lead.Buttons = Buttons.PassLead; rig.Run(0.5); lead.Buttons = Buttons.None; rig.Run(0.5);
			Assert.Equal(other.C.PlayerId, lead.C.Match.JumpLeader);
			Assert.Equal(other.C.PlayerId, other.C.Match.JumpLeader);
			// l'ancien chef n'a plus la main : son saut ne fait rien ; celui du nouveau chef fait sauter l'équipe
			lead.Buttons = Buttons.Jump; rig.Run(0.4); lead.Buttons = Buttons.None;
			Assert.Equal(Stance.Plane, rig.Server.GetPlayer(lead.C.PlayerId).State.Stance);
			other.Buttons = Buttons.Jump; rig.Run(0.4); other.Buttons = Buttons.None;
			Assert.Equal(Stance.Diving, rig.Server.GetPlayer(lead.C.PlayerId).State.Stance);
		}

		[Fact]
		public void A_Bot_Is_Never_The_Leader_Of_A_Team_With_A_Human()
		{
			using var rig = Make(1, 3, bots: true);
			var a = Join(rig, 1)[0];
			rig.Run(1.5);
			rig.OnServer(() =>
			{
				byte team = rig.Server.GetPlayer(a.C.PlayerId).Team;
				var mates = Enumerable.Range(0, 64).Select(i => rig.Server.GetPlayer(i)).Where(q => q != null && q.Team == team).ToList();
				Assert.True(mates.Count >= 2, "des bots complètent l'équipe");
			});
			rig.Run(0.5);
			Assert.Equal(a.C.PlayerId, a.C.Match.JumpLeader);
		}
	}
}
