using System;
using System.Linq;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Fusils à pompe (canon scié, PeaceMaker) : groupé, perte de puissance, total des plombs, rechargement ; et tir coup par coup de l'AK.</summary>
	[Collection("Network")]
	public class ShotgunTests
	{
		/// <summary>Un tir à cette distance, visé à cette hauteur (m) : les confirmations reçues par le tireur.</summary>
		private static ServerEvent[] ShootAt(byte weapon, float distance, float height, bool aim, int shots = 1)
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -distance));
			rig.Give(a, weapon); rig.Give(a, ItemIds.AmmoShell, 16);
			rig.OnServer(() => rig.Server.GetPlayer(b.C.PlayerId).Health = 1e6f);
			rig.Run(0.8);
			a.Pitch = MathF.Atan2(height - Sim.EyeFor(Stance.Standing), distance);
			a.Buttons = aim ? Buttons.Aim : Buttons.None;
			rig.Run(0.2);
			for (int i = 0; i < shots; i++)
			{
				a.Buttons = (aim ? Buttons.Aim : Buttons.None) | Buttons.Fire;
				rig.Run(0.1);
				a.Buttons = aim ? Buttons.Aim : Buttons.None;
				rig.Run(0.2);
			}
			rig.Run(0.3);
			return a.Events.Where(e => e.Type == EventType.HitConfirm).ToArray();
		}

		[Theory]
		[InlineData(ItemIds.Sawnoff, 104)]
		[InlineData(ItemIds.Peacemaker, 104)]
		public void Aiming_Up_Close_Every_Pellet_Lands_And_The_Shooter_Sees_One_Total(byte weapon, int full)
		{
			foreach (float distance in new[] { 1.5f, 2f, 3f })
			{
				var hits = ShootAt(weapon, distance, 1.25f, aim: true);
				Assert.Single(hits);
				Assert.InRange(hits[0].C, full * 0.89f, full * 1.1f);
			}
		}

		[Theory]
		[InlineData(ItemIds.Sawnoff, 208)]
		[InlineData(ItemIds.Peacemaker, 208)]
		public void Aiming_Puts_The_Pellets_In_The_Head_Up_Close(byte weapon, int full)
		{
			var hits = ShootAt(weapon, weapon == ItemIds.Sawnoff ? 1.3f : 2f, HeadHeight(), aim: true);
			Assert.Single(hits);
			Assert.Equal((int)HitZone.Head, hits[0].D & 3);
			Assert.InRange(hits[0].C, full * 0.85f, full * 1.06f);      // un plomb du bord peut toucher le cou
		}

		[Fact]
		public void Hip_Fire_Spreads_More_Than_Aiming()
		{
			int hip = ShootAt(ItemIds.Peacemaker, 8f, 1.25f, aim: false).Sum(e => e.C), aimed = ShootAt(ItemIds.Peacemaker, 8f, 1.25f, aim: true).Sum(e => e.C);
			Assert.True(hip < aimed * 0.85f, $"à 8 m : hanche {hip}, visée {aimed}");
			var saw = WeaponDefs.Get(ItemIds.Sawnoff); var pm = WeaponDefs.Get(ItemIds.Peacemaker);
			Assert.True(saw.Spread(false) > pm.Spread(false) && saw.Spread(true) > pm.Spread(true), "le canon scié disperse plus que le PeaceMaker");
			Assert.InRange(ShootAt(ItemIds.Peacemaker, 1.2f, 1.25f, aim: false).Sum(e => e.C), 92, 115);      // à bout portant, tout touche même à la hanche
		}

		[Theory]
		[InlineData(ItemIds.Peacemaker, 50)]
		[InlineData(ItemIds.Sawnoff, 39)]
		public void Hip_Fire_At_A_Centered_Target_Five_Meters_Away_Always_Lands_A_Good_Part_Of_The_Shot(byte weapon, int least)
		{
			// cinq tirs sur la poitrine d'une cible immobile : aucun ne se réduit à un ou deux plombs
			for (int i = 0; i < 5; i++)
			{
				var hits = ShootAt(weapon, 5.5f, 1.2f, aim: false);
				Assert.Single(hits);
				Assert.True(hits[0].C >= least, $"tir {i} : {hits[0].C} de dégâts");
			}
		}

		/// <summary>Hauteur du centre de la tête d'un joueur debout immobile (la cible n'a pas d'arme en main).</summary>
		private static float HeadHeight()
		{
			var joints = new Vec3[BodyPoses.Joints];
			BodyPoses.Pose(joints, 0.5f, 0f, 0, BodyClip.Idle, 0f);
			return (joints[(int)BodyHitbox.J.Head].Y + joints[(int)BodyHitbox.J.HeadTop].Y) * 0.5f;
		}

		[Fact]
		public void The_Double_Barrel_Chains_Its_Two_Shots()
		{
			var hits = ShootAt(ItemIds.Sawnoff, 1.5f, 1.25f, aim: true, shots: 2);
			Assert.Equal(2, hits.Length);
			Assert.Equal(2, WeaponDefs.Get(ItemIds.Sawnoff).MagazineSize);
			Assert.Equal(2, Tiers.Magazine(WeaponDefs.Get(ItemIds.Sawnoff), (byte)ItemTier.Legendary));
		}

		[Fact]
		public void Pellets_Lose_Their_Power_With_Distance()
		{
			var d = WeaponDefs.Get(ItemIds.Peacemaker);
			Assert.Equal(1f, d.DamageAt(4f));
			Assert.InRange(d.DamageAt(10f), 0.5f, 0.95f);
			Assert.Equal(0f, d.DamageAt(30f));
			Assert.Equal(1f, WeaponDefs.Get(ItemIds.Ak).DamageAt(300f));
			int near = ShootAt(ItemIds.Peacemaker, 4f, 1.25f, aim: true).Sum(e => e.C), far = ShootAt(ItemIds.Peacemaker, 14f, 1.25f, aim: true).Sum(e => e.C);
			Assert.True(far > 0 && far < near * 0.8f, $"4 m : {near}, 14 m : {far}");
			Assert.Empty(ShootAt(ItemIds.Peacemaker, 32f, 1.25f, aim: true));
		}

		[Fact]
		public void The_Peacemaker_Holds_Six_Shells_Needs_A_Pump_Between_Shots_And_Reloads_Shell_By_Shell()
		{
			var d = WeaponDefs.Get(ItemIds.Peacemaker);
			Assert.Equal(6, d.MagazineSize);
			Assert.Equal(6, Tiers.Magazine(d, (byte)ItemTier.Legendary));
			Assert.InRange(d.SecondsBetweenShots, 0.8f, 1.1f);
			Assert.Equal(d.ReloadSeconds, d.ReloadTime(6), 2);
			Assert.InRange(d.ReloadTime(1), 0.8f, 1.4f);

			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur");
			Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Peacemaker); rig.Give(a, ItemIds.AmmoShell, 16);
			rig.Run(0.6);
			int Mag() { int m = -1; rig.OnServer(() => m = rig.Server.GetPlayer(a.C.PlayerId).Inventory.Active.Magazine); return m; }
			Assert.Equal(6, Mag());
			for (int i = 0; i < 3; i++) { a.Buttons = Buttons.Fire; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.15); }
			Assert.Equal(5, Mag());          // la pompe empêche d'enchaîner
			rig.Run(1.0);
			a.Buttons = Buttons.Reload; rig.Run(0.1); a.Buttons = Buttons.None;
			Assert.True(rig.WaitUntil(() => Mag() == 6, 1.6), "une cartouche se remet vite");
		}

		[Fact]
		public void A_Short_Click_Fires_One_Ak_Bullet_And_Holding_Fires_A_Burst()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur");
			Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Run(0.6);
			int Mag() { int m = -1; rig.OnServer(() => m = rig.Server.GetPlayer(a.C.PlayerId).Inventory.Active.Magazine); return m; }
			int start = Mag();
			for (int i = 1; i <= 3; i++)
			{
				a.Buttons = Buttons.Fire; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.15);
				Assert.Equal(start - i, Mag());
			}
			a.Buttons = Buttons.Fire; rig.Run(0.6); a.Buttons = Buttons.None; rig.Run(0.15);
			Assert.InRange(start - 3 - Mag(), 6, 9);
		}
	}
}
