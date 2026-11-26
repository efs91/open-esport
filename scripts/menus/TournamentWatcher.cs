using System;
using System.Linq;
using Godot;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Toujours actif, en menu comme en partie : tient la liste des tournois à jour (toutes les 3 s, le service ne répond en entier que si quelque chose a changé)
	/// et emmène les inscrits au lobby de leur tournoi 3 minutes avant le départ. En partie, un bandeau prévient 30 s avant : on peut alors se désinscrire
	/// et continuer sa partie, ou partir tout de suite. Après une partie de tournoi (jouée ou regardée), il ramène au lobby du tournoi.
	/// </summary>
	public partial class TournamentWatcher : Node
	{
		public const double PollSeconds = 3.0, FullSeconds = 30.0;
		/// <summary>Départ dans moins de ce temps : les inscrits sont emmenés au lobby ; le bandeau prévient <see cref="WarnSeconds"/> avant.</summary>
		public const int MoveSeconds = 180, WarnSeconds = 30;

		public static TournamentWatcher Instance { get; private set; }
		/// <summary>Première liste reçue ; dernière erreur du service (null : tout va bien).</summary>
		public static bool Ready { get; private set; }
		public static string Error { get; private set; }
		/// <summary>Tournoi dont on rejoindra le lobby à la fin de la partie en cours (0 : aucun).</summary>
		public static long ReturnToLobby;

		private double _poll = PollSeconds, _full;
		private bool _busy;
		private string _login = "";
		/// <summary>Tournois dont on a déjà ouvert le lobby depuis le menu, et ceux qu'on a quittés pour rester en partie (on ne les impose plus).</summary>
		private readonly System.Collections.Generic.HashSet<long> _moved = new(), _released = new();
		private CanvasLayer _banner;
		private Label _bannerText;
		private long _bannerFor;

		public override void _Ready()
		{
			Instance = this;
			ProcessMode = ProcessModeEnum.Always;
		}

		public override void _ExitTree() { if (Instance == this) Instance = null; }

		/// <summary>Redemande la liste au prochain passage (après une action : inscription, check-in…).</summary>
		public static void RefreshNow() { if (Instance != null) { Instance._poll = PollSeconds; Instance._full = FullSeconds; } }

		public override void _Process(double delta)
		{
			_poll += delta; _full += delta;
			string login = AccountClient.LoggedIn ? AccountClient.Login : "";
			if (login != _login) { _login = login; Ready = false; Error = null; _moved.Clear(); _released.Clear(); _poll = PollSeconds; _full = FullSeconds; }
			if (_poll >= PollSeconds && !_busy) { _poll = 0; Refresh(); }
			CheckReturn();
			CheckStart();
		}

		private async void Refresh()
		{
			_busy = true;
			bool full = _full >= FullSeconds;
			if (full) _full = 0;
			string error;
			try { error = await SocialClient.RefreshTournamentsAsync(full); }
			finally { _busy = false; }
			if (!IsInstanceValid(this)) return;
			if (error == null) Ready = true;
			Error = error;
		}

		private static bool InGame => OnlineGame.Instance != null && GodotObject.IsInstanceValid(OnlineGame.Instance);

		/// <summary>La partie de tournoi est finie (ou on l'a quittée) : retour au lobby du tournoi.</summary>
		private void CheckReturn()
		{
			if (ReturnToLobby == 0 || InGame || GetTree().Root.GetNodeOrNull("MatchmakingScreen") != null) return;
			long id = ReturnToLobby;
			ReturnToLobby = 0;
			if (AccountClient.LoggedIn) TournamentLobbyScreen.Open(this, id);
		}

		/// <summary>Un de mes tournois part dans moins de 3 min : direction son lobby (en partie, après 30 s de préavis).</summary>
		private void CheckStart()
		{
			if (!AccountClient.LoggedIn) { HideBanner(); return; }
			long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			var next = SocialClient.Tournaments.Games
				.Where(g => g.Joined && g.State == "open" && g.StartAt > 0 && g.StartAt - now <= MoveSeconds + WarnSeconds && g.StartAt - now > -1800 && !_released.Contains(g.Id))
				.OrderBy(g => g.StartAt).FirstOrDefault();
			if (next == null) { HideBanner(); return; }
			long left = next.StartAt - now;
			bool lobbyOpen = TournamentLobbyScreen.OpenId == next.Id;
			if (!InGame)
			{
				HideBanner();
				if (left > MoveSeconds || lobbyOpen || _moved.Contains(next.Id)) return;
				_moved.Add(next.Id);
				TournamentLobbyScreen.Open(this, next.Id);
				return;
			}
			// en partie : préavis de 30 s, puis départ vers le lobby
			if (OnlineGame.Instance.Spectating && OnlineGame.Instance.SpectatedTournament == next.Id) { HideBanner(); return; }
			long moveIn = left - MoveSeconds;
			ShowBanner(next, Math.Max(0, moveIn), left);
			if (moveIn > 0) return;
			HideBanner();
			_moved.Add(next.Id);
			ReturnToLobby = next.Id;
			GetNodeOrNull<MenuManager>(GlobalPaths.Managers.MENU_MANAGER)?.LeaveGameNow();
		}

		// ------------------------------------------------------------------ bandeau en partie

		private void ShowBanner(GameTournamentDto g, long moveIn, long left)
		{
			if (_banner == null || !IsInstanceValid(_banner) || _bannerFor != g.Id) BuildBanner(g);
			_bannerText.Text = I18n.T("« {0} » commence dans {1} : tu rejoins le lobby du tournoi dans {2} s (ta partie sera quittée).", g.Title, Clock(left), moveIn);
		}

		private void BuildBanner(GameTournamentDto g)
		{
			HideBanner();
			_bannerFor = g.Id;
			long id = g.Id;
			_banner = new CanvasLayer { Layer = 120, ProcessMode = ProcessModeEnum.Always };
			var box = new VBoxContainer { Theme = MenuStyle.SharedTheme };
			box.AddThemeConstantOverride("separation", 8);
			_bannerText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(560, 0) };
			_bannerText.AddThemeFontSizeOverride("font_size", 17);
			_bannerText.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.35f));
			box.AddChild(_bannerText);
			var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			row.AddThemeConstantOverride("separation", 10);
			var stay = new Button { Text = I18n.T("Me désinscrire et continuer ma partie (F8)") };
			stay.Pressed += () => Unregister(id);
			var go = new Button { Text = I18n.T("Y aller maintenant (F9)") };
			go.Pressed += () => GoNow(id);
			row.AddChild(stay);
			row.AddChild(go);
			box.AddChild(row);
			var card = MenuStyle.Card(box, margin: 12);
			card.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
			card.GrowHorizontal = Control.GrowDirection.Both;
			card.OffsetTop = 70;
			_banner.AddChild(card);
			GetTree().Root.AddChild(_banner);
		}

		public override void _Input(InputEvent e)
		{
			if (_banner == null || !IsInstanceValid(_banner) || e is not InputEventKey { Pressed: true, Echo: false } k) return;
			if (k.Keycode == Key.F8) { Unregister(_bannerFor); GetViewport().SetInputAsHandled(); }
			else if (k.Keycode == Key.F9) { GoNow(_bannerFor); GetViewport().SetInputAsHandled(); }
		}

		private async void Unregister(long id)
		{
			_released.Add(id);
			HideBanner();
			string error = await SocialClient.TournamentActionAsync(id, "leave");
			if (error != null) { _released.Remove(id); GD.PrintErr("[Tournois] désinscription impossible : " + error); }
			RefreshNow();
		}

		private void GoNow(long id)
		{
			HideBanner();
			_moved.Add(id);
			ReturnToLobby = id;
			GetNodeOrNull<MenuManager>(GlobalPaths.Managers.MENU_MANAGER)?.LeaveGameNow();
		}

		private void HideBanner()
		{
			if (_banner != null && IsInstanceValid(_banner)) _banner.QueueFree();
			_banner = null; _bannerText = null; _bannerFor = 0;
		}

		private static string Clock(long seconds) { seconds = Math.Max(0, seconds); return $"{seconds / 60}:{seconds % 60:00}"; }
	}
}
