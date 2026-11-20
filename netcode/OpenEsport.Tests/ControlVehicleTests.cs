using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
    [Collection("Network")]
    public class ControlVehicleTests
    {
        private static WorldData Load([CallerFilePath] string file = "") => WorldData.LoadFile(Path.Combine(Path.GetDirectoryName(file), "..", "..", "assets", "maps", "controle.world"));
        [Fact]
        public void The_Canyon_Connects_Both_Bases_To_All_Five_Objectives()
        {
            var d = Load();
            Assert.Equal(320, d.MaxX - d.MinX); Assert.Equal(208, d.MaxZ - d.MinZ);
            var goals = d.PointsOf(MapPointKind.ControlPoint).OrderBy(p => p.Index).ToArray();
            Assert.Equal(5, goals.Length);
            Assert.True(goals[0].Pos.Y > goals[2].Pos.Y && goals[2].Pos.Y > goals[3].Pos.Y);
            Assert.True(d.HeightAt(0, 0) < goals[2].Pos.Y - 6);
            var nav = Navigation.For(d);
            Assert.IsType<LayeredNav>(nav);
            foreach (var team in new[] { 0, 1 })
            {
                var spawns = d.PointsOf(MapPointKind.SpawnTeam).Where(p => p.Team == team).ToArray();
                Assert.Equal(10, spawns.Length);
                Assert.Equal(2, d.PointsOf(MapPointKind.VehicleBay).Count(p => p.Team == team));
                foreach (var start in spawns)
                    foreach (var point in goals)
                        Assert.True(nav.FindPath(start.Pos, point.Pos, new List<Vec3>(), 90000), $"{start.Pos} to {point.Pos}");
            }
        }

        [Fact]
        public void Two_Fleets_In_A_Ten_A_Side_Match_Enforce_Seats_Flight_And_Weapons()
        {
            using var rig = new Rig(o =>
            {
                o.Mode = GameMode.Control; o.Match.TeamSize = 10; o.Match.MaxPlayers = 20; o.MinPlayers = 20;
                o.LobbyCountdown = .25f; o.FillBots = false; o.EndLinger = 100;
            }, Load());
            var players = Enumerable.Range(0, 20).Select(i => rig.Connect("J" + i)).ToArray();
            foreach (var p in players) Assert.True(rig.WaitPlaying(p));
            Assert.True(rig.WaitUntil(() => players.All(p => p.C.Mode.Vehicles?.Length == 4), 8));
            var blue = players.Where(p => rig.Server.GetPlayer(p.C.PlayerId).Team == 0).ToArray();
            var red = players.First(p => rig.Server.GetPlayer(p.C.PlayerId).Team == 1);
            var walker = blue[0].C.Mode.Vehicles.Single(v => !v.Hover && v.Team == 0);
            var hover = blue[0].C.Mode.Vehicles.Single(v => v.Hover && v.Team == 0);
            void Board(Rig.Bot player, ControlVehicle v)
            {
                rig.Teleport(player, v.Pos + new Vec3(3.3f, 0, 0));
                player.C.SendCommand(CommandType.BoardVehicle, 0);
                rig.Run(.32);
            }
            ControlVehicle Ride(Rig.Bot p) => p.C.Mode.Ride(p.C.PlayerId, out _);
            Board(red, walker);
            Assert.Null(Ride(red));
            for (int i = 0; i < 5; i++)
            {
                Board(blue[i], walker);
                Assert.NotNull(blue[i].C.Mode.Ride(blue[i].C.PlayerId, out int seat));
                Assert.Equal(i, seat);
            }
            Board(blue[5], walker);
            Assert.Null(Ride(blue[5]));
            Board(blue[5], hover);
            Assert.True(Ride(blue[5]).Hover);
            Board(blue[6], hover);
            Assert.Null(Ride(blue[6]));
            var appearances = new Dictionary<ushort, ShotAppearance>();
            blue[0].C.EventReceived += e =>
            {
                if (e.Type == EventType.Shot) appearances[e.A] = (ShotAppearance)e.D;
            };
            foreach (int station in new[] { 0, 1, 2, 5 })
            {
                long before = rig.Server.TotalShots;
                blue[station].Buttons = Buttons.Fire;
                rig.Run(.20);
                blue[station].Buttons = Buttons.None;
                rig.Run(.08);
                Assert.True(rig.Server.TotalShots > before, $"station {station} should fire");
                Assert.Equal(station switch { 0 => ShotAppearance.WalkerMachineGun, 1 => ShotAppearance.WalkerCannon, _ => ShotAppearance.Standard },
                    appearances[blue[station].C.PlayerId]);
            }
            blue[5].Buttons = Buttons.Jump | Buttons.Fire; blue[5].Forward = 1;
            blue[5].Yaw = hover.Yaw;
            blue[0].Yaw = walker.Yaw; blue[0].Forward = 1;
            blue[1].Buttons = Buttons.Fire; blue[2].Buttons = Buttons.Fire;
            long shots = rig.Server.TotalShots;
            rig.Run(1);
            Assert.True(Ride(blue[5]).Pos.Y > hover.Pos.Y + 3);
            Assert.True(Vec3.Distance(Ride(blue[0]).Pos, walker.Pos) > 2);
            Assert.True(rig.Server.TotalShots > shots + 2);
            var passenger = rig.Server.GetPlayer(blue[2].C.PlayerId);
            Assert.InRange(MathF.Abs(ControlVehicle.Angle(passenger.Yaw - passenger.Ride.SeatHeading(2))), 0, .50f);
            blue[5].Buttons = Buttons.None; blue[5].Forward = 0;
            blue[0].Forward = 0; blue[1].Buttons = Buttons.None; blue[2].Buttons = Buttons.None;
            blue[1].C.SendCommand(CommandType.BoardVehicle, 1); rig.Run(.35);
            Assert.Equal(1, rig.Server.GetPlayer(blue[1].C.PlayerId).Seat);
            blue[0].C.SendCommand(CommandType.BoardVehicle, 0); rig.Run(.35);
            Assert.Null(Ride(blue[0]));
            blue[1].C.SendCommand(CommandType.BoardVehicle, 1); rig.Run(.35);
            Assert.Equal(0, rig.Server.GetPlayer(blue[1].C.PlayerId).Seat);
            rig.OnServer(() => rig.Server.AdminKill(red.C.PlayerId, blue[1].C.PlayerId));
            rig.Run(.4);
            Assert.Null(Ride(blue[1]));
        }
    }
}
