using Godot;

/// <summary>
/// Bras et jambes en chute libre, posés par-dessus l'animation : bras tendus au-dessus de la tête quand on plonge (« avancer » maintenu),
/// ramenés le long du corps, mains près des hanches, quand on relâche. Le passage de l'un à l'autre est progressif. Jambes tendues, pointes de pieds allongées.
/// Directions dans le repère du squelette (humanoïde Godot : +Y vers la tête, +Z vers l'avant du personnage).
/// </summary>
public partial class DiveArms : SkeletonModifier3D
{
	/// <summary>Vrai pendant la chute libre (sinon le modificateur ne touche à rien).</summary>
	public bool On;
	/// <summary>1 = bras au-dessus de la tête (plongeon), 0 = mains aux hanches.</summary>
	public float Raise;

	private float _raise;
	private float _weight;

	public override void _ProcessModificationWithDelta(double delta)
	{
		var sk = GetSkeleton();
		if (sk == null) return;
		float dt = (float)delta;
		_weight = Mathf.MoveToward(_weight, On ? 1f : 0f, dt * 4f);
		_raise = Mathf.MoveToward(_raise, Raise, dt * 3f);
		if (_weight <= 0f) return;
		Pose(sk, "Left", -1f);
		Pose(sk, "Right", 1f);
		Leg(sk, "Left", -1f);
		Leg(sk, "Right", 1f);
	}

	/// <summary>Jambe tendue dans l'axe du corps, un peu écartée de l'autre, pied allongé.</summary>
	private void Leg(Skeleton3D sk, string name, float side)
	{
		int up = sk.FindBone(name + "UpperLeg"), lo = sk.FindBone(name + "LowerLeg"), foot = sk.FindBone(name + "Foot");
		if (up < 0 || lo < 0 || foot < 0) return;
		var leg = new Vector3(-0.05f * side, -1f, 0f).Normalized();
		Aim(sk, up, leg);
		Aim(sk, lo, leg);
		Aim(sk, foot, new Vector3(-0.05f * side, -1f, 0.35f).Normalized());
	}

	/// <summary>Oriente le bras, l'avant-bras et la main d'un côté (side : −1 gauche, +1 droite) vers la direction voulue.</summary>
	private void Pose(Skeleton3D sk, string name, float side)
	{
		int up = sk.FindBone(name + "UpperArm"), lo = sk.FindBone(name + "LowerArm"), hand = sk.FindBone(name + "Hand");
		if (up < 0 || lo < 0 || hand < 0) return;
		// gauche du personnage = +X dans le repère du squelette (il regarde vers +Z)
		float x = -side;
		var overhead = new Vector3(0.16f * x, 1f, 0.12f).Normalized();
		var hips = new Vector3(0.22f * x, -1f, -0.08f).Normalized();
		var wanted = hips.Slerp(overhead, Mathf.SmoothStep(0f, 1f, _raise));
		Aim(sk, up, wanted);
		Aim(sk, lo, wanted);
		Aim(sk, hand, wanted);
	}

	/// <summary>Tourne l'os pour que son axe (+Y) suive la direction voulue, dosé par le poids du modificateur.</summary>
	private void Aim(Skeleton3D sk, int bone, Vector3 wanted)
	{
		var global = sk.GetBoneGlobalPose(bone);
		var current = global.Basis.Y.Normalized();
		var target = current.Slerp(wanted, _weight);
		if (current.Dot(target) > 0.9999f) return;
		var turn = new Quaternion(current, target);
		var basis = new Basis(turn) * global.Basis;
		int parent = sk.GetBoneParent(bone);
		var pb = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis : Basis.Identity;
		sk.SetBonePoseRotation(bone, (pb.Inverse() * basis).Orthonormalized().GetRotationQuaternion());
	}
}
