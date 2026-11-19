using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	[Collection("Network")]
	public class DrapeauMapTests
	{
		private static WorldData Load([CallerFilePath] string here = "") =>
			WorldData.LoadFile(Path.Combine(Path.GetDirectoryName(here), "..", "..", "assets", "maps", "drapeau.world"));

		[Fact]
		public void Forts_And_Team_Markers_Are_Mirrored()
		{
			var d = Load();
			foreach (var b in d.Boxes)
				Assert.Contains(d.Boxes, other =>
					MathF.Abs(b.Min.X + other.Max.X) < .001f && MathF.Abs(b.Max.X + other.Min.X) < .001f &&
					b.Min.Y == other.Min.Y && b.Min.Z == other.Min.Z && b.Max.Y == other.Max.Y && b.Max.Z == other.Max.Z);
			var blue = d.Points.Where(p => p.Team == 0).ToList();
			Assert.Equal(10, blue.Count(p => p.Kind == MapPointKind.SpawnTeam));
			Assert.Single(blue, p => p.Kind == MapPointKind.FlagBase);
			Assert.Equal(blue.Count * 2, d.Points.Count);
			foreach (var p in blue)
				Assert.Contains(d.Points, q => q.Team == 1 && p.Kind == q.Kind && p.Pos.X == -q.Pos.X && p.Pos.Y == q.Pos.Y && p.Pos.Z == q.Pos.Z);
		}

		[Theory]
		[InlineData(0, 4)]
		[InlineData(-39, 4)]
		[InlineData(21, 0)]
		public void Either_Flag_Is_Reachable_With_Only_One_Crossing_Open(int z, int height)
		{
			var d = Load();
			void Block(float y0, float y1, float z0, float z1) =>
				d.Boxes.Add(new Aabb(new Vec3(-2, y0, z0), new Vec3(2, y1, z1)));
			Block(-1, 40, -122, z - 2);
			Block(-1, 40, z + 2, 122);
			if (height == 0) Block(3, 40, z - 2, z + 2);
			else Block(-1, 3.4f, z - 2, z + 2);
			var nav = Navigation.For(d);
			Assert.IsType<LayeredNav>(nav);
			var center = new Vec3(0,height,z);
			foreach (var flag in d.PointsOf(MapPointKind.FlagBase))
			{
				Assert.True(nav.FindPath(center, flag.Pos, new List<Vec3>(), 60000), $"crossing {center} to flag {flag.Pos}");
				foreach (var spawn in d.PointsOf(MapPointKind.SpawnTeam).Where(p => p.Team != flag.Team))
					Assert.True(nav.FindPath(spawn.Pos, center, new List<Vec3>(), 60000), $"spawn {spawn.Pos} to crossing {center}");
			}
		}

		[Fact]
		public void Gallery_Connects_The_Canal_To_Both_Vaults_Without_Surfacing()
		{
			var nav = Navigation.For(Load());
			foreach (var sign in new[] { -1, 1 })
			{
				var previous = new Vec3(0, 0, 21);
				foreach (var (x,z) in new[] { (37,21), (37,15), (57,15), (57,35), (81,35), (81,9) })
				{
					var next = new Vec3(sign*x, 0, z);
					Assert.True(nav.LineClear(previous, next), $"{previous} -> {next}");
					previous = next;
				}
				Assert.True(nav.LineClear(previous, new Vec3(sign*98.5f, 4, 9)));
			}
		}

		[Fact]
		public void Twenty_Clients_Spawn_Apart_And_A_Dropped_Flag_Stays_On_The_Bridge()
		{
			using var rig = new Rig(o =>
			{
				o.Mode = GameMode.CaptureTheFlag;
				o.Match.TeamSize = 10;
				o.Match.MaxPlayers = Modes.Get(o.Mode).PlayersFor(10);
				o.MinPlayers = 20;
				o.LobbyCountdown = .3f;
				o.FillBots = false;
			}, Load());
			var clients = Enumerable.Range(0, 20).Select(i => rig.Connect("J" + i)).ToList();
			Assert.All(clients, c => Assert.True(rig.WaitPlaying(c)));
			Assert.True(rig.WaitUntil(() => rig.Server.Phase == MatchPhase.Playing, 6));
			rig.Run(.3);
			foreach (var team in new[] { 0, 1 })
			{
				var players = clients.Select(c => rig.Server.GetPlayer(c.C.PlayerId)).Where(p => p.Team == team).ToList();
				Assert.Equal(10, players.Count);
				var markers = rig.World.PointsOf(MapPointKind.SpawnTeam).Where(p => p.Team == team);
				foreach (var marker in markers)
					Assert.Single(players, p => Vec3.Distance(p.State.Pos, marker.Pos) < 1.5f);
			}
			var blue = clients.First(c => rig.Server.GetPlayer(c.C.PlayerId).Team == 0);
			var red = clients.First(c => rig.Server.GetPlayer(c.C.PlayerId).Team == 1);
			rig.Teleport(blue, rig.World.PointsOf(MapPointKind.FlagBase).Single(p => p.Team == 1).Pos);
			rig.Run(.3);
			Assert.Equal(1, blue.C.Mode.Flag1.State);
			rig.Teleport(blue, new Vec3(0, 4, 0));
			rig.OnServer(() => rig.Server.AdminKill(red.C.PlayerId, blue.C.PlayerId));
			rig.Run(.3);
			Assert.Equal(2, red.C.Mode.Flag1.State);
			Assert.InRange(red.C.Mode.Flag1.Pos.Y, 3.99f, 4.01f);
		}
	}
}
