using System;
using Godot;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Page « Crédits » : les auteurs des modèles, textures, polices et extensions utilisés par le jeu, avec leur licence et un lien
	/// (cliquable, ouvert dans le navigateur). Les noms et titres ne sont pas traduits ; seuls les intitulés de section le sont.
	/// </summary>
	public partial class CreditsScreen : CanvasLayer
	{
		public event Action Closed;

		private const string CcBy = "CC BY 4.0";
		private const string CcByNc = "CC BY-NC 4.0";
		private const string Cc0 = "CC0 1.0";
		private const string Mit = "MIT";

		/// <summary>(titre de section, à traduire) puis les lignes : œuvre, auteur, licence, lien.</summary>
		private static readonly (string Section, (string Work, string Author, string License, string Url)[] Rows)[] Sections =
		{
			(/*T*/"Armes et mains", new[]
			{
				("fps AK animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/fps-ak-animated-b0b1bae40337449189483f6187cf7af7"),
				("fps pistol Animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/fps-pistol-animated-a8cdd6c2e936484aaf67e1786c8320d9"),
				("knife Animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/knife-animated-f3140db917cc43389c14b5b2536c1dc8"),
				("lmg animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/lmg-animated-c02dc6663f344008a502148e751c9974"),
				("sawnoff animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/sawnoff-animated-806f8327bed34fc3b0e3911d01f0566d"),
				("shotgun animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/shotgun-animated-c3d3cf425869463a84d650c15e3af0d2"),
				("fps animated smg", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/fps-animated-smg-ea3dad7478624495a5a46f40127b0579"),
				("animated pistol", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/animated-pistol-bd896167e7ca44f19597d3afe6a8d83f"),
				("sniper animated", "DJMaesen", CcBy, "https://sketchfab.com/3d-models/sniper-animated-b48999a250b2433da59f705c371a49b2"),
				("Animated FPS hands (rifle animation pack)", "Cransh", CcBy, "https://sketchfab.com/3d-models/animated-fps-hands-rifle-animation-pack-5f2d0ed780a94724b36ab505f7564057"),
				("FPS Red Dot Sight (Game Model)", "Michael Makivic", CcBy, "https://sketchfab.com/3d-models/fps-red-dot-sight-game-model-0cf999d9363940c2b149dcd80c448554"),
				("Accessory Kit (A5, Legacy K1, X50, Muzzles & Charms)", "CuiGames (@GodTrigger0)", "CuiGames", "https://cuigames.itch.io"),
			}),
			(/*T*/"Grenades, munitions et objets", new[]
			{
				("Arc Star", "Mac", CcBy, "https://sketchfab.com/3d-models/arc-star-207b9937c81f4de1ab383f77ff152bac"),
				("Basic Explosive", "Blender3D", CcBy, "https://sketchfab.com/3d-models/basic-explosive-e2ef33fefa4241b59c2aaf45c75a20ee"),
				("Flashbang grenade", "Nikolay Kudrin", CcBy, "https://sketchfab.com/3d-models/flashbang-grenade-2799b0b859c74db6ae8752fdc6bb4c04"),
				("Frag Grenade", "Slayerazazel", CcBy, "https://sketchfab.com/3d-models/frag-grenade-729090db0b4243499f9b7499824e8deb"),
				("Incendiary Grenade", "kidsing", CcByNc, "https://sketchfab.com/3d-models/incendiary-grenade-7b8160d5c0c14b478029230d8a88bc33"),
				("Sniper Ammo Box (Game-ready)", "jsandwich96", CcBy, "https://sketchfab.com/3d-models/sniper-ammo-box-game-ready-5338de8c47ea4f68aa40dd51602a2b53"),
				("Military Backpack", "Neslihan Çakmak", CcBy, "https://sketchfab.com/3d-models/military-backpack-06be5c0f15aa4aa3af8ebcc4c83d02a3"),
				("Military Backpack-1", "Neslihan Çakmak", CcBy, "https://sketchfab.com/3d-models/military-backpack-1-d2cdf41d10a4416b9a5b13f24ba32165"),
				("V2701-JumpPad [2018 Spring #ue4jam]", "Zaxel", CcBy, "https://sketchfab.com/3d-models/v2701-jumppad-2018-spring-ue4jam-1ccc8fed930e45fd88b22532bad32ee2"),
			}),
			(/*T*/"Personnages et skins", new[]
			{
				("MakeHuman", "MakeHuman Community", Cc0, "https://www.makehumancommunity.org"),
				("Hair (cortu, culturalibre, elvs, toigo)", "MakeHuman Community", Cc0, "https://www.makehumancommunity.org"),
				("Animations", "Mixamo (Adobe)", "Mixamo", "https://www.mixamo.com"),
			}),
			(/*T*/"Décors et textures", new[]
			{
				("Modular SciFi MegaKit", "Quaternius", Cc0, "https://quaternius.com"),
				("Pine / Fir Forest, Namaqualand", "Poly Haven", Cc0, "https://polyhaven.com"),
				("Textures (Ground037, Rock030, MetalPlates, Concrete042A, Metal052C…)", "ambientCG", Cc0, "https://ambientcg.com"),
				("Pixel Skies (Demo)", "Digital Moons", "Digital Moons", "https://digitalmoons.itch.io/pixel-skies"),
			}),
			(/*T*/"Polices", new[]
			{
				("Architects Daughter", "Kimberly Geswein", "OFL 1.1", "https://fonts.google.com/specimen/Architects+Daughter"),
				("GROBOLD", "", "", ""),
			}),
			(/*T*/"Moteur et extensions", new[]
			{
				("Godot Engine", "Juan Linietsky, Ariel Manzur & contributors", Mit, "https://godotengine.org"),
				("Terrain3D", "Cory Petkovsek, Roope Palmroos & contributors", Mit, "https://github.com/TokisanGames/Terrain3D"),
				("Character Controller", "Expresso Bits, Whimfoome", "", ""),
				("FPS Hands", "Bytez", "", ""),
				("Raymarched Mountains", "", Cc0, ""),
			}),
		};

		public CreditsScreen() { Layer = 120; }

		public override void _Ready()
		{
			AddChild(MenuStyle.Backdrop());
			var center = new CenterContainer { Theme = MenuStyle.SharedTheme };
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(center);

			var size = GetViewport().GetVisibleRect().Size;
			var column = new VBoxContainer();
			column.AddThemeConstantOverride("separation", 10);
			center.AddChild(MenuStyle.Card(column, Mathf.Min(1000f, size.X - 60f), 20));

			var title = new Label { Text = I18n.T("Crédits"), HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 30);
			column.AddChild(title);

			var text = new RichTextLabel
			{
				BbcodeEnabled = true, FitContent = false, ScrollActive = true, SelectionEnabled = true,
				CustomMinimumSize = new Vector2(0, Mathf.Max(240f, size.Y - 220f)), SizeFlagsVertical = Control.SizeFlags.ExpandFill
			};
			text.MetaClicked += meta => OS.ShellOpen(meta.AsString());
			text.Text = Build();
			column.AddChild(text);

			var close = new Button { Text = I18n.T("Retour"), CustomMinimumSize = new Vector2(200, 42), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
			close.Pressed += Close;
			column.AddChild(close);
			close.GrabFocus();
		}

		private static string Esc(string s) => s.Replace("[", "[lb]");

		private static string Build()
		{
			var sb = new System.Text.StringBuilder();
			sb.Append("[center]Open Esport — ").Append(I18n.T("merci à tous les auteurs dont le travail est utilisé dans le jeu.")).Append("[/center]\n");
			foreach (var (section, rows) in Sections)
			{
				sb.Append("\n[font_size=22][b]").Append(Esc(I18n.T(section))).Append("[/b][/font_size]\n");
				foreach (var (work, author, license, url) in rows)
				{
					sb.Append("• [b]").Append(Esc(work)).Append("[/b]");
					if (author.Length > 0) sb.Append(" — ").Append(Esc(author));
					if (license.Length > 0) sb.Append("  [color=#9fa8b8](").Append(Esc(license)).Append(")[/color]");
					if (url.Length > 0) sb.Append("\n   [color=#7fb4ff][url=").Append(url).Append(']').Append(Esc(url)).Append("[/url][/color]");
					sb.Append('\n');
				}
			}
			sb.Append("\n[color=#9fa8b8]CC BY 4.0 : https://creativecommons.org/licenses/by/4.0/ — CC0 1.0 : https://creativecommons.org/publicdomain/zero/1.0/[/color]\n");
			return sb.ToString();
		}

		public override void _Input(InputEvent e)
		{
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); }
		}

		private void Close()
		{
			Closed?.Invoke();
			QueueFree();
		}
	}
}
