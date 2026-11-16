using Godot;
using System.Collections.Generic;
using System.Text;
using OpenEsport.Core;
using Openesport.Inventory;

namespace Openesport.Online
{
	/// <summary>
	/// Point de butin posé dans la carte : un coffre ou un objet au sol. On le déplace, copie (Ctrl+D) et range comme n'importe quel nœud de la scène
	/// <c>Frontiere01.tscn</c> (dossier « Loot »). L'exporteur de carte (tools/export_world.tscn) les lit pour fabriquer le fichier .world du serveur.
	///
	/// Dans l'éditeur, le point montre le vrai modèle (le coffre du jeu, ou l'objet imposé ; un objet « au hasard » montre une boîte de munitions) avec une étiquette
	/// et un disque coloré au sol : vert = activé, orange = au hasard, rouge = désactivé.
	///
	/// Activation : « Désactivé » (jamais), « Activé » (à chaque partie) ou « Au hasard » (tiré au sort à chaque partie). La probabilité du tirage est
	/// commune à tous les points (réglages du jeu : loot.pointChance pour les objets, loot.chestPointChance pour les coffres) et peut être remplacée sur un point
	/// en cochant « Chance propre ».
	///
	/// Un coffre est un volume plein aligné sur les axes de la carte (1,5 × 0,63 × 0,84 m) : sa rotation n'est pas prise en compte.
	/// La position du nœud est le sol (le dessous du coffre / de l'objet).
	/// </summary>
	[Tool]
	[GlobalClass]
	public partial class LootPoint : Marker3D
	{
		public enum KindEnum { Objet = 0, Coffre = 1 }
		public enum ActivationEnum { Désactivé = 0, Activé = 1, AuHasard = 2 }
		/// <summary>Niveau des armes et boucliers du point (pour un coffre : de son contenu). « En majorité » : voir le réglage loot.qualityMajority.</summary>
		public enum QualityEnum
		{
			CommeLeJeu = 0,
			MajoritéClassique = 1, MajoritéRare = 2, MajoritéÉpique = 3, MajoritéLégendaire = 4,
			QueClassique = 5, QueRare = 6, QueÉpique = 7, QueLégendaire = 8
		}

		private KindEnum _kind = KindEnum.Objet;
		private ActivationEnum _activation = ActivationEnum.AuHasard;
		private bool _ownChance;
		private float _chance = 0.5f;
		private string _item = "random";
		private QualityEnum _quality = QualityEnum.CommeLeJeu;

		[Export] public KindEnum Kind { get => _kind; set { _kind = value; Refresh(); } }
		[Export] public ActivationEnum Activation { get => _activation; set { _activation = value; Refresh(); } }
		/// <summary>Niveau du butin : comme le jeu (tirage normal), en majorité d'un niveau, ou uniquement d'un niveau (que du rare, que du légendaire…).</summary>
		[Export] public QualityEnum Quality { get => _quality; set { _quality = value; Refresh(); } }
		/// <summary>Cocher pour utiliser la probabilité ci-dessous au lieu du réglage commun (loot.pointChance / loot.chestPointChance).</summary>
		[Export] public bool OwnChance { get => _ownChance; set { _ownChance = value; Refresh(); } }
		[Export(PropertyHint.Range, "0,1,0.01")] public float Chance { get => _chance; set { _chance = Mathf.Clamp(value, 0f, 1f); Refresh(); } }
		/// <summary>Objet imposé sur ce point (« random » : tiré dans la table de butin à chaque partie). Sans effet pour un coffre : son contenu est tiré à l'ouverture.</summary>
		[Export] public string Item { get => _item; set { _item = string.IsNullOrEmpty(value) ? "random" : value; Refresh(); } }
		/// <summary>
		/// Liste précise des objets possibles, avec un poids pour chacun (optionnel : 1 par défaut). Vide = tirage normal du jeu.
		/// Point « Objet » : un objet de la liste est tiré à chaque partie. Point « Coffre » : les 4 objets du coffre sont tirés dans la liste (au lieu du contenu habituel).
		/// </summary>
		[Export] public Godot.Collections.Array<LootEntry> Pool { get; set; } = new();
		/// <summary>Quantité (munitions…) ; 0 = quantité normale de l'objet.</summary>
		[Export(PropertyHint.Range, "0,500,1")] public int Count { get; set; }
		/// <summary>L'exporteur pose le point sur le relief (hauteur du terrain sous lui). À décocher pour un point dans un bâtiment ou sur un toit.</summary>
		[Export] public bool SnapToTerrain { get; set; }

		private Node3D _visual;
		private MeshInstance3D _disc;
		private Label3D _label;
		private string _shownModel;

		public override void _ValidateProperty(Godot.Collections.Dictionary property)
		{
			if (property["name"].AsString() == nameof(Item))
			{
				property["hint"] = (int)PropertyHint.Enum;
				property["hint_string"] = ItemChoices();
			}
		}

		private static string _choices;
		private static string ItemChoices() => ItemNames(true);

		/// <summary>Noms des objets du jeu pour une liste déroulante (avec « random » en tête si demandé).</summary>
		public static string ItemNames(bool withRandom)
		{
			if (_choices == null)
			{
				var sb = new StringBuilder();
				foreach (var d in ItemDefs.All)
					if (d.Id != ItemIds.Chest && d.Id != ItemIds.None) sb.Append(d.Name).Append(',');
				_choices = sb.ToString().TrimEnd(',');
			}
			return withRandom ? "random," + _choices : _choices;
		}

		public override void _EnterTree()
		{
			if (!Engine.IsEditorHint()) return;
			// repères visibles dans l'éditeur seulement : sans propriétaire, ils ne sont pas enregistrés avec la scène
			_disc = new MeshInstance3D { Name = "_EditorDisc", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			AddChild(_disc, false, InternalMode.Front);
			_label = new Label3D { Name = "_EditorLabel", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0007f, NoDepthTest = true, Position = new Vector3(0, 1.2f, 0), FontSize = 28, OutlineSize = 10 };
			AddChild(_label, false, InternalMode.Front);
			Refresh();
		}

		public override void _ExitTree()
		{
			_visual = null; _disc = null; _label = null; _shownModel = null;
		}

		/// <summary>Modèle montré dans l'éditeur : le coffre du jeu, l'objet imposé, ou (objet au hasard) une boîte de munitions.</summary>
		private Node3D BuildVisual()
		{
			if (_kind == KindEnum.Coffre)
			{
				var scene = GD.Load<PackedScene>(LootChest.ModelPath);
				if (scene != null) return scene.Instantiate<Node3D>();
			}
			else
			{
				var def = ItemDatabase.Get(ShownItem()) ?? ItemDatabase.Get("ammo_rifle");
				if (def != null)
				{
					try { return ItemVisuals.BuildGroundModel(def, Mathf.Max(0, TierShown())); }
					catch (System.Exception e) { GD.PushWarning("[LootPoint] modèle de l'objet indisponible : " + e.Message); }
				}
			}
			return new MeshInstance3D { Mesh = new BoxMesh { Size = _kind == KindEnum.Coffre ? new Vector3(1.5f, 0.63f, 0.84f) : new Vector3(0.4f, 0.3f, 0.4f) }, Position = new Vector3(0, 0.2f, 0) };
		}

		private string ShownItem()
		{
			if (_item != "random") return _item;
			if (Pool != null) foreach (var e in Pool) if (e != null && !string.IsNullOrEmpty(e.Item)) return e.Item;
			return "ammo_rifle";
		}

		/// <summary>Niveau montré pour l'objet imposé (couleur de l'arme) : celui du réglage, ou classique.</summary>
		private int TierShown() => _quality switch { QualityEnum.MajoritéRare or QualityEnum.QueRare => 1, QualityEnum.MajoritéÉpique or QualityEnum.QueÉpique => 2, QualityEnum.MajoritéLégendaire or QualityEnum.QueLégendaire => 3, _ => 0 };

		private void Refresh()
		{
			if (_disc == null || !IsInstanceValid(_disc)) return;
			bool chest = _kind == KindEnum.Coffre;
			var color = _activation switch
			{
				ActivationEnum.Activé => new Color(0.25f, 0.9f, 0.35f, 0.6f),
				ActivationEnum.AuHasard => new Color(1f, 0.65f, 0.15f, 0.6f),
				_ => new Color(0.9f, 0.2f, 0.2f, 0.45f)
			};
			// le modèle n'est refait que si ce qu'il montre change
			string key = $"{_kind}|{ShownItem()}|{TierShown()}";
			if (key != _shownModel)
			{
				_shownModel = key;
				if (_visual != null && IsInstanceValid(_visual)) { RemoveChild(_visual); _visual.QueueFree(); }
				_visual = BuildVisual();
				_visual.Name = "_EditorModel";
				AddChild(_visual, false, InternalMode.Front);
			}
			// un point désactivé reste visible mais sombre
			if (_visual != null) SetTransparency(_visual, _activation == ActivationEnum.Désactivé ? 0.65f : 0f);
			float radius = chest ? 1.0f : 0.5f;
			_disc.Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.02f, RadialSegments = 32 };
			_disc.Position = new Vector3(0, 0.01f, 0);
			_disc.MaterialOverride = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
			int pool = Pool == null ? 0 : Pool.Count;
			string what = (chest ? "Coffre" : (_item == "random" ? (pool > 0 ? "Objet" : "Objet") : _item)) + (pool > 0 ? $" (liste de {pool})" : "");
			string when = _activation switch
			{
				ActivationEnum.Activé => "toujours",
				ActivationEnum.AuHasard => _ownChance ? $"{Mathf.RoundToInt(_chance * 100)} %" : "au hasard (commun)",
				_ => "désactivé"
			};
			string quality = _quality == QualityEnum.CommeLeJeu ? "" : "\n" + _quality.ToString().Replace("Majorité", "majorité ").Replace("Que", "que ").ToLowerInvariant();
			_label.Text = $"{what} — {when}{quality}";
			_label.Modulate = color with { A = 1f };
		}

		private static void SetTransparency(Node n, float t)
		{
			if (n is GeometryInstance3D g) g.Transparency = t;
			foreach (var c in n.GetChildren()) SetTransparency(c, t);
		}

		/// <summary>Convertit le nœud en point de carte (à la position globale du nœud ; l'exporteur applique éventuellement le relief).</summary>
		public LootSpawn ToSpawn(Vec3 pos)
		{
			byte itemId = ItemIds.None;
			if (_kind == KindEnum.Objet && _item != "random")
			{
				var def = ItemDefs.ByName(_item);
				if (def != null) itemId = def.Id;
				else GD.PushWarning($"[LootPoint] objet inconnu « {_item} » sur {GetPath()} : tiré au hasard à la place.");
			}
			var ids = new List<byte>(); var weights = new List<float>();
			if (Pool != null)
				foreach (var e in Pool)
				{
					if (e == null || e.Weight <= 0f) continue;
					var d = ItemDefs.ByName(e.Item);
					if (d == null || d.Id == ItemIds.Chest || d.Id == ItemIds.None) { GD.PushWarning($"[LootPoint] objet inconnu « {e.Item} » dans la liste de {GetPath()} : ignoré."); continue; }
					ids.Add(d.Id); weights.Add(e.Weight);
				}
			return new LootSpawn
			{
				PoolIds = ids.Count > 0 ? ids.ToArray() : null, PoolWeights = ids.Count > 0 ? weights.ToArray() : null,
				Pos = pos, ItemId = itemId, Count = (ushort)Mathf.Clamp(Count, 0, 65535),
				Kind = _kind == KindEnum.Coffre ? LootSpawn.KindChest : LootSpawn.KindItem,
				Mode = _activation == ActivationEnum.Activé ? LootSpawn.ModeAlways : LootSpawn.ModeRandom, Quality = (byte)_quality, Yaw = GlobalRotation.Y,
				HasChance = _ownChance && _activation == ActivationEnum.AuHasard, Chance = _chance
			};
		}

		/// <summary>Tous les points de butin sous un nœud (récursif), dans l'ordre de la scène.</summary>
		public static List<LootPoint> FindAll(Node root)
		{
			var list = new List<LootPoint>();
			void Walk(Node n) { foreach (var c in n.GetChildren()) { if (c is LootPoint lp) list.Add(lp); Walk(c); } }
			Walk(root);
			return list;
		}
	}
}
