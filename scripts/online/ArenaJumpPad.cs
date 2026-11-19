using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Tremplin d'arène : celui qui marche dessus est projeté en cloche jusqu'au nœud enfant « Cible » (à déplacer dans l'éditeur).
	/// Le vol dure <see cref="FlightTime"/> secondes. L'exporteur de carte l'écrit dans le fichier .world (le serveur et le client simulent le même saut).
	/// Visuel : disque orange lumineux, chevrons qui pulsent ; dans l'éditeur, une ligne pointillée montre la trajectoire.
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class ArenaJumpPad : Node3D
	{
		private float _radius = 1.2f, _time = 1.1f;
		[Export(PropertyHint.Range, "0.6,3,0.1")] public float Radius { get => _radius; set { _radius = value; Rebuild(); } }
		[Export(PropertyHint.Range, "0.4,3,0.05")] public float FlightTime { get => _time; set { _time = value; Rebuild(); } }

		private Node3D _visual;
		private MeshInstance3D _arrows;
		private MeshInstance3D _path;
		private double _clock;

		public Marker3D Target
		{
			get
			{
				var t = GetNodeOrNull<Marker3D>("Cible");
				if (t == null && Engine.IsEditorHint() && IsInsideTree() && GetTree().EditedSceneRoot != null)
				{
					t = new Marker3D { Name = "Cible", Position = new Vector3(0, 4, -6) };
					AddChild(t);
					t.Owner = GetTree().EditedSceneRoot;
				}
				return t;
			}
		}

		public override void _Ready()
		{
			_visual = new Node3D { Name = "_Visual" };
			AddChild(_visual, false, InternalMode.Front);
			Rebuild();
			SetProcess(true);
		}

		private static StandardMaterial3D Glow(Color c, float energy) => new StandardMaterial3D
		{
			AlbedoColor = c, EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = energy, Roughness = 0.4f, Metallic = 0.2f,
		};

		private void Rebuild()
		{
			if (_visual == null || !IsInstanceValid(_visual)) return;
			foreach (var ch in _visual.GetChildren()) { _visual.RemoveChild(ch); ch.QueueFree(); }
			var orange = new Color(1f, 0.55f, 0.12f);
			// socle sombre, anneau lumineux, disque intérieur
			_visual.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _radius + 0.25f, BottomRadius = _radius + 0.35f, Height = 0.12f, RadialSegments = 40 }, Position = new Vector3(0, 0.06f, 0),
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.13f, 0.15f), Metallic = 0.8f, Roughness = 0.35f } });
			_visual.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius - 0.08f, OuterRadius = _radius + 0.08f, Rings = 40, RingSegments = 8 }, Position = new Vector3(0, 0.13f, 0), MaterialOverride = Glow(orange, 4f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			_visual.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _radius - 0.1f, BottomRadius = _radius - 0.1f, Height = 0.02f, RadialSegments = 40 }, Position = new Vector3(0, 0.125f, 0),
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.12f, 0.03f), EmissionEnabled = true, Emission = orange, EmissionEnergyMultiplier = 0.6f, Roughness = 0.3f } });
			// chevrons tournés vers la cible
			var st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);
			for (int k = 0; k < 3; k++)
			{
				float z0 = 0.45f - k * 0.42f, w = _radius * 0.55f, t = 0.14f;
				void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); st.AddVertex(a); st.AddVertex(c); st.AddVertex(d); }
				Quad(new Vector3(-w, 0, z0), new Vector3(0, 0, z0 - 0.35f), new Vector3(0, 0, z0 - 0.35f - t), new Vector3(-w, 0, z0 - t));
				Quad(new Vector3(0, 0, z0 - 0.35f), new Vector3(w, 0, z0), new Vector3(w, 0, z0 - t), new Vector3(0, 0, z0 - 0.35f - t));
			}
			st.GenerateNormals();
			_arrows = new MeshInstance3D { Mesh = st.Commit(), Position = new Vector3(0, 0.14f, 0), MaterialOverride = Glow(new Color(1f, 0.8f, 0.35f), 6f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			_visual.AddChild(_arrows);
			_visual.AddChild(new OmniLight3D { LightColor = orange, LightEnergy = 1.6f, OmniRange = 5f, Position = new Vector3(0, 0.6f, 0), ShadowEnabled = false });
			AimArrows();
			if (Engine.IsEditorHint()) DrawPath();
		}

		private void AimArrows()
		{
			var t = GetNodeOrNull<Marker3D>("Cible");
			if (_arrows == null || t == null) return;
			var d = t.GlobalPosition - GlobalPosition;
			d.Y = 0;
			if (d.LengthSquared() > 0.01f) _arrows.Rotation = new Vector3(0, Mathf.Atan2(-d.X, -d.Z) - GlobalRotation.Y, 0);
		}

		/// <summary>Trajectoire (éditeur) : la même cloche que la simulation (gravité du jeu).</summary>
		private void DrawPath()
		{
			var t = GetNodeOrNull<Marker3D>("Cible");
			if (t == null) return;
			if (_path != null && IsInstanceValid(_path)) _path.QueueFree();
			var im = new ImmediateMesh();
			im.SurfaceBegin(Mesh.PrimitiveType.Lines);
			Vector3 a = GlobalPosition, b = t.GlobalPosition;
			float T = Mathf.Max(0.3f, _time), g = GameBalance.Gravity;
			var v = new Vector3((b.X - a.X) / T, (b.Y - a.Y + 0.5f * g * T * T) / T, (b.Z - a.Z) / T);
			Vector3 prev = Vector3.Zero;
			for (int i = 1; i <= 24; i++)
			{
				float s = T * i / 24f;
				var p = new Vector3(v.X * s, v.Y * s - 0.5f * g * s * s, v.Z * s);
				var local = GlobalTransform.Basis.Inverse() * p;
				if (i % 2 == 1) { im.SurfaceAddVertex(GlobalTransform.Basis.Inverse() * prev + new Vector3(0, 0.9f, 0)); im.SurfaceAddVertex(local + new Vector3(0, 0.9f, 0)); }
				prev = p;
			}
			im.SurfaceEnd();
			_path = new MeshInstance3D { Mesh = im, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.6f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
			_visual.AddChild(_path);
		}

		public override void _Process(double delta)
		{
			_clock += delta;
			if (_arrows?.MaterialOverride is StandardMaterial3D m) m.EmissionEnergyMultiplier = 3f + 3f * (0.5f + 0.5f * Mathf.Sin((float)_clock * 5f));
			if (Engine.IsEditorHint() && ((int)(_clock * 4) != (int)((_clock - delta) * 4))) { AimArrows(); DrawPath(); }
		}

		public JumpPad ToPad()
		{
			var g = GlobalPosition;
			var t = GetNodeOrNull<Marker3D>("Cible")?.GlobalPosition ?? g + new Vector3(0, 4, -6);
			return new JumpPad { Pos = new Vec3(g.X, g.Y, g.Z), Target = new Vec3(t.X, t.Y, t.Z), Radius = _radius, Time = _time };
		}
	}
}
