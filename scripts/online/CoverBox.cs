using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Boîte pleine d'une carte simple (mur, caisse, abri) : visible, avec collision pour le jeu, et exportée comme obstacle dans le fichier .world (boîte alignée sur les axes).
	/// L'origine du nœud est le centre du dessous de la boîte : on la pose sur le sol. Taille réglable dans l'inspecteur (rotation par quarts de tour conseillée).
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class CoverBox : StaticBody3D
	{
		private Vector3 _size = new Vector3(4, 3, 4);
		private Color _color = new Color(0.45f, 0.5f, 0.58f);
		private MeshInstance3D _mesh;
		private CollisionShape3D _shape;

		[Export] public Vector3 Size { get => _size; set { _size = value; Rebuild(); } }
		[Export] public Color Color { get => _color; set { _color = value; Rebuild(); } }

		public override void _EnterTree()
		{
			_mesh = new MeshInstance3D { Name = "_Mesh" };
			_shape = new CollisionShape3D { Name = "_Shape" };
			AddChild(_mesh, false, InternalMode.Front);
			AddChild(_shape, false, InternalMode.Front);
			Rebuild();
		}

		private void Rebuild()
		{
			if (_mesh == null || !IsInstanceValid(_mesh)) return;
			_mesh.Mesh = new BoxMesh { Size = _size };
			_mesh.Position = new Vector3(0, _size.Y / 2f, 0);
			_mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = _color, Roughness = 0.9f };
			_shape.Shape = new BoxShape3D { Size = _size };
			_shape.Position = new Vector3(0, _size.Y / 2f, 0);
		}

		/// <summary>Boîte englobante alignée sur les axes, dans le repère du monde.</summary>
		public Aabb WorldBounds()
		{
			var local = new Aabb(new Vector3(-_size.X / 2f, 0, -_size.Z / 2f), _size);
			return GlobalTransform * local;
		}
	}
}
