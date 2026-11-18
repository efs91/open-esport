using Godot;
using System;
using Openesport.Utils;

namespace Openesport.Inventory
{
	/// <summary>
	/// Interface de l'inventaire : invite de ramassage, barre des armes et panneau d'inventaire (Tab).
	/// </summary>
	public partial class DragButton : Button
	{
		public Godot.Collections.Dictionary DragData;
		public string DragText = "";
		public Func<Godot.Collections.Dictionary, bool> Accepts;
		public Action<Godot.Collections.Dictionary> Dropped;

		public override Variant _GetDragData(Vector2 atPosition)
		{
			if (DragData == null) return default;
			var preview = new PanelContainer { Modulate = new Color(1, 1, 1, 0.9f) };
			var box = new StyleBoxFlat { BgColor = new Color(0.16f, 0.18f, 0.21f, 0.95f), BorderColor = new Color(0.95f, 0.65f, 0.15f) };
			box.SetBorderWidthAll(2); box.SetCornerRadiusAll(4); box.SetContentMarginAll(8);
			preview.AddThemeStyleboxOverride("panel", box);
			preview.AddChild(new Label { Text = DragText });
			SetDragPreview(preview);
			return DragData;
		}

		public override bool _CanDropData(Vector2 atPosition, Variant data)
			=> Accepts != null && data.VariantType == Variant.Type.Dictionary && Accepts(data.AsGodotDictionary());

		public override void _DropData(Vector2 atPosition, Variant data) => Dropped?.Invoke(data.AsGodotDictionary());
	}

	/// <summary>Le fond de l'écran quand l'inventaire est ouvert : y déposer un objet le jette à terre.</summary>
	public partial class DropCatcher : Control
	{
		public Action<Godot.Collections.Dictionary> Dropped;
		public override bool _CanDropData(Vector2 atPosition, Variant data) => data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary().ContainsKey("src");
		public override void _DropData(Vector2 atPosition, Variant data) => Dropped?.Invoke(data.AsGodotDictionary());
	}

	public partial class InventoryUI : CanvasLayer
	{
		private static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.11f, 0.88f);
		private static readonly Color SlotColor = new Color(0.16f, 0.18f, 0.21f, 0.95f);
		private static readonly Color EmptySlotColor = new Color(0.11f, 0.12f, 0.14f, 0.8f);
		private static readonly Color AccentColor = new Color(0.95f, 0.65f, 0.15f);
		private static readonly Color DimTextColor = new Color(0.65f, 0.68f, 0.72f);

		public PlayerInventory Inventory;

		private Label _prompt;
		private Label _toast;
		private HBoxContainer _weaponBar;
		private CenterContainer _panelRoot;
		private DropCatcher _dropCatcher;
		private VBoxContainer _panelContent;

		private bool _dirty = true;
		private double _toastTime;

		private InputEvents _inputEvents;
		private GameManager _gameManager;

		public bool IsOpen => _panelRoot != null && _panelRoot.Visible;

		public override void _Ready()
		{
			// Au-dessus de la vue lunette
			Layer = 2;
			AddToGroup("game_hud");
			BuildLayout();

			Inventory.Changed += OnInventoryChanged;
			Inventory.Message += ShowMessage;

			_inputEvents = GetNode<InputManager>(GlobalPaths.Managers.INPUT_MANAGER).GetInputEvents();
			_inputEvents.OnInventoryToggleTriggered += Toggle;

			_gameManager = GetNode<GameManager>(GlobalPaths.Managers.GAME_MANAGER);
			_gameManager.GameStateChanged += OnGameStateChanged;
		}

		public override void _ExitTree()
		{
			if (Inventory != null)
			{
				Inventory.Changed -= OnInventoryChanged;
				Inventory.Message -= ShowMessage;
			}
			if (_inputEvents != null)
				_inputEvents.OnInventoryToggleTriggered -= Toggle;
			if (_gameManager != null)
				_gameManager.GameStateChanged -= OnGameStateChanged;
		}

		private string _ammoSignature = "";
		private double _ammoCheck;

		public override void _Process(double delta)
		{
			// le chargeur change à chaque tir : on redessine seulement quand un décompte a vraiment changé
			_ammoCheck -= delta;
			if (_ammoCheck <= 0 && Inventory != null)
			{
				_ammoCheck = 0.1;
				string sig = AmmoSignature();
				if (sig != _ammoSignature) { _ammoSignature = sig; _dirty = true; }
			}

			if (_dirty)
			{
				_dirty = false;
				Refresh();
			}

			if (_toastTime > 0)
			{
				_toastTime -= delta;
				if (_toastTime <= 0)
					_toast.Text = "";
			}
		}

		private void OnInventoryChanged()
		{
			// Le rafraîchissement est différé : il peut être déclenché depuis un bouton du panneau
			_dirty = true;
		}

		private void ShowMessage(string text)
		{
			_toast.Text = text;
			_toastTime = 2.5;
		}

		private void OnGameStateChanged(int newState)
		{
			// Pause ou retour au menu : on ferme le panneau, l'InputManager gère la souris
			_panelRoot.Visible = false;
			_dropCatcher.Visible = false;
		}

		public void Toggle()
		{
			Openesport.Diagnostics.LagTracer.Event("inventaire : ouvrir / fermer");
			if (_gameManager.GetCurrentState() != GameManager.GameState.Gameplay)
				return;

			bool open = !_panelRoot.Visible;
			_panelRoot.Visible = open;
			_dropCatcher.Visible = open;
			Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
			_dirty = true;
		}

		// ------------------------------------------------------------------
		// Construction
		// ------------------------------------------------------------------

		private void BuildLayout()
		{
			var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);

			_prompt = CreateCenteredLabel(root, 60, Colors.White, 20);
			_toast = CreateCenteredLabel(root, 95, AccentColor, 18);

			_weaponBar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
			_weaponBar.AddThemeConstantOverride("separation", 8);
			root.AddChild(_weaponBar);
			_weaponBar.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
			_weaponBar.GrowHorizontal = Control.GrowDirection.Begin;
			_weaponBar.GrowVertical = Control.GrowDirection.Begin;
			_weaponBar.OffsetRight = -20;
			_weaponBar.OffsetBottom = -20;
			_weaponBar.OffsetLeft = -20;
			_weaponBar.OffsetTop = -20;

			// fond de l'écran : un objet déposé ici (hors du panneau) est jeté à terre
			_dropCatcher = new DropCatcher { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
			_dropCatcher.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			_dropCatcher.Dropped = DropOnGround;
			root.AddChild(_dropCatcher);

			_panelRoot = new CenterContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			root.AddChild(_panelRoot);
			_panelRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

			var panel = new PanelContainer();
			panel.AddThemeStyleboxOverride("panel", MakeBox(PanelColor, new Color(0.3f, 0.32f, 0.36f), 20));
			_panelRoot.AddChild(panel);

			_panelContent = new VBoxContainer();
			_panelContent.AddThemeConstantOverride("separation", 12);
			panel.AddChild(_panelContent);
		}

		private static Label CreateCenteredLabel(Control parent, float offsetFromCenter, Color color, int fontSize)
		{
			var label = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			label.AddThemeColorOverride("font_color", color);
			label.AddThemeColorOverride("font_outline_color", Colors.Black);
			label.AddThemeConstantOverride("outline_size", 6);
			label.AddThemeFontSizeOverride("font_size", fontSize);
			parent.AddChild(label);
			label.AnchorLeft = 0;
			label.AnchorRight = 1;
			label.AnchorTop = 0.5f;
			label.AnchorBottom = 0.5f;
			label.OffsetTop = offsetFromCenter;
			label.OffsetBottom = offsetFromCenter + 30;
			return label;
		}

		private static StyleBoxFlat MakeBox(Color background, Color border, int margin = 8)
		{
			var box = new StyleBoxFlat
			{
				BgColor = background,
				BorderColor = border
			};
			box.SetBorderWidthAll(2);
			box.SetCornerRadiusAll(4);
			box.SetContentMarginAll(margin);
			return box;
		}

		private static Label MakeLabel(string text, Color color, int fontSize = 16)
		{
			var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
			label.AddThemeColorOverride("font_color", color);
			label.AddThemeFontSizeOverride("font_size", fontSize);
			return label;
		}

		private static DragButton MakeSlotButton(string text, bool filled, Vector2 size, Action onLeft, Action onRight)
		{
			var button = new DragButton
			{
				Text = text,
				CustomMinimumSize = size,
				FocusMode = Control.FocusModeEnum.None,
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
				ClipText = true,
				Disabled = !filled
			};
			button.AddThemeFontSizeOverride("font_size", 14);

			var normal = MakeBox(filled ? SlotColor : EmptySlotColor, new Color(0.3f, 0.32f, 0.36f));
			button.AddThemeStyleboxOverride("normal", normal);
			button.AddThemeStyleboxOverride("disabled", normal);
			button.AddThemeStyleboxOverride("hover", MakeBox(SlotColor.Lightened(0.12f), AccentColor));
			button.AddThemeStyleboxOverride("pressed", MakeBox(SlotColor.Lightened(0.2f), AccentColor));

			if (filled)
			{
				if (onLeft != null)
					button.Pressed += onLeft;
				if (onRight != null)
				{
					button.GuiInput += ev =>
					{
						if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
							onRight();
					};
				}
			}

			return button;
		}

		// ------------------------------------------------------------------
		// Rafraîchissement
		// ------------------------------------------------------------------

		private void Refresh()
		{
			var target = Inventory.Target;
			_prompt.Text = target != null && !IsOpen ? $"[E] Ramasser : {target.GetPromptName()}" : "";
			// l'invite prend la couleur du niveau de l'objet visé (blanc, bleu, violet, rouge)
			_prompt.AddThemeColorOverride("font_color", target != null ? ItemTierStyle.ColorOf(target.Tier) : Colors.White);

			RefreshWeaponBar();
			if (IsOpen)
				RefreshPanel();
		}

		private static void Clear(Node container)
		{
			foreach (var child in container.GetChildren())
			{
				container.RemoveChild(child);
				child.QueueFree();
			}
		}

		private static Godot.Collections.Dictionary Data(string src, int index) => new Godot.Collections.Dictionary { ["src"] = src, ["index"] = index };

		/// <summary>Un emplacement d'arme accepte un viseur glissé depuis le sac (si l'arme en prend un).</summary>
		private void SetOpticTarget(DragButton button, int slot, WeaponSlotState weapon)
		{
			if (weapon == null || !weapon.Definition.AcceptsOptic) return;
			button.Disabled = false;
			button.Accepts = d =>
			{
				if (d["src"].AsString() != "bag") return false;
				int i = d["index"].AsInt32();
				return i >= 0 && i < Inventory.Items.Count && Inventory.Items[i].Definition?.Type == ItemType.Optic;
			};
			button.Dropped = d => Inventory.EquipOpticOn(slot, d["index"].AsInt32());
		}

		/// <summary>Un objet lâché hors du panneau est jeté à terre.</summary>
		private void DropOnGround(Godot.Collections.Dictionary d)
		{
			int i = d["index"].AsInt32();
			switch (d["src"].AsString())
			{
				case "bag": Inventory.DropItem(i); break;
				case "weapon": Inventory.DropWeapon(i); break;
				case "optic": Inventory.DropOptic(i); break;
				case "shield": Inventory.DropShield(); break;
			}
		}

		private static readonly string[] AmmoTypeNames = { null, "9mm", "rifle", "shell", "sniper" };

		/// <summary>
		/// Munitions d'une arme : « chargeur / total » où le total est le chargeur plus les munitions du sac
		/// (ex. « 30 / 150 » = 30 dans le chargeur, 120 en réserve). Null pour le couteau.
		/// </summary>
		private string AmmoText(WeaponSlotState weapon)
		{
			var core = weapon?.Definition == null ? null : OpenEsport.Core.ItemDefs.ByName(weapon.Definition.Id);
			var wd = core == null ? null : OpenEsport.Core.WeaponDefs.Get(core.Id);
			if (wd == null || wd.Melee || wd.AmmoType == 0 || wd.AmmoType >= AmmoTypeNames.Length) return null;
			int magazine = weapon.Magazine < 0 ? wd.MagazineSize : weapon.Magazine;
			int reserve = Inventory.GetAmmo(AmmoTypeNames[wd.AmmoType]);
			return $"{magazine} / {magazine + reserve}";
		}

		/// <summary>Résumé de tous les décomptes : quand il change, la barre d'armes est redessinée.</summary>
		private string AmmoSignature()
		{
			var sb = new System.Text.StringBuilder();
			foreach (var w in Inventory.Weapons) sb.Append(AmmoText(w)).Append(w?.ItemId).Append('|');
			return sb.ToString();
		}

		private string WeaponText(WeaponSlotState weapon)
		{
			if (weapon == null)
				return "—";
			string text = weapon.Definition.DisplayName;
			string ammo = AmmoText(weapon);
			if (ammo != null) text += "\n" + ammo;
			var optic = weapon.Optic;
			return optic != null ? $"{text}\n{optic.DisplayName}" : text;
		}

		private void RefreshWeaponBar()
		{
			Clear(_weaponBar);

			for (int i = 0; i <= Inventory.Weapons.Length; i++)
			{
				bool isHands = i == Inventory.Weapons.Length;
				bool active = isHands ? Inventory.ActiveWeapon < 0 : Inventory.ActiveWeapon == i;
				string text = isHands ? "Mains nues" : WeaponText(Inventory.Weapons[i]);

				var card = new PanelContainer
				{
					CustomMinimumSize = new Vector2(isHands ? 110 : 170, 56),
					MouseFilter = Control.MouseFilterEnum.Ignore
				};
				var cardWeapon = isHands ? null : Inventory.Weapons[i];
				var idleBorder = cardWeapon != null ? ItemTierStyle.ColorOf(cardWeapon.Tier).Darkened(0.35f) : new Color(0.3f, 0.32f, 0.36f);
				card.AddThemeStyleboxOverride("panel", MakeBox(PanelColor, active ? AccentColor : idleBorder));

				var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
				row.AddThemeConstantOverride("separation", 8);
				row.AddChild(MakeLabel((i + 1).ToString(), active ? AccentColor : DimTextColor, 20));
				var weaponForColor = isHands ? null : Inventory.Weapons[i];
				var nameColor = weaponForColor != null ? ItemTierStyle.ColorOf(weaponForColor.Tier) : (active ? Colors.White : DimTextColor);
				if (weaponForColor != null && !active) nameColor = nameColor.Darkened(0.25f);
				row.AddChild(MakeLabel(text, nameColor, 14));
				card.AddChild(row);
				_weaponBar.AddChild(card);
			}
		}

		/// <summary>Image à gauche du texte d'un bouton, à la taille voulue.</summary>
		private static void SetIcon(Button button, Texture2D tex, int size)
		{
			if (tex == null) return;
			button.Icon = tex;
			button.IconAlignment = HorizontalAlignment.Left;
			button.AddThemeConstantOverride("icon_max_width", size);
		}

		/// <summary>Image à gauche du texte d'un bouton, à la taille voulue.</summary>
		private static void SetIcon(Button button, string path, int size)
		{
			if (!ResourceLoader.Exists(path)) return;
			var tex = GD.Load<Texture2D>(path);
			if (tex == null) return;
			var img = tex.GetImage();
			img.Resize(size * 3 / 2, size, Image.Interpolation.Lanczos);
			button.Icon = ImageTexture.CreateFromImage(img);
			button.IconAlignment = HorizontalAlignment.Left;
		}

		private void RefreshPanel()
		{
			Clear(_panelContent);

			_panelContent.AddChild(MakeLabel("INVENTAIRE", Colors.White, 24));

			// --- Armes ---
			_panelContent.AddChild(MakeLabel("Armes", DimTextColor));
			var weaponsRow = new HBoxContainer();
			weaponsRow.AddThemeConstantOverride("separation", 12);
			_panelContent.AddChild(weaponsRow);

			for (int i = 0; i < Inventory.Weapons.Length; i++)
			{
				int slot = i;
				var weapon = Inventory.Weapons[i];
				bool active = Inventory.ActiveWeapon == i;

				var column = new VBoxContainer();
				column.AddThemeConstantOverride("separation", 4);
				weaponsRow.AddChild(column);

				string title = weapon != null ? weapon.Definition.DisplayName : "Vide";
				string ammoLine = AmmoText(weapon);
				if (ammoLine != null) title += $"   {ammoLine}";
				if (weapon != null) title += $"   [{ItemTierStyle.NameOf(weapon.Tier)}]";
				var weaponButton = MakeSlotButton($"{i + 1} — {title}", weapon != null, new Vector2(250, 60),
					() => Inventory.SelectWeapon(slot),
					() => Inventory.DropWeapon(slot));
				if (weapon != null)
				{
					var tierColor = ItemTierStyle.ColorOf(weapon.Tier);
					weaponButton.AddThemeColorOverride("font_color", tierColor);
					weaponButton.AddThemeColorOverride("font_hover_color", tierColor.Lightened(0.3f));
					weaponButton.AddThemeStyleboxOverride("normal", MakeBox(SlotColor, active ? AccentColor : tierColor.Darkened(0.35f)));
				}
				else if (active)
					weaponButton.AddThemeStyleboxOverride("normal", MakeBox(SlotColor, AccentColor));
				if (weapon != null) { weaponButton.DragData = Data("weapon", slot); weaponButton.DragText = weapon.Definition.DisplayName; SetIcon(weaponButton, ItemIcons.Get(weapon.ItemId), 56); }
				SetOpticTarget(weaponButton, slot, weapon);
				column.AddChild(weaponButton);

				bool acceptsOptic = weapon != null && weapon.Definition.AcceptsOptic;
				string opticText = weapon?.Optic != null
					? $"Viseur : {weapon.Optic.DisplayName}"
					: acceptsOptic ? "Viseur : aucun" : "";
				var opticButton = MakeSlotButton(opticText, weapon?.Optic != null, new Vector2(250, 40),
					() => Inventory.UnequipOptic(slot),
					null);
				if (weapon?.Optic != null) { opticButton.DragData = Data("optic", slot); opticButton.DragText = weapon.Optic.DisplayName; SetIcon(opticButton, ItemIcons.Get(weapon.OpticId), 34); }
				SetOpticTarget(opticButton, slot, weapon);
				column.AddChild(opticButton);
			}

			// --- Bouclier ---
			string shieldText = Inventory.ShieldTier >= 0
				? $"Bouclier {ItemTierStyle.NameOf(Inventory.ShieldTier)} — {ItemTierStyle.ShieldPoints(Inventory.ShieldTier)} PS"
				: I18n.T("Pas de bouclier");
			var shieldButton = MakeSlotButton(shieldText, Inventory.ShieldTier >= 0, new Vector2(520, 64), null, () => Inventory.DropShield());
			if (Inventory.ShieldTier >= 0)
			{
				var tc = ItemTierStyle.ColorOf(Inventory.ShieldTier);
				shieldButton.AddThemeColorOverride("font_color", tc);
				shieldButton.AddThemeColorOverride("font_hover_color", tc.Lightened(0.3f));
				shieldButton.AddThemeStyleboxOverride("normal", MakeBox(SlotColor, tc.Darkened(0.35f)));
				SetIcon(shieldButton, ItemTierStyle.ShieldIcon(Inventory.ShieldTier), 56);
			}
			if (Inventory.ShieldTier >= 0) { shieldButton.DragData = Data("shield", 0); shieldButton.DragText = "Bouclier"; }
			_panelContent.AddChild(shieldButton);

			// --- Sac à dos ---
			string backpack = Inventory.BackpackLevel > 1 ? I18n.T("Sac à dos niv. {0} ({1})", Inventory.BackpackLevel, ItemTierStyle.NameOf(ItemTierStyle.TierOfBackpackLevel(Inventory.BackpackLevel))) : I18n.T("Sans sac à dos");
			_panelContent.AddChild(MakeLabel($"{backpack} — {Inventory.Items.Count} / {Inventory.Capacity} emplacements",
				Inventory.BackpackLevel > 1 ? ItemTierStyle.ColorOf(ItemTierStyle.TierOfBackpackLevel(Inventory.BackpackLevel)) : DimTextColor));

			var grid = new GridContainer { Columns = Inventory.Capacity / 2 };
			grid.AddThemeConstantOverride("h_separation", 6);
			grid.AddThemeConstantOverride("v_separation", 6);
			_panelContent.AddChild(grid);

			for (int i = 0; i < Inventory.Capacity; i++)
			{
				int index = i;
				var stack = i < Inventory.Items.Count ? Inventory.Items[i] : null;
				var def = stack?.Definition;
				string label = def == null ? "" : def.Type == ItemType.Ammo || def.Type == ItemType.Consumable || def.Type == ItemType.Grenade ? $"{def.DisplayName}\nx{stack.Count}" : def.DisplayName;
				var slotButton = MakeSlotButton(label, def != null, new Vector2(150, 76),
					def != null && def.Type == ItemType.Optic ? () => Inventory.EquipOpticFromBackpack(index)
						: def != null && def.Type == ItemType.Consumable ? () => Inventory.UseConsumable(def.Id) : null,
					() => Inventory.DropItem(index));
				if (def != null && def.Type == ItemType.Consumable && def.IconPath != null) SetIcon(slotButton, def.IconPath, 44);
				else if (def != null) SetIcon(slotButton, ItemIcons.Get(def.Id), 52);       // miniature du modèle
				if (def != null) { slotButton.DragData = Data("bag", index); slotButton.DragText = def.DisplayName; }
				// on peut y ranger le viseur d'une arme (glissé hors de l'arme)
				slotButton.Disabled = false;
				slotButton.Accepts = d => d["src"].AsString() == "optic";
				slotButton.Dropped = d => Inventory.UnequipOptic(d["index"].AsInt32());
				grid.AddChild(slotButton);
			}

			_panelContent.AddChild(MakeLabel(
				I18n.T("Clic gauche : équiper un viseur, prendre une arme, utiliser un soin  —  Clic droit : jeter  —  Glisser-déposer : un viseur sur une arme pour l'équiper, un objet hors du panneau pour le jeter  —  Tab : fermer"),
				DimTextColor, 13));
		}
	}
}
