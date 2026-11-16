using Godot;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Prise propre à une arme, relevée sur ses mains FPS avant de retirer le viewmodel.
/// Le repère suit la paume (X : poignet vers majeur, Y : index vers auriculaire),
/// et les phalanges sont exprimées dans ce repère, indépendamment des axes des deux rigs.
/// </summary>
public sealed class WeaponHandPose
{
	public const float PalmAlong = 0.6f;
	public Transform3D Palm;
	public readonly Dictionary<string, Basis> Fingers = new();
	public static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };
	private static readonly string[] SourceFingers = { "thumb", "point", "middle", "ring", "pink" };
	private static readonly string[] FingerJoints = { "Proximal", "Intermediate", "Distal" };
	private static readonly string[] ThumbJoints = { "Metacarpal", "Proximal", "Distal" };

	/// <summary>Échantillonner l'idle hors arbre ; la pose enregistrée dans la scène peut être un rechargement.</summary>
	public static void SampleIdle(Node3D model)
	{
		foreach (var node in model.FindChildren("*", "AnimationPlayer", true, false))
		{
			var player = (AnimationPlayer)node;
			if (!player.HasAnimation("idle")) continue;
			var root = player.GetNode(player.RootNode);
			var animation = player.GetAnimation("idle");
			for (int track = 0; track < animation.GetTrackCount(); track++)
			{
				if (!animation.TrackIsEnabled(track) || animation.TrackGetKeyCount(track) == 0) continue;
				var path = animation.TrackGetPath(track);
				var target = root.GetNodeOrNull<Node3D>(new NodePath(path.GetConcatenatedNames()));
				if (target == null) continue;
				int bone = target is Skeleton3D skeleton && path.GetSubNameCount() == 1 ? skeleton.FindBone(path.GetSubName(0)) : -1;
				if (path.GetSubNameCount() > 0 && bone < 0) continue;
				switch (animation.TrackGetType(track))
				{
					case Animation.TrackType.Position3D:
						var position = animation.PositionTrackInterpolate(track, 0);
						if (bone >= 0) ((Skeleton3D)target).SetBonePosePosition(bone, position); else target.Position = position;
						break;
					case Animation.TrackType.Rotation3D:
						var rotation = animation.RotationTrackInterpolate(track, 0);
						if (bone >= 0) ((Skeleton3D)target).SetBonePoseRotation(bone, rotation); else target.Quaternion = rotation;
						break;
					case Animation.TrackType.Scale3D:
						var scale = animation.ScaleTrackInterpolate(track, 0);
						if (bone >= 0) ((Skeleton3D)target).SetBonePoseScale(bone, scale); else target.Scale = scale;
						break;
				}
			}
			return;
		}
	}

	public static string JointName(string finger, int joint) => (finger == "Thumb" ? ThumbJoints : FingerJoints)[joint];

	public static Basis Frame(Vector3 along, Vector3 across)
	{
		var x = along.Normalized();
		var y = (across - x * across.Dot(x)).Normalized();
		return new Basis(x, y, x.Cross(y));
	}

	public static Basis HandRestFrame(Skeleton3D sk, string side)
	{
		Vector3 Rest(string suffix) => sk.GetBoneGlobalRest(sk.FindBone(side + suffix)).Origin;
		return Frame(Rest("MiddleProximal") - Rest("Hand"), Rest("LittleProximal") - Rest("IndexProximal"));
	}

	public static Basis FingerRestFrame(Skeleton3D sk, int bone, int next, Basis handFrame, bool thumb)
	{
		var head = sk.GetBoneGlobalRest(bone).Origin;
		// Les os terminaux du kit FPS ont une longueur artificielle : prolonger le dernier segment réel.
		var direction = next >= 0 ? sk.GetBoneGlobalRest(next).Origin - head
			: head - sk.GetBoneGlobalRest(sk.GetBoneParent(bone)).Origin;
		return Frame(direction, thumb ? handFrame.Z : handFrame.Y);
	}

	public void Transform(Transform3D transform)
	{
		Palm = new Transform3D((transform.Basis * Palm.Basis).Orthonormalized(), transform * Palm.Origin);
	}

	public static WeaponHandPose Capture(Node3D model, string side)
	{
		var sk = FindSkeleton(model);
		if (sk == null) return null;
		var bones = new Dictionary<string, int>();
		for (int i = 0; i < sk.GetBoneCount(); i++)
			bones.TryAdd(Regex.Replace(sk.GetBoneName(i), @"_\d+$", ""), i);
		string prefix = side == "Right" ? "R_" : "L_";
		int Bone(string name) => bones.TryGetValue(prefix + name, out var i) ? i : -1;
		int hand = Bone("wrist"), mid = Bone("middle1"), index = Bone("point1"), little = Bone("pink1");
		if (hand < 0 || mid < 0 || index < 0 || little < 0) return null;
		Vector3 Rest(int i) => sk.GetBoneGlobalRest(i).Origin;
		var restFrame = Frame(Rest(mid) - Rest(hand), Rest(little) - Rest(index));
		var handPose = sk.GetBoneGlobalPose(hand);
		var posedFrame = (handPose.Basis * sk.GetBoneGlobalRest(hand).Basis.Inverse() * restFrame).Orthonormalized();
		var pose = new WeaponHandPose {
			Palm = new Transform3D(posedFrame, handPose.Origin.Lerp(sk.GetBoneGlobalPose(mid).Origin, PalmAlong))
		};
		for (int f = 0; f < FingerNames.Length; f++)
			for (int j = 0; j < 3; j++)
			{
				int bone = Bone(SourceFingers[f] + (j + 1));
				if (bone < 0) continue;
				int next = j < 2 ? Bone(SourceFingers[f] + (j + 2)) : -1;
				var frame = FingerRestFrame(sk, bone, next, restFrame, f == 0);
				var posed = sk.GetBoneGlobalPose(bone).Basis * sk.GetBoneGlobalRest(bone).Basis.Inverse() * frame;
				pose.Fingers[FingerNames[f] + JointName(FingerNames[f], j)] = (posedFrame.Inverse() * posed).Orthonormalized();
			}
		// Même chaîne de transformations que le modèle, y compris son échelle FPS.
		var xf = Transform3D.Identity;
		for (Node node = sk; node != model.GetParent(); node = node.GetParent())
			if (node is Node3D n) xf = n.Transform * xf;
		pose.Transform(xf);
		return pose;
	}

	private static Skeleton3D FindSkeleton(Node root)
	{
		if (root is Skeleton3D sk) return sk;
		foreach (var child in root.GetChildren())
		{
			var found = FindSkeleton(child);
			if (found != null) return found;
		}
		return null;
	}
}
