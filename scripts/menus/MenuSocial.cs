using System;
using System.Linq;
using Godot;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Encart en haut à gauche du menu principal : les tournois programmés (publiés dans ManageComp), le groupe de joueurs (invitations, membres)
	/// et la liste d'amis avec ceux qui sont en ligne. Interroge le service de comptes toutes les quelques secondes tant qu'il est visible.
	/// Quand le chef du groupe lance une partie, les autres membres la rejoignent automatiquement.
	/// </summary>
	public partial class MenuSocial : Control
	{
		private const double PollSeconds = 3.0, TournamentSeconds = 60.0;
		private VBoxContainer _box;
		private double _poll, _tournaments = TournamentSeconds;
		private bool _busy, _showFriends = true, _showCreate;
		private LineEdit _createName, _createWhen; private SpinBox _createRaces; private OptionButton _createFormat;
		private string _signature = "", _message = "";
		private int _joinedSeq;
		private LineEdit _addField;

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Ignore;
			SetAnchorsPreset(LayoutPreset.TopLeft);
			OffsetLeft = 18; OffsetTop = 18; OffsetRight = 400; OffsetBottom = 700;
			_box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
			_box.AddThemeConstantOverride("separation", 8);
			AddChild(_box);
			_box.SetAnchorsPreset(LayoutPreset.TopWide);
			VisibilityChanged += () => { if (IsVisibleInTree()) { _poll = PollSeconds; _tournaments = TournamentSeconds; } };
		}

		public override void _Process(double delta)
		{
			if (!IsVisibleInTree()) return;
			_poll += delta; _tournaments += delta;
			if (_busy || _poll < PollSeconds) return;
			_poll = 0;
			_ = Refresh();
		}

		private async System.Threading.Tasks.Task Refresh()
		{
			_busy = true;
			try
			{
				if (_tournaments >= TournamentSeconds) { _tournaments = 0; await SocialClient.RefreshTournamentsAsync(); }
				if (AccountClient.LoggedIn)
				{
					await SocialClient.RefreshPartyAsync();
					await SocialClient.RefreshFriendsAsync();
				}
				else SocialClient.Clear();
				CheckPartyMatch();
				Rebuild();
			}
			finally { _busy = false; }
		}

		/// <summary>Le chef a publié une partie : les autres membres la rejoignent une seule fois.</summary>
		private void CheckPartyMatch()
		{
			var p = SocialClient.Party?.Party;
			var m = p?.Match;
			if (m == null || p.IsLeader || m.Seq == _joinedSeq || !IsVisibleInTree()) return;
			_joinedSeq = m.Seq;
			_message = I18n.T("Ton groupe lance une partie…");
			SocialClient.JoinPartyGame?.Invoke(m.Host, m.Port, m.Mode, m.TeamSize);
		}

		private static string Signature()
		{
			var s = SocialClient.Party; var f = SocialClient.Friends; var t = SocialClient.Tournaments;
			return string.Join("|", s.Party == null ? "-" : $"{s.Party.Id}:{s.Party.Leader}:{string.Join(",", s.Party.Members.Select(x => x.Login))}:{string.Join(",", s.Party.Pending)}",
				string.Join(",", s.Invites.Select(i => i.PartyId + i.From)),
				string.Join(",", f.Friends.Select(x => x.Login + x.State + x.Online + x.PartyId)),
				string.Join(",", t.Tournaments.Take(4).Select(x => x.Id + x.Date)), string.Join(",", t.Games.Select(g => g.Id + g.State + g.Players + g.Joined + g.Race)), AccountClient.LoggedIn);
		}

		private void Rebuild()
		{
			string sig = Signature() + _message + _showFriends + _showCreate;
			if (sig == _signature) return;
			// on ne reconstruit pas pendant la saisie d'un nom d'ami
			if (_addField != null && IsInstanceValid(_addField) && _addField.HasFocus()) return;
			if ((_createName != null && IsInstanceValid(_createName) && _createName.HasFocus()) || (_createWhen != null && IsInstanceValid(_createWhen) && _createWhen.HasFocus())) return;
			_signature = sig;
			foreach (var c in _box.GetChildren()) c.QueueFree();

			// ---- tournois programmés
			var tv = new VBoxContainer();
			tv.AddThemeConstantOverride("separation", 4);
			tv.AddChild(Title(I18n.T("Tournois programmés")));
			var games = SocialClient.Tournaments.Games.Take(4).ToList();
			var published = SocialClient.Tournaments.Tournaments.Take(Math.Max(0, 4 - games.Count)).ToList();
			if (games.Count == 0 && published.Count == 0) tv.AddChild(Muted(SocialClient.Tournaments.Configured ? I18n.T("Aucun tournoi programmé pour le moment.") : I18n.T("Les tournois arrivent bientôt.")));
			foreach (var g in games)
			{
				var row = new HBoxContainer();
				string when = DateTimeOffset.FromUnixTimeSeconds(g.StartAt).ToLocalTime().ToString("dd/MM HH:mm");
				string state = g.State == "running" ? (g.Mode == "duel" ? I18n.T("en cours") : I18n.T("manche {0}/{1}", Math.Min(g.Race + 1, g.Races), g.Races)) : g.State == "done" ? I18n.T("terminé") : I18n.T("{0} inscrit(s)", g.Players);
				var label = new Label { Text = $"{when}  {g.Title}  ({state})", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, TooltipText = g.Mode == "duel" ? I18n.T("Duel {0} contre {0}, premier à {1}, organisé par {2}", g.TeamSize, g.ScoreLimit, g.Owner) : I18n.T("{0} manche(s), organisé par {1}", g.Races, g.Owner) };
				row.AddChild(label);
				long id = g.Id;
				if (AccountClient.LoggedIn && g.State == "open")
				{
					row.AddChild(Small(g.Joined ? I18n.T("Quitter") : I18n.T("S'inscrire"), () => Act(SocialClient.TournamentActionAsync(id, g.Joined ? "leave" : "join"))));
					if (g.IsOwner) row.AddChild(Small(I18n.T("Lancer"), () => Act(SocialClient.TournamentActionAsync(id, "start"))));
				}
				if (AccountClient.LoggedIn && g.State == "running" && g.Joined)
					row.AddChild(Small(g.Mode == "duel" ? I18n.T("Jouer mon match") : I18n.T("Jouer la manche"), () => PlayRace(id)));
				if (g.State != "open") row.AddChild(Small(I18n.T("Classement"), () => ShowStandings(id, g.Title)));
				tv.AddChild(row);
			}
			foreach (var t in published)
			{
				var b = new Button { Text = $"{Pretty(t.Date)}{t.Title}", Alignment = HorizontalAlignment.Left, ClipText = true, Flat = true, TooltipText = t.Subtitle ?? "" };
				string url = t.Url;
				b.Pressed += () => { if (!string.IsNullOrEmpty(url) && (url.StartsWith("https://") || url.StartsWith("http://"))) OS.ShellOpen(url); };
				tv.AddChild(b);
			}
			if (AccountClient.LoggedIn && SocialClient.Tournaments.Configured)
			{
				var createHead = new Button { Text = (_showCreate ? "▾ " : "▸ ") + I18n.T("Créer un tournoi"), Flat = true, Alignment = HorizontalAlignment.Left };
				createHead.Pressed += () => { _showCreate = !_showCreate; _signature = ""; Rebuild(); };
				tv.AddChild(createHead);
				if (_showCreate) tv.AddChild(CreateForm());
			}
			_box.AddChild(MenuStyle.Card(tv, margin: 12));
			if (!AccountClient.LoggedIn) return;

			// ---- groupe
			var gv = new VBoxContainer();
			gv.AddThemeConstantOverride("separation", 4);
			var party = SocialClient.Party.Party;
			gv.AddChild(Title(party == null ? I18n.T("Groupe") : I18n.T("Groupe ({0}/5)", party.Members.Count)));
			foreach (var inv in SocialClient.Party.Invites)
			{
				var row = new HBoxContainer();
				row.AddChild(new Label { Text = I18n.T("{0} t'invite", inv.From), SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true });
				long id = inv.PartyId;
				row.AddChild(Small(I18n.T("Accepter"), () => Act(SocialClient.PartyActionAsync("accept", partyId: id))));
				row.AddChild(Small("✕", () => Act(SocialClient.PartyActionAsync("decline", partyId: id))));
				gv.AddChild(row);
			}
			if (party != null)
			{
				foreach (var m in party.Members)
				{
					var row = new HBoxContainer();
					bool leader = string.Equals(m.Login, party.Leader, StringComparison.OrdinalIgnoreCase);
					row.AddChild(new Label { Text = (leader ? "★ " : "• ") + m.Login, SizeFlagsHorizontal = SizeFlags.ExpandFill });
					if (party.IsLeader && !leader)
					{
						string who = m.Login;
						row.AddChild(Small(I18n.T("Chef"), () => Act(SocialClient.PartyActionAsync("promote", who))));
						row.AddChild(Small("✕", () => Act(SocialClient.PartyActionAsync("kick", who))));
					}
					gv.AddChild(row);
				}
				foreach (var w in party.Pending) gv.AddChild(Muted(I18n.T("Invitation envoyée à {0}…", w)));
				gv.AddChild(Small(I18n.T("Quitter le groupe"), () => Act(SocialClient.PartyActionAsync("leave"))));
				if (!party.IsLeader) gv.AddChild(Muted(I18n.T("Le chef du groupe lance la partie.")));
			}
			else if (SocialClient.Party.Invites.Count == 0) gv.AddChild(Muted(I18n.T("Invite un ami pour jouer en équipe.")));
			if (!string.IsNullOrEmpty(_message)) gv.AddChild(Muted(_message));
			_box.AddChild(MenuStyle.Card(gv, margin: 12));

			// ---- amis
			var fv = new VBoxContainer();
			fv.AddThemeConstantOverride("separation", 4);
			int online = SocialClient.Friends.Friends.Count(f => f.State == "friend" && f.Online);
			var head = new Button { Text = (_showFriends ? "▾ " : "▸ ") + I18n.T("Amis ({0} en ligne)", online), Flat = true, Alignment = HorizontalAlignment.Left };
			head.AddThemeFontSizeOverride("font_size", 18);
			head.Pressed += () => { _showFriends = !_showFriends; _signature = ""; Rebuild(); };
			fv.AddChild(head);
			if (_showFriends)
			{
				foreach (var f in SocialClient.Friends.Friends)
				{
					var row = new HBoxContainer();
					string dot = f.State == "friend" ? (f.Online ? "● " : "○ ") : "";
					var name = new Label { Text = dot + f.Login + (f.State == "sent" ? "  " + I18n.T("(demande envoyée)") : ""), SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
					name.AddThemeColorOverride("font_color", f.State == "friend" && f.Online ? new Color(0.45f, 0.95f, 0.55f) : MenuStyle.Muted);
					row.AddChild(name);
					string who = f.Login;
					if (f.State == "received")
					{
						row.AddChild(Small(I18n.T("Accepter"), () => Act(SocialClient.FriendActionAsync("accept", who))));
						row.AddChild(Small("✕", () => Act(SocialClient.FriendActionAsync("remove", who))));
					}
					else
					{
						if (f.State == "friend" && f.Online && (party == null || party.IsLeader) && !(party?.Members.Any(m => m.Login == who) ?? false))
							row.AddChild(Small(I18n.T("Inviter"), () => Act(SocialClient.PartyActionAsync("invite", who))));
						row.AddChild(Small("✕", () => Act(SocialClient.FriendActionAsync("remove", who))));
					}
					fv.AddChild(row);
				}
				var add = new HBoxContainer();
				_addField = new LineEdit { PlaceholderText = I18n.T("Ajouter un ami (identifiant)"), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 20 };
				void Send() { string n = _addField.Text.Trim(); if (n.Length > 0) { _addField.Text = ""; Act(SocialClient.FriendActionAsync("add", n)); } }
				_addField.TextSubmitted += _ => Send();
				add.AddChild(_addField);
				add.AddChild(Small("+", Send));
				fv.AddChild(add);
			}
			_box.AddChild(MenuStyle.Card(fv, margin: 12));
		}

		/// <summary>Formulaire de création : nom, date et heure locales (JJ/MM/AAAA HH:MM), nombre de manches (battle royale, classement cumulé).</summary>
		private Control CreateForm()
		{
			var v = new VBoxContainer();
			v.AddThemeConstantOverride("separation", 4);
			_createName = new LineEdit { PlaceholderText = I18n.T("Nom du tournoi"), MaxLength = 60 };
			_createWhen = new LineEdit { PlaceholderText = I18n.T("JJ/MM/AAAA HH:MM"), Text = DateTime.Now.AddHours(2).ToString("dd/MM/yyyy HH:00") };
			v.AddChild(_createName);
			v.AddChild(_createWhen);
			_createFormat = new OptionButton();
			_createFormat.AddItem(I18n.T("Battle royale (manches, classement cumulé)"));
			_createFormat.AddItem(I18n.T("Battle royale en duo (équipes de 2)"));
			_createFormat.AddItem(I18n.T("Battle royale en trio (équipes de 3)"));
			_createFormat.AddItem(I18n.T("Duels 1 contre 1 (poules + tableau)"));
			_createFormat.AddItem(I18n.T("Duels 2 contre 2 (poules + tableau)"));
			v.AddChild(_createFormat);
			var races = new HBoxContainer();
			var racesLabel = new Label { Text = I18n.T("Manches"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
			races.AddChild(racesLabel);
			_createRaces = new SpinBox { MinValue = 1, MaxValue = 10, Value = 5 };
			races.AddChild(_createRaces);
			v.AddChild(races);
			// duel : le nombre de manches devient le nombre de victimes pour gagner un match
			_createFormat.ItemSelected += i =>
			{
				bool duel = i >= 3;
				racesLabel.Text = duel ? I18n.T("Victimes pour gagner") : I18n.T("Manches");
				_createRaces.MinValue = duel ? 3 : 1; _createRaces.MaxValue = duel ? 30 : 10; _createRaces.Value = duel ? 10 : 5;
			};
			v.AddChild(Small(I18n.T("Créer"), () =>
			{
				if (!DateTime.TryParseExact(_createWhen.Text.Trim(), "dd/MM/yyyy HH:mm", null, System.Globalization.DateTimeStyles.AssumeLocal, out var when))
				{ _message = I18n.T("Date invalide : écris JJ/MM/AAAA HH:MM."); _signature = ""; Rebuild(); return; }
				_showCreate = false;
				int format = _createFormat.Selected;
				bool duel = format >= 3;
				Act(SocialClient.CreateTournamentAsync(_createName.Text, new DateTimeOffset(when).ToUnixTimeSeconds(), duel ? 0 : (int)_createRaces.Value,
					duel ? "duel" : "br", duel ? format - 2 : format + 1, duel ? (int)_createRaces.Value : 0));
			}));
			return v;
		}

		/// <summary>Cherche la partie de sa manche : le matchmaking réunit les joueurs du lobby sur un même serveur réservé.</summary>
		private async void PlayRace(long id)
		{
			var (error, tag, mode, teamSize) = await SocialClient.MyRaceAsync(id);
			if (error != null) { _message = error; _signature = ""; Rebuild(); return; }
			var screen = new MatchmakingScreen { Name = "MatchmakingScreen", Tournament = tag, TournamentTeamSize = teamSize };
			screen.OnFound = (h, p) => SocialClient.JoinPartyGame?.Invoke(h, p, mode, teamSize);
			GetTree().Root.AddChild(screen);
		}

		/// <summary>Affiche le classement d'un tournoi dans une fenêtre.</summary>
		private async void ShowStandings(long id, string title)
		{
			var (error, lines) = await SocialClient.StandingsAsync(id);
			string text = error ?? (lines.Count == 0 ? I18n.T("Pas encore de classement.") : string.Join((char)10, lines.Take(20)));
			var dialog = new AcceptDialog { Title = title, DialogText = text };
			dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree;
			GetTree().Root.AddChild(dialog);
			dialog.PopupCentered(new Vector2I(420, 360));
		}

		private async void Act(System.Threading.Tasks.Task<string> task)
		{
			string error = await task;
			_message = error ?? "";
			_tournaments = TournamentSeconds;          // la liste des tournois se recharge tout de suite
			_signature = "";
			Rebuild();
		}

		private static string Pretty(string iso)
		{
			if (string.IsNullOrEmpty(iso)) return "";
			return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
				? d.ToLocalTime().ToString(iso.Length > 10 ? "dd/MM HH:mm" : "dd/MM") + "  " : iso + "  ";
		}

		private static Label Title(string text) { var l = new Label { Text = text }; l.AddThemeFontSizeOverride("font_size", 18); return l; }
		private static Label Muted(string text) { var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart }; l.AddThemeColorOverride("font_color", MenuStyle.Muted); l.AddThemeFontSizeOverride("font_size", 14); return l; }
		private static Button Small(string text, Action onPressed) { var b = new Button { Text = text }; b.Pressed += onPressed; return b; }
	}
}
