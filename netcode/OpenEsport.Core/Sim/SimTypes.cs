using System;

namespace OpenEsport.Core
{
	[Flags]
	public enum Buttons : ushort
	{
		None = 0,
		Jump = 1,
		Crouch = 2,
		Sprint = 4,
		Fire = 8,
		Aim = 16,
		Reload = 32,
		Interact = 64,
		/// <summary>Chute en équipe : le chef de saut passe le commandement au joueur suivant (touche TAB).</summary>
		PassLead = 128,
		/// <summary>Bits 8-9 : changement d'arme (0 = aucun, 1 = arme 1, 2 = arme 2, 3 = mains nues).</summary>
		SlotMask = 0x0300,
	}

	public enum Stance : byte { Standing = 0, Crouching = 1, Sliding = 2, Zipping = 3, Climbing = 4, Plane = 5, Diving = 6, Downed = 7 }

	/// <summary>
	/// Constantes de déplacement, reprises du contrôleur de personnage actuel pour que le ressenti reste identique.
	/// </summary>
	public static class Sim
	{
		// Réglables : voir GameBalance (fichier balance.json du serveur)
		public static float BaseSpeed => GameBalance.BaseSpeed;
		public static float SprintMultiplier => GameBalance.SprintMultiplier;
		public static float CrouchMultiplier => GameBalance.CrouchMultiplier;
		public static float Acceleration => GameBalance.Acceleration;
		public static float Deceleration => GameBalance.Deceleration;
		public static float Gravity => GameBalance.Gravity;
		public static float JumpSpeed => GameBalance.JumpSpeed;

		public static float SlideStartMultiplier => GameBalance.SlideStartMultiplier;
		public static float SlideMaxSpeed => GameBalance.BaseSpeed * GameBalance.SprintMultiplier * GameBalance.SlideMaxMultiplier;
		public static float SlideCooldown => GameBalance.SlideCooldown;
		public static float SlideFlatDeceleration => GameBalance.SlideFlatDeceleration;
		public static float SlideDownAcceleration => GameBalance.SlideDownAcceleration;
		public static float SlideUpDeceleration => GameBalance.SlideUpDeceleration;

		/// <summary>Durée (s) de la poussée du début d'une glissade, pendant laquelle la vitesse augmente.</summary>
		public const float SlideBoostTime = 0.35f;
		public const float Radius = 0.4f;
		public const float HeightStanding = 1.8f, HeightCrouching = 1.2f, HeightSliding = 0.9f;
		public const float EyeStanding = 1.6f, EyeCrouching = 1.1f, EyeSliding = 0.8f;
		public const float StepHeight = 0.5f;
		public const float SnapDistance = 0.5f;
		public const float MaxSlopeTan = 1.0f; // 45 degrés

		public static float HeightFor(Stance s) => s == Stance.Crouching ? HeightCrouching : (s == Stance.Sliding ? HeightSliding : (s == Stance.Downed ? HeightDowned : HeightStanding));
		public static float EyeFor(Stance s) => s == Stance.Crouching ? EyeCrouching : (s == Stance.Sliding ? EyeSliding : (s == Stance.Downed ? EyeDowned : EyeStanding));

		// escalade : on saute vers un rebord (toit, caisse, mur) et on se hisse dessus
		public const float ClimbReach = 2.0f;        // les mains tendues atteignent cette hauteur au-dessus des pieds
		public const float ClimbMinRise = 1.3f;      // en dessous (au-dessus des pieds), un simple saut suffit
		public const float ClimbMaxRise = 2.3f;      // rebord le plus haut saisissable : bras tendus + un peu d'élan
		public const float ClimbTime = 1.6f;         // durée du hissage (s)
		public const float ClimbRisePart = 0.65f;    // part du temps passée à monter (le reste : on passe sur le rebord)

		// arrivée sur la carte : l'avion traverse la carte, on saute quand on veut, puis chute libre et parachute
		public const float PlaneSpeed = 45f;           // m/s
		public const float PlaneMargin = 330f;         // l'avion arrive d'aussi loin hors de la carte (≈ 7 s : le temps de charger)
		public const float PlaneHeightAboveMap = 420f; // altitude de l'avion au-dessus du point le plus haut
		public const float DiveFallMin = 16f, DiveFallMax = 62f;     // vitesse de chute (m/s) : à plat ventre / en piqué
		public const float DiveGlideMax = 32f, DiveGlideMin = 5f;    // vitesse horizontale : à plat ventre / en piqué
		public const float DiveSprintBoost = 1.18f;
		public const float DiveAccelH = 2.6f, DiveAccelV = 9f;
		// pas de parachute (comme dans Apex) : on se pose à la vitesse de la chute ; sous FlareHeight la chute se freine un peu pour un atterrissage en douceur
		public const float FlareHeight = 45f, FlareFall = 20f;

		// à terre (équipe de plus d'un joueur) : on rampe, un coéquipier peut nous relever
		public const float HeightDowned = 0.7f, EyeDowned = 0.45f, DownedSpeed = 1.8f;
		public const float ReviveRange = 2.4f;

		// tyrolienne
		public const float ZipHoldHeight = 2.25f;    // le câble est à cette hauteur au-dessus des pieds : le joueur tient la poignée 30 cm plus bas
		public const float ZipSpeed = 20f, ZipStartSpeed = 10f, ZipAcceleration = 18f;
		public const float ZipGrabHorizontal = 1.4f, ZipGrabVertical = 1.0f;
		public const float ZipLaunchTime = 0.85f;
	}

	/// <summary>
	/// Une commande du joueur pour un tick. Le yaw et le pitch sont arrondis au format du réseau dès la création,
	/// pour que le client simule exactement ce que le serveur recevra.
	/// </summary>
	public struct PlayerInput
	{
		public ushort Seq;
		public sbyte Forward, Strafe;
		public ushort ButtonBits;
		public ushort YawQ, PitchQ;
		/// <summary>Tick serveur affiché au joueur au moment du tir (pour la compensation de latence).</summary>
		public uint ViewTick;
		/// <summary>Objet au sol que le joueur voit sous son viseur quand il interagit (identifiant d'entité). 0 = le serveur choisit ; <see cref="NoTarget"/> = rien sous le viseur.</summary>
		public ushort Target;
		public const ushort NoTarget = 0xFFFF;

		public Buttons Buttons => (Buttons)ButtonBits;
		public float Yaw => YawQ * (MathF.PI * 2f / 65536f);
		public float Pitch => (PitchQ / 65535f - 0.5f) * MathF.PI;
		public int Slot => (ButtonBits & (ushort)Core.Buttons.SlotMask) >> 8;

		public bool Has(Buttons b) => (ButtonBits & (ushort)b) != 0;

		public static ushort QuantizeYaw(float yawRad)
		{
			float t = yawRad / (MathF.PI * 2f);
			t -= MathF.Floor(t);
			return (ushort)((int)MathF.Round(t * 65536f) & 0xFFFF);
		}

		public static ushort QuantizePitch(float pitchRad)
		{
			float p = Math.Clamp(pitchRad, -MathF.PI / 2f, MathF.PI / 2f);
			return (ushort)Math.Clamp((int)MathF.Round((p / MathF.PI + 0.5f) * 65535f), 0, 65535);
		}

		public static PlayerInput Create(ushort seq, float forward, float strafe, Buttons buttons, int slot, float yawRad, float pitchRad, uint viewTick, ushort target = 0)
		{
			return new PlayerInput
			{
				Seq = seq,
				Forward = (sbyte)Math.Clamp((int)MathF.Round(forward * 127f), -127, 127),
				Strafe = (sbyte)Math.Clamp((int)MathF.Round(strafe * 127f), -127, 127),
				ButtonBits = (ushort)(((ushort)buttons & ~(ushort)Core.Buttons.SlotMask) | ((slot & 3) << 8)),
				YawQ = QuantizeYaw(yawRad),
				PitchQ = QuantizePitch(pitchRad),
				ViewTick = viewTick,
				Target = target
			};
		}

		public void Write(BitWriter w)
		{
			w.WriteUInt16(Seq);
			w.WriteSByte(Forward);
			w.WriteSByte(Strafe);
			w.WriteUInt16(ButtonBits);
			w.WriteUInt16(YawQ);
			w.WriteUInt16(PitchQ);
			w.WriteUInt32(ViewTick);
			w.WriteUInt16(Target);
		}

		public static PlayerInput Read(BitReader r)
		{
			return new PlayerInput
			{
				Seq = r.ReadUInt16(),
				Forward = r.ReadSByte(),
				Strafe = r.ReadSByte(),
				ButtonBits = r.ReadUInt16(),
				YawQ = r.ReadUInt16(),
				PitchQ = r.ReadUInt16(),
				ViewTick = r.ReadUInt32(),
				Target = r.ReadUInt16()
			};
		}
	}

	/// <summary>État de simulation d'un joueur : tout ce qu'il faut pour rejouer le mouvement à l'identique.</summary>
	public struct PlayerState
	{
		public Vec3 Pos, Vel;
		public bool OnGround;
		public Stance Stance;
		public float SlideSpeed, SlideDirX, SlideDirZ, SlideCooldown, SlideTime;
		public ushort PrevButtons;
		/// <summary>Câble tenu (indice + 1, 0 = aucun), position le long du câble, vitesse, sens (+1 vers la fin, −1 vers le début), délai avant de pouvoir reprendre un câble.</summary>
		public ushort Zip;
		public float ZipDist, ZipSpeed, ZipCooldown;
		public sbyte ZipDir;
		/// <summary>Escalade en cours (posture Climbing) : temps écoulé, départ et arrivée ; délai avant de pouvoir se hisser de nouveau.</summary>
		public float ClimbT, ClimbCooldown;
		public Vec3 ClimbFrom, ClimbTo;
		/// <summary>Avion (posture Plane) : temps de vol, départ et arrivée, instants d'entrée et de sortie de la carte (s).</summary>
		public float FlightT, PlaneEnter, PlaneExit;
		public Vec3 PlaneFrom, PlaneTo;
		/// <summary>Après un tremplin : câble visé (indice + 1) ; le joueur s'y accroche tout seul en arrivant.</summary>
		public ushort AssistLine;

		public void Write(BitWriter w)
		{
			w.WriteVec3(Pos);
			w.WriteVec3(Vel);
			w.WriteBool(OnGround);
			w.WriteBits((uint)Stance, 3);
			w.WriteFloat(SlideSpeed);
			w.WriteFloat(SlideDirX);
			w.WriteFloat(SlideDirZ);
			w.WriteFloat(SlideCooldown);
			w.WriteFloat(SlideTime);
			w.WriteUInt16(PrevButtons);
			w.WriteBits(Zip, 12);
			if (Zip != 0) { w.WriteFloat(ZipDist); w.WriteFloat(ZipSpeed); w.WriteBool(ZipDir > 0); }
			w.WriteFloat(ZipCooldown);
			w.WriteBits(AssistLine, 12);
			if (Stance == Stance.Climbing) { w.WriteFloat(ClimbT); w.WriteVec3(ClimbFrom); w.WriteVec3(ClimbTo); }
			if (Stance == Stance.Plane) { w.WriteFloat(FlightT); w.WriteFloat(PlaneEnter); w.WriteFloat(PlaneExit); w.WriteVec3(PlaneFrom); w.WriteVec3(PlaneTo); }
			w.WriteFloat(ClimbCooldown);
		}

		public static PlayerState Read(BitReader r)
		{
			var s = new PlayerState();
			s.Pos = r.ReadVec3();
			s.Vel = r.ReadVec3();
			s.OnGround = r.ReadBool();
			s.Stance = (Stance)r.ReadBits(3);
			s.SlideSpeed = r.ReadFloat();
			s.SlideDirX = r.ReadFloat();
			s.SlideDirZ = r.ReadFloat();
			s.SlideCooldown = r.ReadFloat();
			s.SlideTime = r.ReadFloat();
			s.PrevButtons = r.ReadUInt16();
			s.Zip = (ushort)r.ReadBits(12);
			if (s.Zip != 0) { s.ZipDist = r.ReadFloat(); s.ZipSpeed = r.ReadFloat(); s.ZipDir = (sbyte)(r.ReadBool() ? 1 : -1); }
			s.ZipCooldown = r.ReadFloat();
			s.AssistLine = (ushort)r.ReadBits(12);
			if (s.Stance == Stance.Climbing) { s.ClimbT = r.ReadFloat(); s.ClimbFrom = r.ReadVec3(); s.ClimbTo = r.ReadVec3(); }
			if (s.Stance == Stance.Plane) { s.FlightT = r.ReadFloat(); s.PlaneEnter = r.ReadFloat(); s.PlaneExit = r.ReadFloat(); s.PlaneFrom = r.ReadVec3(); s.PlaneTo = r.ReadVec3(); }
			s.ClimbCooldown = r.ReadFloat();
			return s;
		}
	}
}
