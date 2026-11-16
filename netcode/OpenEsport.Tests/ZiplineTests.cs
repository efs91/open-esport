using System;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class ZiplineTests
	{
		private const float CableY = 107f;       // sol à 100 m : câble à 7 m

		private static StaticWorld World(bool pad = false)
		{
			var data = WorldData.CreateFlat(600, 100f);
			var z = new ZipLine { Points = new[] { new Vec3(0, CableY, 0), new Vec3(0, CableY, -100), new Vec3(0, CableY + 4, -200) } };
			z.Build();
			data.Ziplines.Add(z);
			if (pad) data.Pads.Add(new LaunchPad { Pos = new Vec3(5, 100, -40), Line = 0, Dist = 40f });
			return new StaticWorld(data);
		}

		private static PlayerInput In(ushort seq, Buttons b, float yaw = 0f, float fwd = 0f)
		{
			float yawN = yaw;
			return PlayerInput.Create(seq, fwd, 0, b, 0, yawN, 0f, 0u);
		}

		private static PlayerState AtCable(float distAlong)
		{
			float y = distAlong <= 100f ? CableY : CableY + 4f * (distAlong - 100f) / 100f;     // le câble monte après 100 m
			return new PlayerState { Pos = new Vec3(0, y - Sim.ZipHoldHeight, -distAlong), OnGround = false };
		}

		[Fact]
		public void E_At_Cable_Height_Grabs_The_Zipline_And_Goes_Where_The_Player_Looks_At_Twenty_Meters_Per_Second()
		{
			var world = World();
			var s = AtCable(20f);
			PlayerMovement.Step(ref s, In(1, Buttons.Interact, 0f), world, 1f / 60f);
			Assert.Equal(Stance.Zipping, s.Stance);
			Assert.Equal(1, s.ZipDir);                       // il regarde vers -Z : vers la fin du câble
			for (ushort i = 2; i < 140; i++) PlayerMovement.Step(ref s, In(i, Buttons.None, 0f), world, 1f / 60f);
			Assert.InRange(new Vec3(s.Vel.X, 0, s.Vel.Z).Length(), 19.5f, 20.5f);
			Assert.True(s.Pos.Z < -60f, $"il avance vers -Z : {s.Pos.Z}");
			Assert.InRange(s.Pos.Y + Sim.ZipHoldHeight, CableY - 0.01f, CableY + 1.5f);   // sa main est sur le câble

			// regarde dans l'autre sens : il part vers le début du câble
			var t = AtCable(50f);
			PlayerMovement.Step(ref t, In(1, Buttons.Interact, MathF.PI), world, 1f / 60f);
			Assert.Equal(-1, t.ZipDir);
			for (ushort i = 2; i < 60; i++) PlayerMovement.Step(ref t, In(i, Buttons.None, MathF.PI), world, 1f / 60f);
			Assert.True(t.Pos.Z > -45f, $"il revient vers +Z : {t.Pos.Z}");
		}

		[Fact]
		public void E_Far_From_The_Cable_Does_Nothing()
		{
			var world = World();
			var s = new PlayerState { Pos = new Vec3(0, 100, -20), OnGround = true };    // au sol, le câble est à 7 m
			PlayerMovement.Step(ref s, In(1, Buttons.Interact, 0f), world, 1f / 60f);
			Assert.Equal(0, s.Zip);
		}

		[Fact]
		public void Jumping_Off_Hops_Up_And_The_Player_Can_Turn_Round_And_Take_The_Cable_Back()
		{
			var world = World();
			var s = AtCable(30f);
			PlayerMovement.Step(ref s, In(1, Buttons.Interact, 0f), world, 1f / 60f);
			for (ushort i = 2; i < 50; i++) PlayerMovement.Step(ref s, In(i, Buttons.None, 0f), world, 1f / 60f);
			float zBefore = s.Pos.Z;
			PlayerMovement.Step(ref s, In(60, Buttons.Jump, 0f), world, 1f / 60f);
			Assert.Equal(0, s.Zip);
			Assert.True(s.Vel.Y > 4f, $"petit saut vers le haut : {s.Vel.Y}");

			// il se retourne et rappuie sur E quand sa main repasse à hauteur du câble
			bool regrabbed = false;
			for (ushort i = 61; i < 200 && !regrabbed; i++)
			{
				PlayerMovement.Step(ref s, In(i, i % 2 == 0 ? Buttons.Interact : Buttons.None, MathF.PI), world, 1f / 60f);   // appuis successifs
				regrabbed = s.Zip != 0;
			}
			Assert.True(regrabbed, "il doit pouvoir reprendre le câble");
			Assert.Equal(-1, s.ZipDir);
			for (ushort i = 300; i < 360; i++) PlayerMovement.Step(ref s, In(i, Buttons.None, MathF.PI), world, 1f / 60f);
			Assert.True(s.Pos.Z > zBefore, "il repart dans l'autre sens");
		}

		[Fact]
		public void A_Launch_Pad_Throws_The_Player_Onto_The_Cable_Without_Pressing_Anything()
		{
			var world = World(pad: true);
			var s = new PlayerState { Pos = new Vec3(5, 100, -40), OnGround = true };
			bool onCable = false;
			for (ushort i = 1; i < 150 && !onCable; i++)
			{
				PlayerMovement.Step(ref s, In(i, Buttons.None, 0f), world, 1f / 60f);
				onCable = s.Zip != 0;
			}
			Assert.True(onCable, "le tremplin doit amener sur le câble");
		}

		[Fact]
		public void The_Player_Is_Let_Go_At_The_End_Of_The_Cable()
		{
			var world = World();
			var s = AtCable(150f);
			PlayerMovement.Step(ref s, In(1, Buttons.Interact, 0f), world, 1f / 60f);
			for (ushort i = 2; i < 400 && s.Zip != 0; i++) PlayerMovement.Step(ref s, In(i, Buttons.None, 0f), world, 1f / 60f);
			Assert.Equal(0, s.Zip);
			Assert.True(s.Pos.Z < -195f, $"arrivé au bout : {s.Pos.Z}");
		}

		[Fact]
		public void Glass_Tunnel_Stops_Bullets_But_Not_From_Below()
		{
			var world = World();
			// tir depuis l'extérieur, de côté, à hauteur du câble : le verre arrête la balle avant le centre
			Assert.True(world.Raycast(new Vec3(-10, CableY, -50), new Vec3(1, 0, 0), 20f, out float d));
			Assert.InRange(d, 7f, 7.6f);                        // 10 - 2,5
			// de dessous, vers le haut dans l'axe : le dessous est ouvert, mais le dessus arrête la balle
			Assert.True(world.Raycast(new Vec3(0, 101, -50), new Vec3(0, 1, 0), 20f, out float up));
			Assert.InRange(up, 8f, 9f);                         // atteint le plafond de verre à 109,5 m
			// sous le tunnel, à l'horizontale : rien
			Assert.False(world.Raycast(new Vec3(-10, 101, -50), new Vec3(1, 0, 0), 20f, out _));
		}

		[Fact]
		public void The_World_File_Keeps_Ziplines_And_Pads()
		{
			var data = World(pad: true).Data;
			using var ms = new System.IO.MemoryStream();
			data.Save(ms);
			ms.Position = 0;
			var back = WorldData.Load(ms);
			Assert.Single(back.Ziplines);
			Assert.Equal(3, back.Ziplines[0].Points.Length);
			Assert.InRange(back.Ziplines[0].Length, 200f, 210f);
			Assert.Single(back.Pads);
			Assert.Equal(data.Checksum(), back.Checksum());
		}

		[Fact]
		public void The_Zipline_State_Survives_The_Network_Format()
		{
			var s = new PlayerState { Pos = new Vec3(1, 2, 3), Zip = 5, ZipDist = 42f, ZipSpeed = 17f, ZipDir = -1, ZipCooldown = 0.2f, AssistLine = 3, Stance = Stance.Zipping };
			var w = new BitWriter(128);
			s.Write(w);
			var r = new BitReader(w.Buffer, 0, w.ByteLength);
			var b = PlayerState.Read(r);
			Assert.Equal(5, b.Zip); Assert.Equal(42f, b.ZipDist); Assert.Equal(17f, b.ZipSpeed); Assert.Equal(-1, b.ZipDir);
			Assert.Equal(3, b.AssistLine); Assert.Equal(Stance.Zipping, b.Stance);
		}
	
		[Fact]
		public void A_Player_On_The_Zipline_Keeps_His_Weapon_And_Can_Shoot()
		{
			var data = WorldData.CreateFlat(600, 100f);
			var z = new ZipLine { Points = new[] { new Vec3(0, CableY, 0), new Vec3(0, CableY, -300) } };
			z.Build();
			data.Ziplines.Add(z);
			using var rig = new Rig(o => o.Match.TeamSize = 1, data);
			var a = rig.Connect("Tireur"); Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.OnServer(() => { var p = rig.Server.GetPlayer(a.C.PlayerId); p.State.Pos = new Vec3(0, CableY - Sim.ZipHoldHeight, -10); p.State.OnGround = false; });
			rig.Run(0.2);
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.3);
			Assert.Equal(Stance.Zipping, a.C.Predicted.Stance);
			int magazine = a.C.Magazine;
			a.Buttons = Buttons.Fire; rig.Run(0.5); a.Buttons = Buttons.None; rig.Run(0.2);
			Assert.Equal(Stance.Zipping, a.C.Predicted.Stance);        // il glisse toujours
			Assert.True(a.C.Magazine < magazine, $"le tir part en glissant : chargeur {magazine} -> {a.C.Magazine}");
			Assert.Contains(a.Events, e => e.Type == EventType.Shot);
		}
	}
}
