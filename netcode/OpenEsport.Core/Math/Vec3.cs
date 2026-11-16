using System;

namespace OpenEsport.Core
{
	/// <summary>Vecteur 3D en flottants simples. Opérations scalaires uniquement : le résultat est identique sur le client et le serveur.</summary>
	public struct Vec3 : IEquatable<Vec3>
	{
		public float X, Y, Z;

		public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

		public static readonly Vec3 Zero = new Vec3(0, 0, 0);
		public static readonly Vec3 Up = new Vec3(0, 1, 0);

		public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
		public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
		public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
		public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
		public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);
		public static bool operator ==(Vec3 a, Vec3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
		public static bool operator !=(Vec3 a, Vec3 b) => !(a == b);

		public float Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
		public Vec3 Cross(Vec3 o) => new Vec3(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
		public float LengthSquared() => X * X + Y * Y + Z * Z;
		public float Length() => MathF.Sqrt(LengthSquared());

		public Vec3 Normalized()
		{
			float l = Length();
			return l > 1e-8f ? this / l : Zero;
		}

		public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;
		public static float Distance(Vec3 a, Vec3 b) => (a - b).Length();
		public static float DistanceSquared(Vec3 a, Vec3 b) => (a - b).LengthSquared();

		public bool Equals(Vec3 other) => this == other;
		public override bool Equals(object obj) => obj is Vec3 v && this == v;
		public override int GetHashCode() => HashCode.Combine(X, Y, Z);
		public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
	}
}
