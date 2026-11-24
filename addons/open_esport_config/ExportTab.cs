#if TOOLS
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace Openesport.EditorTools
{
	/// <summary>
	/// Onglet « Export du jeu » : met à jour la liste des fichiers à exporter (tools/export/select_export.py : seulement ce que le jeu utilise),
	/// puis fabrique les versions Windows et Linux (préréglages « Windows Desktop » et « Linux Jeu », dossiers builds/win64 et builds/linux).
	/// Chaque export tourne dans un Godot sans fenêtre à part : l'éditeur reste utilisable.
	/// </summary>
	[Tool]
	public partial class ExportTab : VBoxContainer
	{
		private static readonly (string preset, string output)[] Targets =
		{
			("Windows Desktop", "builds/win64/open-esport.exe"),
			("Linux Jeu", "builds/linux/open-esport.x86_64"),
		};

		private Button _select, _win, _linux, _both;
		private CheckBox _selectFirst;
		private Label _status;
		private RichTextLabel _log;
		private bool _busy;

		public override void _Ready()
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			AddThemeConstantOverride("separation", 8);
			AddChild(new Label
			{
				Text = "1. « Trier les fichiers » : ne garde dans l'export que ce que le jeu utilise (scènes, sons, textures… trouvés dans le code), pour un zip plus léger.\n"
					+ "2. « Exporter » : fabrique le jeu dans builds/win64 et builds/linux (2 à 5 minutes chacun). Enregistre tes scènes avant.\n"
					+ "N'enregistre pas la fenêtre Projet > Exporter pendant ce temps : elle réécrirait l'ancienne liste.",
				AutowrapMode = TextServer.AutowrapMode.WordSmart
			});
			_select = new Button { Text = "Trier les fichiers à exporter" };
			_select.Pressed += () => Run(select: true, targets: Array.Empty<(string, string)>());
			AddChild(_select);
			_selectFirst = new CheckBox { Text = "Trier les fichiers avant chaque export", ButtonPressed = true };
			AddChild(_selectFirst);
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 8);
			_win = new Button { Text = "Exporter Windows", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_linux = new Button { Text = "Exporter Linux", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_both = new Button { Text = "Exporter les deux", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_win.Pressed += () => Run(_selectFirst.ButtonPressed, new[] { Targets[0] });
			_linux.Pressed += () => Run(_selectFirst.ButtonPressed, new[] { Targets[1] });
			_both.Pressed += () => Run(_selectFirst.ButtonPressed, Targets);
			row.AddChild(_win); row.AddChild(_linux); row.AddChild(_both);
			AddChild(row);
			_status = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
			AddChild(_status);
			_log = new RichTextLabel { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true };
			AddChild(_log);
		}

		private void SetBusy(bool busy)
		{
			_busy = busy;
			_select.Disabled = _win.Disabled = _linux.Disabled = _both.Disabled = busy;
		}

		private void Run(bool select, (string preset, string output)[] targets)
		{
			if (_busy) return;
			SetBusy(true);
			_log.Text = "";
			string project = ProjectSettings.GlobalizePath("res://").TrimEnd('/', (char)92);
			string godot = OS.GetExecutablePath();
			string console = godot.EndsWith(".exe") && !godot.EndsWith("_console.exe") ? godot[..^4] + "_console.exe" : null;      // Windows : la version console écrit sa sortie
			if (console != null && File.Exists(console)) godot = console;
			Task.Run(() =>
			{
				bool ok = true;
				try
				{
					if (select)
					{
						Status("Tri des fichiers à exporter…");
						var (code, text) = Exec(Python(), new[] { Path.Combine(project, "tools", "export", "select_export.py"), "--godot", godot }, project, null, TimeSpan.FromMinutes(5));
						Log(text.Trim());
						ok = code == 0;
						Log(ok ? "→ liste des fichiers mise à jour (export_presets.cfg)." : $"→ échec du tri (code {code}).");
					}
					foreach (var (preset, output) in targets)
					{
						if (!ok) break;
						string outPath = Path.Combine(project, output.Replace('/', Path.DirectorySeparatorChar));
						Directory.CreateDirectory(Path.GetDirectoryName(outPath));
						var started = DateTime.Now;
						Status($"Export « {preset} » en cours (quelques minutes)…");
						// le Godot sans fenêtre finit l'export puis traîne parfois à se fermer (« leaked at exit ») : on l'arrête une fois ces lignes écrites
						var (code, text) = Exec(godot, new[] { "--headless", "--path", project, "--export-release", preset, outPath }, project,
							line => line.Contains("leaked at exit") || line.Contains("still in use at exit"), TimeSpan.FromMinutes(30));
						var errors = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains("ERROR") && !l.Contains("leaked") && !l.Contains("still in use")).Take(20).ToList();
						bool written = File.Exists(outPath) && File.GetLastWriteTime(outPath) >= started;
						if (errors.Count > 0) Log(string.Join("\n", errors));
						if (written)
						{
							long size = new DirectoryInfo(Path.GetDirectoryName(outPath)).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
							Log($"→ {preset} : {output} ({size / (1024 * 1024)} Mo dans le dossier, {(DateTime.Now - started).TotalMinutes:0.0} min).");
						}
						else { Log($"→ {preset} : échec (code {code}), rien n'a été écrit."); ok = false; }
					}
				}
				catch (Exception e) { Log("Erreur : " + e.Message); ok = false; }
				Status(ok ? (targets.Length > 0 ? "Export terminé : le dossier builds/ est à jour." : "Tri terminé.") : "Échec : voir le journal ci-dessous.");
				CallDeferred(nameof(Finish));
			});
		}

		private void Finish() => SetBusy(false);
		private void Status(string text) => Callable.From(() => _status.Text = text).CallDeferred();
		private void Log(string text) { if (!string.IsNullOrEmpty(text)) Callable.From(() => _log.AppendText(text + "\n")).CallDeferred(); }

		private static string Python() => OS.GetName() == "Windows" ? "python" : "python3";

		/// <summary>Lance un programme et lit sa sortie ; <paramref name="stopWhen"/> : une ligne qui veut dire « travail fini » (le processus est alors arrêté).</summary>
		private static (int code, string text) Exec(string exe, string[] args, string workdir, Func<string, bool> stopWhen, TimeSpan timeout)
		{
			var psi = new ProcessStartInfo(exe) { WorkingDirectory = workdir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
			foreach (var a in args) psi.ArgumentList.Add(a);
			var text = new StringBuilder();
			bool done = false;
			using var p = new Process { StartInfo = psi };
			void OnLine(string line)
			{
				if (line == null) return;
				lock (text) text.AppendLine(line);
				if (stopWhen != null && stopWhen(line)) done = true;
			}
			p.OutputDataReceived += (_, e) => OnLine(e.Data);
			p.ErrorDataReceived += (_, e) => OnLine(e.Data);
			p.Start();
			p.BeginOutputReadLine(); p.BeginErrorReadLine();
			var limit = DateTime.Now + timeout;
			while (!p.WaitForExit(500))
			{
				if (done) { System.Threading.Thread.Sleep(3000); if (!p.HasExited) p.Kill(true); break; }
				if (DateTime.Now > limit) { p.Kill(true); lock (text) text.AppendLine("ERROR: temps dépassé, export arrêté."); break; }
			}
			p.WaitForExit();
			lock (text) return (done ? 0 : p.ExitCode, text.ToString());
		}
	}
}
#endif
