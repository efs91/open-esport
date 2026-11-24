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
		private bool _busy, _showFriends = true;
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
				string.Join(",", t.Tournaments.Take(4).Select(x => x.Id + x.Date)), string.Join(",", t.Games.Select(g => g.Id + g.State + g.Players + g.Joined + g.Race + g.CheckedIn + g.StartAt + g.Invitations.Count + (g.MyTeam == null ? "" : g.MyTeam.Members.Count + "/" + g.MyTeam.Invites.Count))), AccountClient.LoggedIn);
		}

		private void Rebuild()
		{
			string sig = Signature() + _message + _showFriends + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60);      // les comptes à rebours avancent
			if (sig == _signature) return;
			// on ne reconstruit pas pendant la saisie d'un nom d'ami
			if (_addField != null && IsInstanceValid(_addField) && _addField.HasFocus()) return;
			_signature = sig;
			foreach (var c in _box.GetChildren()) c.QueueFree();

			// ---- tournois : rappels de ses tournois (date, check-in, manche à jouer), les prochains, la page dédiée
			var tv = new VBoxContainer();
			tv.AddThemeConstantOverride("separation", 4);
			tv.AddChild(Title(I18n.T("Tournois")));
			var all = SocialClient.Tournaments.Games;
			var mine = all.Where(g => g.Joined && (g.State == "open" || g.State == "running")).OrderBy(g => g.StartAt).Take(3).ToList();
			var upcoming = all.Where(g => !g.Joined && g.State == "open").OrderBy(g => g.StartAt).Take(Math.Max(1, 3 - mine.Count)).ToList();
			var published = SocialClient.Tournaments.Tournaments.Take(Math.Max(0, 3 - mine.Count - upcoming.Count)).ToList();
			// invitations dans une équipe de tournoi : accepter ou refuser tout de suite
			foreach (var g in all.Where(g => g.State == "open" && !g.Joined))
				foreach (var inv in g.Invitations)
				{
					long id = g.Id; int reference = inv.Reference;
					tv.AddChild(Alert(I18n.T("✉ {0} t'invite dans son équipe pour « {1} » ({2}).", inv.Captain, g.Title, DateTimeOffset.FromUnixTimeSeconds(g.StartAt).ToLocalTime().ToString("dd/MM HH:mm"))));
					var row = new HBoxContainer();
					row.AddThemeConstantOverride("separation", 6);
					row.AddChild(Small(I18n.T("Accepter"), () => Act(SocialClient.TournamentActionAsync(id, "accept", reference: reference))));
					row.AddChild(Small(I18n.T("Refuser"), () => Act(SocialClient.TournamentActionAsync(id, "decline", reference: reference))));
					row.AddChild(Small(I18n.T("Voir"), () => OpenTournaments(id)));
					tv.AddChild(row);
				}
			if (mine.Count > 0) tv.AddChild(Muted(I18n.T("Tes tournois")));
			foreach (var g in mine)
			{
				long id = g.Id;
				string when = g.State == "running" ? (g.PhaseRaces > 0 ? I18n.T("en cours · {0} {1}/{2}", I18n.T(g.PhaseName), g.PhaseRace, g.PhaseRaces) : I18n.T("en cours · {0}", I18n.T(g.PhaseName)))
					: $"{DateTimeOffset.FromUnixTimeSeconds(g.StartAt).ToLocalTime():dd/MM HH:mm} · {TournamentsScreen.Countdown(g.StartAt)}";
				var row = new HBoxContainer();
				row.AddChild(Link($"★ {g.Title}\n{when}", () => OpenTournaments(id)));
				if (g.State == "running") row.AddChild(Small(g.Duel ? I18n.T("Jouer mon match") : I18n.T("Jouer"), () => TournamentsScreen.PlayTournament(this, id, e => { _message = e; _signature = ""; Rebuild(); })));
				else if (g.CheckInOpen && !g.CheckedIn) row.AddChild(Small(I18n.T("Je suis présent"), () => Act(SocialClient.TournamentActionAsync(id, "checkin"))));
				tv.AddChild(row);
			}
			if (upcoming.Count > 0) tv.AddChild(Muted(I18n.T("À venir")));
			foreach (var g in upcoming)
			{
				long id = g.Id;
				tv.AddChild(Link($"{DateTimeOffset.FromUnixTimeSeconds(g.StartAt).ToLocalTime():dd/MM HH:mm}  {g.Title}\n{TournamentsScreen.FormatLabel(g)} · {TournamentsScreen.StateLabel(g)}", () => OpenTournaments(id)));
			}
			foreach (var t in published)
			{
				var b = new Button { Text = $"{Pretty(t.Date)}{t.Title}", Alignment = HorizontalAlignment.Left, ClipText = true, Flat = true, TooltipText = t.Subtitle ?? "" };
				string url = t.Url;
				b.Pressed += () => { if (!string.IsNullOrEmpty(url) && (url.StartsWith("https://") || url.StartsWith("http://"))) OS.ShellOpen(url); };
				tv.AddChild(b);
			}
			if (mine.Count == 0 && upcoming.Count == 0 && published.Count == 0)
				tv.AddChild(Muted(SocialClient.Tournaments.Configured ? I18n.T("Aucun tournoi programmé pour le moment.") : I18n.T("Les tournois arrivent bientôt.")));
			var buttons = new HBoxContainer();
			buttons.AddThemeConstantOverride("separation", 6);
			buttons.AddChild(Small(I18n.T("Tous les tournois"), () => OpenTournaments(0)));
			if (AccountClient.LoggedIn && SocialClient.Tournaments.Configured) buttons.AddChild(Small(I18n.T("Créer"), () => OpenTournaments(0, create: true)));
			tv.AddChild(buttons);
			string reminder = Reminder(mine);
			if (reminder != null) tv.AddChild(Alert(reminder));
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

		/// <summary>Ouvre la page Tournois (sur un tournoi, ou sur la création).</summary>
		private void OpenTournaments(long id, bool create = false)
		{
			var root = GetTree().Root;
			root.GetNodeOrNull("TournamentsScreen")?.QueueFree();
			var screen = new TournamentsScreen { Name = "TournamentsScreen", Focus = id, StartWithCreate = create };
			screen.Closed += () => { _tournaments = TournamentSeconds; _signature = ""; };
			root.AddChild(screen);
		}

		/// <summary>Rappel du moment : check-in ouvert, départ imminent, manche à jouer.</summary>
		private static string Reminder(System.Collections.Generic.List<GameTournamentDto> mine)
		{
			foreach (var g in mine)
			{
				if (g.State == "running") return I18n.T("« {0} » est en cours : à toi de jouer !", g.Title);
				if (g.MyTeam != null && !g.MyTeam.Complete && g.State == "open")
					return g.MyTeam.Captain ? I18n.T("Ton équipe pour « {0} » est incomplète ({1}/{2}) : invite tes coéquipiers depuis la page du tournoi.", g.Title, g.MyTeam.Members.Count, g.TeamSize)
						: I18n.T("L'équipe de {0} pour « {1} » est incomplète ({2}/{3}).", g.MyTeam.CaptainName, g.Title, g.MyTeam.Members.Count, g.TeamSize);
				if (g.CheckInOpen && !g.CheckedIn) return I18n.T("Check-in ouvert pour « {0} » : confirme ta présence, sinon ton inscription sera retirée.", g.Title);
				long s = g.StartAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
				if (g.StartMode != "manual" && s > 0 && s <= 3600) return I18n.T("« {0} » commence {1}.", g.Title, TournamentsScreen.Countdown(g.StartAt));
			}
			return null;
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
		private static Button Link(string text, Action onPressed)
		{
			var b = new Button { Text = text, Flat = true, Alignment = HorizontalAlignment.Left, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			b.AddThemeFontSizeOverride("font_size", 14);
			b.Pressed += onPressed;
			return b;
		}
		private static Label Alert(string text) { var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart }; l.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.35f)); l.AddThemeFontSizeOverride("font_size", 14); return l; }
	}
}
