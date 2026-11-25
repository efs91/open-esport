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
	/// Page « Tournois » : la liste (à venir, mes tournois, en cours, terminés) et, à droite, le tournoi choisi — ce qu'il faut savoir, les actions, les équipes
	/// et invitations, et son déroulé étape par étape avec les résultats. La création (modèles, réglages à gauche, déroulé à droite) est dans
	/// <c>TournamentsScreen.Editor.cs</c>.
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
		private const int MaxPhases = 8;
		private static readonly int[] CheckInChoices = { 0, 5, 10, 15, 30, 60 };

		private OptionButton _filter;
		private VBoxContainer _list, _right;
		private Control _browser, _editor;
		private Label _title, _message;
		private Button _create;
		private long _selected, _shown;
		private double _refresh;
		private bool _busy;
		/// <summary>Étapes dépliées ou repliées à la main sur la page d'un tournoi (tournoi, étape).</summary>
		private readonly Dictionary<(long, int), bool> _open = new Dictionary<(long, int), bool>();

		public TournamentsScreen() { Layer = 115; }

		public override void _Ready()
		{
			AddChild(MenuStyle.Backdrop());
			var margin = new MarginContainer { Theme = MenuStyle.SharedTheme };
			margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
			AddChild(margin);
			var page = new VBoxContainer();
			page.AddThemeConstantOverride("separation", 8);
			margin.AddChild(MenuStyle.Card(page, margin: 14));

			var head = new HBoxContainer();
			head.AddThemeConstantOverride("separation", 12);
			_title = new Label { Text = I18n.T("Tournois"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
			_title.AddThemeFontSizeOverride("font_size", 26);
			head.AddChild(_title);
			_create = new Button { Text = I18n.T("+ Créer un tournoi"), CustomMinimumSize = new Vector2(200, 40), Visible = AccountClient.LoggedIn };
			_create.Pressed += ChooseTemplate;
			head.AddChild(_create);
			var close = new Button { Text = I18n.T("Retour"), CustomMinimumSize = new Vector2(120, 40) };
			close.Pressed += Back;
			head.AddChild(close);
			page.AddChild(head);

			_message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
			_message.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.4f));
			page.AddChild(_message);

			var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			body.AddThemeConstantOverride("separation", 14);
			page.AddChild(body);
			_browser = body;

			var left = new VBoxContainer { CustomMinimumSize = new Vector2(310, 0) };
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

			_right = Scroller(body);

			_selected = Focus;
			if (StartWithCreate && AccountClient.LoggedIn) ChooseTemplate();
			Reload();
		}

		/// <summary>Colonne à défilement vertical ajoutée à <paramref name="parent"/> ; retourne son contenu.</summary>
		private static VBoxContainer Scroller(Control parent, float width = 0)
		{
			var scroll = new ScrollContainer { SizeFlagsHorizontal = width > 0 ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(width, 0) };
			var content = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			content.AddThemeConstantOverride("margin_right", 12);
			var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			box.AddThemeConstantOverride("separation", 8);
			content.AddChild(box);
			scroll.AddChild(content);
			parent.AddChild(scroll);
			return box;
		}

		public override void _Process(double delta)
		{
			_refresh += delta;
			if (_refresh >= 20 && !_busy && _editor == null) { _refresh = 0; Reload(); }
		}

		public override void _Input(InputEvent e)
		{
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { Back(); GetViewport().SetInputAsHandled(); }
		}

		private void Close() { Closed?.Invoke(); QueueFree(); }

		/// <summary>Retour : de la page de création à la liste, de la liste au menu.</summary>
		private void Back() { if (_editor != null) CloseEditor(); else Close(); }

		private void Say(string text) { _message.Text = text ?? ""; _message.Visible = _message.Text.Length > 0; }

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
				if (_editor == null) ShowDetails(_selected);
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
			if (_selected == 0) _selected = items[0].Id;
			var group = new ButtonGroup();
			foreach (var g in items)
			{
				string mine = g.Joined ? "★ " : "";
				var b = new Button
				{
					Text = $"{mine}{(g.Invitations.Count > 0 ? "✉ " : "")}{FullTitle(g)}\n{When(g.StartAt)}  ·  {FormatLabel(g)}\n{StateLabel(g)}",
					ToggleMode = true, ButtonGroup = group, ButtonPressed = g.Id == _selected, Alignment = HorizontalAlignment.Left,
					CustomMinimumSize = new Vector2(0, 74), ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				};
				long id = g.Id;
				b.AddThemeFontSizeOverride("font_size", 14);
				b.Pressed += () => { _selected = id; Say(""); ShowDetails(id); };
				_list.AddChild(b);
			}
		}

		// ------------------------------------------------------------------ détail

		private async void ShowDetails(long id)
		{
			if (id == 0) { Clear(); _right.AddChild(Muted(I18n.T("Choisis un tournoi dans la liste, ou crée le tien."))); return; }
			var (error, d) = await SocialClient.TournamentDetailsAsync(id);
			if (!IsInstanceValid(this) || _editor != null || _selected != id) return;
			// un autre tournoi s'affiche depuis le haut ; le même garde sa position
			if (_shown != id && _right.GetParent().GetParent() is ScrollContainer scroll) scroll.ScrollVertical = 0;
			_shown = id;
			Clear();
			if (error != null || d?.Summary == null) { _right.AddChild(Muted(error ?? I18n.T("Tournoi introuvable."))); return; }
			var g = d.Summary;
			var cfg = d.Config ?? new TournamentConfigDto();

			var title = new Label { Text = FullTitle(g), AutowrapMode = TextServer.AutowrapMode.WordSmart };
			title.AddThemeFontSizeOverride("font_size", 24);
			_right.AddChild(title);
			// les divisions d'un tournoi séparé : un bouton chacune
			if (d.Divisions.Count > 1)
			{
				var tabs = new HFlowContainer();
				tabs.AddThemeConstantOverride("h_separation", 6);
				foreach (var v in d.Divisions)
				{
					long other = v.Id;
					tabs.AddChild(TournamentFlow.Chip((v.Mine ? "★ " : "") + I18n.T("Division {0}", v.Division), other == id, MenuStyle.Cyan, () => { _selected = other; ShowDetails(other); RebuildList(); }));
				}
				_right.AddChild(tabs);
			}
			if (!string.IsNullOrWhiteSpace(g.Description)) _right.AddChild(Text(g.Description));
			if (!string.IsNullOrWhiteSpace(g.Note)) _right.AddChild(Warn(I18n.Server(g.Note)));

			var info = new GridContainer { Columns = 2 };
			info.AddThemeConstantOverride("h_separation", 18);
			info.AddThemeConstantOverride("v_separation", 2);
			void Row(string k, string v) { var key = Muted(k); key.CustomMinimumSize = new Vector2(120, 0); key.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin; info.AddChild(key); info.AddChild(TournamentFlow.Line(v, 15)); }
			Row(I18n.T("Format"), FormatLabel(g));
			Row(I18n.T("Organisateur"), g.Owner + "  ·  " + StateLabel(g));
			if (g.State == "open")
			{
				Row(I18n.T("Départ"), g.StartMode == "manual"
					? I18n.T("lancé par l'organisateur (prévu le {0})", Full(g.StartAt))
					: I18n.T("à heure fixe, le {0}", Full(g.StartAt)) + "  ·  " + Countdown(g.StartAt));
				Row(I18n.T("Inscriptions"), I18n.T("du {0} au {1}", Full(g.RegistrationOpensAt), g.StartMode == "manual" && cfg.RegistrationClosesAt <= 0 ? I18n.T("départ") : Full(g.RegistrationClosesAt)));
				if (g.CheckInMinutes > 0) Row(I18n.T("Check-in"), I18n.T("{0} min avant le départ (sans confirmation, l'inscription est retirée)", g.CheckInMinutes));
			}
			if (g.Division == 0) Row(g.TeamSize > 1 ? I18n.T("Équipes") : I18n.T("Inscrits"), g.TeamSize > 1
				? I18n.T("{0} / {1}  (minimum {2}) — {3} complète(s)", g.Players, g.MaxEntries, g.MinEntries, g.CompleteEntries)
				: I18n.T("{0} / {1}  (minimum {2})", g.Players, g.MaxEntries, g.MinEntries));
			if (g.State == "running") Row(I18n.T("En cours"), (g.PhaseRaces > 0 ? I18n.T("{0} — manche {1}/{2}", PhaseLabel(g), g.PhaseRace, g.PhaseRaces) : PhaseLabel(g)) + "  ·  " + ModeLabel(g.PhaseMode));
			_right.AddChild(info);

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
					actions.AddChild(Action(I18n.T("Modifier"), () => OpenEditor(d.Config, g.Id)));
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

			BuildFlow(id, d);

			// série en un seul lobby : son classement est déjà dans le déroulé
			if (d.Standings.Count > 0 && (g.State == "done" || d.Flow.Any(f => f.State == "current" && f.Groups.Count > 1)))
			{
				_right.AddChild(Section(g.State == "done" ? I18n.T("Classement final") : I18n.T("Classement — {0}", PhaseLabel(g))));
				var table = new GridContainer { Columns = 3 };
				table.AddThemeConstantOverride("h_separation", 18);
				foreach (var s in d.Standings.Take(100))
				{
					var place = TournamentFlow.Line(s.Place + ".", 15, MenuStyle.Muted, wrap: false);
					place.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
					place.CustomMinimumSize = new Vector2(34, 0);
					table.AddChild(place);
					table.AddChild(TournamentFlow.Line(s.Name, 15, null, wrap: false));
					table.AddChild(TournamentFlow.Line(DetailLabel(s.Detail), 14, MenuStyle.Muted, wrap: false));
				}
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
				var l = NameLabel((g.CheckInMinutes > 0 && g.State == "open" && t.CheckedIn ? "✔ " : "") + t.Name + extra);
				if (extra.Length > 0) l.AddThemeColorOverride("font_color", MenuStyle.Muted);
				names.AddChild(l);
			}
			_right.AddChild(names);
		}

		/// <summary>Le déroulé : une carte par étape ; celles qui ont des résultats se déplient (l'étape en cours l'est d'office).</summary>
		private void BuildFlow(long id, TournamentDetailsDto d)
		{
			var cfg = d.Config;
			if (cfg == null || cfg.Phases.Count == 0) return;
			_right.AddChild(Section(I18n.T("Déroulé")));
			for (int i = 0; i < cfg.Phases.Count; i++)
			{
				int index = i;
				var flow = d.Flow.FirstOrDefault(f => f.Index == index);
				bool current = flow?.State == "current", results = flow != null && flow.Groups.Count > 0;
				bool open = results && (_open.TryGetValue((id, index), out bool chosen) ? chosen : current || (d.Summary.State == "done" && index == cfg.Phases.Count - 1));
				var box = new VBoxContainer();
				box.AddThemeConstantOverride("separation", 4);
				var head = TournamentFlow.Head(cfg, i, flow == null ? null : current ? I18n.T("en cours") : flow.State == "done" ? I18n.T("terminé") : I18n.T("à venir"));
				if (results) { var fold = TournamentFlow.Line(open ? "▲" : "▼", 13, MenuStyle.Muted, wrap: false); fold.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd; head.AddChild(fold); }
				box.AddChild(head);
				box.AddChild(TournamentFlow.Line(TournamentFlow.Summary(cfg, i), 14, MenuStyle.Muted));
				if (open) box.AddChild(TournamentFlow.Results(flow, d.Summary.MyReference));
				Action toggle = results ? () => { _open[(id, index)] = !open; ShowDetails(id); } : null;
				_right.AddChild(TournamentFlow.Card(box, TournamentFlow.ModeColor(TournamentFlow.ModeOf(cfg.Phases[i])), current, toggle));
				_right.AddChild(TournamentFlow.Arrow(TournamentFlow.Passing(cfg, i), i == cfg.Phases.Count - 1));
			}
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

		public static string ModeLabel(string mode) => TournamentFlow.ModeLabel(mode);

		/// <summary>Le nom du tournoi, avec sa division s'il a été séparé.</summary>
		private static string FullTitle(GameTournamentDto g) => g.Division > 0 ? g.Title + "  ·  " + I18n.T("Division {0}", g.Division) : g.Title;

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

		/// <summary>Nom posé dans une rangée qui passe à la ligne : il garde sa largeur.</summary>
		private static Label NameLabel(string text) { var l = TournamentFlow.Line(text, 15, null, wrap: false); l.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin; return l; }

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
