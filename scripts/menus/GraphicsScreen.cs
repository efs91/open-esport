using System;
using Godot;
using Openesport.Utils;
using G = Openesport.Utils.GraphicsSettings;

namespace Openesport.Menus
{
	/// <summary>
	/// Page « Graphismes » (Paramètres) : plein écran ou fenêtre, synchro verticale, limite d'images, puis un préréglage de qualité
	/// et ses réglages fins pour les machines moins puissantes. Les choix s'appliquent (et s'enregistrent) avec « Appliquer » ;
	/// « Retour » sans appliquer les abandonne.
	/// </summary>
	public partial class GraphicsScreen : CanvasLayer
	{
		public event Action Closed;

		private OptionButton _preset, _scale, _aa, _shadows, _effects;
		private bool _updating;
		private Button _apply;
		/// <summary>Réglages en vigueur (ceux du dernier « Appliquer »).</summary>
		private G.State _applied;

		public GraphicsScreen() { Layer = 120; }

		public override void _Ready()
		{
			_applied = G.Current;
			AddChild(MenuStyle.Backdrop());
			var center = new CenterContainer { Theme = MenuStyle.SharedTheme };
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(center);
			// compact : tout tient dans l'écran de référence (1152 × 648), boutons « Appliquer » et « Retour » compris ; au besoin, les réglages défilent
			var column = new VBoxContainer();
			column.AddThemeConstantOverride("separation", 8);
			center.AddChild(MenuStyle.Card(column, 560f, 18));

			var title = new Label { Text = I18n.T("Graphismes"), HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 24);
			column.AddChild(title);

			var grid = new GridContainer { Columns = 2 };
			grid.AddThemeConstantOverride("h_separation", 24);
			grid.AddThemeConstantOverride("v_separation", 5);
			_scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			_scroll.AddChild(grid);
			column.AddChild(_scroll);
			_grid = grid;

			Section(grid, I18n.T("Affichage"));
			Row(grid, I18n.T("Mode"), Options(new[] { I18n.T("Plein écran"), I18n.T("Fenêtre") }, G.Fullscreen ? 0 : 1, i => { G.Fullscreen = i == 0; Changed(); }));
			Row(grid, I18n.T("Synchro verticale"), Options(new[] { I18n.T("Activée"), I18n.T("Désactivée") }, G.VSync ? 0 : 1, i => { G.VSync = i == 0; Changed(); }));
			var fps = Array.ConvertAll(G.FpsLimits, f => f == 0 ? I18n.T("Sans limite") : I18n.T("{0} images/s", f));
			Row(grid, I18n.T("Limite d'images"), Options(fps, Math.Max(0, Array.IndexOf(G.FpsLimits, G.MaxFps)), i => { G.MaxFps = G.FpsLimits[i]; Changed(); }));

			Section(grid, I18n.T("Qualité"));
			_preset = Options(new[] { I18n.T("Basse"), I18n.T("Moyenne"), I18n.T("Haute"), I18n.T("Ultra"), I18n.T("Personnalisée") }, (int)G.Quality, i =>
			{
				if (i == (int)G.Preset.Custom) { G.Quality = G.Preset.Custom; Changed(); return; }
				G.SetPreset((G.Preset)i);
				Refresh();
				Changed();
			});
			Row(grid, I18n.T("Préréglage"), _preset);
			_scale = Options(Array.ConvertAll(G.RenderScales, s => s >= 0.99f ? I18n.T("100 % (native)") : $"{Mathf.RoundToInt(s * 100)} % (FSR)"), 0, i => { G.RenderScale = G.RenderScales[i]; Custom(); });
			Row(grid, I18n.T("Résolution de rendu"), _scale);
			_aa = Options(new[] { I18n.T("Aucun"), "FXAA", "MSAA 2x", "MSAA 4x" }, 0, i => { G.Aa = (G.AntiAliasing)i; Custom(); });
			Row(grid, I18n.T("Anticrénelage"), _aa);
			var levels = new[] { I18n.T("Désactivées"), I18n.T("Basses"), I18n.T("Moyennes"), I18n.T("Hautes") };
			_shadows = Options(levels, 0, i => { G.Shadows = (G.Level)i; Custom(); });
			Row(grid, I18n.T("Ombres"), _shadows);
			_effects = Options(new[] { I18n.T("Aucun"), I18n.T("Bas"), I18n.T("Moyens"), I18n.T("Hauts") }, 0, i => { G.Effects = (G.Level)i; Custom(); });
			Row(grid, I18n.T("Effets de lumière"), _effects);
			Refresh();

			var hint = new Label { Text = I18n.T("Machine peu puissante : baisse d'abord la résolution de rendu et les effets de lumière, puis les ombres."), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, Modulate = MenuStyle.Muted };
			hint.AddThemeFontSizeOverride("font_size", 13);
			column.AddChild(hint);

			var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			buttons.AddThemeConstantOverride("separation", 16);
			column.AddChild(buttons);
			_apply = new Button { ThemeTypeVariation = "MenuPrimary", Text = I18n.T("Appliquer"), CustomMinimumSize = new Vector2(200, 38), Disabled = true };
			_apply.Pressed += Apply;
			buttons.AddChild(_apply);
			var close = new Button { Text = I18n.T("Retour"), CustomMinimumSize = new Vector2(200, 38) };
			close.Pressed += Close;
			buttons.AddChild(close);
			close.GrabFocus();
			FitScroll();
			CallDeferred(nameof(FitScroll));          // après la première mise en page (tailles des polices connues)
			GetViewport().SizeChanged += FitScroll;
		}

		private ScrollContainer _scroll;
		private GridContainer _grid;

		public override void _ExitTree() { if (GetViewport() != null) GetViewport().SizeChanged -= FitScroll; }

		/// <summary>La zone des réglages prend la hauteur de son contenu, sans dépasser ce que l'écran laisse après le titre et les boutons.</summary>
		private void FitScroll()
		{
			if (_scroll == null || !IsInstanceValid(_scroll)) return;
			float available = GetViewport().GetVisibleRect().Size.Y - 230f;
			_scroll.CustomMinimumSize = new Vector2(_grid.GetCombinedMinimumSize().X, Mathf.Max(120f, Mathf.Min(_grid.GetCombinedMinimumSize().Y, available)));
		}

		private static void Section(GridContainer grid, string text)
		{
			var label = new Label { Text = text };
			label.AddThemeFontSizeOverride("font_size", 18);
			grid.AddChild(label);
			grid.AddChild(new Control());
		}

		private static void Row(GridContainer grid, string text, Control control)
		{
			grid.AddChild(new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center });
			grid.AddChild(control);
		}

		private OptionButton Options(string[] items, int selected, Action<int> onChange)
		{
			var b = new OptionButton { CustomMinimumSize = new Vector2(240, 32) };
			foreach (var item in items) b.AddItem(item);
			b.Select(selected);
			b.ItemSelected += i => { if (!_updating) onChange((int)i); };
			return b;
		}

		/// <summary>Les réglages fins affichent les valeurs en cours (après un préréglage).</summary>
		private void Refresh()
		{
			_updating = true;
			_preset.Select((int)G.Quality);
			int scale = Array.FindIndex(G.RenderScales, s => Mathf.IsEqualApprox(s, G.RenderScale));
			_scale.Select(scale < 0 ? G.RenderScales.Length - 1 : scale);
			_aa.Select((int)G.Aa);
			_shadows.Select((int)G.Shadows);
			_effects.Select((int)G.Effects);
			_updating = false;
		}

		/// <summary>Un réglage fin changé à la main : le préréglage devient « Personnalisée ».</summary>
		private void Custom()
		{
			G.Quality = G.Preset.Custom;
			Refresh();
			Changed();
		}

		/// <summary>Un réglage a changé : il attend « Appliquer ».</summary>
		private void Changed() => _apply.Disabled = G.Current == _applied;

		private void Apply()
		{
			G.ApplyAll();
			G.Save();
			_applied = G.Current;
			_apply.Disabled = true;
		}

		public override void _Input(InputEvent e)
		{
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); }
		}

		private void Close()
		{
			G.Restore(_applied);        // choix non appliqués : abandonnés
			Closed?.Invoke();
			QueueFree();
		}
	}
}
