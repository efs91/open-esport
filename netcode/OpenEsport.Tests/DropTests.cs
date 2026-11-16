using System;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class DropTests
	{
		private static PlayerInput In(ushort seq, bool jump, float pitch = 0f, bool sprint = false, float fwd = 0f)
			=> PlayerInput.Create(seq, fwd, 0f, (jump ? Buttons.Jump : Buttons.None) | (sprint ? Buttons.Sprint : Buttons.None), 0, 0f, pitch, 0);

		private static PlayerState Flight(StaticWorld w)
		{
			// de x = -1000 vers +1000, à 600 m ; la carte plate fait 300 m : entrée à 5 s, sortie à 12 s (marge 100 m, vitesse 45 : valeurs de test)
			return PlayerMovement.BeginFlight(new Vec3(-1000, 600, 0), new Vec3(1000, 600, 0), 5f, 12f);
		}

		[Fact]
		public void The_Plane_Flies_On_Its_Own_And_Jumping_Is_Only_Allowed_Over_The_Map()
		{
			var w = new StaticWorld(WorldData.CreateFlat(300));
			var s = Flight(w);
			ushort seq = 1;
			for (int i = 0; i < 120; i++) PlayerMovement.Step(ref s, In(seq++, true), w, 1f / 60f);   // 2 s : sauter ne fait rien, on n'est pas sur la carte
			Assert.Equal(Stance.Plane, s.Stance);
			Assert.InRange(s.Pos.X, -1000f + 80f, -1000f + 100f);   // 45 m/s pendant 2 s
			for (int i = 0; i < 240; i++) PlayerMovement.Step(ref s, In(seq++, false), w, 1f / 60f);  // jusqu'à 6 s
			Assert.Equal(Stance.Plane, s.Stance);
			PlayerMovement.Step(ref s, In(seq++, true), w, 1f / 60f);
			Assert.Equal(Stance.Diving, s.Stance);
		}

		[Fact]
		public void Everyone_Is_Ejected_At_The_End()
		{
			var w = new StaticWorld(WorldData.CreateFlat(300));
			var s = Flight(w);
			ushort seq = 1;
			for (int i = 0; i < 60 * 12 + 2; i++) PlayerMovement.Step(ref s, In(seq++, false), w, 1f / 60f);
			Assert.Equal(Stance.Diving, s.Stance);
		}

		private static (float fallT, float dist) Fall(float pitch, bool forward)
		{
			var w = new StaticWorld(WorldData.CreateFlat(4000));
			var s = PlayerMovement.BeginFlight(new Vec3(0, 700, 0), new Vec3(1000, 700, 0), 0f, 100f);
			s.FlightT = 1f;
			ushort seq = 1;
			PlayerMovement.Step(ref s, In(seq++, true), w, 1f / 60f);       // on saute
			Vec3 start = s.Pos; float ground = w.HeightAt(0, 0);
			int ticks = 0;
			while (s.Stance == Stance.Diving && ticks < 60 * 120) { PlayerMovement.Step(ref s, In(seq++, false, pitch, false, forward ? 1f : 0f), w, 1f / 60f); ticks++; }
			Assert.NotEqual(Stance.Diving, s.Stance);
			Assert.True(s.OnGround);
			Assert.Equal(ground, s.Pos.Y, 2);
			float dx = s.Pos.X - start.X, dz = s.Pos.Z - start.Z;
			return (ticks / 60f, MathF.Sqrt(dx * dx + dz * dz));
		}

		[Fact]
		public void Diving_Is_Faster_And_Shorter_Than_Lying_Flat()
		{
			var flat = Fall(-1.4f, false);          // « avancer » relâché : on se redresse et on ralentit, quoi qu'on regarde
			var steep = Fall(-1.4f, true);          // « avancer » maintenu en regardant vers le bas : piqué
			var sprint = Fall(0f, true);
			Assert.True(steep.fallT < flat.fallT * 0.75f, $"piqué {steep.fallT:F1} s contre à plat {flat.fallT:F1} s");
			Assert.True(flat.dist > steep.dist * 1.4f, $"à plat on va plus loin : {flat.dist:F0} m contre {steep.dist:F0} m");
			Assert.True(sprint.fallT < flat.fallT, "avancer : on plonge");
		}

		[Fact]
		public void The_Dive_State_Survives_The_Network()
		{
			var s = PlayerMovement.BeginFlight(new Vec3(1, 2, 3), new Vec3(4, 5, 6), 7f, 8f);
			s.FlightT = 3.5f;
			var w = new BitWriter(128); s.Write(w);
			var b = PlayerState.Read(new BitReader(w.Buffer, 0, w.ByteLength));
			Assert.Equal(Stance.Plane, b.Stance); Assert.Equal(3.5f, b.FlightT); Assert.Equal(8f, b.PlaneExit); Assert.Equal(6f, b.PlaneTo.Z);
		}

		[Fact]
		public void A_Player_Joining_A_Dropship_Server_Starts_In_The_Plane_And_Can_Jump()
		{
			using var rig = new Rig(o => { o.Match.TeamSize = 1; o.Dropship = true; });
			var a = rig.Connect("Parachutiste");
			Assert.True(rig.WaitPlaying(a));
			Assert.Equal(Stance.Plane, a.C.Predicted.Stance);
			Assert.Equal(Stance.Plane, rig.Server.GetPlayer(a.C.PlayerId).State.Stance);
			rig.Run(7.9);
			Assert.True(a.C.Predicted.FlightT > a.C.Predicted.PlaneEnter, "l'avion doit arriver sur la carte");
			a.Buttons = Buttons.Jump;
			rig.Run(0.5);
			Assert.Equal(Stance.Diving, rig.Server.GetPlayer(a.C.PlayerId).State.Stance);
			a.Buttons = Buttons.None;
			rig.Run(0.5);
			Assert.Equal(Stance.Diving, a.C.Predicted.Stance);
			Assert.True(a.C.Predicted.Pos.Y < rig.Server.GetPlayer(a.C.PlayerId).State.PlaneFrom.Y);
		}
	}
}
