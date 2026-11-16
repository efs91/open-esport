using System;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class HitboxTests
	{
		private static bool Hit(float y, float side, out HitZone zone, Stance st = Stance.Standing)
			=> Hitboxes.RayVsPlayer(new Vec3(0, 0, 0), st, new Vec3(side, y, 20), new Vec3(0, 0, -1), 50f, out _, out zone);

		[Fact]
		public void Zones_Follow_The_Body()
		{
			Assert.True(Hit(1.65f, 0f, out var z) && z == HitZone.Head);
			Assert.True(Hit(1.2f, 0f, out z) && z == HitZone.Torso);
			Assert.True(Hit(0.4f, 0f, out z) && z == HitZone.Legs);
		}

		[Fact]
		public void Shots_Beside_The_Body_Miss()
		{
			Assert.False(Hit(0.4f, 0.37f, out _));      // à côté des jambes
			Assert.False(Hit(1.65f, 0.33f, out _));      // à côté de la tête
			Assert.True(Hit(1.2f, 0.3f, out _));        // mais les bras comptent pour le torse
		}

		[Fact]
		public void Crouching_Shrinks_The_Body()
		{
			Assert.False(Hit(1.5f, 0f, out _, Stance.Crouching));
			Assert.True(Hit(1.05f, 0f, out var z, Stance.Crouching) && z == HitZone.Head);
		}
	}
}

namespace OpenEsport.Tests
{
	public class HeadshotIntegrationTests
	{
		private static (int confirms, float lost) Shoot(float targetY, bool moving)
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -12));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.OnServer(() => rig.Server.GetPlayer(b.C.PlayerId).Health = 1e6f);
			if (moving) { b.Strafe = 1; b.Yaw = 0; }
			rig.Run(0.5);
			float before = 1e6f;
			a.Pitch = MathF.Atan2(targetY - 1.6f, 12f);
			a.Buttons = Buttons.Fire;
			rig.Run(0.4);
			a.Buttons = Buttons.None;
			rig.Run(0.3);
			float lost = before - rig.Server.GetPlayer(b.C.PlayerId).Health;
			return (a.HitConfirms, lost);
		}

		[Fact]
		public void Aiming_At_The_Head_Of_A_Still_Target_Hits_The_Head_For_More_Damage()
		{
			var (hc, lostHead) = Shoot(1.65f, false);
			var (_, lostBody) = Shoot(1.1f, false);
			Assert.True(hc >= 2, $"touches à la tête confirmées : {hc}");
			Assert.True(lostHead > lostBody * 1.3f, $"tête {lostHead} contre torse {lostBody}");
		}
	}
}

namespace OpenEsport.Tests
{
	public class AutoReloadTests
	{
		[Fact]
		public void Pulling_The_Trigger_On_An_Empty_Weapon_Reloads_It_When_Ammo_Remains()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -10));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Run(0.3);
			rig.OnServer(() => rig.Server.GetPlayer(a.C.PlayerId).Inventory.Active.Magazine = 0);
			rig.Run(0.2);
			a.Buttons = Buttons.Fire;
			rig.Run(0.2);
			a.Buttons = Buttons.None;
			Assert.True(rig.WaitUntil(() => a.C.Reloading, 1.0), "tirer à vide doit lancer le rechargement");
			Assert.True(rig.WaitUntil(() => a.C.Magazine == 30, 4.0), $"le chargeur doit se remplir (chargeur : {a.C.Magazine})");
		}
	}
}
