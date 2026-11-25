using Godot;

namespace Openesport.Menus
{
	/// <summary>Identité commune : bleu nuit, traits cyan et actions vermillon.</summary>
	public static class MenuStyle
	{
		public static readonly Color CardColor = new Color("101b2bf5");
		public static readonly Color CardBorder = new Color("30445b");
		public static readonly Color Muted = new Color("a4b5c9");
		public static readonly Color Ink = new Color("edf4fc");
		public static readonly Color Cyan = new Color("55d5ed");
		public static readonly Color Red = new Color("ed4059");
		public const string BackgroundPath = "res://assets/textures/Menu/MenuBackgroug.png";
		private static Theme _theme;
		public static Theme SharedTheme => _theme ??= BuildTheme();

		private static StyleBoxFlat Surface(Color fill, Color border, int margin = 14)
		{
			var style = new StyleBoxFlat { BgColor = fill, BorderColor = border };
			style.SetBorderWidthAll(1);
			style.SetCornerRadiusAll(6);
			style.SetContentMarginAll(margin);
			return style;
		}

		private static void Buttons(Theme theme, string type, Color fill, Color border, bool navigation = false)
		{
			var normal = Surface(fill, border, 10);
			normal.ContentMarginLeft = normal.ContentMarginRight = 18;
			if (navigation) normal.BorderWidthLeft = 3;
			theme.SetStylebox("normal", type, normal);
			var hover = (StyleBoxFlat)normal.Duplicate();
			hover.BgColor = fill.Lightened(0.10f); hover.BorderColor = Cyan;
			theme.SetStylebox("hover", type, hover);
			var pressed = (StyleBoxFlat)normal.Duplicate();
			pressed.BgColor = fill.Darkened(0.20f); pressed.BorderColor = Cyan;
			theme.SetStylebox("pressed", type, pressed);
			theme.SetStylebox("hover_pressed", type, pressed);
			var disabled = (StyleBoxFlat)normal.Duplicate();
			disabled.BgColor = new Color("101927"); disabled.BorderColor = new Color("263448");
			theme.SetStylebox("disabled", type, disabled);
			var focus = Surface(Colors.Transparent, Cyan, 0);
			focus.SetBorderWidthAll(2);
			theme.SetStylebox("focus", type, focus);
			foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
				theme.SetColor(state, type, Ink);
			theme.SetColor("font_disabled_color", type, new Color("66788e"));
			theme.SetColor("icon_normal_color", type, navigation ? Cyan : Ink);
			theme.SetColor("icon_hover_color", type, Ink);
			theme.SetColor("icon_pressed_color", type, Ink);
			theme.SetConstant("h_separation", type, 18);
			theme.SetConstant("icon_max_width", type, 28);
		}

		private static Theme BuildTheme()
		{
			var theme = new Theme { DefaultFontSize = 18 };
			theme.SetColor("font_color", "Label", Ink);
			theme.SetColor("default_color", "RichTextLabel", Ink);
			Buttons(theme, "Button", new Color("152338"), CardBorder);
			Buttons(theme, "OptionButton", new Color("152338"), CardBorder);
			theme.SetTypeVariation("MenuNavigation", "Button");
			Buttons(theme, "MenuNavigation", new Color("101d30"), CardBorder, true);
			theme.SetTypeVariation("MenuPrimary", "Button");
			Buttons(theme, "MenuPrimary", new Color("b92240"), Red, true);
			theme.SetColor("icon_normal_color", "MenuPrimary", Ink);
			theme.SetTypeVariation("MenuDanger", "Button");
			Buttons(theme, "MenuDanger", new Color("241b2a"), new Color("65404e"), true);
			theme.SetColor("icon_normal_color", "MenuDanger", new Color("ff8393"));
			theme.SetStylebox("panel", "PanelContainer", CardStyle());
			theme.SetStylebox("panel", "Panel", CardStyle());
			foreach (var type in new[] { "LineEdit", "TextEdit", "ItemList", "Tree" })
			{
				theme.SetStylebox("normal", type, Surface(new Color("0c1523"), CardBorder, 10));
				theme.SetStylebox("focus", type, Surface(Colors.Transparent, Cyan, 0));
				theme.SetColor("font_color", type, Ink);
				theme.SetColor("font_placeholder_color", type, Muted);
				theme.SetColor("caret_color", type, Cyan);
				theme.SetColor("selection_color", type, new Color("24546e"));
			}
			theme.SetStylebox("selected", "ItemList", Surface(new Color("24546e"), Cyan, 4));
			theme.SetStylebox("panel", "PopupMenu", CardStyle(8));
			theme.SetStylebox("hover", "PopupMenu", Surface(new Color("24546e"), Cyan, 8));
			theme.SetColor("font_color", "PopupMenu", Ink);
			theme.SetColor("font_hover_color", "PopupMenu", Ink);
			theme.SetStylebox("background", "ProgressBar", Surface(new Color("0c1523"), CardBorder, 4));
			theme.SetStylebox("fill", "ProgressBar", Surface(new Color("258ca8"), Cyan, 4));
			theme.SetStylebox("panel", "PopupPanel", CardStyle(10));
			var separator = new StyleBoxLine { Color = CardBorder, Thickness = 1 };
			theme.SetStylebox("separator", "HSeparator", separator);
			foreach (var type in new[] { "HScrollBar", "VScrollBar" })
			{
				theme.SetStylebox("scroll", type, Surface(new Color("0c1523"), Colors.Transparent, 3));
				theme.SetStylebox("grabber", type, Surface(new Color("36546c"), Colors.Transparent, 3));
				theme.SetStylebox("grabber_highlight", type, Surface(Cyan, Colors.Transparent, 3));
				theme.SetStylebox("grabber_pressed", type, Surface(Cyan.Darkened(0.2f), Colors.Transparent, 3));
			}
			theme.SetStylebox("slider", "HSlider", Surface(new Color("0c1523"), CardBorder, 3));
			theme.SetStylebox("grabber_area", "HSlider", Surface(new Color("258ca8"), Colors.Transparent, 3));
			theme.SetStylebox("grabber_area_highlight", "HSlider", Surface(Cyan, Colors.Transparent, 3));
			theme.SetIcon("grabber", "HSlider", Icon("slider_handle"));
			theme.SetIcon("grabber_highlight", "HSlider", Icon("slider_handle"));
			theme.SetIcon("checked", "CheckButton", Icon("toggle_on"));
			theme.SetIcon("unchecked", "CheckButton", Icon("toggle_off"));
			return theme;
		}

		public static Texture2D Icon(string name) => GD.Load<Texture2D>($"res://assets/ui/menu/icons/{name}.svg");

		public static Button NavigationButton(string text, string icon, string variant = "MenuNavigation") => new Button
		{
			Text = text, Icon = Icon(icon), Theme = SharedTheme, ThemeTypeVariation = variant,
			CustomMinimumSize = new Vector2(320, 64), Alignment = HorizontalAlignment.Left,
			IconAlignment = HorizontalAlignment.Left, ExpandIcon = true
		};

		public static Control Backdrop()
		{
			var root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			var bg = new TextureRect
			{
				Texture = GD.Load<Texture2D>(BackgroundPath),
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(bg);
			root.AddChild(Veil());
			return root;
		}

		/// <summary>Voile sombre posé sur l'image de fond, pour que le contenu reste lisible.</summary>
		public static ColorRect Veil()
		{
			var veil = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
			veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			return veil;
		}

		public static StyleBoxFlat CardStyle(int margin = 16) => Surface(CardColor, CardBorder, margin);

		public static PanelContainer Card(Control content, float minWidth = 0f, int margin = 16)
		{
			var card = new PanelContainer { Theme = SharedTheme, CustomMinimumSize = new Vector2(minWidth, 0) };
			card.AddThemeStyleboxOverride("panel", CardStyle(margin));
			card.AddChild(content);
			return card;
		}
	}
}
