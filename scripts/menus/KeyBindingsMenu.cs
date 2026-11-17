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
			(InputManager.Actions.MOVE_FORWARD, "Avancer"),
			(InputManager.Actions.MOVE_BACK, "Reculer"),
			(InputManager.Actions.MOVE_LEFT, "Aller à gauche"),
			(InputManager.Actions.MOVE_RIGHT, "Aller à droite"),
			(InputManager.Actions.JUMP, "Sauter"),
			(InputManager.Actions.SPRINT, "Courir"),
			(InputManager.Actions.CROUCH, "S'accroupir / glisser"),
			(InputManager.Actions.SHOOT, "Tirer"),
			(InputManager.Actions.AIM, "Viser"),
			(InputManager.Actions.RELOAD, "Recharger"),
			(InputManager.Actions.INTERACT, "Interagir / ramasser"),
			(InputManager.Actions.WEAPON_SLOT_1, "Arme 1"),
			(InputManager.Actions.WEAPON_SLOT_2, "Arme 2"),
			(InputManager.Actions.HOLSTER, "Mains nues"),
			(InputManager.Actions.INVENTORY, "Inventaire"),
			(InputManager.Actions.USE_HEAL, "Menu des soins (maintenir, souris) ; appui bref : soin de vie"),
			(InputManager.Actions.USE_SHIELD, "Utiliser un soin (bouclier)"),
			(InputManager.Actions.USE_BOTH, "Utiliser un soin (bouclier + vie)"),
			(InputManager.Actions.DROP_SHIELD, "Jeter le bouclier"),
			(InputManager.Actions.GRENADE_MENU, "Menu des grenades (maintenir, souris)"),
			(InputManager.Actions.THROW_GRENADE, "Lancer la grenade choisie"),
			(InputManager.Actions.PING, "Ping : désigner un endroit à l'équipe (Maj : danger)"),
			(InputManager.Actions.NEXT_GRENADE, "Changer de grenade"),
			(InputManager.Actions.DROP_BOMB, "Lâcher la bombe (Sabotage)"),
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

			var panel = new PanelContainer { CustomMinimumSize = new Vector2(520, 0) };
			center.AddChild(panel);
			var margin = new MarginContainer();
			foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
			panel.AddChild(margin);
			var column = new VBoxContainer();
			column.AddThemeConstantOverride("separation", 6);
			margin.AddChild(column);

			column.AddChild(new Label { Text = "COMMANDES", HorizontalAlignment = HorizontalAlignment.Center });

			// mode de visée
			var aimRow = new HBoxContainer();
			aimRow.AddChild(new Label { Text = "Visée", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			var aim = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
			aim.AddItem("Basculer (un clic vise, un clic retire)");
			aim.AddItem("Maintenir (visée tant que le bouton est tenu)");
			aim.Select((int)GameSettings.Aim);
			aim.ItemSelected += i => GameSettings.Aim = (AimMode)i;
			aimRow.AddChild(aim);
			column.AddChild(aimRow);
			column.AddChild(new HSeparator());

			// la liste défile : « Retour » reste visible même dans une petite fenêtre
			var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, Mathf.Min(420f, GetViewport().GetVisibleRect().Size.Y - 220f)) };
			var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			list.AddThemeConstantOverride("separation", 6);
			scroll.AddChild(list);
			column.AddChild(scroll);

			foreach (var (action, label) in Rows)
			{
				var row = new HBoxContainer();
				row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
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
			var reset = new Button { Text = "Touches par défaut" };
			reset.Pressed += ResetAll;
			var close = new Button { Text = "Retour" };
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
			_buttons[action].Text = "Appuie sur une touche… (Échap : annuler)";
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
