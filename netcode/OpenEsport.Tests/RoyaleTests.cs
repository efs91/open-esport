using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class RoyaleTests
	{
		private static Rig Make(int players, int teamSize, Action<OpenEsport.Server.ServerOptions> more = null)
			=> new Rig(o =>
			{
				o.Royale = true; o.MinPlayers = players; o.LobbyCountdown = 0.3f; o.Match.TeamSize = teamSize;
				o.EndLinger = 100f; o.DownSeconds = 3f; o.ReviveSeconds = 1f;
				more?.Invoke(o);
			});

		private static Rig.Bot[] Join(Rig rig, int n)
		{
			var list = new Rig.Bot[n];
			for (int i = 0; i < n; i++) list[i] = rig.Connect("J" + i);
			foreach (var b in list) Assert.True(rig.WaitPlaying(b));
			return list;
		}

		/// <summary>Les quatre joueurs rangés par équipe : [a, son coéquipier, c, son coéquipier] (l'ordre d'arrivée réseau n'est pas garanti).</summary>
		private static Rig.Bot[] ByTeam(Rig rig, Rig.Bot[] all)
		{
			var ordered = all.OrderBy(b => rig.Server.GetPlayer(b.C.PlayerId).Team).ToArray();
			Assert.Equal(rig.Server.GetPlayer(ordered[0].C.PlayerId).Team, rig.Server.GetPlayer(ordered[1].C.PlayerId).Team);
			Assert.NotEqual(rig.Server.GetPlayer(ordered[1].C.PlayerId).Team, rig.Server.GetPlayer(ordered[2].C.PlayerId).Team);
			return ordered;
		}

		private static void Shoot(Rig rig, Rig.Bot shooter, double seconds)
		{
			shooter.Buttons = Buttons.Fire; rig.Run(seconds); shooter.Buttons = Buttons.None; rig.Run(0.2);
		}

		[Fact]
		public void The_Lobby_Waits_For_Enough_Players_Then_Everybody_Starts_In_The_Same_Plane()
		{
			using var rig = Make(2, 1);
			var a = rig.Connect("A");
			Assert.True(rig.WaitPlaying(a));
			rig.Run(1.0);
			Assert.Equal(MatchPhase.Lobby, rig.Server.Phase);       // un seul joueur : on attend
			Assert.Equal(MatchPhase.Lobby, a.C.Match.Phase);
			Assert.Equal(1, a.C.Match.Lobby); Assert.Equal(2, a.C.Match.Needed);
			var b = rig.Connect("B");
			Assert.True(rig.WaitPlaying(b));
			rig.Run(1.0);
			Assert.Equal(MatchPhase.Playing, rig.Server.Phase);
			var sa = rig.Server.GetPlayer(a.C.PlayerId).State; var sb = rig.Server.GetPlayer(b.C.PlayerId).State;
			Assert.Equal(Stance.Plane, sa.Stance); Assert.Equal(Stance.Plane, sb.Stance);
			Assert.Equal(sa.PlaneFrom.X, sb.PlaneFrom.X);        // le même avion
			Assert.Equal(2, a.C.Match.PlayersAlive);
		}

		[Fact]
		public void A_Late_Player_Is_Refused_Once_The_Match_Has_Started()
		{
			using var rig = Make(1, 1);
			var a = rig.Connect("A");
			Assert.True(rig.WaitPlaying(a));
			rig.Run(1.0);
			Assert.Equal(MatchPhase.Playing, rig.Server.Phase);
			var late = rig.Connect("Retard");
			rig.Run(2.0);
			Assert.NotEqual(NetState.Playing, late.C.State);       // refusé (déconnecté avec le motif « Partie déjà commencée »)
		}

		[Fact]
		public void The_Last_Player_Alive_Wins_And_There_Is_No_Respawn()
		{
			using var rig = Make(2, 1);
			var p = Join(rig, 2); var a = p[0]; var b = p[1];
			rig.Run(1.0);
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -10));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 90);
			rig.Run(0.4);
			a.Pitch = MathF.Atan2(1.0f - 1.6f, 10f);
			Shoot(rig, a, 2.0);
			Assert.False(rig.Server.GetPlayer(b.C.PlayerId).Alive, "la cible est éliminée");
			Assert.Equal(MatchPhase.Over, rig.Server.Phase);
			Assert.Equal(rig.Server.GetPlayer(a.C.PlayerId).Team, rig.Server.WinnerTeam);
			rig.Run(1.0);                                          // ... et ne réapparaît pas
			Assert.False(rig.Server.GetPlayer(b.C.PlayerId).Alive);
			Assert.True(a.C.Match.Kills >= 1, $"éliminations : {a.C.Match.Kills}");
			Assert.True(a.C.Match.Damage >= 90, $"dégâts : {a.C.Match.Damage}");
			Assert.Equal(1, a.C.Match.Placement);
			Assert.Equal(2, b.C.Match.Placement);
			Assert.Equal(MatchPhase.Over, b.C.Match.Phase);
		}

		[Fact]
		public void In_A_Team_A_Fatal_Hit_Puts_You_Down_And_A_Teammate_Can_Revive_You()
		{
			using var rig = Make(4, 2, o => o.DownSeconds = 12f);
			var p = ByTeam(rig, Join(rig, 4)); var a = p[0]; var mate = p[1]; var c = p[2];
			rig.Run(1.0);
			rig.Teleport(c, new Vec3(0, 0, 0)); rig.Teleport(a, new Vec3(0, 0, -10)); rig.Teleport(mate, new Vec3(3, 0, -14)); rig.Teleport(p[3], new Vec3(40, 0, 40));
			rig.Give(c, ItemIds.Ak); rig.Give(c, ItemIds.AmmoRifle, 90);
			rig.Run(0.4);
			c.Pitch = MathF.Atan2(1.0f - 1.6f, 10f);
			Shoot(rig, c, 1.5);
			var sa = rig.Server.GetPlayer(a.C.PlayerId);
			Assert.True(sa.Alive && sa.Downed, "à terre, pas mort");
			Assert.Equal(Stance.Downed, sa.State.Stance);
			Assert.Equal(MatchPhase.Playing, rig.Server.Phase);
			rig.Run(0.5);
			Assert.Equal(Stance.Downed, a.C.Predicted.Stance);
			Assert.True(a.C.Match.DownLeft > 0);

			// le coéquipier vient et maintient E
			rig.Teleport(mate, new Vec3(1.0f, 0, -10));
			rig.Run(0.2);
			mate.C.InteractTarget = PlayerInput.NoTarget;
			Assert.True(rig.WaitUntil(() => mate.C.Match.CanRevive, 2), "il doit pouvoir réanimer");
			mate.Buttons = Buttons.Interact;
			rig.Run(1.5);
			mate.Buttons = Buttons.None;
			Assert.False(sa.Downed, "relevé");
			Assert.Equal(Stance.Standing, sa.State.Stance);
			Assert.InRange(sa.Health, 40f, 51f);
		}

		[Fact]
		public void A_Downed_Player_Bleeds_Out_And_A_Wiped_Team_Is_Eliminated()
		{
			using var rig = Make(4, 2);
			var p = ByTeam(rig, Join(rig, 4)); var a = p[0]; var mate = p[1]; var c = p[2];
			rig.Run(1.0);
			rig.Teleport(c, new Vec3(0, 0, 0)); rig.Teleport(a, new Vec3(0, 0, -10)); rig.Teleport(mate, new Vec3(60, 0, -60)); rig.Teleport(p[3], new Vec3(40, 0, 40));
			rig.Give(c, ItemIds.Ak); rig.Give(c, ItemIds.AmmoRifle, 120);
			rig.Run(0.4);
			c.Pitch = MathF.Atan2(1.0f - 1.6f, 10f);
			Shoot(rig, c, 1.5);
			var sa = rig.Server.GetPlayer(a.C.PlayerId);
			Assert.True(sa.Downed);
			rig.Run(3.5);                                          // DownSeconds = 3 : il se vide de son sang
			Assert.False(sa.Alive);
			Assert.True(rig.Server.GetPlayer(mate.C.PlayerId).Alive, "le coéquipier debout n'est pas touché");
			Assert.Equal(3, rig.Server.TeamsAliveCount() + 1);     // 2 équipes restent : la partie continue
			Assert.Equal(MatchPhase.Playing, rig.Server.Phase);
		}

		[Fact]
		public void Leaving_Eliminates_A_Player()
		{
			using var rig = Make(2, 1);
			var p = Join(rig, 2);
			rig.Run(1.0);
			Assert.Equal(MatchPhase.Playing, rig.Server.Phase);
			p[1].C.Disconnect();
			Assert.True(rig.WaitUntil(() => rig.Server.Phase == MatchPhase.Over, 3), "le dernier restant gagne");
		}

		[Fact]
		public void When_Only_Bots_Remain_The_Match_Ends_And_A_Launched_Server_Stops()
		{
			using var rig = Make(1, 1, o => { o.EmptyExitSeconds = 0.5f; o.ExitWhenOver = true; o.FillBots = true; o.Match.MaxPlayers = 6; });
			var a = Join(rig, 1)[0];
			rig.Run(1.0);
			Assert.Equal(MatchPhase.Playing, rig.Server.Phase);
			Assert.True(rig.Server.BotCount >= 1, "des bots complètent la partie");
			a.C.Disconnect();
			Assert.True(SpinWaitFor(() => rig.Server.StopRequested, 8), "le serveur doit s'arrêter quand il ne reste que des bots");
		}

		private static bool SpinWaitFor(Func<bool> cond, double seconds)
			=> System.Threading.SpinWait.SpinUntil(cond, TimeSpan.FromSeconds(seconds));

		[Fact]
		public void Players_Outside_The_Zone_Take_Damage_Until_They_Die_And_The_Client_Knows_The_Zone()
		{
			// zone minuscule et immobile au centre de la carte (rayon 2 % de 424 m ≈ 8,5 m), coups toutes les 0,2 s
			var cfg = new ZoneConfig
			{
				Name = "test", FirstRadius = 2f, TickSeconds = 0.2f, FinalDamage = 5f, IgnoreShield = true,
				Stages = { new ZoneStageConfig { Idle = 100000f, Warning = 10f, Shrink = 10f, Radius = 1f, Damage = 5f } }
			};
			using var rig = Make(2, 1, o => o.Zones = cfg);
			var p = Join(rig, 2);
			rig.Run(1.0);
			var inside = p[0]; var outside = p[1];
			rig.Teleport(inside, new Vec3(0, 0, 0)); rig.Teleport(outside, new Vec3(100, 0, 0));
			rig.Run(0.5);
			Assert.True(inside.C.ZoneKnown, "le client reçoit la zone");
			Assert.Equal(ZonePhase.Idle, inside.C.Zone.Phase);
			Assert.InRange(inside.C.Zone.CurRadius, 8f, 9f);
			float before = rig.Server.GetPlayer(outside.C.PlayerId).Health;
			rig.Run(1.0);
			var so = rig.Server.GetPlayer(outside.C.PlayerId);
			Assert.True(so.Health < before - 15f, $"vie {so.Health} (avant {before}) : des coups de zone tous les 0,2 s");
			Assert.Equal(100f, rig.Server.GetPlayer(inside.C.PlayerId).Health);                // dans la zone : aucun dégât
			rig.Run(5.0);                                                                       // 100 PV à 5 par coup : 20 coups = 4 s
			Assert.False(so.Alive, "mort dans la zone");
			Assert.Equal(0, rig.Server.GetPlayer(inside.C.PlayerId).Kills);                     // la zone ne donne de points à personne
			Assert.Equal(MatchPhase.Over, rig.Server.Phase);
			Assert.Equal(rig.Server.GetPlayer(inside.C.PlayerId).Team, rig.Server.WinnerTeam);
		}

		[Fact]
		public void Zone_Damage_Goes_Through_The_Shield_When_Configured_Like_Apex()
		{
			var cfg = new ZoneConfig
			{
				FirstRadius = 2f, TickSeconds = 0.2f, IgnoreShield = true,
				Stages = { new ZoneStageConfig { Idle = 100000f, Warning = 10f, Shrink = 10f, Radius = 1f, Damage = 5f } }
			};
			using var rig = Make(2, 1, o => o.Zones = cfg);
			var p = Join(rig, 2);
			rig.Run(1.0);
			rig.Teleport(p[0], new Vec3(0, 0, 0)); rig.Teleport(p[1], new Vec3(100, 0, 0));
			rig.Run(0.2);
			var v = rig.Server.GetPlayer(p[1].C.PlayerId);
			float shield = v.Shield;
			Assert.True(shield > 0f);
			rig.Run(1.0);
			Assert.Equal(shield, v.Shield);                    // le bouclier n'est pas touché
			Assert.True(v.Health < 100f);
		}
	}
}
