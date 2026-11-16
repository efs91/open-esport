using Godot;

/// <summary>
/// Écarte les bras du buste après l'animation. Les animations Mixamo sont faites pour un corps plus étroit que celui de
/// MakeHuman : sans correction, les avant-bras traversent le torse. Appliqué sur le squelette, après l'AnimationPlayer.
/// </summary>
public partial class ArmClearance : SkeletonModifier3D
{
	/// <summary>Écartement des épaules (clavicules), en degrés.</summary>
	[Export] public float ShoulderDegrees = 6f;
	/// <summary>Écartement des bras, en degrés, autour de l'axe avant du modèle.</summary>
	[Export] public float ArmDegrees = 22f;

	public override void _ProcessModification()
	{
		var sk = GetSkeleton();
		if (sk == null) return;
		Abduct(sk, "LeftShoulder", ShoulderDegrees);
		Abduct(sk, "RightShoulder", -ShoulderDegrees);
		Abduct(sk, "LeftUpperArm", ArmDegrees);
		Abduct(sk, "RightUpperArm", -ArmDegrees);
	}

	// Le modèle regarde vers +Z : son côté gauche est +X. Pivot autour de Z, dans l'espace du squelette.
	private static void Abduct(Skeleton3D sk, string bone, float degrees)
	{
		int i = sk.FindBone(bone);
		if (i < 0) return;
		var rot = new Basis(Vector3.Back, Mathf.DegToRad(degrees));
		int parent = sk.GetBoneParent(i);
		var parentBasis = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis : Basis.Identity;
		var global = parentBasis * new Basis(sk.GetBonePoseRotation(i));
		var local = parentBasis.Inverse() * (rot * global);
		sk.SetBonePoseRotation(i, local.GetRotationQuaternion());
	}
}
