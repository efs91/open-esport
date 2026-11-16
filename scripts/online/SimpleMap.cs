using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Racine d'une carte simple des modes de jeu (sans Terrain3D) : un sol plat de <see cref="Size"/> mètres, centré sur l'origine, dont le dessus est à y = 0.
	/// Les obstacles sont des <see cref="CoverBox"/> et les points d'attention des <see cref="MapPoint"/>, enfants de cette scène.
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class SimpleMap : Node3D
	{
		private Vector2 _size = new Vector2(100, 100);
		private Color _groundColor = new Color(0.32f, 0.36f, 0.3f);
		private MeshInstance3D _mesh;
		private CollisionShape3D _shape;

		[Export] public Vector2 Size { get => _size; set { _size = value; Rebuild(); } }
		[Export] public Color GroundColor { get => _groundColor; set { _groundColor = value; Rebuild(); } }

		public override void _EnterTree()
		{
			var body = new StaticBody3D { Name = "_Ground" };
			_mesh = new MeshInstance3D { Name = "_Mesh" };
			_shape = new CollisionShape3D { Name = "_Shape" };
			body.AddChild(_mesh); body.AddChild(_shape);
			AddChild(body, false, InternalMode.Front);
			Rebuild();
		}

		private void Rebuild()
		{
			if (_mesh == null || !IsInstanceValid(_mesh)) return;
			_mesh.Mesh = new BoxMesh { Size = new Vector3(_size.X, 1f, _size.Y) };
			_mesh.Position = new Vector3(0, -0.5f, 0);
			_mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = _groundColor, Roughness = 1f };
			_shape.Shape = new BoxShape3D { Size = new Vector3(_size.X, 1f, _size.Y) };
			_shape.Position = new Vector3(0, -0.5f, 0);
		}
	}
}
