using Godot;

/// <summary>
/// Décor de montagnes lointaines autour de la carte (add-on « Raymarching Mountains » : un shader sur une grande boîte retournée, très léger).
/// Le terrain de la carte s'arrête net ; sans décor on voit le gris du ciel sous l'horizon. Les montagnes naissent hors de la carte
/// (<see cref="ClearRadius"/>) et se perdent dans la brume vers le bleu de l'horizon.
/// </summary>
public partial class MountainBackdrop : MeshInstance3D
{
	/// <summary>Hauteur du sol du décor (un peu sous le bord du terrain, vers 230 m).</summary>
	[Export] public float BaseHeight = 228f;
	/// <summary>Rayon (autour du centre de la carte) à l'intérieur duquel le décor reste plat, caché par le terrain.</summary>
	[Export] public float ClearRadius = 770f;
	[Export] public float RampLength = 450f;
	[Export] public float PeakHeight = 380f;
	[Export] public float ViewDistance = 3200f;
	[Export] public Color FogColor = new Color(0.68f, 0.74f, 0.82f);

	public override void _Ready()
	{
		var noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Seed = 35, Frequency = 0.015f,
			FractalOctaves = 6, FractalLacunarity = 2.2f, FractalGain = 0.55f };
		var grad = new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { Colors.Black, Colors.White } };
		var tex = new NoiseTexture2D { Width = 512, Height = 512, Noise = noise, ColorRamp = grad, Seamless = true };

		// brume : couleur des montagnes proches vers le bleu de l'horizon, puis transparent
		var fog = new Gradient { Offsets = new[] { 0f, 0.7f, 1f },
			Colors = new[] { new Color(0.26f, 0.34f, 0.3f, 1f), new Color(0.5f, 0.6f, 0.72f, 0.75f), new Color(FogColor, 0f) } };
		var fogTex = new GradientTexture1D { Gradient = fog };

		var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://addons/raymarched_mountains/src/shaders/RaymarchingMountains.gdshader") };
		mat.SetShaderParameter("noise", tex);
		mat.SetShaderParameter("fogGradient", fogTex);
		mat.SetShaderParameter("uv_scale", 0.07f);
		mat.SetShaderParameter("terrain_scale", PeakHeight);
		mat.SetShaderParameter("MAX_STEPS", 80);
		mat.SetShaderParameter("MAX_DIST", ViewDistance * 0.55f);
		mat.SetShaderParameter("terrain_color", new Color(0.42f, 0.5f, 0.38f));
		mat.SetShaderParameter("shadow_color", new Color(0.08f, 0.11f, 0.1f));
		mat.SetShaderParameter("light_dir", new Vector3(1f, 1f, -1f));
		mat.SetShaderParameter("fogStart", ViewDistance * 0.2f);
		mat.SetShaderParameter("fogEnd", ViewDistance * 0.55f);
		mat.SetShaderParameter("clear_radius", ClearRadius);
		mat.SetShaderParameter("clear_ramp", RampLength);
		MaterialOverride = mat;
		Mesh = new BoxMesh { Size = new Vector3(ViewDistance, ViewDistance, ViewDistance), FlipFaces = true };
		CastShadow = ShadowCastingSetting.Off;
		ExtraCullMargin = 16384f;
		TopLevel = true;
		Position = new Vector3(0f, BaseHeight, 0f);
	}
}
