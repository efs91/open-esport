using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// La zone vue dans le monde : un mur cylindrique translucide (bleu) à la limite de la zone sûre, et un mur blanc plus discret pour la zone suivante quand elle
	/// est annoncée. Ce ne sont que des cylindres mis à l'échelle (rayon, hauteur) : aucun maillage n'est reconstruit quand la zone se réduit.
	/// </summary>
	public partial class ZoneWall : Node3D
	{
		private const float WallHeight = 1400f;
		private MeshInstance3D _current, _next;
		private ShaderMaterial _currentMat, _nextMat;

		private const string WallShader = @"
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec4 color : source_color = vec4(0.25, 0.6, 1.0, 1.0);
uniform float strength = 0.22;
void fragment() {
	float band = 0.5 + 0.5 * sin(UV.x * 120.0 + TIME * 0.6) * sin(UV.y * 90.0 - TIME * 0.9);
	float edge = 1.0 - smoothstep(0.0, 0.5, abs(UV.y - 0.5));
	ALBEDO = color.rgb;
	ALPHA = strength * (0.55 + 0.45 * band) * (0.4 + 0.6 * edge);
}";

		public override void _Ready()
		{
			var shader = new Shader { Code = WallShader };
			(_current, _currentMat) = Build(shader, new Color(0.25f, 0.6f, 1f), 0.30f);
			(_next, _nextMat) = Build(shader, new Color(1f, 1f, 1f), 0.10f);
			AddChild(_current); AddChild(_next);
			_current.Visible = false; _next.Visible = false;
		}

		private static (MeshInstance3D, ShaderMaterial) Build(Shader shader, Color color, float strength)
		{
			var mat = new ShaderMaterial { Shader = shader };
			mat.SetShaderParameter("color", color);
			mat.SetShaderParameter("strength", strength);
			var mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 1f, RadialSegments = 128, Rings = 1, CapTop = false, CapBottom = false };
			var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4000f };
			return (mi, mat);
		}

		/// <summary>Place les murs. Rayon 0 ou moins : le mur correspondant disparaît.</summary>
		public void SetZone(bool show, float cx, float cz, float r, bool nextVisible, float nx, float nz, float nr, float baseY)
		{
			if (_current == null) return;
			Place(_current, show && r > 0.5f, cx, cz, r, baseY);
			Place(_next, show && nextVisible && nr > 0.5f, nx, nz, nr, baseY);
		}

		private static void Place(MeshInstance3D wall, bool visible, float x, float z, float r, float baseY)
		{
			wall.Visible = visible;
			if (!visible) return;
			wall.Scale = new Vector3(r, WallHeight, r);
			wall.GlobalPosition = new Vector3(x, baseY + WallHeight * 0.4f, z);
		}
	}
}
