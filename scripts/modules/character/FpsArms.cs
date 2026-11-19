using Godot;
using System.Linq;
using Openesport.Inventory;

/// <summary>
/// Remplace les bras du kit fps-hands par ceux d'un personnage (peau et manche), pour qu'un joueur voie ses propres bras.
/// Les nouveaux bras sont liés aux mêmes os que ceux du kit : toutes les animations de l'arme continuent de s'appliquer.
/// </summary>
public static class FpsArms
{
	public const string DefaultPath = "res://assets/characters/makehuman_test/fps_arms.glb";

	private static T FindFirst<T>(Node n) where T : Node
	{
		if (n is T t)
			return t;
		foreach (var child in n.GetChildren())
		{
			var found = FindFirst<T>(child);
			if (found != null)
				return found;
		}
		return null;
	}

	private static string BoneKey(string name)
	{
		return System.Text.RegularExpressions.Regex.Replace(name, @"_\d+$", "");
	}

	/// <summary>Nom de l'os de l'arme qui correspond à un os des bras (même nom, ou même nom sans suffixe numérique), sinon null.</summary>
	private static string Resolve(Skeleton3D skeleton, System.Collections.Generic.Dictionary<string, string> targetBones, string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		if (skeleton.FindBone(name) >= 0) return name;
		return targetBones.TryGetValue(BoneKey(name), out var mapped) ? mapped : null;
	}

	/// <summary>Installe les bras du personnage sur une scène d'arme FPS. Retourne false si le modèle est introuvable.</summary>
	public static bool Apply(Node3D weapon, string armsPath = DefaultPath)
	{
		var skeleton = FindFirst<Skeleton3D>(weapon);
		var source = ResourceLoader.Exists(armsPath) ? GD.Load<PackedScene>(armsPath)?.Instantiate<Node3D>() : null;
		if (skeleton == null || source == null)
			return false;

		// Bras d'origine : le maillage skinné fils du squelette
		var oldArms = skeleton.GetChildren().OfType<MeshInstance3D>().FirstOrDefault(m => m.Skin != null && m.Name != "CharacterArms");
		float fov = 70.0f;
		if (oldArms?.GetActiveMaterial(0) is ShaderMaterial oldMaterial && oldMaterial.GetShaderParameter("fov").VariantType == Variant.Type.Float)
			fov = oldMaterial.GetShaderParameter("fov").AsSingle();

		// Les os du kit portent un suffixe numérique qui change d'une arme à l'autre (L_thumb3_00, L_thumb3_01…) :
		// on les retrouve sans ce suffixe
		var targetBones = new System.Collections.Generic.Dictionary<string, string>();
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
			targetBones.TryAdd(BoneKey(skeleton.GetBoneName(i)), skeleton.GetBoneName(i));

		var sourceSkeleton = FindFirst<Skeleton3D>(source);
		foreach (var sourceMesh in sourceSkeleton.GetChildren().OfType<MeshInstance3D>())
		{
			// Liaison par nom d'os : l'ordre des os diffère d'un fichier à l'autre
			var skin = new Skin();
			for (int i = 0; i < sourceMesh.Skin.GetBindCount(); i++)
			{
				string name = sourceMesh.Skin.GetBindName(i);
				if (string.IsNullOrEmpty(name))
					name = sourceSkeleton.GetBoneName(sourceMesh.Skin.GetBindBone(i));
				// Os absent de l'arme (paume, bout de doigt…) : on le rattache à son plus proche parent présent.
				// Surtout ne pas l'omettre : les poids des sommets désignent les liaisons par leur rang, une liaison sautée décalerait toutes les suivantes.
				string target = Resolve(skeleton, targetBones, name);
				for (int b = sourceSkeleton.FindBone(name); target == null && b >= 0; b = sourceSkeleton.GetBoneParent(b))
					target = Resolve(skeleton, targetBones, sourceSkeleton.GetBoneName(b));
				target ??= skeleton.GetBoneName(0);
				skin.AddNamedBind(target, sourceMesh.Skin.GetBindPose(i));
			}

			var arms = new MeshInstance3D
			{
				Name = "CharacterArms",
				Mesh = sourceMesh.Mesh,
				Skin = skin,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
			};
			skeleton.AddChild(arms);

			for (int i = 0; i < sourceMesh.Mesh.GetSurfaceCount(); i++)
			{
				var texture = (sourceMesh.Mesh.SurfaceGetMaterial(i) as BaseMaterial3D)?.AlbedoTexture;
				arms.SetSurfaceOverrideMaterial(i, ItemVisuals.CreateViewmodelTextureMaterial(texture, Colors.White, fov));
			}
		}

		if (oldArms != null)
			oldArms.Visible = false;
		source.QueueFree();
		return true;
	}
}
