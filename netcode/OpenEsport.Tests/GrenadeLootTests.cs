using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Les grenades se trouvent vraiment : au sol (part réglable) et dans les coffres.</summary>
	public class GrenadeLootTests
	{
		private static bool IsGrenade(byte id) => ItemDefs.Get(id)?.Kind == ItemKind.Grenade;

		[Fact]
		public void About_One_Ground_Item_In_Eight_Is_A_Grenade()
		{
			var rng = new Random(7);
			int n = 20000, g = Enumerable.Range(0, n).Count(_ => IsGrenade(LootTable.Roll(rng)));
			double share = g / (double)n;
			Assert.InRange(share, GameBalance.GroundGrenadeShare - 0.02, GameBalance.GroundGrenadeShare + 0.02);
		}

		[Fact]
		public void Roughly_Four_Chests_In_Ten_Hold_A_Grenade()
		{
			var rng = new Random(11);
			int n = 5000, withGrenade = 0;
			for (int i = 0; i < n; i++) { var buf = new byte[4]; int k = LootTable.RollChest(rng, buf); if (buf.Take(k).Any(IsGrenade)) withGrenade++; }
			Assert.InRange(withGrenade / (double)n, 0.30, 0.55);
		}

		[Fact]
		public void All_Four_Grenade_Types_Can_Drop()
		{
			var rng = new Random(3);
			var seen = Enumerable.Range(0, 4000).Select(_ => LootTable.RollGrenade(rng)).Distinct().OrderBy(x => x).ToArray();
			Assert.Equal(new[] { ItemIds.GrenadeFrag, ItemIds.GrenadeSticky, ItemIds.GrenadeFire, ItemIds.GrenadeFlash }, seen);
		}
	}
}
