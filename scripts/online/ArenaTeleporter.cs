using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Téléporteur d'arène : celui qui entre dans le disque ressort au nœud enfant « Sortie » (position et orientation : on la tourne dans l'éditeur
	/// pour choisir la direction de sortie). Une paire A ↔ B se fait avec deux téléporteurs dont chaque sortie est devant l'autre.
	/// L'exporteur de carte l'écrit dans le fichier .world. Visuel : cadre sombre, anneau violet lumineux, voile tourbillonnant.
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class ArenaTeleporter : Node3D
	{
		private float _radius = 1.0f;
		private Color _color = new Color(0.7f, 0.3f, 1f);
		[Export(PropertyHint.Range, "0.5,3,0.1")] public float Radius { get => _radius; set { _radius = value; Rebuild(); } }
		[Export] public Color Tint { get => _color; set { _color = value; Rebuild(); } }

		private Node3D _visual;
		private MeshInstance3D _veil;
		private double _clock;

		public override void _Ready()
		{
			_visual = new Node3D { Name = "_Visual" };
			AddChild(_visual, false, InternalMode.Front);
			if (Engine.IsEditorHint() && GetNodeOrNull("Sortie") == null && GetTree().EditedSceneRoot != null)
			{
				var exit = new Marker3D { Name = "Sortie", Position = new Vector3(0, 0, -3) };
				AddChild(exit);
				exit.Owner = GetTree().EditedSceneRoot;
			}
			Rebuild();
		}

		private const string VeilShader = @"shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
uniform vec4 tint : source_color = vec4(0.7, 0.3, 1.0, 1.0);
void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float r = length(p);
	float a = atan(p.y, p.x);
	float swirl = sin(a * 5.0 + r * 10.0 - TIME * 4.0) * 0.5 + 0.5;
	float edge = smoothstep(1.0, 0.75, r);
	float core = smoothstep(0.6, 0.0, r) * 0.6;
	ALBEDO = tint.rgb * (0.6 + swirl * 1.6);
	ALPHA = edge * (0.35 + swirl * 0.45 + core);
}";

		private void Rebuild()
		{
			if (_visual == null || !IsInstanceValid(_visual)) return;
			foreach (var ch in _visual.GetChildren()) { _visual.RemoveChild(ch); ch.QueueFree(); }
			var dark = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.13f), Metallic = 0.85f, Roughness = 0.3f };
			var glow = new StandardMaterial3D { AlbedoColor = _color, EmissionEnabled = true, Emission = _color, EmissionEnergyMultiplier = 5f };
			// socle
			_visual.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _radius + 0.3f, BottomRadius = _radius + 0.45f, Height = 0.16f, RadialSegments = 48 }, Position = new Vector3(0, 0.08f, 0), MaterialOverride = dark });
			_visual.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius - 0.06f, OuterRadius = _radius + 0.06f, Rings = 48, RingSegments = 8 }, Position = new Vector3(0, 0.17f, 0), MaterialOverride = glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			// anneau vertical (le portail)
			_visual.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius + 0.05f, OuterRadius = _radius + 0.28f, Rings = 48, RingSegments = 10 }, Position = new Vector3(0, _radius + 0.55f, 0), RotationDegrees = new Vector3(90, 0, 0), MaterialOverride = dark });
			_visual.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius + 0.02f, OuterRadius = _radius + 0.08f, Rings = 48, RingSegments = 6 }, Position = new Vector3(0, _radius + 0.55f, 0), RotationDegrees = new Vector3(90, 0, 0), MaterialOverride = glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			// voile tourbillonnant au centre du portail et sur le sol
			var veilMat = new ShaderMaterial { Shader = new Shader { Code = VeilShader } };
			veilMat.SetShaderParameter("tint", _color);
			_veil = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(_radius * 2.1f, _radius * 2.1f) }, Position = new Vector3(0, _radius + 0.55f, 0), MaterialOverride = veilMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			_visual.AddChild(_veil);
			_visual.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(_radius * 2f, _radius * 2f) }, Position = new Vector3(0, 0.18f, 0), RotationDegrees = new Vector3(-90, 0, 0), MaterialOverride = veilMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			_visual.AddChild(new OmniLight3D { LightColor = _color, LightEnergy = 2.2f, OmniRange = 7f, Position = new Vector3(0, 1.4f, 0), ShadowEnabled = false });
		}

		public override void _Process(double delta) { _clock += delta; }

		public Teleporter ToTeleporter()
		{
			var g = GlobalPosition;
			var exit = GetNodeOrNull<Node3D>("Sortie");
			var d = exit?.GlobalPosition ?? g + new Vector3(0, 0, -3);
			float yaw = exit?.GlobalRotation.Y ?? GlobalRotation.Y;
			return new Teleporter { Pos = new Vec3(g.X, g.Y, g.Z), Dest = new Vec3(d.X, d.Y, d.Z), Radius = _radius, Yaw = yaw };
		}
	}
}
