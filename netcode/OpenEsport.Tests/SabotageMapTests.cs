using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Carte du Sabotage « Terminal des Cimes » (assets/maps/sabotage.world, produite par tools/map/gen_sabotage_map.py) : deux sites, des apparitions de chaque côté, et des routes praticables.</summary>
	[Collection("Network")]
	public class SabotageMapTests
	{
		private static WorldData Load([CallerFilePath] string here = "")
		{
			string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
			return WorldData.LoadFile(Path.Combine(root, "assets", "maps", "sabotage.world"));
		}

		[Fact]
		public void The_Map_Has_Two_Sites_And_Enough_Spawns_For_Five_Per_Team()
		{
			var d = Load();
			Assert.Equal(2, d.PointsOf(MapPointKind.BombSite).Count());
			var spawns = d.PointsOf(MapPointKind.SpawnTeam).ToList();
			Assert.True(spawns.Count(p => p.Team == 1) >= 5, "apparitions des attaquants");
			Assert.True(spawns.Count(p => p.Team == 0) >= 5, "apparitions des défenseurs");
			// les attaquants sont au sud (+Z), les défenseurs au nord (-Z)
			Assert.True(spawns.Where(p => p.Team == 1).All(p => p.Pos.Z > 40));
			Assert.True(spawns.Where(p => p.Team == 0).All(p => p.Pos.Z < -40));
		}

		[Fact]
		public void Every_Spawn_Can_Walk_To_Both_Sites_Within_A_Sensible_Distance()
		{
			var d = Load();
			var nav = new NavGrid(d);
			var sites = d.PointsOf(MapPointKind.BombSite).OrderBy(p => p.Index).ToList();
			var path = new List<Vec3>();
			foreach (var spawn in d.PointsOf(MapPointKind.SpawnTeam))
				foreach (var site in sites)
				{
					Assert.True(nav.FindPath(spawn.Pos, site.Pos, path, 60000), $"pas de chemin de {spawn.Pos} (camp {spawn.Team}) vers le site {site.Index}");
					float len = 0f;
					var prev = spawn.Pos;       // le chemin ne contient pas le point de départ
					foreach (var wp in path) { len += Vec3.Distance(prev, wp); prev = wp; }
					// les attaquants (loin) mettent une vingtaine de secondes, les défenseurs moins
					System.Console.WriteLine($"DBG spawn {spawn.Pos} team {spawn.Team} -> site {site.Index} {site.Pos}: {len:F0} m, {path.Count} points");
					if (spawn.Team == 1) Assert.InRange(len, 90f, 190f);
					else Assert.InRange(len, 85f, 140f);
				}
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void AttackersCanReachBothSitesUsingEitherTheOuterRoutesOrDispatch(bool closeDispatch)
		{
			var map = Load();
			// Exclude the defenders' rear loop so a path cannot enter a site through the other one.
			map.Boxes.Add(new Aabb(new Vec3(-96, 0, -90), new Vec3(96, 5, -35)));
			if (closeDispatch)
				map.Boxes.Add(new Aabb(new Vec3(-43, 0, -30), new Vec3(43, 5, 20)));
			else
			{
				map.Boxes.Add(new Aabb(new Vec3(-90, 0, 18), new Vec3(-44, 5, 23)));
				map.Boxes.Add(new Aabb(new Vec3(44, 0, 18), new Vec3(90, 5, 23)));
			}
			var nav = new NavGrid(map);
			var path = new List<Vec3>();
			foreach (var spawn in map.PointsOf(MapPointKind.SpawnTeam).Where(s => s.Team == 1))
				foreach (var site in map.PointsOf(MapPointKind.BombSite))
					Assert.True(nav.FindPath(spawn.Pos, site.Pos, path, 60000));
		}

		[Fact]
		public void Bots_Play_Whole_Rounds_On_The_Map_And_Plant_The_Bomb()
		{
			var text = "{\"sabotage.buySeconds\": 1, \"sabotage.roundSeconds\": 70, \"sabotage.plantSeconds\": 1, \"sabotage.bombSeconds\": 6, \"sabotage.endSeconds\": 1.5}";
			GameBalance.ApplyFileText(text, new List<string>());
			try
			{
				using var rig = new Rig(o =>
				{
					o.Mode = GameMode.Sabotage; o.Match.TeamSize = 4; o.MinPlayers = 1; o.LobbyCountdown = 0.3f; o.FillBots = true; o.EndLinger = 100f; o.BotSkill = 0.7f;
					o.Match.MaxPlayers = Modes.Get(GameMode.Sabotage).PlayersFor(4);
				}, Load());
				// le seul humain (immobile) lâche la bombe dès qu'il la reçoit : les bots attaquants la ramassent et la posent
				var human = rig.Connect("Humain");
				Assert.True(rig.WaitPlaying(human));
				bool planted = false;
				var sw = System.Diagnostics.Stopwatch.StartNew();
				while (sw.Elapsed.TotalSeconds < 120 && !planted)       // les bots se battent aussi : une manche peut finir avant la pose, on en laisse plusieurs
				{
					rig.Run(0.25);
					if (human.C.Inventory.CountOf(ItemIds.Bomb) > 0) human.C.SendCommand(CommandType.DropBomb, 0);
					var dbg = rig.Server.ModeDebug();
					if (dbg.Contains("bomb=2") || dbg.Contains("bomb=3") || dbg.Contains("bomb=4")) planted = true;
				}
				Assert.True(planted, "les bots attaquants doivent aller sur un site et poser la bombe");
			}
			finally
			{
				foreach (var k in new[] { "sabotage.buySeconds", "sabotage.roundSeconds", "sabotage.plantSeconds", "sabotage.bombSeconds", "sabotage.endSeconds" })
					GameBalance.ApplyFileText("{\"" + k + "\": " + GameBalance.Entries.First(e => e.Key == k).Default.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", new List<string>());
			}
		}

		[Fact]
		public void The_Site_Centers_Are_Open_Ground()
		{
			var d = Load();
			var world = new StaticWorld(d);
			foreach (var site in d.PointsOf(MapPointKind.BombSite))
			{
				var p = world.ResolveObstacles(site.Pos, 0.4f, 1.8f, 0.5f);
				Assert.True(Vec3.Distance(p, site.Pos) < 0.05f, $"le centre du site {site.Index} est dans un obstacle");
			}
		}
	}
}
