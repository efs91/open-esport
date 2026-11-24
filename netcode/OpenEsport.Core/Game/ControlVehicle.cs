using System;

namespace OpenEsport.Core
{
    public enum ShotAppearance : byte { Standard, WalkerMachineGun, WalkerCannon }

    public sealed class ControlVehicle
    {
        public const ushort Empty = ushort.MaxValue;
        public const float HoverScale = 2.7f, WalkerScale = 3.93f;   // mécha de 1,90 unité à la hauteur de la coque (7,48 m)
        public bool Hover;
        public byte Team;
        public Vec3 Pos, Velocity;
        public float Yaw, Pitch, Roll;
        public int Integrity;
        public int FullIntegrity => Hover ? 180 : 500;
        public Vec3 Bay;
        public float BayYaw, RepairTime;
        public Aabb Hull => new Aabb(Pos + new Vec3(-Radius, 0, -Radius), Pos + new Vec3(Radius, Height, Radius));
        public static void Separate(ControlVehicle[] vehicles, ref PlayerState player)
        {
            if (vehicles == null) return;
            foreach (var v in vehicles)
                if (v.Integrity > 0) StaticWorld.PushOut(ref player.Pos, v.Hull, Sim.Radius, Sim.HeightFor(player.Stance), Sim.StepHeight);
        }
        public ushort[] Seats;
        public float Radius => Hover ? 2.56f : 2.45f;
        public float Height => Hover ? 1.35f : 7.48f;
        public Vec3 Front => new Vec3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        public Vec3 Right => new Vec3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

        public ControlVehicle(bool hover)
        {
            Hover = hover;
            Integrity = FullIntegrity;
            Seats = new ushort[hover ? 1 : 5];
            Array.Fill(Seats, Empty);
        }

        public Vec3 Eye(int seat)
        {
            if (Hover) return Pos + Vec3.Up * (.93f + Sim.EyeStanding);
            if (seat < 2) return Pos + Vec3.Up * (seat == 0 ? 5.65f : 6.25f) + Front * 1.65f + Right * (seat == 0 ? -.65f : .65f);
            return Pos + Vec3.Up * 5f - Front * (seat == 4 ? 1.85f : .85f) + Right * (seat == 2 ? -1.6f : seat == 3 ? 1.6f : 0);
        }

        public float SeatHeading(int seat) => Yaw + (seat == 2 ? MathF.PI / 2 : seat == 3 ? -MathF.PI / 2 : seat == 4 ? MathF.PI : 0);
        public static float Angle(float radians) => MathF.Atan2(MathF.Sin(radians), MathF.Cos(radians));
        public void Aim(int seat, float yaw, float pitch, out float heading, out float elevation)
        {
            float axis = SeatHeading(seat);
            float arc = seat >= 2 ? .49f : seat == 0 && !Hover ? .75f : MathF.PI;
            heading = axis + Math.Clamp(Angle(yaw - axis), -arc, arc);
            elevation = Math.Clamp(pitch, seat >= 2 ? -.17f : -.95f, seat >= 2 ? .17f : 1.15f);
        }

        public bool CanBoard(Vec3 feet) => Integrity > 0 && MathF.Abs(feet.Y - Pos.Y) < 3 && new Vec3(feet.X - Pos.X, 0, feet.Z - Pos.Z).Length() <= Radius + 2.2f;

        public void Write(BitWriter w)
        {
            w.WriteBool(Hover); w.WriteByte(Team); w.WriteUInt16((ushort)Integrity); w.WriteVec3(Pos); w.WriteVec3(Velocity);
            w.WriteFloat(Yaw); w.WriteFloat(Pitch); w.WriteFloat(Roll);
            foreach (ushort player in Seats) w.WriteUInt16(player);
        }
        public static ControlVehicle Read(BitReader r)
        {
            var v = new ControlVehicle(r.ReadBool()) { Team = r.ReadByte(), Integrity = r.ReadUInt16(), Pos = r.ReadVec3(), Velocity = r.ReadVec3(), Yaw = r.ReadFloat(), Pitch = r.ReadFloat(), Roll = r.ReadFloat() };
            for (int i = 0; i < v.Seats.Length; i++) v.Seats[i] = r.ReadUInt16();
            return v;
        }
    }
}
