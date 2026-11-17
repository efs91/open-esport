#if TOOLS
using System;
using System.Linq;
using Godot;
using Openesport.Inventory;

namespace Openesport.EditorTools
{
	/// <summary>
	/// Onglet « Objets » : toutes les armes, viseurs, sacs à dos, munitions, boucliers et soins du jeu, avec leur modèle. Les chiffres (dégâts, cadence, soin, durée…)
	/// se règlent dans l'onglet « Réglages du jeu » (bouton « Réglages » : il y filtre l'objet choisi). Le modèle s'ouvre d'un double-clic.
	/// Prochaine étape prévue : définitions d'objets sous forme de ressources (.tres) éditables ici (modèle, animations, taille au sol, accessoires acceptés…).
	/// </summary>
	[Tool]
	public partial class ItemsTab : VBoxContainer
	{
		public Action<string> OpenBalanceFilter;
		private Tree _tree;
		private Label _detail;

		private static string TypeName(ItemType t) => t switch
		{
			ItemType.Weapon => "Arme", ItemType.Optic => "Viseur", ItemType.Backpack => "Sac à dos", ItemType.Ammo => "Munitions", ItemType.Shield => "Bouclier", ItemType.Consumable => "Soin", ItemType.Grenade => "Grenade", _ => t.ToString()
		};

		public override void _Ready()
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			AddThemeConstantOverride("separation", 6);
			AddChild(new Label
			{
				Text = "Objets du jeu. Double-clic : ouvrir le modèle. « Réglages » : afficher les chiffres de l'objet (dégâts, cadence, chargeur, soin, durée…) dans l'onglet Réglages du jeu.",
				AutowrapMode = TextServer.AutowrapMode.WordSmart
			});
			var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			AddChild(split);

			_tree = new Tree { Columns = 4, HideRoot = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(640, 0) };
			_tree.SetColumnTitle(0, "Objet"); _tree.SetColumnTitle(1, "Type"); _tree.SetColumnTitle(2, "Identifiant"); _tree.SetColumnTitle(3, "Modèle");
			_tree.ColumnTitlesVisible = true;
			_tree.SetColumnExpand(0, true); _tree.SetColumnExpand(3, true);
			_tree.SetColumnCustomMinimumWidth(1, 90); _tree.SetColumnCustomMinimumWidth(2, 110);
			_tree.ItemSelected += ShowDetail;
			_tree.ItemActivated += () => OpenModel();
			split.AddChild(_tree);

			var right = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
			right.AddThemeConstantOverride("separation", 6);
			split.AddChild(right);
			_detail = new Label { Text = "Choisis un objet.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
			right.AddChild(_detail);
			var settings = new Button { Text = "Réglages de cet objet" };
			settings.Pressed += () => { var it = _tree.GetSelected(); if (it != null) OpenBalanceFilter?.Invoke(FilterFor(Def(it))); };
			right.AddChild(settings);
			var open = new Button { Text = "Ouvrir le modèle" };
			open.Pressed += () => { var it = _tree.GetSelected(); if (it != null) OpenModel(); };
			right.AddChild(open);

			Fill();
		}

		/// <summary>Le texte à chercher dans les réglages pour retrouver les chiffres d'un objet.</summary>
		private static string FilterFor(ItemDefinition d) => d.Type switch
		{
			ItemType.Weapon => "weapon." + d.Id,
			ItemType.Shield => "shield",
			ItemType.Consumable => "consumable",
			ItemType.Grenade => "grenade",
			ItemType.Backpack => "sac",
			ItemType.Ammo => "munitions",
			_ => d.Id
		};

		private void Fill()
		{
			_tree.Clear();
			var root = _tree.CreateItem();
			var defs = ItemDatabase.Ids.Select(ItemDatabase.Get).Where(d => d != null).OrderBy(d => d.Type).ThenBy(d => d.DisplayName).ToList();
			foreach (var d in defs)
			{
				var it = _tree.CreateItem(root);
				it.SetText(0, d.DisplayName ?? d.Id);
				it.SetText(1, TypeName(d.Type));
				it.SetText(2, d.Id);
				it.SetText(3, d.ScenePath ?? "(modèle généré)");
				it.SetMetadata(0, Variant.From(defs.IndexOf(d)));
			}
			_defs = defs;
		}

		private System.Collections.Generic.List<ItemDefinition> _defs = new();

		private ItemDefinition Def(TreeItem it) => _defs[(int)it.GetMetadata(0)];

		private void ShowDetail()
		{
			var it = _tree.GetSelected();
			if (it == null) return;
			var d = Def(it);
			_detail.Text = $"{d.DisplayName}\nType : {TypeName(d.Type)}\nIdentifiant : {d.Id}\nModèle : {d.ScenePath ?? "(généré par le jeu)"}\nTaille au sol : {(d.GroundSize > 0 ? d.GroundSize + " m" : "d'origine")}"
				+ (d.Type == ItemType.Weapon ? $"\nAccepte un viseur : {(d.AcceptsOptic ? "oui" : "non")}" : "")
				+ (d.Type == ItemType.Optic ? $"\nZoom : x{d.Zoom}" : "")
				+ (d.Type == ItemType.Backpack ? $"\nNiveau : {d.BackpackLevel}" : "");
		}

		private void OpenModel()
		{
			var it = _tree.GetSelected();
			if (it == null) return;
			var path = Def(it).ScenePath;
			if (!string.IsNullOrEmpty(path) && ResourceLoader.Exists(path)) EditorInterface.Singleton.OpenSceneFromPath(path);
		}
	}
}
#endif
