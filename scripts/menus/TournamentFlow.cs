using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenEsport.Core;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Le déroulé d'un tournoi, de haut en bas : une carte par étape, reliées par des flèches qui disent combien d'inscrits passent.
	/// Sert à la page de création (étapes à composer) et à la page d'un tournoi (étapes jouées, en cours, à venir, avec leurs résultats).
	/// </summary>
	public static class TournamentFlow
	{
		public const int MaxBr = 30, MaxDm = 10;
		public static readonly string[] TwoTeamModes = { "tdm", "sab", "ctl", "ctf" };
		public static readonly int[] Placement = { 12, 9, 7, 5, 4, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1 }, VictoryHeavy = { 65, 54, 48, 44, 40, 36, 33, 30, 27, 24, 20, 16, 12, 8, 4 }, Arena = { 10, 8, 6, 5, 4, 3, 2, 1 };

		public static bool IsLobby(PhaseConfigDto p) => p.Kind == "lobby";
		public static bool IsSplit(PhaseConfigDto p) => p.Kind == "split";
		public static bool IsMatches(PhaseConfigDto p) => p.Kind == "pools" || p.Kind == "table" || p.Kind == "divisions";
		/// <summary>Mode d'une étape (anciens tournois : lobby = battle royale, poules et tableau = match à mort).</summary>
		public static string ModeOf(PhaseConfigDto p) => IsSplit(p) ? "" : string.IsNullOrEmpty(p.Mode) ? (IsLobby(p) ? "br" : "tdm") : p.Mode;
		public static bool TwoTeams(string mode) => TwoTeamModes.Contains(mode);
		/// <summary>Score habituel d'une partie, et ses bornes.</summary>
		public static (int min, int max, int usual) Score(string mode) => mode switch { "dm" => (5, 50, 20), "tdm" => (3, 100, 50), "ctf" => (1, 10, 3), "sab" => (1, 16, 13), "ctl" => (20, 300, 100), _ => (1, 10, 5) };

		public static string ModeLabel(string mode) => !string.IsNullOrEmpty(mode) && Modes.TryParse(mode, out var m) ? GameText.Mode(m) : "";

		public static Color ModeColor(string mode) => mode switch
		{
			"br" => MenuStyle.Cyan, "dm" => MenuStyle.Red, "1v1" => new Color("b88cff"), "tdm" => new Color("ff9f43"), "ctf" => new Color("5dd39e"), "sab" => new Color("ffd166"), "ctl" => new Color("6c9bff"), _ => MenuStyle.Muted,
		};

		public static string KindLabel(PhaseConfigDto p) => p.Kind switch
		{
			"pools" => I18n.T("Poules"), "table" => I18n.T("Tableau (élimination directe)"), "divisions" => I18n.T("Poules de division"), "split" => I18n.T("Séparation en divisions"),
			_ => TwoTeams(ModeOf(p)) ? I18n.T("Série de matchs") : I18n.T("Série de manches"),
		};

		/// <summary>Nom d'une étape : le sien, sinon d'après son déroulement et sa position.</summary>
		public static string Name(TournamentConfigDto cfg, int i)
		{
			var p = cfg.Phases[i];
			if (!string.IsNullOrWhiteSpace(p.Name)) return p.Name;
			if (p.Kind != "lobby") return KindLabel(p);
			if (cfg.Phases.Count == 1) return I18n.T("Manches");
			return i == cfg.Phases.Count - 1 ? I18n.T("Finale") : I18n.T("Phase {0}", i + 1);
		}

		/// <summary>Ce qu'il faut pour gagner une partie, selon le mode.</summary>
		public static string ScoreText(string mode, int score) => mode switch
		{
			"1v1" or "sab" => I18n.T("match en {0} manche(s) gagnée(s)", score),
			"ctf" => I18n.T("match en {0} prise(s)", score),
			"ctl" => I18n.T("match en {0} points", score),
			_ => I18n.T("match en {0} victimes", score),
		};

		/// <summary>Les réglages d'une étape en quelques mots.</summary>
		public static string Summary(TournamentConfigDto cfg, int i)
		{
			var p = cfg.Phases[i];
			string mode = ModeOf(p);
			var parts = new List<string>();
			if (IsSplit(p)) parts.Add(I18n.T("{0} divisions d'après le classement ; chacune joue la suite de son côté", p.Divisions));
			else if (IsLobby(p))
			{
				parts.Add(TwoTeams(mode) ? I18n.T("{0} match(s)", p.Races) : I18n.T("{0} manche(s)", p.Races));
				if (p.Teams == "random" && p.TeamSize > 1) parts.Add(I18n.T("équipes de {0} tirées au sort à chaque manche", p.TeamSize));
				if (!TwoTeams(mode)) parts.Add(I18n.T("lobbys de {0}", p.LobbySize));
				if (mode != "br") parts.Add(ScoreText(mode, p.MaxScore));
				if (p.Distribution == "level") parts.Add(I18n.T("divisions par niveau"));
				parts.Add(I18n.T("points {0}", string.Join("/", p.Scale.Take(TwoTeams(mode) ? 2 : 5)) + (!TwoTeams(mode) && p.Scale.Count > 5 ? "…" : "")));
				if (p.KillPoints > 0) parts.Add(I18n.T("+{0} par élimination", p.KillPoints));
			}
			else
			{
				if (p.Kind == "divisions") parts.Add(I18n.T("{0} divisions", p.Divisions) + (p.Up > 0 || p.Down > 0 ? "  ↑" + p.Up + " ↓" + p.Down : ""));
				else if (p.Kind == "pools") parts.Add(I18n.T("chacun contre chacun"));
				if (p.PoolSize > 0 && p.Kind != "table") parts.Add(I18n.T("poules de {0}", p.PoolSize));
				parts.Add(ScoreText(mode, p.MaxScore));
				if (p.BestOf > 1) parts.Add(I18n.T("en {0} parties gagnantes", p.BestOf / 2 + 1));
				if (mode == "1v1") parts.Add(p.Loadout == "choice" ? I18n.T("équipement choisi") : p.Loadout.StartsWith("fixed:") ? I18n.T("équipement imposé") : I18n.T("équipement au hasard"));
				if (p.Kind == "table" && p.Places != "none") parts.Add(p.Places == "all" ? I18n.T("toutes les places disputées") : I18n.T("match pour la 3e place"));
			}
			return string.Join("  ·  ", parts);
		}

		/// <summary>Le nom d'un modèle du jeu : ses modes, et ce qu'il a de particulier (divisions).</summary>
		public static string Title(TournamentConfigDto cfg)
		{
			string modes = string.Join(" → ", cfg.Phases.Select(ModeOf).Where(m => m != "").Distinct().Select(ModeLabel));
			var special = cfg.Phases.FirstOrDefault(p => p.Kind == "split" || p.Kind == "divisions");
			return special == null ? modes : modes + "  —  " + KindLabel(special);
		}

		/// <summary>Le tournoi en une ligne : « Série de manches → 20 → Finale ».</summary>
		public static string Outline(TournamentConfigDto cfg)
			=> string.Join("  →  ", cfg.Phases.Select((p, i) => (cfg.Phases.Select(ModeOf).Where(m => m != "").Distinct().Count() > 1 && !IsSplit(p) ? ModeLabel(ModeOf(p)) + " : " : "") + KindLabel(p) + (p.Qualified > 0 && i < cfg.Phases.Count - 1 ? $"  →  {p.Qualified}" : "")));

		/// <summary>Combien d'inscrits arrivent à chaque étape, si <paramref name="entries"/> joueurs (ou équipes) sont inscrits ; le dernier nombre est celui des classés de la fin.</summary>
		public static List<int> Entrants(TournamentConfigDto cfg, int entries)
		{
			var list = new List<int> { entries };
			for (int i = 0; i < cfg.Phases.Count; i++)
			{
				var p = cfg.Phases[i];
				int n = list[i];
				if (i < cfg.Phases.Count - 1)
				{
					if (IsSplit(p)) n = Math.Max(2, p.Sizes.Count > 0 ? Math.Min(p.Sizes[0], n) : (n + p.Divisions - 1) / Math.Max(1, p.Divisions));
					else if (p.Qualified > 0) n = Math.Min(n, p.QualifyMode == "lobby" && IsLobby(p) ? p.Qualified * Lobbies(p, n) : p.Qualified);
				}
				list.Add(n);
			}
			return list;
		}

		public static int Lobbies(PhaseConfigDto p, int entrants) => Math.Max(1, (entrants + Math.Max(1, p.LobbySize) - 1) / Math.Max(1, p.LobbySize));

		/// <summary>Ce que l'étape représente pour ce nombre d'inscrits : « 60 joueurs : 3 lobbys, 9 parties ».</summary>
		public static string Volume(TournamentConfigDto cfg, int i, int entrants)
		{
			var p = cfg.Phases[i];
			string who = cfg.TeamSize > 1 ? I18n.T("{0} équipes", entrants) : I18n.T("{0} joueurs", entrants);
			if (IsSplit(p)) return who;
			int games;
			if (IsLobby(p))
			{
				int lobbies = Lobbies(p, entrants);
				return who + "  ·  " + I18n.T("{0} lobby(s), {1} partie(s)", lobbies, lobbies * p.Races);
			}
			if (p.Kind == "table") games = Math.Max(1, entrants - 1) + (p.Places == "third" && entrants >= 4 ? 1 : 0);
			else
			{
				int groups = p.Kind == "divisions" ? Math.Clamp(p.Divisions, 1, Math.Max(1, entrants / 2)) : 1;
				int size = p.PoolSize > 0 ? p.PoolSize : p.Kind == "divisions" ? Math.Max(2, entrants / groups) : 6;
				int pools = Math.Max(1, (entrants + size - 1) / size), low = entrants / pools, more = entrants % pools;
				games = more * ((low + 1) * low / 2) + (pools - more) * (low * (low - 1) / 2);
			}
			return who + "  ·  " + I18n.T("environ {0} match(s)", games);
		}

		/// <summary>Ce qui passe d'une étape à la suivante.</summary>
		public static string Passing(TournamentConfigDto cfg, int i)
		{
			var p = cfg.Phases[i];
			if (i >= cfg.Phases.Count - 1) return I18n.T("Classement final");
			if (IsSplit(p)) return I18n.T("chaque division continue");
			if (p.Qualified <= 0) return I18n.T("tout le monde passe");
			return p.QualifyMode == "lobby" && IsLobby(p) ? I18n.T("{0} qualifiés par {1}", p.Qualified, I18n.T("lobby")) : I18n.T("{0} qualifiés", p.Qualified);
		}

		// ------------------------------------------------------------------ éléments d'affichage

		public static Label Line(string text, int size = 15, Color? color = null, bool wrap = true)
		{
			var l = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			l.AddThemeFontSizeOverride("font_size", size);
			if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
			return l;
		}

		private static StyleBoxFlat Box(Color fill, Color border, Color edge, int edgeWidth, int margin)
		{
			var s = new StyleBoxFlat { BgColor = fill, BorderColor = border };
			s.SetBorderWidthAll(1);
			s.SetCornerRadiusAll(6);
			s.SetContentMarginAll(margin);
			if (edgeWidth > 0) { s.BorderColor = edge; s.BorderWidthTop = s.BorderWidthRight = s.BorderWidthBottom = 0; s.BorderWidthLeft = edgeWidth; s.ContentMarginLeft = margin + edgeWidth; }
			return s;
		}

		/// <summary>Carte d'une étape : bord de la couleur du mode, encadrée si elle est choisie. Un clic n'importe où l'ouvre.</summary>
		public static PanelContainer Card(Control content, Color edge, bool selected, Action pressed)
		{
			var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = pressed != null ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow };
			var normal = Box(selected ? new Color("1d3450") : new Color("142237"), MenuStyle.CardBorder, selected ? MenuStyle.Cyan : edge, 4, 10);
			card.AddThemeStyleboxOverride("panel", normal);
			if (pressed != null)
			{
				var hover = (StyleBoxFlat)normal.Duplicate();
				hover.BgColor = normal.BgColor.Lightened(0.07f);
				card.MouseEntered += () => card.AddThemeStyleboxOverride("panel", hover);
				card.MouseExited += () => card.AddThemeStyleboxOverride("panel", normal);
				card.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { card.AcceptEvent(); pressed(); } };
			}
			Pass(content);
			card.AddChild(content);
			return card;
		}

		/// <summary>Les textes d'une carte laissent passer les clics vers elle (les boutons les gardent).</summary>
		private static void Pass(Control c)
		{
			if (c is not BaseButton && c.MouseFilter == Control.MouseFilterEnum.Stop) c.MouseFilter = Control.MouseFilterEnum.Pass;
			foreach (var child in c.GetChildren()) if (child is Control k) Pass(k);
		}

		/// <summary>Flèche entre deux cartes.</summary>
		public static Control Arrow(string text, bool end = false)
		{
			var l = Line((end ? "🏆  " : "↓  ") + text, 14, end ? new Color("ffd166") : MenuStyle.Muted, wrap: false);
			l.HorizontalAlignment = HorizontalAlignment.Center;
			return l;
		}

		/// <summary>Petit bouton compact ; choisi, il prend la couleur donnée.</summary>
		public static Button Chip(string text, bool on, Color color, Action pressed, int size = 15)
		{
			var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 32) };
			b.AddThemeFontSizeOverride("font_size", size);
			foreach (var (state, fill) in new[] { ("normal", on ? color.Darkened(0.62f) : new Color("152338")), ("hover", on ? color.Darkened(0.5f) : new Color("1f3350")), ("pressed", color.Darkened(0.6f)), ("disabled", new Color("101927")) })
			{
				var s = new StyleBoxFlat { BgColor = fill, BorderColor = on ? color : MenuStyle.CardBorder };
				s.SetBorderWidthAll(1); s.SetCornerRadiusAll(6);
				s.ContentMarginLeft = s.ContentMarginRight = 11; s.ContentMarginTop = s.ContentMarginBottom = 4;
				b.AddThemeStyleboxOverride(state, s);
			}
			if (on) foreach (var state in new[] { "font_color", "font_hover_color", "font_focus_color" }) b.AddThemeColorOverride(state, MenuStyle.Ink);
			if (pressed != null) b.Pressed += pressed;
			return b;
		}

		/// <summary>Le titre d'une carte d'étape : « 2  Sabotage  Poules ».</summary>
		public static HBoxContainer Head(TournamentConfigDto cfg, int i, string state = null)
		{
			var p = cfg.Phases[i];
			string mode = ModeOf(p);
			var head = new HBoxContainer();
			head.AddThemeConstantOverride("separation", 10);
			string name = Name(cfg, i), kind = KindLabel(p);
			var title = Line($"{i + 1}.  {(mode != "" ? ModeLabel(mode) + "  —  " : "")}{name}", 17, null, wrap: false);
			title.ClipText = true;
			head.AddChild(title);
			if (name != kind && mode != "") { var k = Line(kind, 13, MenuStyle.Muted, wrap: false); k.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd; head.AddChild(k); }
			if (state != null)
			{
				var s = Line(state, 13, state == I18n.T("en cours") ? new Color("ffd166") : MenuStyle.Muted, wrap: false);
				s.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
				head.AddChild(s);
			}
			return head;
		}

		// ------------------------------------------------------------------ résultats d'une étape

		/// <summary>Les résultats d'une étape : lobbys (une colonne par manche), poules (classement et matchs) ou tours d'un tableau, côte à côte.</summary>
		public static Control Results(FlowPhaseDto flow, int mine)
		{
			var box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 8);
			var wrap = new HFlowContainer();
			wrap.AddThemeConstantOverride("h_separation", 14);
			wrap.AddThemeConstantOverride("v_separation", 10);
			foreach (var g in flow.Groups)
			{
				var col = new VBoxContainer { CustomMinimumSize = new Vector2(g.Type == "lobby" ? 420 : 285, 0) };
				col.AddThemeConstantOverride("separation", 2);
				col.AddChild(Line(GroupTitle(g), 14, MenuStyle.Cyan));
				if (g.Type == "lobby") Lobby(col, g, mine);
				else
				{
					if (g.Type == "pool") Pool(col, g, mine);
					foreach (var m in g.Matches) col.AddChild(Match(m, mine));
				}
				wrap.AddChild(col);
			}
			box.AddChild(wrap);
			return box;
		}

		private static string GroupTitle(FlowGroupDto g)
		{
			if (g.Type == "lobby") return I18n.T("Lobby") + " " + g.Number;
			if (g.Type == "pool") return g.Division > 0 ? I18n.T("Division {0}", g.Division) : I18n.T("Poule {0}", g.Number);
			if (g.Place > 1) return I18n.T("Match de classement, {0}e place", g.Place);
			return g.Size switch { 2 => I18n.T("Finale"), 4 => I18n.T("Demi-finales"), 8 => I18n.T("Quarts de finale"), 16 => I18n.T("Huitièmes de finale"), _ => I18n.T("Tableau de {0}", g.Size) };
		}

		private static Label Cell(string text, float width, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
		{
			var l = Line(text, 13, color, wrap: false);
			l.CustomMinimumSize = new Vector2(width, 0);
			l.SizeFlagsHorizontal = width > 0 ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ExpandFill;
			l.ClipText = width > 0 || align == HorizontalAlignment.Left;
			l.HorizontalAlignment = align;
			return l;
		}

		private static readonly Color Own = new Color("ffd166");

		private static void Lobby(VBoxContainer col, FlowGroupDto g, int mine)
		{
			foreach (var r in g.Rows.Take(30))
			{
				var row = new HBoxContainer();
				row.AddThemeConstantOverride("separation", 6);
				var color = r.Reference == mine && mine != 0 ? Own : MenuStyle.Ink;
				row.AddChild(Cell(r.Place > 0 ? r.Place + "." : "", 26, MenuStyle.Muted));
				row.AddChild(Cell(r.Name, 0, color));
				// la place de chaque manche (les éliminations en petit), puis le total
				foreach (var k in r.Races.TakeLast(6))
				{
					var c = Cell(k == null ? "·" : k.Place <= 0 ? "–" : "#" + k.Place, 30, k == null ? MenuStyle.Muted : k.Place == 1 ? Own : MenuStyle.Muted, HorizontalAlignment.Right);
					if (k != null && k.Place > 0) c.TooltipText = $"#{k.Place}  ·  {k.Kills} {I18n.T("éliminations")}  ·  " + I18n.T("{0} pts", k.Points);
					c.MouseFilter = Control.MouseFilterEnum.Pass;
					row.AddChild(c);
				}
				row.AddChild(Cell(I18n.T("{0} pts", r.Points), 62, color, HorizontalAlignment.Right));
				col.AddChild(row);
			}
		}

		private static void Pool(VBoxContainer col, FlowGroupDto g, int mine)
		{
			foreach (var r in g.Rows)
			{
				var row = new HBoxContainer();
				var color = r.Reference == mine && mine != 0 ? Own : MenuStyle.Ink;
				row.AddChild(Cell(r.Place > 0 ? r.Place + "." : "", 26, MenuStyle.Muted));
				row.AddChild(Cell(r.Name, 0, color));
				row.AddChild(Cell(r.Points.ToString(), 42, color, HorizontalAlignment.Right));
				row.AddChild(Cell(r.Diff ?? "", 42, MenuStyle.Muted, HorizontalAlignment.Right));
				col.AddChild(row);
			}
			col.AddChild(new HSeparator());
		}

		private static Control Match(FlowMatchDto m, int mine)
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 6);
			bool own = mine != 0 && (m.RefA == mine || m.RefB == mine);
			Color Side(int side) => m.Winner == side ? (own ? Own : MenuStyle.Ink) : m.Winner < 0 && own ? Own : MenuStyle.Muted;
			row.AddChild(Cell(m.A, 0, Side(0)));
			var score = Cell(m.Winner < 0 ? (m.Games.Count > 0 ? string.Join(" ", m.Games) : "–") : $"{m.ScoreA} - {m.ScoreB}", 62, MenuStyle.Ink, HorizontalAlignment.Center);
			if (m.Games.Count > 0) { score.TooltipText = string.Join("   ", m.Games); score.MouseFilter = Control.MouseFilterEnum.Pass; }
			row.AddChild(score);
			var b = Cell(m.B, 0, Side(1), HorizontalAlignment.Right);
			b.ClipText = true;
			row.AddChild(b);
			return row;
		}
	}
}
