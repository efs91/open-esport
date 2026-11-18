using Openesport.Utils;
using System.Collections.Generic;
using Godot;

namespace Openesport.Online
{
	/// <summary>Un ping actif : qui l'a lancé, de quel type (0 ici, 1 danger, 2 ennemi), où et quand.</summary>
	public sealed class PingData
	{
		public ushort Owner;
		public byte Type;
		public Vector3 Pos;
		public double Born;
		public string Name = "";
	}

	/// <summary>Un ping tel que le dessinent les cartes (minimap et grande carte).</summary>
	public struct PingMark
	{
		public Vector3 Pos;
		public byte Type;
		public float Alpha;
		public string Label;
		/// <summary>Auteur du ping (pour le retirer d'un clic sur la grande carte).</summary>
		public ushort Owner;
	}

	/// <summary>Dessine les pings dans le monde : un faisceau vertical, un losange visible à travers les murs, le nom du joueur et la distance.</summary>
	public partial class PingView : Node3D
	{
		/// <summary>Un ping ne s'efface pas tout seul : il reste jusqu'à ce que son auteur le remplace (autre ping) ou le retire (clic dessus sur la carte). Chaque joueur n'en a qu'un.</summary>
		public const double Lifetime = 1e9;

		public static Color ColorOf(byte type) => type switch
		{
			1 => new Color(1f, 0.62f, 0.15f),       // danger : orange
			2 => new Color(1f, 0.25f, 0.2f),        // ennemi : rouge
			_ => new Color(0.35f, 0.8f, 1f),        // ici : bleu clair
		};

		public static string NameOf(byte type) => type switch { 1 => I18n.T("DANGER"), 2 => I18n.T("ENNEMI"), _ => I18n.T("ICI") };

		private sealed class Entry { public Node3D Node, Gem; public Label3D Label; }

		private readonly Dictionary<PingData, Entry> _entries = new();
		private List<PingData> _current = new();

		/// <summary>La liste des pings actifs (la même instance est relue à chaque image).</summary>
		public void Sync(List<PingData> list) => _current = list;

		public override void _Process(double delta)
		{
			double now = Time.GetTicksMsec() / 1000.0;
			var cam = GetViewport().GetCamera3D();
			foreach (var p in _current)
				if (!_entries.ContainsKey(p)) _entries[p] = Build(p);
			List<PingData> gone = null;
			foreach (var kv in _entries) if (!_current.Contains(kv.Key)) (gone ??= new List<PingData>()).Add(kv.Key);
			if (gone != null)
				foreach (var p in gone) { if (GodotObject.IsInstanceValid(_entries[p].Node)) _entries[p].Node.QueueFree(); _entries.Remove(p); }

			foreach (var kv in _entries)
			{
				var p = kv.Key; var e = kv.Value;
				double age = now - p.Born;
				float fade = (float)Mathf.Clamp(Lifetime - age, 0.0, 1.0);                  // il s'efface dans la dernière seconde
				float pop = (float)Mathf.Clamp(age / 0.25, 0.0, 1.0);                        // et grossit à l'arrivée
				float pulse = 1f + 0.12f * Mathf.Sin((float)age * 6f);
				e.Gem.Scale = Vector3.One * pop * pulse;
				e.Node.Visible = fade > 0.01f;
				e.Label.Modulate = new Color(1, 1, 1, fade);
				if (cam != null)
				{
					e.Label.Text = $"{p.Name}\n{NameOf(p.Type)} · {cam.GlobalPosition.DistanceTo(p.Pos):0} m";
					var to = cam.GlobalPosition - e.Gem.GlobalPosition;
					e.Gem.GlobalRotation = new Vector3(0, Mathf.Atan2(to.X, to.Z), 0);          // le losange fait face à la caméra
				}
			}
		}

		private Entry Build(PingData p)
		{
			var col = ColorOf(p.Type);
			var root = new Node3D { Name = "Ping" };
			AddChild(root);
			root.GlobalPosition = p.Pos;

			var beamMat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				BlendMode = BaseMaterial3D.BlendModeEnum.Add,
				AlbedoColor = new Color(col.R, col.G, col.B, 0.32f),
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			};
			root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.18f, Height = 60f, RadialSegments = 10 }, Position = new Vector3(0, 30f, 0), MaterialOverride = beamMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

			// losange : deux triangles dos à dos, visibles à travers les murs
			var gemMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = col, NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
			var gem = new Node3D { Position = new Vector3(0, 1.6f, 0) };
			gem.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.5f, 0.45f, 0.02f) }, MaterialOverride = gemMat, Position = new Vector3(0, 0.225f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			gem.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.5f, 0.45f, 0.02f) }, MaterialOverride = gemMat, Position = new Vector3(0, -0.225f, 0), Rotation = new Vector3(0, 0, Mathf.Pi), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			root.AddChild(gem);

			var label = new Label3D
			{
				Text = p.Name,
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				FixedSize = true,
				PixelSize = 0.00085f,
				NoDepthTest = true,
				FontSize = 34,
				OutlineSize = 10,
				OutlineModulate = new Color(0, 0, 0, 0.9f),
				Position = new Vector3(0, 2.4f, 0),
			};
			root.AddChild(label);
			return new Entry { Node = root, Gem = gem, Label = label };
		}
	}
}
