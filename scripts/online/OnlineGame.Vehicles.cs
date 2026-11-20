using System;
using System.Collections.Generic;
using Godot;
using OpenEsport.Core;
using Openesport.Utils;

namespace Openesport.Online
{
    public partial class OnlineGame
    {
        private readonly List<Node3D> _vehicleModels = new();
        private readonly List<AnimationPlayer> _walkerAnimations = new();
        private Camera3D _rideCamera;
        private Node3D _passengerCabin;
        private CharacterAnimator _rider;
        private VehicleInstruments _instruments;
        private int _lastSeat = -1, _lastRide = -1;
        private ControlVehicle[] _lastFleet;
        private float _fleetAge;

        private static Vector3 WorldVector(Vec3 p) => new Vector3(p.X, p.Y, p.Z);

        private string VehicleHint()
        {
            var fleet = Client.Mode.Vehicles;
            if (fleet == null || !Client.Alive || Client.Match.Phase != MatchPhase.Playing) return null;
            if (Client.Mode.Ride(Client.PlayerId, out _) != null) return "";
            foreach (var v in fleet)
                if (v.Team == Client.TeamId && v.CanBoard(Client.Predicted.Pos) && Array.IndexOf(v.Seats, ControlVehicle.Empty) >= 0)
                    return v.Hover ? "E : piloter le hover" : "E : embarquer dans l’AT-ST";
            return null;
        }

        private void SetWalkerDisplay(bool mounted)
        {
            _hud.SetWalkerDisplay(mounted);
            _player.GetNode<HUD>("HUD").SetWalkerDisplay(mounted);
        }

        private static Node3D BuildPassengerCabin()
        {
            var cabin = new Node3D();
            var plate = new StandardMaterial3D { AlbedoColor = new Color("#384348"), Roughness = .77f };
            var lip = new StandardMaterial3D { AlbedoColor = new Color("#748381"), Metallic = .55f };
            var lamp = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("#98c3ab") };
            void Panel(float x, float y, float z, float w, float h, float d, Material material)
            {
                cabin.AddChild(new MeshInstance3D {
                    Position = new Vector3(x,y,z), Mesh = new BoxMesh { Size = new Vector3(w,h,d), Material = material },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                });
            }
            // At the far lip, the opening clears the full server-authorized firing arc.
            foreach (int side in new[] { -1, 1 })
            {
                Panel(0, side * .75f, -.68f, 3.4f, 1.18f, .14f, plate);
                Panel(side * 1.06f, 0, -.68f, 1.28f, .34f, .14f, plate);
                Panel(0, side * .18f, -.60f, .90f, .04f, .08f, lip);
                Panel(side * .44f, 0, -.60f, .04f, .32f, .08f, lip);
                Panel(side * 1.48f, 0, .1f, .12f, 2.7f, 1.65f, plate);
                Panel(0, side * 1.25f, .1f, 3, .14f, 1.65f, plate);
                Panel(side * .49f, -.49f, -.59f, .14f, .025f, .03f, lamp);
            }
            return cabin;
        }

        private void VehicleKeys()
        {
            if (Input.IsActionJustPressed(InputManager.Actions.INTERACT) && VehicleHint() != null)
                Client.SendCommand(CommandType.BoardVehicle, 0);
        }

        public override void _UnhandledKeyInput(InputEvent e)
        {
            if (Client == null || e is not InputEventKey k || !k.Pressed || k.Echo || Input.MouseMode != Input.MouseModeEnum.Captured) return;
            var ride = Client.Mode.Ride(Client.PlayerId, out _);
            int index = (int)k.Keycode - (int)Key.F1;
            if (ride == null || ride.Hover || index < 0 || index >= 5) return;
            Client.SendCommand(CommandType.BoardVehicle, (byte)(index + 1));
            GetViewport().SetInputAsHandled();
        }

        private void UpdateVehicles(float dt)
        {
            var fleet = Client.Mode.Vehicles;
            if (fleet == null) return;
            if (fleet != _lastFleet) { _fleetAge = 0; _lastFleet = fleet; } else _fleetAge = Mathf.Min(.24f, _fleetAge + dt);
            while (_vehicleModels.Count < fleet.Length)
            {
                var v = fleet[_vehicleModels.Count];
                var body = new Node3D();
                _worldRoot.AddChild(body);
                var asset = v.Hover ? "AI_Military_Combat_Hover" : "at-st";
                var mesh = GD.Load<PackedScene>("res://assets/Vehicules/" + asset + ".glb").Instantiate<Node3D>();
                mesh.Scale = Vector3.One * (v.Hover ? ControlVehicle.HoverScale : ControlVehicle.WalkerScale);
                mesh.Rotation = new Vector3(0, v.Hover ? -Mathf.Pi / 2 : Mathf.Pi, 0);
                if (v.Hover) mesh.Position = Vector3.Up * .68f;
                body.AddChild(mesh);
                _walkerAnimations.Add(null);
                if (!v.Hover && mesh.FindChildren("*", "AnimationPlayer", true, false) is { Count: > 0 } players)
                    _walkerAnimations[^1] = (AnimationPlayer)players[0];
                body.GlobalPosition = WorldVector(v.Pos);
                body.Rotation = new Vector3(v.Pitch, v.Yaw, v.Roll);
                _vehicleModels.Add(body);
            }
            int myRide = -1, mySeat = -1;
            for (int i = 0; i < fleet.Length; i++)
            {
                var v = fleet[i];
                var body = _vehicleModels[i];
                float blend = 1 - Mathf.Exp(-16 * dt);
                body.GlobalPosition = body.GlobalPosition.Lerp(WorldVector(v.Pos + v.Velocity * _fleetAge), blend);
                body.Rotation = new Vector3(v.Pitch, Mathf.LerpAngle(body.Rotation.Y, v.Yaw, blend), v.Roll);
                int seat = Array.IndexOf(v.Seats, Client.PlayerId);
                bool aboard = seat >= 0 && Client.Alive && Client.Match.Phase == MatchPhase.Playing;
                var animation = _walkerAnimations[i];
                if (animation != null && animation.HasAnimation("atst_bm_walk_frwd"))
                {
                    bool moving = v.Velocity.LengthSquared() > .25f;
                    if (moving && !animation.IsPlaying()) animation.Play("atst_bm_walk_frwd");
                    if (!moving && animation.IsPlaying()) animation.Pause();
                }
                body.Visible = v.Integrity > 0 && (!aboard || v.Hover);
                if (aboard) { myRide = i; mySeat = seat; }
            }
            if (myRide < 0)
            {
                if (_lastRide >= 0)
                {
                    _head.GetNode<Camera3D>("Camera").MakeCurrent();
                    _rideCamera.QueueFree(); _rideCamera = null;
                    _passengerCabin?.QueueFree(); _passengerCabin = null;
                    SetWalkerDisplay(false);
                    _rider?.QueueFree(); _rider = null;
                    _instruments.Visible = false;
                    _player.GetNode<CanvasLayer>("InventoryUI").Visible = true;
                }
                _lastRide = _lastSeat = -1;
                return;
            }
            var vehicle = fleet[myRide];
            if (_lastSeat != mySeat || _lastRide != myRide)
            {
                if (_rideCamera == null)
                {
                    _rideCamera = new Camera3D { Near = .04f };
                    AddChild(_rideCamera);
                }
                _rideCamera.MakeCurrent();
                _player.GlobalRotation = new Vector3(0, vehicle.SeatHeading(mySeat), 0);
                _head.Rotation = Vector3.Zero;
                _passengerCabin?.QueueFree(); _passengerCabin = null;
                if (mySeat >= 2)
                {
                    _passengerCabin = BuildPassengerCabin();
                    _worldRoot.AddChild(_passengerCabin);
                }
                _rideCamera.Fov = 75f;
                SetWalkerDisplay(!vehicle.Hover);
                _rider?.QueueFree(); _rider = null;
                if (vehicle.Hover)
                {
                    _rider = new CharacterAnimator();
                    _vehicleModels[myRide].AddChild(_rider);
                    _rider.SetHoverSeated(true);
                    _rider.Position += Vector3.Up * CharacterAnimator.HoverSaddleHeight;
                }
                if (_instruments == null)
                {
                    var layer = new CanvasLayer { Layer = 4 };
                    AddChild(layer);
                    _instruments = new VehicleInstruments { MouseFilter = Control.MouseFilterEnum.Ignore };
                    layer.AddChild(_instruments);
                    _instruments.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                }
                _lastSeat = mySeat; _lastRide = myRide;
                _player.GetNode<CanvasLayer>("InventoryUI").Visible = mySeat >= 2;
            }
            vehicle.Aim(mySeat, _player.GlobalRotation.Y, _head.Rotation.X, out float yaw, out float pitch);
            // Keep the driver's raw yaw for steering. Rear seats have a physical limit at the port.
            if (mySeat >= 2) _player.GlobalRotation = new Vector3(0, yaw, 0);
            _head.Rotation = new Vector3(pitch, 0, 0);
            var eye = _vehicleModels[myRide].GlobalPosition + WorldVector(vehicle.Eye(mySeat) - vehicle.Pos);
            _rideCamera.GlobalRotation = new Vector3(pitch, yaw, 0);
            if (vehicle.Hover)
            {
                var aim = -_rideCamera.GlobalBasis.Z;
                var offset = _rideCamera.GlobalBasis * new Vector3(0, 3.4f, 10.5f);
                float length = offset.Length();
                var ray = offset.Normalized();
                if (_worldCollision.Raycast(new Vec3(eye.X, eye.Y, eye.Z), new Vec3(ray.X, ray.Y, ray.Z), length, out float hit)) offset = ray * Mathf.Max(.2f, hit - .25f);
                _rideCamera.GlobalPosition = eye + offset;
                _rideCamera.LookAt(eye + aim * 8f, Vector3.Up);
                _instruments.HoverSight = _rideCamera.UnprojectPosition(eye + aim * 65f);
            }
            else
            {
                _rideCamera.GlobalPosition = eye;
                if (_passengerCabin != null)
                {
                    _passengerCabin.GlobalPosition = eye;
                    _passengerCabin.GlobalRotation = new Vector3(0, vehicle.SeatHeading(mySeat), 0);
                }
                bool zoom = mySeat < 2 && Input.MouseMode == Input.MouseModeEnum.Captured &&
                    !InputManager.ClickGuard && !InputManager.UiBlock && Input.IsActionPressed(InputManager.Actions.AIM);
                int power = zoom ? (mySeat == 0 ? 2 : 4) : 1;
                float fov = Mathf.RadToDeg(2 * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(75f / 2)) / power));
                _rideCamera.Fov = Mathf.Lerp(_rideCamera.Fov, fov, 1 - Mathf.Exp(-20 * dt));
                _instruments.ZoomPower = power;
            }
            _weapons.HideViewmodels = true; _weapons.Visible = false;
            _instruments.Vehicle = vehicle; _instruments.Seat = mySeat; _instruments.Visible = !_hud.IsLoading; _instruments.QueueRedraw();
        }
    }
}
