using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Les modes de jeu hors battle royale : Deathmatch, Team Deathmatch, Contrôle, Capture du drapeau, Sabotage.</summary>
	[Collection("Network")]
	public class ModeTests
	{
		private static WorldData World()
		{
			var d = WorldData.CreateFlat(300);
			float g = d.HeightAt(0, 0);
			Vec3 P(float x, float z) => new Vec3(x, g, z);
			d.Points.Add(new MapPoint { Kind = MapPointKind.SpawnTeam, Team = 0, Pos = P(-90, 0) });
			d.Points.Add(new MapPoint { Kind = MapPointKind.SpawnTeam, Team = 1, Pos = P(90, 0) });
			for (int i = 0; i < 4; i++) d.Points.Add(new MapPoint { Kind = MapPointKind.SpawnFfa, Pos = P(-60 + i * 40, i % 2 == 0 ? -40 : 40) });
			float[] xs = { -80, -40, 0, 40, 80 };
			for (int i = 0; i < 5; i++) d.Points.Add(new MapPoint { Kind = MapPointKind.ControlPoint, Index = (byte)i, Team = (byte)(i == 0 ? 0 : i == 4 ? 1 : 255), Pos = P(xs[i], 0), Radius = 6f });
			d.Points.Add(new MapPoint { Kind = MapPointKind.FlagBase, Team = 0, Pos = P(-100, 0), Radius = 3f });
			d.Points.Add(new MapPoint { Kind = MapPointKind.FlagBase, Team = 1, Pos = P(100, 0), Radius = 3f });
			d.Points.Add(new MapPoint { Kind = MapPointKind.BombSite, Index = 0, Pos = P(30, -40), Radius = 8f });
			d.Points.Add(new MapPoint { Kind = MapPointKind.BombSite, Index = 1, Pos = P(30, 40), Radius = 8f });
			return d;
		}

		private static void Balance(params (string key, double value)[] kv)
		{
			var text = "{" + string.Join(",", kv.Select(p => $"\"{p.key}\": {p.value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")) + "}";
			GameBalance.ApplyFileText(text, new List<string>());
		}

		private static void Reset(params string[] keys)
		{
			foreach (var k in keys) Balance((k, GameBalance.Entries.First(e => e.Key == k).Default));
		}

		private static Rig Make(GameMode mode, int teamSize, int humans = 1, bool bots = false, Action<OpenEsport.Server.ServerOptions> more = null)
			=> new Rig(o =>
			{
				o.Mode = mode; o.Match.TeamSize = teamSize; o.MinPlayers = humans; o.LobbyCountdown = 0.3f; o.FillBots = bots; o.EndLinger = 100f;
				o.Match.MaxPlayers = Modes.Get(mode).PlayersFor(teamSize);
				more?.Invoke(o);
			}, World());

		private static Rig.Bot[] Join(Rig rig, int n)
		{
			var list = new Rig.Bot[n];
			for (int i = 0; i < n; i++) list[i] = rig.Connect("J" + i);
			foreach (var b in list) Assert.True(rig.WaitPlaying(b));
			Assert.True(RunUntil(rig, () => rig.Server.Phase == MatchPhase.Playing, 6), "la partie doit démarrer");
			rig.Run(0.4);
			return list;
		}

		/// <summary>Fait tourner les clients (60 Hz, avec leurs commandes) jusqu'à ce que la condition soit vraie.</summary>
		private static bool RunUntil(Rig rig, Func<bool> cond, double seconds)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			while (sw.Elapsed.TotalSeconds < seconds) { rig.Run(0.1); if (cond()) return true; }
			return cond();
		}

		private static OpenEsport.Server.ServerPlayer SP(Rig rig, Rig.Bot b) => rig.Server.GetPlayer(b.C.PlayerId);

		[Fact]
		public void Deathmatch_First_To_The_Kill_Target_Wins_And_Players_Respawn()
		{
			Balance(("mode.dmKills", 3));
			try
			{
				using var rig = Make(GameMode.Deathmatch, 1, humans: 2);
				var p = Join(rig, 2);
				int a = p[0].C.PlayerId, b = p[1].C.PlayerId;
				Assert.NotEqual(SP(rig, p[0]).Team, SP(rig, p[1]).Team);                // chacun sa propre équipe
				rig.OnServer(() => rig.Server.AdminKill(a, b));
				Assert.False(SP(rig, p[1]).Alive);
				Assert.True(RunUntil(rig, () => SP(rig, p[1]).Alive, 6), "il réapparaît");
				Assert.True(SP(rig, p[1]).Inventory.Active != null, "avec son équipement de classe");
				rig.Run(2.2);                                                           // fin de l'invulnérabilité
				for (int i = 0; i < 2; i++) { rig.OnServer(() => rig.Server.AdminKill(a, b)); RunUntil(rig, () => SP(rig, p[1]).Alive, 6); rig.Run(0.2); }
				Assert.Equal(MatchPhase.Over, rig.Server.Phase);
				Assert.Equal(SP(rig, p[0]).Team, rig.Server.WinnerTeam);
			}
			finally { Reset("mode.dmKills"); }
		}

		[Fact]
		public void TeamDeathmatch_Balances_Teams_And_Counts_Kills_For_The_Team()
		{
			Balance(("mode.tdmKills", 2));
			try
			{
				using var rig = Make(GameMode.TeamDeathmatch, 2, humans: 4);
				var p = Join(rig, 4);
				Assert.Equal(2, p.Count(b => SP(rig, b).Team == 0)); Assert.Equal(2, p.Count(b => SP(rig, b).Team == 1));
				var a = p.First(b => SP(rig, b).Team == 0); var enemies = p.Where(b => SP(rig, b).Team == 1).ToArray();
				rig.OnServer(() => rig.Server.AdminKill(a.C.PlayerId, enemies[0].C.PlayerId));
				Assert.Equal(1, rig.Server.ScoreOf(0));
				// un tir allié ne rapporte rien
				var mate = p.First(b => b != a && SP(rig, b).Team == 0);
				rig.OnServer(() => rig.Server.AdminKill(a.C.PlayerId, mate.C.PlayerId));
				Assert.Equal(1, rig.Server.ScoreOf(0));
				rig.OnServer(() => rig.Server.AdminKill(a.C.PlayerId, enemies[1].C.PlayerId));
				Assert.Equal(MatchPhase.Over, rig.Server.Phase);
				Assert.Equal(0, rig.Server.WinnerTeam);
			}
			finally { Reset("mode.tdmKills"); }
		}

		[Fact]
		public void Control_Points_Are_Captured_Contested_And_Score_The_Difference()
		{
			Balance(("mode.controlCaptureSeconds", 1), ("mode.controlTickSeconds", 0.25));
			try
			{
				using var rig = Make(GameMode.Control, 5, humans: 2);
				var p = Join(rig, 2);
				var blue = p.First(b => SP(rig, b).Team == 0); var red = p.First(b => b != blue);
				// le joueur bleu capture A : écart de 2 points tenus
				rig.Run(1.0);
				Assert.Equal(0, rig.Server.ScoreOf(0));                                  // une base chacune : égalité, personne ne marque
				rig.Teleport(blue, new Vec3(-40, rig.World.HeightAt(0, 0), 0));
				rig.Run(1.6);
				Assert.Equal(0, blue.C.Mode.Points[1].Owner);
				Assert.True(rig.Server.ScoreOf(0) >= 1, "2 points tenus contre 1 : un point par tic");
				// les deux équipes sur le point B : capture bloquée
				rig.Teleport(blue, new Vec3(0, rig.World.HeightAt(0, 0), 0)); rig.Teleport(red, new Vec3(1, rig.World.HeightAt(0, 0), 1));
				rig.Run(1.5);
				Assert.Equal(255, blue.C.Mode.Points[2].Owner);
				Assert.True(blue.C.Mode.Points[2].Contested);
				// le rouge reste seul sur B : il le capture
				rig.Teleport(blue, new Vec3(-90, rig.World.HeightAt(0, 0), 0));
				rig.Run(1.8);
				Assert.Equal(1, red.C.Mode.Points[2].Owner);
			}
			finally { Reset("mode.controlCaptureSeconds", "mode.controlTickSeconds"); }
		}

		[Fact]
		public void Control_Reaching_The_Target_Ends_The_Game()
		{
			Balance(("mode.controlScore", 3), ("mode.controlCaptureSeconds", 0.5), ("mode.controlTickSeconds", 0.25));
			try
			{
				using var rig = Make(GameMode.Control, 5, humans: 2);
				var p = Join(rig, 2);
				var blue = p.First(b => SP(rig, b).Team == 0);
				rig.Teleport(blue, new Vec3(-40, rig.World.HeightAt(0, 0), 0));         // il capture A : 2 points tenus contre 1
				Assert.True(RunUntil(rig, () => rig.Server.Phase == MatchPhase.Over, 6), "100 points (ici 3) : la partie se termine");
				Assert.Equal(0, rig.Server.WinnerTeam);
			}
			finally { Reset("mode.controlScore", "mode.controlCaptureSeconds", "mode.controlTickSeconds"); }
		}

		[Fact]
		public void Capture_The_Flag_Carry_It_Home_To_Score_And_A_Dead_Carrier_Drops_It()
		{
			using var rig = Make(GameMode.CaptureTheFlag, 4, humans: 2);
			var p = Join(rig, 2);
			var blue = p.First(b => SP(rig, b).Team == 0); var red = p.First(b => b != blue);
			float g = rig.World.HeightAt(0, 0);
			// le bleu prend le drapeau rouge (à sa base, x = 100)
			rig.Teleport(blue, new Vec3(100, g, 0)); rig.Run(0.5);
			Assert.Equal(1, blue.C.Mode.Flag1.State);
			Assert.Equal(blue.C.PlayerId, blue.C.Mode.Flag1.Carrier);
			// il meurt : le drapeau tombe sur place
			rig.OnServer(() => rig.Server.AdminKill(red.C.PlayerId, blue.C.PlayerId)); rig.Run(0.3);
			Assert.Equal(2, red.C.Mode.Flag1.State);
			// le rouge, allié du drapeau, le ramène à sa base
			rig.Teleport(red, new Vec3(100, g, 0)); rig.Run(0.4);
			Assert.Equal(0, red.C.Mode.Flag1.State);
			// le bleu réapparaît, reprend le drapeau et le ramène : une prise
			Assert.True(RunUntil(rig, () => SP(rig, blue).Alive, 8));
			rig.Teleport(red, new Vec3(0, g, 90));
			rig.Teleport(blue, new Vec3(100, g, 0)); rig.Run(0.4);
			Assert.Equal(1, blue.C.Mode.Flag1.State);
			rig.Teleport(blue, new Vec3(-100, g, 0)); rig.Run(0.5);
			Assert.Equal(1, rig.Server.ScoreOf(0));
			Assert.Equal(0, blue.C.Mode.Flag1.State);
		}

		[Fact]
		public void Sabotage_Plant_Explode_Earn_Money_And_Buy()
		{
			Balance(("sabotage.buySeconds", 2.5), ("sabotage.plantSeconds", 0.5), ("sabotage.bombSeconds", 1.0), ("sabotage.endSeconds", 2.0), ("sabotage.roundsToWin", 2));
			try
			{
				using var rig = Make(GameMode.Sabotage, 4, humans: 2);
				var p = Join(rig, 2);
				var att = p.First(b => SP(rig, b).Team == 0);           // les bleus attaquent d'abord
				var def = p.First(b => b != att);
				Assert.Equal(1, att.C.Mode.MySide); Assert.Equal(0, def.C.Mode.MySide);
				// phase d'achat : on peut acheter, pas bouger
				Assert.True(att.C.Mode.CanBuy);
				int money = att.C.Mode.Money;
				Assert.Equal(800, money);
				att.C.SendCommand(CommandType.Buy, 2); rig.Run(0.3);                // SMG : 1200 > 800, refusé
				Assert.Equal(800, SP(rig, att).Money);
				att.C.SendCommand(CommandType.Buy, 9); rig.Run(0.3);                // petit soin : 100
				Assert.Equal(700, SP(rig, att).Money);
				// la manche démarre ; l'attaquant porteur de la bombe la pose sur le site A
				Assert.True(RunUntil(rig, () => att.C.Mode.Stage == (byte)SabotageStage.Round, 3));
				float g = rig.World.HeightAt(0, 0);
				rig.Teleport(att, new Vec3(30, g, -40));
				att.Buttons = Buttons.Interact;
				Assert.True(RunUntil(rig, () => att.C.Mode.BombState == 2, 4), "la bombe doit être posée");
				att.Buttons = Buttons.None;
				// elle explose : les attaquants gagnent la manche et touchent la prime
				Assert.True(RunUntil(rig, () => att.C.Mode.RoundWinner == 1, 3));
				rig.Run(0.2);
				Assert.True(att.C.Mode.Money >= 700 + 3250 + 800 - 100, $"argent {att.C.Mode.Money}");
				Assert.Equal(1, rig.Server.ScoreOf(0));
				// deuxième manche gagnée : fin de partie (2 manches pour gagner), et la mi-temps a changé les camps entre-temps
				Assert.True(RunUntil(rig, () => att.C.Mode.Round >= 2 && att.C.Mode.Stage == (byte)SabotageStage.Round, 9));
				Assert.Equal(0, att.C.Mode.MySide);                                   // mi-temps : camps échangés
			}
			finally { Reset("sabotage.buySeconds", "sabotage.plantSeconds", "sabotage.bombSeconds", "sabotage.endSeconds", "sabotage.roundsToWin"); }
		}

		[Fact]
		public void Sabotage_Defenders_Defuse_The_Bomb()
		{
			Balance(("sabotage.buySeconds", 0.3), ("sabotage.plantSeconds", 0.3), ("sabotage.bombSeconds", 20), ("sabotage.defuseSeconds", 0.5), ("sabotage.endSeconds", 5));
			try
			{
				using var rig = Make(GameMode.Sabotage, 4, humans: 2);
				var p = Join(rig, 2);
				var att = p.First(b => SP(rig, b).Team == 0); var def = p.First(b => b != att);
				Assert.True(RunUntil(rig, () => att.C.Mode.Stage == (byte)SabotageStage.Round, 3));
				float g = rig.World.HeightAt(0, 0);
				rig.Teleport(att, new Vec3(30, g, 40)); att.Buttons = Buttons.Interact;
				Assert.True(RunUntil(rig, () => att.C.Mode.BombState == 2, 3), rig.Server.ModeDebug());
				att.Buttons = Buttons.None;
				rig.Teleport(att, new Vec3(-90, g, 0));
				rig.Teleport(def, new Vec3(30, g, 40)); def.Buttons = Buttons.Interact;
				Assert.True(RunUntil(rig, () => def.C.Mode.BombState == 3, 3), "la bombe doit être désamorcée");
				Assert.Equal(0, def.C.Mode.RoundWinner);
			}
			finally { Reset("sabotage.buySeconds", "sabotage.plantSeconds", "sabotage.bombSeconds", "sabotage.defuseSeconds", "sabotage.endSeconds"); }
		}

		[Fact]
		public void Bots_Fill_The_Teams_And_Play_The_Objective_In_Control()
		{
			Balance(("mode.controlCaptureSeconds", 2));
			try
			{
				using var rig = Make(GameMode.Control, 5, humans: 1, bots: true);
				var a = rig.Connect("Humain");
				Assert.True(rig.WaitPlaying(a));
				Assert.True(RunUntil(rig, () => rig.Server.Phase == MatchPhase.Playing, 6));
				Assert.Equal(10, rig.Server.HumanCount + rig.Server.BotCount);
				int blue = 0, red = 0;
				rig.OnServer(() => { for (int i = 0; i < 64; i++) { var q = rig.Server.GetPlayer(i); if (q == null) continue; if (q.Team == 0) blue++; else red++; } });
				Assert.Equal(5, blue); Assert.Equal(5, red);
				// les bots vont capturer des points : au bout d'un moment au moins un point de contrôle a changé de main
				Assert.True(RunUntil(rig, () => a.C.Mode.Points.Count(c => c.Owner != 255) > 2 || a.C.Mode.Points.Any(c => c.Progress != 0 && c.Owner == 255), 45), "les bots doivent s'emparer de points");
			}
			finally { Reset("mode.controlCaptureSeconds"); }
		}
	}
}
