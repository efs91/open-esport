using System;
using System.IO;

namespace OpenEsport.Core
{
	public enum BodyClip : byte { Idle = 0, Walk = 1, Run = 2, Slide = 3, Air = 4, CrouchIdle = 5, CrouchWalk = 6 }

	/// <summary>Morceau du corps touché (pour l'affichage) ; la zone de dégâts en découle.</summary>
	public enum BodyPart : byte { Head, Neck, Chest, Belly, Pelvis, UpperArm, Forearm, Hand, Thigh, Shin, Foot }

	/// <summary>
	/// Poses du personnage relevées dans le jeu (tools/hitbox/bake_body_poses.gd) : position des articulations pour chaque arme en main,
	/// chaque clip et chaque phase de pas, pour 2 sexes × 3 tailles. Le serveur s'en sert pour juger les tirs sur le corps animé,
	/// sans squelette ni animation à faire tourner.
	/// </summary>
	public static class BodyPoses
	{
		public const int Magic = 0x3242484F; // « OHB2 »
		public static readonly float[] Genders = { 0f, 1f };
		public static readonly float[] Heights = { -1f, 0f, 1f };
		/// <summary>Armes dont la prise change la pose des bras (dans l'ordre de la table ; avant elles : mains nues).</summary>
		public static readonly byte[] HoldWeapons = { ItemIds.Knife, ItemIds.C19, ItemIds.P2020, ItemIds.Smg45, ItemIds.Ak, ItemIds.Lmg63, ItemIds.Sawnoff, ItemIds.Sniper };
		public const int GaitFrames = 16;
		public static readonly (BodyClip clip, int frames)[] Layout = { (BodyClip.Idle, 1), (BodyClip.Walk, GaitFrames), (BodyClip.Run, GaitFrames), (BodyClip.Slide, 1), (BodyClip.Air, 1), (BodyClip.CrouchIdle, 1), (BodyClip.CrouchWalk, GaitFrames) };
		public static readonly string[] JointBones =
		{
			"Hips", "Spine", "Chest", "UpperChest", "Neck", "Head", "HeadTop",
			"LeftUpperArm", "LeftLowerArm", "LeftHand", "LeftMiddleDistal",
			"RightUpperArm", "RightLowerArm", "RightHand", "RightMiddleDistal",
			"LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes", "LeftToeTip",
			"RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes", "RightToeTip",
		};
		public static int Joints => JointBones.Length;
		public static int FramesPerHold { get { int n = 0; foreach (var l in Layout) n += l.frames; return n; } }

		/// <summary>Seuils des clips de jambes, les mêmes que l'animateur du client.</summary>
		public const float IdleThreshold = 0.4f;
		public static float RunAt => Sim.BaseSpeed * (1f + Sim.SprintMultiplier) * 0.5f;
		public static float WalkLength, WalkGroundSpeed, RunLength, RunGroundSpeed, CrouchLength, CrouchGroundSpeed;
		private static short[] _data;
		public static bool Loaded => _data != null;

		static BodyPoses()
		{
			using var stream = typeof(BodyPoses).Assembly.GetManifestResourceStream("OpenEsport.Core.body_poses.bin");
			if (stream == null) return;
			using var r = new BinaryReader(stream);
			if (r.ReadInt32() != Magic) return;
			WalkLength = r.ReadSingle(); WalkGroundSpeed = r.ReadSingle(); RunLength = r.ReadSingle(); RunGroundSpeed = r.ReadSingle();
			CrouchLength = r.ReadSingle(); CrouchGroundSpeed = r.ReadSingle();
			int count = Genders.Length * Heights.Length * (HoldWeapons.Length + 1) * FramesPerHold * Joints * 3;
			var data = new short[count];
			for (int i = 0; i < count; i++) data[i] = r.ReadInt16();
			_data = data;
		}

		public static int HoldIndex(byte weaponId)
		{
			for (int i = 0; i < HoldWeapons.Length; i++) if (HoldWeapons[i] == weaponId) return i + 1;
			return 0;
		}

		/// <summary>Clip des jambes, comme l'animateur du client.</summary>
		public static BodyClip ClipFor(Stance stance, bool onGround, float groundSpeed)
		{
			if (stance == Stance.Sliding) return BodyClip.Slide;
			if (!onGround) return BodyClip.Air;
			if (stance == Stance.Crouching) return groundSpeed < IdleThreshold ? BodyClip.CrouchIdle : BodyClip.CrouchWalk;
			if (groundSpeed < IdleThreshold) return BodyClip.Idle;
			return groundSpeed < RunAt ? BodyClip.Walk : BodyClip.Run;
		}

		/// <summary>Avance de la phase de pas (0..1) en une durée dt, au même rythme que le clip du client (vitesse de lecture calée sur la vitesse au sol).</summary>
		public static float AdvanceGait(float phase, BodyClip clip, float groundSpeed, float dt)
		{
			if (clip != BodyClip.Walk && clip != BodyClip.Run && clip != BodyClip.CrouchWalk) return phase;
			float length = clip == BodyClip.Walk ? WalkLength : clip == BodyClip.Run ? RunLength : CrouchLength;
			float reference = clip == BodyClip.Walk ? WalkGroundSpeed : clip == BodyClip.Run ? RunGroundSpeed : CrouchGroundSpeed;
			if (length <= 0f) return phase;
			float rate = (reference > .1f ? Math.Clamp(groundSpeed / reference, .6f, 1.8f) : 1f) / length;
			phase += rate * dt;
			return phase - MathF.Floor(phase);
		}

		/// <summary>Articulations (repère du joueur : pieds à l'origine, regard vers -Z) pour cette apparence, arme, clip et phase.</summary>
		public static void Pose(Vec3[] joints, float gender, float height, int hold, BodyClip clip, float phase)
		{
			int frame0 = 0, frames = 1;
			foreach (var (c, n) in Layout) { if (c == clip) { frames = n; break; } frame0 += n; }
			float fp = phase * frames;
			int fa = (int)MathF.Floor(fp) % frames, fb = (fa + 1) % frames;
			float ft = frames > 1 ? fp - MathF.Floor(fp) : 0f;
			float g = Math.Clamp(gender, 0f, 1f);
			float h = Math.Clamp(height, -1f, 1f);
			int h0 = h < 0f ? 0 : 1;
			float ht = h < 0f ? h + 1f : h;
			for (int j = 0; j < joints.Length; j++) joints[j] = default;
			Accumulate(joints, 0, h0, hold, frame0 + fa, (1 - g) * (1 - ht) * (1 - ft));
			Accumulate(joints, 0, h0, hold, frame0 + fb, (1 - g) * (1 - ht) * ft);
			Accumulate(joints, 0, h0 + 1, hold, frame0 + fa, (1 - g) * ht * (1 - ft));
			Accumulate(joints, 0, h0 + 1, hold, frame0 + fb, (1 - g) * ht * ft);
			Accumulate(joints, 1, h0, hold, frame0 + fa, g * (1 - ht) * (1 - ft));
			Accumulate(joints, 1, h0, hold, frame0 + fb, g * (1 - ht) * ft);
			Accumulate(joints, 1, h0 + 1, hold, frame0 + fa, g * ht * (1 - ft));
			Accumulate(joints, 1, h0 + 1, hold, frame0 + fb, g * ht * ft);
		}

		private static void Accumulate(Vec3[] joints, int gender, int height, int hold, int frame, float weight)
		{
			if (weight <= 0f || _data == null) return;
			int variant = gender * Heights.Length + height;
			int baseIndex = (((variant * (HoldWeapons.Length + 1)) + hold) * FramesPerHold + frame) * Joints * 3;
			for (int j = 0; j < joints.Length; j++)
			{
				int k = baseIndex + j * 3;
				joints[j] = new Vec3(joints[j].X + _data[k] * .001f * weight, joints[j].Y + _data[k + 1] * .001f * weight, joints[j].Z + _data[k + 2] * .001f * weight);
			}
		}
	}

	/// <summary>
	/// Corps touchable : une sphère pour la tête et des capsules pour le cou, le tronc, les bras, les mains, les jambes et les pieds,
	/// posées sur les articulations de <see cref="BodyPoses"/>. Coût : une sphère englobante, puis une vingtaine de tests de capsule
	/// seulement pour les tirs qui passent près du joueur.
	/// </summary>
	public static class BodyHitbox
	{
		public enum J { Hips, Spine, Chest, UpperChest, Neck, Head, HeadTop, LUA, LLA, LH, LHT, RUA, RLA, RH, RHT, LUL, LLL, LF, LT, LTT, RUL, RLL, RF, RT, RTT }

		public readonly struct Volume
		{
			public readonly int A, B;
			public readonly float Radius;
			public readonly BodyPart Part;
			/// <summary>Bouts de la capsule, en fraction du segment A→B (0 et 1 : sur les articulations).</summary>
			public readonly float FromA, ToB;
			public Volume(J a, J b, float radius, BodyPart part, float fromA = 0f, float toB = 1f) { A = (int)a; B = (int)b; Radius = radius; Part = part; FromA = fromA; ToB = toB; }
			public void Ends(Vec3[] joints, out Vec3 a, out Vec3 b)
			{
				Vec3 ja = joints[A], jb = joints[B];
				a = new Vec3(ja.X + (jb.X - ja.X) * FromA, ja.Y + (jb.Y - ja.Y) * FromA, ja.Z + (jb.Z - ja.Z) * FromA);
				b = new Vec3(ja.X + (jb.X - ja.X) * ToB, ja.Y + (jb.Y - ja.Y) * ToB, ja.Z + (jb.Z - ja.Z) * ToB);
			}
		}

		/// <summary>Capsules (A = B : sphère). Rayons pour un gabarit moyen ; la corpulence les élargit un peu.</summary>
		public static readonly Volume[] Volumes =
		{
			// crâne : de 6 à 12 cm au-dessus de l'articulation de la tête (base du crâne) ; avec le rayon, du menton au sommet
			new Volume(J.Head, J.HeadTop, .09f, BodyPart.Head, .3f, .6f),
			new Volume(J.Neck, J.Head, .06f, BodyPart.Neck),
			new Volume(J.Chest, J.UpperChest, .14f, BodyPart.Chest),
			new Volume(J.LUA, J.RUA, .07f, BodyPart.Chest),
			new Volume(J.Spine, J.Chest, .13f, BodyPart.Belly),
			new Volume(J.Hips, J.Spine, .13f, BodyPart.Pelvis),
			new Volume(J.LUL, J.RUL, .1f, BodyPart.Pelvis),
			new Volume(J.LUA, J.LLA, .055f, BodyPart.UpperArm),
			new Volume(J.RUA, J.RLA, .055f, BodyPart.UpperArm),
			new Volume(J.LLA, J.LH, .045f, BodyPart.Forearm),
			new Volume(J.RLA, J.RH, .045f, BodyPart.Forearm),
			new Volume(J.LH, J.LHT, .04f, BodyPart.Hand),
			new Volume(J.RH, J.RHT, .04f, BodyPart.Hand),
			new Volume(J.LUL, J.LLL, .085f, BodyPart.Thigh),
			new Volume(J.RUL, J.RLL, .085f, BodyPart.Thigh),
			new Volume(J.LLL, J.LF, .06f, BodyPart.Shin),
			new Volume(J.RLL, J.RF, .06f, BodyPart.Shin),
			new Volume(J.LF, J.LTT, .05f, BodyPart.Foot),
			new Volume(J.RF, J.RTT, .05f, BodyPart.Foot),
		};

		public static HitZone ZoneOf(BodyPart part) => part == BodyPart.Head ? HitZone.Head
			: part == BodyPart.Neck || part == BodyPart.Chest || part == BodyPart.Belly || part == BodyPart.Pelvis ? HitZone.Torso : HitZone.Legs;

		/// <summary>Ce dont dépend la pose d'un joueur à un instant (gardé dans l'historique pour la compensation de latence).</summary>
		public struct State
		{
			public float Yaw;
			public Stance Stance;
			public BodyClip Clip;
			public float Phase;
			public byte Weapon;
			public float Gender, Height, Bulk;
		}

		/// <summary>Morphologie lue dans les octets d'apparence (sexe, corpulence, taille).</summary>
		public static void ReadLook(byte[] look, ref State s)
		{
			if (look == null || look.Length < 5) { s.Gender = 0; s.Height = 0; s.Bulk = 0; return; }
			s.Gender = look[1] / 255f;
			s.Bulk = look[2] / 255f * 2f - 1f;
			s.Height = look[4] / 255f * 2f - 1f;
		}

		[ThreadStatic] private static Vec3[] _joints;

		/// <summary>Articulations en coordonnées du monde.</summary>
		public static Vec3[] Joints(Vec3 feet, in State s)
		{
			var joints = _joints ??= new Vec3[BodyPoses.Joints];
			BodyPoses.Pose(joints, s.Gender, s.Height, BodyPoses.HoldIndex(s.Weapon), s.Clip, s.Phase);
			float c = MathF.Cos(s.Yaw), sn = MathF.Sin(s.Yaw);
			for (int i = 0; i < joints.Length; i++)
			{
				var p = joints[i];
				joints[i] = new Vec3(feet.X + c * p.X + sn * p.Z, feet.Y + p.Y, feet.Z - sn * p.X + c * p.Z);
			}
			return joints;
		}

		public static float RadiusScale(in State s) => 1f + .12f * Math.Clamp(s.Bulk, -1f, 1f);

		/// <summary>Premier morceau du corps touché par le rayon (dir unitaire) avant maxDist.</summary>
		public static bool Ray(Vec3 feet, in State s, Vec3 origin, Vec3 dir, float maxDist, out float t, out BodyPart part)
		{
			t = 0; part = BodyPart.Chest;
			// sphère englobante (bras tendus, arme comprise) : la plupart des tirs s'arrêtent là
			var center = new Vec3(feet.X, feet.Y + .9f, feet.Z);
			if (!RaySphere(center, 1.25f, origin, dir, out float tc) || tc > maxDist) return false;
			var joints = Joints(feet, s);
			float scale = RadiusScale(s), best = maxDist;
			bool hit = false;
			foreach (var v in Volumes)
			{
				v.Ends(joints, out var a, out var b);
				if (RayCapsule(a, b, v.Radius * scale, origin, dir, out float tv) && tv < best)
				{
					best = tv; part = v.Part; hit = true;
				}
			}
			t = best;
			return hit;
		}

		private static bool RaySphere(Vec3 c, float r, Vec3 o, Vec3 d, out float t)
		{
			t = 0;
			float ox = o.X - c.X, oy = o.Y - c.Y, oz = o.Z - c.Z;
			float b = ox * d.X + oy * d.Y + oz * d.Z;
			float cc = ox * ox + oy * oy + oz * oz - r * r;
			float disc = b * b - cc;
			if (disc < 0f) return false;
			float sq = MathF.Sqrt(disc);
			float t0 = -b - sq, t1 = -b + sq;
			if (t1 < 0f) return false;
			t = MathF.Max(t0, 0f);
			return true;
		}

		/// <summary>Rayon contre capsule (segment ab, rayon r) : cylindre puis calottes.</summary>
		private static bool RayCapsule(Vec3 a, Vec3 b, float r, Vec3 o, Vec3 d, out float t)
		{
			t = float.MaxValue;
			float bax = b.X - a.X, bay = b.Y - a.Y, baz = b.Z - a.Z;
			float oax = o.X - a.X, oay = o.Y - a.Y, oaz = o.Z - a.Z;
			float baba = bax * bax + bay * bay + baz * baz;
			float bard = bax * d.X + bay * d.Y + baz * d.Z;
			float baoa = bax * oax + bay * oay + baz * oaz;
			float rdoa = d.X * oax + d.Y * oay + d.Z * oaz;
			float oaoa = oax * oax + oay * oay + oaz * oaz;
			bool found = false;
			if (baba > 1e-8f)
			{
				float qa = baba - bard * bard, qb = baba * rdoa - baoa * bard, qc = baba * oaoa - baoa * baoa - r * r * baba;
				float h = qb * qb - qa * qc;
				if (h >= 0f && qa > 1e-8f)
				{
					float tt = (-qb - MathF.Sqrt(h)) / qa;
					float y = baoa + tt * bard;
					if (y > 0f && y < baba && tt >= 0f) { t = tt; found = true; }
				}
			}
			if (RaySphere(a, r, o, d, out float ta) && ta < t) { t = ta; found = true; }
			if (baba > 1e-8f && RaySphere(b, r, o, d, out float tb) && tb < t) { t = tb; found = true; }
			return found;
		}
	}
}
