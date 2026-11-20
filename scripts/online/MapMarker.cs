using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Marqueur d'attention d'une carte de mode de jeu : apparition, point de contrôle, base de drapeau ou site de bombe. On le déplace et on le copie dans l'éditeur comme
	/// n'importe quel nœud ; l'exporteur de carte (onglet « Carte » du panneau Open Esport) les écrit dans le fichier .world que lisent le serveur et le jeu.
	/// Dans l'éditeur il montre un disque coloré (bleu / rouge selon l'équipe) et une étiquette.
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class MapMarker : Marker3D
	{
		public enum KindEnum
		{
			ApparitionChacunPourSoi = 1,
			ApparitionEquipe = 2,
			PointDeControle = 3,
			BaseDeDrapeau = 4,
			SiteDeBombe = 5,
			Garage = 6,
		}

		private KindEnum _kind = KindEnum.ApparitionEquipe;
		private int _team, _index;
		private float _radius = 2f;

		[Export] public KindEnum Kind { get => _kind; set { _kind = value; Refresh(); } }
		/// <summary>0 = bleus, 1 = rouges, 255 = personne. En Sabotage : 0 = défenseurs, 1 = attaquants. Point de contrôle : propriétaire au départ.</summary>
		[Export(PropertyHint.Range, "0,255,1")] public int Team { get => _team; set { _team = value; Refresh(); } }
		/// <summary>Relais A à E (0 à 4), site de bombe A ou B (0 ou 1), garage hover ou marcheur (0 ou 1).</summary>
		[Export(PropertyHint.Range, "0,9,1")] public int Index { get => _index; set { _index = value; Refresh(); } }
		/// <summary>Rayon de la zone (points de contrôle, sites de bombe, bases de drapeau).</summary>
		[Export(PropertyHint.Range, "0.5,30,0.5")] public float Radius { get => _radius; set { _radius = value; Refresh(); } }

		private MeshInstance3D _disc;
		private Label3D _label;

		public override void _EnterTree()
		{
			if (!Engine.IsEditorHint()) return;
			_disc = new MeshInstance3D { Name = "_EditorDisc", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			AddChild(_disc, false, InternalMode.Front);
			_label = new Label3D { Name = "_EditorLabel", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0008f, NoDepthTest = true, Position = new Vector3(0, 2.2f, 0), FontSize = 28, OutlineSize = 10 };
			AddChild(_label, false, InternalMode.Front);
			Refresh();
		}

		public override void _ExitTree() { _disc = null; _label = null; }

		public string Describe()
		{
			string team = _team == 0 ? "équipe 0" : _team == 1 ? "équipe 1" : "personne";
			return _kind switch
			{
				KindEnum.ApparitionChacunPourSoi => "Apparition (chacun pour soi)",
				KindEnum.ApparitionEquipe => $"Apparition — {team}",
				KindEnum.PointDeControle => $"Point de contrôle {(char)('A' + _index)} — départ : {team}",
				KindEnum.Garage => _index == 0 ? "Hangar hover" : "Hangar AT-ST",
				KindEnum.BaseDeDrapeau => $"Base du drapeau — {team}",
				_ => $"Site de bombe {(char)('A' + _index)}",
			};
		}

		private void Refresh()
		{
			if (_disc == null || !IsInstanceValid(_disc)) return;
			var color = _team == 0 ? new Color(0.25f, 0.55f, 1f, 0.55f) : _team == 1 ? new Color(1f, 0.3f, 0.25f, 0.55f) : new Color(0.9f, 0.9f, 0.9f, 0.5f);
			if (_kind == KindEnum.SiteDeBombe) color = new Color(1f, 0.8f, 0.2f, 0.5f);
			float r = _kind == KindEnum.ApparitionChacunPourSoi || _kind == KindEnum.ApparitionEquipe ? 1f : _radius;
			_disc.Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 0.04f, RadialSegments = 40 };
			_disc.Position = new Vector3(0, 0.03f, 0);
			_disc.MaterialOverride = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
			_label.Text = Describe();
			_label.Modulate = color with { A = 1f };
		}

		public OpenEsport.Core.MapPoint ToPoint(Vec3 pos) => new OpenEsport.Core.MapPoint
		{
			Kind = (MapPointKind)(int)_kind, Team = (byte)Mathf.Clamp(_team, 0, 255), Index = (byte)Mathf.Clamp(_index, 0, 255), Pos = pos, Radius = _radius, Yaw = GlobalRotation.Y
		};
	}
}
