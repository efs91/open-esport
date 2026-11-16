using System;
using System.IO;
using System.Linq;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Points de butin posés dans l'éditeur : toujours présents, tirés au sort (chance commune ou propre au point), format du fichier.</summary>
	[Collection("Network")]
	public class LootPointTests
	{
		private static WorldData WorldWith(int count, Action<int, WorldData> make)
		{
			var d = WorldData.CreateFlat(600);
			for (int i = 0; i < count; i++) make(i, d);
			return d;
		}

		private static int Chests(WorldData d, int seed = 1)
		{
			var opt = new ServerOptions { Port = 0, StatsIntervalSeconds = 3600 };
			using var _ = new Restore();
			var s = new GameServer(opt, d);
			return s.LootCount;
		}

		/// <summary>Remet les réglages communs d'origine après un test qui les change.</summary>
		private sealed class Restore : IDisposable { public void Dispose()
		{
			// seulement les deux probabilités que ces tests changent : les autres tests (qui tournent en parallèle) gardent leurs réglages
			foreach (var k in new[] { "loot.pointChance", "loot.chestPointChance" })
				SetChance(k, GameBalance.Entries.First(e => e.Key == k).Default);
		}
	}

		private static LootSpawn Chest(float x, byte mode = LootSpawn.ModeAlways, bool hasChance = false, float chance = 0f)
			=> new LootSpawn { Pos = new Vec3(x, 100f, 0), Kind = LootSpawn.KindChest, Mode = mode, HasChance = hasChance, Chance = chance };

		[Fact]
		public void Always_Points_Are_Always_Filled()
		{
			var d = WorldWith(30, (i, w) => w.LootSpawns.Add(Chest(i * 4f)));
			for (int k = 0; k < 5; k++) Assert.Equal(30, Chests(d));
		}

		[Fact]
		public void A_Random_Point_Uses_The_Shared_Chance_Unless_It_Has_Its_Own()
		{
			using var _ = new Restore();
			var all = WorldWith(200, (i, w) => w.LootSpawns.Add(Chest(i * 2f, LootSpawn.ModeRandom)));

			GameBalance.Get("loot.chestPointChance"); // le réglage existe
			SetChance("loot.chestPointChance", 0.0);
			Assert.Equal(0, Chests(all));

			SetChance("loot.chestPointChance", 1.0);
			Assert.Equal(200, Chests(all));

			SetChance("loot.chestPointChance", 0.5);
			int half = Chests(all);
			Assert.InRange(half, 60, 140);                    // environ la moitié, d'une partie à l'autre

			// la valeur propre au point l'emporte sur le réglage commun
			SetChance("loot.chestPointChance", 0.0);
			var own = WorldWith(50, (i, w) => w.LootSpawns.Add(Chest(i * 2f, LootSpawn.ModeRandom, true, 1f)));
			Assert.Equal(50, Chests(own));
			SetChance("loot.chestPointChance", 1.0);
			var never = WorldWith(50, (i, w) => w.LootSpawns.Add(Chest(i * 2f, LootSpawn.ModeRandom, true, 0f)));
			Assert.Equal(0, Chests(never));
		}

		[Fact]
		public void Item_Points_Draw_Their_Own_Shared_Chance_And_A_Fixed_Item_Is_Kept()
		{
			using var _ = new Restore();
			SetChance("loot.pointChance", 0.0);
			var items = WorldWith(40, (i, w) => w.LootSpawns.Add(new LootSpawn { Pos = new Vec3(i * 3f, 100f, 10), ItemId = ItemIds.Ak, Mode = LootSpawn.ModeRandom }));
			Assert.Equal(0, Chests(items));
			SetChance("loot.pointChance", 1.0);
			Assert.True(Chests(items) >= 40);                 // chaque arme a au moins ses munitions à côté
		}

		[Fact]
		public void The_Map_File_Keeps_Kind_Mode_And_Chance()
		{
			var d = WorldData.CreateFlat(100);
			d.LootSpawns.Add(Chest(3f, LootSpawn.ModeRandom, true, 0.25f));
			d.LootSpawns.Add(new LootSpawn { Pos = new Vec3(1, 100, 1), ItemId = ItemIds.Ak, Count = 0 });
			using var ms = new MemoryStream();
			d.Save(ms);
			ms.Position = 0;
			var back = WorldData.Load(ms);
			Assert.Equal(2, back.LootSpawns.Count);
			Assert.True(back.LootSpawns[0].IsChest);
			Assert.Equal(LootSpawn.ModeRandom, back.LootSpawns[0].Mode);
			Assert.True(back.LootSpawns[0].HasChance);
			Assert.Equal(0.25f, back.LootSpawns[0].Chance);
			Assert.False(back.LootSpawns[1].IsChest);
			Assert.Equal(ItemIds.Ak, back.LootSpawns[1].ItemId);
		}

		[Fact]
		public void Chests_Block_Only_Once_They_Are_Placed_And_Disappear_When_Removed()
		{
			var w = new StaticWorld(WorldData.CreateFlat(300));
			var o = new Vec3(0, 100f + 0.3f, 0);
			Assert.False(w.Raycast(o, new Vec3(0, 0, -1), 30f, out _));
			w.SetDynamicBoxes(new[] { WorldData.ChestBox(new Vec3(0, 100f, -10)) });
			Assert.True(w.Raycast(o, new Vec3(0, 0, -1), 30f, out float d));
			Assert.InRange(d, 9.5f, 9.65f);
			w.SetDynamicBoxes(Array.Empty<Aabb>());
			Assert.False(w.Raycast(o, new Vec3(0, 0, -1), 30f, out _));
		}

		[Fact]
		public void A_Pool_Picks_By_Weight_And_Survives_The_File()
		{
			var spawn = new LootSpawn { Pos = new Vec3(0, 100, 0), PoolIds = new[] { ItemIds.Ak, ItemIds.C19 }, PoolWeights = new[] { 9f, 1f }, Quality = 6 };
			var rng = new Random(3);
			int ak = 0;
			for (int i = 0; i < 2000; i++) if (spawn.PickFromPool(rng) == ItemIds.Ak) ak++;
			Assert.InRange(ak, 1700, 1900);                                    // environ 90 %
			Assert.Equal(ItemIds.None, new LootSpawn().PickFromPool(rng));
			var d = WorldData.CreateFlat(100);
			d.LootSpawns.Add(spawn);
			using var ms = new MemoryStream(); d.Save(ms); ms.Position = 0;
			var back = WorldData.Load(ms).LootSpawns[0];
			Assert.Equal(new[] { ItemIds.Ak, ItemIds.C19 }, back.PoolIds);
			Assert.Equal(new[] { 9f, 1f }, back.PoolWeights);
			Assert.Equal(6, back.Quality);
		}

		[Fact]
		public void Quality_Forces_Or_Favours_A_Tier()
		{
			var rng = new Random(5);
			for (int i = 0; i < 200; i++) Assert.Equal(3, Tiers.Roll(rng, 8));        // que du légendaire
			int rare = 0;
			for (int i = 0; i < 2000; i++) if (Tiers.Roll(rng, 2) == 1) rare++;       // en majorité rare
			Assert.True(rare > 1300, $"rare = {rare}");
			int classic = 0;
			for (int i = 0; i < 2000; i++) if (Tiers.Roll(rng, 0) == 0) classic++;   // tirage normal : 55 % classique
			Assert.InRange(classic, 900, 1300);
		}

		[Fact]
		public void A_Chest_With_A_Pool_Holds_Four_Objects_From_The_List()
		{
			var d = WorldData.CreateFlat(300);
			d.LootSpawns.Add(new LootSpawn { Pos = new Vec3(0, 100, -10), Kind = LootSpawn.KindChest, PoolIds = new[] { ItemIds.HealSmall }, PoolWeights = new[] { 1f }, Quality = 8 });
			using var rig = new Rig(o => { }, d);
			rig.Run(0.3);
			int before = rig.Server.LootCount;
			rig.OnServer(() => rig.Server.OpenAllChests());
			rig.Run(0.3);
			Assert.Equal(before + 4, rig.Server.LootCount);
		}

		private static void SetChance(string key, double v)
		{
			var text = $"{{ \"{key}\": {v.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}";
			GameBalance.ApplyFileText(text, new System.Collections.Generic.List<string>());
		}
	}
}
