using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using OpenEsport.Core;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Création et modification d'un tournoi. On choisit d'abord un modèle (du jeu, à soi, partagé par un autre joueur, ou une page blanche), puis la page a deux
	/// parties : à gauche les réglages — l'essentiel, l'ajout d'une étape, ou les réglages de l'étape choisie — et à droite le déroulé du tournoi, étape par étape.
	/// Une étape ajoutée a déjà des réglages habituels : rien n'est obligatoire à part le nom et l'heure.
	/// </summary>
	public partial class TournamentsScreen
	{
		private TournamentConfigDto _form;
		private long _editing;              // 0 = création
		private bool _participate = true;
		private int _step = -1;             // étape choisie (-1 : réglages du tournoi)
		private bool _more;                 // « Plus de réglages » déplié
		private string _addMode = "br";
		private VBoxContainer _settings, _flow;
		private LineEdit _startField, _opensField, _closesField;

		private void Editor(string title, Control content)
		{
			_editor?.QueueFree();
			_editor = content;
			_browser.Visible = false;
			_create.Visible = false;
			_title.Text = title;
			content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			_browser.GetParent().AddChild(content);
		}

		private void CloseEditor()
		{
			_editor?.QueueFree();
			_editor = null; _form = null; _settings = _flow = null;
			_browser.Visible = true;
			_create.Visible = AccountClient.LoggedIn;
			_title.Text = I18n.T("Tournois");
			Say("");
			ShowDetails(_selected);
		}

		// ------------------------------------------------------------------ choix d'un modèle

		private int _templateTab;
		private TournamentTemplatesDto _templates;

		private async void ChooseTemplate()
		{
			_editing = 0;
			Say("");
			var box = new VBoxContainer();
			Editor(I18n.T("Nouveau tournoi"), box);
			box.AddChild(new LoadingRing());
			var (error, t) = await SocialClient.TemplatesAsync();
			if (!IsInstanceValid(this) || _editor != box) return;
			// service trop ancien ou injoignable : on part d'une page blanche
			if (error != null) { OpenEditor(Blank(), 0); Say(error); return; }
			_templates = t;
			ShowTemplates();
		}

		private void ShowTemplates()
		{
			var page = new VBoxContainer();
			page.AddThemeConstantOverride("separation", 10);
			Editor(I18n.T("Nouveau tournoi"), page);
			var tabs = new HBoxContainer();
			tabs.AddThemeConstantOverride("separation", 8);
			string[] names = { I18n.T("Modèles du jeu"), I18n.T("Mes modèles") + (_templates.Mine.Count > 0 ? $" ({_templates.Mine.Count})" : ""), I18n.T("Modèles des joueurs") + (_templates.Shared.Count > 0 ? $" ({_templates.Shared.Count})" : "") };
			for (int i = 0; i < names.Length; i++) { int tab = i; tabs.AddChild(TournamentFlow.Chip(names[i], _templateTab == i, MenuStyle.Cyan, () => { _templateTab = tab; ShowTemplates(); })); }
			page.AddChild(tabs);
			var list = Scroller(page);
			var flow = new HFlowContainer();
			flow.AddThemeConstantOverride("h_separation", 10);
			flow.AddThemeConstantOverride("v_separation", 10);
			list.AddChild(flow);

			if (_templateTab == 0) flow.AddChild(TemplateCard(I18n.T("Page blanche"), "", null, MenuStyle.Muted, () => OpenEditor(Blank(), 0), null));
			var templates = _templateTab == 1 ? _templates.Mine : _templateTab == 2 ? _templates.Shared : _templates.BuiltIn;
			foreach (var t in templates)
			{
				if (t.Config == null) continue;
				var config = t.Config;
				string name = _templateTab == 0 ? TournamentFlow.Title(config) + (config.Phases.All(x => x.Mode == "br") ? "  —  " + I18n.T(Registration[Math.Clamp(config.TeamSize - 1, 0, 4)]) : "") : t.Name;
				string sub = I18n.T(Registration[Math.Clamp(config.TeamSize - 1, 0, 4)]) + "  ·  " + I18n.T("jusqu'à {0} inscrits", config.MaxEntries) + (_templateTab == 2 ? "  ·  " + I18n.T("par {0}", t.Owner) : "");
				HBoxContainer tools = null;
				if (_templateTab == 1)
				{
					tools = new HBoxContainer();
					tools.AddThemeConstantOverride("separation", 6);
					long id = t.Id; bool shared = t.Shared;
					tools.AddChild(TournamentFlow.Chip(shared ? I18n.T("Public") : I18n.T("Privé"), shared, new Color("5dd39e"), () => TemplateAct(SocialClient.ShareTemplateAsync(id, !shared)), 13));
					tools.AddChild(TournamentFlow.Chip(I18n.T("Supprimer"), false, MenuStyle.Red, () => Confirm(I18n.T("Supprimer le modèle « {0} » ?", name), () => TemplateAct(SocialClient.DeleteTemplateAsync(id))), 13));
				}
				flow.AddChild(TemplateCard(name, sub, config, TournamentFlow.ModeColor(config.Phases.Select(TournamentFlow.ModeOf).FirstOrDefault(m => m != "") ?? ""), () => OpenEditor(FromTemplate(config), 0), tools));
			}
			if (templates.Count == 0) flow.AddChild(Muted(_templateTab == 1
				? I18n.T("Tu n'as pas encore de modèle : compose un tournoi, puis « Enregistrer comme modèle ».")
				: I18n.T("Aucun joueur n'a encore partagé de modèle.")));
		}

		private async void TemplateAct(System.Threading.Tasks.Task<string> task)
		{
			string error = await task;
			if (!IsInstanceValid(this)) return;
			Say(error);
			var (e, t) = await SocialClient.TemplatesAsync();
			if (!IsInstanceValid(this) || _editor == null || _form != null) return;
			if (t != null) _templates = t; else Say(e);
			ShowTemplates();
		}

		private static Control TemplateCard(string name, string sub, TournamentConfigDto config, Color edge, Action pressed, Control tools)
		{
			var box = new VBoxContainer { CustomMinimumSize = new Vector2(316, 92) };
			box.AddThemeConstantOverride("separation", 4);
			box.AddChild(TournamentFlow.Line(name, 18));
			box.AddChild(TournamentFlow.Line(sub, 14, MenuStyle.Muted));
			if (config != null) box.AddChild(TournamentFlow.Line(TournamentFlow.Outline(config), 14, MenuStyle.Cyan));
			if (tools != null) box.AddChild(tools);
			var card = TournamentFlow.Card(box, edge, false, pressed);
			card.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
			return card;
		}

		private static long Tomorrow() => new DateTimeOffset(DateTime.Now.AddDays(1).Date.AddHours(20)).ToUnixTimeSeconds();

		private static TournamentConfigDto Blank() => new TournamentConfigDto { StartAt = Tomorrow(), MinEntries = 2, MaxEntries = 60, CheckInMinutes = 15 };

		private static TournamentConfigDto FromTemplate(TournamentConfigDto template)
		{
			var c = template.Copy();
			c.StartAt = Tomorrow(); c.RegistrationOpensAt = c.RegistrationClosesAt = 0;
			return c;
		}

		// ------------------------------------------------------------------ page à deux parties

		private void OpenEditor(TournamentConfigDto config, long editing)
		{
			_form = config.Copy();
			_editing = editing;
			_step = -1; _more = false;
			foreach (var p in _form.Phases) p.Mode = TournamentFlow.ModeOf(p);
			_addMode = _form.Phases.Select(p => p.Mode).LastOrDefault(m => !string.IsNullOrEmpty(m)) ?? "br";
			Say("");
			var page = new VBoxContainer();
			page.AddThemeConstantOverride("separation", 8);
			Editor(_editing == 0 ? I18n.T("Nouveau tournoi") : I18n.T("Modifier le tournoi"), page);
			var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			body.AddThemeConstantOverride("separation", 14);
			page.AddChild(body);
			_settings = Scroller(body, 430);
			body.AddChild(new VSeparator());
			_flow = Scroller(body);

			var buttons = new HBoxContainer();
			buttons.AddThemeConstantOverride("separation", 10);
			var submit = new Button { Text = _editing == 0 ? I18n.T("Créer le tournoi") : I18n.T("Enregistrer"), ThemeTypeVariation = "MenuPrimary", CustomMinimumSize = new Vector2(0, 42) };
			submit.Pressed += Submit;
			buttons.AddChild(submit);
			buttons.AddChild(Action(I18n.T("Enregistrer comme modèle"), AskTemplateName));
			page.AddChild(buttons);
			Refresh();
		}

		/// <summary>Reconstruit les deux parties (différé : on est souvent dans le clic d'un bouton qui va disparaître).</summary>
		private void Refresh() => CallDeferred(nameof(RefreshNow));

		private void RefreshNow()
		{
			if (_form == null || _settings == null) return;
			ReadDates();
			Empty(_settings);
			if (_step >= _form.Phases.Count) _step = -1;
			if (_step >= 0) BuildStep(_form.Phases[_step]); else BuildGeneral();
			RefreshFlow();
		}

		private void RefreshFlow()
		{
			if (_form == null || _flow == null) return;
			Empty(_flow);
			BuildTimeline();
		}

		private static void Empty(Control box) { foreach (var c in box.GetChildren()) { box.RemoveChild(c); c.QueueFree(); } }

		// ------------------------------------------------------------------ à droite : le déroulé

		private void BuildTimeline()
		{
			var f = _form;
			var entry = new VBoxContainer();
			entry.AddThemeConstantOverride("separation", 2);
			entry.AddChild(TournamentFlow.Line(I18n.T("Inscriptions") + "  —  " + I18n.T(Registration[Math.Clamp(f.TeamSize - 1, 0, 4)]), 17, null, wrap: false));
			entry.AddChild(TournamentFlow.Line((f.TeamSize > 1 ? I18n.T("{0} équipes", $"{f.MinEntries} – {f.MaxEntries}") : I18n.T("{0} joueurs", $"{f.MinEntries} – {f.MaxEntries}"))
				+ "  ·  " + Full(f.StartAt) + (f.StartMode != "manual" && f.CheckInMinutes > 0 ? "  ·  " + I18n.T("Check-in") + " " + I18n.T("{0} min avant le départ", f.CheckInMinutes) : ""), 14, MenuStyle.Muted));
			_flow.AddChild(TournamentFlow.Card(entry, MenuStyle.Muted, _step < 0, () => { _step = -1; Refresh(); }));

			if (f.Phases.Count == 0)
			{
				_flow.AddChild(TournamentFlow.Arrow(I18n.T("{0} joueurs", f.MaxEntries)));
				_flow.AddChild(Muted(I18n.T("Ajoute une première étape depuis la partie gauche.")));
				return;
			}
			var entrants = TournamentFlow.Entrants(f, f.MaxEntries);
			_flow.AddChild(TournamentFlow.Arrow(f.TeamSize > 1 ? I18n.T("{0} équipes", f.MaxEntries) : I18n.T("{0} joueurs", f.MaxEntries)));
			for (int i = 0; i < f.Phases.Count; i++)
			{
				int index = i;
				var p = f.Phases[i];
				var box = new VBoxContainer();
				box.AddThemeConstantOverride("separation", 3);
				box.AddChild(TournamentFlow.Head(f, i));
				box.AddChild(TournamentFlow.Line(TournamentFlow.Summary(f, i), 14, MenuStyle.Muted));
				box.AddChild(TournamentFlow.Line(TournamentFlow.Volume(f, i, entrants[i]), 13, MenuStyle.Cyan));
				string problem = Problem(i);
				if (problem != null) box.AddChild(TournamentFlow.Line("⚠ " + problem, 14, new Color(1f, 0.6f, 0.45f)));
				if (_step == i)
				{
					var tools = new HBoxContainer();
					tools.AddThemeConstantOverride("separation", 6);
					if (i > 0) tools.AddChild(TournamentFlow.Chip("▲", false, MenuStyle.Cyan, () => Move(index, -1), 13));
					if (i < f.Phases.Count - 1) tools.AddChild(TournamentFlow.Chip("▼", false, MenuStyle.Cyan, () => Move(index, 1), 13));
					tools.AddChild(TournamentFlow.Chip(I18n.T("Dupliquer"), false, MenuStyle.Cyan, () => Insert(p.Copy(), index + 1), 13));
					tools.AddChild(TournamentFlow.Chip(I18n.T("Retirer"), false, MenuStyle.Red, () => { f.Phases.RemoveAt(index); _step = -1; Refresh(); }, 13));
					box.AddChild(tools);
				}
				_flow.AddChild(TournamentFlow.Card(box, TournamentFlow.ModeColor(p.Mode), _step == i, () => { if (_step == index) return; _step = index; _more = false; Refresh(); }));
				_flow.AddChild(TournamentFlow.Arrow(TournamentFlow.Passing(f, i), i == f.Phases.Count - 1));
			}
		}

		/// <summary>Ce qui ne va pas avec la place d'une étape (le reste est vérifié à la création).</summary>
		private string Problem(int i)
		{
			var p = _form.Phases[i];
			if (!TournamentFlow.IsSplit(p)) return null;
			if (i == 0 || TournamentFlow.IsSplit(_form.Phases[i - 1])) return I18n.T("une séparation en divisions se place après une étape jouée.");
			return i == _form.Phases.Count - 1 ? I18n.T("une séparation en divisions est suivie d'au moins une étape.") : null;
		}

		private void Move(int index, int by)
		{
			var p = _form.Phases[index];
			_form.Phases.RemoveAt(index);
			_form.Phases.Insert(index + by, p);
			_step = index + by;
			Refresh();
		}

		private void Insert(PhaseConfigDto p, int at)
		{
			if (_form.Phases.Count >= MaxPhases) { Say(I18n.T("Un tournoi compte au plus {0} phases.", MaxPhases)); return; }
			_form.Phases.Insert(at, p);
			_step = at; _more = false;
			Say("");
			Refresh();
		}

		// ------------------------------------------------------------------ à gauche : le tournoi

		private void BuildGeneral()
		{
			var f = _form;
			_settings.AddChild(Section(I18n.T("L'essentiel")));
			var name = new LineEdit { Text = f.Title, PlaceholderText = I18n.T("Nom du tournoi (3 à 60 caractères)"), MaxLength = 60 };
			name.TextChanged += t => f.Title = t;
			_settings.AddChild(name);

			_startField = DateField(f.StartAt, false);
			_startField.TextChanged += _ => { if (ReadDate(_startField.Text, false, out long at)) { f.StartAt = at; RefreshFlow(); } };
			Row(I18n.T("Date et heure"), _startField);
			var dates = new HBoxContainer();
			dates.AddThemeConstantOverride("separation", 6);
			var now = DateTime.Now;
			void Quick(string label, DateTime when) => dates.AddChild(TournamentFlow.Chip(label, f.StartAt == new DateTimeOffset(when).ToUnixTimeSeconds(), MenuStyle.Cyan, () => { f.StartAt = new DateTimeOffset(when).ToUnixTimeSeconds(); _startField.Text = when.ToString(DateFormat); Refresh(); }, 13));
			Quick(I18n.T("Dans 1 heure"), now.Date.AddHours(now.Hour + 1).AddMinutes(now.Minute));
			if (now.Hour < 19) Quick(I18n.T("Ce soir"), now.Date.AddHours(20));
			Quick(I18n.T("Demain"), now.Date.AddDays(1).AddHours(20));
			Quick(I18n.T("Samedi"), now.Date.AddDays(((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7 is var d && d == 0 ? 7 : d).AddHours(20));
			_settings.AddChild(dates);

			bool locked = _editing != 0 && (SocialClient.Tournaments.Games.FirstOrDefault(g => g.Id == _editing)?.Players ?? 0) > 0;
			var sizes = new HBoxContainer();
			sizes.AddThemeConstantOverride("separation", 6);
			for (int n = 1; n <= 5; n++)
			{
				int size = n;
				var chip = TournamentFlow.Chip(n == 1 ? I18n.T("Solo") : n.ToString(), f.TeamSize == n, MenuStyle.Cyan, () => SetTeamSize(size));
				chip.Disabled = locked;
				sizes.AddChild(chip);
			}
			Row(f.TeamSize > 1 ? I18n.T(Registration[f.TeamSize - 1]) : I18n.T("Inscription"), sizes);
			Row(I18n.T("Maximum"), Number(2, 100, f.MaxEntries, v => { f.MaxEntries = v; if (f.MinEntries > v) f.MinEntries = v; }));
			if (_editing == 0)
			{
				var me = new CheckButton { Text = f.TeamSize > 1 ? I18n.T("Je participe aussi (je serai chef de mon équipe)") : I18n.T("Je participe aussi"), ButtonPressed = _participate };
				me.Theme = MenuStyle.SharedTheme;
				foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus" }) me.AddThemeStyleboxOverride(state, new StyleBoxEmpty { ContentMarginTop = 2, ContentMarginBottom = 2 });
				me.AddThemeFontSizeOverride("font_size", 15);
				me.Toggled += on => _participate = on;
				_settings.AddChild(me);
			}

			BuildAdd();

			More();
			if (!_more) return;
			var desc = new TextEdit { Text = f.Description, PlaceholderText = I18n.T("Règlement, récompenses, lien de diffusion… (facultatif)"), CustomMinimumSize = new Vector2(0, 70), WrapMode = TextEdit.LineWrappingMode.Boundary };
			desc.TextChanged += () => f.Description = desc.Text.Length > 500 ? desc.Text[..500] : desc.Text;
			_settings.AddChild(desc);
			Row(I18n.T("Minimum pour lancer"), Number(2, 100, f.MinEntries, v => f.MinEntries = v));
			Row(I18n.T("Départ"), Options(new[] { I18n.T("À heure fixe (lancement automatique)"), I18n.T("Lancé par l'organisateur") }, f.StartMode == "manual" ? 1 : 0, i => { f.StartMode = i == 1 ? "manual" : "fixed"; Refresh(); }));
			if (f.StartMode != "manual")
				Row(I18n.T("Check-in"), Options(CheckInChoices.Select(m => m == 0 ? I18n.T("Pas de check-in") : I18n.T("{0} min avant le départ", m)).ToArray(), Math.Max(0, Array.IndexOf(CheckInChoices, f.CheckInMinutes)), i => { f.CheckInMinutes = CheckInChoices[i]; RefreshFlow(); }));
			_opensField = DateField(f.RegistrationOpensAt, true, I18n.T("dès maintenant"));
			Row(I18n.T("Ouverture des inscriptions"), _opensField);
			_closesField = DateField(f.RegistrationClosesAt, true, I18n.T("au départ"));
			Row(I18n.T("Clôture des inscriptions"), _closesField);
			_settings.AddChild(Muted(I18n.T("Format des dates : JJ/MM/AAAA HH:MM (heure de ton ordinateur). Inscriptions possibles jusqu'à un an à l'avance. À heure fixe, le tournoi est annulé si le minimum d'inscrits présents n'est pas atteint ; le check-in retire les inscrits qui n'ont pas confirmé leur présence.")));
		}

		/// <summary>Reprend les dates saisies avant de reconstruire la page.</summary>
		private void ReadDates()
		{
			if (Alive(_startField) && ReadDate(_startField.Text, false, out long start)) _form.StartAt = start;
			if (Alive(_opensField) && ReadDate(_opensField.Text, true, out long opens)) _form.RegistrationOpensAt = opens;
			if (Alive(_closesField) && ReadDate(_closesField.Text, true, out long closes)) _form.RegistrationClosesAt = closes;
		}

		private static bool Alive(Control c) => c != null && IsInstanceValid(c) && c.IsInsideTree() && !c.IsQueuedForDeletion();

		private void More()
		{
			var b = TournamentFlow.Chip((_more ? "▲  " : "▼  ") + I18n.T("Plus de réglages"), false, MenuStyle.Cyan, () => { _more = !_more; Refresh(); }, 14);
			_settings.AddChild(b);
		}

		/// <summary>Ajouter une étape : le mode de jeu, puis le déroulement. Elle arrive avec des réglages habituels, après l'étape choisie (ou à la fin).</summary>
		private void BuildAdd()
		{
			_settings.AddChild(Section(I18n.T("Ajouter une étape")));
			var modes = new HFlowContainer();
			modes.AddThemeConstantOverride("h_separation", 6);
			modes.AddThemeConstantOverride("v_separation", 6);
			var list = ModesFor(_form.TeamSize);
			if (!list.Contains(_addMode)) _addMode = list[0];
			foreach (var mode in list) { string m = mode; modes.AddChild(TournamentFlow.Chip(TournamentFlow.ModeLabel(mode), _addMode == mode, TournamentFlow.ModeColor(mode), () => { _addMode = m; Refresh(); }, 14)); }
			_settings.AddChild(modes);
			foreach (var kind in KindsFor(_addMode, _form.TeamSize).Append("split"))
			{
				string k = kind;
				var sample = new PhaseConfigDto { Kind = k, Mode = k == "split" ? "" : _addMode };
				var box = new VBoxContainer();
				box.AddThemeConstantOverride("separation", 0);
				box.AddChild(TournamentFlow.Line("+  " + TournamentFlow.KindLabel(sample), 16));
				box.AddChild(TournamentFlow.Line(KindHelp(k, _addMode), 13, MenuStyle.Muted));
				_settings.AddChild(TournamentFlow.Card(box, TournamentFlow.ModeColor(sample.Mode), false, () => Add(k)));
			}
		}

		/// <summary>Modes proposés : en solo, les modes à deux équipes se jouent en équipes tirées au sort à chaque match.</summary>
		private static List<string> ModesFor(int teamSize) => teamSize == 1 ? new List<string> { "br", "dm", "1v1", "tdm", "sab", "ctl", "ctf" } : new List<string> { "br", "tdm", "sab", "ctl", "ctf" };

		private static List<string> KindsFor(string mode, int teamSize) => mode == "br" || mode == "dm" || (teamSize == 1 && TournamentFlow.TwoTeams(mode)) ? new List<string> { "lobby" }
			: mode == "1v1" ? new List<string> { "pools", "table", "divisions" } : new List<string> { "pools", "table", "lobby", "divisions" };

		private string KindHelp(string kind, string mode) => kind switch
		{
			"pools" => I18n.T("Petits groupes, chacun rencontre tous les autres ; les meilleurs passent."),
			"table" => I18n.T("Un match perdu et c'est fini, jusqu'à la finale."),
			"divisions" => I18n.T("D1, D2… en poules ; à l'étape suivante du même type, les premiers montent et les derniers descendent."),
			"split" => I18n.T("Coupe le classement en divisions : chacune joue la suite de son côté."),
			_ => I18n.T("Plusieurs parties dont les points s'additionnent.") + (TournamentFlow.TwoTeams(mode) && _form.TeamSize == 1 ? " " + I18n.T("Équipes tirées au sort à chaque match") + "." : ""),
		};

		private void Add(string kind)
		{
			int at = _form.Phases.Count;
			var p = NewStep(kind, kind == "split" ? "" : _addMode);
			// l'étape d'avant garde le nombre de qualifiés qui convient à celle-ci
			if (at > 0 && _form.Phases[at - 1] is var before && !TournamentFlow.IsSplit(before) && before.Qualified == 0)
			{
				int n = TournamentFlow.Entrants(_form, _form.MaxEntries)[at - 1];
				if (kind == "table" && n > 8) before.Qualified = n > 32 ? 16 : 8;
				else if (kind == "lobby" && !TournamentFlow.TwoTeams(p.Mode) && n > p.LobbySize) before.Qualified = p.LobbySize;
			}
			Insert(p, at);
			_step = -1;              // on reste sur « Ajouter une étape » pour enchaîner ; un clic sur la carte la règle
		}

		/// <summary>Une étape avec les réglages habituels de son mode et de son déroulement.</summary>
		private PhaseConfigDto NewStep(string kind, string mode)
		{
			var p = new PhaseConfigDto { Kind = kind, Mode = mode, MaxScore = TournamentFlow.Score(mode).usual, Races = 3 };
			if (kind == "pools") p.PoolSize = 4;
			if (kind == "table") p.BestOf = mode == "ctl" ? 1 : 3;
			if (kind == "lobby") LobbyDefaults(p);
			return p;
		}

		private void LobbyDefaults(PhaseConfigDto p)
		{
			int entry = Math.Max(1, _form.TeamSize);
			p.KillPoints = p.Mode == "br" ? 1 : 0;
			p.Teams = "entry";
			if (p.Mode == "br") { p.LobbySize = Math.Min(20, TournamentFlow.MaxBr / entry); p.Scale = TournamentFlow.Placement.ToList(); }
			else if (p.Mode == "dm") { p.LobbySize = TournamentFlow.MaxDm; p.Scale = TournamentFlow.Arena.ToList(); }
			else { p.Scale = new List<int> { 3, 0 }; if (entry == 1) { p.Teams = "random"; p.TeamSize = Math.Max(p.Mode == "tdm" ? 4 : 5, 2); } }
		}

		/// <summary>Solo ou équipes : les étapes qui ne s'y prêtent pas sont adaptées.</summary>
		private void SetTeamSize(int size)
		{
			var f = _form;
			f.TeamSize = size;
			foreach (var p in f.Phases)
			{
				if (TournamentFlow.IsSplit(p)) continue;
				if (size > 1 && (p.Mode == "dm" || p.Mode == "1v1")) SetMode(p, "tdm");
				else if (size == 1 && TournamentFlow.TwoTeams(p.Mode)) { p.Kind = "lobby"; LobbyDefaults(p); }
				else if (size > 1) p.Teams = "entry";
				if (p.Mode == "br") p.LobbySize = Math.Clamp(p.LobbySize, 2, TournamentFlow.MaxBr / size);
			}
			Refresh();
		}

		private void SetMode(PhaseConfigDto p, string mode)
		{
			p.Mode = mode;
			p.MaxScore = TournamentFlow.Score(mode).usual;
			var kinds = KindsFor(mode, _form.TeamSize);
			if (!kinds.Contains(p.Kind)) p.Kind = kinds[0];
			if (p.Kind == "lobby") LobbyDefaults(p);
			else { p.Teams = "entry"; if (p.Kind == "pools" && p.PoolSize == 0) p.PoolSize = 4; }
		}

		// ------------------------------------------------------------------ à gauche : une étape

		private void BuildStep(PhaseConfigDto p)
		{
			bool last = _step == _form.Phases.Count - 1;
			var back = TournamentFlow.Chip("←  " + I18n.T("Réglages du tournoi, ajouter une étape"), false, MenuStyle.Cyan, () => { _step = -1; _more = false; Refresh(); }, 14);
			_settings.AddChild(back);
			_settings.AddChild(Section(TournamentFlow.IsSplit(p) ? TournamentFlow.KindLabel(p) : TournamentFlow.ModeLabel(p.Mode) + "  —  " + TournamentFlow.KindLabel(p)));
			if (TournamentFlow.IsSplit(p)) { BuildSplit(p); return; }

			var modes = new HFlowContainer();
			modes.AddThemeConstantOverride("h_separation", 6);
			modes.AddThemeConstantOverride("v_separation", 6);
			foreach (var mode in ModesFor(_form.TeamSize)) { string m = mode; modes.AddChild(TournamentFlow.Chip(TournamentFlow.ModeLabel(m), p.Mode == m, TournamentFlow.ModeColor(m), () => { if (p.Mode != m) { SetMode(p, m); Refresh(); } }, 14)); }
			_settings.AddChild(modes);
			var kinds = KindsFor(p.Mode, _form.TeamSize);
			if (!kinds.Contains(p.Kind)) kinds.Add(p.Kind);
			if (kinds.Count > 1)
			{
				var row = new HFlowContainer();
				row.AddThemeConstantOverride("h_separation", 6);
				row.AddThemeConstantOverride("v_separation", 6);
				foreach (var kind in kinds)
				{
					string k = kind;
					row.AddChild(TournamentFlow.Chip(TournamentFlow.KindLabel(new PhaseConfigDto { Kind = k, Mode = p.Mode }), p.Kind == k, MenuStyle.Cyan, () =>
					{
						if (p.Kind == k) return;
						p.Kind = k;
						if (k == "lobby") LobbyDefaults(p);
						if (k == "pools" && p.PoolSize == 0) p.PoolSize = 4;
						Refresh();
					}, 14));
				}
				_settings.AddChild(row);
			}

			var (min, max, _) = TournamentFlow.Score(p.Mode);
			string unit = p.Mode switch { "1v1" or "sab" => I18n.T("manches"), "ctf" => I18n.T("prises"), "ctl" => I18n.T("points"), _ => I18n.T("éliminations") };
			if (TournamentFlow.IsLobby(p)) BuildLobby(p, min, max, unit);
			else
			{
				Row(I18n.T("Pour gagner une partie"), Number(min, max, p.MaxScore, v => p.MaxScore = v, unit));
				Row(I18n.T("Un match se joue en"), Options(new[] { I18n.T("une partie"), I18n.T("2 gagnantes sur 3"), I18n.T("3 gagnantes sur 5") }, p.BestOf / 2, i => { p.BestOf = i * 2 + 1; RefreshFlow(); }));
				if (p.Kind == "pools") Row(I18n.T("Taille des poules"), Number(2, 12, p.PoolSize == 0 ? 4 : p.PoolSize, v => p.PoolSize = v));
				if (p.Kind == "divisions")
				{
					Row(I18n.T("Nombre de divisions"), Number(2, 8, p.Divisions, v => p.Divisions = v));
					Row(I18n.T("Montées"), Number(0, 20, p.Up, v => p.Up = v));
					Row(I18n.T("Descentes"), Number(0, 20, p.Down, v => p.Down = v));
				}
			}
			if (!last)
			{
				Row(I18n.T("Qualifiés pour la suite (0 = tous)"), Number(0, 100, p.Qualified, v => p.Qualified = v));
				if (TournamentFlow.IsLobby(p) && p.Qualified > 0) Row(I18n.T("Qualification"), Options(new[] { I18n.T("Au total (classement fusionné)"), I18n.T("Par lobby") }, p.QualifyMode == "lobby" ? 1 : 0, j => { p.QualifyMode = j == 1 ? "lobby" : "total"; RefreshFlow(); }));
			}

			More();
			if (!_more) return;
			var name = new LineEdit { Text = p.Name, PlaceholderText = I18n.T("Nom (facultatif : Qualifications, Finale, Division 1…)"), MaxLength = 40 };
			name.TextChanged += t => { p.Name = t; RefreshFlow(); };
			_settings.AddChild(name);
			if (p.Mode == "1v1") LoadoutFields(p);
			if (p.Kind == "table") Row(I18n.T("Places disputées"), Options(new[] { I18n.T("Match pour la 3e place"), I18n.T("Toutes les places"), I18n.T("Aucune (vainqueur seulement)") }, p.Places == "all" ? 1 : p.Places == "none" ? 2 : 0, j => { p.Places = j == 1 ? "all" : j == 2 ? "none" : "third"; RefreshFlow(); }));
			if (p.Kind == "divisions") Row(I18n.T("Répartition de départ"), Options(new[] { I18n.T("Au classement"), I18n.T("Au hasard") }, p.Start == "random" ? 1 : 0, j => p.Start = j == 1 ? "random" : "ranking"));
			if (TournamentFlow.IsLobby(p))
			{
				var scale = new LineEdit { Text = string.Join(", ", p.Scale), PlaceholderText = TournamentFlow.TwoTeams(p.Mode) ? "3, 0" : string.Join(", ", TournamentFlow.Placement) };
				scale.TextChanged += t => { p.Scale = ParseScale(t); RefreshFlow(); };
				Row(TournamentFlow.TwoTeams(p.Mode) ? I18n.T("Points (vainqueur, perdant)") : I18n.T("Points par place (1er, 2e…)"), scale);
				if (!TournamentFlow.TwoTeams(p.Mode)) Row(I18n.T("Points par élimination"), Number(0, 20, p.KillPoints, v => p.KillPoints = v));
				Row(I18n.T("Points d'un abandon"), Number(0, 100, p.AbandonPoints, v => p.AbandonPoints = v));
				Row(I18n.T("Répartition"), Options(new[] { I18n.T("Équilibrée (serpentin)"), I18n.T("Par niveau (divisions)") }, p.Distribution == "level" ? 1 : 0, j => { p.Distribution = j == 1 ? "level" : "balanced"; RefreshFlow(); }));
			}
		}

		/// <summary>Série de manches : parties, lobbys, points (trois barèmes tout prêts, ou le sien dans « Plus de réglages »).</summary>
		private void BuildLobby(PhaseConfigDto p, int min, int max, string unit)
		{
			bool two = TournamentFlow.TwoTeams(p.Mode);
			Row(two ? I18n.T("Matchs") : I18n.T("Manches"), Number(1, 12, p.Races, v => p.Races = v));
			if (p.Mode == "br") Row(_form.TeamSize > 1 ? I18n.T("Équipes par lobby") : I18n.T("Joueurs par lobby"), Number(2, Math.Max(2, TournamentFlow.MaxBr / Math.Max(1, _form.TeamSize)), p.LobbySize, v => p.LobbySize = v));
			if (p.Mode == "dm") Row(I18n.T("Joueurs par lobby"), Number(2, TournamentFlow.MaxDm, p.LobbySize, v => p.LobbySize = v));
			if (p.Mode != "br") Row(I18n.T("Pour gagner une partie"), Number(min, max, p.MaxScore, v => p.MaxScore = v, unit));
			if (_form.TeamSize == 1 && (two || p.Mode == "br"))
			{
				if (p.Mode == "br") Row(I18n.T("Équipes"), Options(new[] { I18n.T("Chacun pour soi"), I18n.T("Équipes tirées au sort à chaque manche") }, p.Teams == "random" ? 1 : 0, j => { p.Teams = j == 1 ? "random" : "entry"; if (j == 1) p.TeamSize = Math.Max(2, p.TeamSize); Refresh(); }));
				if (p.Teams == "random") Row(I18n.T("Joueurs par équipe tirée"), Number(2, 5, p.TeamSize, v => p.TeamSize = v));
			}
			if (two) return;
			var sets = new List<(string label, int[] scale, int kills)>
			{
				(I18n.T("Place et éliminations"), TournamentFlow.Placement, 1), (I18n.T("La victoire surtout"), TournamentFlow.VictoryHeavy, 4), (I18n.T("Place seulement"), TournamentFlow.Arena, 0),
			};
			int current = sets.FindIndex(s => s.kills == p.KillPoints && s.scale.SequenceEqual(p.Scale));
			var labels = sets.Select(s => s.label).ToList();
			if (current < 0) { labels.Add(I18n.T("Personnalisé")); current = sets.Count; }
			Row(I18n.T("Points"), Options(labels.ToArray(), current, i => { if (i < sets.Count) { p.Scale = sets[i].scale.ToList(); p.KillPoints = sets[i].kills; Refresh(); } }));
		}

		/// <summary>Séparation : nombre de divisions, à parts égales ou avec le nombre de participants de chacune.</summary>
		private void BuildSplit(PhaseConfigDto p)
		{
			_settings.AddChild(Muted(KindHelp("split", "")));
			Row(I18n.T("Nombre de divisions"), Number(2, 8, p.Divisions, v => { if (v == p.Divisions) return; p.Divisions = v; if (p.Sizes.Count > 0) p.Sizes = Even(v); Refresh(); }));
			Row(I18n.T("Taille des divisions"), Options(new[] { I18n.T("Parts égales"), I18n.T("Choisie") }, p.Sizes.Count > 0 ? 1 : 0, j => { p.Sizes = j == 1 ? Even(p.Divisions) : new List<int>(); Refresh(); }));
			for (int i = 0; i < p.Sizes.Count; i++)
			{
				int k = i;
				if (i == p.Sizes.Count - 1) { Row(I18n.T("Division {0}", i + 1), Muted(I18n.T("le reste"))); continue; }
				Row(I18n.T("Division {0}", i + 1), Number(2, 100, p.Sizes[i], v => p.Sizes[k] = v));
			}
		}

		private List<int> Even(int divisions)
		{
			int n = TournamentFlow.Entrants(_form, _form.MaxEntries)[Math.Max(0, _step)];
			return Enumerable.Repeat(Math.Max(2, n / Math.Max(1, divisions)), divisions).ToList();
		}

		/// <summary>1 VS 1 : équipement au hasard à chaque manche, choisi par chacun au début, ou imposé (deux armes avec leur viseur, une grenade, un soin).</summary>
		private void LoadoutFields(PhaseConfigDto p)
		{
			string policy = p.Loadout == "choice" ? "choice" : (p.Loadout ?? "").StartsWith("fixed:") ? "fixed" : "random";
			Row(I18n.T("Équipement"), Options(new[] { I18n.T("Au hasard à chaque manche (le même pour les deux)"), I18n.T("Choisi par chaque joueur au début"), I18n.T("Imposé") }, policy == "choice" ? 1 : policy == "fixed" ? 2 : 0, j =>
			{
				p.Loadout = j == 1 ? "choice" : j == 2 ? "fixed:" + new DuelLoadout(3, 3, 2, 1, 1, 0) : "random";
				Refresh();
			}));
			if (policy != "fixed") return;
			if (!DuelLoadout.TryParse(p.Loadout.Substring(6), out var l)) l = new DuelLoadout(3, 3, 2, 1, 1, 0);
			var flow = new HFlowContainer();
			flow.AddThemeConstantOverride("h_separation", 6);
			flow.AddThemeConstantOverride("v_separation", 6);
			int[] values = { l.Weapon, l.Optic, l.Weapon2, l.Optic2, l.Grenade, l.Heal };
			var lists = new[] { DuelRules.Weapons, DuelRules.Optics, DuelRules.Weapons, DuelRules.Optics, DuelRules.Grenades, DuelRules.Heals };
			for (int k = 0; k < lists.Length; k++)
			{
				int slot = k;
				flow.AddChild(Options(lists[k].Select(c => I18n.T(c.Label)).ToArray(), values[k], j =>
				{
					values[slot] = j;
					p.Loadout = "fixed:" + new DuelLoadout(values[0], values[1], values[2], values[3], values[4], values[5]);
				}));
			}
			_settings.AddChild(Muted(I18n.T("Arme 1, viseur, arme 2, viseur, grenade, soin")));
			_settings.AddChild(flow);
		}

		private static List<int> ParseScale(string text)
		{
			var list = new List<int>();
			foreach (var part in text.Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries))
				if (int.TryParse(part, out int v)) list.Add(Math.Clamp(v, 0, 100));
			return list;
		}

		// ------------------------------------------------------------------ enregistrement

		private async void Submit()
		{
			var f = _form;
			if (Alive(_startField) && !ReadDate(_startField.Text, false, out _)) { Say(I18n.T("Date de départ invalide : JJ/MM/AAAA HH:MM.")); return; }
			if (Alive(_opensField) && !ReadDate(_opensField.Text, true, out _)) { Say(I18n.T("Date d'ouverture des inscriptions invalide.")); return; }
			if (Alive(_closesField) && !ReadDate(_closesField.Text, true, out _)) { Say(I18n.T("Date de clôture des inscriptions invalide.")); return; }
			ReadDates();
			if (f.Phases.Count == 0) { Say(I18n.T("Ajoute une première étape depuis la partie gauche.")); return; }
			if (f.Title.Trim().Length < 3) { Say(I18n.T("Le nom du tournoi doit faire entre 3 et 60 caractères.")); if (_step >= 0) { _step = -1; Refresh(); } return; }
			if (f.StartMode == "manual") f.CheckInMinutes = 0;
			Say(I18n.T("Enregistrement…"));
			long id = _editing;
			string error, done;
			if (_editing == 0)
			{
				(error, id) = await SocialClient.CreateTournamentAsync(f, _participate);
				done = I18n.T("Tournoi créé : partage-le pour que les joueurs s'inscrivent !");
			}
			else
			{
				error = await SocialClient.UpdateTournamentAsync(_editing, f);
				done = I18n.T("Modifications enregistrées.");
			}
			if (!IsInstanceValid(this)) return;
			if (error != null) { Say(I18n.T(error)); return; }
			_selected = id;
			if (_editing == 0) _filter.Select(1);
			CloseEditor();
			Say(done);
			Reload();
		}

		/// <summary>Enregistrer comme modèle : un nom, et le choix de le rendre public (privé d'office).</summary>
		private void AskTemplateName()
		{
			ReadDates();
			if (_form.Phases.Count == 0) { Say(I18n.T("Ajoute une première étape depuis la partie gauche.")); return; }
			var box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 8);
			var name = new LineEdit { Text = _form.Title, PlaceholderText = I18n.T("Nom du modèle"), MaxLength = 40, CustomMinimumSize = new Vector2(420, 0) };
			box.AddChild(name);
			var shared = new CheckButton { Text = I18n.T("Public : tous les joueurs peuvent s'en servir") };
			box.AddChild(shared);
			var dialog = new ConfirmationDialog { Title = I18n.T("Enregistrer comme modèle"), OkButtonText = I18n.T("Enregistrer"), CancelButtonText = I18n.T("Annuler"), Theme = MenuStyle.SharedTheme };
			dialog.AddChild(box);
			dialog.AddThemeStyleboxOverride("panel", MenuStyle.CardStyle(12));
			var frame = MenuStyle.CardStyle(12);
			frame.ExpandMarginTop = 36;
			foreach (var style in new[] { "embedded_border", "embedded_unfocused_border" }) dialog.AddThemeStyleboxOverride(style, frame);
			async void Save()
			{
				string error = await SocialClient.SaveTemplateAsync(name.Text.Trim(), shared.ButtonPressed, _form);
				if (!IsInstanceValid(this)) return;
				Say(error ?? I18n.T("Modèle enregistré dans « Mes modèles »."));
				dialog.QueueFree();
			}
			dialog.Confirmed += Save;
			dialog.Canceled += dialog.QueueFree;
			AddChild(dialog);
			dialog.PopupCentered(new Vector2I(480, 170));
		}

		// ------------------------------------------------------------------ champs

		/// <summary>Une ligne « libellé : champ » des réglages.</summary>
		private void Row(string label, Control field)
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 10);
			var l = TournamentFlow.Line(label, 15);
			l.CustomMinimumSize = new Vector2(130, 0);
			l.VerticalAlignment = VerticalAlignment.Center;
			row.AddChild(l);
			field.CustomMinimumSize = new Vector2(Math.Max(field.CustomMinimumSize.X, 190), field.CustomMinimumSize.Y);
			field.SizeFlagsHorizontal = Control.SizeFlags.Fill;
			if (field is OptionButton o) { o.FitToLongestItem = false; o.ClipText = true; o.CustomMinimumSize = new Vector2(250, 0); Compact(o); }
			if (field is LineEdit line) Compact(line);
			field.AddThemeFontSizeOverride("font_size", 15);
			row.AddChild(field);
			_settings.AddChild(row);
		}

		/// <summary>Champ moins haut que dans le reste des menus : il y en a beaucoup sur cette page.</summary>
		private static void Compact(Control c)
		{
			foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled", "read_only" })
			{
				var s = new StyleBoxFlat { BgColor = state == "focus" ? Colors.Transparent : new Color("0c1523"), BorderColor = state is "hover" or "focus" ? MenuStyle.Cyan : MenuStyle.CardBorder };
				s.SetBorderWidthAll(1); s.SetCornerRadiusAll(6);
				s.ContentMarginLeft = s.ContentMarginRight = 10; s.ContentMarginTop = s.ContentMarginBottom = 4;
				c.AddThemeStyleboxOverride(state, s);
			}
		}

		/// <summary>Nombre à régler ; le déroulé suit aussitôt.</summary>
		private SpinBox Number(int min, int max, int value, Action<int> changed, string suffix = "")
		{
			var s = new SpinBox { MinValue = min, MaxValue = max, Value = Math.Clamp(value, min, max), Rounded = true, Suffix = suffix };
			if ((int)s.Value != value) changed((int)s.Value);
			s.GetLineEdit().AddThemeFontSizeOverride("font_size", 15);
			s.GetLineEdit().Theme = MenuStyle.SharedTheme;
			Compact(s.GetLineEdit());
			s.ValueChanged += v => { changed((int)v); RefreshFlow(); };
			return s;
		}
	}
}
