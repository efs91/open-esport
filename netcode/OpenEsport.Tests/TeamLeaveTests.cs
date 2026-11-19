using System;
using System.Linq;
using System.Threading.Tasks;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Un équipier (ou le chef) quitte pendant la partie : le serveur et l'autre joueur doivent continuer sans exception ni blocage.</summary>
	[Collection("Network")]
	public class TeamLeaveTests
	{
		private static Rig Make(bool bots)
			=> new Rig(o => { o.Royale = true; o.MinPlayers = 2; o.LobbyCountdown = 0.3f; o.Match.TeamSize = 3; o.Match.MaxPlayers = 12; o.FillBots = bots; o.EndLinger = 100f; });

		private static void Within(Action a, int seconds = 90) => Assert.True(Task.Run(a).Wait(TimeSpan.FromSeconds(seconds)), "blocage : la partie ne répond plus");

		/// <summary>0 = au salon, 1 = dans l'avion, 2 = en chute, 3 = au sol</summary>
		[Theory]
		[InlineData(0, false, true)]
		[InlineData(1, false, true)]
		[InlineData(2, false, true)]
		[InlineData(3, false, true)]
		[InlineData(1, true, true)]
		[InlineData(2, true, true)]
		[InlineData(3, true, true)]
		[InlineData(2, false, false)]
		[InlineData(3, false, false)]
		public void A_Player_Leaving_Never_Breaks_The_Match(int moment, bool leaderLeaves, bool bots)
		{
			Within(() =>
			{
				using var rig = Make(bots);
				var a = rig.Connect("J0"); var b = rig.Connect("J1");
				Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
				var team = new[] { a, b };
				if (moment >= 1) rig.Run(8.2);
				var lead = team.FirstOrDefault(x => x.C.Match.JumpLeader == x.C.PlayerId) ?? a;
				var other = team.First(x => x != lead);
				if (moment >= 2) { lead.Buttons = Buttons.Jump; rig.Run(0.5); lead.Buttons = Buttons.None; rig.Run(1.0); }
				if (moment >= 3) { lead.Pitch = 0f; rig.Run(40.0); }
				var leaver = leaderLeaves ? lead : other; var stayer = leaderLeaves ? other : lead;
				leaver.C.Disconnect();
				rig.Run(8.0);
				Assert.True(stayer.C.State == NetState.Playing, "le joueur restant est toujours en partie : " + stayer.C.State);
				stayer.Forward = 1f; rig.Run(3.0);
			});
		}
	}
}
