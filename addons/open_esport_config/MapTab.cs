#if TOOLS
using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Openesport.EditorTools
{
	/// <summary>
	/// Onglet « Carte » : fabrique le fichier .world que lisent le serveur et le jeu (relief, collisions, tyroliennes, points de butin et coffres de la scène).
	/// À refaire après toute modification de la carte ou des points de butin (dossier Loot), puis relancer le serveur / le matchmaking.
	/// </summary>
	[Tool]
	public partial class MapTab : VBoxContainer
	{
		private OptionButton _maps;
		private Button _button;
		private Label _status;
		private RichTextLabel _log;

		public override void _Ready()
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			AddThemeConstantOverride("separation", 8);
			AddChild(new Label
			{
				Text = "Le serveur et le jeu lisent chaque carte dans un fichier (assets/maps/<nom de la scène en minuscules>.world). Après avoir déplacé, ajouté ou réglé des coffres et des points de butin (nœuds du dossier Loot), "
					+ "enregistre la scène (Ctrl+S) puis clique sur « Exporter la carte ». Relance ensuite le serveur ou le matchmaking.",
				AutowrapMode = TextServer.AutowrapMode.WordSmart
			});
			_maps = new OptionButton();
			using (var dir = DirAccess.Open("res://scenes/maps"))
			{
				var names = new System.Collections.Generic.List<string>();
				if (dir != null) foreach (var f in dir.GetFiles()) if (f.EndsWith(".tscn")) names.Add(f);
				names.Sort();
				foreach (var n in names) _maps.AddItem(n.GetBaseName());
				int frontiere = names.IndexOf("Frontiere01.tscn");
				if (frontiere >= 0) _maps.Selected = frontiere;
			}
			AddChild(_maps);
			_button = new Button { Text = "Exporter la carte" };
			_button.Pressed += Export;
			AddChild(_button);
			_status = new Label { Text = "" };
			AddChild(_status);
			_log = new RichTextLabel { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true };
			AddChild(_log);
		}

		private void Export()
		{
			_button.Disabled = true;
			_status.Text = "Export en cours (environ 20 secondes)… pense à avoir enregistré la scène.";
			_log.Text = "";
			string exe = OS.GetExecutablePath();
			string project = ProjectSettings.GlobalizePath("res://").TrimEnd('/', (char)92);
			string name = _maps.ItemCount > 0 ? _maps.GetItemText(_maps.Selected) : "Frontiere01";
			string mapScene = $"res://scenes/maps/{name}.tscn", worldFile = $"res://assets/maps/{name.ToLowerInvariant()}.world";
			var args = new[] { "--headless", "--path", project, "res://tools/export_world.tscn", "--", "--map=" + mapScene, "--out=" + worldFile };
			Task.Run(() =>
			{
				var output = new Godot.Collections.Array();
				int code = OS.Execute(exe, args, output, true);
				string text = output.Count > 0 ? output[0].AsString() : "";
				CallDeferred(nameof(Done), code, text);
			});
		}

		private void Done(int code, string text)
		{
			_button.Disabled = false;
			var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains("[WorldExporter]") || l.Contains("ERROR: ") && !l.Contains("leaked") || l.Contains("SCRIPT ERROR")).ToList();
			_log.Text = string.Join("\n", lines);
			bool ok = text.Contains("carte écrite") || text.Contains("carte simple écrite");
			var loot = lines.FirstOrDefault(l => l.Contains("points de butin"));
			_status.Text = ok ? "Carte exportée. " + (loot != null ? loot.Replace("[WorldExporter] ", "") : "") + " Relance le serveur / le matchmaking." : $"Échec de l'export (code {code}) : voir le journal ci-dessous.";
		}
	}
}
#endif
