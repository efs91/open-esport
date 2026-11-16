using System;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class SlideTests
	{
		private static StaticWorld Plane(float grade, bool floor = false)
		{
			var d = WorldData.CreateFlat(1000);
			for (int z = 0; z < d.Depth; z++)
				for (int x = 0; x < d.Width; x++)
					d.Heights[z * d.Width + x] = 100f + grade * (d.OriginZ + z * d.CellSize);
			if (floor) d.Boxes.Add(new Aabb(new Vec3(-100, 130, -100), new Vec3(100, 140, 100)));
			return new StaticWorld(d);
		}

		private static void Tick(ref PlayerState s, StaticWorld world, float dt = 1f / 60f)
		{
			var input = PlayerInput.Create(1, 1, 0, Buttons.Sprint | Buttons.Crouch, 0, 0, 0, 0, 0);
			PlayerMovement.Step(ref s, input, world, dt);
		}

		[Theory]
		[InlineData(0f)]
		[InlineData(0.25f)] // Forward is -Z: downhill.
		public void Start_Immediately_Increases_Actual_Velocity_And_Displacement(float grade)
		{
			var world = Plane(grade);
			var s = PlayerMovement.Spawn(Vec3.Zero, world);
			float sprint = Sim.BaseSpeed * Sim.SprintMultiplier;
			s.Vel = new Vec3(0, 0, -sprint);
			float before = s.Pos.Z;
			Tick(ref s, world);
			Assert.Equal(Stance.Sliding, s.Stance);
			Assert.True(-s.Vel.Z > sprint * 1.1f);
			Assert.True(before - s.Pos.Z > sprint / 60f);
		}

		[Theory]
		[InlineData(30)]
		[InlineData(60)]
		[InlineData(120)]
		public void Flat_Boost_Then_Slowdown_Is_Independent_Of_Tick_Rate(int hz)
		{
			var world = Plane(0);
			var s = PlayerMovement.Spawn(Vec3.Zero, world);
			float sprint = Sim.BaseSpeed * Sim.SprintMultiplier;
			s.Vel = new Vec3(0, 0, -sprint);
			Tick(ref s, world, 1f / hz);
			int ticks = (int)MathF.Ceiling(Sim.SlideBoostTime * hz);
			for (int i = 0; i < ticks; i++) Tick(ref s, world, 1f / hz);
			float expected = sprint * Sim.SlideStartMultiplier - Sim.SlideFlatDeceleration * (ticks / (float)hz - Sim.SlideBoostTime);
			Assert.InRange(s.SlideSpeed, expected - 0.001f, expected + 0.001f);
			float peak = s.SlideSpeed;
			Tick(ref s, world, 1f / hz);
			Assert.True(s.SlideSpeed < peak);
		}

		[Fact]
		public void Downhill_Accelerates_To_Cap_And_Uphill_Slows()
		{
			foreach (float grade in new[] { -0.25f, 0.25f })
			{
				var world = Plane(grade);
				var s = PlayerMovement.Spawn(Vec3.Zero, world);
				s.Stance = Stance.Sliding;
				s.SlideSpeed = Sim.BaseSpeed * Sim.SprintMultiplier;
				s.SlideDirZ = -1;
				s.SlideTime = Sim.SlideBoostTime;
				float before = s.SlideSpeed;
				Tick(ref s, world);
				Assert.True(grade > 0 ? s.SlideSpeed > before : s.SlideSpeed < before);
				if (grade < 0) continue;
				for (int i = 0; i < 1200; i++)
				{
					Tick(ref s, world);
					Assert.InRange(s.SlideSpeed, before, Sim.SlideMaxSpeed);
				}
				Assert.Equal(Sim.SlideMaxSpeed, s.SlideSpeed);
			}
		}

		[Fact]
		public void Flat_Roof_Ignores_Terrain_Slope_Underneath()
		{
			var world = Plane(-0.25f, true);
			var s = PlayerMovement.Spawn(new Vec3(0, 140, 0), world);
			float sprint = Sim.BaseSpeed * Sim.SprintMultiplier;
			s.Vel = new Vec3(0, 0, -sprint);
			Tick(ref s, world);
			for (int i = 0; i < 21; i++) Tick(ref s, world);
			Assert.InRange(s.SlideSpeed, sprint * Sim.SlideStartMultiplier - 0.001f, sprint * Sim.SlideStartMultiplier + 0.001f);
			Assert.Equal(140f, s.Pos.Y);
		}
	}
}
