using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>
	/// Carte « Relais » du Deathmatch (assets/maps/relais.world, produite par tools/map/gen_relais_map.py puis l'exporteur) et règles d'arène :
	/// trois niveaux, tremplins et téléporteurs simulés pareil partout, chemins des bots, départ au pistolet, armes fixes qui reviennent, soins immédiats.
	/// </summary>
	[Collection("Network")]
	public class RelaisArenaTests
	{
		private static WorldData Load([CallerFilePath] string here = "")
		{
			string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
			return WorldData.LoadFile(Path.Combine(root, "assets", "maps", "relais.world"));
		}

		private static PlayerInput Idle(ushort seq, float yaw = 0f) => PlayerInput.Create(seq, 0f, 0f, Buttons.None, 0, yaw, 0f, 0);

		[Fact]
		public void The_Map_Has_Spawns_On_Three_Levels_Pads_Teleporters_And_Legendary_Power_Weapons()
		{
			var d = Load();
			var spawns = d.PointsOf(MapPointKind.SpawnFfa).ToList();
			Assert.True(spawns.Count >= 16, "16 apparitions");
			foreach (float level in new[] { 0f, 4f, 8f }) Assert.Contains(spawns, s => MathF.Abs(s.Pos.Y - level) < 0.3f);
			Assert.Equal(6, d.Jumps.Count);
			Assert.Equal(4, d.Teleports.Count);
			Assert.Contains(d.LootSpawns, l => l.ItemId == ItemIds.Sniper && l.Quality == 8);
			Assert.Contains(d.LootSpawns, l => l.ItemId == ItemIds.Lmg63 && l.Quality == 8);
			Assert.True(d.LootSpawns.Count(l => ItemDefs.Get(l.ItemId)?.Kind == ItemKind.Weapon) >= 8);
			// relue à l'identique (format 9)
			var ms = new MemoryStream(); d.Save(ms); ms.Position = 0;
			var back = WorldData.Load(ms);
			Assert.Equal(d.Checksum(), back.Checksum());
			Assert.Equal(d.Teleports[0].Yaw, back.Teleports[0].Yaw);
		}

		[Fact]
		public void Every_Jump_Pad_Lands_On_Its_Target_From_Anywhere_On_The_Pad()
		{
			var d = Load();
			var w = new StaticWorld(d);
			foreach (var pad in d.Jumps)
			{
				var starts = new List<Vec3> { pad.Pos };
				for (int k = 0; k < 4; k++) { float a = k * MathF.PI / 2 + 0.4f; starts.Add(new Vec3(pad.Pos.X + MathF.Cos(a) * pad.Radius * 0.8f, pad.Pos.Y, pad.Pos.Z + MathF.Sin(a) * pad.Radius * 0.8f)); }
				foreach (var start in starts)
				{
					var s = PlayerMovement.Spawn(start, w);
					bool flew = false;
					ushort seq = 1;
					for (int i = 0; i < 300; i++)
					{
						PlayerMovement.Step(ref s, Idle(seq++), w, 1f / 60f);
						if (!s.OnGround) flew = true;
						if (flew && s.OnGround && i > 10) break;
					}
					Assert.True(flew, $"le tremplin {pad.Pos} doit projeter");
					float dist = MathF.Sqrt((s.Pos.X - pad.Target.X) * (s.Pos.X - pad.Target.X) + (s.Pos.Z - pad.Target.Z) * (s.Pos.Z - pad.Target.Z));
					Assert.True(dist < 1.8f && MathF.Abs(s.Pos.Y - pad.Target.Y) < 0.3f, $"tremplin {pad.Pos} depuis {start} : arrivé en {s.Pos}, cible {pad.Target}");
				}
			}
		}

		[Fact]
		public void Every_Teleporter_Sends_To_Its_Exit_And_The_Exit_Does_Not_Send_Back()
		{
			var d = Load();
			var w = new StaticWorld(d);
			foreach (var tp in d.Teleports)
			{
				// on arrive en marchant vers le centre du téléporteur
				var s = PlayerMovement.Spawn(new Vec3(tp.Pos.X, tp.Pos.Y, tp.Pos.Z + 1.6f), w);
				ushort seq = 1;
				bool jumped = false;
				for (int i = 0; i < 90 && !jumped; i++)
				{
					var before = s.Pos;
					PlayerMovement.Step(ref s, PlayerInput.Create(seq++, 1f, 0f, Buttons.None, 0, 0f, 0f, 0), w, 1f / 60f);
					jumped = Vec3.Distance(before, s.Pos) > 4f;
				}
				Assert.True(jumped, $"le téléporteur {tp.Pos} doit envoyer ailleurs");
				Assert.True(Vec3.Distance(s.Pos, tp.Dest) < 0.6f, $"sortie attendue {tp.Dest}, arrivé en {s.Pos}");
				Assert.True(w.TeleportExit(s.Pos, out float yaw) && MathF.Abs(yaw - tp.Yaw) < 1e-4f, "le client reconnaît la sortie pour tourner la caméra");
				// immobile à la sortie : on n'est pas renvoyé
				var at = s.Pos;
				for (int i = 0; i < 60; i++) PlayerMovement.Step(ref s, Idle(seq++), w, 1f / 60f);
				Assert.True(Vec3.Distance(at, s.Pos) < 3f, "la sortie n'est pas une entrée");
			}
		}

		[Fact]
		public void Bots_Find_Paths_Between_Spawns_And_Up_To_The_Power_Weapons()
		{
			var d = Load();
			var nav = Navigation.For(d);
			Assert.IsType<LayeredNav>(nav);
			var spawns = d.PointsOf(MapPointKind.SpawnFfa).Select(p => p.Pos).ToList();
			var path = new List<Vec3>();
			var goals = new List<Vec3>(spawns);
			goals.AddRange(d.LootSpawns.Where(l => l.ItemId == ItemIds.Sniper || l.ItemId == ItemIds.Lmg63).Select(l => l.Pos));
			foreach (var g in goals)
				Assert.True(nav.FindPath(spawns[0], g, path, 60000), $"chemin de {spawns[0]} à {g}");
			foreach (var s in spawns)
				Assert.True(nav.FindPath(s, spawns[0], path, 60000), $"chemin de {s} à {spawns[0]}");
			// un chemin du sol vers la salle du Rail finit bien au niveau +8
			var rail = d.LootSpawns.First(l => l.ItemId == ItemIds.Sniper).Pos;
			Assert.True(nav.FindPath(new Vec3(0, 0, 0), rail, path, 60000));
			Assert.True(MathF.Abs(path[^1].Y - 8f) < 0.6f);
		}

		// ------------------------------------------------------------------ règles d'arène sur le serveur

		private static Rig Arena(int humans = 1) => new Rig(o =>
		{
			o.Mode = GameMode.Deathmatch; o.Match.TeamSize = 1; o.MinPlayers = humans; o.LobbyCountdown = 0.3f; o.FillBots = false; o.EndLinger = 100f;
			o.Match.MaxPlayers = 10;
		}, Load());

		private static Rig.Bot[] Start(Rig rig, int n)
		{
			var list = new Rig.Bot[n];
			for (int i = 0; i < n; i++) list[i] = rig.Connect("J" + i);
			foreach (var b in list) Assert.True(rig.WaitPlaying(b));
			Assert.True(rig.WaitUntil(() => rig.Server.Phase == MatchPhase.Playing, 6), "la partie doit démarrer");
			rig.Run(0.5);
			return list;
		}

		private static T OnServer<T>(Rig rig, Func<T> f) { T r = default; rig.OnServer(() => r = f()); return r; }

		[Fact]
		public void Deathmatch_Starts_With_A_Red_Dot_P2020_And_An_Empty_Epic_Shield_And_Nothing_Can_Be_Dropped()
		{
			using var rig = Arena();
			var a = Start(rig, 1)[0];
			var inv = OnServer(rig, () => { var p = rig.Server.GetPlayer(a.C.PlayerId); return (w0: p.Inventory.Weapons[0]?.WeaponId ?? 0, optic: p.Inventory.Weapons[0]?.OpticId ?? 0, w1: p.Inventory.Weapons[1]?.WeaponId ?? 0, tier: p.Inventory.ShieldTier, shield: p.Shield, grenades: p.Inventory.Items.Count(i => ItemDefs.Get(i.Id).Kind == ItemKind.Grenade)); });
			Assert.Equal(ItemIds.P2020, inv.w0);
			Assert.Equal(ItemIds.OpticRedDot, inv.optic);
			Assert.Equal(0, inv.w1);
			Assert.Equal((int)ItemTier.Epic, inv.tier);
			Assert.Equal(0f, inv.shield);
			Assert.Equal(0, inv.grenades);
			Assert.True(OnServer(rig, () => rig.Server.ArenaSpotCount) >= 30, "les emplacements d'arène sont posés");
			a.C.SendCommand(CommandType.DropWeapon, 0);
			rig.Run(0.4);
			Assert.Equal(ItemIds.P2020, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Inventory.Weapons[0]?.WeaponId ?? 0));
		}

		[Fact]
		public void An_Arena_Weapon_Has_Its_Optic_Replaces_The_Weapon_In_Hand_And_Returns_When_Replaced_Or_When_The_Holder_Dies()
		{
			using var rig = Arena(2);
			var both = Start(rig, 2);
			var a = both[0]; var b = both[1];
			Assert.True(OnServer(rig, () => rig.Server.ArenaGrab(a.C.PlayerId, ItemIds.Ak)));
			var w = OnServer(rig, () => { var inv = rig.Server.GetPlayer(a.C.PlayerId).Inventory; return (inv.Weapons[0]?.WeaponId ?? 0, inv.Weapons[1]?.WeaponId ?? 0, inv.Weapons[1]?.OpticId ?? 0, inv.Weapons[1]?.Tier ?? 0, inv.ActiveSlot, inv.GetAmmo(2)); });
			Assert.Equal(ItemIds.P2020, w.Item1);
			Assert.Equal(ItemIds.Ak, w.Item2);
			Assert.Equal(ArenaRules.OpticFor(ItemIds.Ak), w.Item3);
			Assert.Equal((byte)ItemTier.Epic, w.Item4);
			Assert.Equal(1, w.ActiveSlot);
			Assert.True(w.Item6 >= WeaponDefs.Get(ItemIds.Ak).MagazineSize, "des munitions viennent avec l'arme");
			int akSpots = OnServer(rig, () => rig.Server.ArenaSpots().Count(s => s.Item == ItemIds.Ak && !s.Present));
			Assert.Equal(1, akSpots);

			// les deux emplacements sont pleins : le fusil à pompe remplace l'AK en main, qui disparaît et revient à sa place
			Assert.True(OnServer(rig, () => rig.Server.ArenaGrab(a.C.PlayerId, ItemIds.Sawnoff)));
			var after = OnServer(rig, () => { var inv = rig.Server.GetPlayer(a.C.PlayerId).Inventory; return (inv.Weapons[0]?.WeaponId ?? 0, inv.Weapons[1]?.WeaponId ?? 0); });
			Assert.Equal((ItemIds.P2020, ItemIds.Sawnoff), after);
			Assert.True(rig.WaitUntil(() => OnServer(rig, () => rig.Server.ArenaSpots().All(s => s.Item != ItemIds.Ak || s.Present)), ArenaRules.WeaponRespawn + 2), "l'AK remplacé revient à sa place");
			Assert.Equal(0, OnServer(rig, () => rig.Server.LootCount - rig.Server.ArenaSpots().Count(s => s.Present)));    // rien n'est tombé au sol

			// le porteur du fusil à pompe meurt : l'arme revient, et il ne lâche rien
			rig.OnServer(() => rig.Server.AdminKill(b.C.PlayerId, a.C.PlayerId));
			Assert.True(rig.WaitUntil(() => OnServer(rig, () => rig.Server.ArenaSpots().Where(s => s.Item == ItemIds.Sawnoff).All(s => s.Present)), ArenaRules.WeaponRespawn + 2), "l'arme revient quand son porteur meurt");
			Assert.Equal(0, OnServer(rig, () => rig.Server.LootCount - rig.Server.ArenaSpots().Count(s => s.Present)));
		}

		[Fact]
		public void Bots_Play_The_Arena_They_Take_Weapons_Climb_The_Levels_And_Score()
		{
			using var rig = new Rig(o =>
			{
				o.Mode = GameMode.Deathmatch; o.Match.TeamSize = 1; o.MinPlayers = 1; o.LobbyCountdown = 0.3f; o.FillBots = true; o.BotFillTarget = 8; o.EndLinger = 100f;
				o.Match.MaxPlayers = 10;
			}, Load());
			Start(rig, 1);
			bool armed = false; float highest = 0f; long kills = 0;
			for (int i = 0; i < 40 && !(armed && highest > 3.5f && kills > 0); i++)
			{
				rig.Run(1.0);
				rig.OnServer(() =>
				{
					for (int id = 0; id < 64; id++)
					{
						var p = rig.Server.GetPlayer(id);
						if (p == null || !p.IsBot || !p.Alive) continue;
						foreach (var w in p.Inventory.Weapons) if (w != null && w.WeaponId != ItemIds.P2020) armed = true;
						if (p.State.OnGround) highest = MathF.Max(highest, p.State.Pos.Y);
					}
					kills = rig.Server.TotalKills;
				});
			}
			Assert.True(armed, "un bot doit avoir pris une arme de l'arène");
			Assert.True(highest > 3.5f, $"les bots doivent monter dans les étages (plus haut : {highest} m)");
			Assert.True(kills > 0, "les bots doivent se battre");
		}

		[Fact]
		public void Walking_Over_A_Weapon_Takes_It_But_Not_One_Already_Held()
		{
			using var rig = Arena();
			var a = Start(rig, 1)[0];
			var smg = OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Item == ItemIds.Smg45));
			rig.Teleport(a, smg.Pos);
			rig.Run(0.3);
			Assert.Equal(ItemIds.Smg45, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Inventory.Weapons[1]?.WeaponId ?? 0));
			Assert.False(OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Pos.Equals(smg.Pos)).Present));
			// la seconde SMG : on l'a déjà, elle reste au sol
			var other = OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Item == ItemIds.Smg45 && s.Present));
			rig.Teleport(a, other.Pos);
			rig.Run(0.3);
			Assert.True(OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Pos.Equals(other.Pos)).Present), "une arme déjà en main n'est pas reprise");
		}

		[Fact]
		public void Health_Regenerates_Five_Seconds_After_The_Last_Wound_But_Not_The_Shield_And_A_Kill_Gives_50()
		{
			using var rig = Arena(2);
			var both = Start(rig, 2);
			var a = both[0]; var b = both[1];
			rig.OnServer(() => { var p = rig.Server.GetPlayer(a.C.PlayerId); p.Health = 40f; p.Shield = 10f; p.LastHurtTick = rig.Server.Tick; });
			rig.Run(4.0);
			Assert.Equal(40f, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Health));        // moins de 5 s : rien
			rig.Run(2.6);
			var st = OnServer(rig, () => { var p = rig.Server.GetPlayer(a.C.PlayerId); return (p.Health, p.Shield); });
			Assert.InRange(st.Health, 41f, 47f);                                                          // ~1 PV toutes les 0,3 s
			Assert.Equal(10f, st.Shield);                                                                 // le bouclier ne remonte pas

			rig.OnServer(() => { var p = rig.Server.GetPlayer(a.C.PlayerId); p.Health = 30f; p.LastHurtTick = rig.Server.Tick; });
			rig.OnServer(() => rig.Server.AdminKill(a.C.PlayerId, b.C.PlayerId));
			Assert.Equal(80f, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Health));
		}

		[Fact]
		public void The_P2020_Is_A_Light_Fast_Pistol()
		{
			var p = WeaponDefs.Get(ItemIds.P2020);
			var c19 = WeaponDefs.Get(ItemIds.C19);
			Assert.Equal(18f, p.Damage);
			Assert.True(p.RoundsPerMinute > c19.RoundsPerMinute && !p.Automatic);
			Assert.Equal(1, p.AmmoType);
			Assert.Equal("p2020", ItemDefs.Get(ItemIds.P2020).Name);
		}

		[Fact]
		public void Heals_And_Shields_Are_Taken_By_Walking_On_Them_Act_At_Once_And_Come_Back_Later()
		{
			using var rig = Arena();
			var a = Start(rig, 1)[0];
			var heal = OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Item == ItemIds.HealSmall));
			var shield = OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Item == ItemIds.ShieldSmall));

			// en pleine santé, le soin reste au sol
			rig.Teleport(a, heal.Pos);
			rig.Run(0.3);
			Assert.True(OnServer(rig, () => rig.Server.ArenaSpots().First(s => s.Pos.Equals(heal.Pos)).Present), "un soin inutile n'est pas pris");

			rig.OnServer(() => rig.Server.GetPlayer(a.C.PlayerId).Health = 50f);
			rig.Teleport(a, heal.Pos);
			rig.Run(0.3);
			var st = OnServer(rig, () => (rig.Server.GetPlayer(a.C.PlayerId).Health, rig.Server.ArenaSpots().First(s => s.Pos.Equals(heal.Pos))));
			Assert.Equal(75f, st.Health);
			Assert.False(st.Item2.Present);
			Assert.InRange(st.Item2.ReturnIn, ArenaRules.HealRespawn - 1.5f, ArenaRules.HealRespawn);
			Assert.Equal(0, OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Inventory.CountOf(ItemIds.HealSmall)));    // rien dans le sac : l'effet est immédiat

			rig.Teleport(a, shield.Pos);
			rig.Run(0.3);
			Assert.Equal(GameBalance.ConsumableShield[ItemIds.ShieldSmall - ItemIds.HealSmall], OnServer(rig, () => rig.Server.GetPlayer(a.C.PlayerId).Shield));
		}
	}
}
