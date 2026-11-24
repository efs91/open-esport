using Openesport.Utils;
using System;
using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>Interface du 1 VS 1 : score des manches, équipement de la manche, annonces, et écran de choix de l'équipement (règle « choisi au début »).</summary>
	public partial class ModeHud
	{
		private ModeState _duelPrev;
		private bool _duelPrevKnown;

		private static string Me => $"[color=#{Hex(Blue)}]{I18n.T("Toi")}[/color]";
		private static string Them => $"[color=#{Hex(Red)}]{I18n.T("Adversaire")}[/color]";

		private static string LoadoutText(DuelLoadout l) => l.Known
			? $"{I18n.T(l.WeaponChoice.Label)} ({I18n.T(l.OpticChoice.Label)}) + {I18n.T(l.Weapon2Choice.Label)} ({I18n.T(l.Optic2Choice.Label)}) · {I18n.T(l.GrenadeChoice.Label)} · {I18n.T(l.HealChoice.Label)}"
			: I18n.T("en cours de choix…");

		/// <summary>Lignes du haut : manche et score (moi à gauche, l'adversaire à droite), puis l'état de la manche.</summary>
		private (string l1, string l2) DuelLines(ModeState m, string timer)
		{
			int mine = m.MySide == 0 ? m.Score0 : m.Score1, theirs = m.MySide == 0 ? m.Score1 : m.Score0;
			string l1 = (m.Round > 0 ? I18n.T("Manche {0}", m.Round) + "   ·   " : "") + $"{Me} [b]{mine}[/b]  —  [b]{theirs}[/b] {Them}   " + I18n.T("(premier à {0})", m.Target);
			string l2 = (DuelStage)m.Stage switch
			{
				DuelStage.Pick => I18n.T("[color=#ffd23f]Choix de l'équipement[/color]   ·   {0}", timer),
				DuelStage.Prepare => I18n.T("[color=#ffd23f]Prépare-toi…[/color]   {0}", timer),
				DuelStage.Round => timer,
				_ => m.RoundWinner == 255 ? I18n.T("Manche nulle : elle est rejouée") : m.RoundWinner == m.MySide ? I18n.T("[color=#6fe08a]Manche gagnée[/color]") : I18n.T("[color=#ff6152]Manche perdue[/color]"),
			};
			return (l1, l2);
		}

		/// <summary>Bas de l'écran : mon équipement, et celui de l'adversaire quand chacun a choisi le sien.</summary>
		private static string DuelFooter(ModeState m)
		{
			string text = I18n.T("Équipement : {0}", LoadoutText(m.Loadout)) + "   ·   " + I18n.T("munitions infinies");
			if (m.DuelPolicy == DuelPolicy.Choice && m.OpponentLoadout.Known && !m.OpponentLoadout.Equals(m.Loadout)) text += "\n" + I18n.T("Adversaire : {0}", LoadoutText(m.OpponentLoadout));
			return text;
		}

		/// <summary>Menu de choix ouvert tout seul pendant l'étape de choix (tant que je n'ai pas validé), et annonces des manches.</summary>
		private void UpdateDuel(ModeState m, MatchPhase phase)
		{
			bool picking = phase == MatchPhase.Playing && m.DuelPolicy == DuelPolicy.Choice && (DuelStage)m.Stage == DuelStage.Pick && !m.Picked;
			if (picking && !(MenuOpen && _menuKind == "duel")) ToggleMenu("duel");
			else if (!picking && MenuOpen && _menuKind == "duel") CloseMenu();
			else if (picking && (m.Loadout.Pack() != _duelPrev.Loadout.Pack() || m.TimeLeft != _duelPrev.TimeLeft)) Rebuild();

			var p = _duelPrev;
			bool known = _duelPrevKnown;
			_duelPrev = m; _duelPrevKnown = phase == MatchPhase.Playing;
			if (!known || phase != MatchPhase.Playing) return;
			var stage = (DuelStage)m.Stage;
			if (stage == DuelStage.Prepare && (m.Round != p.Round || (DuelStage)p.Stage != DuelStage.Prepare))
				Announce(I18n.T("[b]MANCHE {0}[/b]", m.Round) + "\n" + LoadoutText(m.Loadout), Gold, 3f, true);
			else if (stage == DuelStage.Round && (DuelStage)p.Stage == DuelStage.Prepare)
				Announce(I18n.T("[b]COMBATTEZ ![/b]"), Green, 1.5f, true);
			else if (stage == DuelStage.RoundEnd && (DuelStage)p.Stage != DuelStage.RoundEnd)
				Announce(m.RoundWinner == 255 ? I18n.T("[b]MANCHE NULLE[/b]") : m.RoundWinner == m.MySide ? I18n.T("[b]MANCHE GAGNÉE[/b]") : I18n.T("[b]MANCHE PERDUE[/b]"),
					m.RoundWinner == 255 ? Gold : m.RoundWinner == m.MySide ? Green : Red, 2.5f, true);
		}

		/// <summary>Écran de choix : une ligne par catégorie (arme, viseur, grenade, soin), l'option retenue cochée, puis « Valider ».</summary>
		private void BuildDuelPick()
		{
			var l = _last.Loadout;
			var title = new Label { Text = I18n.T("Choisis ton équipement"), HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 26);
			_menuBox.AddChild(title);
			var info = new Label { Text = I18n.T("Tu le gardes pour toute la partie. Sans validation à temps, un équipement est tiré au hasard ({0} s).", _last.TimeLeft), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
			info.AddThemeColorOverride("font_color", new Color(0.7f, 0.73f, 0.78f));
			_menuBox.AddChild(info);
			Row(I18n.T("ARME 1"), DuelRules.Weapons, l.Weapon, 0);
			Row(I18n.T("VISEUR DE L'ARME 1"), DuelRules.Optics, l.Optic, 1);
			Row(I18n.T("ARME 2"), DuelRules.Weapons, l.Weapon2, 4);
			Row(I18n.T("VISEUR DE L'ARME 2"), DuelRules.Optics, l.Optic2, 5);
			Row(I18n.T("GRENADE"), DuelRules.Grenades, l.Grenade, 2);
			Row(I18n.T("SOIN"), DuelRules.Heals, l.Heal, 3);
			var ok = new Button { Text = I18n.T("Valider"), CustomMinimumSize = new Vector2(0, 48) };
			ok.AddThemeFontSizeOverride("font_size", 20);
			ok.Pressed += () => { Send?.Invoke(CommandType.DuelPick, DuelRules.ConfirmPick); _last.Picked = true; CloseMenu(); };
			_menuBox.AddChild(ok);
		}

		private void Row(string name, DuelChoice[] list, int selected, int category)
		{
			var label = new Label { Text = name };
			label.AddThemeFontSizeOverride("font_size", 15);
			label.AddThemeColorOverride("font_color", new Color(0.7f, 0.73f, 0.78f));
			_menuBox.AddChild(label);
			var flow = new HFlowContainer();
			flow.AddThemeConstantOverride("h_separation", 6);
			flow.AddThemeConstantOverride("v_separation", 6);
			for (int i = 0; i < list.Length; i++)
			{
				int index = i;
				var b = new Button { Text = (i == selected ? "✔  " : "") + I18n.T(list[i].Label), ToggleMode = true, ButtonPressed = i == selected, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(140, 34) };
				b.Pressed += () => { Send?.Invoke(CommandType.DuelPick, DuelRules.PickArg(category, index)); };
				flow.AddChild(b);
			}
			_menuBox.AddChild(flow);
		}
	}
}
