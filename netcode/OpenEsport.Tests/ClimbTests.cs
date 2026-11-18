using System;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class ClimbTests
	{
		private static PlayerInput In(ushort seq, bool fwd, bool jump)
			=> PlayerInput.Create(seq, fwd ? 1f : 0f, 0f, jump ? Buttons.Jump : Buttons.None, 0, 0f, 0f, 0);

		/// <summary>Un bloc de haut <paramref name="h"/> au-dessus du sol, devant le joueur (il regarde vers -Z).</summary>
		private static (StaticWorld w, float ground) Setup(float h, float depth = 6f)
		{
			var d = WorldData.CreateFlat(300);
			float g = d.HeightAt(0, 0);
			d.Boxes.Add(new Aabb(new Vec3(-5, g - 1, -2 - depth), new Vec3(5, g + h, -2)));
			return (new StaticWorld(d), g);
		}

		private static PlayerState Run(StaticWorld w, float startZ, int ticks, bool holdJump, out bool climbed)
		{
			var s = PlayerMovement.Spawn(new Vec3(0, 0, startZ), w);
			climbed = false;
			ushort seq = 1;
			for (int i = 0; i < ticks; i++)
			{
				PlayerMovement.Step(ref s, In(seq++, true, holdJump), w, 1f / 60f);
				if (s.Stance == Stance.Climbing) climbed = true;
			}
			return s;
		}

		[Fact]
		public void Jumping_At_A_High_Ledge_Pulls_The_Player_Up_Onto_It()
		{
			var (w, g) = Setup(2.4f);
			var s = Run(w, -0.5f, 160, true, out bool climbed);
			Assert.True(climbed, "le joueur doit s'accrocher au rebord");
			Assert.NotEqual(Stance.Climbing, s.Stance);
			Assert.True(s.OnGround);
			Assert.InRange(s.Pos.Y, g + 2.3f, g + 2.5f);        // sur le toit
			Assert.True(s.Pos.Z < -2f, $"il doit être sur le bloc, z = {s.Pos.Z}");
		}

		[Fact]
		public void A_Ledge_Out_Of_Reach_Is_Not_Climbed()
		{
			var (w, _) = Setup(5.5f);
			var s = Run(w, -0.5f, 240, true, out bool climbed);
			Assert.False(climbed);
			Assert.True(s.Pos.Z > -2f);
		}

		[Fact]
		public void Cannot_Climb_Up_Through_A_Floor_Slab_Overhead()
		{
			// le plancher d'un étage (dalle de 0,3 m à 2,7 m du sol) au-dessus de la tête : sauter dessous ne doit pas hisser à travers
			var d = WorldData.CreateFlat(300);
			float g = d.HeightAt(0, 0);
			d.Boxes.Add(new Aabb(new Vec3(-5, g + 2.7f, -8), new Vec3(5, g + 3.0f, 1f)));
			var w = new StaticWorld(d);
			var s = Run(w, 0f, 200, true, out bool climbed);
			Assert.False(climbed, "on ne se hisse pas à travers un plafond");
			Assert.True(s.Pos.Y < g + 2.0f, $"y = {s.Pos.Y - g}");
		}

		[Fact]
		public void Without_Holding_Jump_There_Is_No_Climb()
		{
			var (w, _) = Setup(2.4f);
			var s = Run(w, -0.5f, 120, false, out bool climbed);
			Assert.False(climbed);
		}

		[Fact]
		public void A_Low_Obstacle_Is_Just_Jumped_Not_Climbed()
		{
			var (w, g) = Setup(1.0f);
			var s = Run(w, -0.5f, 240, true, out bool climbed);
			Assert.False(climbed);
			Assert.True(s.Pos.Z < -2f, "le saut suffit pour monter dessus");
		}

		[Fact]
		public void Climb_State_Survives_The_Network()
		{
			var s = new PlayerState { Pos = new Vec3(1, 2, 3), Stance = Stance.Climbing, ClimbT = 0.4f, ClimbFrom = new Vec3(1, 2, 3), ClimbTo = new Vec3(1, 4.4f, 1), ClimbCooldown = 0.1f };
			var w = new BitWriter(); s.Write(w);
			var r = new BitReader(w.Buffer, 0, w.ByteLength);
			var b = PlayerState.Read(r);
			Assert.Equal(Stance.Climbing, b.Stance);
			Assert.Equal(0.4f, b.ClimbT); Assert.Equal(4.4f, b.ClimbTo.Y);
		}
	}
}
