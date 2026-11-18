using Openesport.Utils;
using System;
using Godot;
using OpenEsport.Core;
using Openesport.Online;

namespace Openesport.Menus
{
	/// <summary>
	/// Page « Mode de jeu » du menu Multijoueur : on choisit le mode (Battle Royale, Deathmatch, Team Deathmatch, Capture du drapeau, Sabotage, Contrôle) puis la taille
	/// d'équipe proposée par ce mode. « Continuer » ouvre le choix du personnage, puis le matchmaking cherche une partie de ce mode.
	/// </summary>
	public partial class ModeScreen : CanvasLayer
	{
		public Action OnContinue;
		public Action OnBack;

		private GameMode _mode;
		private int _teamSize;
		private Label _description, _sizeTitle;
		private HBoxContainer _sizes;
		private readonly System.Collections.Generic.Dictionary<GameMode, Button> _modeButtons = new();

		public ModeScreen() { Layer = 110; }

		public override void _Ready()
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
			_mode = MatchSettings.Mode;
			_teamSize = MatchSettings.TeamSize;

			var root = new Control();
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);
			root.AddChild(MenuStyle.Backdrop());
			var center = new CenterContainer();
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(center);
			var box = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
			box.AddThemeConstantOverride("separation", 14);
			center.AddChild(MenuStyle.Card(box, margin: 28));

			var title = new Label { Text = I18n.T("Mode de jeu"), HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 34);
			box.AddChild(title);

			var grid = new GridContainer { Columns = 3 };
			grid.AddThemeConstantOverride("h_separation", 10);
			grid.AddThemeConstantOverride("v_separation", 10);
			box.AddChild(grid);
			var group = new ButtonGroup();
			foreach (var info in Modes.All)
			{
				var m = info.Mode;
				var b = new Button { Text = I18n.T(info.Label), ToggleMode = true, ButtonGroup = group, CustomMinimumSize = new Vector2(240, 64), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ButtonPressed = m == _mode };
				b.AddThemeFontSizeOverride("font_size", 20);
				b.Pressed += () => SelectMode(m);
				grid.AddChild(b);
				_modeButtons[m] = b;
			}

			_description = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 54), HorizontalAlignment = HorizontalAlignment.Center };
			_description.AddThemeFontSizeOverride("font_size", 17);
			box.AddChild(_description);

			_sizeTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
			box.AddChild(_sizeTitle);
			_sizes = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			_sizes.AddThemeConstantOverride("separation", 8);
			box.AddChild(_sizes);

			var go = new Button { Text = I18n.T("Continuer : choisir mon personnage"), CustomMinimumSize = new Vector2(0, 52) };
			go.AddThemeFontSizeOverride("font_size", 20);
			go.Pressed += () => { MatchSettings.Mode = _mode; MatchSettings.TeamSize = _teamSize; var next = OnContinue; QueueFree(); next?.Invoke(); };
			box.AddChild(go);
			var back = new Button { Text = I18n.T("Retour"), CustomMinimumSize = new Vector2(0, 40) };
			back.Pressed += () => { var cb = OnBack; QueueFree(); cb?.Invoke(); };
			box.AddChild(back);

			SelectMode(_mode);
		}

		private void SelectMode(GameMode mode)
		{
			_mode = mode;
			var info = Modes.Get(mode);
			_teamSize = Modes.ValidTeamSize(mode, _teamSize);
			_description.Text = I18n.T(info.Description);
			foreach (var kv in _modeButtons) kv.Value.SetPressedNoSignal(kv.Key == mode);
			RebuildSizes(info);
		}

		private void RebuildSizes(ModeInfo info)
		{
			foreach (var c in _sizes.GetChildren()) c.QueueFree();
			if (info.TeamSizes.Length <= 1)
			{
				_sizeTitle.Text = info.Mode == GameMode.Deathmatch ? I18n.T("Chacun pour soi (2 à 20 joueurs, complétés par des bots)") : "";
				return;
			}
			_sizeTitle.Text = info.Mode == GameMode.BattleRoyale ? I18n.T("Taille d'équipe") : I18n.T("Joueurs par équipe (deux équipes, complétées par des bots)");
			var group = new ButtonGroup();
			foreach (int size in info.TeamSizes)
			{
				int s = size;
				string label = info.Mode == GameMode.BattleRoyale ? GameText.Team(s) : I18n.T("{0} contre {0}", s);
				var b = new Button { Text = label, ToggleMode = true, ButtonGroup = group, ButtonPressed = s == _teamSize, CustomMinimumSize = new Vector2(120, 40) };
				b.Pressed += () => _teamSize = s;
				_sizes.AddChild(b);
			}
		}
	}
}
