using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using OpenEsport.Core;

/// <summary>
/// Outil (lancé par tools/hitbox/bake_body_poses.gd) : fait jouer le vrai CharacterAnimator (animations, IK des bras avec chaque arme,
/// morphologie) et relève la position des articulations pour chaque pose que le serveur doit connaître.
/// Écrit netcode/OpenEsport.Core/Game/body_poses.bin, lu par BodyHitbox (serveur et client).
/// </summary>
public partial class HitboxBaker : Node
{
	public override async void _Ready()
	{
		var layout = BodyPoses.Layout;
		var holds = new List<Openesport.Inventory.ItemDefinition> { null };
		foreach (byte id in BodyPoses.HoldWeapons) holds.Add(Openesport.Inventory.ItemDatabase.Get(ItemDefs.Get(id).Name));

		using var data = new MemoryStream();
		using var w = new BinaryWriter(data);
		float walkLength = 0, walkRef = 0, runLength = 0, runRef = 0, crouchLength = 0, crouchRef = 0;
		foreach (float gender in BodyPoses.Genders)
		foreach (float height in BodyPoses.Heights)
		foreach (var hold in holds)
		{
			var animator = new CharacterAnimator { Appearance = new CharacterAppearance { Gender = gender, Height = height } };
			AddChild(animator);
			await Frames(3);
			animator.Hold(hold);
			var player = animator.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
			var arms = animator.FindChild("ArmsPlayer", true, false) as AnimationPlayer;
			var sk = animator.Skeleton;
			// lue en dernier dans la chaîne des modificateurs : la pose finale (morphologie, IK des bras)
			var probe = new PoseProbe { Animator = animator };
			sk.AddChild(probe);
			foreach (var (clip, frames) in layout)
			{
				string name = clip switch
				{
					BodyClip.Idle => "idle", BodyClip.Walk => "walk", BodyClip.Run => "run", BodyClip.Slide => "slide",
					BodyClip.CrouchIdle => "crouch_idle", BodyClip.CrouchWalk => "crouch_walk", _ => "jump_loop"
				};
				float speed = clip == BodyClip.Walk ? 2.5f : clip == BodyClip.Run ? 6f : clip == BodyClip.CrouchWalk ? 1.5f : 0f;
				animator.Crouching = clip == BodyClip.CrouchIdle || clip == BodyClip.CrouchWalk;
				bool floor = clip != BodyClip.Air;
				// règle l'IK et les bras comme en jeu (bras de garde sans arme), puis fige le clip à la bonne phase
				animator.Tick(new Vector3(0, 0, -speed), floor, clip == BodyClip.Slide, 0.016);
				animator.Tick(new Vector3(0, 0, -speed), floor, clip == BodyClip.Slide, 0.016);
				var anim = player.GetAnimation(name);
				if (clip == BodyClip.Walk) { walkLength = (float)anim.Length; walkRef = anim.GetMeta("ground_speed").AsSingle(); }
				if (clip == BodyClip.Run) { runLength = (float)anim.Length; runRef = anim.GetMeta("ground_speed").AsSingle(); }
				if (clip == BodyClip.CrouchWalk) { crouchLength = (float)anim.Length; crouchRef = anim.GetMeta("ground_speed").AsSingle(); }
				for (int f = 0; f < frames; f++)
				{
					float t = clip == BodyClip.Slide ? (float)anim.Length * .5f : (float)anim.Length * f / frames;
					player.Play(name, 0);
					player.Seek(t, true);
					player.SpeedScale = 0;
					if (arms != null) arms.SpeedScale = 0;
					await Frames(3);
					foreach (var p in probe.Captured)
					{
						w.Write((short)Mathf.RoundToInt(p.X * 1000));
						w.Write((short)Mathf.RoundToInt(p.Y * 1000));
						w.Write((short)Mathf.RoundToInt(p.Z * 1000));
					}
				}
			}
			animator.QueueFree();
			await Frames(1);
		}
		w.Flush();
		string path = ProjectSettings.GlobalizePath("res://netcode/OpenEsport.Core/Game/body_poses.bin");
		using (var file = File.Create(path))
		using (var o = new BinaryWriter(file))
		{
			o.Write(BodyPoses.Magic);
			o.Write(walkLength); o.Write(walkRef); o.Write(runLength); o.Write(runRef); o.Write(crouchLength); o.Write(crouchRef);
			o.Write(data.ToArray());
		}
		GD.Print($"HITBOX poses écrites : {path} ({data.Length} octets) walk {walkLength:0.00}s @{walkRef:0.00} m/s, run {runLength:0.00}s @{runRef:0.00} m/s");
		GetTree().Quit();
	}

	private async System.Threading.Tasks.Task Frames(int n)
	{
		for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	/// <summary>Modificateur placé après tous les autres : relève les articulations une fois la pose finie.</summary>
	private partial class PoseProbe : SkeletonModifier3D
	{
		public CharacterAnimator Animator;
		public List<Vector3> Captured = new();
		public override void _ProcessModificationWithDelta(double delta)
		{
			var sk = GetSkeleton();
			if (sk == null || Animator == null) return;
			Captured = new List<Vector3>(Sample(Animator, sk));
		}
	}

	/// <summary>Articulations dans le repère du joueur (pieds à l'origine, regard vers -Z), dans l'ordre de BodyPoses.JointBones.</summary>
	private static IEnumerable<Vector3> Sample(CharacterAnimator animator, Skeleton3D sk)
	{
		Vector3 At(string bone) => animator.ToLocal(sk.ToGlobal(sk.GetBoneGlobalPose(sk.FindBone(bone)).Origin));
		var head = sk.GetBoneGlobalPose(sk.FindBone("Head"));
		var headUp = animator.GlobalBasis.Inverse() * (sk.GlobalBasis * head.Basis.Y).Normalized();
		foreach (var joint in BodyPoses.JointBones)
		{
			if (joint == "HeadTop") { yield return At("Head") + headUp * .2f; continue; }
			if (joint.EndsWith("ToeTip"))
			{
				string side = joint.StartsWith("Left") ? "Left" : "Right";
				Vector3 toes = At(side + "Toes"), foot = At(side + "Foot");
				var dir = new Vector3(toes.X - foot.X, 0, toes.Z - foot.Z).Normalized();
				yield return toes + dir * .07f;
				continue;
			}
			yield return At(joint);
		}
	}
}
