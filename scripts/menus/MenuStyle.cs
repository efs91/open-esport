using Godot;

namespace Openesport.Menus
{
	/// <summary>
	/// Le décor commun des pages de menu : l'image du menu principal sous un voile sombre, et des « cartes » (panneaux sombres aux coins arrondis)
	/// pour poser le contenu lisiblement par-dessus. Toutes les pages qui suivent le menu principal l'utilisent.
	/// </summary>
	public static class MenuStyle
	{
		public static readonly Color CardColor = new Color(0.04f, 0.05f, 0.08f, 0.80f);
		public static readonly Color CardBorder = new Color(1f, 1f, 1f, 0.10f);
		public static readonly Color Muted = new Color(0.62f, 0.66f, 0.72f);

		/// <summary>Plein écran : image de fond du menu et voile sombre. Arrête aussi les clics (rien ne traverse vers le menu en dessous).</summary>
		public static Control Backdrop(float veil = 0.5f)
		{
			var root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			var bg = new TextureRect
			{
				Texture = GD.Load<Texture2D>("res://assets/textures/Menu/MenuBackgroug.png"),
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(bg);
			var shade = new ColorRect { Color = new Color(0, 0, 0, veil), MouseFilter = Control.MouseFilterEnum.Ignore };
			shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(shade);
			return root;
		}

		public static StyleBoxFlat CardStyle(int margin = 16)
		{
			var s = new StyleBoxFlat { BgColor = CardColor, BorderColor = CardBorder };
			s.SetBorderWidthAll(1);
			s.SetCornerRadiusAll(10);
			s.SetContentMarginAll(margin);
			return s;
		}

		/// <summary>Un panneau sombre autour de <paramref name="content"/>, d'une largeur minimale donnée (0 = celle du contenu).</summary>
		public static PanelContainer Card(Control content, float minWidth = 0f, int margin = 16)
		{
			var card = new PanelContainer { CustomMinimumSize = new Vector2(minWidth, 0) };
			card.AddThemeStyleboxOverride("panel", CardStyle(margin));
			card.AddChild(content);
			return card;
		}
	}
}
