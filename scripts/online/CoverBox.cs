using System.Collections.Generic;
using Godot;

namespace Openesport.Online
{
	/// <summary>Matières des cartes simples : une texture répétée (triplanaire : sans déformation, quelle que soit la taille de la boîte) teintée par la couleur de la boîte.</summary>
	public enum Surface
	{
		Unie = 0, Beton = 1, Asphalte = 2, Roche = 3, Terre = 4, Herbe = 5, Metal = 6, Caisse = 7, Sable = 8, Bois = 9,
	}

	public static class Surfaces
	{
		private const string Terrain = "res://maps/frontiere01/textures/";

		private static readonly Dictionary<Surface, (string path, float tile)> Textures = new()
		{
			[Surface.Beton] = (Terrain + "concrete_alb_ht.png", 3.5f),
			[Surface.Asphalte] = (Terrain + "asphalt_alb_ht.png", 3f),
			[Surface.Roche] = (Terrain + "cliff_alb_ht.png", 5f),
			[Surface.Terre] = (Terrain + "dirt_road_alb_ht.png", 4f),
			[Surface.Herbe] = ("res://assets/textures/grass_ground_d.jpg", 3f),
			[Surface.Metal] = (Terrain + "metal_alb_ht.png", 3f),
			[Surface.Sable] = (Terrain + "sand_alb_ht.png", 4f),
			[Surface.Bois] = ("res://assets/textures/wood1.jpg", 2f),
			[Surface.Caisse] = ("res://assets/textures/Box.png", 1f),
		};

		private static readonly Dictionary<(Surface, Color, float), StandardMaterial3D> Cache = new();

		/// <summary>Matériau d'une matière teintée. <paramref name="tile"/> : taille en mètres d'un motif (0 = la valeur d'origine de la matière).</summary>
		public static StandardMaterial3D Material(Surface surface, Color tint, float tile = 0f)
		{
			var key = (surface, tint, tile);
			if (Cache.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
			var mat = new StandardMaterial3D { AlbedoColor = tint, Roughness = 0.92f };
			if (Textures.TryGetValue(surface, out var t) && ResourceLoader.Exists(t.path))
			{
				mat.AlbedoTexture = GD.Load<Texture2D>(t.path);
				if (surface != Surface.Caisse)
				{
					// triplanaire dans le repère du monde : la matière se prolonge d'une boîte à l'autre
					mat.Uv1Triplanar = true;
					mat.Uv1WorldTriplanar = true;
					mat.Uv1Scale = Vector3.One / (tile > 0f ? tile : t.tile);
				}
				mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
			}
			Cache[key] = mat;
			return mat;
		}
	}

	/// <summary>
	/// Boîte pleine d'une carte simple (mur, bâtiment, caisse, abri) : visible, avec collision pour le jeu, et exportée comme obstacle dans le fichier .world (boîte alignée sur les axes).
	/// L'origine du nœud est le centre du dessous de la boîte : on la pose sur le sol. Taille réglable dans l'inspecteur (rotation par quarts de tour conseillée).
	/// <see cref="Surface"/> donne la matière (béton, roche, herbe…) ; <see cref="Style"/> « Bâtiment » ajoute socle, corniche et fenêtres (décor sans collision) ;
	/// décocher <see cref="Solid"/> en fait une simple dalle de sol : visible mais sans collision et sans obstacle exporté.
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class CoverBox : StaticBody3D
	{
		public enum StyleEnum { Simple = 0, Batiment = 1 }

		private Vector3 _size = new Vector3(4, 3, 4);
		private Color _color = new Color(0.45f, 0.5f, 0.58f);
		private Surface _surface = Surface.Unie;
		private StyleEnum _style = StyleEnum.Simple;
		private bool _solid = true;
		private MeshInstance3D _mesh;
		private CollisionShape3D _shape;
		private Node3D _decor;

		[Export] public Vector3 Size { get => _size; set { _size = value; Rebuild(); } }
		[Export] public Color Color { get => _color; set { _color = value; Rebuild(); } }
		[Export] public Surface Surface { get => _surface; set { _surface = value; Rebuild(); } }
		[Export] public StyleEnum Style { get => _style; set { _style = value; Rebuild(); } }
		[Export] public bool Solid { get => _solid; set { _solid = value; Rebuild(); } }

		public override void _EnterTree()
		{
			_mesh = new MeshInstance3D { Name = "_Mesh" };
			_shape = new CollisionShape3D { Name = "_Shape" };
			_decor = new Node3D { Name = "_Decor" };
			AddChild(_mesh, false, InternalMode.Front);
			AddChild(_shape, false, InternalMode.Front);
			AddChild(_decor, false, InternalMode.Front);
			Rebuild();
		}

		private void Rebuild()
		{
			if (_mesh == null || !IsInstanceValid(_mesh)) return;
			_mesh.Mesh = new BoxMesh { Size = _size };
			_mesh.Position = new Vector3(0, _size.Y / 2f, 0);
			_mesh.MaterialOverride = _surface == Surface.Unie
				? new StandardMaterial3D { AlbedoColor = _color, Roughness = 0.9f }
				: Surfaces.Material(_surface, _color);
			_mesh.CastShadow = _solid ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
			_shape.Shape = new BoxShape3D { Size = _size };
			_shape.Position = new Vector3(0, _size.Y / 2f, 0);
			_shape.Disabled = !_solid;
			CollisionLayer = _solid ? 1u : 0u;
			BuildDecor();
		}

		// ------------------------------------------------------------------ décor des bâtiments (sans collision)

		private void BuildDecor()
		{
			foreach (var c in _decor.GetChildren()) { _decor.RemoveChild(c); c.QueueFree(); }
			if (_style != StyleEnum.Batiment || !_solid || _size.Y < 3f) return;

			var dark = _color.Darkened(0.28f);
			// socle (le bas du mur, plus sombre) et corniche (la dalle du toit, qui déborde)
			AddBox(new Vector3(_size.X + 0.2f, 0.7f, _size.Z + 0.2f), new Vector3(0, 0.35f, 0), dark, true);
			AddBox(new Vector3(_size.X + 0.7f, 0.45f, _size.Z + 0.7f), new Vector3(0, _size.Y + 0.1f, 0), dark, true);
			AddBox(new Vector3(_size.X + 0.2f, 0.25f, _size.Z + 0.2f), new Vector3(0, _size.Y - 0.35f, 0), _color.Darkened(0.12f), true);

			// fenêtres : des rectangles sombres, deux rangées sur les grands bâtiments
			var st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);
			int quads = 0;
			float[] rows = _size.Y >= 5.5f ? new[] { 1.9f, 3.9f } : new[] { 1.9f };
			void Face(float length, Vector3 origin, Vector3 along, Vector3 normal)
			{
				if (length < 5f) return;
				const float w = 1.6f, h = 1.45f, step = 3.6f;
				int n = (int)((length - 3.2f) / step) + 1;
				float used = (n - 1) * step + w;
				float start = (length - used) / 2f;
				for (int i = 0; i < n; i++)
					foreach (var y in rows)
					{
						var a = origin + along * (start + i * step) + Vector3.Up * y + normal * 0.03f;
						var b = a + along * w;
						var c = b + Vector3.Up * h;
						var d = a + Vector3.Up * h;
						st.SetNormal(normal); st.SetUV(new Vector2(0, 1)); st.AddVertex(a);
						st.SetNormal(normal); st.SetUV(new Vector2(1, 1)); st.AddVertex(b);
						st.SetNormal(normal); st.SetUV(new Vector2(1, 0)); st.AddVertex(c);
						st.SetNormal(normal); st.SetUV(new Vector2(0, 1)); st.AddVertex(a);
						st.SetNormal(normal); st.SetUV(new Vector2(1, 0)); st.AddVertex(c);
						st.SetNormal(normal); st.SetUV(new Vector2(0, 0)); st.AddVertex(d);
						quads++;
					}
			}
			float hx = _size.X / 2f, hz = _size.Z / 2f;
			Face(_size.X, new Vector3(-hx, 0, hz), Vector3.Right, Vector3.Back);          // face sud (+Z), vue de l'extérieur
			Face(_size.X, new Vector3(hx, 0, -hz), Vector3.Left, Vector3.Forward);         // face nord (-Z)
			Face(_size.Z, new Vector3(hx, 0, hz), Vector3.Forward, Vector3.Right);         // face est (+X)
			Face(_size.Z, new Vector3(-hx, 0, -hz), Vector3.Back, Vector3.Left);           // face ouest (-X)
			if (quads == 0) return;
			var glass = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.07f, 0.1f, 0.14f),
				Metallic = 0.7f,
				Roughness = 0.18f,
				EmissionEnabled = true,
				Emission = new Color(0.03f, 0.05f, 0.08f),
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			};
			_decor.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = glass, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
		}

		private void AddBox(Vector3 size, Vector3 position, Color color, bool textured)
		{
			var mat = textured && _surface != Surface.Unie ? Surfaces.Material(_surface, color) : new StandardMaterial3D { AlbedoColor = color, Roughness = 0.9f };
			_decor.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = position, MaterialOverride = mat });
		}

		/// <summary>Boîte englobante alignée sur les axes, dans le repère du monde.</summary>
		public Aabb WorldBounds()
		{
			var local = new Aabb(new Vector3(-_size.X / 2f, 0, -_size.Z / 2f), _size);
			return GlobalTransform * local;
		}
	}
}
