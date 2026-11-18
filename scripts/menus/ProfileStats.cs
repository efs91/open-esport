using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Openesport.Online;

namespace Openesport.Menus
{
	/// <summary>
	/// Colonne de statistiques de la scène « Mon profil » : le classement général (score, rang, tous les modes d'un coup, meilleurs joueurs) puis un onglet par mode de jeu
	/// avec les statistiques du joueur dans ce mode et le classement de ce mode. Les données viennent du service de comptes.
	/// </summary>
	public partial class ProfileStats : VBoxContainer
	{
		public Action OnLogout;

		private Label _who;
		private TabContainer _tabs;
		private RichTextLabel _general;
		private readonly Dictionary<string, RichTextLabel> _modes = new();

		private static RichTextLabel NewText(string title)
		{
			var t = new RichTextLabel { Name = title, BbcodeEnabled = true, FitContent = false, ScrollActive = true, SelectionEnabled = true };
			t.AddThemeFontSizeOverride("normal_font_size", 17);
			t.AddThemeFontSizeOverride("bold_font_size", 17);
			return t;
		}

		public override void _Ready()
		{
			CustomMinimumSize = new Vector2(500, 0);
			AddThemeConstantOverride("separation", 8);

			var head = new HBoxContainer();
			_who = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_who.AddThemeFontSizeOverride("font_size", 26);
			var logout = new Button { Text = "Se déconnecter" };
			logout.Pressed += () => OnLogout?.Invoke();
			head.AddChild(_who); head.AddChild(logout);
			AddChild(head);

			_tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			AddChild(_tabs);
			_general = NewText("Général");
			_tabs.AddChild(_general);
			foreach (var m in OpenEsport.Core.Modes.All)
			{
				var t = NewText(Short(m.Label));
				_modes[m.Key] = t;
				_tabs.AddChild(t);
			}
			Load();
		}

		/// <summary>Libellé court d'un onglet.</summary>
		private static string Short(string label) => label switch { "Battle Royale" => "Battle R.", "Match à mort" => "Mort sub.", _ => label.Length > 12 ? label.Substring(0, 12) : label };

		private void SetAll(string bbcode)
		{
			_general.Text = bbcode;
			foreach (var t in _modes.Values) t.Text = bbcode;
		}

		private async void Load()
		{
			_who.Text = AccountClient.Login;
			SetAll("[color=#9aa4b2]Chargement des statistiques…[/color]");
			string error = await AccountClient.RefreshAsync();
			if (!IsInstanceValid(this)) return;
			if (error != null) { SetAll("[color=#ff7766]" + error + "[/color]"); return; }
			var p = AccountClient.Profile;
			_who.Text = p.Login;
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

		private static void Board(StringBuilder sb, List<LeaderRow> rows, string login)
		{
			sb.AppendLine();
			sb.AppendLine("[b]Meilleurs joueurs[/b]");
			if (rows == null) { sb.AppendLine("[color=#9aa4b2]Classement indisponible.[/color]"); return; }
			if (rows.Count == 0) { sb.AppendLine("[color=#9aa4b2]Personne n'est encore classé.[/color]"); return; }
			sb.AppendLine("[table=4]");
			sb.AppendLine("[cell][color=#9aa4b2]Rang[/color]   [/cell][cell][color=#9aa4b2]Joueur[/color]   [/cell][cell][color=#9aa4b2]Score[/color]   [/cell][cell][color=#9aa4b2]Parties[/color][/cell]");
			foreach (var r in rows)
			{
				string c = r.Login == login ? "[color=#ffd24a]" : "[color=#ffffff]";
				sb.AppendLine($"[cell]{c}#{r.Rank}[/color]   [/cell][cell]{c}{Escape(r.Login)}[/color]   [/cell][cell]{c}{r.Rating}[/color]   [/cell][cell]{c}{r.Matches}[/color][/cell]");
			}
			sb.AppendLine("[/table]");
		}

		private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

		private void FillGeneral(AccountProfile p, List<LeaderRow> top)
		{
			var sb = new StringBuilder();
			sb.AppendLine("[font_size=24][b]Classement général[/b][/font_size]");
			sb.AppendLine($"Score : [b]{p.General.Rating}[/b] pts     Rang : [b]{(p.General.Rank > 0 ? "#" + p.General.Rank : "non classé")}[/b]");
			sb.AppendLine(p.General.Rank > 0 ? $"{p.General.Matches} parties jouées" : $"{p.General.Matches} partie(s) jouée(s) — il en faut {3} pour être classé");
			sb.AppendLine("[color=#9aa4b2]Le score général est la moyenne des scores de chaque mode, pondérée par le nombre de parties jouées dans chacun. On part de 1000.[/color]");
			sb.AppendLine();
			sb.AppendLine("[b]Chaque mode[/b]");
			sb.AppendLine("[table=6]");
			sb.AppendLine("[cell][color=#9aa4b2]Mode[/color]   [/cell][cell][color=#9aa4b2]Parties[/color]   [/cell][cell][color=#9aa4b2]Victoires[/color]   [/cell][cell][color=#9aa4b2]K/D[/color]   [/cell][cell][color=#9aa4b2]Score[/color]   [/cell][cell][color=#9aa4b2]Rang[/color][/cell]");
			foreach (var m in p.Modes)
				sb.AppendLine($"[cell]{m.Label}   [/cell][cell]{m.Matches}   [/cell][cell]{Percent(m.Wins, m.Matches)}   [/cell][cell]{Ratio(m.Kills, m.Deaths)}   [/cell][cell]{m.Rating}   [/cell][cell]{RankText(m.Rank)}[/cell]");
			sb.AppendLine("[/table]");
			Board(sb, top, p.Login);
			_general.Text = sb.ToString();
		}

		private void FillMode(ModeStat m, List<LeaderRow> rows)
		{
			if (!_modes.TryGetValue(m.Mode, out var label)) return;
			var sb = new StringBuilder();
			sb.AppendLine($"[font_size=24][b]{m.Label}[/b][/font_size]");
			sb.AppendLine($"Score : [b]{m.Rating}[/b] pts     Rang : [b]{(m.Rank > 0 ? "#" + m.Rank : "non classé")}[/b]");
			if (m.Matches == 0) sb.AppendLine("[color=#9aa4b2]Tu n'as pas encore joué à ce mode.[/color]");
			sb.AppendLine();
			sb.AppendLine("[table=2]");
			void Row(string name, string value) => sb.AppendLine($"[cell][color=#9aa4b2]{name}[/color]      [/cell][cell][b]{value}[/b][/cell]");
			Row("Parties jouées", m.Matches.ToString());
			Row("Victoires", $"{m.Wins}  ({Percent(m.Wins, m.Matches)})");
			Row("Défaites", (m.Matches - m.Wins).ToString());
			Row("Éliminations", m.Kills.ToString());
			Row("Morts", m.Deaths.ToString());
			Row("Ratio éliminations / morts", Ratio(m.Kills, m.Deaths));
			Row("Dégâts infligés", m.Damage.ToString("N0"));
			if (m.Matches > 0) Row("Dégâts par partie", (m.Damage / m.Matches).ToString("N0"));
			if (!m.Teams) Row("Meilleure place", m.BestPlacement > 0 ? "#" + m.BestPlacement : "—");
			sb.AppendLine("[/table]");
			Board(sb, rows, AccountClient.Login);
			label.Text = sb.ToString();
		}
	}
}
