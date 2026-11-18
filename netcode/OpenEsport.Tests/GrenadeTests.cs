using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	[Collection("Network")]
	public class GrenadeTests
	{
		/// <summary>Un joueur seul qui regarde presque droit vers le haut : la grenade retombe à ses pieds.</summary>
		private static (Rig rig, Rig.Bot a) Setup(byte grenade, int count = 1)
		{
			var rig = new Rig(o => { o.Match.TeamSize = 1; o.SpawnClusterRadius = 5; });
			var a = rig.Connect("A");
			Assert.True(rig.WaitPlaying(a));
			rig.Give(a, grenade, count);
			rig.Run(0.3);
			a.Pitch = 1.5f;
			rig.Run(0.2);
			return (rig, a);
		}

		[Fact]
		public void A_Thrown_Grenade_Leaves_The_Bag_Flies_And_Is_Seen_By_The_Client()
		{
			var (rig, a) = Setup(ItemIds.GrenadeFrag, 2);
			using var _ = rig;
			a.C.SendCommand(CommandType.ThrowGrenade, ItemIds.GrenadeFrag);
			Assert.True(rig.WaitUntil(() => a.C.Grenades.Count == 1, 1.5), "le client voit la grenade en l'air");
			Assert.Equal(0, a.C.Grenades[0].Kind);
			Assert.Equal(1, a.C.Inventory.CountOf(ItemIds.GrenadeFrag));

			// pas de seconde grenade pendant le délai de relance
			a.C.SendCommand(CommandType.ThrowGrenade, ItemIds.GrenadeFrag);
			rig.Run(0.2);
			Assert.Equal(1, a.C.Inventory.CountOf(ItemIds.GrenadeFrag));
		}

		[Fact]
		public void A_Frag_Grenade_Explodes_After_Its_Fuse_And_Hurts_Whoever_Is_Close()
		{
			var (rig, a) = Setup(ItemIds.GrenadeFrag);
			using var _ = rig;
			a.C.SendCommand(CommandType.ThrowGrenade, ItemIds.GrenadeFrag);
			Assert.True(rig.WaitUntil(() => a.Events.Any(e => e.Type == EventType.Explosion), 5), "l'explosion est annoncée");
			var boom = a.Events.First(e => e.Type == EventType.Explosion);
			Assert.Equal(0, boom.C);
			rig.Run(0.3);
			Assert.True(a.C.Health < 100 || a.C.Shield < 50, $"retombée sur son lanceur : vie {a.C.Health}, bouclier {a.C.Shield}");
			Assert.Empty(a.C.Grenades);
		}

		[Fact]
		public void A_Flash_Grenade_Blinds_Without_Damage()
		{
			var (rig, a) = Setup(ItemIds.GrenadeFlash);
			using var _ = rig;
			a.C.SendCommand(CommandType.ThrowGrenade, ItemIds.GrenadeFlash);
			Assert.True(rig.WaitUntil(() => a.Events.Any(e => e.Type == EventType.Flashed), 5), "le joueur est aveuglé");
			var flash = a.Events.First(e => e.Type == EventType.Flashed);
			Assert.True(flash.C >= 3, $"durée {flash.C / 10f} s");
			rig.Run(0.3);
			Assert.Equal(100, a.C.Health);
		}

		[Fact]
		public void An_Incendiary_Grenade_Leaves_A_Fire_That_Burns()
		{
			var (rig, a) = Setup(ItemIds.GrenadeFire);
			using var _ = rig;
			a.C.SendCommand(CommandType.ThrowGrenade, ItemIds.GrenadeFire);
			Assert.True(rig.WaitUntil(() => a.C.Grenades.Any(g => g.Kind == GrenadeDefs.FireZoneKind), 6), "une zone de feu apparaît");
			int shield = a.C.Shield, health = a.C.Health;
			rig.Run(3.0);
			Assert.True(a.C.Health < health || a.C.Shield < shield, "le feu brûle ce qui reste dedans (sinon la grenade a roulé hors de la zone)");
		}

		[Fact]
		public void A_Grenade_In_Hand_Is_Thrown_By_The_Left_Click_And_The_Weapon_Comes_Back()
		{
			using var rig = new Rig(o => { o.Match.TeamSize = 1; o.SpawnClusterRadius = 5; });
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Ak);
			rig.Give(a, ItemIds.GrenadeFrag, 2); rig.Give(a, ItemIds.GrenadeFlash);
			rig.Run(0.3);
			a.Pitch = 1.5f; rig.Run(0.2);
			// pas de grenade qu'on n'a pas
			a.C.SendCommand(CommandType.EquipGrenade, ItemIds.GrenadeFire); rig.Run(0.4);
			Assert.Equal(0, a.C.Inventory.GrenadeInHand);
			a.C.SendCommand(CommandType.EquipGrenade, ItemIds.GrenadeFrag);
			Assert.True(rig.WaitUntil(() => a.C.Inventory.GrenadeInHand == ItemIds.GrenadeFrag, 2), "la grenade est en main");
			Assert.Equal(-1, a.C.Inventory.ActiveSlot);                       // mains nues : l'arme est rangée
			Assert.Empty(a.C.Grenades);
			a.Buttons = Buttons.Fire; rig.Run(0.3); a.Buttons = Buttons.None;
			Assert.True(rig.WaitUntil(() => a.C.Grenades.Count == 1, 2), "le clic gauche la lance");
			Assert.True(rig.WaitUntil(() => a.C.Inventory.GrenadeInHand == 0 && a.C.Inventory.ActiveSlot == 0, 2), "la main est libre et l'arme revient");
			Assert.Equal(1, a.C.Inventory.CountOf(ItemIds.GrenadeFrag));
			// la ranger sans la lancer
			rig.Run(1.0);
			a.C.SendCommand(CommandType.EquipGrenade, ItemIds.GrenadeFlash);
			Assert.True(rig.WaitUntil(() => a.C.Inventory.GrenadeInHand == ItemIds.GrenadeFlash, 2));
			a.C.SendCommand(CommandType.EquipGrenade, 0);
			Assert.True(rig.WaitUntil(() => a.C.Inventory.GrenadeInHand == 0 && a.C.Inventory.ActiveSlot == 0, 2), "rangée : l'arme revient");
			Assert.Equal(1, a.C.Inventory.CountOf(ItemIds.GrenadeFlash));
		}

		[Fact]
		public void Throwing_Without_A_Grenade_Does_Nothing()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("A");
			Assert.True(rig.WaitPlaying(a));
			a.C.SendCommand(CommandType.ThrowGrenade, ItemIds.GrenadeFrag);
			rig.Run(0.6);
			Assert.Empty(a.C.Grenades);
		}

		[Fact]
		public void Grenades_And_The_Sniper_Are_Registered_In_The_Item_Tables_And_The_Balance_File()
		{
			foreach (var id in new[] { ItemIds.GrenadeFrag, ItemIds.GrenadeSticky, ItemIds.GrenadeFire, ItemIds.GrenadeFlash })
			{
				Assert.Equal(ItemKind.Grenade, ItemDefs.Get(id).Kind);
				Assert.NotNull(GrenadeDefs.Get(id));
				Assert.True(GameBalance.Has($"grenade.{GrenadeDefs.NameOf(GrenadeDefs.Get(id))}.fuse"));
			}
			var sniper = WeaponDefs.Get(ItemIds.Sniper);
			Assert.NotNull(sniper);
			Assert.Equal(800f, sniper.Speed);
			Assert.Equal(GameBalance.BulletSpeed, WeaponDefs.Get(ItemIds.Sawnoff).Speed);
			Assert.Equal(ItemIds.AmmoSniper, ItemDefs.AmmoItemFor(sniper.AmmoType));
		}
	}
}
