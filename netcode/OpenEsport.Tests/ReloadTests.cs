using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Chargeur et rechargement vus du client : ils disent toujours de quelle arme ils parlent, et changer d'arme abandonne le rechargement sans rien perdre.</summary>
	[Collection("Network")]
	public class ReloadTests
	{
		[Fact]
		public void The_Magazine_Sent_To_The_Client_Always_Names_Its_Weapon_Even_During_A_Switch()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur");
			Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.Smg45);
			rig.Run(0.5);
			int ak = -1, smg = -1;
			rig.OnServer(() =>
			{
				var inv = rig.Server.GetPlayer(a.C.PlayerId).Inventory;
				for (int i = 0; i < inv.Weapons.Length; i++) { if (inv.Weapons[i]?.WeaponId == ItemIds.Ak) ak = i; if (inv.Weapons[i]?.WeaponId == ItemIds.Smg45) smg = i; }
				inv.Weapons[ak].Magazine = 0; inv.Weapons[smg].Magazine = 7;
			});
			bool wrong = false;
			a.Driver = bot =>
			{
				if (bot.C.MagazineWeapon == ItemIds.Ak && (bot.C.MagazineSlot != ak || bot.C.Magazine != 0)) wrong = true;
				if (bot.C.MagazineWeapon == ItemIds.Smg45 && (bot.C.MagazineSlot != smg || bot.C.Magazine != 7)) wrong = true;
				return true;
			};
			rig.Run(0.3);
			wrong = false;          // le temps que les valeurs posées ci-dessus arrivent
			for (int i = 0; i < 6; i++) { a.Slot = i % 2 == 0 ? ak + 1 : smg + 1; rig.Run(0.5); a.Slot = 0; rig.Run(0.1); }
			Assert.False(wrong, "un chargeur a été annoncé pour la mauvaise arme");
			Assert.Equal(ItemIds.Smg45, a.C.MagazineWeapon);
			Assert.Equal(7, a.C.Magazine);
		}

		[Fact]
		public void Switching_Weapon_While_Reloading_Cancels_It_Without_Losing_Ammo_And_It_Can_Be_Done_Again()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur");
			Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.Smg45); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Run(0.5);
			int ak = -1, smg = -1, before = 0;
			rig.OnServer(() =>
			{
				var inv = rig.Server.GetPlayer(a.C.PlayerId).Inventory;
				for (int i = 0; i < inv.Weapons.Length; i++) { if (inv.Weapons[i]?.WeaponId == ItemIds.Ak) ak = i; if (inv.Weapons[i]?.WeaponId == ItemIds.Smg45) smg = i; }
				inv.Weapons[ak].Magazine = 3; before = inv.GetAmmo(2);
			});
			a.Slot = ak + 1; rig.Run(0.3); a.Slot = 0; rig.Run(0.4);
			a.Buttons = Buttons.Reload; rig.Run(0.1); a.Buttons = Buttons.None;
			Assert.True(rig.WaitUntil(() => a.C.Reloading && a.C.MagazineWeapon == ItemIds.Ak, 1.0));
			a.Slot = smg + 1; rig.Run(0.3); a.Slot = 0;
			Assert.True(rig.WaitUntil(() => !a.C.Reloading && a.C.MagazineWeapon == ItemIds.Smg45, 1.0), "changer d'arme arrête le rechargement");
			rig.Run(2.6);
			int mag = -1, ammo = -1;
			rig.OnServer(() => { var inv = rig.Server.GetPlayer(a.C.PlayerId).Inventory; mag = inv.Weapons[ak].Magazine; ammo = inv.GetAmmo(2); });
			Assert.Equal(3, mag);
			Assert.Equal(before, ammo);
			a.Slot = ak + 1; rig.Run(0.3); a.Slot = 0; rig.Run(0.4);
			Assert.True(rig.WaitUntil(() => a.C.MagazineWeapon == ItemIds.Ak && a.C.Magazine == 3, 1.0));
			a.Buttons = Buttons.Reload; rig.Run(0.1); a.Buttons = Buttons.None;
			Assert.True(rig.WaitUntil(() => a.C.Magazine == WeaponDefs.Get(ItemIds.Ak).MagazineSize && !a.C.Reloading, 4.0), "le rechargement se refait normalement");
		}
	}
}
