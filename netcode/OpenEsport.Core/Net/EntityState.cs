using System;

namespace OpenEsport.Core
{
	public enum EntityKind : byte { Player = 0, Loot = 1 }

	[Flags]
	public enum EntityFlags : byte
	{
		None = 0,
		OnGround = 1,
		Crouching = 2,
		Sliding = 4,
		Firing = 8,
		Reloading = 16,
		Dead = 32,
		Aiming = 64,
		/// <summary>Utilise un soin (animation de remplissage visible par les autres).</summary>
		Using = 128,
	}

	/// <summary>
	/// Quantification des positions pour le réseau. L'origine de la carte est communiquée à la connexion.
	/// X et Z : 20 bits au 1/64 m (jusqu'à 16 km), Y : 16 bits au 1/64 m (1 km de dénivelé).
	/// </summary>
	public readonly struct Quantizer
	{
		public readonly float MinX, MinY, MinZ;
		public Quantizer(float minX, float minY, float minZ) { MinX = minX; MinY = minY; MinZ = minZ; }

		public uint QX(float x) => (uint)Math.Clamp((int)MathF.Round((x - MinX) * 64f), 0, (1 << 20) - 1);
		public uint QZ(float z) => (uint)Math.Clamp((int)MathF.Round((z - MinZ) * 64f), 0, (1 << 20) - 1);
		public ushort QY(float y) => (ushort)Math.Clamp((int)MathF.Round((y - MinY) * 64f), 0, 65535);
		public float DX(uint q) => MinX + q / 64f;
		public float DZ(uint q) => MinZ + q / 64f;
		public float DY(ushort q) => MinY + q / 64f;
	}

	/// <summary>
	/// État répliqué d'une entité, déjà quantifié : deux états identiques sont égaux bit à bit,
	/// ce qui permet de ne renvoyer que ce qui a changé depuis le dernier acquittement du client.
	/// </summary>
	public struct EntityState : IEquatable<EntityState>
	{
		public ushort Id;
		public EntityKind Kind;
		public uint X, Z;
		public ushort Y;
		// Joueur
		public ushort Yaw;     // 12 bits
		public byte Pitch;     // 8 bits
		public short VelX, VelZ; // 10 bits signés, au 1/8 m/s
		public byte Flags;     // 8 bits
		public byte Health;    // 7 bits
		public byte Shield;    // 7 bits : points de bouclier actuels
		public byte ShieldTier; // 3 bits : niveau du bouclier équipé + 1 (0 = aucun)
		public byte Zip;       // 3 bits : 1 = tyrolienne, 2 = escalade, 3 = dans l'avion, 4 = chute libre, 5 = à terre
		public byte Weapon;    // 8 bits : identifiant de l'arme en main
		public byte Team;      // 7 bits
		// Butin : Weapon = identifiant de l'objet, Count = quantité
		public ushort Count;

		public static ushort QuantizeYaw12(float yawRad)
		{
			float t = yawRad / (MathF.PI * 2f);
			t -= MathF.Floor(t);
			return (ushort)((int)MathF.Round(t * 4096f) & 0xFFF);
		}

		public static float DequantizeYaw12(ushort q) => q * (MathF.PI * 2f / 4096f);

		public static byte QuantizePitch8(float pitchRad)
		{
			float p = Math.Clamp(pitchRad, -MathF.PI / 2f, MathF.PI / 2f);
			return (byte)Math.Clamp((int)MathF.Round((p / MathF.PI + 0.5f) * 255f), 0, 255);
		}

		public static float DequantizePitch8(byte q) => (q / 255f - 0.5f) * MathF.PI;

		public static short QuantizeVel(float v) => (short)Math.Clamp((int)MathF.Round(v * 8f), -511, 511);
		public static float DequantizeVel(short q) => q / 8f;

		public bool Equals(EntityState o)
		{
			return Id == o.Id && Kind == o.Kind && X == o.X && Z == o.Z && Y == o.Y && Yaw == o.Yaw && Pitch == o.Pitch
				&& VelX == o.VelX && VelZ == o.VelZ && Flags == o.Flags && Health == o.Health && Shield == o.Shield && ShieldTier == o.ShieldTier && Zip == o.Zip && Weapon == o.Weapon
				&& Team == o.Team && Count == o.Count;
		}

		public override bool Equals(object obj) => obj is EntityState e && Equals(e);
		public override int GetHashCode() => HashCode.Combine(Id, X, Z, Y, Yaw, Flags);

		/// <summary>Taille maximale en octets d'une entité sur le réseau (pour respecter le budget d'un paquet).</summary>
		public const int MaxBytesPlayer = 21;
		public const int MaxBytesLoot = 11;

		public void Write(BitWriter w)
		{
			w.WriteUInt16(Id);
			w.WriteBits((uint)Kind, 1);
			w.WriteBits(X, 20);
			w.WriteBits(Z, 20);
			w.WriteBits(Y, 16);
			if (Kind == EntityKind.Player)
			{
				w.WriteBits(Yaw, 12);
				w.WriteBits(Pitch, 8);
				w.WriteBits((uint)(VelX & 0x3FF), 10);
				w.WriteBits((uint)(VelZ & 0x3FF), 10);
				w.WriteBits(Flags, 8);
				w.WriteBits(Health, 7);
				w.WriteBits(Shield, 7);
				w.WriteBits(ShieldTier, 3);
				w.WriteBits(Zip, 3);
				w.WriteBits(Weapon, 8);
				w.WriteBits(Team, 7);
			}
			else
			{
				w.WriteBits(Weapon, 8);
				w.WriteBits(Count, 16);
			}
		}

		public static EntityState Read(BitReader r)
		{
			var e = new EntityState();
			e.Id = r.ReadUInt16();
			e.Kind = (EntityKind)r.ReadBits(1);
			e.X = r.ReadBits(20);
			e.Z = r.ReadBits(20);
			e.Y = (ushort)r.ReadBits(16);
			if (e.Kind == EntityKind.Player)
			{
				e.Yaw = (ushort)r.ReadBits(12);
				e.Pitch = (byte)r.ReadBits(8);
				e.VelX = SignExtend10(r.ReadBits(10));
				e.VelZ = SignExtend10(r.ReadBits(10));
				e.Flags = (byte)r.ReadBits(8);
				e.Health = (byte)r.ReadBits(7);
				e.Shield = (byte)r.ReadBits(7);
				e.ShieldTier = (byte)r.ReadBits(3);
				e.Zip = (byte)r.ReadBits(3);
				e.Weapon = (byte)r.ReadBits(8);
				e.Team = (byte)r.ReadBits(7);
			}
			else
			{
				e.Weapon = (byte)r.ReadBits(8);
				e.Count = (ushort)r.ReadBits(16);
			}
			return e;
		}

		private static short SignExtend10(uint v) => (short)((v & 0x200) != 0 ? (int)v - 1024 : (int)v);
	}

	/// <summary>Arithmétique sur des numéros de séquence de 16 bits qui bouclent.</summary>
	public static class Seq
	{
		/// <summary>Vrai si a est plus récent que b.</summary>
		public static bool Newer(ushort a, ushort b) => a != b && (ushort)(a - b) < 32768;
	}
}
