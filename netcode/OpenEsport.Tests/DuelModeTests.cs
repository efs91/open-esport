using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Mode 1 VS 1 : carte « Duel » (assets/maps/duel.world, tools/map/gen_duel_map.py), manches, équipement au hasard, choisi ou imposé.</summary>
	[Collection("Network")]
	public class DuelModeTests
	{
		private static WorldData Load([CallerFilePath] string here = "")
		{
			string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
			return WorldData.LoadFile(Path.Combine(root, "assets", "maps", Modes.Get(GameMode.Duel).MapFile));
		}

		private static Rig Duel(Action<ServerOptions> more = null) => new Rig(o =>
		{
			o.Mode = GameMode.Duel; o.Match.TeamSize = 1; o.Match.MaxPlayers = 2; o.MinPlayers = 2; o.LobbyCountdown = 0.3f; o.FillBots = false; o.EndLinger = 100f;
			more?.Invoke(o);
		}, Load());

		private static (Rig.Bot a, Rig.Bot b) Start(Rig rig)
		{
			var a = rig.Connect("A"); var b = rig.Connect("B");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			Assert.True(rig.WaitUntil(() => rig.Server.Phase == MatchPhase.Playing, 6), "la partie doit démarrer");
			rig.Run(0.3);
			return (a, b);
		}

		private static T OnServer<T>(Rig rig, Func<T> f) { T r = default; rig.OnServer(() => r = f()); return r; }

		[Fact]
		public void The_Map_Has_One_Spawn_Side_Per_Player_And_A_Central_Block_To_Hide_Behind_And_Climb()
		{
			var d = Load();
			var spawns = d.PointsOf(MapPointKind.SpawnTeam).ToList();
			Assert.Contains(spawns, s => s.Team == 0 && s.Pos.X < -8f);
			Assert.Contains(spawns, s => s.Team == 1 && s.Pos.X > 8f);
			var world = new StaticWorld(d);
			// le bloc du milieu : on ne voit pas d'un côté à l'autre à hauteur des yeux, mais son dessus est entre la hauteur d'un saut et celle de l'escalade
			Assert.True(world.Raycast(new Vec3(-8f, 1.6f, 0f), new Vec3(1f, 0f, 0f), 16f, out _), "le bloc cache un joueur debout");
			float top = world.GroundAt(0f, 0f, 5f, Sim.StepHeight);
			Assert.InRange(top, Sim.ClimbMinRise, Sim.ClimbMaxRise);
		}

		[Fact]
		public void A_Loadout_Round_Trips_Through_Text_And_Packing()
		{
			Assert.True(DuelRules.TryParsePolicy("fixed:sniper,x4,pompe,point,flash,grande-recharge", out var policy, out var l));
			Assert.Equal(DuelPolicy.Fixed, policy);
			Assert.Equal(ItemIds.Sniper, l.WeaponChoice.Item);
			Assert.Equal(l, DuelLoadout.Unpack(l.Pack()));
			Assert.Equal(ItemIds.Sawnoff, l.Weapon2Choice.Item);
			Assert.Equal("sniper,x4,pompe,point,flash,grande-recharge", l.ToString());
			Assert.False(DuelRules.TryParsePolicy("fixed:bazooka,x4,pompe,point,flash,soins", out _, out _));
			var kit = DuelRules.Kit(l);
			Assert.Equal(1, kit.ShieldBig);
			Assert.Equal(ItemIds.Sawnoff, kit.Secondary);
			Assert.Equal(ItemIds.OpticRedDot, kit.SecondaryOptic);
			Assert.Equal(new[] { ItemIds.GrenadeFlash }, kit.Grenades);
		}

		[Fact]
		public void Both_Players_Get_The_Same_Random_Loadout_Rounds_Are_Won_By_Kills_And_The_Target_Ends_The_Match()
		{
			using var rig = Duel(o => o.ScoreLimit = 2);
			var (a, b) = Start(rig);
			var pa = OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId)); var pb = OnServer(rig, () => rig.Server.GetPlayer(b.C.PlayerId));
			Assert.NotEqual(pa.Team, pb.Team);
			Assert.Contains("stage=Prepare", OnServer(rig, () => rig.Server.ModeDebug()));
			var wa = OnServer(rig, () => pa.Inventory.Active.WeaponId);
			Assert.Equal(wa, OnServer(rig, () => pb.Inventory.Active.WeaponId));
			Assert.True(OnServer(rig, () => pa.Shield) > 0f, "bouclier plein");
			Assert.Equal(2, OnServer(rig, () => pa.Inventory.Weapons.Count(w => w != null)));
			Assert.NotEqual(OnServer(rig, () => pa.Inventory.Weapons[0].WeaponId), OnServer(rig, () => pa.Inventory.Weapons[1].WeaponId));
			// munitions infinies : la réserve revient toute seule
			var ammo = OnServer(rig, () => { var w = pa.Inventory.Weapons.First(x => WeaponDefs.Get(x.WeaponId).AmmoType != 0); return WeaponDefs.Get(w.WeaponId).AmmoType; });
			int full = OnServer(rig, () => pa.Inventory.GetAmmo(ammo));
			Assert.True(full > 0);
			rig.OnServer(() => pa.Inventory.TakeAmmo(ammo, full));
			rig.Run(0.2);
			Assert.Equal(full, OnServer(rig, () => pa.Inventory.GetAmmo(ammo)));
			Assert.True(rig.WaitUntil(() => rig.Server.ModeDebug().Contains("stage=Round "), 5), "la manche commence après la préparation");

			rig.OnServer(() => rig.Server.AdminKill(a.C.PlayerId, b.C.PlayerId));
			Assert.Equal(1, OnServer(rig, () => rig.Server.ScoreOf(pa.Team)));
			Assert.Contains("stage=RoundEnd", OnServer(rig, () => rig.Server.ModeDebug()));
			Assert.True(rig.WaitUntil(() => rig.Server.ModeDebug().Contains("round=2"), 5), "manche suivante");
			Assert.True(OnServer(rig, () => pb.Alive && pb.Health == 100f), "chacun repart avec toute sa vie");
			Assert.True(rig.WaitUntil(() => rig.Server.ModeDebug().Contains("stage=Round "), 5));
			rig.OnServer(() => rig.Server.AdminKill(a.C.PlayerId, b.C.PlayerId));
			Assert.Equal(MatchPhase.Over, OnServer(rig, () => rig.Server.Phase));
			Assert.Equal(2, OnServer(rig, () => rig.Server.ScoreOf(pa.Team)));
		}

		[Fact]
		public void In_Choice_Mode_Each_Player_Picks_His_Loadout_At_The_Start_And_Keeps_It()
		{
			using var rig = Duel(o => o.DuelPolicy = DuelPolicy.Choice);
			var (a, b) = Start(rig);
			Assert.Contains("stage=Pick", OnServer(rig, () => rig.Server.ModeDebug()));
			var la = new DuelLoadout(6, 4, 2, 1, 2, 1); var lb = new DuelLoadout(5, 1, 3, 3, 1, 0);
			void Pick(Rig.Bot bot, DuelLoadout l)
			{
				bot.C.SendCommand(CommandType.DuelPick, DuelRules.PickArg(0, l.Weapon)); bot.C.SendCommand(CommandType.DuelPick, DuelRules.PickArg(1, l.Optic));
				bot.C.SendCommand(CommandType.DuelPick, DuelRules.PickArg(2, l.Grenade)); bot.C.SendCommand(CommandType.DuelPick, DuelRules.PickArg(3, l.Heal));
				bot.C.SendCommand(CommandType.DuelPick, DuelRules.PickArg(4, l.Weapon2)); bot.C.SendCommand(CommandType.DuelPick, DuelRules.PickArg(5, l.Optic2));
			}
			Pick(a, la); Pick(b, lb);
			rig.Run(0.5);
			Assert.Contains("stage=Pick", OnServer(rig, () => rig.Server.ModeDebug()));         // réglé mais pas validé
			a.C.SendCommand(CommandType.DuelPick, DuelRules.ConfirmPick);
			b.C.SendCommand(CommandType.DuelPick, DuelRules.ConfirmPick);
			Assert.True(rig.WaitUntil(() => rig.Server.ModeDebug().Contains("stage=Prepare"), 3), "les deux ont choisi : la manche se prépare");
			Assert.Equal(ItemIds.Sniper, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Inventory.Active.WeaponId));
			Assert.Equal(ItemIds.Optic4x, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Inventory.Active.OpticId));
			Assert.Equal(ItemIds.Sawnoff, OnServer(rig, () => rig.Server.GetPlayer(b.C.PlayerId).Inventory.Active.WeaponId));
			Assert.True(rig.WaitUntil(() => a.C.ModeKnown && a.C.Mode.Mode == GameMode.Duel && a.C.Mode.OpponentLoadout.Known, 3));
			Assert.Equal(lb, a.C.Mode.OpponentLoadout);
		}

		[Fact]
		public void A_Player_Who_Leaves_Loses_The_Match()
		{
			using var rig = Duel();
			var (a, b) = Start(rig);
			int teamA = OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Team);
			b.C.Disconnect();
			Assert.True(rig.WaitUntil(() => rig.Server.Phase == MatchPhase.Over, 5), "la partie se termine");
			Assert.Equal(DuelRules.RoundsToWin, OnServer(rig, () => rig.Server.ScoreOf(teamA)));
		}
	}
}
