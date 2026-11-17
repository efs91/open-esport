using System.Collections.Generic;
using Godot;
using OpenEsport.Core;
using Openesport.Inventory;

namespace Openesport.Online
{
	/// <summary>
	/// Dessine les grenades en vol et les zones de feu décrites par le serveur (20 messages par seconde) : les positions reçues sont
	/// complétées par la même gravité entre deux messages, pour un mouvement fluide. Seul le serveur décide des rebonds et des dégâts.
	/// </summary>
	public partial class GrenadeView : Node3D
	{
		private sealed class Entry
		{
			public Node3D Node;
			public Vector3 Vel;
			public bool Stuck;
			public double Seen;
			public byte Kind;
			public Vector3 Spin;
		}

		private readonly Dictionary<ushort, Entry> _items = new();
		private readonly System.Random _rng = new(7);
		private double _now;

		/// <summary>Remplace l'état connu par la liste reçue : ce qui n'y figure plus disparaît (après un court délai, un message peut se perdre).</summary>
		public void Apply(List<GrenadeInfo> list)
		{
			_now = Time.GetTicksMsec() / 1000.0;
			foreach (var g in list)
			{
				var pos = new Vector3(g.Pos.X, g.Pos.Y, g.Pos.Z);
				if (!_items.TryGetValue(g.Id, out var e))
				{
					e = new Entry { Kind = g.Kind, Node = g.Kind == GrenadeDefs.FireZoneKind ? BuildFire() : BuildGrenade(g.Kind) };
					AddChild(e.Node);
					e.Node.GlobalPosition = pos;
					e.Spin = new Vector3((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f).Normalized() * 9f;
					_items[g.Id] = e;
				}
				e.Seen = _now;
				e.Stuck = g.Stuck;
				e.Vel = new Vector3(g.Vel.X, g.Vel.Y, g.Vel.Z);
				// recalage doux sur la position du serveur
				e.Node.GlobalPosition = e.Node.GlobalPosition.Lerp(pos, e.Stuck ? 1f : 0.6f);
			}
		}

		public override void _Process(double delta)
		{
			float dt = (float)delta;
			double now = Time.GetTicksMsec() / 1000.0;
			List<ushort> gone = null;
			foreach (var kv in _items)
			{
				var e = kv.Value;
				if (now - e.Seen > 0.5) { (gone ??= new List<ushort>()).Add(kv.Key); continue; }
				if (e.Stuck || e.Kind == GrenadeDefs.FireZoneKind) continue;
				e.Vel += Vector3.Down * GrenadeDefs.Gravity * dt;
				e.Node.GlobalPosition += e.Vel * dt;
				float speed = e.Vel.Length();
				if (speed > 0.8f) e.Node.RotateObjectLocal(e.Spin.Normalized(), e.Spin.Length() * Mathf.Min(1f, speed / 10f) * dt);
			}
			if (gone != null)
				foreach (var id in gone) { if (GodotObject.IsInstanceValid(_items[id].Node)) _items[id].Node.QueueFree(); _items.Remove(id); }
		}

		private static Node3D BuildGrenade(byte kind)
		{
			var root = new Node3D { Name = "Grenade" };
			var def = ItemDatabase.Get(ItemDefs.Get((byte)(ItemIds.GrenadeFrag + kind))?.Name);
			if (def != null)
			{
				var model = ItemVisuals.BuildGroundModel(def);
				model.Position = new Vector3(0, -def.GroundSize * 0.5f, 0);      // centrée sur son point de rotation
				root.AddChild(model);
			}
			else root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.06f, Height = 0.12f } });
			return root;
		}

		/// <summary>Foyer d'une grenade incendiaire : flammes qui montent, braises, et une lueur qui vacille.</summary>
		private static Node3D BuildFire()
		{
			float radius = GrenadeDefs.Get(ItemIds.GrenadeFire)?.Radius ?? 4.5f;
			var root = new FireNode { Name = "Feu" };
			var flames = GrenadeFx.Particles(80, 1.1f, 0.4f, 1.6f, 1.6f, 3.2f, new Vector3(0, 2.2f, 0), GrenadeFx.FireRamp(), true, false);
			flames.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
			flames.EmissionSphereRadius = radius * 0.75f;
			flames.Direction = Vector3.Up; flames.Spread = 12f;
			flames.OneShot = false; flames.Explosiveness = 0f;
			flames.Position = new Vector3(0, 0.1f, 0);
			root.AddChild(flames);
			var embers = GrenadeFx.Particles(18, 1.6f, 1.5f, 3.5f, 0.05f, 0.12f, new Vector3(0, 1.0f, 0), GrenadeFx.EmberRamp(), true, false);
			embers.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
			embers.EmissionSphereRadius = radius * 0.7f;
			embers.Direction = Vector3.Up; embers.Spread = 25f;
			embers.OneShot = false; embers.Explosiveness = 0f;
			root.AddChild(embers);
			root.Light = new OmniLight3D { LightColor = new Color(1f, 0.5f, 0.15f), OmniRange = radius * 2.4f, LightEnergy = 2.5f, Position = new Vector3(0, 1.2f, 0), ShadowEnabled = false };
			root.AddChild(root.Light);
			return root;
		}

		private partial class FireNode : Node3D
		{
			public OmniLight3D Light;
			private float _t;
			public override void _Process(double delta)
			{
				_t += (float)delta * 11f;
				if (Light != null) Light.LightEnergy = 2.2f + Mathf.Sin(_t) * 0.5f + Mathf.Sin(_t * 2.3f) * 0.3f;
			}
		}
	}

	/// <summary>Effets d'une grenade qui agit : boule de feu et fumée (fragmentation, collante), gerbe de feu (incendiaire), éclair (flash).</summary>
	public static class GrenadeFx
	{
		private static GradientTexture2D _soft;

		private static Texture2D Soft()
		{
			if (_soft != null) return _soft;
			var g = new Gradient();
			g.SetColor(0, new Color(1, 1, 1, 1));
			g.SetColor(1, new Color(1, 1, 1, 0));
			_soft = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64 };
			return _soft;
		}

		public static Gradient FireRamp()
		{
			var g = new Gradient { Offsets = new[] { 0f, 0.25f, 0.7f, 1f }, Colors = new[] { new Color(1f, 0.95f, 0.6f, 0.9f), new Color(1f, 0.55f, 0.1f, 0.85f), new Color(0.8f, 0.15f, 0.05f, 0.45f), new Color(0.2f, 0.05f, 0.02f, 0f) } };
			return g;
		}

		public static Gradient EmberRamp() => new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { new Color(1f, 0.7f, 0.2f, 1f), new Color(1f, 0.2f, 0.05f, 0f) } };

		private static Gradient SmokeRamp() => new Gradient { Offsets = new[] { 0f, 0.2f, 1f }, Colors = new[] { new Color(0.25f, 0.23f, 0.22f, 0.0f), new Color(0.3f, 0.28f, 0.26f, 0.55f), new Color(0.35f, 0.34f, 0.33f, 0f) } };

		/// <summary>Un émetteur de particules « billboard » doux (CPU : peu coûteux et sans shader à compiler).</summary>
		public static CpuParticles3D Particles(int amount, float lifetime, float velMin, float velMax, float sizeMin, float sizeMax, Vector3 gravity, Gradient ramp, bool additive, bool oneShot)
		{
			var mat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
				BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
				BillboardKeepScale = true,
				VertexColorUseAsAlbedo = true,
				AlbedoTexture = Soft(),
				DisableReceiveShadows = true,
				NoDepthTest = false,
			};
			return new CpuParticles3D
			{
				Amount = amount,
				Lifetime = lifetime,
				OneShot = oneShot,
				Explosiveness = oneShot ? 1f : 0f,
				Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
				Direction = Vector3.Up,
				Spread = 180f,
				InitialVelocityMin = velMin,
				InitialVelocityMax = velMax,
				Gravity = gravity,
				ScaleAmountMin = sizeMin,
				ScaleAmountMax = sizeMax,
				ColorRamp = ramp,
				Emitting = true,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			};
		}

		private static void Burst(Node parent, CpuParticles3D p, Vector3 pos)
		{
			parent.AddChild(p);
			p.GlobalPosition = pos;
			parent.GetTree().CreateTimer(p.Lifetime + 0.4).Timeout += () => { if (GodotObject.IsInstanceValid(p)) p.QueueFree(); };
		}

		private static void FlashLight(Node parent, Vector3 pos, Color color, float energy, float range, float seconds)
		{
			var light = new OmniLight3D { LightColor = color, LightEnergy = energy, OmniRange = range, ShadowEnabled = false };
			parent.AddChild(light);
			light.GlobalPosition = pos;
			var tween = light.CreateTween();
			tween.TweenProperty(light, "light_energy", 0f, seconds);
			tween.TweenCallback(Callable.From(light.QueueFree));
		}

		/// <summary>Une grenade agit en <paramref name="pos"/> : <paramref name="kind"/> = 0 frag, 1 collante, 2 incendiaire, 3 flash.</summary>
		public static void Explosion(Node parent, byte kind, Vector3 pos)
		{
			if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
			switch (kind)
			{
				case 2:
					FlashLight(parent, pos + Vector3.Up * 0.8f, new Color(1f, 0.5f, 0.15f), 8f, 12f, 0.6f);
					Burst(parent, Particles(40, 0.9f, 2f, 7f, 1.0f, 2.4f, new Vector3(0, 1.5f, 0), FireRamp(), true, true), pos + Vector3.Up * 0.3f);
					GameAudio.Boom(parent, pos, 0.55f);
					break;
				case 3:
					FlashLight(parent, pos + Vector3.Up * 0.3f, Colors.White, 40f, 45f, 0.35f);
					Burst(parent, Particles(1, 0.35f, 0f, 0f, 9f, 9f, Vector3.Zero, new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0) } }, true, true), pos + Vector3.Up * 0.3f);
					GameAudio.Bang(parent, pos);
					break;
				default:
					FlashLight(parent, pos + Vector3.Up * 0.5f, new Color(1f, 0.7f, 0.3f), 14f, 18f, 0.45f);
					Burst(parent, Particles(28, 0.55f, 4f, 11f, 1.8f, 3.6f, Vector3.Zero, FireRamp(), true, true), pos + Vector3.Up * 0.4f);
					Burst(parent, Particles(26, 2.0f, 1.5f, 5f, 3f, 5.5f, new Vector3(0, 0.6f, 0), SmokeRamp(), false, true), pos + Vector3.Up * 0.5f);
					Burst(parent, Particles(30, 0.9f, 8f, 18f, 0.06f, 0.14f, new Vector3(0, -9.8f, 0), EmberRamp(), true, true), pos + Vector3.Up * 0.3f);
					GameAudio.Boom(parent, pos, 1f);
					break;
			}
		}
	}

	/// <summary>Écran blanc d'une grenade flash : plein tant que l'éclair éblouit, puis il s'estompe.</summary>
	public partial class FlashOverlay : CanvasLayer
	{
		private ColorRect _rect;
		private float _left, _total, _peak;

		public FlashOverlay() { Layer = 126; }

		public override void _Ready()
		{
			_rect = new ColorRect { Color = new Color(1, 1, 1, 0), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(_rect);
		}

		/// <param name="seconds">Durée de l'aveuglement.</param>
		/// <param name="facing">0 (dos tourné) à 1 (on regardait la grenade) : le blanc est moins complet si on ne la regardait pas.</param>
		public void Trigger(float seconds, float facing)
		{
			_peak = Mathf.Lerp(0.55f, 1f, Mathf.Clamp(facing, 0f, 1f));
			_left = Mathf.Max(_left, seconds);
			_total = Mathf.Max(_left, 0.3f);
		}

		public override void _Process(double delta)
		{
			if (_left <= 0f) { if (_rect.Visible) _rect.Visible = false; return; }
			_left -= (float)delta;
			float t = Mathf.Clamp(_left / _total, 0f, 1f);
			float alpha = _peak * (t > 0.45f ? 1f : Mathf.SmoothStep(0f, 1f, t / 0.45f));
			_rect.Visible = alpha > 0.01f;
			_rect.Color = new Color(1f, 1f, 1f, alpha);
		}
	}
}
