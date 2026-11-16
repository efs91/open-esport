using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class NavigationTests
	{
		/// <summary>Monde plat avec un long mur (x de -40 à 40, épais de 2 m, haut de 4 m) percé d'une porte de 2 m à x = 30.</summary>
		private static WorldData WallWorld(bool door = true)
		{
			var d = WorldData.CreateFlat(300, 100f);
			if (door)
			{
				d.Boxes.Add(new Aabb(new Vec3(-40, 100, -1), new Vec3(29, 104, 1)));
				d.Boxes.Add(new Aabb(new Vec3(31, 100, -1), new Vec3(40, 104, 1)));
			}
			else d.Boxes.Add(new Aabb(new Vec3(-40, 100, -1), new Vec3(40, 104, 1)));
			return d;
		}

		[Fact]
		public void A_Path_Goes_Around_A_Wall_Through_The_Door()
		{
			var nav = new NavGrid(WallWorld());
			var path = new List<Vec3>();
			Assert.True(nav.FindPath(new Vec3(24, 100, 15), new Vec3(24, 100, -15), path));
			Assert.True(path.Count >= 2, "il faut au moins un virage");
			// aucun point du chemin n'est dans le mur, et chaque tronçon est libre
			var prev = path[0];       // (le départ exact peut différer de la case de départ de moins d'un mètre)
			foreach (var p in path.Skip(1))
			{
				Assert.True(nav.LineClear(prev, p), $"tronçon bloqué de {prev} à {p}");
				prev = p;
			}
			Assert.Contains(path, p => MathF.Abs(p.X - 30f) < 2.5f && MathF.Abs(p.Z) < 3f);   // il passe par la porte
		}

		[Fact]
		public void Straight_Line_Is_Blocked_By_The_Wall_But_Not_Beside_It()
		{
			var nav = new NavGrid(WallWorld());
			Assert.False(nav.LineClear(new Vec3(0, 100, 10), new Vec3(0, 100, -10)));
			Assert.True(nav.LineClear(new Vec3(60, 100, 10), new Vec3(60, 100, -10)));
		}

		[Fact]
		public void No_Path_When_The_Goal_Is_Walled_Off()
		{
			// un enclos fermé de 4 murs autour du but
			var d = WorldData.CreateFlat(300, 100f);
			d.Boxes.Add(new Aabb(new Vec3(-6, 100, -6), new Vec3(6, 104, -4)));
			d.Boxes.Add(new Aabb(new Vec3(-6, 100, 4), new Vec3(6, 104, 6)));
			d.Boxes.Add(new Aabb(new Vec3(-6, 100, -6), new Vec3(-4, 104, 6)));
			d.Boxes.Add(new Aabb(new Vec3(4, 100, -6), new Vec3(6, 104, 6)));
			var nav = new NavGrid(d);
			Assert.False(nav.FindPath(new Vec3(-30, 100, 0), new Vec3(0, 100, 0), new List<Vec3>(), maxExpansions: 20000));
		}

		[Fact]
		public void Low_Obstacles_And_Steps_Do_Not_Block()
		{
			var d = WorldData.CreateFlat(300, 100f);
			d.Boxes.Add(new Aabb(new Vec3(-5, 100, -1), new Vec3(5, 100.4f, 1)));   // une marche de 40 cm : franchissable
			var nav = new NavGrid(d);
			Assert.True(nav.LineClear(new Vec3(0, 100, 10), new Vec3(0, 100, -10)));
		}

		[Fact]
		public void Bots_Walk_Around_A_Wall_To_Rejoin_Their_Human_Teammate()
		{
			var data = WallWorld();
			using var rig = new Rig(o => { o.FillBots = true; o.Match.MaxPlayers = 2; o.Match.TeamSize = 2; o.SpawnClusterRadius = 5; o.BotSkill = 0.5f; }, data);
			Assert.True(rig.WaitUntil(() => rig.Server.PlayerCount == 2, 6));
			var human = rig.Connect("Humain");
			Assert.True(rig.WaitPlaying(human));
			Assert.True(rig.WaitUntil(() => human.C.Roster.Count == 2, 3));
			var mateId = human.C.Roster.First(r => r.IsBot).Id;
			// le bot est derrière le mur, le joueur de l'autre côté
			rig.OnServer(() => { var b = rig.Server.GetPlayer(mateId); b.State = PlayerMovement.Spawn(new Vec3(0, 0, 14), rig.Collision); });
			rig.Teleport(human, new Vec3(0, 0, -14));
			rig.Run(30.0);
			var hp = rig.Server.GetPlayer(human.C.PlayerId).State.Pos;
			var bp = rig.Server.GetPlayer(mateId).State.Pos;
			float d = MathF.Sqrt((bp.X - hp.X) * (bp.X - hp.X) + (bp.Z - hp.Z) * (bp.Z - hp.Z));
			Assert.True(d < 14f, $"le bot doit contourner le mur : il est à {d:F0} m du joueur (bot z={bp.Z:F0}, joueur z={hp.Z:F0})");
		}
	}
}
