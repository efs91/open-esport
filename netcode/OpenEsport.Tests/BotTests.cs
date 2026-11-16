using System;
using System.Linq;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	[Collection("Network")]
	public class BotTests
	{
		[Fact]
		public void A_Server_Fills_Itself_With_Bots_And_They_Give_Their_Seats_To_Humans()
		{
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 12; o.Match.TeamSize = 3; });
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 12, 6), $"joueurs : {rig.Server.PlayerCount}");
			Assert.Equal(12, rig.Server.BotCount);

			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 12 && human.C.Roster.Count(r => r.IsBot) == 11, 3));
			Assert.Equal(12, rig.Server.PlayerCount);           // la partie reste pleine
			Assert.Equal(1, rig.Server.HumanCount);

			// la place reprise par l'humain est dans une équipe complète : chaque équipe garde 3 joueurs
			var sizes = human.C.Roster.GroupBy(r => r.Team).Select(g => g.Count()).OrderBy(x => x).ToList();
			Assert.Equal(new[] { 3, 3, 3, 3 }, sizes);

			// l'humain parti, un bot revient
			human.C.Disconnect();
			Assert.True(rig.WaitUntil(() => rig.Server.BotCount == 12, 6));
		}

		[Fact]
		public void Several_Humans_Replace_Several_Bots_And_The_Server_Never_Exceeds_Its_Limit()
		{
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 10; o.Match.TeamSize = 2; });
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 10, 6));
			var humans = Enumerable.Range(0, 4).Select(i => rig.Connect("H" + i)).ToList();
			Assert.True(rig.WaitUntil(() => humans.All(h => h.C.State == NetState.Playing), 8));
			rig.Run(2.5);
			Assert.Equal(10, rig.Server.PlayerCount);
			Assert.Equal(4, rig.Server.HumanCount);
			Assert.Equal(6, rig.Server.BotCount);
		}

		[Fact]
		public void Bots_Loot_Fight_And_Kill_Each_Other_Like_Players()
		{
			var world = WorldData.CreateProcedural(500, 4, 500);
			using var rig = new Rig(o =>
			{
				o.FillBots = true; o.Match.MaxPlayers = 16; o.Match.TeamSize = 2; o.SpawnClusterRadius = 60; o.Match.RespawnSeconds = 1f; o.BotSkill = 0.8f;
			}, world);
			rig.WaitUntil(() => false, 25);

			Assert.Equal(16, rig.Server.PlayerCount);
			Assert.True(rig.Server.TotalShots > 50, $"les bots doivent s'équiper puis tirer : {rig.Server.TotalShots} tirs");
			Assert.True(rig.Server.TotalKills >= 1, $"les bots doivent s'éliminer : {rig.Server.TotalKills} éliminations");
		}

		[Fact]
		public void Bots_Of_A_Humans_Team_Stay_Close_To_Him_And_Catch_Up_When_He_Leaves()
		{
			var world = WorldData.CreateProcedural(500, 4, 200);
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 3; o.Match.TeamSize = 3; o.SpawnClusterRadius = 20; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 3, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 3, 3));
			var mates = human.C.Roster.Where(r => r.IsBot && r.Team == human.C.TeamId).Select(r => r.Id).ToList();
			Assert.Equal(2, mates.Count);

			// le joueur s'éloigne de 90 m : ses coéquipiers bots doivent le rejoindre
			rig.Teleport(human, new Vec3(90, 0, 0));
			rig.Run(26.0); // 90 m à 6,8 m/s : ~13 s de course
			var hp = rig.Server.GetPlayer(human.C.PlayerId).State.Pos;
			foreach (var id in mates)
			{
				var bp = rig.Server.GetPlayer(id).State.Pos;
				float d = MathF.Sqrt((bp.X - hp.X) * (bp.X - hp.X) + (bp.Z - hp.Z) * (bp.Z - hp.Z));
				Assert.True(d < 15f, $"le bot {id} est resté à {d:F0} m du joueur");
			}

			// ils ne s'empilent pas : les deux bots sont distincts l'un de l'autre et du joueur
			rig.Run(4.0);
			var p1 = rig.Server.GetPlayer(mates[0]).State.Pos; var p2 = rig.Server.GetPlayer(mates[1]).State.Pos;
			hp = rig.Server.GetPlayer(human.C.PlayerId).State.Pos;
			float Dist(Vec3 a, Vec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
			// (seulement une fois arrivés près du joueur : le relief ou un obstacle peut retenir un bot en chemin, et deux bots en chemin se suivent)
			if (Dist(p1, hp) < 6f && Dist(p2, hp) < 6f)
			Assert.True(Dist(p1, p2) > 1.0f, $"les deux bots sont superposés ({Dist(p1, p2):F2} m) : bots {mates[0]}/{mates[1]} en ({p1.X:F1},{p1.Z:F1}) et ({p2.X:F1},{p2.Z:F1}), joueur ({hp.X:F1},{hp.Z:F1})");
			Assert.True(Dist(p1, hp) > 0.9f && Dist(p2, hp) > 0.9f, "un bot est superposé au joueur");
		}

		[Fact]
		public void Easy_Bots_Hit_Their_Target_About_One_Shot_In_Twenty()
		{
			var world = WorldData.CreateProcedural(500, 4, 200);
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 2; o.Match.TeamSize = 1; o.SpawnClusterRadius = 20; o.BotSkill = 0.2f; o.StartingLoadout = true; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 2, 6));
			var human = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(human));
			rig.OnServer(() => rig.Server.GetPlayer(human.C.PlayerId).Health = 1e6f); // la cible ne meurt pas : on mesure la précision

			long before = rig.Server.TotalShots;
			rig.Run(40.0);
			long shots = rig.Server.TotalShots - before;
			int hits = human.Events.Count(e => e.Type == EventType.Damaged);
			Assert.True(shots >= 40, $"le bot doit tirer : {shots} tirs");
			Assert.True(hits / (double)shots <= 0.12, $"un bot facile touche trop : {hits} touches sur {shots} tirs");
		}

		[Fact]
		public void A_Human_Sees_Bots_Move_Like_Other_Players()
		{
			var world = WorldData.CreateProcedural(500, 4, 200);
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 8; o.Match.TeamSize = 1; o.SpawnClusterRadius = 40; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 8, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			rig.Run(3.0);
			var seen = new System.Collections.Generic.List<InterpolatedEntity>();
			human.C.SampleEntities(seen);
			var players = seen.Where(e => e.Kind == EntityKind.Player).ToList();
			Assert.True(players.Count >= 5, $"joueurs visibles : {players.Count}");
			Assert.Contains(players, e => new Vec3(e.Vel.X, 0, e.Vel.Z).Length() > 3f); // au moins un bot est en mouvement
		}

		[Fact]
		public void A_Human_Takes_The_Seat_Of_A_Bot_Even_When_The_Server_Has_Free_Slots()
		{
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 30; o.BotFillTarget = 9; o.Match.TeamSize = 3; });
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 9, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			rig.Run(1.2);
			Assert.Equal(9, rig.Server.PlayerCount);
			Assert.Equal(8, rig.Server.BotCount);
			// il est dans une équipe de 3, pas dans une équipe à lui tout seul
			Assert.True(human.C.TeamId < 3, $"équipe {human.C.TeamId}");
		}

		[Fact]
		public void The_Number_Of_Bots_Can_Be_Capped()
		{
			using var rig = new Rig(o => { o.FillBots = true; o.MaxBots = 3; o.Match.MaxPlayers = 20; o.Match.TeamSize = 2; });
			Assert.True(rig.WaitUntil(() => rig.Server.BotCount == 3, 6), $"bots : {rig.Server.BotCount}");
			rig.Run(1.5);
			Assert.Equal(3, rig.Server.BotCount);
			Assert.Equal(3, rig.Server.PlayerCount);
		}

		[Fact]
		public void No_Bots_When_Max_Is_Zero()
		{
			using var rig = new Rig(o => { o.FillBots = true; o.MaxBots = 0; o.Match.MaxPlayers = 10; });
			rig.Run(1.5);
			Assert.Equal(0, rig.Server.BotCount);
		}

		[Fact]
		public void A_Players_Appearance_Is_Relayed_To_Everyone_And_Bots_Have_One()
		{
			using var rig = new Rig(o => { o.FillBots = true; o.MaxBots = 2; o.Match.MaxPlayers = 10; });
			var look = new byte[] { 3, 255, 10, 20, 30, 40, 5, 6, 7, 8 };
			var a = rig.Connect("A", appearance: look);
			var b = rig.Connect("B");
			Assert.True(rig.WaitPlaying(a));
			Assert.True(rig.WaitPlaying(b));
			Assert.True(rig.WaitUntil(() => b.C.Roster.Count >= 4, 4));
			var seen = b.C.Roster.First(r => r.Name == "A");
			Assert.Equal(look, seen.Appearance);
			Assert.All(b.C.Roster.Where(r => r.IsBot), r => Assert.Equal(10, r.Appearance.Length));
		}
	}
}
