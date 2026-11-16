using System.Collections.Generic;
using Godot;

/// <summary>
/// Les mains nues du joueur, vues à la première personne : le personnage lui-même (peau, manches) dont on ne garde que les
/// bras et les mains, placé sous la caméra. Il joue la garde de boxe au repos et la boxe quand on frappe.
/// Affiché seulement quand aucune arme n'est en main.
/// </summary>
public partial class FistsViewmodel : Node3D
{
	/// <summary>Le joueur frappe : les bras jouent la boxe.</summary>
	public bool Attacking { get => _animator != null && _animator.Attacking; set { if (_animator != null) _animator.Attacking = value; } }

	/// <summary>Position du personnage dans le repère de la caméra (ses pieds sont à cette position) : les yeux sont à la caméra.</summary>
	public Vector3 Offset = new Vector3(0f, -1.93f, -0.32f);

	private CharacterAnimator _animator;

	// pas la clavicule (« Shoulder ») : le bord du bras au niveau de l'épaule resterait visible en bas de l'écran
	private static readonly string[] ArmParts = { "UpperArm", "LowerArm", "Hand", "Thumb", "Index", "Middle", "Ring", "Little" };

	/// <summary>Lance un coup tout de suite ; il se joue en entier même si on lâche le clic.</summary>
	public void Punch() => _animator?.RequestPunch();

	/// <summary>Soin en cours : les mains lèvent un flacon à la bouche (voir CharacterAnimator.SetUsing).</summary>
	public void SetUsing(bool on, Openesport.Inventory.ItemDefinition def) => _animator?.SetUsing(on, def, true);

	public override void _Ready()
	{
		Position = Offset;
		_animator = new CharacterAnimator { Name = "Body", Appearance = CharacterAppearance.Current };
		AddChild(_animator);
		KeepOnlyArms(_animator);
	}

	public override void _Process(double delta)
	{
		if (_animator != null && Visible)
			_animator.Tick(Vector3.Zero, true, false, delta);
	}

	/// <summary>Remplace chaque maillage du personnage par le même, réduit aux triangles qui appartiennent aux bras (et aux mains).</summary>
	private static void KeepOnlyArms(Node root)
	{
		foreach (var node in root.FindChildren("*", "MeshInstance3D", true, false))
		{
			if (node is not MeshInstance3D mi || mi.Mesh is not ArrayMesh src || mi.Skin == null) continue;
			var skeleton = mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton);
			if (skeleton == null) continue;
			mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

			// indices de l'« os » dans la peau (skin) qui sont des os de bras
			var armBinds = new HashSet<int>();
			var lowerBinds = new HashSet<int>(); // avant-bras, main, doigts : ce qui reste visible
			for (int b = 0; b < mi.Skin.GetBindCount(); b++)
			{
				string name = mi.Skin.GetBindName(b);
				if (string.IsNullOrEmpty(name)) name = skeleton.GetBoneName(mi.Skin.GetBindBone(b));
				if ((name.StartsWith("Left") || name.StartsWith("Right")) && IsArmPart(name))
				{
					armBinds.Add(b);
					if (!name.Contains("UpperArm")) lowerBinds.Add(b);
				}
			}

			// Position de l'avant-bras dans le repère des sommets : le maillage garde sa pose d'origine alors que les os sont en pose
			// en T, donc on prend la pose de liaison de la peau. On coupe le bras net un peu après le coude : le haut du bras et
			// l'épaule, qui reviennent vers la caméra, resteraient visibles (et déchiquetés) en bas de l'écran.
			Vector3 BindOrigin(string bone)
			{
				for (int b = 0; b < mi.Skin.GetBindCount(); b++)
				{
					string name = mi.Skin.GetBindName(b);
					if (string.IsNullOrEmpty(name)) name = skeleton.GetBoneName(mi.Skin.GetBindBone(b));
					if (name == bone) return mi.Skin.GetBindPose(b).AffineInverse().Origin;
				}
				return Vector3.Zero;
			}
			var leftArm = (elbow: BindOrigin("LeftLowerArm"), wrist: BindOrigin("LeftHand"));
			var rightArm = (elbow: BindOrigin("RightLowerArm"), wrist: BindOrigin("RightHand"));

			var dst = new ArrayMesh { BlendShapeMode = src.BlendShapeMode };
			for (int i = 0; i < src.GetBlendShapeCount(); i++) dst.AddBlendShape(src.GetBlendShapeName(i));
			for (int s = 0; s < src.GetSurfaceCount(); s++)
			{
				var arrays = src.SurfaceGetArrays(s);
				var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
				var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
				var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
				var index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
				if (vertices.Length == 0 || bones.Length == 0 || index.Length == 0) continue;
				int perVertex = bones.Length / vertices.Length;

				// un sommet est « bras » si la majorité de son poids (≥ 0,75) vient d'os de bras : bords nets, pas de lambeaux d'épaule
				bool IsArmVertex(int v)
				{
					float arm = 0f, total = 0f;
					for (int k = 0; k < perVertex; k++)
					{
						float w = weights[v * perVertex + k];
						total += w;
						if (armBinds.Contains(bones[v * perVertex + k])) arm += w;
					}
					return total > 0f && arm / total >= 0.75f;
				}

				var kept = new List<int>();
				for (int t = 0; t + 2 < index.Length; t += 3)
					if (IsArmVertex(index[t]) && IsArmVertex(index[t + 1]) && IsArmVertex(index[t + 2]))
					{
						kept.Add(index[t]); kept.Add(index[t + 1]); kept.Add(index[t + 2]);
					}
				if (kept.Count == 0) continue;

				// Transparence le long de l'avant-bras : invisible au coude et au-dessus (haut du bras, épaule), pleine à partir de
				// FadeTo. Pas de bord net : quand le bras part en avant pendant un coup, on ne voit aucune coupure.
				var colors = new Color[vertices.Length];
				for (int v = 0; v < vertices.Length; v++)
				{
					var (elbow, wrist) = vertices[v].X >= 0f ? leftArm : rightArm; // le côté gauche du modèle est +X
					Vector3 axis = wrist - elbow;
					float length2 = axis.LengthSquared();
					float along = length2 < 1e-8f ? 1f : (vertices[v] - elbow).Dot(axis) / length2;
					colors[v] = new Color(1f, 1f, 1f, Mathf.Clamp((along - FadeFrom) / (FadeTo - FadeFrom), 0f, 1f));
				}
				arrays[(int)Mesh.ArrayType.Color] = colors;
				arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
				var blend = src.SurfaceGetBlendShapeArrays(s);
				dst.AddSurfaceFromArrays(src.SurfaceGetPrimitiveType(s), arrays, blend);
				dst.SurfaceSetMaterial(dst.GetSurfaceCount() - 1, src.SurfaceGetMaterial(s));
			}

			if (dst.GetSurfaceCount() == 0) mi.Visible = false; // cheveux, yeux, jambes, chaussures… : rien à garder
			else
			{
				mi.Mesh = dst;
				// un matériau posé sur toute l'instance passe avant ceux des surfaces
				if (mi.MaterialOverride != null) mi.MaterialOverride = WithVertexAlpha(mi.MaterialOverride);
				else
					for (int i = 0; i < dst.GetSurfaceCount(); i++)
						mi.SetSurfaceOverrideMaterial(i, WithVertexAlpha(mi.GetActiveMaterial(i)));
			}
		}
	}

	/// <summary>Avancement le long de l'avant-bras (0 = coude, 1 = poignet) où le bras commence à apparaître, puis devient plein.</summary>
	private const float FadeFrom = 0.10f, FadeTo = 0.50f;

	/// <summary>Rend le matériau transparent selon l'alpha des sommets (le fondu calculé ci-dessus).</summary>
	private static Material WithVertexAlpha(Material material)
	{
		if (material is not BaseMaterial3D standard) return material;
		var copy = (BaseMaterial3D)standard.Duplicate();
		copy.VertexColorUseAsAlbedo = true;
		copy.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		return copy;
	}

	private static bool IsArmPart(string bone)
	{
		foreach (var part in ArmParts) if (bone.Contains(part)) return true;
		return false;
	}
}
