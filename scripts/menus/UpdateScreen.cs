using System;
using System.Text.RegularExpressions;
using Godot;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>Version du jeu publiée (étiquette de la release GitHub, sans le « v »). À augmenter à chaque release.</summary>
	public static class GameVersion
	{
		public const string Current = "0.11";
		public const string Repository = "efs91/open-esport";

		/// <summary>« 0.5 », « v0.10 », « v0.3 WIN64 » → (0, 5), (0, 10), (0, 3) : comparaison nombre par nombre.</summary>
		public static int Compare(string a, string b)
		{
			var na = Numbers(a); var nb = Numbers(b);
			for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
			{
				int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
				if (x != y) return x.CompareTo(y);
			}
			return 0;
		}

		private static int[] Numbers(string s)
		{
			var m = Regex.Match(s ?? "", @"\d+(\.\d+)*");
			if (!m.Success) return new int[0];
			return Array.ConvertAll(m.Value.Split('.'), int.Parse);
		}
	}

	/// <summary>
	/// Premier écran du jeu exporté : demande à GitHub la dernière release. Si elle est plus récente, télécharge l'archive de la plateforme
	/// (Windows : .zip, Linux : .tar.gz) dans le dossier du jeu, puis ferme le jeu et laisse un petit script la décompresser par-dessus
	/// l'installation et relancer le jeu (un exécutable ne peut pas se remplacer lui-même pendant qu'il tourne).
	/// Hors ligne, sans release plus récente, dans l'éditeur ou en mode sans fenêtre : on passe directement à la connexion.
	/// </summary>
	public partial class UpdateScreen : AuthScreen
	{
		private const string ApiUrl = "https://api.github.com/repos/" + GameVersion.Repository + "/releases/latest";

		private ProgressBar _bar;
		private Button _skip, _open;
		private HttpRequest _http;
		private string _version, _pageUrl, _archive;
		private long _size;
		private bool _downloading;

		protected override string Subtitle => I18n.T("Mise à jour");

		/// <summary>Faut-il vérifier les mises à jour ? Seulement dans le jeu exporté, avec une fenêtre, et sans l'option --no-update.</summary>
		public static bool Enabled => OS.HasFeature("template") && DisplayServer.GetName() != "headless"
			&& Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-update") < 0 && Array.IndexOf(OS.GetCmdlineArgs(), "--no-update") < 0;

		protected override void BuildForm()
		{
			_bar = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, CustomMinimumSize = new Vector2(0, 26), Visible = false, ShowPercentage = true };
			Form.AddChild(_bar);
			_open = Action(I18n.T("Ouvrir la page de téléchargement"), () => { if (_pageUrl != null) OS.ShellOpen(_pageUrl); }, true);
			_open.Visible = false;
			_skip = Action(I18n.T("Passer"), Skip);
		}

		public override void _Ready()
		{
			base._Ready();
			Say(I18n.T("Recherche d'une nouvelle version…"));
			_http = new HttpRequest { Timeout = 8, MaxRedirects = 8, UseThreads = true };
			AddChild(_http);
			_http.RequestCompleted += OnRelease;
			if (_http.Request(ApiUrl, Headers()) != Error.Ok) Done();
		}

		private static string[] Headers() => new[] { "User-Agent: OpenEsport-Updater", "Accept: application/vnd.github+json" };

		private void Skip()
		{
			if (_downloading) { _http.CancelRequest(); TryDelete(_archive); }
			Done();
		}

		private void OnRelease(long result, long code, string[] headers, byte[] body)
		{
			if (result != (long)HttpRequest.Result.Success || code != 200) { Done(); return; }
			var json = Json.ParseString(System.Text.Encoding.UTF8.GetString(body));
			if (json.VariantType != Variant.Type.Dictionary) { Done(); return; }
			var rel = json.AsGodotDictionary();
			_version = rel.TryGetValue("tag_name", out var tag) ? tag.AsString() : "";
			_pageUrl = rel.TryGetValue("html_url", out var page) ? page.AsString() : $"https://github.com/{GameVersion.Repository}/releases/latest";
			if (GameVersion.Compare(_version, GameVersion.Current) <= 0) { Done(); return; }

			// l'archive de cette plateforme
			bool windows = OS.GetName() == "Windows";
			string url = null;
			if (rel.TryGetValue("assets", out var assets))
				foreach (var a in assets.AsGodotArray())
				{
					var d = a.AsGodotDictionary();
					string name = d["name"].AsString().ToLowerInvariant();
					bool mine = windows ? name.Contains("win") && name.EndsWith(".zip") : name.Contains("linux") && name.EndsWith(".tar.gz");
					if (!mine) continue;
					url = d["browser_download_url"].AsString();
					_size = d["size"].AsInt64();
					_archive = InstallDir.PathJoin("_update").PathJoin(d["name"].AsString());
				}
			if (url == null || !Writable())
			{
				Say(I18n.T("La version {0} est disponible, mais elle ne peut pas s'installer toute seule ici : télécharge-la sur GitHub.", _version), true);
				_open.Visible = true;
				_skip.Text = I18n.T("Continuer");
				return;
			}

			DirAccess.MakeDirRecursiveAbsolute(_archive.GetBaseDir());
			TryDelete(_archive);
			_http.RequestCompleted -= OnRelease;
			_http.RequestCompleted += OnDownloaded;
			_http.Timeout = 0;
			_http.DownloadFile = _archive;
			_downloading = true;
			_bar.Visible = true;
			Say(I18n.T("Nouvelle version {0} : téléchargement…", _version));
			if (_http.Request(url, new[] { "User-Agent: OpenEsport-Updater" }) != Error.Ok) Fail("requête");
		}

		public override void _Process(double delta)
		{
			if (!_downloading || _size <= 0) return;
			long got = _http.GetDownloadedBytes();
			_bar.Value = Math.Clamp(got / (double)_size, 0, 1);
			Status.Text = I18n.T("Téléchargement de la version {0} : {1} / {2} Mo", _version, got / 1048576, _size / 1048576);
		}

		private void OnDownloaded(long result, long code, string[] headers, byte[] body)
		{
			_downloading = false;
			if (result != (long)HttpRequest.Result.Success || code != 200) { Fail($"{(HttpRequest.Result)result} {code}"); return; }
			var f = Godot.FileAccess.Open(_archive, Godot.FileAccess.ModeFlags.Read);
			long len = f == null ? 0 : (long)f.GetLength();
			f?.Close();
			if (_size > 0 && len != _size) { Fail(I18n.T("fichier incomplet")); return; }
			Install();
		}

		private void Fail(string why)
		{
			_downloading = false;
			TryDelete(_archive);
			Say(I18n.T("Échec du téléchargement : {0}", why), true);
			_open.Visible = true;
			_skip.Text = I18n.T("Continuer");
		}

		/// <summary>Dossier où est installé le jeu (celui de l'exécutable).</summary>
		private static string InstallDir => OS.GetExecutablePath().GetBaseDir();

		private static bool Writable()
		{
			string probe = InstallDir.PathJoin(".update_test");
			var f = Godot.FileAccess.Open(probe, Godot.FileAccess.ModeFlags.Write);
			if (f == null) return false;
			f.Close();
			DirAccess.RemoveAbsolute(probe);
			return true;
		}

		private static void TryDelete(string path)
		{
			if (!string.IsNullOrEmpty(path) && Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
		}

		/// <summary>Écrit le script d'installation, le lance, et ferme le jeu : le script attend la fin du jeu, décompresse par-dessus et relance.</summary>
		private void Install()
		{
			Say(I18n.T("Installation de la version {0}… le jeu va redémarrer.", _version));
			_bar.Value = 1;
			int pid = OS.GetProcessId();
			string dir = InstallDir, exe = OS.GetExecutablePath();
			string script;
			if (OS.GetName() == "Windows")
			{
				string W(string p) => p.Replace('/', '\\');
				script = dir.PathJoin("_update").PathJoin("update.cmd");
				// outils de Windows par leur chemin complet (un autre « find » ou « tar » dans le PATH ne doit pas être pris à la place)
				const string sys = "%SystemRoot%\\System32\\";
				string text = string.Join("\r\n",
					"@echo off",
					":wait",
					$"\"{sys}tasklist.exe\" /FI \"PID eq {pid}\" 2>nul | \"{sys}find.exe\" \"{pid}\" >nul",
					$"if not errorlevel 1 ( \"{sys}timeout.exe\" /t 1 /nobreak >nul & goto wait )",
					$"\"{sys}tar.exe\" -xf \"{W(_archive)}\" -C \"{W(dir)}\"",       // tar de Windows 10+ : il lit les .zip
					$"if errorlevel 1 ( echo echec de la decompression > \"{W(dir)}\\_update\\echec.txt\" ) else ( del \"{W(_archive)}\" )",
					$"start \"\" \"{W(exe)}\"",
					"");
				Write(script, text);
				OS.CreateProcess("cmd.exe", new[] { "/c", W(script) });
			}
			else
			{
				script = dir.PathJoin("_update").PathJoin("update.sh");
				string text = string.Join("\n",
					"#!/bin/sh",
					$"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done",
					$"cd '{dir}' || exit 1",
					$"if tar xzf '_update/{_archive.GetFile()}' -C .; then rm -f '_update/{_archive.GetFile()}'; else echo 'echec de la decompression' > '_update/echec.txt'; fi",
					$"chmod +x '{exe}'",
					$"nohup '{exe}' >/dev/null 2>&1 &",
					"");
				Write(script, text);
				// détaché du jeu (nohup + arrière-plan) : il doit survivre à sa fermeture
				OS.CreateProcess("/bin/sh", new[] { "-c", $"nohup /bin/sh '{script}' >/dev/null 2>&1 &" });
			}
			GetTree().CreateTimer(0.6).Timeout += () => GetTree().Quit();
		}

		private static void Write(string path, string text)
		{
			var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
			f?.StoreString(text);
			f?.Close();
		}
	}
}
