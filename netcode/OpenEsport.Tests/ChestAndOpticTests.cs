using System;
using System.Collections.Generic;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class ChestAndOpticTests
	{
		private static WorldData WorldWithChest(out Vec3 chestPos)
		{
			var d = WorldData.CreateFlat(300);
			float g = d.HeightAt(0, 0);
			chestPos = new Vec3(0, g, -10);
			d.LootSpawns.Add(new LootSpawn { Pos = chestPos, Kind = LootSpawn.KindChest });
			return d;
		}

		[Fact]
		public void A_Chest_Stops_Bullets_And_Blocks_Players()
		{
			var w = new StaticWorld(WorldWithChest(out var c));
			w.SetDynamicBoxes(new[] { WorldData.ChestBox(c) });                       // le serveur et le client posent le coffre quand il existe dans la partie
			// un rayon à hauteur de poitrine vers le coffre s'arrête sur lui
			Assert.True(w.Raycast(new Vec3(0, c.Y + 0.3f, 0), new Vec3(0, 0, -1), 50f, out float d));
			Assert.InRange(d, 9.5f, 9.65f);                                   // face avant du coffre à z = -10 + 0,42, vue de z = 0
			// au-dessus du coffre, il passe
			Assert.False(w.Raycast(new Vec3(0, c.Y + 1.2f, 0), new Vec3(0, 0, -1), 50f, out _));
			// un joueur qui court dessus est arrêté
			var s = PlayerMovement.Spawn(new Vec3(0, 0, 0), w);
			for (ushort i = 1; i <= 240; i++) PlayerMovement.Step(ref s, PlayerInput.Create(i, 1f, 0f, Buttons.Sprint, 0, 0f, 0f, 0), w, 1f / 60f);
			Assert.True(s.Pos.Z > -10f + 0.42f, $"le coffre doit bloquer : z = {s.Pos.Z}");
		}

		[Fact]
		public void Replacing_A_Weapon_Moves_The_Optic_To_The_New_One_And_Drops_The_Ground_Weapons_Optic()
		{
			var inv = new InventoryState();
			var none = new List<LootItem>();
			var a = LootItem.Of(ItemIds.Ak); inv.TryPickup(ref a, none);
			var b = LootItem.Of(ItemIds.Smg45); inv.TryPickup(ref b, none);           // deux armes : les deux emplacements sont pris
			inv.ActiveSlot = Array.FindIndex(inv.Weapons, w => w != null && w.WeaponId == ItemIds.Ak);
			var optic = LootItem.Of(ItemIds.Optic4x); inv.TryPickup(ref optic, none);
			Assert.Equal(ItemIds.Optic4x, inv.Active.OpticId);

			var ground = LootItem.Of(ItemIds.C19); ground.Optic = ItemIds.OpticHolo;
			var dropped = new List<LootItem>();
			Assert.True(inv.TryPickup(ref ground, dropped));
			Assert.Equal(ItemIds.C19, inv.Active.WeaponId);
			Assert.Equal(ItemIds.Optic4x, inv.Active.OpticId);                          // on garde son viseur
			Assert.Contains(dropped, d => d.ItemId == ItemIds.Ak && d.Optic == ItemIds.None);   // l'ancienne arme tombe sans viseur
			Assert.Contains(dropped, d => d.ItemId == ItemIds.OpticHolo);              // le viseur de l'arme ramassée tombe au sol
		}

		[Fact]
		public void Without_An_Optic_On_The_Old_Weapon_The_New_Weapon_Keeps_Its_Own()
		{
			var inv = new InventoryState();
			var none = new List<LootItem>();
			var a = LootItem.Of(ItemIds.Ak); inv.TryPickup(ref a, none);
			var b = LootItem.Of(ItemIds.Smg45); inv.TryPickup(ref b, none);
			var ground = LootItem.Of(ItemIds.C19); ground.Optic = ItemIds.OpticHolo;
			var dropped = new List<LootItem>();
			inv.TryPickup(ref ground, dropped);
			Assert.Equal(ItemIds.OpticHolo, inv.Active.OpticId);
			Assert.DoesNotContain(dropped, d => d.ItemId == ItemIds.OpticHolo);
		}

		[Fact]
		public void Unarmed_Players_Run_Faster()
		{
			var w = new StaticWorld(WorldData.CreateFlat(2000));
			PlayerState Run(float scale)
			{
				var s = PlayerMovement.Spawn(Vec3.Zero, w);
				for (ushort i = 1; i <= 180; i++) PlayerMovement.Step(ref s, PlayerInput.Create(i, 1f, 0f, Buttons.Sprint, 0, 0f, 0f, 0), w, 1f / 60f, scale);
				return s;
			}
			float armed = new Vec3(Run(1f).Vel.X, 0, Run(1f).Vel.Z).Length();
			float bare = new Vec3(Run(GameBalance.UnarmedMultiplier).Vel.X, 0, Run(GameBalance.UnarmedMultiplier).Vel.Z).Length();
			Assert.InRange(bare / armed, 1.15f, 1.25f);
		}
	}
}
