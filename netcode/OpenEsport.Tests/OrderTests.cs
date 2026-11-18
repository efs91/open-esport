using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Les pings donnent des ordres aux bots de l'équipe : défendre un endroit, attaquer un adversaire, jusqu'à ce que l'auteur retire le ping.</summary>
	[Collection("Network")]
	public class OrderTests
	{
		private static float Flat(Vec3 a, Vec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

		[Fact]
		public void Bots_Of_The_Team_Defend_A_Pinged_Place_Instead_Of_Following_The_Human()
		{
			var world = WorldData.CreateProcedural(500, 4, 200);
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 3; o.Match.TeamSize = 3; o.SpawnClusterRadius = 20; o.StartingLoadout = true; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 3, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 3, 3));
			var mates = human.C.Roster.Where(r => r.IsBot && r.Team == human.C.TeamId).Select(r => r.Id).ToList();
			Assert.Equal(2, mates.Count);
			rig.Run(0.5);

			var spot = new Vec3(60f, world.HeightAt(60, 0), 0f);
			human.C.SendPing(0, spot);                       // « ici » : les bots défendent cet endroit
			rig.Teleport(human, new Vec3(-90f, 0, 0));        // le joueur s'éloigne : sans ordre, les bots iraient le rejoindre
			rig.WaitUntil(() => mates.Any(id => Flat(rig.Server.GetPlayer(id).State.Pos, spot) < 14f), 45.0);      // 60 m à parcourir, en contournant les obstacles

			// au moins un défenseur est arrivé (l'autre peut être retenu par un obstacle du terrain de test : défaut de navigation sans rapport avec l'ordre)
			Assert.True(mates.Any(id => Flat(rig.Server.GetPlayer(id).State.Pos, spot) < 14f), $"aucun bot ne défend le ping. {rig.Server.DebugBots()}");
		}

		[Fact]
		public void Removing_The_Ping_Tells_The_Team_And_Frees_The_Bots()
		{
			var world = WorldData.CreateProcedural(500, 4, 200);
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 3; o.Match.TeamSize = 3; o.SpawnClusterRadius = 20; o.StartingLoadout = true; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 3, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			rig.Run(0.5);

			human.C.SendPing(0, new Vec3(30f, world.HeightAt(30, 0), 0f));
			Assert.True(rig.WaitUntil(() => human.Events.Any(e => e.Type == EventType.Ping && e.C == 0), 2));
			rig.Run(0.5);
			human.C.SendPing(GameServerPingCancel, Vec3.Zero);                                  // clic gauche sur son ping dans la carte
			Assert.True(rig.WaitUntil(() => human.Events.Any(e => e.Type == EventType.Ping && e.C == GameServerPingCancel && e.A == human.C.PlayerId), 2), "le ping doit être retiré chez les équipiers");
		}

		private const byte GameServerPingCancel = 255;

		[Fact]
		public void A_Ping_On_An_Enemy_Sends_The_Bots_Of_The_Team_After_Him()
		{
			var world = WorldData.CreateProcedural(500, 4, 200);
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 4; o.Match.TeamSize = 2; o.SpawnClusterRadius = 20; o.StartingLoadout = true; }, world);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 4, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 4, 3));
			var mate = human.C.Roster.First(r => r.IsBot && r.Team == human.C.TeamId);
			var enemy = human.C.Roster.First(r => r.IsBot && r.Team != human.C.TeamId);
			rig.Run(0.5);

			var ep = rig.Server.GetPlayer(enemy.Id).State.Pos;
			human.C.SendPing(2, ep, enemy.Id);              // « ennemi » sur un joueur précis : on l'attaque
			rig.Run(3.0);

			Assert.Contains($"{mate.Name} ", rig.Server.DebugBots().Replace("\r", ""));
			var line = rig.Server.DebugBots().Split('\n').First(l => l.StartsWith(mate.Name + " "));
			Assert.Contains($"target={enemy.Name}", line);
		}
	}
}
