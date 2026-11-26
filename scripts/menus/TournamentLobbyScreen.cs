using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Le lobby d'un tournoi : à gauche les règles et où en est le tournoi (étape, manche, check-in) avec ce qu'on peut y faire ; au centre les parties
	/// en cours (que les observateurs peuvent regarder) et le classement provisoire ; à droite le tchat du tournoi et les joueurs présents.
	/// Se met à jour toutes les 3 secondes.
	/// </summary>
	public partial class TournamentLobbyScreen : CanvasLayer
	{
		public const string NodeName = "TournamentLobbyScreen";
		private const double PollSeconds = 3.0;

		/// <summary>Le tournoi dont le lobby est ouvert (0 : aucun).</summary>
		public static long OpenId { get; private set; }

		public long Id;

		private Label _title, _status, _message, _presentTitle;
		private VBoxContainer _left, _middle, _chatLines, _present;
		private ScrollContainer _chatScroll;
		private LineEdit _chatField;
		private Button _send;
		private double _poll = PollSeconds;
		private bool _busy;
		private long _lastLine;
		private string _signature = "";
		private TournamentLobbyDto _lobby;
		private static readonly Regex KeyPattern = new Regex(@"^(?:f(\d+))?(p\d+m\d+|t\d+n\d+)(?:g(\d+))?$");

		public TournamentLobbyScreen() { Layer = 116; }

		/// <summary>Ouvre le lobby d'un tournoi, par-dessus le menu (les écrans de tournoi et de recherche de partie se ferment).</summary>
		public static void Open(Node from, long id)
		{
			var root = from.GetTree().Root;
			foreach (var name in new[] { "TournamentsScreen", "MatchmakingScreen", "ModeScreen", "CharacterPanel", "LoadoutScreen" }) root.GetNodeOrNull(name)?.QueueFree();
			var old = root.GetNodeOrNull<TournamentLobbyScreen>(NodeName);
			if (old != null)
			{
				if (old.Id == id && !old.IsQueuedForDeletion()) return;
				old.Name = NodeName + "_old";
				old.QueueFree();
			}
			root.CallDeferred(Node.MethodName.AddChild, new TournamentLobbyScreen { Name = NodeName, Id = id });
		}

		public override void _Ready()
		{
			OpenId = Id;
			Input.MouseMode = Input.MouseModeEnum.Visible;
			AddChild(MenuStyle.Backdrop());
			var margin = new MarginContainer { Theme = MenuStyle.SharedTheme };
			margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
			AddChild(margin);
			var page = new VBoxContainer();
			page.AddThemeConstantOverride("separation", 8);
			margin.AddChild(MenuStyle.Card(page, margin: 12));

			var head = new HBoxContainer();
			head.AddThemeConstantOverride("separation", 10);
			var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			titles.AddThemeConstantOverride("separation", 0);
			_title = new Label { Text = I18n.T("Lobby du tournoi"), ClipText = true };
			_title.AddThemeFontSizeOverride("font_size", 24);
			titles.AddChild(_title);
			_status = TournamentFlow.Line("", 14, MenuStyle.Cyan, wrap: false);
			_status.ClipText = true;
			titles.AddChild(_status);
			head.AddChild(titles);
			var page2 = new Button { Text = I18n.T("Page du tournoi"), CustomMinimumSize = new Vector2(150, 38) };
			page2.Pressed += OpenPage;
			head.AddChild(page2);
			var close = new Button { Text = I18n.T("Quitter le lobby"), CustomMinimumSize = new Vector2(150, 38) };
			close.Pressed += Close;
			head.AddChild(close);
			page.AddChild(head);

			_message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
			_message.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.4f));
			page.AddChild(_message);

			var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			body.AddThemeConstantOverride("separation", 12);
			page.AddChild(body);
			_left = Column(body, 330);
			_middle = Column(body, 0);

			// tchat et présents
			var right = new VBoxContainer { CustomMinimumSize = new Vector2(290, 0), SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
			right.AddThemeConstantOverride("separation", 6);
			body.AddChild(right);
			right.AddChild(Section(I18n.T("Tchat du tournoi")));
			_chatScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			_chatLines = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_chatLines.AddThemeConstantOverride("separation", 2);
			_chatScroll.AddChild(_chatLines);
			right.AddChild(_chatScroll);
			var send = new HBoxContainer();
			_chatField = new LineEdit { PlaceholderText = I18n.T("Écrire un message…"), MaxLength = 200, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_chatField.TextSubmitted += _ => Send();
			send.AddChild(_chatField);
			_send = new Button { Text = I18n.T("Envoyer") };
			_send.Pressed += Send;
			send.AddChild(_send);
			right.AddChild(send);
			_presentTitle = Section(I18n.T("Présents"));
			right.AddChild(_presentTitle);
			var presentScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 110), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			_present = new VBoxContainer();
			_present.AddThemeConstantOverride("separation", 0);
			presentScroll.AddChild(_present);
			right.AddChild(presentScroll);

			_left.AddChild(new LoadingRing());
		}

		public override void _ExitTree() { if (OpenId == Id) OpenId = 0; }

		private static VBoxContainer Column(Control parent, float width)
		{
			var scroll = new ScrollContainer
			{
				SizeFlagsHorizontal = width > 0 ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(width, 0),
			};
			var content = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			content.AddThemeConstantOverride("margin_right", 10);
			var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			box.AddThemeConstantOverride("separation", 6);
			content.AddChild(box);
			scroll.AddChild(content);
			parent.AddChild(scroll);
			return box;
		}

		public override void _Process(double delta)
		{
			_poll += delta;
			if (_poll >= PollSeconds && !_busy) { _poll = 0; Reload(); }
		}

		public override void _Input(InputEvent e)
		{
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && !_chatField.HasFocus()) { Close(); GetViewport().SetInputAsHandled(); }
		}

		private void Close() { QueueFree(); }

		private void OpenPage()
		{
			var root = GetTree().Root;
			root.GetNodeOrNull("TournamentsScreen")?.QueueFree();
			root.AddChild(new TournamentsScreen { Name = "TournamentsScreen", Focus = Id });
			QueueFree();
		}

		private void Say(string text) { _message.Text = text ?? ""; _message.Visible = _message.Text.Length > 0; }

		private async void Reload()
		{
			_busy = true;
			try
			{
				var (error, lobby) = await SocialClient.LobbyAsync(Id, _lastLine);
				if (!IsInstanceValid(this)) return;
				if (error != null) { Say(error); return; }
				_lobby = lobby;
				AddChat(lobby.Chat);
				Rebuild();
			}
			finally { _busy = false; }
		}

		// ------------------------------------------------------------------ tchat

		private void AddChat(List<ChatLineDto> lines)
		{
			if (lines.Count == 0) return;
			bool bottom = _chatScroll.ScrollVertical >= _chatScroll.GetVScrollBar().MaxValue - _chatScroll.Size.Y - 30;
			foreach (var l in lines.Where(l => l.Id > _lastLine))
			{
				_lastLine = l.Id;
				var line = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SelectionEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				line.AddThemeFontSizeOverride("normal_font_size", 14);
				line.AddThemeFontSizeOverride("bold_font_size", 14);
				string when = DateTimeOffset.FromUnixTimeSeconds(l.At).ToLocalTime().ToString("HH:mm");
				string color = string.Equals(l.Login, AccountClient.Login, StringComparison.OrdinalIgnoreCase) ? "ffd166" : "55d5ed";
				line.Text = $"[color=#8090a0]{when}[/color] [b][color=#{color}]{Escape(l.Login)}[/color][/b]  {Escape(l.Text)}";
				_chatLines.AddChild(line);
			}
			while (_chatLines.GetChildCount() > 200) { var first = _chatLines.GetChild(0); _chatLines.RemoveChild(first); first.QueueFree(); }
			if (bottom || _chatLines.GetChildCount() <= lines.Count) CallDeferred(nameof(ScrollChatDown));
		}

		private void ScrollChatDown() { if (IsInstanceValid(_chatScroll)) _chatScroll.ScrollVertical = (int)_chatScroll.GetVScrollBar().MaxValue; }

		private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

		private async void Send()
		{
			string text = _chatField.Text.Trim();
			if (text.Length == 0) return;
			_chatField.Text = "";
			string error = await SocialClient.ChatAsync(Id, text);
			if (!IsInstanceValid(this)) return;
			if (error != null) { Say(error); _chatField.Text = text; return; }
			Say(null);
			_poll = PollSeconds;
		}

		// ------------------------------------------------------------------ règles, état, parties, classement

		private void Rebuild()
		{
			var d = _lobby.Details;
			var g = d?.Summary;
			if (g == null) return;
			string sig = JsonSerializer.Serialize(d) + JsonSerializer.Serialize(_lobby.Live) + string.Join(",", _lobby.Present) + _lobby.CanChat + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20);
			_chatField.PlaceholderText = _lobby.CanChat ? I18n.T("Écrire un message…") : I18n.T("Réservé aux inscrits et aux observateurs");
			_send.Disabled = !_lobby.CanChat;
			_chatField.Editable = _lobby.CanChat;
			if (sig == _signature) return;
			_signature = sig;
			_title.Text = (g.Division > 0 ? g.Title + "  ·  " + I18n.T("Division {0}", g.Division) : g.Title);
			_status.Text = StatusLine(g);

			foreach (var c in _left.GetChildren()) c.QueueFree();
			foreach (var c in _middle.GetChildren()) c.QueueFree();
			BuildActions(g);
			BuildRules(d);
			BuildLive(d);
			BuildStandings(d);

			foreach (var c in _present.GetChildren()) c.QueueFree();
			_presentTitle.Text = I18n.T("Présents ({0})", _lobby.Present.Count);
			foreach (var p in _lobby.Present)
			{
				bool observer = _lobby.Observers.Contains(p, StringComparer.OrdinalIgnoreCase);
				_present.AddChild(TournamentFlow.Line("● " + p + (observer ? "  " + I18n.T("(observateur)") : ""), 14, new Color(0.45f, 0.95f, 0.55f), wrap: false));
			}
			if (_lobby.Observers.Count > 0) _present.AddChild(TournamentFlow.Line(I18n.T("Observateurs : {0}", string.Join(", ", _lobby.Observers)), 13, MenuStyle.Muted));
		}

		private static string StatusLine(GameTournamentDto g)
		{
			if (g.State == "running")
				return (g.PhaseRaces > 0 ? I18n.T("En cours : {0} — manche {1}/{2}", TournamentsScreen.PhaseLabel(g), g.PhaseRace, g.PhaseRaces) : I18n.T("En cours : {0}", TournamentsScreen.PhaseLabel(g)))
					+ "  ·  " + TournamentsScreen.ModeLabel(g.PhaseMode) + (g.Phases > 1 ? "  ·  " + I18n.T("étape {0}/{1}", g.Phase + 1, g.Phases) : "");
			if (g.State == "open")
				return (g.StartMode == "manual" ? I18n.T("Départ lancé par l'organisateur (prévu {0})", TournamentsScreen.Countdown(g.StartAt)) : I18n.T("Départ {0}", TournamentsScreen.Countdown(g.StartAt)))
					+ "  ·  " + (g.TeamSize > 1 ? I18n.T("{0} équipe(s) inscrite(s)", g.Players) : I18n.T("{0} inscrit(s)", g.Players))
					+ (g.CheckInOpen ? "  ·  " + I18n.T("check-in ouvert") : "");
			return TournamentsScreen.StateLabel(g);
		}

		/// <summary>Ce que je peux faire maintenant : confirmer ma présence, jouer ma manche ou mon match ; l'organisateur : ouvrir le check-in, lancer.</summary>
		private void BuildActions(GameTournamentDto g)
		{
			long id = g.Id;
			var box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 6);
			box.AddChild(Section(I18n.T("Où on en est")));
			if (g.State == "open")
			{
				if (g.Joined) box.AddChild(Text(g.CheckInOpen ? (g.CheckedIn ? I18n.T("Présence confirmée : rendez-vous au départ.") : I18n.T("Le check-in est ouvert : confirme ta présence avant le départ."))
					: I18n.T("Tu es inscrit. Le tournoi commence {0}.", TournamentsScreen.Countdown(g.StartAt))));
				else if (g.IsObserver) box.AddChild(Text(I18n.T("Tu es observateur : tu pourras regarder les parties en cours depuis ce lobby.")));
				if (g.Joined && g.CheckInOpen && !g.CheckedIn) box.AddChild(Big(I18n.T("✔ Je suis présent"), () => Act(SocialClient.TournamentActionAsync(id, "checkin"))));
				if (g.IsOwner && !g.CheckInOpen) box.AddChild(Small(I18n.T("Ouvrir le check-in maintenant"), () => Act(SocialClient.TournamentActionAsync(id, "opencheckin"))));
				if (g.IsOwner) box.AddChild(Small(I18n.T("Lancer le tournoi maintenant"), () => Act(SocialClient.TournamentActionAsync(id, "start"))));
			}
			else if (g.State == "running")
			{
				if (g.Joined)
				{
					box.AddChild(Text(g.Duel ? I18n.T("Quand ton match est prêt, lance-le ici : ton adversaire te rejoint.") : I18n.T("Quand ta manche est prête, lance-la ici : tout ton lobby se retrouve sur le même serveur.")));
					box.AddChild(Big(g.Duel ? I18n.T("▶ Jouer mon match") : I18n.T("▶ Jouer ma manche"), () => TournamentsScreen.PlayTournament(this, id, Say)));
				}
				if (g.IsObserver) box.AddChild(Text(I18n.T("Observateur : clique sur « Regarder » à côté d'une partie en cours.")));
			}
			else box.AddChild(Text(TournamentsScreen.StateLabel(g)));
			if (!string.IsNullOrWhiteSpace(g.Note)) box.AddChild(TournamentFlow.Line(I18n.Server(g.Note), 14, new Color(1f, 0.75f, 0.4f)));
			_left.AddChild(box);
		}

		/// <summary>Le règlement en clair : inscription, chaque étape avec son mode, ses parties, ses points, ses bots.</summary>
		private void BuildRules(TournamentDetailsDto d)
		{
			var cfg = d.Config;
			var g = d.Summary;
			_left.AddChild(Section(I18n.T("Règles")));
			_left.AddChild(Text(TournamentsScreen.FormatLabel(g) + "  ·  " + I18n.T("organisé par {0}", g.Owner)));
			if (!string.IsNullOrWhiteSpace(g.Description)) _left.AddChild(TournamentFlow.Line(g.Description, 14, MenuStyle.Muted));
			if (cfg == null) return;
			for (int i = 0; i < cfg.Phases.Count; i++)
			{
				var p = cfg.Phases[i];
				string state = g.State == "running" ? (i < g.Phase ? I18n.T("terminée") : i == g.Phase ? I18n.T("en cours") : null) : g.State == "done" ? I18n.T("terminée") : null;
				var head = TournamentFlow.Head(cfg, i, state);
				_left.AddChild(head);
				string summary = TournamentFlow.Summary(cfg, i);
				if (!TournamentFlow.IsSplit(p)) summary += "  ·  " + (p.FillBots ? (p.MaxBots > 0 ? I18n.T("bots : {0} au plus", p.MaxBots) : I18n.T("complété par des bots")) : I18n.T("sans bots"));
				string passing = TournamentFlow.Passing(cfg, i);
				_left.AddChild(TournamentFlow.Line(summary + (string.IsNullOrEmpty(passing) ? "" : "\n" + passing), 13, MenuStyle.Muted));
			}
		}

		/// <summary>Les parties en cours : manche et lobby (ou match), état, joueurs, scores ; « Regarder » pour les observateurs.</summary>
		private void BuildLive(TournamentDetailsDto d)
		{
			var g = d.Summary;
			if (g.State != "running") return;
			_middle.AddChild(Section(I18n.T("Parties en cours")));
			if (_lobby.Live.Count == 0) { _middle.AddChild(Text(I18n.T("Aucune partie en cours pour l'instant."))); return; }
			foreach (var live in _lobby.Live)
			{
				var row = new HBoxContainer();
				row.AddThemeConstantOverride("separation", 8);
				var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				text.AddThemeConstantOverride("separation", 0);
				text.AddChild(TournamentFlow.Line(GameLabel(d, live), 15, null, wrap: false));
				text.AddChild(TournamentFlow.Line(GameState(live), 13, live.Phase == 1 ? new Color("ffd166") : MenuStyle.Muted, wrap: false));
				row.AddChild(text);
				if (_lobby.Observer && live.Port > 0 && live.Phase != 2)
				{
					var l = live;
					row.AddChild(Small(I18n.T("Regarder"), () => Watch(l)));
				}
				_middle.AddChild(MenuStyle.Card(row, margin: 8));
			}
			if (!_lobby.Observer) _middle.AddChild(TournamentFlow.Line(I18n.T("Seuls les observateurs invités par l'organisateur regardent les parties."), 13, MenuStyle.Muted));
		}

		/// <summary>« Manche 3 · lobby 2 » ou « alice contre bob (partie 2) ».</summary>
		private static string GameLabel(TournamentDetailsDto d, LiveGameDto live)
		{
			string mode = TournamentsScreen.ModeLabel(live.Mode);
			if (string.IsNullOrEmpty(live.Match))
			{
				var g = d.Summary;
				int race = g.PhaseRaces > 0 ? live.Race - (g.Race - (g.PhaseRace - 1)) + 1 : live.Race + 1;
				return I18n.T("Manche {0} · lobby {1}", Math.Max(1, race), live.Lobby + 1) + "  —  " + mode;
			}
			var k = KeyPattern.Match(live.Match);
			if (!k.Success) return mode;
			int phase = k.Groups[1].Success ? int.Parse(k.Groups[1].Value) : 0;
			string key = k.Groups[2].Value;
			int game = k.Groups[3].Success ? int.Parse(k.Groups[3].Value) + 1 : 1;
			var m = d.Flow.FirstOrDefault(f => f.Index == phase)?.Groups.SelectMany(x => x.Matches).FirstOrDefault(x => x.Key == key);
			string who = m != null ? I18n.T("{0} contre {1}", m.A, m.B) : I18n.T("Match");
			return who + (game > 1 ? "  " + I18n.T("(partie {0})", game) : "") + "  —  " + mode;
		}

		private static string GameState(LiveGameDto live)
		{
			if (live.Phase == 0) return I18n.T("salle d'attente · {0} joueur(s)", live.Humans);
			if (live.Phase == 2) return I18n.T("terminée");
			string s = I18n.T("en cours · {0} joueur(s)", live.Humans);
			if (TournamentFlow.TwoTeams(live.Mode) || live.Mode == "1v1") s += $"  ·  {live.Score0} - {live.Score1}";
			if (live.TimeLeft > 0) s += $"  ·  {live.TimeLeft / 60}:{live.TimeLeft % 60:00}";
			return s;
		}

		/// <summary>Classement provisoire de l'étape en cours (ou final), puis les résultats de l'étape.</summary>
		private void BuildStandings(TournamentDetailsDto d)
		{
			var g = d.Summary;
			if (g.State == "open")
			{
				_middle.AddChild(Section(g.TeamSize > 1 ? I18n.T("Équipes inscrites ({0})", d.Teams.Count) : I18n.T("Inscrits ({0})", d.Teams.Count)));
				var names = new HFlowContainer();
				names.AddThemeConstantOverride("h_separation", 14);
				foreach (var t in d.Teams)
					names.AddChild(TournamentFlow.Line((t.CheckedIn ? "✔ " : "") + t.Name, 14, t.CheckedIn ? new Color(0.45f, 0.95f, 0.55f) : MenuStyle.Ink, wrap: false));
				if (d.Teams.Count == 0) names.AddChild(TournamentFlow.Line(I18n.T("Personne pour l'instant."), 14, MenuStyle.Muted));
				_middle.AddChild(names);
				return;
			}
			if (d.Standings.Count > 0)
			{
				_middle.AddChild(Section(g.State == "done" ? I18n.T("Classement final") : I18n.T("Classement provisoire — {0}", TournamentsScreen.PhaseLabel(g))));
				var table = new GridContainer { Columns = 3 };
				table.AddThemeConstantOverride("h_separation", 16);
				foreach (var s in d.Standings.Take(60))
				{
					var place = TournamentFlow.Line(s.Place + ".", 14, MenuStyle.Muted, wrap: false);
					place.CustomMinimumSize = new Vector2(32, 0);
					table.AddChild(place);
					table.AddChild(TournamentFlow.Line(s.Name, 14, null, wrap: false));
					table.AddChild(TournamentFlow.Line(s.Detail ?? "", 13, MenuStyle.Muted, wrap: false));
				}
				_middle.AddChild(table);
			}
			var current = d.Flow.FirstOrDefault(f => f.State == "current") ?? d.Flow.LastOrDefault(f => f.State == "done");
			if (current != null && current.Groups.Count > 0)
			{
				_middle.AddChild(Section(current.State == "current" ? I18n.T("Étape en cours") : I18n.T("Dernière étape")));
				_middle.AddChild(TournamentFlow.Results(current, g.MyReference));
			}
		}

		// ------------------------------------------------------------------ actions

		/// <summary>Observateur : rejoint la partie en spectateur (il ne joue pas) ; à la fin, retour à ce lobby.</summary>
		private void Watch(LiveGameDto live)
		{
			if (!OpenEsport.Core.Modes.TryParse(live.Mode, out _)) return;
			MatchSettings.SpectateTournament = Id;
			TournamentWatcher.ReturnToLobby = Id;
			QueueFree();
			SocialClient.JoinPartyGame?.Invoke(live.Host, live.Port, live.Mode, 1);
		}

		private async void Act(System.Threading.Tasks.Task<string> task)
		{
			string error = await task;
			if (!IsInstanceValid(this)) return;
			Say(error);
			_signature = "";
			_poll = PollSeconds;
			TournamentWatcher.RefreshNow();
		}

		private static Label Section(string text)
		{
			var l = new Label { Text = text };
			l.AddThemeFontSizeOverride("font_size", 18);
			l.AddThemeColorOverride("font_color", MenuStyle.Cyan);
			return l;
		}
		private static Label Text(string text) => TournamentFlow.Line(text, 14);
		private static Button Small(string text, Action pressed) { var b = new Button { Text = text }; b.Pressed += pressed; return b; }
		private static Button Big(string text, Action pressed) { var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) }; b.AddThemeFontSizeOverride("font_size", 18); b.Pressed += pressed; return b; }
	}
}
