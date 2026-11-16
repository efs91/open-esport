#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using OpenEsport.Core;

namespace Openesport.EditorTools
{
	/// <summary>
	/// Onglet « Réglages du jeu » : tous les réglages enregistrés dans <see cref="GameBalance"/> (déplacement, glissade, balles, zones touchées, armes, niveaux d'équipement,
	/// boucliers, soins, butin, bots, zones…) sont affichés automatiquement, groupés, avec leur description, leur unité et leur valeur par défaut. Un nouveau réglage
	/// ajouté dans le code (<c>GameBalance.Define</c>) apparaît ici sans rien modifier. « Enregistrer » écrit <c>balance.json</c>, que lisent le serveur et les conteneurs.
	/// </summary>
	[Tool]
	public partial class BalanceTab : VBoxContainer
	{
		private sealed class Row
		{
			public GameBalance.Entry Entry;
			public Control Box;
			public SpinBox Spin;
			public Button Reset;
			public string SearchText;
			public string Group;
		}

		private readonly List<Row> _rows = new();
		private readonly Dictionary<string, (Button header, VBoxContainer body, Label count)> _groups = new();
		private LineEdit _filter;
		private VBoxContainer _list;
		private Label _status;
		private bool _dirty;

		private static string RootFile => ProjectSettings.GlobalizePath("res://balance.json");
		private static string NetcodeFile => ProjectSettings.GlobalizePath("res://netcode/balance.json");

		public override void _Ready()
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			AddThemeConstantOverride("separation", 6);

			var bar = new HBoxContainer();
			bar.AddThemeConstantOverride("separation", 8);
			AddChild(bar);
			_filter = new LineEdit { PlaceholderText = "Rechercher un réglage (arme, vitesse, soin, bot…)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
			_filter.TextChanged += _ => ApplyFilter();
			bar.AddChild(_filter);
			var save = new Button { Text = "Enregistrer" };
			save.Pressed += Save;
			bar.AddChild(save);
			var reload = new Button { Text = "Recharger" };
			reload.Pressed += () => { LoadFromDisk(); Rebuild(); };
			bar.AddChild(reload);
			var defaults = new Button { Text = "Tout remettre par défaut" };
			defaults.Pressed += () => { GameBalance.ResetToDefaults(); _dirty = true; Rebuild(); SetStatus("Valeurs par défaut rétablies (pas encore enregistrées)."); };
			bar.AddChild(defaults);

			_status = new Label { Text = "" };
			AddChild(_status);

			var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			AddChild(scroll);
			_list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_list.AddThemeConstantOverride("separation", 2);
			scroll.AddChild(_list);

			LoadFromDisk();
			Rebuild();
		}

		public void SetFilter(string text) { if (_filter != null) { _filter.Text = text; ApplyFilter(); } }

		// ------------------------------------------------------------------ lecture / écriture

		private void LoadFromDisk()
		{
			GameBalance.ResetToDefaults();
			_dirty = false;
			if (!File.Exists(RootFile)) { SetStatus("Pas de balance.json : valeurs par défaut."); return; }
			var warnings = new List<string>();
			try { GameBalance.ApplyFileText(File.ReadAllText(RootFile), warnings); }
			catch (Exception e) { SetStatus("balance.json illisible : " + e.Message); return; }
			SetStatus(warnings.Count == 0 ? $"balance.json chargé ({GameBalance.ModifiedCount} réglage(s) modifié(s))." : "balance.json chargé, avec des avertissements : " + string.Join(" ; ", warnings));
		}

		private void Save()
		{
			try
			{
				string text = GameBalance.ToFileText();
				File.WriteAllText(RootFile, text);
				string both = "";
				// le serveur lit le balance.json du dossier où il est lancé : on garde la copie de netcode/ identique pour qu'aucun des deux ne contredise l'autre
				if (File.Exists(NetcodeFile)) { File.WriteAllText(NetcodeFile, text); both = " et netcode/balance.json"; }
				_dirty = false;
				SetStatus($"Enregistré dans balance.json{both}. Les nouvelles parties l'utilisent (relance le serveur ; les conteneurs lancés ensuite le lisent au démarrage).");
				RefreshRows();
			}
			catch (Exception e) { SetStatus("Enregistrement impossible : " + e.Message); }
		}

		private void SetStatus(string text) { if (_status != null) _status.Text = text; }

		// ------------------------------------------------------------------ interface

		private static string GroupKey(GameBalance.Entry e)
		{
			var p = e.Key.Split('.');
			return p[0] == "weapon" && p.Length > 2 ? p[0] + "." + p[1] : p[0];
		}

		private static string GroupTitle(string key)
		{
			var p = key.Split('.');
			string head = p[0] switch
			{
				"movement" => "Déplacement", "slide" => "Glissade", "bullet" => "Balles", "hit" => "Zones touchées", "weapon" => "Armes",
				"tier" => "Niveaux d'équipement (blanc / bleu / violet / rouge)", "mode" => "Modes de jeu (Deathmatch, drapeau, contrôle…)", "sabotage" => "Sabotage (manches, argent, bombe)", "zone" => "Zone (dégâts hors zone)", "shield" => "Boucliers", "consumable" => "Soins", "loot" => "Butin", "bot" => "Bots",
				_ => p[0]
			};
			return p.Length > 1 ? $"{head} › {p[1]}" : head;
		}

		private void Rebuild()
		{
			foreach (var c in _list.GetChildren()) c.QueueFree();
			_rows.Clear(); _groups.Clear();

			foreach (var e in GameBalance.Entries)
			{
				string g = GroupKey(e);
				if (!_groups.TryGetValue(g, out var grp))
				{
					var header = new Button { Text = "", Flat = true, Alignment = HorizontalAlignment.Left, ToggleMode = true, ButtonPressed = false };
					var body = new VBoxContainer { Visible = false };
					body.AddThemeConstantOverride("separation", 3);
					var count = new Label();
					header.Toggled += on => { body.Visible = on; UpdateHeader(g); };
					_list.AddChild(header);
					var margin = new MarginContainer();
					margin.AddThemeConstantOverride("margin_left", 24);
					margin.AddChild(body);
					_list.AddChild(margin);
					grp = (header, body, count);
					_groups[g] = grp;
				}
				var row = MakeRow(e, g);
				grp.body.AddChild(row.Box);
				_rows.Add(row);
			}
			foreach (var g in _groups.Keys) UpdateHeader(g);
			ApplyFilter();
		}

		private Row MakeRow(GameBalance.Entry e, string group)
		{
			var box = new HBoxContainer();
			box.AddThemeConstantOverride("separation", 8);
			var name = new Label { Text = e.Description, TooltipText = e.Key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(380, 0) };
			box.AddChild(name);
			var spin = new SpinBox
			{
				MinValue = 0, MaxValue = 1_000_000, Step = e.Integer ? 1 : 0.01, AllowGreater = true, Value = e.Value,
				CustomMinimumSize = new Vector2(120, 0), TooltipText = e.Key
			};
			box.AddChild(spin);
			box.AddChild(new Label { Text = e.Unit, CustomMinimumSize = new Vector2(46, 0) });
			var reset = new Button { Text = "↺", TooltipText = $"Remettre la valeur par défaut ({e.Default:0.####})" };
			box.AddChild(reset);
			box.AddChild(new Label { Text = $"défaut {e.Default:0.####}", CustomMinimumSize = new Vector2(90, 0), Modulate = new Color(1, 1, 1, 0.55f) });
			var row = new Row { Entry = e, Box = box, Spin = spin, Reset = reset, Group = group, SearchText = (e.Key + " " + e.Description + " " + GroupTitle(group)).ToLowerInvariant() };
			spin.ValueChanged += v => { e.Value = e.Integer ? Math.Round(v) : v; _dirty = true; RefreshRow(row); UpdateHeader(group); SetStatus("Modifications non enregistrées."); };
			reset.Pressed += () => { spin.Value = e.Default; };
			RefreshRow(row);
			return row;
		}

		private static void RefreshRow(Row r)
		{
			bool modified = Math.Abs(r.Entry.Value - r.Entry.Default) > 1e-9;
			r.Reset.Disabled = !modified;
			r.Spin.Modulate = modified ? new Color(1f, 0.9f, 0.45f) : Colors.White;
		}

		private void RefreshRows() { foreach (var r in _rows) RefreshRow(r); }

		private void UpdateHeader(string g)
		{
			if (!_groups.TryGetValue(g, out var grp)) return;
			int total = _rows.Count(r => r.Group == g), modified = _rows.Count(r => r.Group == g && Math.Abs(r.Entry.Value - r.Entry.Default) > 1e-9);
			grp.header.Text = $"{(grp.header.ButtonPressed ? "▾" : "▸")}  {GroupTitle(g)}   ({total}{(modified > 0 ? $", {modified} modifié(s)" : "")})";
		}

		private void ApplyFilter()
		{
			string f = (_filter?.Text ?? "").Trim().ToLowerInvariant();
			var visibleGroups = new HashSet<string>();
			foreach (var r in _rows)
			{
				bool show = f.Length == 0 || f.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => r.SearchText.Contains(w));
				r.Box.Visible = show;
				if (show) visibleGroups.Add(r.Group);
			}
			foreach (var kv in _groups)
			{
				bool any = visibleGroups.Contains(kv.Key);
				kv.Value.header.Visible = any;
				kv.Value.body.GetParent<Control>().Visible = any;
				// une recherche ouvre les groupes qui ont des résultats
				if (f.Length > 0 && any && !kv.Value.header.ButtonPressed) kv.Value.header.ButtonPressed = true;
			}
		}
	}
}
#endif
