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
			_teamSize = Modes.ValidTeamSize(_mode, MatchSettings.TeamSize);
			var root = new Control();
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);
			root.AddChild(new ColorRect { Color = new Color(0.05f, 0.06f, 0.08f), AnchorRight = 1, AnchorBottom = 1 });
			var center = new CenterContainer();
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(center);
			var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
			box.AddThemeConstantOverride("separation", 14);
			center.AddChild(box);
			_title = new Label { Text = "Recherche d'une partie — " + Modes.Label(_mode, _teamSize), HorizontalAlignment = HorizontalAlignment.Center };
			_title.AddThemeFontSizeOverride("font_size", 28);
			box.AddChild(_title);
			_status = new Label { Text = "Connexion au matchmaking…", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_status.AddThemeFontSizeOverride("font_size", 18);
			box.AddChild(_status);
			_cancel = new Button { Text = "Annuler", CustomMinimumSize = new Vector2(0, 44) };
			_cancel.Pressed += Cancel;
			box.AddChild(_cancel);

			Start();
		}

		private void Start()
		{
			MatchSettings.MatchmakerAddress(out _mmHost, out _mmPort);
			_client?.Dispose();
			_client = new MatchmakerClient();
			_client.Start(_mmHost, _mmPort, _mode, _teamSize);
			_cancel.Text = "Annuler";
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
					_status.Text = _client.Message.TrimEnd('.', '…') + dots;
					break;
				case MatchmakingState.Found:
					_status.Text = $"Partie trouvée ({_client.Humans} joueur(s) dans la salle d'attente)…";
					var found = OnFound; string host = _client.Host; int port = _client.ServerPort;
					_client.Dispose(); _client = null;
					QueueFree();
					found?.Invoke(host, port);
					break;
				case MatchmakingState.Failed:
					_status.Text = _client.Message + $"\n(matchmaking : {_mmHost}:{_mmPort})";
					_cancel.Text = "Retour";
					_client.Dispose(); _client = null;
					var retry = new Button { Text = "Réessayer", CustomMinimumSize = new Vector2(0, 44) };
					retry.Pressed += () => { retry.QueueFree(); Start(); };
					_cancel.GetParent().AddChild(retry);
					_cancel.GetParent().MoveChild(retry, 2);
					// développement : se connecter directement à un serveur lancé à la main (sans matchmaking)
					var direct = new Button { Text = "Serveur direct (127.0.0.1:27015)", CustomMinimumSize = new Vector2(0, 44) };
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
