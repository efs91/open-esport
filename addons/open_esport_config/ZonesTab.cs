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
	/// Onglet « Zones » : édite les réglages de zones du battle royale (fichiers <c>data/zones/*.json</c>, au format que lit le serveur : <c>--zones</c>) avec un aperçu
	/// sur la carte : une glissière dans le temps montre la zone actuelle, la zone annoncée et le chemin des anciennes zones. Les préréglages « apex » et « fortnite » sont
	/// créés au premier lancement ; on peut les dupliquer et les modifier librement (nombre d'étapes, durées, tailles, dégâts, placement, mode cyclique…).
	/// </summary>
	[Tool]
	public partial class ZonesTab : HSplitContainer
	{
		private const string Dir = "res://data/zones/";
		private const string MapPng = "res://assets/maps/frontiere01.png";
		private const string MapWorld = "res://assets/maps/frontiere01.world";

		private OptionButton _files;
		private ZoneConfig _cfg;
		private string _currentFile;                // nom sans extension
		private bool _dirty;
		private VBoxContainer _form, _stagesBox;
		private Label _status, _info, _timeLabel;
		private ZonePreview _preview;
		private HSlider _slider;
		private SpinBox _seedBox;
		private int _seed = 1;
		private readonly List<ZonePreview.Frame> _frames = new();

		private static string AbsDir => ProjectSettings.GlobalizePath(Dir);

		public override void _Ready()
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			EnsurePresets();

			// ---- gauche : formulaire
			var left = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
			left.AddThemeConstantOverride("separation", 6);
			AddChild(left);

			var bar = new HBoxContainer();
			bar.AddThemeConstantOverride("separation", 6);
			left.AddChild(bar);
			bar.AddChild(new Label { Text = "Réglage :" });
			_files = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_files.ItemSelected += i => LoadFile(_files.GetItemText((int)i));
			bar.AddChild(_files);
			AddButton(bar, "Enregistrer", Save);
			AddButton(bar, "Dupliquer", Duplicate);
			AddButton(bar, "Supprimer", Delete);

			_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			left.AddChild(_status);

			var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			left.AddChild(scroll);
			_form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_form.AddThemeConstantOverride("separation", 6);
			scroll.AddChild(_form);

			// ---- droite : aperçu
			var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			right.AddThemeConstantOverride("separation", 6);
			AddChild(right);
			_preview = new ZonePreview { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(420, 120) };
			right.AddChild(_preview);
			var tl = new HBoxContainer();
			tl.AddThemeConstantOverride("separation", 8);
			right.AddChild(tl);
			_timeLabel = new Label { CustomMinimumSize = new Vector2(330, 0) };
			tl.AddChild(_timeLabel);
			_slider = new HSlider { MinValue = 0, MaxValue = 1, Step = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_slider.ValueChanged += v => ShowFrame((int)v);
			tl.AddChild(_slider);
			var seedRow = new HBoxContainer();
			seedRow.AddThemeConstantOverride("separation", 8);
			right.AddChild(seedRow);
			seedRow.AddChild(new Label { Text = "Graine (chaque partie en tire une au hasard) :" });
			_seedBox = new SpinBox { MinValue = 1, MaxValue = 999999, Step = 1, Value = _seed };
			_seedBox.ValueChanged += v => { _seed = (int)v; Simulate(); };
			seedRow.AddChild(_seedBox);
			AddButton(seedRow, "Autre graine", () => { _seed = new Random().Next(1, 999999); _seedBox.Value = _seed; });
			_info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			right.AddChild(_info);

			LoadMapPreview();
			RefreshFileList(null);
		}

		private static Button AddButton(Control parent, string text, Action pressed)
		{
			var b = new Button { Text = text };
			b.Pressed += pressed;
			parent.AddChild(b);
			return b;
		}

		// ------------------------------------------------------------------ fichiers

		private static void EnsurePresets()
		{
			Directory.CreateDirectory(AbsDir);
			foreach (var cfg in new[] { ZonePresets.Apex(), ZonePresets.Fortnite() })
			{
				string path = Path.Combine(AbsDir, cfg.Name + ".json");
				if (!File.Exists(path)) File.WriteAllText(path, cfg.ToJson());
			}
		}

		private void RefreshFileList(string select)
		{
			_files.Clear();
			var names = Directory.GetFiles(AbsDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n != "apex").ThenBy(n => n).ToList();
			foreach (var n in names) _files.AddItem(n);
			int idx = Math.Max(0, names.IndexOf(select ?? (names.Contains("apex") ? "apex" : names.FirstOrDefault())));
			if (names.Count > 0) { _files.Select(idx); LoadFile(names[idx]); }
		}

		private void LoadFile(string name)
		{
			try { _cfg = ZoneConfig.FromJson(File.ReadAllText(Path.Combine(AbsDir, name + ".json"))); }
			catch (Exception e) { SetStatus($"« {name} » illisible : {e.Message}"); return; }
			_currentFile = name;
			_dirty = false;
			SetStatus($"Réglage « {name} » chargé. Fichier : data/zones/{name}.json — pour l'utiliser : serveur « --zones data/zones/{name}.json », ou matchmaking « --zones-file ».");
			BuildForm();
			Simulate();
		}

		private void Save()
		{
			if (_cfg == null) return;
			try
			{
				_cfg.Validated();
				File.WriteAllText(Path.Combine(AbsDir, _currentFile + ".json"), _cfg.ToJson());
				_dirty = false;
				SetStatus($"Enregistré : data/zones/{_currentFile}.json");
			}
			catch (Exception e) { SetStatus("Réglage invalide : " + e.Message); }
		}

		private void Duplicate()
		{
			if (_cfg == null) return;
			string name = _currentFile + "_copie";
			int n = 2;
			while (File.Exists(Path.Combine(AbsDir, name + ".json"))) name = $"{_currentFile}_copie{n++}";
			var copy = ZoneConfig.FromJson(_cfg.ToJson());
			copy.Name = name;
			File.WriteAllText(Path.Combine(AbsDir, name + ".json"), copy.ToJson());
			RefreshFileList(name);
		}

		private void Delete()
		{
			if (_currentFile == null || _currentFile == "apex" || _currentFile == "fortnite") { SetStatus("Les préréglages « apex » et « fortnite » ne se suppriment pas (duplique-les pour en faire d'autres)."); return; }
			File.Delete(Path.Combine(AbsDir, _currentFile + ".json"));
			RefreshFileList("apex");
		}

		private void SetStatus(string t) { if (_status != null) _status.Text = t; }
		private void Changed() { _dirty = true; SetStatus($"« {_currentFile} » : modifications non enregistrées."); Simulate(); }

		// ------------------------------------------------------------------ formulaire

		private SpinBox Spin(double value, double min, double max, double step, Action<double> set, string suffix = "")
		{
			var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, AllowGreater = true, CustomMinimumSize = new Vector2(96, 0) };
			s.ValueChanged += v => { set(v); Changed(); };
			return s;
		}

		private void Line(Control parent, string label, Control field, string tip = "")
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 8);
			var l = new Label { Text = label, CustomMinimumSize = new Vector2(250, 0), TooltipText = tip, AutowrapMode = TextServer.AutowrapMode.WordSmart };
			row.AddChild(l);
			field.TooltipText = tip;
			row.AddChild(field);
			parent.AddChild(row);
		}

		private void BuildForm()
		{
			foreach (var c in _form.GetChildren()) c.QueueFree();
			_stagesBox = null;
			if (_cfg == null) return;

			_form.AddChild(new Label { Text = "Général", ThemeTypeVariation = "HeaderMedium" });
			var name = new LineEdit { Text = _cfg.Name, Editable = false, CustomMinimumSize = new Vector2(200, 0) };
			Line(_form, "Nom (= nom du fichier)", name);
			var refOpt = new OptionButton();
			refOpt.AddItem("Diagonale (la zone 1 couvre toute la carte)"); refOpt.AddItem("Demi-côté (la zone 1 est le cercle inscrit)");
			refOpt.Select(_cfg.Reference == "half-width" ? 1 : 0);
			refOpt.ItemSelected += i => { _cfg.Reference = i == 1 ? "half-width" : "diagonal"; Changed(); };
			Line(_form, "Rayon de référence (100 %)", refOpt, "Les tailles de zones sont des pourcentages de ce rayon.");
			Line(_form, "Taille de la zone 1", Spin(_cfg.FirstRadius, 1, 300, 1, v => _cfg.FirstRadius = (float)v, " %"));
			Line(_form, "Intervalle des coups hors zone", Spin(_cfg.TickSeconds, 0.1, 10, 0.1, v => _cfg.TickSeconds = (float)v, " s"), "Apex : un coup toutes les 1,5 s.");
			Line(_form, "Dégâts une fois la zone refermée", Spin(_cfg.FinalDamage, 0, 500, 1, v => _cfg.FinalDamage = (float)v), "Quand plus aucune place n'est sûre.");
			var ign = new CheckBox { Text = "Les dégâts passent à travers le bouclier", ButtonPressed = _cfg.IgnoreShield };
			ign.Toggled += on => { _cfg.IgnoreShield = on; Changed(); };
			Line(_form, "Bouclier", ign);

			_form.AddChild(new HSeparator());
			_form.AddChild(new Label { Text = "Placement de la zone suivante", ThemeTypeVariation = "HeaderMedium" });
			var cont = new OptionButton();
			cont.AddItem("Dans la zone actuelle (comme Apex)"); cont.AddItem("Se déplace et chevauche (comme Fortnite)"); cont.AddItem("N'importe où sur la carte");
			cont.Select(_cfg.Containment == "overlap" ? 1 : _cfg.Containment == "free" ? 2 : 0);
			cont.ItemSelected += i => { _cfg.Containment = i == 1 ? "overlap" : i == 2 ? "free" : "inside"; Changed(); };
			Line(_form, "Mode", cont);
			Line(_form, "Décalage maximal du centre", Spin(_cfg.MaxShift, 0, 3, 0.05, v => _cfg.MaxShift = (float)v), "Mode « chevauche » : en fraction du rayon actuel.");
			Line(_form, "Chance de revenir sur une ancienne zone", Spin(_cfg.RevisitChance * 100, 0, 100, 5, v => _cfg.RevisitChance = (float)(v / 100.0), " %"), "La nouvelle zone est recentrée sur un ancien emplacement.");

			_form.AddChild(new HSeparator());
			_form.AddChild(new Label { Text = "Étapes (la zone i devient la zone i+1)", ThemeTypeVariation = "HeaderMedium" });
			_form.AddChild(new Label
			{
				Text = "Attente : la zone actuelle est seule.  Annonce : la zone suivante est dessinée, rien ne bouge.  Réduction : la zone actuelle se réduit jusqu'à la suivante.  Rayon 0 % : la zone disparaît (plus aucune place).",
				AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.65f)
			});
			_stagesBox = new VBoxContainer();
			_stagesBox.AddThemeConstantOverride("separation", 3);
			_form.AddChild(_stagesBox);
			BuildStages();
			AddButton(_form, "+ Ajouter une étape", () => { _cfg.Stages.Add(new ZoneStageConfig { Idle = 0, Warning = 60, Shrink = 60, Radius = Math.Max(0, (_cfg.Stages.LastOrDefault()?.Radius ?? 50f) * 0.5f), Damage = 5 }); BuildStages(); Changed(); });

			_form.AddChild(new HSeparator());
			_form.AddChild(new Label { Text = "Mode cyclique (zone qui bouge presque indéfiniment)", ThemeTypeVariation = "HeaderMedium" });
			var cyc = new CheckBox { Text = "Continuer après la dernière étape", ButtonPressed = _cfg.Cycle };
			cyc.Toggled += on => { _cfg.Cycle = on; Changed(); };
			Line(_form, "Cycle", cyc, "La dernière étape doit avoir un rayon > 0 : ensuite la zone rétrécit par cycles, puis repart plus large.");
			Line(_form, "Annonce par cycle", Spin(_cfg.CycleWarning, 0, 600, 1, v => _cfg.CycleWarning = (float)v, " s"));
			Line(_form, "Réduction par cycle", Spin(_cfg.CycleShrink, 1, 600, 1, v => _cfg.CycleShrink = (float)v, " s"));
			Line(_form, "Dégâts par cycle", Spin(_cfg.CycleDamage, 0, 500, 1, v => _cfg.CycleDamage = (float)v));
			Line(_form, "Le rayon est multiplié par", Spin(_cfg.CycleRatio, 0.1, 1, 0.01, v => _cfg.CycleRatio = (float)v));
			Line(_form, "Rayon minimal", Spin(_cfg.CycleMinRadius, 1, 100, 1, v => _cfg.CycleMinRadius = (float)v, " %"), "Dessous, le rayon repart de « rayon de relance ».");
			Line(_form, "Rayon de relance", Spin(_cfg.CycleResetRadius, 1, 300, 1, v => _cfg.CycleResetRadius = (float)v, " %"));
		}

		private void BuildStages()
		{
			foreach (var c in _stagesBox.GetChildren()) c.QueueFree();
			var head = new HBoxContainer();
			head.AddThemeConstantOverride("separation", 4);
			foreach (var (t, w) in new[] { ("#", 24), ("Attente", 92), ("Annonce", 92), ("Réduction", 92), ("Rayon", 84), ("Dégâts", 78) })
				head.AddChild(new Label { Text = t, CustomMinimumSize = new Vector2(w, 0), Modulate = new Color(1, 1, 1, 0.65f) });
			_stagesBox.AddChild(head);
			for (int i = 0; i < _cfg.Stages.Count; i++)
			{
				int idx = i; var s = _cfg.Stages[i];
				var row = new HBoxContainer();
				row.AddThemeConstantOverride("separation", 4);
				row.AddChild(new Label { Text = (i + 1).ToString(), CustomMinimumSize = new Vector2(24, 0) });
				row.AddChild(Spin(s.Idle, 0, 3600, 1, v => s.Idle = (float)v, " s"));
				row.AddChild(Spin(s.Warning, 0, 3600, 1, v => s.Warning = (float)v, " s"));
				row.AddChild(Spin(s.Shrink, 1, 3600, 1, v => s.Shrink = (float)v, " s"));
				row.AddChild(Spin(s.Radius, 0, 300, 1, v => s.Radius = (float)v, " %"));
				row.AddChild(Spin(s.Damage, 0, 500, 1, v => s.Damage = (float)v));
				AddButton(row, "↑", () => { if (idx > 0) { (_cfg.Stages[idx - 1], _cfg.Stages[idx]) = (_cfg.Stages[idx], _cfg.Stages[idx - 1]); BuildStages(); Changed(); } });
				AddButton(row, "↓", () => { if (idx < _cfg.Stages.Count - 1) { (_cfg.Stages[idx + 1], _cfg.Stages[idx]) = (_cfg.Stages[idx], _cfg.Stages[idx + 1]); BuildStages(); Changed(); } });
				AddButton(row, "⧉", () => { _cfg.Stages.Insert(idx + 1, new ZoneStageConfig { Idle = s.Idle, Warning = s.Warning, Shrink = s.Shrink, Radius = s.Radius, Damage = s.Damage }); BuildStages(); Changed(); });
				AddButton(row, "✕", () => { _cfg.Stages.RemoveAt(idx); BuildStages(); Changed(); });
				_stagesBox.AddChild(row);
			}
		}

		// ------------------------------------------------------------------ aperçu

		private float _minX = -760, _maxX = 760, _minZ = -760, _maxZ = 760;

		private void LoadMapPreview()
		{
			try
			{
				var bytes = Godot.FileAccess.GetFileAsBytes(MapWorld);
				if (bytes != null && bytes.Length > 0)
				{
					var d = WorldData.Load(new MemoryStream(bytes));
					_minX = d.MinX; _maxX = d.MaxX; _minZ = d.MinZ; _maxZ = d.MaxZ;
				}
			}
			catch (Exception) { }
			_preview.Setup(GD.Load<Texture2D>(MapPng), _minX, _maxX, _minZ, _maxZ);
		}

		/// <summary>Déroule tout le réglage (une image par seconde) pour pouvoir parcourir le temps avec la glissière.</summary>
		private void Simulate()
		{
			_frames.Clear();
			if (_cfg == null) return;
			ZoneRunner runner;
			try
			{
				runner = new ZoneRunner(ZoneConfig.FromJson(_cfg.ToJson()), _minX, _maxX, _minZ, _maxZ, _seed);
				runner.Start();
			}
			catch (Exception e) { _info.Text = "Réglage invalide : " + e.Message; _preview.SetFrames(_frames, 0); return; }

			const int Cap = 3 * 3600;
			var phaseStarts = new List<(int t, string text)>();
			int lastStage = 0; ZonePhase lastPhase = ZonePhase.None;
			for (int t = 0; t <= Cap; t++)
			{
				runner.CurrentCircle(out float cx, out float cz, out float r);
				runner.NextCircle(out float nx, out float nz, out float nr);
				_frames.Add(new ZonePreview.Frame { T = t, Phase = runner.Phase, Stage = runner.Stage, Cx = cx, Cz = cz, R = r, NextVisible = runner.NextVisible, Nx = nx, Nz = nz, Nr = nr, Damage = runner.Damage, Ref = runner.ReferenceRadius });
				if (runner.Phase != lastPhase || runner.Stage != lastStage) { phaseStarts.Add((t, $"{Mm(t)} zone {runner.Stage} : {PhaseName(runner.Phase)}")); lastPhase = runner.Phase; lastStage = runner.Stage; }
				if (runner.Phase == ZonePhase.Collapsed) break;
				for (int k = 0; k < 60; k++) runner.Update(1f / 60f);
			}
			bool endless = _frames.Count > Cap;
			_slider.MaxValue = Math.Max(1, _frames.Count - 1);
			_slider.Value = Math.Min(_slider.Value, _slider.MaxValue);
			_info.Text = (endless ? "Durée : sans fin (mode cyclique) — aperçu des 3 premières heures.\n" : $"Durée totale : {Mm(_frames.Count - 1)} jusqu'à la fermeture complète.\n")
				+ string.Join("\n", phaseStarts.Take(24).Select(p => p.text)) + (phaseStarts.Count > 24 ? "\n…" : "");
			_preview.SetFrames(_frames, (int)_slider.Value);
			ShowFrame((int)_slider.Value);
		}

		private static string Mm(int s) => $"{s / 60}:{s % 60:00}";
		private static string PhaseName(ZonePhase p) => p switch { ZonePhase.Idle => "seule", ZonePhase.Warning => "prochaine zone annoncée", ZonePhase.Shrinking => "se réduit", ZonePhase.Collapsed => "refermée", _ => "" };

		private void ShowFrame(int i)
		{
			if (_frames.Count == 0) { _timeLabel.Text = ""; return; }
			i = Math.Clamp(i, 0, _frames.Count - 1);
			var f = _frames[i];
			_timeLabel.Text = $"t = {Mm(f.T)} — zone {f.Stage} : {PhaseName(f.Phase)} — rayon {f.R:0} m ({(f.Ref > 0 ? f.R / f.Ref * 100 : 0):0} %) — {f.Damage:0.#} dégâts/coup";
			_preview.SetFrames(_frames, i);
		}
	}

	/// <summary>Carte avec la zone à l'instant choisi : cercle sûr (bleu), zone suivante (blanc), zone dangereuse teintée, chemin des anciennes zones.</summary>
	[Tool]
	public partial class ZonePreview : Control
	{
		public struct Frame { public int T; public ZonePhase Phase; public int Stage; public float Cx, Cz, R, Nx, Nz, Nr, Damage, Ref; public bool NextVisible; }

		private Texture2D _tex;
		private float _minX, _maxX, _minZ, _maxZ;
		private List<Frame> _frames = new();
		private int _index;

		public void Setup(Texture2D tex, float minX, float maxX, float minZ, float maxZ) { _tex = tex; _minX = minX; _maxX = maxX; _minZ = minZ; _maxZ = maxZ; QueueRedraw(); }
		public void SetFrames(List<Frame> frames, int index) { _frames = frames; _index = index; QueueRedraw(); }

		public override void _Ready() { ClipContents = true; }

		public override void _Draw()
		{
			DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.05f, 0.06f, 0.08f));
			float side = Mathf.Min(Size.X, Size.Y) - 8f;
			var origin = new Vector2((Size.X - side) * 0.5f, (Size.Y - side) * 0.5f);
			if (_tex != null) DrawTextureRect(_tex, new Rect2(origin, new Vector2(side, side)), false);
			DrawRect(new Rect2(origin, new Vector2(side, side)), new Color(1, 1, 1, 0.8f), false, 2f);
			if (_frames.Count == 0) return;
			float kx = side / (_maxX - _minX);
			Vector2 P(float x, float z) => origin + new Vector2((x - _minX) / (_maxX - _minX), (z - _minZ) / (_maxZ - _minZ)) * side;

			int idx = Mathf.Clamp(_index, 0, _frames.Count - 1);
			var f = _frames[idx];
			// chemin des centres des zones déjà passées
			Vector2? prev = null; int lastStage = 0;
			for (int i = 0; i <= idx; i++)
			{
				var g = _frames[i];
				if (g.Stage == lastStage) continue;
				lastStage = g.Stage;
				var p = P(g.Cx, g.Cz);
				if (prev.HasValue) DrawLine(prev.Value, p, new Color(1f, 0.9f, 0.3f, 0.5f), 1.5f, true);
				DrawCircle(p, 3f, new Color(1f, 0.9f, 0.3f, 0.8f));
				DrawString(ThemeDB.FallbackFont, p + new Vector2(5, -4), g.Stage.ToString(), HorizontalAlignment.Left, -1, 12, new Color(1f, 0.9f, 0.3f));
				prev = p;
			}
			var c = P(f.Cx, f.Cz); float r = f.R * kx;
			const float Big = 4000f;
			var tint = new Color(0.45f, 0.1f, 0.8f, 0.30f);
			if (r > 0.5f) DrawArc(c, r + Big * 0.5f, 0f, Mathf.Tau, 160, tint, Big, false); else DrawRect(new Rect2(origin, new Vector2(side, side)), tint);
			if (r > 0.5f) DrawArc(c, r, 0f, Mathf.Tau, 128, new Color(0.25f, 0.6f, 1f), 3f, true);
			if (f.NextVisible && f.Nr * kx > 0.5f) DrawArc(P(f.Nx, f.Nz), f.Nr * kx, 0f, Mathf.Tau, 128, Colors.White, 2.5f, true);
		}
	}
}
#endif
