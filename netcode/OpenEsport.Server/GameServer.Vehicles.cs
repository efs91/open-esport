using System;
using System.Linq;
using OpenEsport.Core;

namespace OpenEsport.Server
{
    public sealed partial class GameServer
    {
        private ControlVehicle[] _vehicles = Array.Empty<ControlVehicle>();

        private void StartVehicles()
        {
            _vehicles = _data.PointsOf(MapPointKind.VehicleBay).Where(p => p.Team < 2 && p.Index < 2).Take(4)
                .Select(p => new ControlVehicle(p.Index == 0) { Team = p.Team, Pos = p.Pos + Vec3.Up * (p.Index == 0 ? .7f : 0), Yaw = p.Yaw, BayYaw = p.Yaw, Bay = p.Pos + Vec3.Up * (p.Index == 0 ? .7f : 0) }).ToArray();
        }

        private void ReleaseSeat(ServerPlayer p)
        {
            if (p.Ride == null) return;
            p.Ride.Seats[p.Seat] = ControlVehicle.Empty;
            p.Ride = null;
            p.Seat = -1;
        }

        private void ToggleVehicle(ServerPlayer p, int request)
        {
            if (_opt.Mode != GameMode.Control || Phase != MatchPhase.Playing || !p.Alive) return;
            if (p.Ride != null)
            {
                var v = p.Ride;
                if (request > 0)
                {
                    int seat = request - 1;
                    if (seat >= v.Seats.Length || v.Seats[seat] != ControlVehicle.Empty) return;
                    v.Seats[p.Seat] = ControlVehicle.Empty;
                    PlaceInSeat(p, v, seat);
                    return;
                }
                foreach (var dir in new[] { v.Right, -v.Right, -v.Front, v.Front })
                {
                    var exit = v.Pos + dir * (v.Radius + 1.15f);
                    _world.ClampToBounds(ref exit);
                    exit.Y = MathF.Max(v.Pos.Y, _world.GroundAt(exit.X, exit.Z, v.Pos.Y, .5f));
                    var clear = _world.ResolveObstacles(exit, Sim.Radius, Sim.HeightStanding, .1f);
                    if (Vec3.DistanceSquared(clear, exit) > .01f || _world.Raycast(exit + Vec3.Up * .15f, Vec3.Up, 1.6f, out _)) continue;
                    ReleaseSeat(p);
                    p.State = new PlayerState { Pos = exit };
                    return;
                }
                return;
            }
            if (request != 0) return;
            foreach (var v in _vehicles)
            {
                if (v.Team != p.Team || !v.CanBoard(p.State.Pos)) continue;
                var target = v.Pos + Vec3.Up;
                var origin = p.State.Pos + Vec3.Up;
                float distance = Vec3.Distance(target, origin);
                if (_world.Raycast(origin, (target - origin).Normalized(), distance, out float wall) && wall < distance - .3f) continue;
                int seat = Array.IndexOf(v.Seats, ControlVehicle.Empty);
                if (seat < 0) continue;
                PlaceInSeat(p, v, seat);
                return;
            }
        }

        private void PlaceInSeat(ServerPlayer p, ControlVehicle v, int seat)
        {
            v.Seats[seat] = p.Id;
            p.Ride = v;
            p.Seat = seat;
            p.UseId = 0;
            p.LastInput = PlayerInput.Create(0, 0, 0, Buttons.None, 0, v.SeatHeading(seat), 0, _tick);
            p.State = new PlayerState { Pos = v.Eye(seat) - Vec3.Up * Sim.EyeStanding };
        }

        private void VehicleInput(ServerPlayer p, PlayerInput input)
        {
            var v = p.Ride;
            v.Aim(p.Seat, input.Yaw, input.Pitch, out float yaw, out float pitch);
            p.LastInput = input;
            p.RideInputTick = _tick;
            p.Yaw = yaw;
            p.Pitch = pitch;
            input.YawQ = PlayerInput.QuantizeYaw(yaw);
            input.PitchQ = PlayerInput.QuantizePitch(pitch);
            if (p.Seat >= 2)
            {
                input.ButtonBits &= (ushort)(Buttons.Fire | Buttons.Reload | Buttons.SlotMask | Buttons.Aim);
                if (p.Inventory.Active == null) input.ButtonBits &= unchecked((ushort)~(ushort)Buttons.Fire);
                ApplyActions(p, input);
                return;
            }
            if (!input.Has(Buttons.Fire) || p.ShotCooldown > 0) return;
            bool cannon = !v.Hover && p.Seat == 1;
            byte weapon = cannon ? ItemIds.Sniper : ItemIds.Lmg63;
            float cp = MathF.Cos(pitch);
            var dir = new Vec3(-MathF.Sin(yaw) * cp, MathF.Sin(pitch), -MathF.Cos(yaw) * cp);
            SpawnProjectile(p, weapon, v.Hover ? 19 : cannon ? 140 : 32, WeaponDefs.Get(weapon), v.Eye(p.Seat), dir, _tick, cannon);
            p.ShotCooldown = cannon ? 2.1f : .115f;
            p.FireFlagTicks = 4;
            TotalShots++;
        }

        private void HitVehicle(ControlVehicle v, ServerPlayer attacker, float damage)
        {
            if (v.Integrity <= 0 || (v.Team == attacker.Team && !_m.FriendlyFire)) return;
            v.Integrity = Math.Max(0, v.Integrity - (int)MathF.Ceiling(damage));
            if (v.Integrity > 0) return;
            v.RepairTime = 14;
            v.Velocity = Vec3.Zero;
            BroadcastExplosion(GrenadeDefs.Get(ItemIds.GrenadeFrag).Kind, v.Pos + Vec3.Up);
            for (int i = 0; i < v.Seats.Length; i++)
            {
                if (v.Seats[i] == ControlVehicle.Empty) continue;
                var p = GetPlayer(v.Seats[i]);
                if (p != null) { ReleaseSeat(p); ApplyDamage(attacker, p, 65, HitZone.Torso, ItemIds.GrenadeFrag); }
            }
        }

        private void TickVehicles(float dt)
        {
            foreach (var v in _vehicles)
            {
                if (v.Integrity == 0)
                {
                    v.RepairTime -= dt;
                    if (v.RepairTime <= 0) { v.Pos = v.Bay; v.Yaw = v.BayYaw; v.Pitch = v.Roll = 0; v.Integrity = v.FullIntegrity; }
                    continue;
                }
                var pilot = v.Seats[0] == ControlVehicle.Empty ? null : GetPlayer(v.Seats[0]);
                bool active = pilot != null && pilot.Alive && _tick - pilot.RideInputTick < _m.TickRate / 3;
                var input = active ? pilot.LastInput : default;
                if (active)
                    v.Yaw += Math.Clamp(ControlVehicle.Angle(input.Yaw - v.Yaw), -2f * dt, 2f * dt);
                var desired = v.Front * (input.Forward / 127f) + v.Right * (input.Strafe / 127f);
                if (desired.LengthSquared() > 1) desired = desired.Normalized();
                desired *= v.Hover ? 23 : 11;
                if (v.Hover) desired.Y = pilot == null ? -5 : ((input.Has(Buttons.Jump) ? 1 : 0) - (input.Has(Buttons.Crouch) ? 1 : 0)) * 10;
                var velocity = Vec3.Lerp(v.Velocity, desired, MathF.Min(1, dt * 5));
                var next = v.Pos + velocity * dt;
                next.X = Math.Clamp(next.X, _data.MinX + v.Radius, _data.MaxX - v.Radius);
                next.Z = Math.Clamp(next.Z, _data.MinZ + v.Radius, _data.MaxZ - v.Radius);
                float floor = _world.GroundAt(next.X, next.Z, v.Pos.Y, .55f);
                next.Y = v.Hover ? Math.Clamp(next.Y, floor + .7f, MathF.Max(floor + .7f, _data.MaxHeight + 18)) : floor;
                bool clear = Vec3.DistanceSquared(next, _world.ResolveObstacles(next, v.Radius, v.Height, .55f)) < .001f;
                if (!v.Hover && MathF.Abs(next.Y - v.Pos.Y) > 1) clear = false;
                if (clear && next.Y > v.Pos.Y && _world.Raycast(v.Pos + Vec3.Up * (v.Height - .05f), Vec3.Up, next.Y - v.Pos.Y + .05f, out _)) clear = false;
                foreach (var other in _vehicles)
                {
                    if (other.Integrity == 0 || v == other || next.Y >= other.Pos.Y + other.Height || other.Pos.Y >= next.Y + v.Height) continue;
                    float dx = next.X - other.Pos.X, dz = next.Z - other.Pos.Z;
                    if (dx * dx + dz * dz < MathF.Pow(v.Radius + other.Radius, 2)) clear = false;
                }
                v.Velocity = clear ? (next - v.Pos) / dt : Vec3.Zero;
                if (clear) v.Pos = next;
                v.Pitch = MathF.Atan2(v.Velocity.Y, MathF.Max(3, v.Velocity.Length())) * (v.Hover ? .2f : .8f);
                v.Roll = v.Hover ? -.12f * v.Velocity.Dot(v.Right) / 23f : 0;
                for (int i = 0; i < v.Seats.Length; i++)
                {
                    if (v.Seats[i] == ControlVehicle.Empty) continue;
                    var p = GetPlayer(v.Seats[i]);
                    if (p == null || !p.Alive || p.Ride != v) { v.Seats[i] = ControlVehicle.Empty; continue; }
                    p.State.Pos = v.Eye(i) - Vec3.Up * Sim.EyeStanding;
                    p.State.Vel = v.Velocity;
                }
            }
        }
    }
}
