using System;
using Godot;
using OpenEsport.Core;
using Openesport.Online;

namespace Openesport.Menus
{
	/// <summary>
	/// Page « Équipement » du menu Multijoueur (modes sans butin au sol) : on choisit son équipement de départ, appliqué à chaque réapparition.
	/// On peut en changer pendant la partie avec la touche C.
	/// </summary>
	public partial class LoadoutScreen : CanvasLayer
	{
		public Action OnContinue;
		public Action OnBack;

		private int _selected;

		public LoadoutScreen() { Layer = 110; }

		private static string WeaponName(byte id) => id switch
		{
			ItemIds.Ak => "AK-74", ItemIds.Smg45 => "SMG-45", ItemIds.Lmg63 => "LMG-63", ItemIds.Sawnoff => "Fusil à canon scié", ItemIds.C19 => "C19", ItemIds.Sniper => "Sniper", _ => ""
		};

		private static string OpticName(byte id) => id switch
		{
			ItemIds.OpticHolo => "viseur holographique", ItemIds.Optic2x => "viseur holographique x2", ItemIds.Optic4x => "lunette 4x", ItemIds.Optic6x => "lunette 6x", ItemIds.OpticRedDot => "viseur point rouge", _ => ""
		};

		public override void _Ready()
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
			_selected = MatchSettings.Loadout;

			var root = new Control();
			root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);
			root.AddChild(new ColorRect { Color = new Color(0.06f, 0.07f, 0.09f), AnchorRight = 1, AnchorBottom = 1 });
			var center = new CenterContainer();
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(center);
			var box = new VBoxContainer { CustomMinimumSize = new Vector2(900, 0) };
			box.AddThemeConstantOverride("separation", 14);
			center.AddChild(box);

			var title = new Label { Text = "Choisis ton équipement", HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 34);
			box.AddChild(title);
			box.AddChild(new Label
			{
				Text = "Mode : " + Modes.Label(MatchSettings.Mode, Modes.ValidTeamSize(MatchSettings.Mode, MatchSettings.TeamSize)) + "   —   tu pourras en changer en partie avec la touche C.",
				HorizontalAlignment = HorizontalAlignment.Center
			});

			var grid = new GridContainer { Columns = 2 };
			grid.AddThemeConstantOverride("h_separation", 12);
			grid.AddThemeConstantOverride("v_separation", 12);
			box.AddChild(grid);
			var group = new ButtonGroup();
			foreach (var kit in Loadouts.Kits)
			{
				var k = kit;
				string optic = OpticName(k.PrimaryOptic);
				string shield = k.ShieldTier < 0 ? "pas de bouclier" : "bouclier " + new[] { "blanc", "bleu", "violet", "rouge" }[Math.Clamp(k.ShieldTier, 0, 3)];
				string gear = $"{WeaponName(k.Primary)}{(optic != "" ? " + " + optic : "")}  ·  {WeaponName(k.Secondary)}  ·  {shield}  ·  {k.HealSmall} soin(s), {k.ShieldSmall} recharge(s) de bouclier";
				var b = new Button
				{
					Text = k.Name + "\n" + k.Description + "\n" + gear,
					ToggleMode = true, ButtonGroup = group, ButtonPressed = k.Id == _selected,
					CustomMinimumSize = new Vector2(440, 150), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
					AutowrapMode = TextServer.AutowrapMode.WordSmart, Alignment = HorizontalAlignment.Left
				};
				b.Pressed += () => _selected = k.Id;
				grid.AddChild(b);
			}

			var go = new Button { Text = "Jouer avec cet équipement", CustomMinimumSize = new Vector2(0, 52) };
			go.AddThemeFontSizeOverride("font_size", 20);
			go.Pressed += () => { MatchSettings.Loadout = _selected; var next = OnContinue; QueueFree(); next?.Invoke(); };
			box.AddChild(go);
			var back = new Button { Text = "Retour", CustomMinimumSize = new Vector2(0, 40) };
			back.Pressed += () => { var cb = OnBack; QueueFree(); cb?.Invoke(); };
			box.AddChild(back);
		}
	}
}
