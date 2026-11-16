using System;
using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Interface des modes de jeu hors battle royale : scores, minuteur, points de contrôle, drapeaux, bombe, argent ; menu de classe (touche C) et boutique du Sabotage (touche B).
	/// Elle ne fait que montrer ce que dit le serveur (<see cref="ModeState"/>) et envoyer ses choix (<see cref="Send"/>).
	/// </summary>
	public partial class ModeHud : CanvasLayer
	{
		public Action<CommandType, int> Send;
		public Func<ushort, string> NameOf = id => "Joueur " + id;
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
			string[] names = { "BASE", "A", "B", "C", "BASE" };
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
			_footer.AnchorTop = 1; _footer.AnchorBottom = 1; _footer.OffsetLeft = 16; _footer.OffsetTop = -120; _footer.OffsetBottom = -80;
			_footer.AddThemeFontSizeOverride("font_size", 20);
			_footer.AddThemeColorOverride("font_outline_color", Colors.Black); _footer.AddThemeConstantOverride("outline_size", 6);
			root.AddChild(_footer);

			_actionLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_actionLabel.AnchorLeft = 0.5f; _actionLabel.AnchorRight = 0.5f; _actionLabel.AnchorTop = 0.62f; _actionLabel.OffsetLeft = -200; _actionLabel.OffsetRight = 200;
			_actionLabel.AddThemeFontSizeOverride("font_size", 22);
			root.AddChild(_actionLabel);
			_action = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_action.AnchorLeft = 0.5f; _action.AnchorRight = 0.5f; _action.AnchorTop = 0.62f; _action.OffsetLeft = -140; _action.OffsetRight = 140; _action.OffsetTop = 34; _action.OffsetBottom = 52;
			root.AddChild(_action);

			_menu = new PanelContainer { Visible = false };
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
		private string Side(int team) => $"[color=#{Hex(team == 0 ? Blue : Red)}]{(team == 0 ? "Bleus" : "Rouges")}[/color]";

		public override void _Input(InputEvent e)
		{
			if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
			if (_last.Mode == GameMode.BattleRoyale || _phase == MatchPhase.Over) return;
			if (k.Keycode == Key.B && _last.Mode == GameMode.Sabotage && _last.CanBuy) { ToggleMenu("buy"); GetViewport().SetInputAsHandled(); }
			else if (k.Keycode == Key.C && _last.Mode != GameMode.Sabotage) { ToggleMenu("class"); GetViewport().SetInputAsHandled(); }
			else if (k.Keycode == Key.Escape && MenuOpen) { CloseMenu(); GetViewport().SetInputAsHandled(); }
		}

		private void ToggleMenu(string kind)
		{
			if (MenuOpen && _menuKind == kind) { CloseMenu(); return; }
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
			var title = new Label { Text = _menuKind == "buy" ? $"Boutique — argent : {_last.Money} $" : "Choisis ta classe", HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 24);
			_menuBox.AddChild(title);
			if (_menuKind == "buy")
			{
				_menuBox.AddChild(new Label { Text = "B ou Échap : fermer. Les achats se font pendant la phase d'achat.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
				foreach (var item in Shop.Items)
				{
					if (item.Side != 255 && item.Side != _last.MySide) continue;
					var it = item;
					var b = new Button { Text = $"{it.Name} — {it.Price} $", Disabled = it.Price > _last.Money || (it.DefuseKit && _last.HasKit), CustomMinimumSize = new Vector2(0, 32) };
					b.Pressed += () => { Send?.Invoke(CommandType.Buy, it.Code); b.Disabled = true; };
					_menuBox.AddChild(b);
				}
			}
			else
			{
				_menuBox.AddChild(new Label { Text = "Le changement prend effet à ta prochaine réapparition (tout de suite en salle d'attente).", AutowrapMode = TextServer.AutowrapMode.WordSmart });
				foreach (var kit in Loadouts.Kits)
				{
					var k = kit;
					var b = new Button { Text = $"{(k.Id == _last.ClassId ? "✔  " : "")}{k.Name} — {k.Description}", CustomMinimumSize = new Vector2(0, 46), AutowrapMode = TextServer.AutowrapMode.WordSmart };
					b.Pressed += () => { Send?.Invoke(CommandType.SelectClass, k.Id); _last.ClassId = k.Id; Rebuild(); };
					_menuBox.AddChild(b);
				}
			}
			var close = new Button { Text = "Fermer" };
			close.Pressed += CloseMenu;
			_menuBox.AddChild(close);
		}

		/// <summary>Met l'interface à jour : à appeler à chaque image avec l'état du mode et de la partie.</summary>
		public void Refresh(ModeState m, MatchPhase phase, bool alive, int respawnHint = 0)
		{
			bool classChanged = m.ClassId != _last.ClassId || m.Money != _last.Money;
			_last = m; _phase = phase; _alive = alive;
			if (MenuOpen && _menuKind == "buy" && !m.CanBuy) CloseMenu();
			if (MenuOpen && classChanged && _menuKind == "buy") Rebuild();
			string timer = m.TimeLeft > 0 ? Clock(m.TimeLeft) : "";
			string l1 = "", l2 = "";
			_points.Visible = m.Mode == GameMode.Control;
			switch (m.Mode)
			{
				case GameMode.Deathmatch:
					l1 = $"Éliminations [b]{m.MyScore}[/b] / {m.Target}   ·   meilleur : {m.TopScore}"; l2 = timer;
					break;
				case GameMode.TeamDeathmatch:
					l1 = $"{Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   (objectif {m.Target})"; l2 = timer;
					break;
				case GameMode.CaptureTheFlag:
					l1 = $"{Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   ({m.Target} prises)   {timer}";
					l2 = $"Drapeau {Side(0)} : {FlagText(m.Flag0)}   ·   Drapeau {Side(1)} : {FlagText(m.Flag1)}";
					break;
				case GameMode.Control:
					l1 = $"{Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   (objectif {m.Target})"; l2 = timer;
					for (int i = 0; i < 5; i++)
					{
						var c = m.Points[i];
						var col = c.Owner == 0 ? Blue : c.Owner == 1 ? Red : Gray;
						_pointLabels[i].Modulate = c.Contested ? new Color(1f, 0.85f, 0.2f) : col;
						_pointBars[i].Value = c.Progress;
						_pointBars[i].Modulate = c.Progress < 0 ? Blue : c.Progress > 0 ? Red : Gray;
					}
					break;
				case GameMode.Sabotage:
					l1 = $"Manche {m.Round}   ·   {Side(0)} [b]{m.Score0}[/b]  —  [b]{m.Score1}[/b] {Side(1)}   (premier à {m.Target})";
					l2 = SabotageText(m, timer);
					break;
			}
			if (_line1.Text != l1) _line1.Text = l1;
			if (_line2.Text != l2) _line2.Text = l2;

			string role = m.Mode == GameMode.Sabotage ? (m.MySide == 1 ? "Attaquant" : "Défenseur") + $"   ·   {m.Money} $" + (m.HasKit ? "   ·   kit de désamorçage" : "") : "";
			string hint = m.Mode == GameMode.Sabotage ? (m.CanBuy ? "B : boutique" : "") : "C : classe (" + Loadouts.Get(m.ClassId).Name + ")";
			if (m.Invulnerable) hint += "   (protégé)";
			_footer.Text = (role.Length > 0 ? role + "\n" : "") + hint;
			_footer.Visible = phase != MatchPhase.Over;

			_action.Visible = _actionLabel.Visible = m.Action != 0;
			if (m.Action != 0) { _action.Value = m.ActionProgress; _actionLabel.Text = m.Action == 1 ? "Pose de la bombe…" : "Désamorçage…"; }
		}

		private string FlagText(FlagState f) => f.State == 0 ? "à sa base" : f.State == 1 ? $"porté par {NameOf(f.Carrier)}" : "tombé au sol";

		private string SabotageText(ModeState m, string timer)
		{
			switch ((SabotageStage)m.Stage)
			{
				case SabotageStage.Buy: return $"Phase d'achat — B : boutique — départ dans {timer}";
				case SabotageStage.Round:
					string bomb = m.MySide == 1 ? (m.BombCarrier != 0xFFFF ? $"la bombe est portée par {NameOf(m.BombCarrier)}" : "la bombe est tombée au sol") : "empêche la pose de la bombe";
					return $"{timer}   ·   {bomb}";
				case SabotageStage.Planted: return $"[color=#ffcc33][b]BOMBE POSÉE — site {(char)('A' + m.BombSite)}[/b][/color]   ·   explosion dans {Math.Ceiling(m.BombTimer):0} s";
				default:
					return m.RoundWinner == 255 ? "" : $"Manche gagnée par les {(m.RoundWinner == 1 ? "attaquants" : "défenseurs")}";
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

		public void Build(WorldData data)
		{
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
						string name = i == 0 || i == 4 ? "BASE" : ((char)('A' + i - 1)).ToString();
						AddChild(new Label3D { Text = name, Position = pos + new Vector3(0, 17f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 220, PixelSize = 0.01f, NoDepthTest = true, Modulate = Colors.White, OutlineSize = 20 });
						break;
					}
					case MapPointKind.FlagBase:
						AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = pt.Radius, BottomRadius = pt.Radius, Height = 0.08f, RadialSegments = 40 }, Position = pos + new Vector3(0, 0.06f, 0), MaterialOverride = Glow(pt.Team == 0 ? Blue : Red, 0.4f) });
						break;
					case MapPointKind.BombSite:
						AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = pt.Radius, BottomRadius = pt.Radius, Height = 0.08f, RadialSegments = 48 }, Position = pos + new Vector3(0, 0.06f, 0), MaterialOverride = Glow(new Color(1f, 0.8f, 0.2f), 0.35f) });
						AddChild(new Label3D { Text = ((char)('A' + pt.Index)).ToString(), Position = pos + new Vector3(0, 6f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 220, PixelSize = 0.01f, NoDepthTest = true, Modulate = new Color(1f, 0.85f, 0.3f), OutlineSize = 20 });
						break;
				}
			}
			_flag0 = MakeFlag(Blue); _flag1 = MakeFlag(Red);
			_bomb = new Node3D { Visible = false };
			_bomb.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.35f) }, Position = new Vector3(0, 0.15f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.15f) } });
			_bombLight = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f }, Position = new Vector3(0, 0.34f, 0), MaterialOverride = Glow(Red, 1f) };
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
