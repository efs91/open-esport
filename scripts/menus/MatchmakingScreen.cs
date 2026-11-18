using Openesport.Utils;
using System;
using Godot;
using OpenEsport.Core;
using Openesport.Online;

namespace Openesport.Menus
{
	/// <summary>
	/// Écran « Recherche d'une partie » : le jeu demande au service de matchmaking une salle d'attente du mode choisi (le service lance
	/// au besoin un serveur de partie), puis donne l'adresse du serveur à <see cref="OnFound"/>.
	/// </summary>
	public partial class MatchmakingScreen : CanvasLayer
	{
		public Action<string, int> OnFound;
		public Action OnCancel;
		/// <summary>Manche de tournoi (« tournoi:manche:lobby ») : battle royale réservé aux joueurs du lobby ; sinon partie ordinaire.</summary>
		public string Tournament;
		/// <summary>Duel de tournoi (tag « D… ») : joueurs par équipe.</summary>
		public int TournamentTeamSize = 1;

		private MatchmakerClient _client;
		private Label _status, _title;
		private Button _cancel;
		private double _dots;
		private string _mmHost; private int _mmPort;
		private int _teamSize;
		private GameMode _mode;

		public MatchmakingScreen() { Layer = 130; }

		public override void _Ready()
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
			_mode = MatchSettings.Mode;
			// un groupe joue ensemble : l'équipe est au moins aussi grande que le groupe (les places en plus sont prises par des bots)
			_teamSize = Modes.ValidTeamSize(_mode, Math.Max(MatchSettings.TeamSize, SocialClient.PartySize));
			if (!string.IsNullOrEmpty(Tournament)) { bool duel = Tournament.StartsWith("D"); _mode = duel ? GameMode.TeamDeathmatch : GameMode.BattleRoyale; _teamSize = duel ? Math.Clamp(TournamentTeamSize, 1, 2) : Math.Clamp(TournamentTeamSize, 1, 5); }
			var root = new Control();
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);
			root.AddChild(MenuStyle.Backdrop());
			var center = new CenterContainer();
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(center);
			var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
			box.AddThemeConstantOverride("separation", 14);
			center.AddChild(MenuStyle.Card(box, margin: 28));
			_title = new Label { Text = I18n.T("Recherche d'une partie — {0}", GameText.Label(_mode, _teamSize)), HorizontalAlignment = HorizontalAlignment.Center };
			_title.AddThemeFontSizeOverride("font_size", 28);
			box.AddChild(_title);
			_status = new Label { Text = I18n.T("Connexion au matchmaking…"), HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_status.AddThemeFontSizeOverride("font_size", 18);
			box.AddChild(_status);
			_cancel = new Button { Text = I18n.T("Annuler"), CustomMinimumSize = new Vector2(0, 44) };
			_cancel.Pressed += Cancel;
			box.AddChild(_cancel);

			Start();
		}

		private void Start()
		{
			MatchSettings.MatchmakerAddress(out _mmHost, out _mmPort);
			_client?.Dispose();
			_client = new MatchmakerClient();
			_client.Start(_mmHost, _mmPort, _mode, _teamSize, string.IsNullOrEmpty(Tournament) ? SocialClient.PartySize : 1, Tournament);
			_cancel.Text = I18n.T("Annuler");
		}

		/// <summary>Les messages du matchmaking viennent du service, en français : on les traduit ici (le nom du mode est recomposé avec la langue du joueur).</summary>
		private string Localize(string message)
		{
			string core = (message ?? "").TrimEnd('.', '…');
			if (core.StartsWith("Préparation d'une partie ")) return I18n.T("Préparation d'une partie {0}", GameText.Label(_mode, _teamSize));
			if (core.StartsWith("Lancement d'une partie ")) return I18n.T("Lancement d'une partie {0}", GameText.Label(_mode, _teamSize));
			foreach (var prefix in new[] { "Impossible de lancer un serveur de partie : ", "Adresse du matchmaking invalide : " })
				if (core.StartsWith(prefix)) return I18n.T(prefix + "{0}", core.Substring(prefix.Length).Replace("{", "{{").Replace("}", "}}"));
			string exact = I18n.T(message ?? "");
			if (exact != message) return exact;                  // le message complet a une traduction
			return I18n.T(core);
		}

		public override void _Process(double delta)
		{
			if (_client == null) return;
			_client.Poll();
			_dots += delta;
			string dots = new string('.', (int)(_dots * 2) % 4);
			switch (_client.State)
			{
				case MatchmakingState.Searching:
					_status.Text = Localize(_client.Message) + dots;
					break;
				case MatchmakingState.Found:
					_status.Text = I18n.T("Partie trouvée ({0} joueur(s) dans la salle d'attente)…", _client.Humans);
					var found = OnFound; string host = _client.Host; int port = _client.ServerPort;
					if (SocialClient.InParty && string.IsNullOrEmpty(Tournament)) _ = SocialClient.PartyActionAsync("match", host: host, port: port, mode: Modes.Get(_mode).Key, teamSize: _teamSize);     // les autres membres rejoignent
					_client.Dispose(); _client = null;
					QueueFree();
					found?.Invoke(host, port);
					break;
				case MatchmakingState.Failed:
					_status.Text = Localize(_client.Message) + "\n" + I18n.T("(matchmaking : {0}:{1})", _mmHost, _mmPort);
					_cancel.Text = I18n.T("Retour");
					_client.Dispose(); _client = null;
					var retry = new Button { Text = I18n.T("Réessayer"), CustomMinimumSize = new Vector2(0, 44) };
					retry.Pressed += () => { retry.QueueFree(); Start(); };
					_cancel.GetParent().AddChild(retry);
					_cancel.GetParent().MoveChild(retry, 2);
					// développement : se connecter directement à un serveur lancé à la main (sans matchmaking)
					var direct = new Button { Text = I18n.T("Serveur direct (127.0.0.1:27015)"), CustomMinimumSize = new Vector2(0, 44) };
					direct.Pressed += () => { var f = OnFound; QueueFree(); f?.Invoke("127.0.0.1", 27015); };
					_cancel.GetParent().AddChild(direct);
					_cancel.GetParent().MoveChild(direct, 3);
					break;
			}
		}

		private void Cancel()
		{
			_client?.Dispose(); _client = null;
			var cancel = OnCancel;
			QueueFree();
			cancel?.Invoke();
		}

		public override void _ExitTree() { _client?.Dispose(); }
	}
}
