using System.Collections.Generic;
using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Balle visible en multijoueur, dans l'esprit d'Apex Legends : la balle suit la même trajectoire que sur le serveur
	/// (250 m/s, gravité) et laisse derrière elle une traînée de fumée qui reste un moment (~0,6 s) : elle s'élargit, devient
	/// irrégulière et s'estompe, pour qu'on voie la trajectoire. Un peu de couleur de la munition près de la balle.
	/// Elle s'arrête sur le relief et les obstacles (étincelle).
	/// Le serveur seul décide des dégâts : ceci n'est que l'affichage.
	/// </summary>
	public partial class ShotTracer : Node3D
	{
		/// <summary>Mêmes valeurs que le serveur (réglage reçu à la connexion).</summary>
		public static float Speed => OpenEsport.Core.GameBalance.BulletSpeed;
		public static float Gravity => OpenEsport.Core.GameBalance.BulletGravity;
		/// <summary>Durée de vie d'un point du ruban : la traînée reste visible ce temps-là.</summary>
		public const float TrailSeconds = 0.6f;
		private const float MaxFlightSeconds = 3f;
		private const float HeadWidth = 0.1f;
		private const float SmokeSpread = 1.0f;   // largeur ajoutée par la fumée à l'extrémité la plus ancienne

		private struct Point { public Vector3 Pos; public double Born; public float Dist; }

		private readonly List<Point> _points = new();
		private Vector3 _pos, _vel;
		private float _flight;
		private float _size = 1;
		private MeshInstance3D _bolt;
		private bool _stopped;
		private Color _color;
		private ImmediateMesh _mesh;
		private double _now;
		private float _seed;

		private static Shader _sharedShader;

		/// <summary>Compile le shader de traînée avant le premier tir.</summary>
		public static void WarmUp(Node parent) => Spawn(parent, new Vector3(0, -500f, 0), Vector3.Down, Colors.White);

		private const string SmokeShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
uniform float seed = 0.0;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
	vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
void fragment() {
	float across = abs(UV.x * 2.0 - 1.0);
	float soft = 1.0 - smoothstep(0.0, 1.0, across);
	soft *= soft;
	float n = vnoise(vec2(UV.y * 0.45 + seed, UV.x * 3.0)) * 0.6 + vnoise(vec2(UV.y * 1.3 + seed * 3.1, UV.x * 6.0)) * 0.4;
	ALBEDO = COLOR.rgb;
	ALPHA = clamp(COLOR.a * soft * (0.25 + 1.1 * n), 0.0, 1.0);
}
";

		/// <summary>Couleur de traînée selon le type de munition (1 = 9 mm, 2 = fusil, 3 = cartouches), comme les couleurs de munitions d'Apex.</summary>
		public static Color ColorFor(int ammoType) => ammoType switch
		{
			1 => new Color(1.0f, 0.62f, 0.15f),   // légères : orange
			2 => new Color(0.45f, 1.0f, 0.45f),   // lourdes : vert
			3 => new Color(1.0f, 0.28f, 0.22f),   // cartouches : rouge
			4 => new Color(0.55f, 0.8f, 1.0f),    // précision : bleu clair
			_ => new Color(1.0f, 0.9f, 0.5f),
		};

		/// <param name="speed">Vitesse de la balle (m/s) ; 0 = vitesse commune. Chaque arme peut avoir la sienne (sniper).</param>
		public static void Spawn(Node parent, Vector3 from, Vector3 direction, Color color, float speed = 0f, ShotAppearance appearance = ShotAppearance.Standard)
		{
			if (!GodotObject.IsInstanceValid(parent)) return;
			var t = new ShotTracer { _pos = from, _vel = direction.Normalized() * (speed > 0f ? speed : Speed), _color = color };
			if (appearance == ShotAppearance.WalkerMachineGun || appearance == ShotAppearance.WalkerCannon)
			{
				bool cannon = appearance == ShotAppearance.WalkerCannon;
				t._size = cannon ? 5f : 2.4f;
				t._color = cannon ? new Color("#ff9148") : new Color("#ffe598");
			}
			parent.AddChild(t);
		}

		public override void _Ready()
		{
			TopLevel = true;
			GlobalTransform = Transform3D.Identity;
			if (_size > 1)
			{
				float radius = HeadWidth * _size * .5f;
				_bolt = new MeshInstance3D {
					Position = _pos, Mesh = new SphereMesh { Radius = radius, Height = 2 * radius, RadialSegments = 8, Rings = 4 },
					MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = _color },
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
				};
				AddChild(_bolt);
			}
			_seed = GD.Randf() * 100f;
			_sharedShader ??= new Shader { Code = SmokeShader };   // un seul shader pour toutes les balles : il n'est compilé qu'une fois
			var _smoke = new ShaderMaterial { Shader = _sharedShader };
			_smoke.SetShaderParameter("seed", _seed);
			_mesh = new ImmediateMesh();
			AddChild(new MeshInstance3D
			{
				Mesh = _mesh,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				ExtraCullMargin = 1000f,
				MaterialOverride = _smoke,
			});
			_now = Time.GetTicksMsec() / 1000.0;
			_points.Add(new Point { Pos = _pos, Born = _now, Dist = 0f });
		}

		public override void _PhysicsProcess(double delta)
		{
			if (_stopped) return;
			float dt = (float)delta;
			_flight += dt;
			_vel += Vector3.Down * Gravity * dt;
			Vector3 step = _vel * dt;
			var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(_pos, _pos + step));
			bool stop = hit.Count > 0;
			_pos = stop ? hit["position"].AsVector3() : _pos + step;
			_points.Add(new Point { Pos = _pos, Born = Time.GetTicksMsec() / 1000.0, Dist = _points.Count > 0 ? _points[^1].Dist + _points[^1].Pos.DistanceTo(_pos) : 0f });
			if (stop) { _stopped = true; Spark(); }
			else if (_flight >= MaxFlightSeconds) _stopped = true;
		}

		public override void _Process(double delta)
		{
			_now = Time.GetTicksMsec() / 1000.0;
			while (_points.Count > 0 && _now - _points[0].Born > TrailSeconds) _points.RemoveAt(0);
			if (_points.Count == 0 && _stopped) { QueueFree(); return; }
			Rebuild();
		}

		/// <summary>Reconstruit le ruban (face à la caméra) : large et brillant près de la balle, fin et transparent à l'arrière.</summary>
		private void Rebuild()
		{
			_mesh.ClearSurfaces();
			if (_points.Count < 1) return;
			var cam = GetViewport().GetCamera3D();
			Vector3 camPos = cam != null ? cam.GlobalPosition : Vector3.Zero;

			// la tête avance entre deux pas de physique : on l'extrapole pour un mouvement fluide
			Vector3 head = _pos;
			if (!_stopped) head += _vel * (float)(Engine.GetPhysicsInterpolationFraction() / Engine.PhysicsTicksPerSecond);

			if (_bolt != null) { _bolt.Visible = !_stopped; _bolt.Position = head; }

			int n = _points.Count + (_stopped ? 0 : 1);
			if (n < 2) return;
			_mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
			var smoke = new Color(0.82f, 0.84f, 0.88f);
			for (int i = 0; i < n; i++)
			{
				bool isHead = i == _points.Count;
				Vector3 p = isHead ? head : _points[i].Pos;
				Vector3 a = i > 0 ? (i - 1 == _points.Count ? head : _points[i - 1].Pos) : p;
				Vector3 b = i < n - 1 ? (i + 1 == _points.Count ? head : _points[i + 1].Pos) : p;
				Vector3 tangent = (b - a);
				if (tangent.LengthSquared() < 1e-8f) tangent = _vel;
				tangent = tangent.Normalized();
				Vector3 side = tangent.Cross((camPos - p).Normalized());
				side = side.LengthSquared() < 1e-8f ? Vector3.Right : side.Normalized();

				double age = isHead ? 0.0 : _now - _points[i].Born;
				float life = Mathf.Clamp(1f - (float)(age / TrailSeconds), 0f, 1f);
				float old = 1f - life;
				// la fumée s'élargit en vieillissant ; fine et dense à la tête
				float width = HeadWidth * _size + SmokeSpread * Mathf.Pow(old, 1.2f);
				float dist = isHead ? (_points.Count > 0 ? _points[^1].Dist + _points[^1].Pos.DistanceTo(head) : 0f) : _points[i].Dist;
				// presque blanc-gris, avec la couleur de la munition près de la balle
				Color c = smoke.Lerp(_color, Mathf.Pow(life, 3f) * 0.85f);
				c.A = Mathf.Pow(life, 1.3f) * 0.6f * Mathf.SmoothStep(0f, 1f, Mathf.Clamp(dist / 5f, 0f, 1f)); // la fumée naît au canon
				_mesh.SurfaceSetColor(c); _mesh.SurfaceSetUV(new Vector2(0f, dist)); _mesh.SurfaceAddVertex(p + side * width * 0.5f);
				_mesh.SurfaceSetColor(c); _mesh.SurfaceSetUV(new Vector2(1f, dist)); _mesh.SurfaceAddVertex(p - side * width * 0.5f);
			}
			_mesh.SurfaceEnd();
		}

		private void Spark()
		{
			var spark = new MeshInstance3D
			{
				Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f, RadialSegments = 8, Rings = 4 },
				MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = _color.Lerp(Colors.White, 0.5f) },
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				TopLevel = true,
			};
			AddChild(spark);
			spark.GlobalPosition = _pos;
			var tween = CreateTween();
			tween.TweenProperty(spark, "scale", Vector3.One * 0.1f, 0.15f);
			tween.TweenCallback(Callable.From(spark.QueueFree));
		}
	}
}
