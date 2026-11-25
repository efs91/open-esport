using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Page « Tournois » : la liste (à venir, mes tournois, en cours, terminés), le détail d'un tournoi (configuration, équipes et invitations, check-in,
	/// classement, actions de l'organisateur) et la page de création / modification : inscription en solo ou par équipes (le chef invite ses coéquipiers,
	/// qui acceptent ou refusent), inscrits mini et maxi, départ à heure fixe ou par l'organisateur, période d'inscription, check-in, et les phases,
	/// chacune dans son mode de jeu (Battle Royale, Capture du drapeau, 1 VS 1) : séries de manches avec leurs points, poules, tableau, sélections et divisions.
	/// </summary>
	public partial class TournamentsScreen : CanvasLayer
	{
		public event Action Closed;
		/// <summary>Tournoi à montrer à l'ouverture (0 : le premier de la liste).</summary>
		public long Focus;
		/// <summary>Ouvre directement la page de création.</summary>
		public bool StartWithCreate;

		private const string DateFormat = "dd/MM/yyyy HH:mm";
		private static readonly string[] Registration = { /*T*/"Solo", /*T*/"Équipes de 2", /*T*/"Équipes de 3", /*T*/"Équipes de 4", /*T*/"Équipes de 5" };
		private const int MaxPhases = 6;
		private static readonly int[] CheckInChoices = { 0, 5, 10, 15, 30, 60 };

		private OptionButton _filter;
		private VBoxContainer _list, _right;
		private Label _message;
		private long _selected;
		private double _refresh;
		private bool _busy;

		// formulaire
		private TournamentConfigDto _form;
		private long _editing;          // 0 = création
		private bool _participate = true;
		private VBoxContainer _phasesBox;
		private LineEdit _startField, _opensField, _closesField;
		private Control _checkInRow;

		public TournamentsScreen() { Layer = 115; }

		public override void _Ready()
		{
			AddChild(MenuStyle.Backdrop());
			var margin = new MarginContainer { Theme = MenuStyle.SharedTheme };
			margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 28);
			AddChild(margin);
			var page = new VBoxContainer();
			page.AddThemeConstantOverride("separation", 10);
			margin.AddChild(MenuStyle.Card(page, margin: 18));

			var head = new HBoxContainer();
			head.AddThemeConstantOverride("separation", 12);
			var title = new Label { Text = I18n.T("Tournois"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			title.AddThemeFontSizeOverride("font_size", 30);
			head.AddChild(title);
			if (AccountClient.LoggedIn)
			{
				var create = new Button { Text = I18n.T("+ Créer un tournoi"), CustomMinimumSize = new Vector2(200, 40) };
				create.Pressed += () => OpenForm(null);
				head.AddChild(create);
			}
			var close = new Button { Text = I18n.T("Retour"), CustomMinimumSize = new Vector2(140, 40) };
			close.Pressed += Close;
			head.AddChild(close);
			page.AddChild(head);

			_message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_message.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.4f));
			page.AddChild(_message);

			var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			body.AddThemeConstantOverride("separation", 16);
			page.AddChild(body);

			var left = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
			left.AddThemeConstantOverride("separation", 8);
			body.AddChild(left);
			_filter = new OptionButton();
			foreach (var f in new[] { /*T*/"À venir", /*T*/"Mes tournois", /*T*/"En cours", /*T*/"Terminés" }) _filter.AddItem(I18n.T(f));
			_filter.ItemSelected += _ => RebuildList();
			left.AddChild(_filter);
			var listScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			_list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_list.AddThemeConstantOverride("separation", 6);
			listScroll.AddChild(_list);
			left.AddChild(listScroll);

			var rightScroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			_right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_right.AddThemeConstantOverride("separation", 10);
			rightScroll.AddChild(_right);
			body.AddChild(rightScroll);

			_selected = Focus;
			if (StartWithCreate && AccountClient.LoggedIn) OpenForm(null);
			Reload();
		}

		public override void _Process(double delta)
		{
			_refresh += delta;
			if (_refresh >= 20 && !_busy && _form == null) { _refresh = 0; Reload(); }
		}

		public override void _Input(InputEvent e)
		{
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); }
		}

		private void Close() { Closed?.Invoke(); QueueFree(); }

		private void Say(string text) => _message.Text = text ?? "";

		// ------------------------------------------------------------------ liste

		private async void Reload()
		{
			_busy = true;
			try
			{
				string error = await SocialClient.RefreshTournamentsAsync();
				if (!IsInstanceValid(this)) return;
				if (error != null) Say(error);
				if (!SocialClient.Tournaments.Configured) Say(I18n.T("Les tournois ne sont pas encore ouverts sur ce serveur."));
				RebuildList();
				if (_form == null) ShowDetails(_selected);
			}
			finally { _busy = false; }
		}

		private IEnumerable<GameTournamentDto> Filtered()
		{
			var all = SocialClient.Tournaments.Games;
			return _filter.Selected switch
			{
				1 => all.Where(g => g.Joined || g.IsOwner || g.Invitations.Count > 0),
				2 => all.Where(g => g.State == "running"),
				3 => all.Where(g => g.State == "done" || g.State == "cancelled"),
				_ => all.Where(g => g.State == "open"),
			};
		}

		private void RebuildList()
		{
			foreach (var c in _list.GetChildren()) c.QueueFree();
			var items = Filtered().OrderBy(g => g.StartAt).ToList();
			if (items.Count == 0) { _list.AddChild(Muted(I18n.T("Aucun tournoi ici pour le moment."))); return; }
			if (_selected == 0 && _form == null) _selected = items[0].Id;
			var group = new ButtonGroup();
			foreach (var g in items)
			{
				string mine = g.Joined ? "★ " : "";
				var b = new Button
				{
					Text = $"{mine}{(g.Invitations.Count > 0 ? "✉ " : "")}{g.Title}\n{When(g.StartAt)}  ·  {FormatLabel(g)}  ·  {StateLabel(g)}",
					ToggleMode = true, ButtonGroup = group, ButtonPressed = g.Id == _selected, Alignment = HorizontalAlignment.Left,
					CustomMinimumSize = new Vector2(0, 58), ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				};
				long id = g.Id;
				b.Pressed += () => { _form = null; _selected = id; ShowDetails(id); };
				_list.AddChild(b);
			}
		}

		// ------------------------------------------------------------------ détail

		private async void ShowDetails(long id)
		{
			if (id == 0) { Clear(); _right.AddChild(Muted(I18n.T("Choisis un tournoi dans la liste, ou crée le tien."))); return; }
			var (error, d) = await SocialClient.TournamentDetailsAsync(id);
			if (!IsInstanceValid(this) || _form != null || _selected != id) return;
			Clear();
			if (error != null || d?.Summary == null) { _right.AddChild(Muted(error ?? I18n.T("Tournoi introuvable."))); return; }
			var g = d.Summary;
			var cfg = d.Config ?? new TournamentConfigDto();

			var title = new Label { Text = g.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
			title.AddThemeFontSizeOverride("font_size", 26);
			_right.AddChild(title);
			if (!string.IsNullOrWhiteSpace(g.Description)) _right.AddChild(Text(g.Description));
			if (!string.IsNullOrWhiteSpace(g.Note)) _right.AddChild(Warn(I18n.Server(g.Note)));

			var info = new GridContainer { Columns = 2 };
			info.AddThemeConstantOverride("h_separation", 18);
			info.AddThemeConstantOverride("v_separation", 4);
			void Row(string k, string v) { info.AddChild(Muted(k)); info.AddChild(Text(v)); }
			Row(I18n.T("Format"), FormatLabel(g));
			Row(I18n.T("Organisateur"), g.Owner);
			Row(I18n.T("État"), StateLabel(g));
			Row(I18n.T("Départ"), g.StartMode == "manual"
				? I18n.T("lancé par l'organisateur (prévu le {0})", Full(g.StartAt))
				: I18n.T("à heure fixe, le {0}", Full(g.StartAt)) + "  ·  " + Countdown(g.StartAt));
			Row(I18n.T("Inscriptions"), I18n.T("du {0} au {1}", Full(g.RegistrationOpensAt), g.StartMode == "manual" && cfg.RegistrationClosesAt <= 0 ? I18n.T("départ") : Full(g.RegistrationClosesAt)));
			if (g.CheckInMinutes > 0) Row(I18n.T("Check-in"), I18n.T("{0} min avant le départ (sans confirmation, l'inscription est retirée)", g.CheckInMinutes));
			Row(g.TeamSize > 1 ? I18n.T("Équipes") : I18n.T("Inscrits"), g.TeamSize > 1
				? I18n.T("{0} / {1}  (minimum {2}) — {3} complète(s)", g.Players, g.MaxEntries, g.MinEntries, g.CompleteEntries)
				: I18n.T("{0} / {1}  (minimum {2})", g.Players, g.MaxEntries, g.MinEntries));
			if (g.State == "running") Row(I18n.T("En cours"), (g.PhaseRaces > 0 ? I18n.T("{0} — manche {1}/{2}", PhaseLabel(g), g.PhaseRace, g.PhaseRaces) : PhaseLabel(g)) + "  ·  " + ModeLabel(g.PhaseMode));
			_right.AddChild(info);

			_right.AddChild(Section(I18n.T("Phases")));
			for (int i = 0; i < cfg.Phases.Count; i++) _right.AddChild(Text($"{i + 1}. {PhaseSummary(cfg, i)}"));

			// invitations reçues : rejoindre l'équipe d'un chef, ou refuser
			if (AccountClient.LoggedIn && g.State == "open")
				foreach (var inv in g.Invitations)
				{
					int reference = inv.Reference;
					var row = new HBoxContainer();
					row.AddThemeConstantOverride("separation", 8);
					var lbl = Text(I18n.T("✉ {0} t'invite dans son équipe.", inv.Captain));
					lbl.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.35f));
					row.AddChild(lbl);
					row.AddChild(Action(I18n.T("Accepter"), () => Act(SocialClient.TournamentActionAsync(id, "accept", reference: reference)), true));
					row.AddChild(Action(I18n.T("Refuser"), () => Act(SocialClient.TournamentActionAsync(id, "decline", reference: reference))));
					_right.AddChild(row);
				}
			if (g.Joined && g.Teams && g.MyTeam != null) BuildMyTeam(id, g);

			// actions
			var actions = new HFlowContainer();
			actions.AddThemeConstantOverride("h_separation", 8);
			actions.AddThemeConstantOverride("v_separation", 8);
			if (AccountClient.LoggedIn)
			{
				if (g.State == "open" && !g.Joined && g.RegistrationOpen) actions.AddChild(Action(g.TeamSize > 1 ? I18n.T("Inscrire une équipe (j'en serai le chef)") : I18n.T("S'inscrire"), () => Act(SocialClient.TournamentActionAsync(id, "join"))));
				if (g.State == "open" && g.Joined)
				{
					bool captain = g.MyTeam?.Captain ?? true;
					actions.AddChild(Action(!g.Teams ? I18n.T("Se désinscrire") : captain ? I18n.T("Désinscrire mon équipe") : I18n.T("Quitter l'équipe"),
						() => Confirm(g.Teams && captain ? I18n.T("Désinscrire toute ton équipe ?") : I18n.T("Te désinscrire de ce tournoi ?"), () => Act(SocialClient.TournamentActionAsync(id, "leave")))));
				}
				if (g.Joined && g.CheckInOpen && !g.CheckedIn) actions.AddChild(Action(I18n.T("✔ Confirmer ma présence"), () => Act(SocialClient.TournamentActionAsync(id, "checkin")), true));
				if (g.State == "running" && g.Joined) actions.AddChild(Action(g.Duel ? I18n.T("▶ Jouer mon match") : I18n.T("▶ Jouer ma manche"), () => Play(id), true));
				if (g.IsOwner && g.State == "open")
				{
					actions.AddChild(Action(I18n.T("Modifier"), () => OpenForm(d)));
					actions.AddChild(Action(I18n.T("Lancer maintenant"), () => Confirm(I18n.T("Lancer le tournoi maintenant ? Les inscriptions seront closes."), () => Act(SocialClient.TournamentActionAsync(id, "start")))));
				}
				if (g.IsOwner && (g.State == "open" || g.State == "running"))
					actions.AddChild(Action(I18n.T("Annuler le tournoi"), () => Confirm(I18n.T("Annuler définitivement ce tournoi ?"), () => Act(SocialClient.TournamentActionAsync(id, "cancel")))));
			}
			else _right.AddChild(Muted(I18n.T("Connecte-toi pour t'inscrire.")));
			_right.AddChild(actions);
			if (g.Joined && g.State == "open")
				_right.AddChild(Muted(g.CheckInOpen ? (g.CheckedIn ? I18n.T("Présence confirmée : rendez-vous au départ.") : I18n.T("Le check-in est ouvert : confirme ta présence avant le départ."))
					: g.CheckInMinutes > 0 ? I18n.T("Tu es inscrit. Pense à confirmer ta présence {0} min avant le départ.", g.CheckInMinutes) : I18n.T("Tu es inscrit. Un rappel s'affichera dans le menu.")));

			if (d.Standings.Count > 0)
			{
				_right.AddChild(Section(g.State == "done" ? I18n.T("Classement final") : I18n.T("Classement — {0}", PhaseLabel(g))));
				var table = new GridContainer { Columns = 3 };
				table.AddThemeConstantOverride("h_separation", 18);
				foreach (var s in d.Standings.Take(100)) { table.AddChild(Text(s.Place)); table.AddChild(Text(s.Name)); table.AddChild(Muted(DetailLabel(s.Detail))); }
				_right.AddChild(table);
			}
			_right.AddChild(Section(g.TeamSize > 1 ? I18n.T("Équipes inscrites ({0})", d.Teams.Count) : I18n.T("Inscrits ({0})", d.Teams.Count)));
			if (d.Teams.Count == 0) _right.AddChild(Muted(I18n.T("Personne pour l'instant.")));
			var names = new HFlowContainer();
			names.AddThemeConstantOverride("h_separation", 14);
			foreach (var t in d.Teams)
			{
				string extra = g.TeamSize > 1 && g.State == "open" && !t.Complete
					? "  " + I18n.T("(incomplète{0})", t.Invites.Count > 0 ? " — " + I18n.T("invités : {0}", string.Join(", ", t.Invites)) : "") : "";
				var l = Text((g.CheckInMinutes > 0 && g.State == "open" && t.CheckedIn ? "✔ " : "") + t.Name + extra);
				if (extra.Length > 0) l.AddThemeColorOverride("font_color", MenuStyle.Muted);
				names.AddChild(l);
			}
			_right.AddChild(names);
		}

		/// <summary>Mon équipe : ses joueurs, les invitations en attente ; le chef invite par nom de compte et peut retirer un joueur ou une invitation.</summary>
		private void BuildMyTeam(long id, GameTournamentDto g)
		{
			var team = g.MyTeam;
			_right.AddChild(Section(I18n.T("Mon équipe ({0}/{1})", team.Members.Count, g.TeamSize)));
			bool open = g.State == "open";
			var list = new VBoxContainer();
			list.AddThemeConstantOverride("separation", 4);
			foreach (var m in team.Members)
			{
				var row = new HBoxContainer();
				row.AddChild(Text((m == team.CaptainName ? "★ " : "• ") + m + (m == team.CaptainName ? "  " + I18n.T("(chef)") : "")));
				string who = m;
				if (open && team.Captain && m != team.CaptainName) row.AddChild(Action(I18n.T("Retirer"), () => Act(SocialClient.TournamentActionAsync(id, "kick", login: who))));
				list.AddChild(row);
			}
			foreach (var m in team.Invites)
			{
				var row = new HBoxContainer();
				var l = Text("… " + m + "  " + I18n.T("(invitation envoyée, en attente de sa réponse)"));
				l.AddThemeColorOverride("font_color", MenuStyle.Muted);
				row.AddChild(l);
				string who = m;
				if (open && team.Captain) row.AddChild(Action(I18n.T("Annuler"), () => Act(SocialClient.TournamentActionAsync(id, "kick", login: who))));
				list.AddChild(row);
			}
			_right.AddChild(list);
			int free = g.TeamSize - team.Members.Count - team.Invites.Count;
			if (open && team.Captain && free > 0)
			{
				var row = new HBoxContainer();
				row.AddThemeConstantOverride("separation", 8);
				var login = new LineEdit { PlaceholderText = I18n.T("Nom du joueur à inviter"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = 32 };
				void Send() { string who = login.Text.Trim(); if (who.Length > 0) Act(SocialClient.TournamentActionAsync(id, "invite", login: who)); }
				login.TextSubmitted += _ => Send();
				row.AddChild(login);
				row.AddChild(Action(I18n.T("Inviter"), Send, true));
				_right.AddChild(row);
				// les amis en ligne, en un clic
				var friends = SocialClient.Friends.Friends.Where(f => f.State == "friend" && !team.Members.Contains(f.Login) && !team.Invites.Contains(f.Login)).Take(8).ToList();
				if (friends.Count > 0)
				{
					var flow = new HFlowContainer();
					flow.AddThemeConstantOverride("h_separation", 6);
					flow.AddChild(Muted(I18n.T("Amis :")));
					foreach (var f in friends) { string who = f.Login; flow.AddChild(Action((f.Online ? "● " : "") + who, () => Act(SocialClient.TournamentActionAsync(id, "invite", login: who)))); }
					_right.AddChild(flow);
				}
			}
			if (open && !team.Complete)
				_right.AddChild(Warn(team.Captain
					? I18n.T("Ton équipe est incomplète : invite encore {0} joueur(s). Chacun doit accepter l'invitation dans son jeu. Une équipe incomplète au départ est retirée.", g.TeamSize - team.Members.Count)
					: I18n.T("L'équipe de {0} est incomplète : une équipe incomplète au départ est retirée.", team.CaptainName)));
		}

		/// <summary>Nom de la phase en cours : celui de l'organisateur, sinon « Poules », « Tableau », « Finale », « Manches » ou « Phase n ».</summary>
		public static string PhaseLabel(GameTournamentDto g)
			=> g.PhaseName != null && g.PhaseName.StartsWith("Phase ") && int.TryParse(g.PhaseName.AsSpan(6), out int n) ? I18n.T("Phase {0}", n) : I18n.T(g.PhaseName ?? "");

		/// <summary>Détail d'une ligne de classement (« 24 pts », « 24 pts · lobby 2 »).</summary>
		private static string DetailLabel(string detail)
			=> detail != null && detail.EndsWith(" pts") && int.TryParse(detail.AsSpan(0, detail.Length - 4), out int n) ? I18n.T("{0} pts", n) : I18n.Server(detail ?? "");

		/// <summary>Mode d'une phase (anciens tournois : lobby = battle royale, poules et tableau = match à mort).</summary>
		private static string ModeOf(PhaseConfigDto p) => string.IsNullOrEmpty(p.Mode) ? (p.Kind == "lobby" ? "br" : "tdm") : p.Mode;

		public static string ModeLabel(string mode) => mode switch
		{
			"ctf" => I18n.T("Capture du drapeau"),
			"1v1" => I18n.T("1 VS 1"),
			"tdm" => I18n.T("Match à mort par équipe"),
			"br" => I18n.T("Battle Royale"),
			_ => "",
		};

		/// <summary>Ce qu'il faut pour gagner un match, selon le mode.</summary>
		private static string ScoreText(string mode, int score) => mode switch
		{
			"1v1" => I18n.T("match en {0} manche(s) gagnée(s)", score),
			"ctf" => I18n.T("match en {0} prise(s)", score),
			_ => I18n.T("match en {0} victimes", score),
		};

		/// <summary>Résumé d'une phase : « Phase 1 · Battle Royale · 5 manches · lobbys de 20 · points 12/9/7… · 20 qualifiés au total ».</summary>
		private static string PhaseSummary(TournamentConfigDto cfg, int i)
		{
			var p = cfg.Phases[i];
			bool last = i == cfg.Phases.Count - 1;
			string mode = ModeOf(p);
			string name = !string.IsNullOrWhiteSpace(p.Name) ? p.Name : p.Kind == "pools" ? I18n.T("Poules") : p.Kind == "table" ? I18n.T("Tableau") : cfg.Phases.Count > 1 && last ? I18n.T("Finale") : cfg.Phases.Count > 1 ? I18n.T("Phase {0}", i + 1) : I18n.T("Manches");
			var parts = new List<string> { name, ModeLabel(mode) };
			if ((mode == "br" || mode == "ctf") && p.Teams == "random" && p.TeamSize > 1) parts.Add(I18n.T("équipes de {0} tirées au sort à chaque manche", p.TeamSize));
			if (p.Kind == "lobby")
			{
				parts.Add(mode == "ctf" ? I18n.T("{0} match(s)", p.Races) : I18n.T("{0} manche(s)", p.Races));
				if (mode != "ctf") parts.Add(I18n.T("lobbys de {0}", p.LobbySize));
				if (mode == "ctf") parts.Add(ScoreText(mode, p.MaxScore));
				parts.Add(p.Distribution == "level" ? I18n.T("divisions par niveau") : I18n.T("lobbys équilibrés"));
				parts.Add(I18n.T("points {0}", string.Join("/", p.Scale.Take(mode == "ctf" ? 2 : 8)) + (mode != "ctf" && p.Scale.Count > 8 ? "…" : "")));
				if (p.AbandonPoints > 0) parts.Add(I18n.T("abandon {0} pt", p.AbandonPoints));
			}
			else
			{
				parts.Add(p.Kind == "pools" ? I18n.T("chacun contre chacun") : I18n.T("élimination directe"));
				parts.Add(ScoreText(mode, p.MaxScore));
				if (mode == "1v1") parts.Add(p.Loadout == "choice" ? I18n.T("équipement choisi") : p.Loadout.StartsWith("fixed:") ? I18n.T("équipement imposé") : I18n.T("équipement au hasard"));
				if (p.Kind == "table") parts.Add(p.Places == "all" ? I18n.T("toutes les places disputées") : p.Places == "none" ? I18n.T("pas de petite finale") : I18n.T("match pour la 3e place"));
			}
			if (!last && p.Qualified > 0) parts.Add(p.QualifyMode == "lobby" ? I18n.T("{0} qualifiés par {1}", p.Qualified, p.Kind == "pools" ? I18n.T("poule") : I18n.T("lobby")) : I18n.T("{0} qualifiés au total", p.Qualified));
			else if (!last) parts.Add(I18n.T("tout le monde passe"));
			return string.Join("  ·  ", parts);
		}

		private async void Act(System.Threading.Tasks.Task<string> task)
		{
			string error = await task;
			if (!IsInstanceValid(this)) return;
			Say(error);
			Reload();
		}

		private void Play(long id) => PlayTournament(this, id, Say);

		/// <summary>Cherche la partie de sa manche ou de son match : le matchmaking réunit les joueurs sur un serveur réservé.</summary>
		public static async void PlayTournament(Node from, long id, Action<string> onError)
		{
			var (error, tag, mode, teamSize) = await SocialClient.MyRaceAsync(id);
			if (error != null) { onError?.Invoke(I18n.T(error)); return; }
			if (!GodotObject.IsInstanceValid(from)) return;
			from.GetTree().Root.GetNodeOrNull("TournamentsScreen")?.QueueFree();
			var screen = new MatchmakingScreen { Name = "MatchmakingScreen", Tournament = tag, TournamentTeamSize = teamSize, TournamentMode = mode };
			screen.OnFound = (h, p) => SocialClient.JoinPartyGame?.Invoke(h, p, mode, teamSize);
			from.GetTree().Root.AddChild(screen);
		}

		private void Confirm(string text, Action yes)
		{
			var dialog = new ConfirmationDialog { DialogText = text, OkButtonText = I18n.T("Oui"), CancelButtonText = I18n.T("Non") };
			dialog.Confirmed += () => { yes(); dialog.QueueFree(); };
			dialog.Canceled += dialog.QueueFree;
			AddChild(dialog);
			dialog.PopupCentered(new Vector2I(460, 140));
		}

		// ------------------------------------------------------------------ création et modification

		private void OpenForm(TournamentDetailsDto existing)
		{
			_editing = existing?.Summary?.Id ?? 0;
			if (existing?.Config != null) _form = existing.Config;
			else
			{
				var start = DateTime.Now.AddDays(1).Date.AddHours(20);
				_form = new TournamentConfigDto { StartAt = new DateTimeOffset(start).ToUnixTimeSeconds(), MinEntries = 2, MaxEntries = 60, CheckInMinutes = 15 };
				_form.Phases.Add(new PhaseConfigDto { Kind = "lobby", Mode = "br", Races = 5, LobbySize = 20 });
			}
			Say("");
			BuildForm();
		}

		private void BuildForm()
		{
			Clear();
			var f = _form;
			var title = new Label { Text = _editing == 0 ? I18n.T("Nouveau tournoi") : I18n.T("Modifier le tournoi") };
			title.AddThemeFontSizeOverride("font_size", 26);
			_right.AddChild(title);

			// ---- général
			_right.AddChild(Section(I18n.T("Informations générales")));
			var grid = Grid();
			var name = new LineEdit { Text = f.Title, PlaceholderText = I18n.T("Nom du tournoi (3 à 60 caractères)"), MaxLength = 60, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			name.TextChanged += t => f.Title = t;
			Field(grid, I18n.T("Nom"), name);
			var desc = new TextEdit { Text = f.Description, PlaceholderText = I18n.T("Règlement, récompenses, lien de diffusion… (facultatif)"), CustomMinimumSize = new Vector2(0, 70), WrapMode = TextEdit.LineWrappingMode.Boundary };
			desc.TextChanged += () => f.Description = desc.Text.Length > 500 ? desc.Text[..500] : desc.Text;
			Field(grid, I18n.T("Description"), desc);
			var team = Options(Registration.Select(I18n.T).ToArray(), Math.Clamp(f.TeamSize - 1, 0, Registration.Length - 1), i =>
			{
				f.TeamSize = i + 1;
				// par équipes : pas de 1 VS 1 ni d'équipes tirées au sort (elles se forment avec des inscrits en solo)
				if (f.TeamSize > 1) foreach (var p in f.Phases) { p.Teams = "entry"; if (ModeOf(p) == "1v1") { p.Mode = "ctf"; } }
				CallDeferred(nameof(BuildForm));
			});
			team.Disabled = _editing != 0 && (SocialClient.Tournaments.Games.FirstOrDefault(g => g.Id == _editing)?.Players ?? 0) > 0;
			Field(grid, I18n.T("Inscription"), team);
			if (_editing == 0)
			{
				var me = new CheckBox { Text = f.TeamSize > 1 ? I18n.T("Je participe aussi (je serai chef de mon équipe)") : I18n.T("Je participe aussi"), ButtonPressed = _participate };
				me.Toggled += on => _participate = on;
				Field(grid, "", me);
			}
			Field(grid, "", Muted(f.TeamSize > 1
				? I18n.T("Par équipes : le chef inscrit son équipe puis invite ses coéquipiers depuis la page du tournoi ; chacun voit l'invitation dans son jeu et l'accepte ou la refuse. Une équipe incomplète au départ est retirée.")
				: I18n.T("En solo, une phase peut tirer des équipes au sort à chaque manche (Battle Royale en duos ou trios, Capture du drapeau).")));
			_right.AddChild(grid);

			// ---- participants
			_right.AddChild(Section(f.TeamSize > 1 ? I18n.T("Équipes inscrites") : I18n.T("Participants")));
			grid = Grid();
			Field(grid, I18n.T("Minimum pour lancer"), Spin(2, 100, f.MinEntries, v => f.MinEntries = v));
			Field(grid, I18n.T("Maximum"), Spin(2, 100, f.MaxEntries, v => f.MaxEntries = v));
			_right.AddChild(grid);

			// ---- calendrier
			_right.AddChild(Section(I18n.T("Calendrier")));
			grid = Grid();
			Field(grid, I18n.T("Départ"), Options(new[] { I18n.T("À heure fixe (lancement automatique)"), I18n.T("Lancé par l'organisateur") }, f.StartMode == "manual" ? 1 : 0, i =>
			{
				f.StartMode = i == 1 ? "manual" : "fixed";
				_checkInRow.Visible = f.StartMode == "fixed";
			}));
			_startField = DateField(f.StartAt, false);
			Field(grid, I18n.T("Date et heure"), _startField);
			_opensField = DateField(f.RegistrationOpensAt, true, I18n.T("dès maintenant"));
			Field(grid, I18n.T("Ouverture des inscriptions"), _opensField);
			_closesField = DateField(f.RegistrationClosesAt, true, I18n.T("au départ"));
			Field(grid, I18n.T("Clôture des inscriptions"), _closesField);
			var checkIn = Options(CheckInChoices.Select(m => m == 0 ? I18n.T("Pas de check-in") : I18n.T("{0} min avant le départ", m)).ToArray(), Math.Max(0, Array.IndexOf(CheckInChoices, f.CheckInMinutes)), i => f.CheckInMinutes = CheckInChoices[i]);
			_checkInRow = Field(grid, I18n.T("Check-in"), checkIn);
			_checkInRow.Visible = f.StartMode == "fixed";
			checkIn.Visible = f.StartMode == "fixed";
			_right.AddChild(grid);
			_right.AddChild(Muted(I18n.T("Format des dates : JJ/MM/AAAA HH:MM (heure de ton ordinateur). Inscriptions possibles jusqu'à un an à l'avance. À heure fixe, le tournoi est annulé si le minimum d'inscrits présents n'est pas atteint ; le check-in retire les inscrits qui n'ont pas confirmé leur présence.")));

			// ---- phases
			_right.AddChild(Section(I18n.T("Phases")));
			_phasesBox = new VBoxContainer();
			_phasesBox.AddThemeConstantOverride("separation", 10);
			_right.AddChild(_phasesBox);
			BuildPhases();
			var add = new HBoxContainer();
			add.AddThemeConstantOverride("separation", 8);
			add.AddChild(Action(I18n.T("+ Battle Royale"), () => AddPhase(new PhaseConfigDto { Kind = "lobby", Mode = "br", Races = 3, LobbySize = Math.Min(20, 30 / Math.Max(1, f.TeamSize)) })));
			add.AddChild(Action(I18n.T("+ Capture du drapeau"), () => AddPhase(new PhaseConfigDto { Kind = "lobby", Mode = "ctf", Races = 3, MaxScore = 3, Teams = f.TeamSize > 1 ? "entry" : "random", TeamSize = 4, Scale = new List<int> { 3, 0 } })));
			if (f.TeamSize == 1) add.AddChild(Action(I18n.T("+ 1 VS 1"), () => AddPhase(new PhaseConfigDto { Kind = "pools", Mode = "1v1", MaxScore = 3 })));
			_right.AddChild(add);
			_right.AddChild(Muted(I18n.T("Chaque phase a son mode de jeu. Séries de manches : les points par place s'additionnent manche après manche (en Capture du drapeau, chaque manche est un match : 1re place au vainqueur, 2e au perdant). Répartition « par niveau » : les meilleurs ensemble dans le lobby 1, les suivants dans le lobby 2… (divisions). Poules : chacun contre chacun ; tableau : élimination directe, toujours en dernier. Les qualifiés d'une phase passent à la suivante (sélections).")));

			// ---- validation
			var buttons = new HBoxContainer();
			buttons.AddThemeConstantOverride("separation", 10);
			buttons.AddChild(Action(_editing == 0 ? I18n.T("Créer le tournoi") : I18n.T("Enregistrer"), Submit, true));
			buttons.AddChild(Action(I18n.T("Annuler"), () => { _form = null; ShowDetails(_selected); }));
			_right.AddChild(buttons);
		}

		private void AddPhase(PhaseConfigDto p)
		{
			if (_form.Phases.Count >= MaxPhases) { Say(I18n.T("Un tournoi compte au plus {0} phases.", MaxPhases)); return; }
			int table = _form.Phases.FindIndex(x => x.Kind == "table");
			if (table >= 0) { _form.Phases.Insert(table, p); BuildPhases(); return; }      // le tableau reste à la fin
			_form.Phases.Add(p);
			BuildPhases();
		}

		private void BuildPhases()
		{
			foreach (var c in _phasesBox.GetChildren()) c.QueueFree();
			int entry = _form.TeamSize;
			for (int i = 0; i < _form.Phases.Count; i++)
			{
				var p = _form.Phases[i];
				p.Mode = ModeOf(p);
				int index = i;
				bool last = i == _form.Phases.Count - 1;
				var box = new VBoxContainer();
				box.AddThemeConstantOverride("separation", 6);
				var head = new HBoxContainer();
				var label = new Label { Text = $"{i + 1}. {ModeLabel(p.Mode)} — " + (p.Kind == "pools" ? I18n.T("Poules") : p.Kind == "table" ? I18n.T("Tableau (élimination directe)") : p.Mode == "ctf" ? I18n.T("Série de matchs") : I18n.T("Série de manches")), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				label.AddThemeFontSizeOverride("font_size", 18);
				head.AddChild(label);
				if (i > 0) head.AddChild(Action("▲", () => { _form.Phases.RemoveAt(index); _form.Phases.Insert(index - 1, p); BuildPhases(); }));
				if (!last) head.AddChild(Action("▼", () => { _form.Phases.RemoveAt(index); _form.Phases.Insert(index + 1, p); BuildPhases(); }));
				if (_form.Phases.Count > 1) head.AddChild(Action(I18n.T("Retirer"), () => { _form.Phases.RemoveAt(index); BuildPhases(); }));
				box.AddChild(head);
				var grid = Grid();
				var name = new LineEdit { Text = p.Name, PlaceholderText = I18n.T("Nom (facultatif : Qualifications, Finale, Division 1…)"), MaxLength = 40, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				name.TextChanged += t => p.Name = t;
				Field(grid, I18n.T("Nom"), name);

				// mode de jeu (le 1 VS 1 demande une inscription en solo)
				var modes = new List<string> { "br", "ctf" };
				if (entry == 1 || p.Mode == "1v1") modes.Add("1v1");
				if (p.Mode == "tdm") modes.Add("tdm");
				Field(grid, I18n.T("Mode de jeu"), Options(modes.Select(ModeLabel).ToArray(), Math.Max(0, modes.IndexOf(p.Mode)), j =>
				{
					p.Mode = modes[j];
					if (p.Mode == "br") { p.Kind = "lobby"; p.Scale = new List<int> { 12, 9, 7, 5, 4, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1 }; p.LobbySize = Math.Min(20, 30 / Math.Max(1, entry)); }
					else if (p.Mode == "1v1") { if (p.Kind == "lobby") p.Kind = "pools"; p.MaxScore = 3; p.Teams = "entry"; }
					else { p.MaxScore = 3; p.Scale = new List<int> { 3, 0 }; if (entry == 1) { p.Teams = "random"; p.TeamSize = Math.Max(2, p.TeamSize); } }
					CallDeferred(nameof(BuildPhases));
				}));

				// déroulement
				var kinds = p.Mode == "br" ? new List<string> { "lobby" } : p.Mode == "1v1" || p.Mode == "tdm" ? new List<string> { "pools", "table" }
					: p.Teams == "random" && entry == 1 ? new List<string> { "lobby" } : new List<string> { "lobby", "pools", "table" };
				if (!kinds.Contains(p.Kind)) p.Kind = kinds[0];
				if (kinds.Count > 1)
					Field(grid, I18n.T("Déroulement"), Options(kinds.Select(k => k == "lobby" ? I18n.T("Série de matchs (points par place)") : k == "pools" ? I18n.T("Poules (chacun contre chacun)") : I18n.T("Tableau (élimination directe)")).ToArray(), kinds.IndexOf(p.Kind), j =>
					{
						p.Kind = kinds[j];
						if (p.Kind == "table" && !last) Say(I18n.T("Le tableau doit être la dernière phase : déplace-le à la fin."));
						CallDeferred(nameof(BuildPhases));
					}));

				// équipes : celles de l'inscription, ou tirées au sort (inscription en solo)
				if ((p.Mode == "br" || p.Mode == "ctf") && entry == 1)
				{
					var teamChoices = p.Mode == "br" ? new[] { I18n.T("Chacun pour soi"), I18n.T("Équipes tirées au sort à chaque manche") } : new[] { I18n.T("Équipes tirées au sort à chaque match") };
					if (p.Mode == "br")
						Field(grid, I18n.T("Équipes"), Options(teamChoices, p.Teams == "random" ? 1 : 0, j => { p.Teams = j == 1 ? "random" : "entry"; if (j == 1) p.TeamSize = Math.Max(2, p.TeamSize); CallDeferred(nameof(BuildPhases)); }));
					else { p.Teams = "random"; Field(grid, I18n.T("Équipes"), Muted(teamChoices[0])); }
					if (p.Teams == "random")
						Field(grid, I18n.T("Joueurs par équipe tirée"), Spin(2, 5, p.TeamSize, v => p.TeamSize = v));
				}
				else if (p.Mode != "1v1") { p.Teams = "entry"; Field(grid, I18n.T("Équipes"), Muted(entry > 1 ? I18n.T("Les équipes inscrites ({0} joueurs)", entry) : I18n.T("Chacun pour soi"))); }

				if (p.Kind == "lobby")
				{
					Field(grid, p.Mode == "ctf" ? I18n.T("Matchs") : I18n.T("Manches"), Spin(1, 10, p.Races, v => p.Races = v));
					if (p.Mode == "br")
					{
						int maxLobby = Math.Max(2, 30 / Math.Max(1, entry));
						Field(grid, entry > 1 ? I18n.T("Équipes par lobby") : I18n.T("Joueurs par lobby"), Spin(2, maxLobby, p.LobbySize, v => p.LobbySize = v));
					}
					else Field(grid, I18n.T("Lobby"), Muted(I18n.T("un match à deux équipes par lobby (une équipe seule est exemptée et gagne)")));
					if (p.Mode == "ctf") Field(grid, I18n.T("Prises pour gagner un match"), Spin(1, 10, p.MaxScore, v => p.MaxScore = v));
					var scale = new LineEdit { Text = string.Join(", ", p.Scale), PlaceholderText = p.Mode == "ctf" ? "3, 0" : "12, 9, 7, 5, 4, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
					scale.TextChanged += t => p.Scale = ParseScale(t);
					Field(grid, p.Mode == "ctf" ? I18n.T("Points (vainqueur, perdant)") : I18n.T("Points par place (1er, 2e…)"), scale);
					Field(grid, I18n.T("Points d'un abandon"), Spin(0, 100, p.AbandonPoints, v => p.AbandonPoints = v));
					Field(grid, I18n.T("Répartition"), Options(new[] { I18n.T("Équilibrée (serpentin)"), I18n.T("Par niveau (divisions)") }, p.Distribution == "level" ? 1 : 0, j => p.Distribution = j == 1 ? "level" : "balanced"));
				}
				else
				{
					if (p.Mode == "1v1") Field(grid, I18n.T("Manches pour gagner un match"), Spin(1, 10, p.MaxScore, v => p.MaxScore = v));
					else if (p.Mode == "ctf") Field(grid, I18n.T("Prises pour gagner un match"), Spin(1, 10, p.MaxScore, v => p.MaxScore = v));
					else Field(grid, I18n.T("Victimes pour gagner un match"), Spin(3, 30, p.MaxScore, v => p.MaxScore = v));
					if (p.Mode == "1v1") LoadoutFields(grid, p);
					if (p.Kind == "table") Field(grid, I18n.T("Places disputées"), Options(new[] { I18n.T("Match pour la 3e place"), I18n.T("Toutes les places"), I18n.T("Aucune (vainqueur seulement)") }, p.Places == "all" ? 1 : p.Places == "none" ? 2 : 0, j => p.Places = j == 1 ? "all" : j == 2 ? "none" : "third"));
				}
				if (!last)
				{
					Field(grid, I18n.T("Qualifiés pour la suite (0 = tous)"), Spin(0, 100, p.Qualified, v => p.Qualified = v));
					Field(grid, I18n.T("Qualification"), Options(new[] { I18n.T("Au total (classement fusionné)"), p.Kind == "pools" ? I18n.T("Par poule") : I18n.T("Par lobby") }, p.QualifyMode == "lobby" ? 1 : 0, j => p.QualifyMode = j == 1 ? "lobby" : "total"));
				}
				box.AddChild(grid);
				var card = MenuStyle.Card(box, margin: 12);
				_phasesBox.AddChild(card);
			}
		}

		/// <summary>1 VS 1 : équipement au hasard à chaque manche, choisi par chacun au début, ou imposé (deux armes avec leur viseur, une grenade, un soin).</summary>
		private void LoadoutFields(GridContainer grid, PhaseConfigDto p)
		{
			string policy = p.Loadout == "choice" ? "choice" : (p.Loadout ?? "").StartsWith("fixed:") ? "fixed" : "random";
			Field(grid, I18n.T("Équipement"), Options(new[] { I18n.T("Au hasard à chaque manche (le même pour les deux)"), I18n.T("Choisi par chaque joueur au début"), I18n.T("Imposé") }, policy == "choice" ? 1 : policy == "fixed" ? 2 : 0, j =>
			{
				p.Loadout = j == 1 ? "choice" : j == 2 ? "fixed:" + new OpenEsport.Core.DuelLoadout(3, 3, 2, 1, 1, 0) : "random";
				CallDeferred(nameof(BuildPhases));
			}));
			if (policy != "fixed") return;
			if (!OpenEsport.Core.DuelLoadout.TryParse(p.Loadout.Substring(6), out var l)) l = new OpenEsport.Core.DuelLoadout(3, 3, 2, 1, 1, 0);
			var flow = new HFlowContainer();
			flow.AddThemeConstantOverride("h_separation", 6);
			flow.AddThemeConstantOverride("v_separation", 6);
			int[] values = { l.Weapon, l.Optic, l.Weapon2, l.Optic2, l.Grenade, l.Heal };
			var lists = new[] { OpenEsport.Core.DuelRules.Weapons, OpenEsport.Core.DuelRules.Optics, OpenEsport.Core.DuelRules.Weapons, OpenEsport.Core.DuelRules.Optics, OpenEsport.Core.DuelRules.Grenades, OpenEsport.Core.DuelRules.Heals };
			for (int k = 0; k < lists.Length; k++)
			{
				int slot = k;
				flow.AddChild(Options(lists[k].Select(c => I18n.T(c.Label)).ToArray(), values[k], j =>
				{
					values[slot] = j;
					p.Loadout = "fixed:" + new OpenEsport.Core.DuelLoadout(values[0], values[1], values[2], values[3], values[4], values[5]);
				}));
			}
			Field(grid, I18n.T("Arme 1, viseur, arme 2, viseur, grenade, soin"), flow);
		}

		private static List<int> ParseScale(string text)
		{
			var list = new List<int>();
			foreach (var part in text.Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries))
				if (int.TryParse(part, out int v)) list.Add(Math.Clamp(v, 0, 100));
			return list;
		}

		private async void Submit()
		{
			var f = _form;
			if (!ReadDate(_startField.Text, false, out long start)) { Say(I18n.T("Date de départ invalide : JJ/MM/AAAA HH:MM.")); return; }
			if (!ReadDate(_opensField.Text, true, out long opens)) { Say(I18n.T("Date d'ouverture des inscriptions invalide.")); return; }
			if (!ReadDate(_closesField.Text, true, out long closes)) { Say(I18n.T("Date de clôture des inscriptions invalide.")); return; }
			f.StartAt = start; f.RegistrationOpensAt = opens; f.RegistrationClosesAt = closes;
			if (f.StartMode == "manual") f.CheckInMinutes = 0;
			Say(I18n.T("Enregistrement…"));
			if (_editing == 0)
			{
				var (error, id) = await SocialClient.CreateTournamentAsync(f, _participate);
				if (!IsInstanceValid(this)) return;
				if (error != null) { Say(I18n.T(error)); return; }
				_selected = id;
				_filter.Select(1);
				Say(I18n.T("Tournoi créé : partage-le pour que les joueurs s'inscrivent !"));
			}
			else
			{
				string error = await SocialClient.UpdateTournamentAsync(_editing, f);
				if (!IsInstanceValid(this)) return;
				if (error != null) { Say(I18n.T(error)); return; }
				Say(I18n.T("Modifications enregistrées."));
			}
			_form = null;
			Reload();
		}

		// ------------------------------------------------------------------ outils d'affichage

		private void Clear() { foreach (var c in _right.GetChildren()) c.QueueFree(); }

		private static string When(long unix) => unix <= 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd/MM HH:mm");
		private static string Full(long unix) => unix <= 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd/MM/yyyy HH:mm");

		/// <summary>« dans 3 j 4 h », « dans 25 min », « commencé ».</summary>
		public static string Countdown(long unix)
		{
			long s = unix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			if (s <= 0) return I18n.T("maintenant");
			if (s < 3600) return I18n.T("dans {0} min", Math.Max(1, s / 60));
			if (s < 86400) return I18n.T("dans {0} h {1:00}", s / 3600, s % 3600 / 60);
			return I18n.T("dans {0} j {1} h", s / 86400, s % 86400 / 3600);
		}

		/// <summary>« Battle Royale → 1 VS 1 · solo », « Capture du drapeau · équipes de 4 ».</summary>
		public static string FormatLabel(GameTournamentDto g)
		{
			var modes = g.Modes != null && g.Modes.Count > 0 ? g.Modes : new List<string> { g.Mode == "duel" ? "tdm" : "br" };
			return string.Join(" → ", modes.Select(ModeLabel)) + "  ·  " + I18n.T(Registration[Math.Clamp(g.TeamSize - 1, 0, Registration.Length - 1)]);
		}

		public static string StateLabel(GameTournamentDto g) => g.State switch
		{
			"running" => I18n.T("en cours"),
			"done" => I18n.T("terminé"),
			"cancelled" => I18n.T("annulé"),
			_ => g.RegistrationOpen ? I18n.T("inscriptions ouvertes ({0}/{1})", g.Players, g.MaxEntries) : I18n.T("inscriptions closes ({0})", g.Players),
		};

		private static bool ReadDate(string text, bool optional, out long unix)
		{
			unix = 0;
			text = (text ?? "").Trim();
			if (text.Length == 0) return optional;
			if (!DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return false;
			unix = new DateTimeOffset(d).ToUnixTimeSeconds();
			return true;
		}

		private static LineEdit DateField(long unix, bool optional, string placeholder = null) => new LineEdit
		{
			Text = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString(DateFormat) : "",
			PlaceholderText = placeholder ?? I18n.T("JJ/MM/AAAA HH:MM"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};

		private static GridContainer Grid()
		{
			var g = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			g.AddThemeConstantOverride("h_separation", 16);
			g.AddThemeConstantOverride("v_separation", 8);
			return g;
		}

		/// <summary>Ajoute une ligne « libellé : champ » ; retourne le libellé (pour masquer la ligne).</summary>
		private static Control Field(GridContainer grid, string label, Control field)
		{
			var l = new Label { Text = label, CustomMinimumSize = new Vector2(240, 0), VerticalAlignment = VerticalAlignment.Center };
			grid.AddChild(l);
			field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			grid.AddChild(field);
			field.VisibilityChanged += () => l.Visible = field.Visible;
			return l;
		}

		private static SpinBox Spin(int min, int max, int value, Action<int> changed)
		{
			var s = new SpinBox { MinValue = min, MaxValue = max, Value = Math.Clamp(value, min, max), Rounded = true };
			changed((int)s.Value);
			s.ValueChanged += v => changed((int)v);
			return s;
		}

		private static OptionButton Options(string[] items, int selected, Action<int> changed)
		{
			var o = new OptionButton();
			foreach (var it in items) o.AddItem(it);
			o.Select(Math.Clamp(selected, 0, items.Length - 1));
			o.ItemSelected += i => changed((int)i);
			return o;
		}

		private static Button Action(string text, Action pressed, bool strong = false)
		{
			var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 38) };
			if (strong) b.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.35f));
			b.Pressed += pressed;
			return b;
		}

		private static Label Section(string text)
		{
			var l = new Label { Text = text };
			l.AddThemeFontSizeOverride("font_size", 20);
			return l;
		}

		private static Label Text(string text) => new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

		private static Label Muted(string text)
		{
			var l = Text(text);
			l.AddThemeColorOverride("font_color", MenuStyle.Muted);
			l.AddThemeFontSizeOverride("font_size", 14);
			return l;
		}

		private static Label Warn(string text)
		{
			var l = Text(text);
			l.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.45f));
			return l;
		}
	}
}
