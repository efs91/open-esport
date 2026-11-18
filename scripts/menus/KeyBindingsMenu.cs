using System;
using System.Collections.Generic;
using Godot;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Réglages des commandes : une touche par action (clic sur le bouton puis appui sur la nouvelle touche, Échap annule)
	/// et le mode de visée (maintenir ou basculer). Les choix sont sauvegardés et appliqués tout de suite.
	/// </summary>
	public partial class KeyBindingsMenu : CanvasLayer
	{
		public event Action Closed;

		private static readonly (string Action, string Label)[] Rows =
		{
			(InputManager.Actions.MOVE_FORWARD, /*T*/"Avancer"),
			(InputManager.Actions.MOVE_BACK, /*T*/"Reculer"),
			(InputManager.Actions.MOVE_LEFT, /*T*/"Aller à gauche"),
			(InputManager.Actions.MOVE_RIGHT, /*T*/"Aller à droite"),
			(InputManager.Actions.JUMP, /*T*/"Sauter"),
			(InputManager.Actions.SPRINT, /*T*/"Courir"),
			(InputManager.Actions.CROUCH, /*T*/"S'accroupir / glisser"),
			(InputManager.Actions.SHOOT, /*T*/"Tirer"),
			(InputManager.Actions.AIM, /*T*/"Viser"),
			(InputManager.Actions.RELOAD, /*T*/"Recharger"),
			(InputManager.Actions.INTERACT, /*T*/"Interagir / ramasser"),
			(InputManager.Actions.WEAPON_SLOT_1, /*T*/"Arme 1"),
			(InputManager.Actions.WEAPON_SLOT_2, /*T*/"Arme 2"),
			(InputManager.Actions.HOLSTER, /*T*/"Mains nues"),
			(InputManager.Actions.INVENTORY, /*T*/"Inventaire"),
			(InputManager.Actions.USE_HEAL, /*T*/"Menu des soins (maintenir, souris) ; appui bref : soin de vie"),
			(InputManager.Actions.USE_SHIELD, /*T*/"Utiliser un soin (bouclier)"),
			(InputManager.Actions.USE_BOTH, /*T*/"Utiliser un soin (bouclier + vie)"),
			(InputManager.Actions.DROP_SHIELD, /*T*/"Jeter le bouclier"),
			(InputManager.Actions.GRENADE_MENU, /*T*/"Menu des grenades (maintenir, souris)"),
			(InputManager.Actions.THROW_GRENADE, /*T*/"Lancer la grenade choisie"),
			(InputManager.Actions.PING, /*T*/"Ping : désigner un endroit à l'équipe (Maj : danger)"),
			(InputManager.Actions.NEXT_GRENADE, /*T*/"Changer de grenade"),
			(InputManager.Actions.DROP_BOMB, /*T*/"Lâcher la bombe (Sabotage)"),
		};

		private readonly Dictionary<string, Button> _buttons = new();
		private string _waiting;

		public KeyBindingsMenu() { Layer = 120; }

		public override void _Ready()
		{
			var back = new ColorRect { Color = new Color(0.05f, 0.06f, 0.09f, 1f) };
			back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(back);

			var center = new CenterContainer();
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(center);

			var panel = new PanelContainer { CustomMinimumSize = new Vector2(Mathf.Min(1000f, GetViewport().GetVisibleRect().Size.X - 60f), 0) };
			center.AddChild(panel);
			var margin = new MarginContainer();
			foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
			panel.AddChild(margin);
			var column = new VBoxContainer();
			column.AddThemeConstantOverride("separation", 6);
			margin.AddChild(column);

			column.AddChild(new Label { Text = I18n.T("COMMANDES"), HorizontalAlignment = HorizontalAlignment.Center });

			// mode de visée
			var aimRow = new HBoxContainer();
			aimRow.AddChild(new Label { Text = I18n.T("Visée"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			var aim = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
			aim.AddItem(I18n.T("Basculer (un clic vise, un clic retire)"));
			aim.AddItem(I18n.T("Maintenir (visée tant que le bouton est tenu)"));
			aim.Select((int)GameSettings.Aim);
			aim.ItemSelected += i => GameSettings.Aim = (AimMode)i;
			aimRow.AddChild(aim);
			column.AddChild(aimRow);

			// sensibilité de la souris
			var sensRow = new HBoxContainer();
			sensRow.AddChild(new Label { Text = I18n.T("Sensibilité de la souris"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			var sensValue = new Label { Text = GameSettings.MouseSensitivity.ToString("0.0"), CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
			var sens = new HSlider { MinValue = 0.2, MaxValue = 10, Step = 0.1, Value = GameSettings.MouseSensitivity, CustomMinimumSize = new Vector2(240, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
			sens.ValueChanged += v =>
			{
				GameSettings.MouseSensitivity = (float)v;
				sensValue.Text = ((float)v).ToString("0.0");
				foreach (var n in GetTree().GetNodesInGroup("head_movement"))
					if (n is HeadMovement3D h) h.MouseSensitivity = (float)v;
			};
			sensRow.AddChild(sens);
			sensRow.AddChild(sensValue);
			column.AddChild(sensRow);
			column.AddChild(new HSeparator());

			// la liste défile : « Retour » reste visible même dans une petite fenêtre
			var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, Mathf.Min(420f, GetViewport().GetVisibleRect().Size.Y - 220f)) };
			var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			list.AddThemeConstantOverride("separation", 6);
			scroll.AddChild(list);
			column.AddChild(scroll);

			foreach (var (action, label) in Rows)
			{
				var row = new HBoxContainer();
				row.AddChild(new Label { Text = I18n.T(label), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
				var button = new Button { CustomMinimumSize = new Vector2(240, 0), Text = CurrentText(action) };
				string a = action;
				button.Pressed += () => BeginRebind(a);
				row.AddChild(button);
				list.AddChild(row);
				_buttons[action] = button;
			}

			column.AddChild(new HSeparator());
			var bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			bottom.AddThemeConstantOverride("separation", 12);
			var reset = new Button { Text = I18n.T("Touches par défaut") };
			reset.Pressed += ResetAll;
			var close = new Button { Text = I18n.T("Retour") };
			close.Pressed += Close;
			bottom.AddChild(reset);
			bottom.AddChild(close);
			column.AddChild(bottom);
		}

		private static string CurrentText(string action)
		{
			var events = InputMap.ActionGetEvents(action);
			return events.Count > 0 ? GameSettings.Describe(events[0]) : "—";
		}

		private void BeginRebind(string action)
		{
			if (_waiting != null) _buttons[_waiting].Text = CurrentText(_waiting);
			_waiting = action;
			_buttons[action].Text = I18n.T("Appuie sur une touche… (Échap : annuler)");
		}

		public override void _Input(InputEvent e)
		{
			if (_waiting == null)
			{
				if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); }
				return;
			}

			InputEvent chosen = null;
			if (e is InputEventKey { Pressed: true, Echo: false } k)
			{
				if (k.Keycode == Key.Escape) { _buttons[_waiting].Text = CurrentText(_waiting); _waiting = null; GetViewport().SetInputAsHandled(); return; }
				// touche logique (la lettre affichée) ; la rangée de chiffres reste liée à la position physique
				bool digit = _waiting is "weapon_slot_1" or "weapon_slot_2" or "holster";
				chosen = digit ? new InputEventKey { PhysicalKeycode = k.PhysicalKeycode } : new InputEventKey { Keycode = k.Keycode };
			}
			else if (e is InputEventMouseButton { Pressed: true } m && m.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle or MouseButton.Xbutton1 or MouseButton.Xbutton2)
				chosen = new InputEventMouseButton { ButtonIndex = m.ButtonIndex };
			if (chosen == null) return;

			// une touche ne sert qu'à une seule action : elle quitte l'ancienne
			foreach (var (other, _) in Rows)
			{
				if (other == _waiting) continue;
				foreach (var ev in InputMap.ActionGetEvents(other))
					if (GameSettings.Encode(ev) == GameSettings.Encode(chosen))
					{
						InputMap.ActionEraseEvent(other, ev);
						GameSettings.SetBinding(other, new InputEventKey());
						_buttons[other].Text = "—";
					}
			}
			InputMap.ActionEraseEvents(_waiting);
			InputMap.ActionAddEvent(_waiting, chosen);
			GameSettings.SetBinding(_waiting, chosen);
			_buttons[_waiting].Text = GameSettings.Describe(chosen);
			_waiting = null;
			GetViewport().SetInputAsHandled();
		}

		private void ResetAll()
		{
			GameSettings.ResetBindings();
			var input = GetNodeOrNull<InputManager>(GlobalPaths.Managers.INPUT_MANAGER);
			input?.InitializeInputMap();
			foreach (var (action, _) in Rows) _buttons[action].Text = CurrentText(action);
			_waiting = null;
		}

		private void Close()
		{
			Closed?.Invoke();
			QueueFree();
		}
	}
}
