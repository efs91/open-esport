using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Bots : ils laissent le butin à un joueur de leur équipe qui est à côté, et se mettent à couvert derrière les obstacles entre deux rafales.</summary>
	[Collection("Network")]
	public class CoverAndLootTests
	{
		[Fact]
		public void A_Bot_Leaves_The_Loot_The_Human_Teammate_Is_Looking_At()
		{
			var world = WorldData.CreateFlat(300, 100f);
			world.LootSpawns.Add(new LootSpawn { Pos = new Vec3(120f, 100f, 0f), ItemId = ItemIds.Ak, Mode = LootSpawn.ModeAlways });      // une arme, à 4 m
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 2; o.Match.TeamSize = 2; o.SpawnClusterRadius = 5; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 2, 6));
			var human = rig.Connect("Humain");
			human.Yaw = -MathF.PI / 2f;      // il regarde l'arme (vers +X), dès le départ
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 2, 3));
			var mate = human.C.Roster.First(r => r.IsBot && r.Team == human.C.TeamId);
			rig.Teleport(human, new Vec3(117f, 100f, 0f));
			rig.Run(0.4);                    // le serveur reçoit d'abord le regard du joueur
			rig.OnServer(() => rig.Server.GetPlayer(mate.Id).State.Pos = new Vec3(118f, 100f, 1f));
			rig.Run(8.0);
			var bot = rig.Server.GetPlayer(mate.Id);
			Assert.True(bot.Inventory.Weapons[0] == null && bot.Inventory.Weapons[1] == null, "le bot ne doit pas prendre l'arme que le joueur regarde");
		}

		[Fact]
		public void A_Bot_Takes_The_Loot_The_Human_Teammate_Is_Not_Looking_At()
		{
			var world = WorldData.CreateFlat(300, 100f);
			world.LootSpawns.Add(new LootSpawn { Pos = new Vec3(120f, 100f, 0f), ItemId = ItemIds.Ak, Mode = LootSpawn.ModeAlways });
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 2; o.Match.TeamSize = 2; o.SpawnClusterRadius = 5; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 2, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 2, 3));
			var mate = human.C.Roster.First(r => r.IsBot && r.Team == human.C.TeamId);
			rig.Teleport(human, new Vec3(117f, 100f, 0f));
			rig.Run(0.4);                    // le serveur reçoit d'abord le regard du joueur
			human.Yaw = MathF.PI / 2f;       // il regarde ailleurs (vers -X)
			rig.OnServer(() => rig.Server.GetPlayer(mate.Id).State.Pos = new Vec3(118f, 100f, 1f));
			rig.Run(8.0);
			var bot = rig.Server.GetPlayer(mate.Id);
			Assert.True(bot.Inventory.Weapons[0] != null || bot.Inventory.Weapons[1] != null, "le bot ramasse l'arme : le joueur ne la regarde pas");
		}

		[Fact]
		public void A_Bot_Shoots_Then_Hides_Crouched_Behind_An_Obstacle_Near_Him()
		{
			var world = WorldData.CreateFlat(300, 100f);
			world.Boxes.Add(new Aabb(new Vec3(4f, 100f, -1.5f), new Vec3(6f, 102.5f, 1.5f)));        // une caisse juste à l'est du bot ; l'adversaire est à l'ouest
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 2; o.Match.TeamSize = 1; o.SpawnClusterRadius = 5; o.StartingLoadout = true; o.BotSkill = 0.5f; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 2, 6));
			var human = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 2, 3));
			var enemy = human.C.Roster.First(r => r.IsBot);
			rig.OnServer(() => { var h = rig.Server.GetPlayer(human.C.PlayerId); h.Health = 1e6f; });
			rig.Teleport(human, new Vec3(-25f, 100f, 0f));
			rig.OnServer(() => rig.Server.GetPlayer(enemy.Id).State.Pos = new Vec3(0f, 100f, 0f));

			bool hid = rig.WaitUntil(() =>
			{
				var b = rig.Server.GetPlayer(enemy.Id);
				return b != null && b.State.Stance == Stance.Crouching && b.State.Pos.X > 5.5f;      // accroupi, de l'autre côté de la caisse
			}, 30.0);
			Assert.True(hid, $"le bot doit se cacher derrière la caisse : {rig.Server.DebugBots()}");
			Assert.True(rig.Server.TotalShots > 5, $"et il doit aussi tirer : {rig.Server.TotalShots} tirs");
		}
	}
}
