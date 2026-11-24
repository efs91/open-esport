#if TOOLS
using System;
using Godot;

namespace Openesport.EditorTools
{
	/// <summary>La fenêtre du panneau : un onglet par famille de réglages. Pour en ajouter un, créer un contrôle et l'ajouter ici (Add).</summary>
	[Tool]
	public partial class ConfigPanel : VBoxContainer
	{
		private TabContainer _tabs;

		public override void _Ready()
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			CustomMinimumSize = new Vector2(480, 160);
			_tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			AddChild(_tabs);

			BalanceTab balance = null;
			Add("Réglages du jeu", () => balance = new BalanceTab());
			Add("Zones (battle royale)", () => new ZonesTab());
			Add("Objets", () => new ItemsTab { OpenBalanceFilter = text => { balance?.SetFilter(text); _tabs.CurrentTab = 0; } });
			Add("Carte (export)", () => new MapTab());
			Add("Export du jeu", () => new ExportTab());
			Add("Réseau", () => new NetworkTab());
			GD.Print($"[Open Esport] panneau créé : {_tabs.GetTabCount()} onglet(s)");
		}

		/// <summary>Ajoute un onglet ; si sa création échoue, l'onglet affiche l'erreur au lieu de laisser le panneau vide.</summary>
		private void Add(string title, Func<Control> make)
		{
			Control tab;
			try { tab = make(); }
			catch (Exception e)
			{
				GD.PushError($"[Open Esport] onglet « {title} » : {e}");
				tab = new RichTextLabel { Text = $"Impossible de créer l'onglet « {title} » :\n{e}", SelectionEnabled = true };
			}
			tab.Name = title;
			try { _tabs.AddChild(tab); }
			catch (Exception e)
			{
				GD.PushError($"[Open Esport] onglet « {title} » (ajout) : {e}");
				if (tab.GetParent() == null) { tab.QueueFree(); _tabs.AddChild(new RichTextLabel { Name = title, Text = $"Erreur à l'ouverture de l'onglet « {title} » :\n{e}", SelectionEnabled = true }); }
			}
		}
	}
}
#endif
