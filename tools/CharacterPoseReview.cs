using Godot;
using Openesport.Inventory;

/// <summary>Vue de contrôle hors jeu : captures dans .godot/pose-review.</summary>
public partial class CharacterPoseReview : Node3D
{
	private static readonly string[] Weapons = { "unarmed", "knife", "c19", "p2020", "smg45", "sniper", "ak", "lmg63", "sawnoff", "peacemaker" };
	private CharacterAnimator _character;
	private Vector3 _velocity;
	private bool _onFloor = true, _sliding;
	private float _maxError;
	private bool _invalidFreeHand;
	private int _failures;

	public override void _Process(double delta) => _character?.Tick(_velocity, _onFloor, _sliding, delta);

	private void Check(bool condition, string message)
	{
		if (condition) return;
		_failures++;
		GD.PushError("POSE_REVIEW " + message);
	}

	public override async void _Ready()
	{
		bool capture = DisplayServer.GetName() != "headless";
		var env = new WorldEnvironment { Environment = new Godot.Environment {
			BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("282e38"),
			AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White,
			AmbientLightEnergy = 0.65f, TonemapMode = Godot.Environment.ToneMapper.Filmic } };
		AddChild(env);
		AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, -35, 0), LightEnergy = 1.8f });
		var camera = new Camera3D { Position = new Vector3(1.1f, 1.45f, -1.7f), Fov = 35 };
		AddChild(camera);
		camera.LookAt(new Vector3(0, 1.25f, -0.15f));
		var appearance = new CharacterAppearance();
		appearance.Pieces["clothes"] = 3;
		var character = new CharacterAnimator { Appearance = appearance };
		AddChild(character);
		_character = character;
		var ik = character.Skeleton.GetNode<ArmIK>("ArmIK");
		ik.ModificationProcessed += () => {
			foreach (var side in new[] { "Right", "Left" })
			{
				var target = side == "Right" ? ik.RightTarget : ik.LeftTarget;
				var sk = character.Skeleton;
				var hand = sk.GetBoneGlobalPose(sk.FindBone(side + "Hand")).Origin;
				if (target == null)
				{
					var shoulder = sk.GetBoneGlobalPose(sk.FindBone(side + "UpperArm")).Origin;
					var elbow = sk.GetBoneGlobalPose(sk.FindBone(side + "LowerArm")).Origin;
					int chest = sk.FindBone("UpperChest");
					var down = (sk.GetBoneGlobalPose(chest).Basis * sk.GetBoneGlobalRest(chest).Basis.Inverse() * Vector3.Down).Normalized();
					float length = shoulder.DistanceTo(elbow) + elbow.DistanceTo(hand);
					if (ik.Grounded && (!hand.IsFinite() || (hand - shoulder).Dot(down) < length * 0.7f) && !_invalidFreeHand)
					{
						_invalidFreeHand = true;
						GD.Print("POSE_REVIEW free hand detail: ", side, " hand=", hand, " shoulder=", shoulder, " clip=", character.CurrentClip, " speed=", ik.GroundSpeed);
					}
					continue;
				}
				var mid = sk.GetBoneGlobalPose(sk.FindBone(side + "MiddleProximal")).Origin;
				float error = (sk.GlobalTransform * hand.Lerp(mid, WeaponHandPose.PalmAlong)).DistanceTo(target.GlobalPosition);
				_maxError = float.IsFinite(error) ? Mathf.Max(_maxError, error) : float.PositiveInfinity;
			}
		};
		string folder = "res://.godot/pose-review";
		DirAccess.MakeDirRecursiveAbsolute(folder);
		async System.Threading.Tasks.Task Photo(string name, Vector3 pos, Vector3 focus)
		{
			camera.Position = pos;
			camera.LookAt(focus);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(folder + "/" + name + ".png");
		}

		float maxSlideHeight = float.NegativeInfinity;
		float minKnifeAlign = 1f, minElbowSide = float.PositiveInfinity, minStockClearance = float.PositiveInfinity;
		Aabb weaponBounds = default;
		ik.ModificationProcessed += () =>
		{
			if (ik.RightTarget == null) return;
			var sk = character.Skeleton;
			Vector3 Joint(string n) => character.ToLocal(sk.ToGlobal(sk.GetBoneGlobalPose(sk.FindBone(n)).Origin));
			var r = Joint("RightUpperArm");
			var l = Joint("LeftUpperArm");
			var center = (r+l)*.5f;
			var across = (r-l).Normalized();
			var palm = character.ToLocal(ik.RightTarget.GlobalPosition);
			if (ik.Sliding)
				maxSlideHeight = Mathf.Max(maxSlideHeight, palm.Y-r.Y);
			else
			{
				minElbowSide = Mathf.Min(minElbowSide, (Joint("RightLowerArm")-center).Dot(across));
				if (ik.LeftTarget != null) minElbowSide = Mathf.Min(minElbowSide, (center-Joint("LeftLowerArm")).Dot(across));
				if (ik.Knife)
				{
					var forearm = (Joint("RightHand")-Joint("RightLowerArm")).Normalized();
					var fingers = (palm-Joint("RightHand")).Normalized();
					minKnifeAlign = Mathf.Min(minKnifeAlign, forearm.Dot(fingers));
				}
				if (ik.LongGun)
				{
					// Sample the stock line against the core of the upper torso (horizontal ellipse).
					across.Y = 0;
					across = across.Normalized();
					var front = Vector3.Up.Cross(across);
					var butt = character.ToLocal(ik.Weapon.ToGlobal(new Vector3(0,0,weaponBounds.End.Z)));
					for (int i = 0; i <= 12; i++)
					{
						var offset = butt.Lerp(palm,i/12f) - center;
						float side = offset.Dot(across)/(.40f*r.DistanceTo(l));
						float depth = offset.Dot(front)/.095f;
						minStockClearance = Mathf.Min(minStockClearance,side*side + depth*depth);
					}
				}
			}
		};

		foreach (string id in Weapons)
		{
			character.Hold(id == "unarmed" ? null : ItemDatabase.Get(id));
			if (ik.Weapon != null && ik.LongGun) weaponBounds = ItemVisuals.ComputeLocalAabb(ik.Weapon) ?? default;
			_maxError = 0;
			Check((ik.RightTarget != null) == (id != "unarmed"), id + " right target");
			Check((ik.LeftTarget != null) == (id is not ("unarmed" or "knife" or "c19")), id + " left target");
			if (ik.RightPose != null) Check(ik.RightPose.Fingers.Count == 15, id + " right fingers");
			if (ik.LeftPose != null) Check(ik.LeftPose.Fingers.Count == 15, id + " left fingers");
			for (int frame = 0; frame < 12; frame++)
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (capture)
			{
				await Photo(id, new Vector3(1.1f,1.45f,-1.7f), new Vector3(0,1.25f,-.15f));
				await Photo(id + "-left", new Vector3(-1.1f,1.45f,-1.7f), new Vector3(0,1.25f,-.15f));
				await Photo(id + "-profile", new Vector3(2.15f,1.3f,-.15f), new Vector3(0,1.25f,-.15f));
			}
			_sliding = true;
			_velocity = Vector3.Forward * 9;
			for (int frame = 0; frame < 35; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Check(character.CurrentClip == "slide", id + " slide clip");
			if (capture) await Photo(id + "-slide", new Vector3(2.2f,.75f,-1.7f), new Vector3(0,.32f,-.15f));
			_sliding = false;
			_velocity = Vector3.Zero;
			Check(_maxError < 0.002f, id + " palm error " + _maxError);
			GD.Print("POSE_REVIEW ", id, " palm_error_m=", _maxError);
		}
		// Rééquiper, changer de morphologie avec une arme en main et déplacer le parent :
		// ces cas détectent les prises obsolètes et les mélanges entre repères local/global.
		foreach (var morph in new[] {
			new CharacterAppearance { Height = -1, Weight = -1, Muscle = -1, Gender = 1 },
			new CharacterAppearance { Height = 1, Weight = 1, Muscle = 1 },
			new CharacterAppearance { Height = -1, Weight = 1, Age = 1 } })
		{
			morph.SetDetail("measure/measure-upperarm-length-decr|incr", morph.Height * .65f);
			character.SetAppearance(morph);
			character.Position = new Vector3(2, 0.5f, -3);
			character.Rotation = new Vector3(0, 1.2f, 0);
			character.Scale = new Vector3(1, 0.72f, 1);
			foreach (string id in Weapons)
			{
				character.Hold(id == "unarmed" ? null : ItemDatabase.Get(id));
				if (ik.Weapon != null && ik.LongGun) weaponBounds = ItemVisuals.ComputeLocalAabb(ik.Weapon) ?? default;
				_maxError = 0;
				foreach (float speed in new[] { 0f, 2.4f, 6.5f, 9f })
				{
					_sliding = speed == 9f;
					_velocity = new Vector3(0, 0, -speed);
					for (int frame = 0; frame < 20; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				}
				Check(_maxError < 0.002f, id + " morph/locomotion error " + _maxError);
				GD.Print("POSE_REVIEW morph ", morph.Height, "/", morph.Weight, " ", id, " palm_error_m=", _maxError);
			}
		}
		// Une main libre conserve l'animation corporelle pendant le saut/la glissade.
		character.Hold(null);
		_sliding = false;
		_onFloor = false; _velocity = new Vector3(0, 2, 0);
		for (int frame = 0; frame < 8; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Check(character.CurrentClip == "jump_up", "unarmed jump");
		_onFloor = true; _sliding = true; _velocity = new Vector3(0, 0, -9);
		for (int frame = 0; frame < 30; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Check(!_invalidFreeHand, "free hand did not relax below shoulder");
		GD.Print("POSE_REVIEW slide_height=",maxSlideHeight," elbow_side=",minElbowSide," knife_alignment=",minKnifeAlign," stock_clearance=",minStockClearance);
		Check(maxSlideHeight < 0, "sliding weapon rose above shoulder");
		Check(minKnifeAlign > 0, "knife bends the wrist backwards");
		Check(minElbowSide > 0, "elbow crossed torso midline");
		Check(minStockClearance >= 1, "stock intersects upper torso core");
		GD.Print("POSE_REVIEW failures=", _failures);
		GetTree().Quit(_failures == 0 ? 0 : 1);
	}
}
