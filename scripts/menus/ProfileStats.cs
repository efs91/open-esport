using Openesport.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using Openesport.Online;

namespace Openesport.Menus
{
	/// <summary>
	/// Colonne de statistiques de la scène « Mon profil » : un sélecteur (Général, puis un bouton par mode de jeu) et, dessous, le détail.
	/// Général : score, rang, tous les modes d'un coup et les meilleurs joueurs. Un mode : les statistiques du joueur dans ce mode et le classement de ce mode.
	/// Les données viennent du service de comptes.
	/// </summary>
	public partial class ProfileStats : VBoxContainer
	{
		private const string General = "general";

		private HFlowContainer _selector;
		private RichTextLabel _body;
		private readonly Dictionary<string, string> _texts = new();
		private readonly Dictionary<string, Button> _buttons = new();
		private string _current = General;

		public override void _Ready()
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			AddThemeConstantOverride("separation", 10);

			var title = new Label { Text = I18n.T("Statistiques et classements") };
			title.AddThemeFontSizeOverride("font_size", 22);
			AddChild(title);

			_selector = new HFlowContainer();
			_selector.AddThemeConstantOverride("h_separation", 6);
			_selector.AddThemeConstantOverride("v_separation", 6);
			AddChild(_selector);
			var group = new ButtonGroup();
			AddSelector(General, I18n.T("Général"), group);
			foreach (var m in OpenEsport.Core.Modes.All) AddSelector(m.Key, I18n.T(m.Label), group);

			_body = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ScrollActive = true, SelectionEnabled = true };
			_body.AddThemeFontSizeOverride("normal_font_size", 17);
			_body.AddThemeFontSizeOverride("bold_font_size", 17);
			AddChild(_body);

			_buttons[General].ButtonPressed = true;
			Load();
		}

		private void AddSelector(string key, string label, ButtonGroup group)
		{
			var b = new Button { Text = label, ToggleMode = true, ButtonGroup = group, CustomMinimumSize = new Vector2(0, 34) };
			b.AddThemeFontSizeOverride("font_size", 16);
			b.Pressed += () => Select(key);
			_selector.AddChild(b);
			_buttons[key] = b;
		}

		private void Select(string key)
		{
			_current = key;
			_body.Text = _texts.TryGetValue(key, out var t) ? t : "";
			_body.ScrollToLine(0);
		}

		private void Set(string key, string bbcode)
		{
			_texts[key] = bbcode;
			if (key == _current) _body.Text = bbcode;
		}

		private void SetAll(string bbcode)
		{
			foreach (var k in _buttons.Keys) Set(k, bbcode);
		}

		private async void Load()
		{
			SetAll("[color=#9aa4b2]" + I18n.T("Chargement des statistiques…") + "[/color]");
			string error = await AccountClient.RefreshAsync();
			if (!IsInstanceValid(this)) return;
			if (error != null) { SetAll("[color=#ff7766]" + I18n.T(error) + "[/color]"); return; }
			var p = AccountClient.Profile;
			FillGeneral(p, null);
			foreach (var m in p.Modes) FillMode(m, null);

			// les classements des autres joueurs arrivent ensuite, sans bloquer l'affichage de ses propres statistiques
			var top = await AccountClient.LeaderboardAsync("all");
			if (!IsInstanceValid(this)) return;
			FillGeneral(p, top);
			foreach (var m in p.Modes)
			{
				var rows = await AccountClient.LeaderboardAsync(m.Mode);
				if (!IsInstanceValid(this)) return;
				FillMode(m, rows);
			}
		}

		private static string Ratio(int kills, int deaths) => deaths == 0 ? kills.ToString("0.00") : ((double)kills / deaths).ToString("0.00");
		private static string Percent(int wins, int matches) => matches == 0 ? "—" : (100.0 * wins / matches).ToString("0") + " %";
		private static string RankText(int rank) => rank > 0 ? "#" + rank : "—";
		private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

		private static void Board(StringBuilder sb, List<LeaderRow> rows, string login)
		{
			sb.AppendLine();
			sb.AppendLine("[b]" + I18n.T("Meilleurs joueurs") + "[/b]");
			if (rows == null) { sb.AppendLine("[color=#9aa4b2]" + I18n.T("Classement indisponible.") + "[/color]"); return; }
			if (rows.Count == 0) { sb.AppendLine("[color=#9aa4b2]" + I18n.T("Personne n'est encore classé.") + "[/color]"); return; }
			sb.AppendLine("[table=4]");
			sb.AppendLine($"[cell][color=#9aa4b2]{I18n.T("Rang")}[/color]   [/cell][cell][color=#9aa4b2]{I18n.T("Joueur")}[/color]   [/cell][cell][color=#9aa4b2]{I18n.T("Score")}[/color]   [/cell][cell][color=#9aa4b2]{I18n.T("Parties")}[/color][/cell]");
			foreach (var r in rows)
			{
				string c = r.Login == login ? "[color=#ffd24a]" : "[color=#ffffff]";
				sb.AppendLine($"[cell]{c}#{r.Rank}[/color]   [/cell][cell]{c}{Escape(r.Login)}[/color]   [/cell][cell]{c}{r.Rating}[/color]   [/cell][cell]{c}{r.Matches}[/color][/cell]");
			}
			sb.AppendLine("[/table]");
		}

		private void FillGeneral(AccountProfile p, List<LeaderRow> top)
		{
			var sb = new StringBuilder();
			LastMatch(sb, p.LastMatch);
			sb.AppendLine("[font_size=26][b]" + I18n.T("Classement général") + "[/b][/font_size]");
			sb.AppendLine(p.General.Matches == 0
				? "[color=#9aa4b2]" + I18n.T("Non classé : joue une partie pour obtenir un score.") + "[/color]"
				: I18n.T("Score : [b]{0}[/b] pts     Rang : [b]{1}[/b]", p.General.Rating, p.General.Rank > 0 ? "#" + p.General.Rank : I18n.T("non classé")));
			sb.AppendLine(p.General.Rank > 0 ? I18n.T("{0} partie(s) jouée(s)", p.General.Matches) : I18n.T("{0} partie(s) jouée(s) — il en faut 3 pour être classé", p.General.Matches));
			sb.AppendLine("[color=#9aa4b2]" + I18n.T("Moyenne des scores de chaque mode, pondérée par le nombre de parties jouées dans chacun. On part de 1000.") + "[/color]");
			sb.AppendLine();
			sb.AppendLine("[b]" + I18n.T("Chaque mode") + "[/b]");
			foreach (var m in p.Modes)
			{
				sb.AppendLine(m.Matches == 0
					? $"[b]{I18n.T(m.Label)}[/b]   [color=#9aa4b2]{I18n.T("non classé")}[/color]"
					: $"[b]{I18n.T(m.Label)}[/b]   [color=#ffd24a]{I18n.T("{0} pts", m.Rating)}[/color]   {I18n.T("rang {0}", RankText(m.Rank))}");
				sb.AppendLine("[color=#9aa4b2]" + I18n.T("{0} partie(s)  ·  victoires {1}  ·  K/D {2}", m.Matches, Percent(m.Wins, m.Matches), Ratio(m.Kills, m.Deaths)) + "[/color]");
			}
			Board(sb, top, p.Login);
			Set(General, sb.ToString());
		}

		/// <summary>La dernière partie : mode, résultat, durée, éliminations, morts, dégâts et points gagnés ou perdus.</summary>
		private static void LastMatch(StringBuilder sb, LastMatchStat m)
		{
			sb.AppendLine("[font_size=26][b]" + I18n.T("Dernière partie") + "[/b][/font_size]");
			if (m == null) { sb.AppendLine("[color=#9aa4b2]" + I18n.T("Aucune partie enregistrée pour le moment.") + "[/color]"); sb.AppendLine(); return; }
			sb.AppendLine($"[b]{Escape(m.ModeText)}[/b]   [color=#9aa4b2]{m.Ago}[/color]");
			sb.AppendLine("[table=2]");
			void Row(string name, string value) => sb.AppendLine($"[cell][color=#9aa4b2]{name}[/color]      [/cell][cell][b]{value}[/b][/cell]");
			Row(I18n.T("Résultat"), m.Result);
			Row(I18n.T("Temps de jeu"), m.Duration);
			Row(I18n.T("Éliminations"), m.Kills.ToString());
			Row(I18n.T("Morts"), m.Deaths.ToString());
			Row(I18n.T("Dégâts infligés"), m.Damage.ToString("N0"));
			Row(I18n.T("Score du mode"), $"{m.Rating} pts  ({m.DeltaText})");
			sb.AppendLine("[/table]");
			sb.AppendLine();
		}

		private void FillMode(ModeStat m, List<LeaderRow> rows)
		{
			var sb = new StringBuilder();
			sb.AppendLine($"[font_size=26][b]{I18n.T(m.Label)}[/b][/font_size]");
			sb.AppendLine(m.Matches == 0
				? "[color=#9aa4b2]" + I18n.T("Non classé : joue une partie pour obtenir un score.") + "[/color]"
				: I18n.T("Score : [b]{0}[/b] pts     Rang : [b]{1}[/b]", m.Rating, m.Rank > 0 ? "#" + m.Rank : I18n.T("non classé")));
			if (m.Matches == 0) sb.AppendLine("[color=#9aa4b2]" + I18n.T("Tu n'as pas encore joué à ce mode.") + "[/color]");
			sb.AppendLine();
			sb.AppendLine("[table=2]");
			void Row(string name, string value) => sb.AppendLine($"[cell][color=#9aa4b2]{name}[/color]      [/cell][cell][b]{value}[/b][/cell]");
			Row(I18n.T("Parties jouées"), m.Matches.ToString());
			Row(I18n.T("Victoires"), $"{m.Wins}  ({Percent(m.Wins, m.Matches)})");
			Row(I18n.T("Défaites"), (m.Matches - m.Wins).ToString());
			Row(I18n.T("Éliminations"), m.Kills.ToString());
			Row(I18n.T("Morts"), m.Deaths.ToString());
			Row(I18n.T("Ratio éliminations / morts"), Ratio(m.Kills, m.Deaths));
			Row(I18n.T("Dégâts infligés"), m.Damage.ToString("N0"));
			if (m.Matches > 0) Row(I18n.T("Dégâts par partie"), (m.Damage / m.Matches).ToString("N0"));
			if (!m.Teams) Row(I18n.T("Meilleure place"), m.BestPlacement > 0 ? "#" + m.BestPlacement : "—");
			sb.AppendLine("[/table]");
			Board(sb, rows, AccountClient.Login);
			Set(m.Mode, sb.ToString());
		}
	}
}
