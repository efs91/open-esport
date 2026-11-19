using Godot;
using System;
using System.Collections.Generic;

namespace Openesport.Inventory
{
	/// <summary>
	/// Construction des modèles 3D des objets : version posée au sol et version montée sur l'arme en vue FPS.
	/// </summary>
	public static class ItemVisuals
	{
		public const string OpticalCenterName = "OpticalCenter";

		// Même projection que addons/fps-hands/shader.gdshader, pour que le viseur reste aligné
		// avec l'arme quel que soit le FOV de la caméra (zoom en visée).
		private const string ViewmodelShaderCode = @"
shader_type spatial;
render_mode blend_mix, RENDER_MODES, diffuse_burley, specular_schlick_ggx;

uniform float fov = 75.0;
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform float roughness : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

void vertex() {
	float scale = 1.0 / tan(fov * 0.5 * PI / 180.0);
	PROJECTION_MATRIX[0][0] = scale / (VIEWPORT_SIZE.x / VIEWPORT_SIZE.y);
	PROJECTION_MATRIX[1][1] = -scale;
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX.xyz, 1.0);
}

void fragment() {
	vec4 tex = texture(texture_albedo, UV);
	ALBEDO = albedo.rgb * tex.rgb;
	METALLIC = metallic;
	ROUGHNESS = roughness;
	ALPHA_LINE
}
";

		private static Shader _viewmodelOpaque;
		private static Shader _viewmodelGlass;
		private static Shader _viewmodelUnshaded;

		/// <summary>Variante non éclairée (point rouge du viseur).</summary>
		private static Shader ViewmodelUnshadedShader()
		{
			_viewmodelUnshaded ??= new Shader
			{
				Code = ViewmodelShaderCode
					.Replace("RENDER_MODES", "depth_draw_opaque, cull_back, unshaded")
					.Replace("ALPHA_LINE", "")
			};
			return _viewmodelUnshaded;
		}

		private static Shader ViewmodelShader(bool glass)
		{
			if (glass)
			{
				_viewmodelGlass ??= new Shader
				{
					Code = ViewmodelShaderCode
						.Replace("RENDER_MODES", "depth_draw_never, cull_disabled")
						.Replace("ALPHA_LINE", "ALPHA = albedo.a;")
				};
				return _viewmodelGlass;
			}

			_viewmodelOpaque ??= new Shader
			{
				Code = ViewmodelShaderCode
					.Replace("RENDER_MODES", "depth_draw_opaque, cull_back")
					.Replace("ALPHA_LINE", "")
			};
			return _viewmodelOpaque;
		}

		// ------------------------------------------------------------------
		// Outils
		// ------------------------------------------------------------------

		/// <summary>
		/// Boîte englobante des maillages visibles sous <paramref name="root"/>, exprimée dans son repère local.
		/// Fonctionne aussi hors de l'arbre de scène.
		/// </summary>
		public static Aabb? ComputeLocalAabb(Node root, Func<MeshInstance3D, bool> filter = null)
		{
			Aabb box = default;
			bool has = false;
			Accumulate(root, Transform3D.Identity, true, filter, ref box, ref has);
			return has ? box : null;
		}

		private static void Accumulate(Node node, Transform3D parentXf, bool isRoot, Func<MeshInstance3D, bool> filter, ref Aabb box, ref bool has)
		{
			var xf = parentXf;
			if (node is Node3D n3)
			{
				if (!isRoot)
				{
					if (!n3.Visible)
						return;
					xf = parentXf * n3.Transform;
				}

				if (node is MeshInstance3D mi && mi.Mesh != null && (filter == null || filter(mi)))
				{
					var a = xf * mi.Mesh.GetAabb();
					box = has ? box.Merge(a) : a;
					has = true;
				}
			}

			foreach (var child in node.GetChildren())
				Accumulate(child, xf, false, filter, ref box, ref has);
		}

		private static void ForEachMesh(Node node, Action<MeshInstance3D> action)
		{
			if (node is MeshInstance3D mi)
				action(mi);
			foreach (var child in node.GetChildren())
				ForEachMesh(child, action);
		}

		private static bool IsGlass(Material material)
		{
			if (material == null)
				return false;
			string name = (material.ResourceName ?? "").ToLowerInvariant();
			if (name.Contains("glass") || name.Contains("pane"))
				return true;
			return material is BaseMaterial3D bm && bm.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
		}

		private static bool HasGlassSurface(MeshInstance3D mi)
		{
			for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
			{
				if (IsGlass(mi.GetActiveMaterial(i)))
					return true;
			}
			return false;
		}

		private static readonly System.Collections.Generic.Dictionary<string, PackedScene> SceneCache = new();

		/// <summary>Charge une scène une seule fois et la garde : sinon Godot la libère dès qu'on n'en a plus de référence et la recharge (300 à 700 ms) au prochain objet posé.</summary>
		public static PackedScene LoadCached(string path)
		{
			if (string.IsNullOrEmpty(path)) return null;
			if (SceneCache.TryGetValue(path, out var ps)) return ps;
			ps = GD.Load<PackedScene>(path);
			if (ps != null) SceneCache[path] = ps;
			return ps;
		}

		private static Node3D InstantiateModel(ItemDefinition def, int tier = 0)
		{
			if ((def.Type == ItemType.Shield || def.Type == ItemType.Consumable) && (string.IsNullOrEmpty(def.ScenePath) || !ResourceLoader.Exists(def.ScenePath)))
				return ItemProceduralModels.Build(def, tier);
			if (def.Type == ItemType.Shield)
			{
				// un seul modèle de bouclier : on le teinte selon son niveau (blanc, bleu, violet, rouge)
				var shield = LoadCached(def.ScenePath).Instantiate<Node3D>();
				TintModel(shield, ItemTierStyle.ColorOf(tier), tier);
				return shield;
			}
			PackedScene scene = ResourceLoader.Exists(def.ScenePath) ? LoadCached(def.ScenePath) : null;
			if (scene != null)
				return scene.Instantiate<Node3D>();

			// Modèle introuvable ou pas encore importé : cube de remplacement
			GD.PrintErr($"[ItemVisuals] Modèle introuvable pour '{def.Id}' : {def.ScenePath}");
			return new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = new Vector3(0.25f, 0.25f, 0.25f) },
				MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Magenta }
			};
		}

		// ------------------------------------------------------------------
		// Modèle posé au sol
		// ------------------------------------------------------------------

		/// <summary>
		/// Pose sur un bidon (petit soin) une coque grise qui recouvre la partie du liquide et qu'on fait descendre avec
		/// <c>level</c> (1 = plein, 0 = vide) : le bidon semble se vider. Retourne les matériaux à animer.
		/// </summary>
		public static System.Collections.Generic.List<ShaderMaterial> AddEmptyOverlay(Node root, float liquidFraction = 0.5f)
		{
			var list = new System.Collections.Generic.List<ShaderMaterial>();
			var shader = new Shader { Code = @"
shader_type spatial;
uniform float y_bottom;
uniform float y_top;
uniform float level = 1.0;
varying float vy;
void vertex() { vy = VERTEX.y; VERTEX += NORMAL * 0.006; }
void fragment() {
	float fill_y = y_bottom + level * (y_top - y_bottom);
	if (vy < fill_y || vy > y_top) discard;
	ALBEDO = vec3(0.07, 0.075, 0.085);
	METALLIC = 0.7;
	ROUGHNESS = 0.4;
}" };
			foreach (var node in root.FindChildren("*", "MeshInstance3D", true, false))
			{
				if (node is not MeshInstance3D mi || mi.Mesh == null || mi.GetParent() == null) continue;
				var box = mi.Mesh.GetAabb();
				var mat = new ShaderMaterial { Shader = shader };
				mat.SetShaderParameter("y_bottom", box.Position.Y);
				mat.SetShaderParameter("y_top", box.Position.Y + box.Size.Y * liquidFraction);
				mat.SetShaderParameter("level", 1.0f);
				var overlay = new MeshInstance3D { Name = "EmptyOverlay", Mesh = mi.Mesh, MaterialOverride = mat, Transform = mi.Transform, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
				mi.GetParent().AddChild(overlay);
				list.Add(mat);
			}
			return list;
		}

		/// <summary>Hauteur (en mètres, repère de <paramref name="root"/>) d'un modèle déjà dans l'arbre.</summary>
		public static float HeightOf(Node3D root)
		{
			float top = float.MinValue, bottom = float.MaxValue;
			var inv = root.GlobalTransform.AffineInverse();
			foreach (var node in root.FindChildren("*", "VisualInstance3D", true, false))
			{
				if (node is not VisualInstance3D v) continue;
				var b = inv * v.GlobalTransform * v.GetAabb();
				top = Mathf.Max(top, b.End.Y); bottom = Mathf.Min(bottom, b.Position.Y);
			}
			return top > bottom ? top - bottom : 0.2f;
		}

		/// <summary>Multiplie la couleur de tous les matériaux d'un modèle par une teinte, avec une légère lueur pour les niveaux élevés.</summary>
		public static void TintModel(Node root, Color tint, int tier)
		{
			if (tier <= 0) return;
			foreach (var node in root.FindChildren("*", "MeshInstance3D", true, false))
			{
				if (node is not MeshInstance3D mi || mi.Mesh == null) continue;
				for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
				{
					if (mi.GetActiveMaterial(i) is not StandardMaterial3D src) continue;
					var m = (StandardMaterial3D)src.Duplicate();
					m.AlbedoColor = new Color(tint.R, tint.G, tint.B, m.AlbedoColor.A);
					m.EmissionEnabled = true;
					m.Emission = tint;
					m.EmissionEnergyMultiplier = 0.25f + 0.1f * tier;
					mi.SetSurfaceOverrideMaterial(i, m);
				}
			}
		}

		/// <summary>
		/// Construit le modèle d'un objet posé au sol : centré sur l'origine, base à y = 0.
		/// </summary>
		public static Node3D BuildGroundModel(ItemDefinition def, int tier = 0)
		{
			var model = InstantiateModel(def, tier);
			var pivot = new Node3D { Name = "Pivot" };
			var holder = new Node3D { Name = "Visual" };
			holder.AddChild(pivot);
			pivot.AddChild(model);

			if (def.Type == ItemType.Weapon)
			{
				StripViewmodel(model);
				// Couchée sur le flanc
				pivot.Basis = new Basis(Vector3.Back, Mathf.Pi / 2);
			}
			else if (def.Type == ItemType.Shield && !string.IsNullOrEmpty(def.ScenePath))
				pivot.Basis = new Basis(Vector3.Right, -Mathf.Pi / 2f);   // le plastron est posé à plat, face vers le haut

			var local = ComputeLocalAabb(pivot);
			if (local == null)
				return holder;

			var box = pivot.Transform * local.Value;
			if (def.GroundSize > 0)
			{
				float longest = Mathf.Max(box.Size.X, Mathf.Max(box.Size.Y, box.Size.Z));
				if (longest > 0.0001f)
				{
					pivot.Scale = Vector3.One * (def.GroundSize / longest);
					box = pivot.Transform * local.Value;
				}
			}

			var center = box.GetCenter();
			pivot.Position = new Vector3(-center.X, -box.Position.Y, -center.Z);
			return holder;
		}

		/// <summary>
		/// Arme tenue par un personnage : à l'endroit, à l'échelle réelle, centrée sur l'origine (canon vers -Z).
		/// <paramref name="bounds"/> est la boîte englobante dans le repère du résultat.
		/// </summary>
		public static Node3D BuildHeldModel(ItemDefinition def, out Aabb bounds)
			=> BuildHeldModel(def, out bounds, out _, out _);

		public static Node3D BuildHeldModel(ItemDefinition def, out Aabb bounds, out WeaponHandPose right, out WeaponHandPose left)
		{
			var model = InstantiateModel(def);
			var holder = new Node3D { Name = "Held" };
			var pivot = new Node3D { Name = "Pivot" };
			holder.AddChild(pivot);
			pivot.AddChild(model);
			// Relever une vraie pose de prise avant de supprimer les bras et les animations FPS.
			WeaponHandPose.SampleIdle(model);
			right = WeaponHandPose.Capture(model, "Right");
			// Le couteau et le C19 ont un idle à une main ; leur main gauche FPS est hors de l'arme.
			left = def.Id is "knife" or "c19" ? null : WeaponHandPose.Capture(model, "Left");
			StripViewmodel(model);
			pivot.Basis = new Basis(Vector3.Up, Mathf.Pi); // les modèles FPS ont le canon vers +Z
			if (def.Id == "knife") pivot.Basis = new Basis(Vector3.Up, Mathf.Pi / 2f); // lame le long de X dans le modèle
			bounds = new Aabb(Vector3.Zero, new Vector3(0.1f, 0.1f, 0.5f));
			var local = ComputeLocalAabb(pivot);
			if (local == null) { right = null; left = null; return holder; }
			float longest = Mathf.Max(local.Value.Size.X, Mathf.Max(local.Value.Size.Y, local.Value.Size.Z));
			float length = def.Id switch { "ak" => 0.95f, "lmg63" => 1.1f, "smg45" => 0.6f, "c19" => 0.24f, "p2020" => 0.22f, "sawnoff" => 0.75f, "sniper" => 1.3f, "knife" => 0.3f, _ => def.GroundSize > 0 ? def.GroundSize : 0.9f };
			if (longest > 0.0001f) pivot.Scale = Vector3.One * (length / longest);
			var box = pivot.Transform * local.Value;
			pivot.Position = -box.GetCenter();
			bounds = new Aabb(-box.Size / 2f, box.Size);
			right?.Transform(pivot.Transform);
			left?.Transform(pivot.Transform);
			return holder;
		}

		/// <summary>
		/// Retire d'une scène d'arme FPS tout ce qui ne sert qu'en vue à la première personne
		/// (bras, animations, son) et remplace le shader de vue FPS par un matériau standard.
		/// </summary>
		private static void StripViewmodel(Node3D model)
		{
			var toFree = new List<Node>();
			CollectViewmodelOnly(model, toFree);
			foreach (var node in toFree)
			{
				node.GetParent()?.RemoveChild(node);
				node.Free();
			}

			var cache = new Dictionary<Material, Material>();
			ForEachMesh(model, mi =>
			{
				if (mi.Mesh == null)
					return;
				mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
				for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
				{
					if (mi.GetActiveMaterial(i) is not ShaderMaterial sm)
						continue;
					if (!cache.TryGetValue(sm, out var standard))
					{
						standard = ToStandardMaterial(sm);
						cache[sm] = standard;
					}
					mi.SetSurfaceOverrideMaterial(i, standard);
				}
			});
		}

		private static void CollectViewmodelOnly(Node node, List<Node> result)
		{
			foreach (var child in node.GetChildren())
			{
				if (child is Skeleton3D || child is AnimationMixer || child is AudioStreamPlayer3D)
					result.Add(child);
				else
					CollectViewmodelOnly(child, result);
			}
		}

		private static StandardMaterial3D ToStandardMaterial(ShaderMaterial sm)
		{
			var mat = new StandardMaterial3D();

			var albedo = sm.GetShaderParameter("albedo");
			if (albedo.VariantType == Variant.Type.Color)
				mat.AlbedoColor = albedo.AsColor();

			if (sm.GetShaderParameter("texture_albedo").As<Texture2D>() is { } albedoTex)
				mat.AlbedoTexture = albedoTex;

			if (sm.GetShaderParameter("texture_normal").As<Texture2D>() is { } normalTex)
			{
				mat.NormalEnabled = true;
				mat.NormalTexture = normalTex;
			}

			if (sm.GetShaderParameter("texture_roughness").As<Texture2D>() is { } roughTex)
			{
				mat.RoughnessTexture = roughTex;
				mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
			}

			var roughness = sm.GetShaderParameter("roughness");
			if (roughness.VariantType == Variant.Type.Float)
				mat.Roughness = roughness.AsSingle();

			return mat;
		}

		// ------------------------------------------------------------------
		// Viseur monté sur l'arme (vue FPS)
		// ------------------------------------------------------------------

		/// <summary>
		/// Construit le modèle d'un viseur pour la vue FPS. L'avant du viseur est orienté vers +Z,
		/// son point de fixation est à l'origine, et un nœud <see cref="OpticalCenterName"/> marque le centre de la lentille.
		/// </summary>
		public static Node3D BuildViewmodelOptic(ItemDefinition def, float fov)
		{
			var model = InstantiateModel(def);
			var root = new Node3D { Name = "Optic" };
			root.AddChild(model);
			// L'avant du viseur est tourné vers +Z (l'avant de l'arme)
			model.Basis = new Basis(Vector3.Up, Mathf.DegToRad(def.ModelYawDegrees));

			// Modèle sans échelle ni point de fixation connus : on le met à la taille voulue
			// et on pose son pied sur l'origine, centré sur la lentille
			if (def.ModelSize > 0 && ComputeLocalAabb(root) is { } raw)
			{
				float longest = Mathf.Max(raw.Size.X, Mathf.Max(raw.Size.Y, raw.Size.Z));
				if (longest > 0.0001f)
					model.Scale = Vector3.One * (def.ModelSize / longest);

				var body = ComputeLocalAabb(root).Value;
				var lens = ComputeLocalAabb(root, HasGlassSurface) ?? body;
				model.Position = new Vector3(-lens.GetCenter().X, -body.Position.Y, -body.GetCenter().Z);
			}

			var glass = ComputeLocalAabb(root, HasGlassSurface) ?? ComputeLocalAabb(root);

			var cache = new Dictionary<Material, Material>();
			ForEachMesh(model, mi =>
			{
				if (mi.Mesh == null)
					return;
				mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
				for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
				{
					var source = mi.GetActiveMaterial(i);
					if (source == null)
						continue;
					if (!cache.TryGetValue(source, out var converted))
					{
						converted = ToViewmodelMaterial(source, fov);
						cache[source] = converted;
					}
					mi.SetSurfaceOverrideMaterial(i, converted);
				}
			});

			var center = new Marker3D { Name = OpticalCenterName };
			root.AddChild(center);
			if (glass != null)
				center.Position = glass.Value.GetCenter();
			center.Position += def.CenterOffset;

			if (def.RedDot)
			{
				var dotMaterial = new ShaderMaterial { Shader = ViewmodelUnshadedShader() };
				dotMaterial.SetShaderParameter("fov", fov);
				dotMaterial.SetShaderParameter("albedo", new Color(1.0f, 0.08f, 0.05f));
				var dot = new MeshInstance3D
				{
					Name = "RedDot",
					Mesh = new SphereMesh { Radius = 0.0012f, Height = 0.0024f, RadialSegments = 12, Rings = 6 },
					Position = center.Position,
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
				};
				dot.SetSurfaceOverrideMaterial(0, dotMaterial);
				root.AddChild(dot);
			}

			// Cale entre le rail de l'arme (origine) et le pied du viseur, qui est remonté d'autant
			if (def.MountRise > 0)
			{
				model.Position += Vector3.Up * def.MountRise;
				center.Position += Vector3.Up * def.MountRise;
				var riserMaterial = new ShaderMaterial { Shader = ViewmodelShader(false) };
				riserMaterial.SetShaderParameter("fov", fov);
				riserMaterial.SetShaderParameter("albedo", new Color(0.03f, 0.035f, 0.04f));
				riserMaterial.SetShaderParameter("roughness", 0.6f);
				var riser = new MeshInstance3D
				{
					Name = "Riser",
					Mesh = new BoxMesh { Size = new Vector3(0.022f, def.MountRise, 0.07f) },
					Position = new Vector3(0, def.MountRise * 0.5f, 0),
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
				};
				riser.SetSurfaceOverrideMaterial(0, riserMaterial);
				root.AddChild(riser);
			}

			return root;
		}

		/// <summary>
		/// Matériau de peau ou de tissu pour la vue FPS : même projection que les armes (FOV propre à la vue FPS),
		/// mais éclairé normalement. Le shader du kit supprime la lumière ambiante, ce qui rend noir tout ce qui
		/// n'a pas ses textures d'origine.
		/// </summary>
		public static ShaderMaterial CreateViewmodelTextureMaterial(Texture2D albedo, Color color, float fov)
		{
			var mat = new ShaderMaterial { Shader = ViewmodelShader(false) };
			mat.SetShaderParameter("fov", fov);
			mat.SetShaderParameter("albedo", color);
			mat.SetShaderParameter("roughness", 0.8f);
			mat.SetShaderParameter("metallic", 0.0f);
			if (albedo != null)
				mat.SetShaderParameter("texture_albedo", albedo);
			return mat;
		}

		private static ShaderMaterial ToViewmodelMaterial(Material source, float fov)
		{
			bool glass = IsGlass(source);
			var mat = new ShaderMaterial { Shader = ViewmodelShader(glass) };
			mat.SetShaderParameter("fov", fov);

			if (source is BaseMaterial3D bm)
			{
				var color = bm.AlbedoColor;
				if (glass)
					color.A = 0.12f;
				mat.SetShaderParameter("albedo", color);
				mat.SetShaderParameter("roughness", bm.Roughness);
				mat.SetShaderParameter("metallic", bm.Metallic);
				if (bm.AlbedoTexture != null)
					mat.SetShaderParameter("texture_albedo", bm.AlbedoTexture);
			}
			else if (glass)
			{
				mat.SetShaderParameter("albedo", new Color(0.1f, 0.3f, 0.4f, 0.12f));
			}

			return mat;
		}
	}
}
