using System.Collections.Generic;
using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Trajectoire d'une grenade tenue en main, comme dans Apex Legends : un ruban lumineux depuis la main jusqu'au premier impact, et un cercle au sol
	/// qui montre le rayon de l'effet. Elle est calculée comme sur le serveur (mêmes vitesse, gravité et élan du joueur), sur le même relief et les mêmes obstacles ;
	/// seul le premier impact est montré (les rebonds suivants dépendent des surfaces).
	/// </summary>
	public partial class GrenadePreview : Node3D
	{
		private MeshInstance3D _ribbon, _ring;
		private ImmediateMesh _mesh;
		private StandardMaterial3D _ribbonMat, _ringMat;
		private TorusMesh _torus;
		private readonly List<Vector3> _points = new();

		public override void _Ready()
		{
			_mesh = new ImmediateMesh();
			_ribbonMat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				BlendMode = BaseMaterial3D.BlendModeEnum.Add,
				VertexColorUseAsAlbedo = true,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				NoDepthTest = true,
			};
			_ribbon = new MeshInstance3D { Mesh = _mesh, MaterialOverride = _ribbonMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 200f, TopLevel = true };
			AddChild(_ribbon);
			_ringMat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				BlendMode = BaseMaterial3D.BlendModeEnum.Add,
				NoDepthTest = false,
			};
			_torus = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1f, Rings = 48, RingSegments = 6 };
			_ring = new MeshInstance3D { Mesh = _torus, MaterialOverride = _ringMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Visible = false };
			AddChild(_ring);
			Visible = false;
		}

		private static Color ColorOf(byte id) => id switch
		{
			ItemIds.GrenadeFire => new Color(1f, 0.45f, 0.15f),
			ItemIds.GrenadeFlash => new Color(0.95f, 0.95f, 0.55f),
			ItemIds.GrenadeSticky => new Color(0.4f, 0.8f, 1f),
			_ => new Color(1f, 0.75f, 0.25f),
		};

		/// <summary>À appeler à chaque image. <paramref name="grenade"/> : identifiant de la grenade en main (0 = aucune : tout est caché).</summary>
		public void Refresh(Camera3D cam, Vector3 horizontalVelocity, byte grenade, StaticWorld world, bool show)
		{
			var def = GrenadeDefs.Get(grenade);
			if (!show || cam == null || def == null || world == null) { Hide(); return; }

			Vector3 fwd = -cam.GlobalTransform.Basis.Z.Normalized();
			Vector3 eye = cam.GlobalPosition;
			Vector3 origin = eye + fwd * 0.5f;
			if (world.Raycast(new Vec3(eye.X, eye.Y, eye.Z), new Vec3(fwd.X, fwd.Y, fwd.Z), 0.5f, out _)) origin = eye;     // contre un mur : elle part de l'œil (comme le serveur)
			Vector3 vel = (fwd + Vector3.Up * 0.2f).Normalized() * GrenadeDefs.ThrowSpeed + horizontalVelocity * 0.6f;

			_points.Clear();
			_points.Add(origin + new Vector3(0, -0.25f, 0) + cam.GlobalTransform.Basis.X * 0.12f);     // le ruban part de la main
			Vector3 pos = origin;
			const float dt = 1f / 30f;
			bool hit = false;
			Vector3 impact = pos;
			for (int i = 0; i < 150 && !hit; i++)
			{
				vel += Vector3.Down * GrenadeDefs.Gravity * dt;
				Vector3 step = vel * dt;
				float len = step.Length();
				if (len < 1e-5f) break;
				Vector3 dir = step / len;
				if (world.Raycast(new Vec3(pos.X, pos.Y, pos.Z), new Vec3(dir.X, dir.Y, dir.Z), len + 0.12f, out float wall))
				{
					impact = pos + dir * Mathf.Max(0f, wall - 0.12f);
					_points.Add(impact);
					hit = true;
				}
				else { pos += step; _points.Add(pos); }
			}
			Visible = true;
			Draw(cam, ColorOf(grenade));

			// cercle d'effet au point d'impact (rayon de l'explosion ou de la zone de feu ; petit pour l'éclair)
			_ring.Visible = hit;
			if (hit)
			{
				float r = def.Effect == GrenadeEffect.Flash ? 1.2f : def.Radius;
				_torus.InnerRadius = Mathf.Max(0.02f, r - 0.09f);
				_torus.OuterRadius = r;
				_ringMat.AlbedoColor = ColorOf(grenade) with { A = 0.85f };
				_ring.GlobalPosition = impact + Vector3.Up * 0.06f;
			}
		}

		private void Hide()
		{
			if (!Visible) return;
			Visible = false;
			_mesh.ClearSurfaces();
		}

		/// <summary>Ruban de 7 cm de large qui fait face à la caméra, de plus en plus transparent vers la fin.</summary>
		private void Draw(Camera3D cam, Color color)
		{
			_mesh.ClearSurfaces();
			if (_points.Count < 2) return;
			_ribbon.GlobalTransform = Transform3D.Identity;
			_mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
			Vector3 view = -cam.GlobalTransform.Basis.Z;
			int n = _points.Count;
			for (int i = 0; i < n; i++)
			{
				Vector3 a = _points[Mathf.Max(0, i - 1)], b = _points[Mathf.Min(n - 1, i + 1)];
				Vector3 tangent = (b - a).Normalized();
				Vector3 side = tangent.Cross(view);
				side = side.LengthSquared() < 1e-6f ? cam.GlobalTransform.Basis.X : side.Normalized();
				float t = i / (float)(n - 1);
				float w = 0.035f + 0.02f * t;
				var c = new Color(color.R, color.G, color.B, 0.95f - 0.5f * t);
				_mesh.SurfaceSetColor(c); _mesh.SurfaceAddVertex(_points[i] + side * w);
				_mesh.SurfaceSetColor(c); _mesh.SurfaceAddVertex(_points[i] - side * w);
			}
			_mesh.SurfaceEnd();
		}
	}
}
