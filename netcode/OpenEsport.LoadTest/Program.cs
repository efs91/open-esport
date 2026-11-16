using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.LoadTest
{
	/// <summary>
	/// Simule N joueurs qui courent, sautent, glissent, ramassent et tirent, contre un serveur réel, et mesure
	/// ce que voit un client (fluidité, débit, corrections) ainsi que ce que rapporte le serveur (durée des ticks, charge).
	/// </summary>
	public static class Program
	{
		private sealed class Bot
		{
			public GameClient Client;
			public Random Rng;
			public float Yaw, Pitch, TurnTimer, ActionTimer;
			public bool Sprint, Slide;
			public int Index;
			public readonly List<InterpolatedEntity> Seen = new List<InterpolatedEntity>(64);
		}

		public static int Main(string[] args)
		{
			string host = "127.0.0.1", worldPath = null, outFile = null, makeWorld = null;
			int port = 27015, clients = 100, seconds = 60, worldSize = 2000, joinRateMs = 40, tickHz = 60;
			bool aggressive = true;
			for (int i = 0; i < args.Length; i++)
			{
				string a = args[i];
				string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("valeur manquante pour " + a);
				switch (a)
				{
					case "--host": host = Next(); break;
					case "--port": port = int.Parse(Next()); break;
					case "--clients": clients = int.Parse(Next()); break;
					case "--seconds": seconds = int.Parse(Next()); break;
					case "--world": worldPath = Next(); break;
					case "--out": outFile = Next(); break;
					case "--tick": tickHz = int.Parse(Next()); break;
					case "--make-world": makeWorld = Next(); break;
					case "--size": worldSize = int.Parse(Next()); break;
					case "--peaceful": aggressive = false; break;
					case "--join-rate-ms": joinRateMs = int.Parse(Next()); break;
					default: Console.Error.WriteLine("option inconnue : " + a); return 2;
				}
			}

			if (makeWorld != null)
			{
				var w = WorldData.CreateProcedural(worldSize, 1, 2500);
				using (var fs = File.Create(makeWorld)) w.Save(fs);
				Console.WriteLine($"Carte écrite : {makeWorld} ({new FileInfo(makeWorld).Length / 1024} Ko), {w.LootSpawns.Count} objets, empreinte {w.Checksum():X8}");
				return 0;
			}

			var world = worldPath != null ? WorldData.LoadFile(worldPath) : WorldData.CreateProcedural(worldSize, 1, 2500);
			var collision = new StaticWorld(world);
			uint checksum = world.Checksum();
			Console.WriteLine($"Test de charge : {clients} robots -> {host}:{port}, {seconds} s, carte {world.Width}x{world.Depth} ({checksum:X8})");

			var bots = new List<Bot>();
			for (int i = 0; i < clients; i++)
			{
				var c = new GameClient(collision, checksum, updateTimeMs: 4);
				if (Environment.GetEnvironmentVariable("OE_DEBUG") == "1" && i < 6) { int idx = i; int shown = 0; c.DebugLog = m => { if (shown++ < 4) Console.WriteLine($"[robot {idx}] {m}"); }; }
				c.Connect(host, port, "Robot" + i);
				bots.Add(new Bot { Client = c, Rng = new Random(1000 + i), Index = i, Yaw = (float)(i * 0.37) });
				// connexion échelonnée, comme de vrais joueurs qui arrivent
				var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * joinRateMs / 1000;
				while (Stopwatch.GetTimestamp() < end) { foreach (var b in bots) b.Client.Poll(); Thread.Sleep(1); }
			}

			// questionnaire des statistiques du serveur (message hors connexion)
			string serverReport = "{}";
			var qListener = new EventBasedNetListener();
			var query = new NetManager(qListener) { UnconnectedMessagesEnabled = true };
			qListener.NetworkReceiveUnconnectedEvent += (ep, reader, type) => { serverReport = Encoding.UTF8.GetString(reader.RawData, reader.UserDataOffset, reader.UserDataSize); };
			query.Start();
			var serverEp = new IPEndPoint(Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), port);

			var samples = new List<Dictionary<string, double>>();
			long freq = Stopwatch.Frequency;
			long start = Stopwatch.GetTimestamp();
			long tickIndex = 0;
			long nextReport = start + 5 * freq;
			long lastBytesIn = 0, lastSnaps = 0;
			var lastCorrections = 0; double lastReportSec = 0;
			double maxLoopMs = 0, sumLoopMs = 0; long loops = 0;

			long endAt = start + (long)seconds * freq;
			while (Stopwatch.GetTimestamp() < endAt)
			{
				long now = Stopwatch.GetTimestamp();
				while (start + (tickIndex + 1) * freq / tickHz <= now)
				{
					tickIndex++;
					long t0 = Stopwatch.GetTimestamp();
					float dt = 1f / tickHz;
					foreach (var b in bots)
					{
						b.Client.Poll();
						if (b.Client.State != NetState.Playing) continue;
						Drive(b, dt, aggressive);
					}
					double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / freq;
					maxLoopMs = Math.Max(maxLoopMs, ms); sumLoopMs += ms; loops++;
				}

				if (now >= nextReport)
				{
					nextReport += 5 * freq;
					query.SendUnconnectedMessage(Encoding.ASCII.GetBytes("STATS"), serverEp);
					query.PollEvents();
					double elapsed = (now - start) / (double)freq;
					var playing = bots.Where(b => b.Client.State == NetState.Playing).ToList();
					long bytesIn = playing.Sum(b => b.Client.BytesReceived);
					long snaps = playing.Sum(b => (long)b.Client.SnapshotsReceived);
					int corr = playing.Sum(b => b.Client.SmallCorrections);
					double window = elapsed - lastReportSec; lastReportSec = elapsed;
					var s = new Dictionary<string, double>
					{
						["t"] = elapsed,
						["playing"] = playing.Count,
						["pingMs"] = playing.Count > 0 ? playing.Average(b => b.Client.Ping) : 0,
						["clientKBpsIn"] = playing.Count > 0 ? (bytesIn - lastBytesIn) / 1024.0 / window / playing.Count : 0,
						["snapshotsPerSec"] = playing.Count > 0 ? (snaps - lastSnaps) / window / playing.Count : 0,
						["entitiesKnown"] = playing.Count > 0 ? playing.Average(b => b.Client.EntityCount) : 0,
						["correctionsPerMin"] = playing.Count > 0 ? (corr - lastCorrections) / window * 60.0 / playing.Count : 0,
						["maxCorrectionM"] = playing.Count > 0 ? playing.Max(b => b.Client.MaxSmallCorrection) : 0,
						["botLoopMsMax"] = maxLoopMs,
					};
					lastBytesIn = bytesIn; lastSnaps = snaps; lastCorrections = corr;
					AddServerFields(serverReport, s);
					samples.Add(s);
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
						"t={0,3:F0}s | robots {1,3} | ping {2,4:F1} ms | client {3,5:F1} Ko/s, {4,4:F1} snap/s, {5,4:F0} entités | corrections {6,5:F2}/min, max {7:F3} m | serveur : tick p99 {8,6:F2} ms, max {9,6:F2} ms, dépass. {10:F0}, sortie {11:F0} Ko/s, {12:F0} o/snap, GC pause {13:F0} ms",
						elapsed, playing.Count, s["pingMs"], s["clientKBpsIn"], s["snapshotsPerSec"], s["entitiesKnown"], s["correctionsPerMin"], s["maxCorrectionM"],
						Get(s, "tickMsP99"), Get(s, "tickMsMax"), Get(s, "overruns"), Get(s, "outKBps"), Get(s, "avgSnapshotBytes"), Get(s, "gcPauseMs")));
					maxLoopMs = 0;
				}
				Thread.Sleep(1);
			}

			// synthèse sur la seconde moitié du test (le début contient les connexions)
			var steady = samples.Skip(samples.Count / 3).ToList();
			Console.WriteLine();
			Console.WriteLine("=== Synthèse (régime établi) ===");
			if (steady.Count > 0)
			{
				void Line(string label, string key, string unit)
				{
					var v = steady.Where(x => x.ContainsKey(key)).Select(x => x[key]).ToList();
					if (v.Count == 0) return;
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-34} moyenne {1,9:F2} | pire {2,9:F2} {3}", label, v.Average(), v.Max(), unit));
				}
				Line("Joueurs connectés", "playing", "");
				Line("Durée d'un tick serveur, p99", "tickMsP99", "ms");
				Line("Durée d'un tick serveur, max", "tickMsMax", "ms");
				Line("Débit sortant serveur", "outKBps", "Ko/s");
				Line("Débit reçu par client", "clientKBpsIn", "Ko/s");
				Line("Taille moyenne d'un snapshot", "avgSnapshotBytes", "octets");
				Line("Snapshots reçus par client", "snapshotsPerSec", "/s");
				Line("Divergences de prédiction (<3 m)", "correctionsPerMin", "/min/client");
				Line("Plus grande divergence", "maxCorrectionM", "m");
				Line("Allocations serveur", "allocMBps", "Mo/s");
				Line("Pause cumulée du GC serveur", "gcPauseMs", "ms");
			}
			if (outFile != null)
			{
				File.WriteAllText(outFile, "[" + string.Join(",", samples.Select(s => "{" + string.Join(",", s.Select(kv => $"\"{kv.Key}\":{kv.Value.ToString(CultureInfo.InvariantCulture)}")) + "}")) + "]");
			}

			foreach (var b in bots) b.Client.Disconnect();
			query.Stop();
			return 0;
		}

		private static double Get(Dictionary<string, double> s, string key) => s.TryGetValue(key, out var v) ? v : double.NaN;

		private static void AddServerFields(string json, Dictionary<string, double> s)
		{
			// le rapport du serveur est un objet JSON plat de nombres
			json = json.Trim().TrimStart('{').TrimEnd('}');
			foreach (var part in json.Split(','))
			{
				var kv = part.Split(':');
				if (kv.Length != 2) continue;
				if (double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
					s[kv[0].Trim('"')] = v;
			}
		}

		/// <summary>Comportement d'un robot : il court dans une direction, change de cap, saute, glisse, vise le plus proche ennemi et tire.</summary>
		private static void Drive(Bot b, float dt, bool aggressive)
		{
			var c = b.Client;
			b.TurnTimer -= dt;
			b.ActionTimer -= dt;
			if (b.TurnTimer <= 0f)
			{
				b.Yaw += (float)(b.Rng.NextDouble() * 2.2 - 1.1);
				b.TurnTimer = 1f + (float)b.Rng.NextDouble() * 2.5f;
				b.Sprint = b.Rng.Next(3) != 0;
			}

			var buttons = Buttons.None;
			if (b.Sprint) buttons |= Buttons.Sprint;
			if (b.Rng.Next(200) == 0) buttons |= Buttons.Jump;
			if (b.ActionTimer <= 0f)
			{
				b.Slide = b.Rng.Next(6) == 0;
				b.ActionTimer = 2f + (float)b.Rng.NextDouble() * 3f;
			}
			if (b.Slide && b.ActionTimer > 1.7f) buttons |= Buttons.Sprint | Buttons.Crouch;

			float pitch = 0f;
			if (aggressive)
			{
				c.SampleEntities(b.Seen);
				float best = 90f * 90f;
				InterpolatedEntity? target = null;
				foreach (var e in b.Seen)
				{
					if (e.Kind != EntityKind.Player || e.Team == c.TeamId || e.Has(EntityFlags.Dead)) continue;
					float dx = e.Pos.X - c.Predicted.Pos.X, dz = e.Pos.Z - c.Predicted.Pos.Z;
					float d2 = dx * dx + dz * dz;
					if (d2 < best) { best = d2; target = e; }
				}
				if (target.HasValue)
				{
					Vec3 eye = c.ViewPosition + new Vec3(0, 1.6f, 0);
					Vec3 d = target.Value.Pos + new Vec3(0, 1.0f, 0) - eye;
					b.Yaw = MathF.Atan2(-d.X, -d.Z);
					pitch = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
					buttons |= Buttons.Fire;
				}
				if (c.Magazine == 0 && !c.Reloading) buttons |= Buttons.Reload;
				if (b.Rng.Next(400) == 0) buttons |= Buttons.Reload;
			}

			int slot = 0;
			if (c.ClientTick == 30) slot = 1; // prend l'arme en main au départ
			c.Tick(1f, 0f, buttons, slot, b.Yaw, pitch);
		}
	}
}
