using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Openesport.Diagnostics
{
	/// <summary>
	/// Boîte noire des à-coups. Pendant qu'on joue, elle note dans <c>logs/lag_trace.log</c> (à la racine du projet) chaque image trop longue
	/// avec tout ce qui peut l'expliquer : les derniers événements du jeu (création d'un joueur ou d'un objet, arme sortie, inventaire,
	/// coffre, tyrolienne, tirs…), le temps passé dans chaque partie du code, le ramasse-miettes, la mémoire, les compteurs du moteur
	/// et l'état du réseau. Elle note aussi les coupures du flux réseau (plus de snapshot du serveur), qui ressemblent à un lag.
	///
	/// Utilisation dans le code : <c>LagTracer.Event("texte")</c> pour un fait, <c>LagTracer.Measure("nom", () => ...)</c> ou
	/// <c>using (LagTracer.Scope("nom"))</c> pour mesurer un bloc (noté dès qu'il dépasse quelques millisecondes).
	/// </summary>
	public partial class LagTracer : Node
	{
		public static LagTracer Instance { get; private set; }

		/// <summary>Une image plus longue que ça (ms) est considérée comme un à-coup.</summary>
		public const double SpikeMs = 70.0;

		private readonly struct Entry { public readonly double T; public readonly string Text; public Entry(double t, string s) { T = t; Text = s; } }

		private static readonly Queue<Entry> Ring = new();
		private static readonly Dictionary<string, double> Phases = new();
		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static StreamWriter _file;
		private static string _path;

		private int _gc0, _gc1, _gc2;
		private long _mem;
		private double _lastFrameT;
		private int _frames, _spikes;
		private double _maxFrame, _sumFrame, _summaryAt = 10;

		// réseau (renseigné par OnlineGame)
		public static Func<string> NetState = () => "";
		public static Func<int> SnapshotCount = () => 0;
		private int _lastSnap = -1;
		private double _lastSnapT;
		private bool _gap;

		public static double Now => Clock.Elapsed.TotalSeconds;

		public static void Start(Node host)
		{
			if (Instance != null && GodotObject.IsInstanceValid(Instance)) return;
			var node = new LagTracer { Name = "LagTracer", ProcessMode = ProcessModeEnum.Always };
			host.GetTree().Root.CallDeferred(Node.MethodName.AddChild, node);
		}

		public override void _EnterTree()
		{
			Instance = this;
			try
			{
				string dir = ProjectSettings.GlobalizePath("res://logs");
				try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, ".gdignore"), ""); }
				catch { dir = ProjectSettings.GlobalizePath("user://"); }
				_path = Path.Combine(dir, "lag_trace.log");
				if (File.Exists(_path)) File.Copy(_path, Path.Combine(dir, "lag_trace.prev.log"), true);
				_file = new StreamWriter(new System.IO.FileStream(_path, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
				Write($"=== Trace des à-coups — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — Godot {Engine.GetVersionInfo()["string"]} — {OS.GetName()} ===");
				Write($"Une image de plus de {SpikeMs:F0} ms est notée avec les événements des 4 secondes précédentes.");
				Write($"GPU : {RenderingServer.GetVideoAdapterName()} | fenêtre : {DisplayServer.WindowGetSize()}");
			}
			catch (Exception e) { GD.PrintErr("[LagTracer] " + e.Message); }
			_gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2); _mem = GC.GetTotalMemory(false);
			_lastFrameT = Now;
		}

		public override void _ExitTree()
		{
			Write($"=== fin : {_frames} images, {_spikes} à-coups, image max {_maxFrame:F0} ms ===");
			_file?.Dispose();
			_file = null;
			if (Instance == this) Instance = null;
		}

		// ------------------------------------------------------------------ API

		/// <summary>Note un fait dans la mémoire tampon (il ressortira si un à-coup suit).</summary>
		public static void Event(string text)
		{
			double t = Now;
			Ring.Enqueue(new Entry(t, text));
			while (Ring.Count > 0 && t - Ring.Peek().T > 4.0) Ring.Dequeue();
		}

		/// <summary>Mesure un bloc : le temps est cumulé par nom pour l'image en cours, et noté comme événement s'il dépasse 8 ms.</summary>
		public static void Measure(string name, Action action)
		{
			long t0 = Stopwatch.GetTimestamp();
			try { action(); }
			finally { Record(name, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency); }
		}

		public readonly struct ScopeTimer : IDisposable
		{
			private readonly string _name; private readonly long _t0;
			public ScopeTimer(string name) { _name = name; _t0 = Stopwatch.GetTimestamp(); }
			public void Dispose() => Record(_name, (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency);
		}

		public static ScopeTimer Scope(string name) => new ScopeTimer(name);

		private static void Record(string name, double ms)
		{
			Phases.TryGetValue(name, out double acc);
			Phases[name] = acc + ms;
			if (ms >= 8.0) Event($"{name} : {ms:F0} ms");
		}

		private static void Write(string line)
		{
			if (_file == null) return;
			try { _file.WriteLine(line); } catch { }
		}

		// ------------------------------------------------------------------ boucle

		public override void _Process(double delta)
		{
			double now = Now;
			double frameMs = (now - _lastFrameT) * 1000.0;
			_lastFrameT = now;
			_frames++; _sumFrame += frameMs; if (frameMs > _maxFrame) _maxFrame = frameMs;

			// flux réseau coupé : plus de snapshot du serveur
			int snaps = SnapshotCount();
			string net = NetState();
			if (net == "Playing")
			{
				if (_lastSnap < 0 || snaps != _lastSnap) { if (_gap) Write($"[{now:F3}] RÉSEAU : le flux est revenu après {(now - _lastSnapT) * 1000:F0} ms sans snapshot"); _gap = false; _lastSnap = snaps; _lastSnapT = now; }
				else if (!_gap && now - _lastSnapT > 0.3) { _gap = true; Write($"[{now:F3}] RÉSEAU : aucun snapshot du serveur depuis {(now - _lastSnapT) * 1000:F0} ms (coupure réseau ou serveur lent)"); }
			}

			if (frameMs >= SpikeMs) { _spikes++; DumpSpike(now, frameMs); }
			Phases.Clear();

			if (now >= _summaryAt)
			{
				_summaryAt = now + 10;
				Write($"[{now:F1}] résumé 10 s : {_frames} images, moyenne {_sumFrame / Math.Max(1, _frames):F1} ms, max {_maxFrame:F0} ms, à-coups {_spikes}, mém. {GC.GetTotalMemory(false) / 1048576} Mo");
				_frames = 0; _sumFrame = 0; _maxFrame = 0;
			}
		}

		private void DumpSpike(double now, double frameMs)
		{
			var sb = new StringBuilder();
			sb.Append("\n[").Append(now.ToString("F3")).Append("] ÀCOUP : image de ").Append(frameMs.ToString("F0")).Append(" ms\n");

			int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
			long mem = GC.GetTotalMemory(false);
			sb.Append($"  ramasse-miettes depuis l'image précédente : gén0 +{g0 - _gc0}, gén1 +{g1 - _gc1}, gén2 +{g2 - _gc2} | mémoire gérée {mem / 1048576} Mo ({(mem - _mem) / 1048576:+#;-#;0} Mo)\n");
			if (g2 - _gc2 > 0 || g1 - _gc1 > 0) sb.Append("  >>> une collecte du ramasse-miettes a eu lieu pendant cette image : cause probable\n");
			_gc0 = g0; _gc1 = g1; _gc2 = g2; _mem = mem;

			sb.Append($"  moteur : nœuds {(int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}, objets {(int)Performance.GetMonitor(Performance.Monitor.ObjectCount)}, ressources {(int)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)}, " +
				$"draw calls {(int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}, primitives {(int)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)}, " +
				$"vidéo {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} Mo, textures {Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576:F0} Mo\n");
			sb.Append($"  durée moteur : traitement {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} ms, physique {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:F1} ms, FPS {Performance.GetMonitor(Performance.Monitor.TimeFps):F0}\n");
			sb.Append($"  réseau : {NetState()}, dernier snapshot il y a {(now - _lastSnapT) * 1000:F0} ms\n");

			if (Phases.Count > 0)
			{
				sb.Append("  temps par partie du code dans cette image :");
				foreach (var kv in Phases) sb.Append($" {kv.Key} {kv.Value:F1} ms;");
				sb.Append('\n');
			}
			sb.Append("  événements des 4 dernières secondes (t = secondes avant la fin de l'image) :\n");
			int n = 0;
			foreach (var e in Ring)
			{
				if (now - e.T > 4.0) continue;
				sb.Append($"    -{now - e.T:F3} s  {e.Text}\n");
				if (++n > 80) { sb.Append("    … (suite coupée)\n"); break; }
			}
			if (n == 0) sb.Append("    (aucun événement noté : le coupable est hors des points surveillés — ramasse-miettes, moteur ou carte graphique)\n");
			Write(sb.ToString());
		}
	}
}
