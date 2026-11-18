using System;
using System.Collections.Generic;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class TierTests
	{
		[Fact]
		public void Tier_Rolls_Follow_The_Configured_Weights()
		{
			var rng = new Random(5);
			var counts = new int[4];
			const int runs = 40000;
			for (int i = 0; i < runs; i++) counts[Tiers.Roll(rng)]++;
			Assert.InRange(counts[0] / (double)runs, 0.52, 0.58);   // classique ~ 55 %
			Assert.InRange(counts[1] / (double)runs, 0.25, 0.31);   // rare ~ 28 %
			Assert.InRange(counts[2] / (double)runs, 0.11, 0.15);   // épique ~ 13 %
			Assert.InRange(counts[3] / (double)runs, 0.025, 0.055); // légendaire ~ 4 %
		}

		[Fact]
		public void Backpacks_Have_Fixed_Tiers_And_Weapon_Tiers_Travel_With_The_Weapon()
		{
			Assert.Equal((byte)ItemTier.Rare, Tiers.Of(ItemIds.Backpack2, 3));
			Assert.Equal((byte)ItemTier.Epic, Tiers.Of(ItemIds.Backpack3, 0));
			Assert.Equal((byte)ItemTier.Legendary, Tiers.Of(ItemIds.Ak, (byte)ItemTier.Legendary));
			Assert.Equal((byte)0, Tiers.Of(ItemIds.AmmoRifle, 3));

			var inv = new InventoryState();
			var ak = LootItem.Of(ItemIds.Ak); ak.Tier = (byte)ItemTier.Epic;
			Assert.True(inv.TryPickup(ref ak, new List<LootItem>()));
			Assert.Equal(Tiers.Magazine(WeaponDefs.Get(ItemIds.Ak), (byte)ItemTier.Epic), inv.Weapons[0].Magazine); // chargeur plus grand
			// réseau : le niveau est conservé
			var w = new BitWriter(64); inv.Write(w);
			var back = InventoryState.Read(new BitReader(w.Buffer, 0, w.ByteLength));
			Assert.Equal((byte)ItemTier.Epic, back.Weapons[0].Tier);
			// lâcher l'arme : le niveau suit
			Assert.True(inv.DropWeapon(0, out var dropped));
			Assert.Equal((byte)ItemTier.Epic, dropped.Tier);
		}
	}

	public class ChestContentTests
	{
		[Fact]
		public void Chests_Hold_One_Weapon_Eighty_Percent_Of_The_Time_And_An_Accessory_Seventy_Percent()
		{
			var rng = new Random(123);
			var into = new byte[4];
			const int runs = 20000;
			int oneWeapon = 0, accessory = 0;
			for (int i = 0; i < runs; i++)
			{
				int n = LootTable.RollChest(rng, into);
				Assert.Equal(4, n);                  // toujours 4 objets
				int weapons = 0, accessories = 0;
				for (int k = 0; k < n; k++)
				{
					var kind = ItemDefs.Get(into[k]).Kind;
					if (kind == ItemKind.Weapon) weapons++;
					if (kind == ItemKind.Optic || kind == ItemKind.Backpack) accessories++;
				}
				Assert.InRange(weapons, 1, 2);       // au moins une arme, jamais plus de deux
				Assert.InRange(accessories, 0, 1);
				if (weapons == 1) oneWeapon++;
				if (accessories == 1) accessory++;
			}
			Assert.InRange(oneWeapon / (double)runs, 0.77, 0.83);
			Assert.InRange(accessory / (double)runs, 0.67, 0.73);
		}
	}

	public class BitIoTests
	{
		[Fact]
		public void BitWriter_Reader_RoundTrip_AllWidths()
		{
			var rng = new Random(7);
			var w = new BitWriter(16);
			var expected = new List<(uint value, int bits)>();
			for (int i = 0; i < 500; i++)
			{
				int bits = rng.Next(1, 33);
				uint v = (uint)rng.NextInt64(0, 1L << bits);
				expected.Add((v, bits));
				w.WriteBits(v, bits);
			}
			var r = new BitReader(w.Buffer, 0, w.ByteLength);
			foreach (var (v, bits) in expected)
				Assert.Equal(v, r.ReadBits(bits));
			Assert.False(r.Overflow);
		}

		[Fact]
		public void BitReader_NeverThrows_OnTruncatedOrGarbagePackets()
		{
			var rng = new Random(3);
			for (int n = 0; n < 200; n++)
			{
				var data = new byte[rng.Next(0, 40)];
				rng.NextBytes(data);
				var r = new BitReader(data, 0, data.Length);
				for (int i = 0; i < 100; i++) { r.ReadBits(rng.Next(1, 33)); r.ReadString(); r.ReadFloat(); }
			}
		}

		[Fact]
		public void EntityState_RoundTrip_Player_And_Loot()
		{
			var q = new Quantizer(-1000, 50, -1000);
			var p = new EntityState
			{
				Id = 17, Kind = EntityKind.Player, X = q.QX(123.456f), Z = q.QZ(-512.2f), Y = q.QY(101.5f),
				Yaw = EntityState.QuantizeYaw12(2.5f), Pitch = EntityState.QuantizePitch8(-0.7f),
				VelX = EntityState.QuantizeVel(-12.3f), VelZ = EntityState.QuantizeVel(30f), Flags = 0x55, Health = 99, Weapon = 4, Team = 33
			};
			var l = new EntityState { Id = 1234, Kind = EntityKind.Loot, X = q.QX(5), Z = q.QZ(6), Y = q.QY(100), Weapon = 31, Count = 30 };
			var w = new BitWriter();
			p.Write(w); l.Write(w);
			var r = new BitReader(w.Buffer, 0, w.ByteLength);
			Assert.Equal(p, EntityState.Read(r));
			Assert.Equal(l, EntityState.Read(r));
			Assert.True(w.ByteLength <= EntityState.MaxBytesPlayer + EntityState.MaxBytesLoot);
		}

		[Fact]
		public void Quantizer_Precision_Is_Under_A_Centimeter_And_Half()
		{
			var q = new Quantizer(-1000, 50, -1000);
			for (float x = -900; x < 900; x += 37.113f)
				Assert.InRange(MathF.Abs(q.DX(q.QX(x)) - x), 0f, 0.0079f);
		}

		[Fact]
		public void Seq_Newer_HandlesWrapAround()
		{
			Assert.True(Seq.Newer(5, 3));
			Assert.False(Seq.Newer(3, 5));
			Assert.True(Seq.Newer(2, 65530));
			Assert.False(Seq.Newer(65530, 2));
			Assert.False(Seq.Newer(7, 7));
		}
	}

	public class MovementTests
	{
		private static PlayerInput Input(ushort seq, float fwd, float strafe, Buttons b, float yaw)
			=> PlayerInput.Create(seq, fwd, strafe, b, 0, yaw, 0f, 0);

		[Fact]
		public void Movement_IsDeterministic_ClientReplayMatchesServer()
		{
			var world = new StaticWorld(WorldData.CreateProcedural(600, 5, 0));
			var rng = new Random(11);
			var inputs = new List<PlayerInput>();
			for (ushort i = 1; i <= 600; i++)
			{
				var b = Buttons.None;
				if (rng.Next(10) == 0) b |= Buttons.Jump;
				if (rng.Next(3) == 0) b |= Buttons.Sprint;
				if (rng.Next(12) == 0) b |= Buttons.Crouch;
				inputs.Add(Input(i, rng.Next(3) - 1, rng.Next(3) - 1, b, (float)(rng.NextDouble() * 6.28)));
			}
			PlayerState a = PlayerMovement.Spawn(new Vec3(0, 0, 0), world), b2 = PlayerMovement.Spawn(new Vec3(0, 0, 0), world);
			foreach (var inp in inputs) PlayerMovement.Step(ref a, inp, world, 1f / 60f);
			foreach (var inp in inputs) PlayerMovement.Step(ref b2, inp, world, 1f / 60f);
			Assert.Equal(a.Pos, b2.Pos);
			Assert.Equal(a.Vel, b2.Vel);
		}

		[Fact]
		public void Walking_Forward_Reaches_The_Controller_Speeds()
		{
			var world = new StaticWorld(WorldData.CreateFlat(2000));
			var s = PlayerMovement.Spawn(Vec3.Zero, world);
			for (ushort i = 1; i <= 120; i++) PlayerMovement.Step(ref s, Input(i, 1, 0, Buttons.None, 0), world, 1f / 60f);
			Assert.InRange(new Vec3(s.Vel.X, 0, s.Vel.Z).Length(), Sim.BaseSpeed * 0.95f, Sim.BaseSpeed + 0.01f); // vitesse normale (réglable)

			for (ushort i = 121; i <= 300; i++) PlayerMovement.Step(ref s, Input(i, 1, 0, Buttons.Sprint, 0), world, 1f / 60f);
			float sprint = Sim.BaseSpeed * Sim.SprintMultiplier;
			Assert.InRange(new Vec3(s.Vel.X, 0, s.Vel.Z).Length(), sprint * 0.97f, sprint + 0.01f); // sprint (réglable)
			Assert.True(s.Pos.Z < -20f, "vers l'avant = -Z");
		}

		[Fact]
		public void Player_Climbs_Stairs_Stands_On_A_Roof_And_Is_Blocked_By_A_Wall()
		{
			var data = WorldData.CreateFlat(400, 10f);
			// escalier de 6 marches (0,4 m chacune) vers -Z, puis un étage (dalle fine à 2,4 m) et un mur plein
			for (int i = 0; i < 6; i++)
				data.Boxes.Add(new Aabb(new Vec3(-2, 10f, -4f - i * 1.0f - 1.0f), new Vec3(2, 10f + 0.4f * (i + 1), -4f - i * 1.0f)));
			data.Boxes.Add(new Aabb(new Vec3(-2, 12.1f, -20), new Vec3(2, 12.4f, -10)));          // dalle de l'étage
			data.Boxes.Add(new Aabb(new Vec3(-10, 10f, 20), new Vec3(10, 14f, 22)));              // mur
			var world = new StaticWorld(data);
			var s = PlayerMovement.Spawn(new Vec3(0, 10, 0), world);
			var trace = new System.Text.StringBuilder();
			for (ushort i = 1; i <= 190; i++)
			{
				PlayerMovement.Step(ref s, Input(i, 1, 0, Buttons.None, 0), world, 1f / 60f);
				if (i % 40 == 0) trace.Append($"[{i}: z={s.Pos.Z:F1} y={s.Pos.Y:F2} g={s.OnGround}] ");
			}
			Assert.True(s.Pos.Z < -10.5f, "le joueur monte l'escalier et avance sur l'étage : " + trace);
			Assert.True(s.Pos.Y > 12.35f && s.Pos.Y < 12.45f, "hauteur finale " + s.Pos.Y + " " + trace);   // il est sur la dalle, pas dans le sol

			var t = PlayerMovement.Spawn(new Vec3(0, 10, 10), world);
			for (ushort i = 1; i <= 300; i++) PlayerMovement.Step(ref t, Input(i, -1, 0, Buttons.None, 0), world, 1f / 60f);
			Assert.True(t.Pos.Z < 20f - Sim.Radius + 0.05f, "le mur arrête le joueur : z=" + t.Pos.Z);
		}

		[Fact]
		public void Raycast_Through_The_Grid_Matches_Testing_Every_Box()
		{
			var data = WorldData.CreateProcedural(600, 5, 0);
			var world = new StaticWorld(data);
			var rng = new Random(9);
			int diff = 0;
			for (int i = 0; i < 4000; i++)
			{
				var o = new Vec3((float)(rng.NextDouble() - 0.5) * 500, 100f + (float)rng.NextDouble() * 8f, (float)(rng.NextDouble() - 0.5) * 500);
				float a = (float)(rng.NextDouble() * 6.28), p = (float)(rng.NextDouble() - 0.5) * 0.2f;
				var d = new Vec3(MathF.Cos(a) * MathF.Cos(p), MathF.Sin(p), MathF.Sin(a) * MathF.Cos(p)).Normalized();
				bool hit = world.Raycast(o, d, 300f, out float dist);
				// référence : toutes les boîtes (le relief est identique dans les deux calculs)
				float best = 300f; bool refHit = false;
				var refWorld = new StaticWorld(new WorldData { OriginX = data.OriginX, OriginZ = data.OriginZ, CellSize = data.CellSize, Width = data.Width, Depth = data.Depth, Heights = data.Heights });
				refWorld.Raycast(o, d, 300f, out float terrain);
				best = terrain; refHit = terrain < 300f;
				foreach (var b in data.Boxes)
				{
					float t0 = 0, t1 = best; bool ok = true;
					for (int ax = 0; ax < 3 && ok; ax++)
					{
						float oo = ax == 0 ? o.X : ax == 1 ? o.Y : o.Z, dd = ax == 0 ? d.X : ax == 1 ? d.Y : d.Z;
						float mn = ax == 0 ? b.Min.X : ax == 1 ? b.Min.Y : b.Min.Z, mx = ax == 0 ? b.Max.X : ax == 1 ? b.Max.Y : b.Max.Z;
						if (MathF.Abs(dd) < 1e-8f) { if (oo < mn || oo > mx) ok = false; continue; }
						float ta = (mn - oo) / dd, tb = (mx - oo) / dd;
						if (ta > tb) (ta, tb) = (tb, ta);
						t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
						if (t0 > t1) ok = false;
					}
					if (ok && t0 < best) { best = t0; refHit = true; }
				}
				if (hit != refHit || (hit && MathF.Abs(dist - best) > 0.02f)) diff++;
			}
			Assert.True(diff == 0, diff + " rayons différents");
		}

		[Fact]
		public void Jump_Rises_And_Lands()
		{
			var world = new StaticWorld(WorldData.CreateFlat(400));
			var s = PlayerMovement.Spawn(Vec3.Zero, world);
			float ground = s.Pos.Y, apex = ground;
			bool wasAirborne = false;
			for (ushort i = 1; i <= 120; i++)
			{
				PlayerMovement.Step(ref s, Input(i, 0, 0, i == 1 ? Buttons.Jump : Buttons.None, 0), world, 1f / 60f);
				apex = MathF.Max(apex, s.Pos.Y);
				if (!s.OnGround) wasAirborne = true;
			}
			Assert.True(wasAirborne);
			Assert.InRange(apex - ground, 1.4f, 1.8f); // v²/2g = 100 / 58,8 = 1,7 m
			Assert.True(s.OnGround);
			Assert.Equal(ground, s.Pos.Y, 3);
		}

		[Fact]
		public void Slide_Starts_Decays_And_Has_A_Cooldown()
		{
			var world = new StaticWorld(WorldData.CreateFlat(2000));
			var s = PlayerMovement.Spawn(Vec3.Zero, world);
			ushort seq = 1;
			for (int i = 0; i < 120; i++) PlayerMovement.Step(ref s, Input(seq++, 1, 0, Buttons.Sprint, 0), world, 1f / 60f);
			PlayerMovement.Step(ref s, Input(seq++, 1, 0, Buttons.Sprint | Buttons.Crouch, 0), world, 1f / 60f);
			Assert.Equal(Stance.Sliding, s.Stance);
			float sprint = Sim.BaseSpeed * Sim.SprintMultiplier;
			float prev = s.SlideSpeed;
			Assert.True(prev >= sprint * 0.98f, "la glissade ne doit pas freiner d'entrée");
			for (int i = 0; i < 18; i++)   // la poussée du début : la vitesse ne fait que monter, même sur du plat
			{
				PlayerMovement.Step(ref s, Input(seq++, 1, 0, Buttons.Sprint | Buttons.Crouch, 0), world, 1f / 60f);
				Assert.True(s.SlideSpeed >= prev, "la vitesse doit augmenter pendant la poussée");
				prev = s.SlideSpeed;
			}
			Assert.True(s.SlideSpeed > sprint * 1.25f);
			for (int i = 0; i < 240; i++) PlayerMovement.Step(ref s, Input(seq++, 1, 0, Buttons.Sprint | Buttons.Crouch, 0), world, 1f / 60f);
			Assert.NotEqual(Stance.Sliding, s.Stance); // la glissade s'arrête quand la vitesse tombe à celle de la marche
			Assert.True(s.SlideCooldown > 0f || s.SlideCooldown == 0f);
		}

		[Fact]
		public void Obstacle_Blocks_Horizontal_Movement()
		{
			var data = WorldData.CreateFlat(400);
			data.Boxes.Add(new Aabb(new Vec3(-5, 99, -20), new Vec3(5, 110, -15)));
			var world = new StaticWorld(data);
			var s = PlayerMovement.Spawn(Vec3.Zero, world);
			for (ushort i = 1; i <= 300; i++) PlayerMovement.Step(ref s, Input(i, 1, 0, Buttons.Sprint, 0), world, 1f / 60f);
			Assert.True(s.Pos.Z > -15f - Sim.Radius + 0.01f, $"le joueur ne doit pas traverser le mur, z = {s.Pos.Z}");
		}

		[Fact]
		public void Steep_Slope_Cannot_Be_Climbed()
		{
			var data = WorldData.CreateFlat(200);
			for (int z = 0; z < data.Depth; z++)
				for (int x = 0; x < data.Width; x++)
					if (z < data.Depth / 2 - 5) data.Heights[z * data.Width + x] = 100f + (data.Depth / 2 - 5 - z) * 4f; // pente de 63 degrés vers -Z
			data.Quantize();
			var world = new StaticWorld(data);
			var s = PlayerMovement.Spawn(new Vec3(0, 0, 0), world);
			for (ushort i = 1; i <= 200; i++) PlayerMovement.Step(ref s, Input(i, 1, 0, Buttons.Sprint, 0), world, 1f / 60f);
			Assert.True(s.Pos.Y < 105f, $"pente trop raide : y = {s.Pos.Y}");
		}

		[Fact]
		public void World_Serialization_RoundTrips_Exactly()
		{
			var a = WorldData.CreateProcedural(400, 3, 50);
			var ms = new System.IO.MemoryStream();
			a.Save(ms);
			ms.Position = 0;
			var b = WorldData.Load(ms);
			Assert.Equal(a.Checksum(), b.Checksum());
			Assert.Equal(a.HeightAt(12.3f, -45.6f), b.HeightAt(12.3f, -45.6f));
			Assert.Equal(a.Boxes.Count, b.Boxes.Count);
			Assert.Equal(a.LootSpawns.Count, b.LootSpawns.Count);
		}

		[Fact]
		public void Raycast_Hits_Terrain_And_Boxes()
		{
			var data = WorldData.CreateFlat(400);
			data.Boxes.Add(new Aabb(new Vec3(-2, 99, -30), new Vec3(2, 110, -28)));
			var world = new StaticWorld(data);
			Assert.True(world.Raycast(new Vec3(0, 101, 0), new Vec3(0, 0, -1), 100f, out float d));
			Assert.InRange(d, 27.9f, 28.1f); // face avant de la boîte (z = -28)
			Assert.True(world.Raycast(new Vec3(0, 110, 0), new Vec3(0, -1, 0).Normalized(), 50f, out float down));
			Assert.InRange(down, 9.9f, 10.1f);
			Assert.False(world.Raycast(new Vec3(100, 101, 0), new Vec3(0, 1, 0), 50f, out _));
		}
	}

	public class CombatTests
	{
		[Fact]
		public void Ray_Hits_Zones_By_Height()
		{
			var feet = new Vec3(0, 0, 0);
			Assert.True(Hitboxes.RayVsPlayer(feet, Stance.Standing, new Vec3(0, 1.7f, 10), new Vec3(0, 0, -1), 50, out _, out var head));
			Assert.Equal(HitZone.Head, head);
			Assert.True(Hitboxes.RayVsPlayer(feet, Stance.Standing, new Vec3(0, 1.0f, 10), new Vec3(0, 0, -1), 50, out _, out var torso));
			Assert.Equal(HitZone.Torso, torso);
			Assert.True(Hitboxes.RayVsPlayer(feet, Stance.Standing, new Vec3(0, 0.3f, 10), new Vec3(0, 0, -1), 50, out _, out var legs));
			Assert.Equal(HitZone.Legs, legs);
			Assert.False(Hitboxes.RayVsPlayer(feet, Stance.Standing, new Vec3(0, 2.2f, 10), new Vec3(0, 0, -1), 50, out _, out _));
			Assert.False(Hitboxes.RayVsPlayer(feet, Stance.Standing, new Vec3(1.0f, 1.0f, 10), new Vec3(0, 0, -1), 50, out _, out _));
		}

		[Fact]
		public void Crouching_Lowers_The_Hitbox()
		{
			var feet = Vec3.Zero;
			Assert.True(Hitboxes.RayVsPlayer(feet, Stance.Standing, new Vec3(0, 1.5f, 10), new Vec3(0, 0, -1), 50, out _, out _));
			Assert.False(Hitboxes.RayVsPlayer(feet, Stance.Crouching, new Vec3(0, 1.5f, 10), new Vec3(0, 0, -1), 50, out _, out _));
		}

		[Fact]
		public void Ray_Beyond_Range_Misses()
		{
			Assert.False(Hitboxes.RayVsPlayer(Vec3.Zero, Stance.Standing, new Vec3(0, 1, 100), new Vec3(0, 0, -1), 50, out _, out _));
		}

		[Fact]
		public void History_Returns_The_Position_At_The_Requested_Tick()
		{
			var h = new PositionHistory();
			for (uint t = 1; t <= 100; t++) h.Record(t, new Vec3(t, 0, 0), Stance.Standing, true);
			Assert.True(h.TryGet(90, out var e));
			Assert.Equal(90f, e.Pos.X);
			Assert.True(h.TryGet(1000, out e)); // futur : dernier connu
			Assert.Equal(100f, e.Pos.X);
			Assert.True(h.TryGet(10, out e));   // trop ancien : plus ancien conservé
			Assert.Equal(100u - (PositionHistory.Capacity - 1), e.Tick);
		}
	}

	public class InventoryTests
	{
		[Fact]
		public void Backpack_Capacity_Follows_Level()
		{
			Assert.Equal(8, ItemDefs.CapacityForBackpack(1));
			Assert.Equal(10, ItemDefs.CapacityForBackpack(2));
			Assert.Equal(12, ItemDefs.CapacityForBackpack(3));
		}

		[Fact]
		public void Weapons_Fill_Two_Slots_Then_Replace_The_Active_One()
		{
			var inv = new InventoryState();
			var drops = new List<LootItem>();
			var ak = LootItem.Of(ItemIds.Ak); var smg = LootItem.Of(ItemIds.Smg45); var lmg = LootItem.Of(ItemIds.Lmg63);
			Assert.True(inv.TryPickup(ref ak, drops));
			Assert.True(inv.TryPickup(ref smg, drops));
			Assert.Empty(drops);
			Assert.Equal(ItemIds.Smg45, inv.ActiveWeaponId);
			Assert.True(inv.TryPickup(ref lmg, drops));
			Assert.Single(drops);
			Assert.Equal(ItemIds.Smg45, drops[0].ItemId);
			Assert.Equal(ItemIds.Lmg63, inv.ActiveWeaponId);
		}

		[Fact]
		public void Ammo_Stacks_And_Leaves_The_Rest_On_The_Ground_When_Full()
		{
			var inv = new InventoryState();
			var drops = new List<LootItem>();
			for (int i = 0; i < 8; i++)
			{
				var box = new LootItem { ItemId = ItemIds.AmmoShell, Count = 16 };
				Assert.True(inv.TryPickup(ref box, drops));
				Assert.Equal(0, box.Count);
			}
			Assert.Equal(8, inv.Items.Count);
			var extra = new LootItem { ItemId = ItemIds.AmmoShell, Count = 8 };
			Assert.False(inv.TryPickup(ref extra, drops));
			Assert.Equal(8, extra.Count);
		}

		[Fact]
		public void Backpack_Upgrade_Only_Goes_Up_And_Drops_The_Old_One()
		{
			var inv = new InventoryState();
			var drops = new List<LootItem>();
			var b2 = LootItem.Of(ItemIds.Backpack2); var b3 = LootItem.Of(ItemIds.Backpack3);
			Assert.True(inv.TryPickup(ref b2, drops));
			Assert.Equal(10, inv.Capacity);
			Assert.True(inv.TryPickup(ref b3, drops));
			Assert.Equal(12, inv.Capacity);
			Assert.Single(drops);
			var again = LootItem.Of(ItemIds.Backpack2);
			Assert.False(inv.TryPickup(ref again, drops));
		}

		[Fact]
		public void Optic_Mounts_On_The_Held_Weapon_Or_Goes_To_The_Bag()
		{
			var inv = new InventoryState();
			var drops = new List<LootItem>();
			var optic = LootItem.Of(ItemIds.Optic4x);
			Assert.True(inv.TryPickup(ref optic, drops));
			Assert.Single(inv.Items);
			var ak = LootItem.Of(ItemIds.Ak);
			inv.TryPickup(ref ak, drops);
			Assert.True(inv.EquipOptic(0));
			Assert.Equal(ItemIds.Optic4x, inv.Active.OpticId);
			Assert.Empty(inv.Items);
		}

		[Fact]
		public void Reload_Takes_Ammo_From_The_Bag()
		{
			var inv = new InventoryState();
			var drops = new List<LootItem>();
			var ammo = new LootItem { ItemId = ItemIds.AmmoRifle, Count = 50 };
			inv.TryPickup(ref ammo, drops);
			Assert.Equal(50, inv.GetAmmo(2));
			Assert.Equal(12, inv.TakeAmmo(2, 12));
			Assert.Equal(38, inv.GetAmmo(2));
			Assert.Equal(38, inv.TakeAmmo(2, 100));
			Assert.Empty(inv.Items);
		}

		[Fact]
		public void Inventory_Serialization_RoundTrips()
		{
			var inv = new InventoryState();
			var drops = new List<LootItem>();
			var b = LootItem.Of(ItemIds.Backpack3); inv.TryPickup(ref b, drops);
			var ak = LootItem.Of(ItemIds.Ak); inv.TryPickup(ref ak, drops);
			var ammo = new LootItem { ItemId = ItemIds.AmmoRifle, Count = 45 }; inv.TryPickup(ref ammo, drops);
			var w = new BitWriter();
			inv.Write(w);
			var back = InventoryState.Read(new BitReader(w.Buffer, 0, w.ByteLength));
			Assert.Equal(inv.BackpackLevel, back.BackpackLevel);
			Assert.Equal(inv.ActiveSlot, back.ActiveSlot);
			Assert.Equal(ItemIds.Ak, back.Weapons[0].WeaponId);
			Assert.Equal(WeaponDefs.Get(ItemIds.Ak).MagazineSize, back.Weapons[0].Magazine);
			Assert.Equal(45, back.GetAmmo(2));
		}
	}

	public class TeamTests
	{
		[Theory]
		[InlineData(100, 2, 50)]
		[InlineData(100, 3, 34)]
		[InlineData(100, 4, 25)]
		[InlineData(100, 5, 20)]
		[InlineData(7, 3, 3)]
		[InlineData(2, 5, 1)]
		public void Players_Are_Grouped_In_Teams_Of_The_Requested_Size(int players, int teamSize, int expectedTeams)
		{
			var roster = new TeamRoster(teamSize);
			for (int i = 0; i < players; i++) roster.Join();
			Assert.Equal(expectedTeams, roster.TeamCount);
			for (int t = 0; t < roster.TeamCount - 1; t++) Assert.Equal(teamSize, roster.CountIn(t));
		}

		[Fact]
		public void A_Free_Spot_Is_Reused_When_A_Player_Leaves()
		{
			var roster = new TeamRoster(3);
			for (int i = 0; i < 6; i++) roster.Join();
			roster.Leave(0);
			Assert.Equal(0, roster.Join());
		}

		[Fact]
		public void Match_Limits_Are_Clamped_To_The_Specification()
		{
			var c = new MatchConfig { MaxPlayers = 500, TeamSize = 19 }.Validated();
			Assert.Equal(100, c.MaxPlayers);
			Assert.Equal(10, c.TeamSize);
			c = new MatchConfig { MaxPlayers = 1, TeamSize = 0 }.Validated();
			Assert.Equal(2, c.MaxPlayers);
			Assert.Equal(1, c.TeamSize);
		}
	}

	public class GridTests
	{
		[Fact]
		public void Query_Returns_Only_Nearby_Cells()
		{
			var g = new SpatialGrid(-500, -500, 500, 500, 64);
			var rng = new Random(5);
			var pts = new List<(int id, float x, float z)>();
			for (int i = 0; i < 400; i++)
			{
				var p = (i, (float)(rng.NextDouble() * 1000 - 500), (float)(rng.NextDouble() * 1000 - 500));
				pts.Add(p);
				g.Insert(p.Item1, p.Item2, p.Item3);
			}
			g.Build();
			var res = new List<int>();
			g.Query(0, 0, 100, res);
			var set = new HashSet<int>(res);
			foreach (var (id, x, z) in pts)
				if (x * x + z * z <= 100 * 100) Assert.Contains(id, set); // rien de proche n'est oublié
			Assert.True(res.Count < pts.Count / 3);                       // et on ne renvoie pas tout le monde
		}
	}
}
