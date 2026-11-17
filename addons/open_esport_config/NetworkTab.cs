#if TOOLS
using Godot;

namespace Openesport.EditorTools
{
	/// <summary>Onglet « Réseau » : l'adresse du service de matchmaking que le jeu contacte. Elle est enregistrée dans res://data/network.cfg, donc exportée avec le jeu
	/// (les joueurs n'ont rien à régler). Les options --matchmaker, la variable OE_MATCHMAKER et user://network.cfg restent prioritaires (développement).</summary>
	[Tool]
	public partial class NetworkTab : VBoxContainer
	{
		public const string File = "res://data/network.cfg";
		private LineEdit _host;
		private SpinBox _port;
		private Label _status;

		public override void _Ready()
		{
			var cfg = new ConfigFile();
			string host = "127.0.0.1"; int port = 27000;
			if (cfg.Load(File) == Error.Ok)
			{
				host = (string)cfg.GetValue("network", "host", host);
				port = (int)cfg.GetValue("network", "port", port);
			}

			AddChild(new Label { Text = "Serveur de matchmaking que le jeu contacte (adresse publique du serveur en ligne)." });
			var row = new HBoxContainer();
			row.AddChild(new Label { Text = "Adresse / IP :" });
			_host = new LineEdit { Text = host, CustomMinimumSize = new Vector2(240, 0), PlaceholderText = "ex. 51.91.125.49" };
			row.AddChild(_host);
			row.AddChild(new Label { Text = "  Port UDP :" });
			_port = new SpinBox { MinValue = 1, MaxValue = 65535, Value = port };
			row.AddChild(_port);
			AddChild(row);

			var buttons = new HBoxContainer();
			var save = new Button { Text = "Enregistrer" };
			save.Pressed += Save;
			buttons.AddChild(save);
			var online = new Button { Text = "Serveur en ligne (51.91.125.49)" };
			online.Pressed += () => { _host.Text = "51.91.125.49"; _port.Value = 27000; Save(); };
			buttons.AddChild(online);
			var local = new Button { Text = "Local (127.0.0.1)" };
			local.Pressed += () => { _host.Text = "127.0.0.1"; _port.Value = 27000; Save(); };
			buttons.AddChild(local);
			AddChild(buttons);

			_status = new Label { Text = "" };
			AddChild(_status);
			AddChild(new Label { Text = "Pense à ouvrir ce port en UDP sur le serveur, ainsi que la plage des parties (27100-27199 par défaut)." });
		}

		private void Save()
		{
			var cfg = new ConfigFile();
			cfg.SetValue("network", "host", _host.Text.Trim());
			cfg.SetValue("network", "port", (int)_port.Value);
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://data"));
			var err = cfg.Save(File);
			_status.Text = err == Error.Ok ? $"Enregistré : {_host.Text.Trim()}:{(int)_port.Value}  ({File})" : $"Échec de l'enregistrement : {err}";
		}
	}
}
#endif
