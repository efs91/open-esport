using Openesport.Utils;
using System;
using Godot;
using OpenEsport.Core;
using Openesport.Inventory;

namespace Openesport.Online
{
	/// <summary>
	/// Interface des modes de jeu hors battle royale : scores, minuteur, points de contrôle, drapeaux, bombe, argent ; menu de classe (touche C) et boutique du Sabotage (touche B).
	/// Elle ne fait que montrer ce que dit le serveur (<see cref="ModeState"/>) et envoyer ses choix (<see cref="Send"/>).
	/// </summary>
	public partial class ModeHud : CanvasLayer
	{
		public Action<CommandType, int> Send;
		public Func<ushort, string> NameOf = id => I18n.T("Joueur {0}", id);
		public bool MenuOpen => _menu != null && _menu.Visible;

		private RichTextLabel _line1, _line2;
		private HBoxContainer _points;
		private Label[] _pointLabels = new Label[5];
		private ProgressBar[] _pointBars = new ProgressBar[5];
		private Label _footer, _actionLabel;
		private ProgressBar _action;
		private PanelContainer _menu;
		private VBoxContainer _menuBox;
		private string _menuKind = "";
		private ModeState _last = ModeState.Empty;
		private bool _alive = true;
		private MatchPhase _phase;

		// ---- messages : un seul emplacement par sorte de message, pour que rien ne se superpose
		//   haut : scores et état de la manche (lignes 1 et 2)  ·  bandeau d'annonce (file d'attente, un message à la fois)
		//   puis : panneau de la phase d'achat (persistant)  ·  alerte pose / désamorçage de l'adversaire  ·  pose / désamorçage de soi (barre au centre)
		/// <summary>Position du joueur local (pour la distance et la direction de la bombe) et son identifiant.</summary>
		public Func<Vector3?> LocalPosition = () => null;
		public Func<ushort> MyId = () => 0xFFFF;

		private PanelContainer _bannerPanel, _buyPanel;
		private RichTextLabel _bannerText, _buyText, _alert;
		private StyleBoxFlat _bannerStyle;
		private ProgressBar _buyBar;
		private float _bannerLeft;
		private readonly System.Collections.Generic.Queue<(string text, Color color, float seconds)> _queue = new();
		private ModeState _prev;
		private bool _prevKnown;

		private static readonly Color Blue = new Color(0.35f, 0.62f, 1f), Red = new Color(1f, 0.38f, 0.32f), Gray = new Color(0.8f, 0.8f, 0.8f);

		public ModeHud() { Layer = 55; }

		public override void _Ready()
		{
			var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);

			var top = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
			top.AnchorLeft = 0.5f; top.AnchorRight = 0.5f; top.OffsetLeft = -360; top.OffsetRight = 360; top.OffsetTop = 10;
			top.AddThemeConstantOverride("separation", 4);
			root.AddChild(top);
			_line1 = MakeRich(26);
			_line2 = MakeRich(18);
			top.AddChild(_line1); top.AddChild(_line2);
			_points = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, Visible = false };
			_points.AddThemeConstantOverride("separation", 10);
			top.AddChild(_points);
			string[] names = { "A", "B", "C", "D", "E" };
			for (int i = 0; i < 5; i++)
			{
				var cell = new VBoxContainer { CustomMinimumSize = new Vector2(64, 0) };
				_pointLabels[i] = new Label { Text = names[i], HorizontalAlignment = HorizontalAlignment.Center };
				_pointLabels[i].AddThemeFontSizeOverride("font_size", 20);
				_pointBars[i] = new ProgressBar { MinValue = -100, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(64, 8) };
				cell.AddChild(_pointLabels[i]); cell.AddChild(_pointBars[i]);
				_points.AddChild(cell);
			}

			_footer = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
			_footer.AnchorTop = 1; _footer.AnchorBottom = 1; _footer.OffsetLeft = 16; _footer.OffsetTop = -214; _footer.OffsetBottom = -164;      // au-dessus de la vie et du bouclier
			_footer.AddThemeFontSizeOverride("font_size", 20);
			_footer.AddThemeColorOverride("font_outline_color", Colors.Black); _footer.AddThemeConstantOverride("outline_size", 6);
			root.AddChild(_footer);

			_actionLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_actionLabel.AnchorLeft = 0.5f; _actionLabel.AnchorRight = 0.5f; _actionLabel.AnchorTop = 0.70f; _actionLabel.OffsetLeft = -200; _actionLabel.OffsetRight = 200;
			_actionLabel.AddThemeFontSizeOverride("font_size", 22);
			root.AddChild(_actionLabel);
			_action = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_action.AnchorLeft = 0.5f; _action.AnchorRight = 0.5f; _action.AnchorTop = 0.70f; _action.OffsetLeft = -140; _action.OffsetRight = 140; _action.OffsetTop = 34; _action.OffsetBottom = 52;
			root.AddChild(_action);

			// bandeau d'annonce : sous les scores, un message à la fois (les suivants attendent leur tour)
			_bannerStyle = PanelStyle(Gray);
			_bannerPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_bannerPanel.AddThemeStyleboxOverride("panel", _bannerStyle);
			_bannerPanel.AnchorLeft = 0.5f; _bannerPanel.AnchorRight = 0.5f; _bannerPanel.AnchorTop = 0.15f; _bannerPanel.OffsetLeft = -340; _bannerPanel.OffsetRight = 340;
			_bannerText = MakeRich(30);
			_bannerText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_bannerText.HorizontalAlignment = HorizontalAlignment.Center;
			_bannerPanel.AddChild(_bannerText);
			root.AddChild(_bannerPanel);

			// phase d'achat : un grand panneau qui reste affiché tant qu'on peut acheter
			_buyPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_buyPanel.AddThemeStyleboxOverride("panel", PanelStyle(new Color(1f, 0.82f, 0.25f)));
			_buyPanel.AnchorLeft = 0.5f; _buyPanel.AnchorRight = 0.5f; _buyPanel.AnchorTop = 0.235f; _buyPanel.OffsetLeft = -300; _buyPanel.OffsetRight = 300;
			var buyBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
			buyBox.AddThemeConstantOverride("separation", 6);
			_buyText = MakeRich(24);
			_buyText.HorizontalAlignment = HorizontalAlignment.Center;
			_buyText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_buyBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 10), MouseFilter = Control.MouseFilterEnum.Ignore };
			buyBox.AddChild(_buyText); buyBox.AddChild(_buyBar);
			_buyPanel.AddChild(buyBox);
			root.AddChild(_buyPanel);

			// alerte : l'adversaire pose ou désamorce la bombe
			_alert = MakeRich(26);
			_alert.HorizontalAlignment = HorizontalAlignment.Center;
			_alert.AnchorLeft = 0.5f; _alert.AnchorRight = 0.5f; _alert.AnchorTop = 0.30f; _alert.OffsetLeft = -380; _alert.OffsetRight = 380;
			_alert.Visible = false;
			root.AddChild(_alert);

			_menu = new PanelContainer { Visible = false };
			_menu.AddThemeStyleboxOverride("panel", PanelStyle(new Color(0.45f, 0.47f, 0.52f), 0.97f));
			_menu.SetAnchorsPreset(Control.LayoutPreset.Center);
			_menu.OffsetLeft = -250; _menu.OffsetRight = 250; _menu.OffsetTop = -240; _menu.OffsetBottom = 240;
			_menuBox = new VBoxContainer();
			_menuBox.AddThemeConstantOverride("separation", 6);
			var margin = new MarginContainer();
			foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 14);
			margin.AddChild(_menuBox); _menu.AddChild(margin);
			root.AddChild(_menu);
		}

		private static RichTextLabel MakeRich(int size)
		{
			var l = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.Off };
			l.AddThemeFontSizeOverride("normal_font_size", size);
			l.AddThemeFontSizeOverride("bold_font_size", size);
			l.AddThemeColorOverride("font_outline_color", Colors.Black); l.AddThemeConstantOverride("outline_size", 6);
			l.AddThemeColorOverride("default_color", Colors.White);
			return l;
		}

		private static string Hex(Color c) => c.ToHtml(false);
		private static string Clock(int s) => $"{Math.Max(0, s) / 60}:{Math.Max(0, s) % 60:00}";
		private string Side(int team) => $"[color=#{Hex(team == 0 ? Blue : Red)}]{(team == 0 ? I18n.T("Bleus") : I18n.T("Rouges"))}[/color]";

		/// <summary>Observateur : on montre les scores et les annonces, jamais les menus (classe, boutique, choix de l'équipement).</summary>
		public bool Spectator;

		public override void _Input(InputEvent e)
		{
			if (Spectator || e is not InputEventKey k || !k.Pressed || k.Echo) return;
			if (_last.Mode == GameMode.BattleRoyale || _phase == MatchPhase.Over) return;
			if (k.Keycode == Key.B && _last.Mode == GameMode.Sabotage && _last.CanBuy) { ToggleMenu("buy"); GetViewport().SetInputAsHandled(); }
			else if (k.Keycode == Key.C && HasClasses(_last.Mode)) { ToggleMenu("class"); GetViewport().SetInputAsHandled(); }
			else if (k.Keycode == Key.Escape && MenuOpen) { CloseMenu(); GetViewport().SetInputAsHandled(); }
		}

		/// <summary>Stations de ravitaillement de la carte (Capture du drapeau), pour savoir si le joueur est dessus.</summary>
		public System.Collections.Generic.List<MapPoint> Stations = new();
		private bool _classOffered, _wasAlive = true, _autoMenu;

		private static bool HasClasses(GameMode mode) => mode != GameMode.BattleRoyale && mode != GameMode.Sabotage && mode != GameMode.Duel && !Modes.UsesArenaRules(mode);

		private bool AtStation()
		{
			if (_last.Mode != GameMode.CaptureTheFlag || !_alive) return false;
			var p = LocalPosition();
			if (p == null) return false;
			var pos = new Vec3(p.Value.X, p.Value.Y, p.Value.Z);
			foreach (var st in Stations) if (st.Team == _last.MySide && Resupply.Inside(st, pos)) return true;
			return false;
		}

		/// <summary>Choix de la classe : proposé à l'arrivée dans la partie et à chaque mort ; refermé tout seul à la réapparition s'il s'était ouvert seul.</summary>
		private void OfferClass(bool alive, MatchPhase phase)
		{
			if (Spectator || !HasClasses(_last.Mode) || phase == MatchPhase.Over) { _wasAlive = alive; return; }
			if (!_classOffered) { _classOffered = true; if (!MenuOpen) { ToggleMenu("class"); _autoMenu = true; } }
			else if (_wasAlive && !alive && !MenuOpen) { ToggleMenu("class"); _autoMenu = true; }
			else if (!_wasAlive && alive && _autoMenu && MenuOpen && _menuKind == "class") { CloseMenu(); }
			_wasAlive = alive;
		}

		private void ToggleMenu(string kind)
		{
			if (MenuOpen && _menuKind == kind) { CloseMenu(); return; }
			_autoMenu = false;
			_menuKind = kind;
			Rebuild();
			_menu.Visible = true;
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}

		public void CloseMenu()
		{
			if (!MenuOpen) return;
			_menu.Visible = false;
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}

		private void Rebuild()
		{
			foreach (var c in _menuBox.GetChildren()) c.QueueFree();
			bool buy = _menuKind == "buy";
			if (_menuKind == "duel")
			{
				_menu.OffsetLeft = -470; _menu.OffsetRight = 470; _menu.OffsetTop = -300; _menu.OffsetBottom = 300;
				BuildDuelPick();
				return;
			}
			// la boutique est large (une colonne par famille d'articles), le menu de classe reste étroit
			_menu.OffsetLeft = buy ? -600 : -250; _menu.OffsetRight = buy ? 600 : 250;
			_menu.OffsetTop = buy ? -300 : -240; _menu.OffsetBottom = buy ? 300 : 240;
			if (buy) { BuildShop(); return; }

			var title = new Label { Text = I18n.T("Choisis ta classe"), HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 24);
			_menuBox.AddChild(title);
			_menuBox.AddChild(new Label { Text = _last.Mode == GameMode.CaptureTheFlag
				? I18n.T("Le changement prend effet à ta prochaine réapparition, ou tout de suite à la station de ravitaillement de ta base.")
				: I18n.T("Le changement prend effet à ta prochaine réapparition (tout de suite en salle d'attente)."), AutowrapMode = TextServer.AutowrapMode.WordSmart });
			foreach (var kit in Loadouts.Kits)
			{
				var k = kit;
				var b = new Button { Text = $"{(k.Id == _last.ClassId ? "✔  " : "")}{I18n.T(k.Name)} — {I18n.T(k.Description)}", CustomMinimumSize = new Vector2(0, 46), AutowrapMode = TextServer.AutowrapMode.WordSmart };
				b.Pressed += () => { Send?.Invoke(CommandType.SelectClass, k.Id); _last.ClassId = k.Id; MatchSettings.Loadout = k.Id; CloseMenu(); };
				_menuBox.AddChild(b);
			}
			var close = new Button { Text = I18n.T("Fermer") };
			close.Pressed += CloseMenu;
			_menuBox.AddChild(close);
		}

		// ------------------------------------------------------------------ boutique (comme celles de Valorant et de Counter-Strike : des familles en colonnes, des cases à cliquer)

		private static readonly string[] ShopColumns = { /*T*/"PISTOLET", /*T*/"PROXIMITÉ", /*T*/"FUSILS", /*T*/"BOUCLIERS", /*T*/"SOINS", /*T*/"VISEURS", /*T*/"MUNITIONS", /*T*/"GRENADES", /*T*/"ÉQUIPEMENT" };

		private static string ShopCategory(ShopItem it)
		{
			if (it.DefuseKit) return "ÉQUIPEMENT";
			if (it.ShieldTier >= 0) return "BOUCLIERS";
			var d = ItemDefs.Get(it.ItemId);
			if (d == null) return "ÉQUIPEMENT";
			return d.Kind switch
			{
				ItemKind.Weapon => it.ItemId == ItemIds.C19 ? "PISTOLET" : it.ItemId == ItemIds.Smg45 || it.ItemId == ItemIds.Sawnoff || it.ItemId == ItemIds.Peacemaker ? "PROXIMITÉ" : "FUSILS",
				ItemKind.Optic => "VISEURS",
				ItemKind.Ammo => "MUNITIONS",
				ItemKind.Grenade => "GRENADES",
				ItemKind.Consumable => "SOINS",
				_ => "ÉQUIPEMENT",
			};
		}

		private void BuildShop()
		{
			var head = new HBoxContainer();
			var title = new Label { Text = I18n.T("BOUTIQUE"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			title.AddThemeFontSizeOverride("font_size", 30);
			var money = new Label { Text = $"{_last.Money} $" };
			money.AddThemeFontSizeOverride("font_size", 30);
			money.AddThemeColorOverride("font_color", Gold);
			head.AddChild(title); head.AddChild(money);
			_menuBox.AddChild(head);
			var hint = new Label { Text = I18n.T("Clique sur un article pour l'acheter pendant la phase d'achat   ·   B ou Échap : fermer") };
			hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.73f, 0.78f));
			_menuBox.AddChild(hint);

			var columns = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			columns.AddThemeConstantOverride("separation", 10);
			foreach (var cat in ShopColumns)
			{
				var items = new System.Collections.Generic.List<ShopItem>();
				foreach (var it in Shop.Items) if ((it.Side == 255 || it.Side == _last.MySide) && ShopCategory(it) == cat) items.Add(it);
				if (items.Count == 0) continue;
				var col = new VBoxContainer();
				col.AddThemeConstantOverride("separation", 8);
				var name = new Label { Text = I18n.T(cat), HorizontalAlignment = HorizontalAlignment.Center };
				name.AddThemeFontSizeOverride("font_size", 16);
				name.AddThemeColorOverride("font_color", new Color(0.7f, 0.73f, 0.78f));
				col.AddChild(name);
				foreach (var it in items) col.AddChild(ShopTile(it));
				columns.AddChild(col);
			}
			var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 475), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
			scroll.AddChild(columns);
			_menuBox.AddChild(scroll);
			var close = new Button { Text = I18n.T("Fermer") };
			close.Pressed += CloseMenu;
			_menuBox.AddChild(close);
		}

		/// <summary>Une case d'article : miniature, nom, prix. Grisée si on n'a pas assez d'argent (ou déjà le kit).</summary>
		private Button ShopTile(ShopItem it)
		{
			bool afford = it.Price <= _last.Money && !(it.DefuseKit && _last.HasKit);
			var b = new Button { CustomMinimumSize = new Vector2(128, 104), Disabled = !afford, FocusMode = Control.FocusModeEnum.None };
			StyleBoxFlat Box(Color bg, Color border) { var s = new StyleBoxFlat { BgColor = bg, BorderColor = border }; s.SetBorderWidthAll(2); s.SetCornerRadiusAll(5); return s; }
			b.AddThemeStyleboxOverride("normal", Box(new Color(0.12f, 0.14f, 0.19f), new Color(0.32f, 0.35f, 0.41f)));
			b.AddThemeStyleboxOverride("hover", Box(new Color(0.18f, 0.2f, 0.27f), Gold));
			b.AddThemeStyleboxOverride("pressed", Box(new Color(0.24f, 0.26f, 0.34f), Gold));
			b.AddThemeStyleboxOverride("disabled", Box(new Color(0.08f, 0.09f, 0.12f), new Color(0.2f, 0.22f, 0.26f)));

			var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = afford ? Colors.White : new Color(1, 1, 1, 0.45f) };
			box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			box.OffsetLeft = 6; box.OffsetRight = -6; box.OffsetTop = 6; box.OffsetBottom = -6;
			box.AddThemeConstantOverride("separation", 2);
			Texture2D icon = it.ItemId != ItemIds.None ? ItemIcons.Get(ItemDefs.Get(it.ItemId)?.Name)
				: it.ShieldTier >= 0 && ResourceLoader.Exists(ItemTierStyle.ShieldIcon(it.ShieldTier)) ? GD.Load<Texture2D>(ItemTierStyle.ShieldIcon(it.ShieldTier)) : null;
			box.AddChild(new TextureRect { Texture = icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(0, 44), SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
			var label = new Label { Text = I18n.T(it.Name), HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
			label.AddThemeFontSizeOverride("font_size", 12);
			box.AddChild(label);
			var price = new Label { Text = $"{it.Price} $", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
			price.AddThemeFontSizeOverride("font_size", 17);
			price.AddThemeColorOverride("font_color", afford ? Gold : new Color(0.9f, 0.35f, 0.3f));
			box.AddChild(price);
			b.AddChild(box);
			b.Pressed += () => { Send?.Invoke(CommandType.Buy, it.Code); b.Disabled = true; };
			return b;
		}

		/// <summary>Met l'interface à jour : à appeler à chaque image avec l'état du mode et de la partie.</summary>
		public void Refresh(ModeState m, MatchPhase phase, bool alive, int respawnHint = 0)
		{
			bool classChanged = m.ClassId != _last.ClassId || m.Money != _last.Money;
			_last = m; _phase = phase; _alive = alive;
			OfferClass(alive, phase);
			if (MenuOpen && _menuKind == "buy" && !m.CanBuy) CloseMenu();
			if (MenuOpen && classChanged && _menuKind == "buy") Rebuild();
			string timer = m.TimeLeft > 0 ? Clock(m.TimeLeft) : "";
			string l1 = "", l2 = "";
			_points.Visible = m.Mode == GameMode.Control;
			switch (m.Mode)
			{
				case GameMode.Deathmatch:
					l1 = I18n.T("Éliminations [b]{0}[/b] / {1}   ·   meilleur : {2}", m.MyScore, m.Target, m.TopScore); l2 = timer;
					break;
				case GameMode.TeamDeathmatch:
					l1 = $"{Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   {I18n.T("(objectif {0})", m.Target)}"; l2 = timer;
					break;
				case GameMode.CaptureTheFlag:
					l1 = $"{Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   {I18n.T("({0} prises)", m.Target)}   {timer}";
					l2 = I18n.T("Drapeau {0} : {1}", Side(0), FlagText(m.Flag0)) + "   ·   " + I18n.T("Drapeau {0} : {1}", Side(1), FlagText(m.Flag1));
					break;
				case GameMode.Control:
					l1 = $"{Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   {I18n.T("(objectif {0})", m.Target)}"; l2 = timer;
					for (int i = 0; i < 5; i++)
					{
						var c = m.Points[i];
						var col = c.Owner == 0 ? Blue : c.Owner == 1 ? Red : Gray;
						_pointLabels[i].Modulate = c.Contested ? new Color(1f, 0.85f, 0.2f) : col;
						_pointBars[i].Value = c.Progress;
						_pointBars[i].Modulate = c.Progress < 0 ? Blue : c.Progress > 0 ? Red : Gray;
					}
					break;
				case GameMode.Duel:
					(l1, l2) = DuelLines(m, timer);
					break;
				case GameMode.Sabotage:
					l1 = I18n.T("Manche {0}", m.Round) + $"   ·   {Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   " + I18n.T("(premier à {0})", m.Target);
					l2 = SabotageText(m, timer);
					break;
			}
			if (_line1.Text != l1) _line1.Text = l1;
			if (_line2.Text != l2) _line2.Text = l2;

			string role = m.Mode == GameMode.Sabotage ? (m.MySide == 1 ? I18n.T("Attaquant") : I18n.T("Défenseur")) + $"   ·   {m.Money} $" + (m.HasKit ? "   ·   " + I18n.T("kit de désamorçage") : "") : "";
			// Deathmatch : pas de classe, on part au pistolet et les armes sont dans l'arène
			string hint = m.Mode == GameMode.Duel ? DuelFooter(m)
				: m.Mode == GameMode.Sabotage ? (m.CanBuy ? I18n.T("B : boutique") : "")
				: Modes.UsesArenaRules(m.Mode) ? I18n.T("Arène : passe sur les armes et les soins pour les prendre")
				: I18n.T("C : classe ({0})", I18n.T(Loadouts.Get(m.ClassId).Name));
			if (AtStation()) hint = I18n.T("Ravitaillement : armes, munitions, grenades, soins, vie et bouclier rechargés") + "\n" + I18n.T("C : changer de classe tout de suite ({0})", I18n.T(Loadouts.Get(m.ClassId).Name));
			if (m.Invulnerable) hint += "   " + I18n.T("(protégé)");
			_footer.Text = (role.Length > 0 ? role + "\n" : "") + hint;
			_footer.Visible = phase != MatchPhase.Over && m.Ride(MyId(),out _) == null;

			_action.Visible = _actionLabel.Visible = m.Action != 0;
			if (m.Action != 0) { _action.Value = m.ActionProgress; _actionLabel.Text = m.Action == 1 ? I18n.T("Pose de la bombe…") : I18n.T("Désamorçage…"); }
			UpdateMessages(m, phase);
			if (m.Mode == GameMode.Duel) UpdateDuel(m, phase);
		}

		private static StyleBoxFlat PanelStyle(Color border, float alpha = 0.8f)
		{
			var s = new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.08f, alpha), BorderColor = border };
			s.SetBorderWidthAll(3); s.SetCornerRadiusAll(8);
			s.ContentMarginLeft = 18; s.ContentMarginRight = 18; s.ContentMarginTop = 10; s.ContentMarginBottom = 12;
			return s;
		}

		/// <summary>Affiche un message dans le bandeau. Les messages se suivent (3,5 s chacun) ; un message urgent efface la file et passe tout de suite.</summary>
		public void Announce(string text, Color color, float seconds = 3.5f, bool urgent = false)
		{
			if (urgent) { _queue.Clear(); _bannerLeft = 0f; }
			while (_queue.Count >= 4) _queue.Dequeue();
			_queue.Enqueue((text, color, seconds));
		}

		public override void _Process(double delta)
		{
			float dt = (float)delta;
			if (_bannerLeft > 0f)
			{
				_bannerLeft -= dt;
				_bannerPanel.Modulate = new Color(1, 1, 1, Mathf.Clamp(_bannerLeft / 0.4f, 0f, 1f));
				if (_bannerLeft <= 0f) _bannerPanel.Visible = false;
			}
			if (_bannerLeft <= 0f && _queue.Count > 0)
			{
				var (text, color, seconds) = _queue.Dequeue();
				_bannerText.Text = text;
				_bannerStyle.BorderColor = color;
				_bannerPanel.Modulate = Colors.White;
				_bannerPanel.Visible = true;
				_bannerLeft = seconds;
			}
			_bannerPanel.Visible = _bannerLeft > 0f && !MenuOpen;      // pas d'annonce par-dessus un menu
			if (_alert.Visible) _alert.Modulate = new Color(1, 1, 1, 0.65f + 0.35f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 8f));
		}

		/// <summary>Où est un point : coordonnées de la carte, distance et direction par rapport au joueur (le nord est vers -Z).</summary>
		private string Where(Vec3 p)
		{
			string coords = $"X {p.X:0} · Z {p.Z:0}";
			var me = LocalPosition();
			if (me == null) return coords;
			float dx = p.X - me.Value.X, dz = p.Z - me.Value.Z;
			string[] names = { /*T*/"nord", /*T*/"nord-est", /*T*/"est", /*T*/"sud-est", /*T*/"sud", /*T*/"sud-ouest", /*T*/"ouest", /*T*/"nord-ouest" };
			int dir = ((int)MathF.Round(MathF.Atan2(dx, -dz) / (MathF.PI / 4f)) % 8 + 8) % 8;
			return $"{coords}   ({MathF.Sqrt(dx * dx + dz * dz):0} m, {I18n.T(names[dir])})";
		}

		private static readonly Color Gold = new Color(1f, 0.82f, 0.25f), Orange = new Color(1f, 0.55f, 0.15f), Green = new Color(0.4f, 0.9f, 0.5f);

		/// <summary>Sabotage : panneau d'achat, alerte de l'adversaire et annonces des changements d'état (manche, bombe au sol, pose, désamorçage, fin).</summary>
		private void UpdateMessages(ModeState m, MatchPhase phase)
		{
			bool sab = m.Mode == GameMode.Sabotage && phase != MatchPhase.Lobby;
			var stage = (SabotageStage)m.Stage;
			bool attack = m.MySide == 1;
			ushort me = MyId();

			// phase d'achat : grand panneau fixe
			_buyPanel.Visible = sab && stage == SabotageStage.Buy && phase == MatchPhase.Playing && !MenuOpen;      // la boutique ouverte passe devant : on ne montre pas les deux
			if (_buyPanel.Visible)
			{
				float total = Mathf.Max(1f, GameBalance.F("sabotage.buySeconds"));
				_buyBar.Value = Mathf.Clamp(m.TimeLeft / total * 100f, 0f, 100f);
				_buyText.Text = I18n.T("[b]PHASE D'ACHAT[/b]   ·   départ dans [b]{0}[/b]", Clock(m.TimeLeft)) + "\n"
					+ (attack ? I18n.T("[color=#ff6152]Vous ATTAQUEZ[/color]") : I18n.T("[color=#59a0ff]Vous DÉFENDEZ[/color]")) + "   ·   " + I18n.T("argent [b]{0} $[/b]", m.Money) + "\n"
					+ I18n.T("[color=#ffd23f][b]B[/b][/color] : ouvrir la boutique");
			}

			// alerte : l'adversaire agit sur la bombe (pas moi : ma propre barre est au centre)
			bool other = sab && !MenuOpen && m.BombAction != 0 && m.Action == 0 && (stage == SabotageStage.Round || stage == SabotageStage.Planted);
			_alert.Visible = other;
			if (other)
			{
				string who = NameOf(m.BombActor);
				_alert.Text = m.BombAction == 2
					? (attack ? I18n.T("[color=#ff5040][b]⚠  {0} DÉSAMORCE LA BOMBE — {1} %[/b][/color]", who, m.BombActionProgress) : I18n.T("[color=#6fe08a][b]{0} désamorce la bombe — {1} %[/b][/color]", who, m.BombActionProgress))
					: (attack ? I18n.T("[color=#6fe08a][b]{0} pose la bombe — {1} %[/b][/color]", who, m.BombActionProgress) : I18n.T("[color=#ff5040][b]⚠  {0} POSE LA BOMBE — {1} %[/b][/color]", who, m.BombActionProgress));
			}

			// annonces : seulement sur un changement d'état (jamais à la première image reçue)
			if (!sab) { _prevKnown = false; return; }
			var p = _prev;
			bool known = _prevKnown;
			_prev = m; _prevKnown = true;
			if (!known) return;
			string role = attack ? I18n.T("[color=#ff6152]Vous attaquez[/color]") : I18n.T("[color=#59a0ff]Vous défendez[/color]");

			if (m.Stage != p.Stage || m.Round != p.Round)
			{
				if (stage == SabotageStage.Buy) Announce(I18n.T("[b]MANCHE {0}[/b]", m.Round) + "\n" + role, Gold, 3f, true);
				else if (stage == SabotageStage.Round && (SabotageStage)p.Stage == SabotageStage.Buy)
				{
					string goal = !attack ? I18n.T("Empêchez la pose de la bombe sur les sites A et B")
						: m.BombCarrier == me ? I18n.T("[b]Tu portes la bombe[/b] : rejoins un site (A ou B), sors-la (molette ou [b]5[/b]) puis maintiens le [b]clic gauche[/b]   ([b]X[/b] : la lâcher)")
						: m.BombCarrier != 0xFFFF ? I18n.T("{0} porte la bombe : protège-le jusqu'à un site", NameOf(m.BombCarrier)) : I18n.T("La bombe est au sol");
					Announce(I18n.T("[b]C'EST PARTI ![/b]") + "\n" + goal, Green, 4f, true);
				}
				else if (stage == SabotageStage.Planted)
					Announce(I18n.T("[b]BOMBE POSÉE — SITE {0}[/b]", (char)('A' + m.BombSite)) + "\n" + (attack ? I18n.T("Défends-la jusqu'à l'explosion") : I18n.T("Désamorcez-la : maintenez [b]E[/b] près de la bombe")), Orange, 4f, true);
			}

			// la bombe change de main ou tombe
			if (stage == SabotageStage.Round)
			{
				if (m.BombState == 1 && p.BombState != 1)
					Announce(I18n.T("[b]BOMBE AU SOL[/b]") + "\n" + Where(m.BombPos), Orange, 5f, true);
				else if (m.BombState == 0 && p.BombState == 1)
					Announce(m.BombCarrier == me ? I18n.T("[b]Tu as ramassé la bombe[/b]") : I18n.T("{0} a ramassé la bombe", NameOf(m.BombCarrier)), Gold, 3f);
			}

			// désamorçage vu par tous
			if (m.BombAction == 2 && p.BombAction != 2 && attack)
				Announce(I18n.T("[b][color=#ff5040]DÉSAMORÇAGE EN COURS[/color][/b]") + "\n" + I18n.T("par {0} — empêchez-le !", NameOf(m.BombActor)), Red, 3f, true);
			else if (p.BombAction == 2 && m.BombAction == 0 && stage == SabotageStage.Planted)
				Announce(attack ? I18n.T("Désamorçage interrompu") : I18n.T("[b]Désamorçage interrompu[/b]"), Gold, 2.5f);

			// fin de manche
			if (m.BombState == 3 && p.BombState != 3)
				Announce(I18n.T("[b]BOMBE DÉSAMORCÉE[/b]") + "\n" + (attack ? I18n.T("Les défenseurs gagnent la manche : votre équipe a perdu") : I18n.T("Les défenseurs gagnent la manche")), attack ? Red : Green, 5f, true);
			else if (m.BombState == 4 && p.BombState != 4)
				Announce(I18n.T("[b]LA BOMBE A EXPLOSÉ[/b]") + "\n" + I18n.T("Les attaquants gagnent la manche"), attack ? Green : Red, 5f, true);
			else if (stage == SabotageStage.RoundEnd && (SabotageStage)p.Stage != SabotageStage.RoundEnd && m.BombState < 3 && m.RoundWinner != 255)
				Announce(m.RoundWinner == 1 ? I18n.T("[b]MANCHE GAGNÉE PAR LES ATTAQUANTS[/b]") : I18n.T("[b]MANCHE GAGNÉE PAR LES DÉFENSEURS[/b]"), (m.RoundWinner == 1) == attack ? Green : Red, 4f, true);
		}

		private string BombLine(ModeState m, string timer)
		{
			ushort me = MyId();
			string state = m.BombState == 1 ? I18n.T("[color=#ff8c26][b]bombe au sol[/b][/color] : {0}", Where(m.BombPos))
				: m.BombCarrier == me ? I18n.T("[b]tu portes la bombe[/b] (X : la lâcher)")
				: m.BombCarrier != 0xFFFF && m.MySide == 1 ? I18n.T("la bombe est portée par {0}", NameOf(m.BombCarrier))
				: m.MySide == 1 ? I18n.T("la bombe est tombée") : I18n.T("empêche la pose de la bombe");
			return state;
		}

		private string FlagText(FlagState f) => f.State == 0 ? I18n.T("à sa base") : f.State == 1 ? I18n.T("porté par {0}", NameOf(f.Carrier)) : I18n.T("tombé au sol");

		private string SabotageText(ModeState m, string timer)
		{
			switch ((SabotageStage)m.Stage)
			{
				case SabotageStage.Buy: return I18n.T("[color=#ffd23f]Phase d'achat[/color]   ·   départ dans {0}", timer);
				case SabotageStage.Round:
					string bomb = BombLine(m, "");
					return $"{timer}   ·   {bomb}";      // le texte de la bombe : porteur, ou « au sol » avec ses coordonnées
				case SabotageStage.Planted: return I18n.T("[color=#ffcc33][b]BOMBE POSÉE — site {0}[/b][/color]   ·   explosion dans {1} s", (char)('A' + m.BombSite), Math.Ceiling(m.BombTimer).ToString("0"));
				default:
					return m.RoundWinner == 255 ? "" : m.RoundWinner == 1 ? I18n.T("Manche gagnée par les attaquants") : I18n.T("Manche gagnée par les défenseurs");
			}
		}
	}

	/// <summary>Visuels 3D des modes : points de contrôle, bases de drapeau, sites de bombe, drapeaux (au sol ou au-dessus de leur porteur) et bombe.</summary>
	public partial class ModeWorld : Node3D
	{
		public Func<ushort, Vector3?> CarrierPosition = id => null;

		private readonly MeshInstance3D[] _rings = new MeshInstance3D[5];
		private readonly MeshInstance3D[] _beams = new MeshInstance3D[5];
		private Node3D _flag0, _flag1, _bomb;
		private MeshInstance3D _bombLight;
		private static readonly Color Blue = new Color(0.3f, 0.6f, 1f), Red = new Color(1f, 0.33f, 0.28f), Gray = new Color(0.8f, 0.8f, 0.8f);

		private static StandardMaterial3D Glow(Color c, float alpha) => new StandardMaterial3D { AlbedoColor = c with { A = alpha }, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = false };

		private WorldData _data;
		private bool _stationsBuilt;

		/// <summary>Stations de ravitaillement (Capture du drapeau) : un disque à la couleur de l'équipe, une caisse lumineuse et une étiquette.</summary>
		private void BuildStations()
		{
			_stationsBuilt = true;
			foreach (var st in Resupply.Stations(_data))
			{
				var pos = new Vector3(st.Pos.X, st.Pos.Y, st.Pos.Z);
				var col = st.Team == 0 ? Blue : Red;
				AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = st.Radius, BottomRadius = st.Radius, Height = 0.06f, RadialSegments = 40 }, Position = pos + new Vector3(0, 0.05f, 0), MaterialOverride = Glow(col, 0.3f) });
				var green = new Color(0.35f, 1f, 0.5f);
				AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.9f, 0.9f) }, Position = pos + new Vector3(0, 0.45f, 0), MaterialOverride = Glow(green, 0.55f) });
				AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.12f, Height = 6f }, Position = pos + new Vector3(0, 3f, 0), MaterialOverride = Glow(green, 0.25f) });
				AddChild(new Label3D { Text = I18n.T("Ravitaillement"), Position = pos + new Vector3(0, 2.2f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 64, PixelSize = 0.01f, Modulate = green, OutlineSize = 12 });
			}
		}

		public void Build(WorldData data)
		{
			_data = data;
			foreach (var pt in data.Points)
			{
				var pos = new Vector3(pt.Pos.X, pt.Pos.Y, pt.Pos.Z);
				switch (pt.Kind)
				{
					case MapPointKind.ControlPoint:
					{
						int i = Math.Clamp((int)pt.Index, 0, 4);
						var ring = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = pt.Radius, BottomRadius = pt.Radius, Height = 0.08f, RadialSegments = 48 }, Position = pos + new Vector3(0, 0.06f, 0), MaterialOverride = Glow(Gray, 0.35f) };
						var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.35f, Height = 16f }, Position = pos + new Vector3(0, 8f, 0), MaterialOverride = Glow(Gray, 0.25f) };
						AddChild(ring); AddChild(beam);
						_rings[i] = ring; _beams[i] = beam;
						string name = ((char)('A' + i)).ToString();
						AddChild(new Label3D { Text = name, Position = pos + new Vector3(0, 17f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 220, PixelSize = 0.01f, NoDepthTest = true, Modulate = Colors.White, OutlineSize = 20 });
						break;
					}
					case MapPointKind.FlagBase:
						AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = pt.Radius, BottomRadius = pt.Radius, Height = 0.08f, RadialSegments = 40 }, Position = pos + new Vector3(0, 0.06f, 0), MaterialOverride = Glow(pt.Team == 0 ? Blue : Red, 0.4f) });
						break;
					case MapPointKind.BombSite:
					{
						// toute la zone de pose : un rectangle jaune (surface translucide + liseré plus marqué), tourné comme le point
						float side = pt.Radius * 2f;
						var gold = new Color(1f, 0.8f, 0.2f);
						var zone = new Node3D { Position = pos + new Vector3(0, 0.06f, 0), Rotation = new Vector3(0, pt.Yaw, 0) };
						zone.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(side, 0.06f, side) }, MaterialOverride = Glow(gold, 0.22f) });
						float t = 0.35f;
						foreach (var (size, off) in new[] { (new Vector3(side, 0.1f, t), new Vector3(0, 0, pt.Radius - t / 2)), (new Vector3(side, 0.1f, t), new Vector3(0, 0, -pt.Radius + t / 2)), (new Vector3(t, 0.1f, side), new Vector3(pt.Radius - t / 2, 0, 0)), (new Vector3(t, 0.1f, side), new Vector3(-pt.Radius + t / 2, 0, 0)) })
							zone.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = off, MaterialOverride = Glow(gold, 0.75f) });
						AddChild(zone);
						AddChild(new Label3D { Text = ((char)('A' + pt.Index)).ToString(), Position = pos + new Vector3(0, 6f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 220, PixelSize = 0.01f, NoDepthTest = true, Modulate = new Color(1f, 0.85f, 0.3f), OutlineSize = 20 });
						break;
					}
				}
			}
			_flag0 = MakeFlag(Blue); _flag1 = MakeFlag(Red);
			_bomb = new Node3D { Visible = false };
			// charge explosive (assets/grenades/basic_explosive.glb) : modèle debout et centré, 3,4 unités de haut → 0,5 m, posé sur son pied
			var bombModel = GD.Load<PackedScene>("res://assets/grenades/basic_explosive.glb")?.Instantiate<Node3D>();
			if (bombModel != null)
			{
				const float height = 0.5f;
				bombModel.Scale = Vector3.One * (height / 3.4f);
				bombModel.Position = new Vector3(0, height * 0.5f, 0);
				_bomb.AddChild(bombModel);
			}
			else
				_bomb.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.35f) }, Position = new Vector3(0, 0.15f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.15f) } });
			_bombLight = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.04f, Height = 0.08f }, Position = new Vector3(0, 0.54f, 0), MaterialOverride = Glow(Red, 1f) };      // voyant au-dessus du modèle : clignote de plus en plus vite une fois la bombe posée
			_bomb.AddChild(_bombLight);
			AddChild(_bomb);
		}

		private Node3D MakeFlag(Color c)
		{
			var n = new Node3D { Visible = false };
			n.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 2.6f }, Position = new Vector3(0, 1.3f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.8f, 0.8f) } });
			n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.1f, 0.7f, 0.04f) }, Position = new Vector3(0.58f, 2.2f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
			AddChild(n);
			return n;
		}

		public void Refresh(ModeState m)
		{
			if (_flag0 == null) return;
			if (!_stationsBuilt && m.Mode == GameMode.CaptureTheFlag && _data != null) BuildStations();
			if (m.Mode == GameMode.Control)
				for (int i = 0; i < 5; i++)
				{
					if (_rings[i] == null) continue;
					var c = m.Points[i];
					var col = c.Contested ? new Color(1f, 0.85f, 0.2f) : c.Owner == 0 ? Blue : c.Owner == 1 ? Red : Gray;
					((StandardMaterial3D)_rings[i].MaterialOverride).AlbedoColor = col with { A = 0.4f };
					((StandardMaterial3D)_beams[i].MaterialOverride).AlbedoColor = col with { A = 0.28f };
				}
			bool ctf = m.Mode == GameMode.CaptureTheFlag;
			PlaceFlag(_flag0, m.Flag0, ctf); PlaceFlag(_flag1, m.Flag1, ctf);
			bool sab = m.Mode == GameMode.Sabotage;
			bool bombVisible = sab && (m.BombState == 1 || m.BombState == 2);
			_bomb.Visible = bombVisible;
			if (bombVisible)
			{
				_bomb.GlobalPosition = new Vector3(m.BombPos.X, m.BombPos.Y, m.BombPos.Z);
				// la bombe posée clignote de plus en plus vite
				float rate = m.BombState == 2 ? Mathf.Lerp(3f, 14f, 1f - Mathf.Clamp(m.BombTimer / 40f, 0f, 1f)) : 0f;
				_bombLight.Visible = m.BombState == 1 || Mathf.Sin(Time.GetTicksMsec() / 1000f * rate * Mathf.Tau) > 0f;
			}
		}

		private void PlaceFlag(Node3D node, FlagState f, bool show)
		{
			node.Visible = show;
			if (!show) return;
			var pos = new Vector3(f.Pos.X, f.Pos.Y, f.Pos.Z);
			if (f.State == 1)
			{
				var carrier = CarrierPosition(f.Carrier);
				if (carrier.HasValue) pos = carrier.Value + new Vector3(0, 1.2f, 0);
			}
			node.GlobalPosition = pos;
		}
	}
}
