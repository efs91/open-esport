using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Le sac est toujours rangé dans le même ordre, et le glisser-déposer d'un viseur vise une arme précise ou le sol.</summary>
	[Collection("Network")]
	public class InventoryOrderTests
	{
		[Fact]
		public void The_Bag_Is_Always_Sorted_Heavy_Ammo_First_Then_Light_Optics_Supplies_Grenades_And_The_Bomb_Last()
		{
			var inv = new InventoryState();
			var none = new List<LootItem>();
			// arrivée dans le désordre
			foreach (var id in new[] { ItemIds.GrenadeFlash, ItemIds.Ammo9mm, ItemIds.OpticHolo, ItemIds.HealSmall, ItemIds.AmmoSniper, ItemIds.AmmoRifle, ItemIds.GrenadeFrag, ItemIds.AmmoShell })
			{
				var it = LootItem.Of(id);
				Assert.True(inv.TryPickup(ref it, none), $"ramassage de {id}");
			}
			inv.Items.Add(new ItemStack { Id = ItemIds.Bomb, Count = 1 });
			inv.SortItems();
			var ids = inv.Items.Select(s => s.Id).ToList();
			Assert.Equal(new byte[] { ItemIds.AmmoSniper, ItemIds.AmmoRifle, ItemIds.AmmoShell, ItemIds.Ammo9mm, ItemIds.OpticHolo, ItemIds.HealSmall, ItemIds.GrenadeFrag, ItemIds.GrenadeFlash, ItemIds.Bomb }.Take(ids.Count), ids);
		}

		[Fact]
		public void An_Optic_Can_Be_Mounted_On_The_Weapon_In_The_Other_Slot_Or_Dropped_On_The_Ground()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.Smg45);
			rig.Give(a, ItemIds.Optic4x);                                                 // se monte tout seul sur l'arme en main (la dernière ramassée)
			rig.Run(0.3);
			a.C.SendCommand(CommandType.UnequipOptic, 0); a.C.SendCommand(CommandType.UnequipOptic, 1);      // le viseur passe dans le sac (et le client est informé)
			Assert.True(rig.WaitUntil(() => a.C.Inventory.Items.Any(s => s.Id == ItemIds.Optic4x) && a.C.Inventory.Weapons[0] != null, 2), "le viseur est dans le sac");
			int idx = a.C.Inventory.Items.FindIndex(s => s.Id == ItemIds.Optic4x);
			int other = a.C.Inventory.ActiveSlot == 0 ? 1 : 0;

			a.C.SendCommand(CommandType.EquipOpticOn, (byte)(other * 16 + idx));
			Assert.True(rig.WaitUntil(() => a.C.Inventory.Weapons[other].OpticId == ItemIds.Optic4x, 2), "monté sur l'arme qui n'est pas en main");
			Assert.DoesNotContain(a.C.Inventory.Items, s => s.Id == ItemIds.Optic4x);

			a.C.SendCommand(CommandType.DropOptic, (byte)other);
			Assert.True(rig.WaitUntil(() => a.C.Inventory.Weapons[other].OpticId == ItemIds.None, 2), "jeté à terre : l'arme n'a plus de viseur");
			Assert.DoesNotContain(a.C.Inventory.Items, s => s.Id == ItemIds.Optic4x);     // il est par terre, pas dans le sac
		}
	}
}
