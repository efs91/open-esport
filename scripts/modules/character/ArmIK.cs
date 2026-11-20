using Godot;

/// <summary>Deux bras IK ; les cibles représentent la paume et portent la prise propre à l'arme.</summary>
public partial class ArmIK : SkeletonModifier3D
{
	public Node3D RightTarget;
	public Node3D LeftTarget;
	public WeaponHandPose RightPose;
	public WeaponHandPose LeftPose;
	public Node3D HeldRoot;
	public Vector3 HeldPosition;
	public Node3D Weapon;
	public bool LongGun, Knife, Sliding;
	public float GroundSpeed;
	public bool Grounded = true;
	private float _stride;
	private static readonly string[] Sides = { "Right", "Left" };

	public override void _ProcessModificationWithDelta(double delta)
	{
		var sk = GetSkeleton();
		if (sk == null) return;
		_stride += (float)delta * Mathf.Lerp(3f, 10f, Mathf.Clamp(GroundSpeed / 6f, 0, 1));
		bool aiming = GodotObject.IsInstanceValid(Weapon) && HeldRoot == Weapon && RightTarget?.GetParent() == Weapon;
		if (aiming) PlaceWeapon(sk);
		FitReach(sk, aiming);
		Apply(sk, "Right", RightTarget, RightPose);
		Apply(sk, "Left", LeftTarget, LeftPose);
	}

	// Les cibles sont attachées aux épaules animées, y compris lorsque le bassin
	// descend et que le torse pivote pendant la glissade.
	private void PlaceWeapon(Skeleton3D sk)
	{
		var actor = Weapon.GetParent<Node3D>();
		var local = actor.GlobalTransform.AffineInverse() * sk.GlobalTransform;
		Vector3 Joint(int i) => local * sk.GetBoneGlobalPose(i).Origin;
		int right = sk.FindBone("RightUpperArm"), left = sk.FindBone("LeftUpperArm");
		if (right < 0 || left < 0) return;
		if (LongGun)
		{
			int chest = sk.FindBone("Chest"), neck = sk.FindBone("Neck");
			if (chest >= 0 && neck >= 0)
			{
				// Avancer l'épaule de soutien pour atteindre le garde-main.
				var neckBasis = sk.GetBoneGlobalPose(neck).Basis;
				var up = (local.Basis.Inverse() * Vector3.Up).Normalized();
				SetGlobalBasis(sk,chest,new Basis(up, Mathf.DegToRad(-38)) * sk.GetBoneGlobalPose(chest).Basis);
				SetGlobalBasis(sk,neck,neckBasis);
			}
		}
		var shoulder = Joint(right);
		var center = (shoulder + Joint(left)) * .5f;
		float width = shoulder.DistanceTo(Joint(left)) * .5f;
		var palm = new Vector3(center.X + width * (LongGun ? 1.16f : .63f),
			shoulder.Y - (LongGun ? .13f : .20f) - (Sliding ? .045f : 0),
			Mathf.Min(center.Z,shoulder.Z) - (LongGun ? .22f : .33f));
		float yaw = Knife ? 54f : 0f;
		Weapon.Rotation = new Vector3(0,Mathf.DegToRad(yaw),Knife ? Mathf.DegToRad(-22) : 0);
		Weapon.Position = palm - Weapon.Basis * RightTarget.Position;
	}

	// Déplacer l'arme entière si une prise dépasse la portée : les mains gardent leur contact,
	// même sur les morphologies petites ou pendant l'oscillation du torse.
	private void FitReach(Skeleton3D sk, bool aiming)
	{
		if (!GodotObject.IsInstanceValid(HeldRoot)) return;
		if (!aiming)
		{
			HeldRoot.Position = HeldPosition;
			if (HeldRoot == Weapon) HeldRoot.Rotation = Vector3.Zero;
		}
		for (int iteration = 0; iteration < 8; iteration++)
		{
			bool moved = false;
			foreach (var side in Sides)
			{
				var target = side == "Right" ? RightTarget : LeftTarget;
				if (!GodotObject.IsInstanceValid(target) || !FindArm(sk, side, out int up, out int lo, out int hand)) continue;
				var tgt = sk.GlobalTransform.AffineInverse() * target.GlobalTransform;
				var wanted = tgt.Basis.Orthonormalized() * WeaponHandPose.HandRestFrame(sk, side).Inverse() * sk.GetBoneGlobalRest(hand).Basis;
				var h = sk.GetBoneGlobalPose(hand);
				var middle = sk.GetBoneGlobalPose(sk.FindBone(side + "MiddleProximal")).Origin;
				var wrist = tgt.Origin - wanted * (h.Basis.Inverse() * (middle - h.Origin) * WeaponHandPose.PalmAlong);
				var shoulder = sk.GetBoneGlobalPose(up).Origin;
				var elbow = sk.GetBoneGlobalPose(lo).Origin;
				float reach = (shoulder.DistanceTo(elbow) + elbow.DistanceTo(h.Origin)) * (aiming && LongGun ? .92f : .97f);
				var direction = wrist - shoulder;
				float excess = direction.Length() - reach;
				if (excess <= 0.0001f) continue;
				var correction = direction.Normalized() * excess;
				if (aiming && LongGun)
				{
					// Shorten reach in the vertical/forward plane first. Pulling a long
					// gun sideways towards the support hand drives its stock into the ribs.
					var local = HeldRoot.GetParent<Node3D>().GlobalBasis.Inverse() * sk.GlobalBasis;
					var lateral = local.Transposed().X.Normalized();
					float lateralDistance = direction.Dot(lateral);
					var plane = direction - lateral * lateralDistance;
					if (Mathf.Abs(lateralDistance) < reach && plane.LengthSquared() > .000001f)
						correction = plane - plane.Normalized() * Mathf.Sqrt(reach * reach - lateralDistance * lateralDistance);
				}
				HeldRoot.GlobalPosition -= sk.GlobalBasis * correction;
				moved = true;
			}
			if (!moved) break;
		}
	}

	private void Apply(Skeleton3D sk, string side, Node3D target, WeaponHandPose pose)
	{
		if (pose != null && GodotObject.IsInstanceValid(target) && target.IsInsideTree())
			Solve(sk, side, target, pose);
		else if (Grounded || Sliding)
			Relax(sk, side);
	}

	private static Basis FromTo(Vector3 a, Vector3 b)
	{
		if (a.LengthSquared() < 1e-10f || b.LengthSquared() < 1e-10f) return Basis.Identity;
		a = a.Normalized(); b = b.Normalized();
		float dot = Mathf.Clamp(a.Dot(b), -1f, 1f);
		var axis = a.Cross(b);
		if (axis.LengthSquared() < 1e-10f)
		{
			if (dot > 0) return Basis.Identity;
			axis = a.Cross(Mathf.Abs(a.Y) < 0.9f ? Vector3.Up : Vector3.Right);
		}
		return new Basis(axis.Normalized(), Mathf.Acos(dot));
	}

	private static void SetGlobalBasis(Skeleton3D sk, int bone, Basis global)
	{
		int parent = sk.GetBoneParent(bone);
		var pb = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis : Basis.Identity;
		sk.SetBonePoseRotation(bone, (pb.Inverse() * global).Orthonormalized().GetRotationQuaternion());
	}

	private static bool FindArm(Skeleton3D sk, string side, out int up, out int lo, out int hand)
	{
		up = sk.FindBone(side + "UpperArm"); lo = sk.FindBone(side + "LowerArm"); hand = sk.FindBone(side + "Hand");
		return up >= 0 && lo >= 0 && hand >= 0 && sk.FindBone(side + "MiddleProximal") >= 0
			&& sk.FindBone(side + "IndexProximal") >= 0 && sk.FindBone(side + "LittleProximal") >= 0;
	}

	private void Solve(Skeleton3D sk, string side, Node3D target, WeaponHandPose pose)
	{
		if (!FindArm(sk, side, out int up, out int lo, out int hand)) return;
		var tgt = sk.GlobalTransform.AffineInverse() * target.GlobalTransform;
		tgt.Basis = tgt.Basis.Orthonormalized();
		var restFrame = WeaponHandPose.HandRestFrame(sk, side);
		var wanted = tgt.Basis * restFrame.Inverse() * sk.GetBoneGlobalRest(hand).Basis;
		// Mesurer sur la main morphée : la cible est la paume, pas l'articulation du poignet.
		var handBefore = sk.GetBoneGlobalPose(hand);
		var middle = sk.GetBoneGlobalPose(sk.FindBone(side + "MiddleProximal")).Origin;
		var palmLocal = handBefore.Basis.Inverse() * (middle - handBefore.Origin) * WeaponHandPose.PalmAlong;
		Vector3 wristTarget = tgt.Origin - wanted * palmLocal;
		Vector3 a = sk.GetBoneGlobalPose(up).Origin, b = sk.GetBoneGlobalPose(lo).Origin, c = handBefore.Origin;
		float l1 = a.DistanceTo(b), l2 = b.DistanceTo(c);
		if (Mathf.Min(l1, l2) < 0.001f) return;
		var toTarget = wristTarget - a;
		float distance = toTarget.Length();
		if (distance < 1e-6f) return;
		float d = Mathf.Clamp(distance, Mathf.Abs(l1 - l2) + 0.001f, (l1 + l2) * 0.999f);
		Vector3 u = toTarget / distance;
		float x = (l1 * l1 - l2 * l2 + d * d) / (2f * d);
		float h = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - x * x));
		var chest = sk.FindBone("UpperChest");
		var torso = chest >= 0 ? sk.GetBoneGlobalPose(chest).Basis * sk.GetBoneGlobalRest(chest).Basis.Inverse() : Basis.Identity;
		var hint = torso * new Vector3(side == "Right" ? -.95f : .95f, -.70f, -.25f);
		if (HeldRoot == Weapon && LongGun)
		{
			int opposite = sk.FindBone(side == "Right" ? "LeftUpperArm" : "RightUpperArm");
			var outward = (a - sk.GetBoneGlobalPose(opposite).Origin).Normalized();
			hint = outward * 1.5f + torso * new Vector3(0,-.55f,-.2f);
		}
		var perp = hint - u * hint.Dot(u);
		if (perp.LengthSquared() < 1e-6f) perp = u.Cross(Mathf.Abs(u.Y) < 0.9f ? Vector3.Up : Vector3.Right);
		var elbow = a + u * x + perp.Normalized() * h;
		var wrist = a + u * d;
		SetGlobalBasis(sk, up, FromTo(b - a, elbow - a) * sk.GetBoneGlobalPose(up).Basis);
		var lower = sk.GetBoneGlobalPose(lo);
		SetGlobalBasis(sk, lo, FromTo(sk.GetBoneGlobalPose(hand).Origin - lower.Origin, wrist - lower.Origin) * lower.Basis);

		// Répartir la pronation entre avant-bras et poignet, sans déplacer le poignet.
		var current = sk.GetBoneGlobalPose(hand).Basis;
		var rotation = (wanted * current.Inverse()).Orthonormalized().GetRotationQuaternion();
		var axis = (sk.GetBoneGlobalPose(hand).Origin - sk.GetBoneGlobalPose(lo).Origin).Normalized();
		float twist = 2f * Mathf.Atan2(new Vector3(rotation.X, rotation.Y, rotation.Z).Dot(axis), rotation.W);
		twist = Mathf.Wrap(twist, -Mathf.Pi, Mathf.Pi);
		SetGlobalBasis(sk, lo, new Basis(axis, twist * 0.7f) * sk.GetBoneGlobalPose(lo).Basis);
		SetGlobalBasis(sk, hand, wanted);
		foreach (var finger in WeaponHandPose.FingerNames)
			for (int j = 0; j < 3; j++)
			{
				string suffix = finger + WeaponHandPose.JointName(finger, j);
				int bone = sk.FindBone(side + suffix);
				if (bone < 0 || !pose.Fingers.TryGetValue(suffix, out var frame)) continue;
				int next = j < 2 ? sk.FindBone(side + finger + WeaponHandPose.JointName(finger, j + 1)) : -1;
				var rest = WeaponHandPose.FingerRestFrame(sk, bone, next, restFrame, finger == "Thumb");
				SetGlobalBasis(sk, bone, tgt.Basis * frame * rest.Inverse() * sk.GetBoneGlobalRest(bone).Basis);
			}
	}

	/// <summary>Pose libre superposée à la locomotion : bras le long du corps, avec balancement à la marche.</summary>
	private void Relax(Skeleton3D sk, string side)
	{
		if (!FindArm(sk, side, out int up, out int lo, out int hand)) return;
		float sign = side == "Left" ? 1f : -1f;
		float swing = Mathf.Sin(_stride) * sign * Mathf.Clamp(GroundSpeed / 6f, 0, 1) * 0.4f;
		int chest = sk.FindBone("UpperChest");
		var torso = chest >= 0 ? sk.GetBoneGlobalPose(chest).Basis * sk.GetBoneGlobalRest(chest).Basis.Inverse() : Basis.Identity;
		var upper = sk.GetBoneGlobalPose(up);
		SetGlobalBasis(sk, up, FromTo(sk.GetBoneGlobalPose(lo).Origin - upper.Origin,
			torso * new Vector3(sign * 0.16f, -1, swing)) * upper.Basis);
		var lower = sk.GetBoneGlobalPose(lo);
		SetGlobalBasis(sk, lo, FromTo(sk.GetBoneGlobalPose(hand).Origin - lower.Origin,
			torso * new Vector3(sign * 0.07f, -1, 0.12f + swing)) * lower.Basis);
		var restFrame = WeaponHandPose.HandRestFrame(sk, side);
		var frame = torso * WeaponHandPose.Frame(new Vector3(sign * 0.05f, -1, 0.12f + swing), Vector3.Forward);
		var handDelta = frame * restFrame.Inverse();
		SetGlobalBasis(sk, hand, handDelta * sk.GetBoneGlobalRest(hand).Basis);
		foreach (var finger in WeaponHandPose.FingerNames)
			for (int j = 0; j < 3; j++)
			{
				int bone = sk.FindBone(side + finger + WeaponHandPose.JointName(finger, j));
				float curl = finger == "Thumb" ? 0 : sign * 0.18f * (j + 1);
				if (bone >= 0) SetGlobalBasis(sk, bone, handDelta * new Basis(restFrame.Y, curl) * sk.GetBoneGlobalRest(bone).Basis);
			}
	}
}
