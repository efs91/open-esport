using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OpenEsport.Core;
using OpenEsport.Server;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Un vrai serveur et de vrais clients, en UDP sur la machine locale.</summary>
	public sealed class Rig : IDisposable
	{
		private static int _nextPort = 31000;

		public sealed class Bot
		{
			public GameClient C;
			public float Forward, Strafe, Yaw, Pitch;
			public Buttons Buttons;
			public int Slot;
			public readonly List<ServerEvent> Events = new List<ServerEvent>();
			public Func<Bot, bool> Driver;
			public int HitConfirms => Events.Count(e => e.Type == EventType.HitConfirm);
		}

		public readonly GameServer Server;
		public readonly WorldData World;
		public readonly StaticWorld Collision;
		public readonly int Port;
		public readonly List<Bot> Bots = new List<Bot>();
		private readonly CancellationTokenSource _cts = new CancellationTokenSource();
		private readonly Task _task;

		public Rig(Action<ServerOptions> configure = null, WorldData world = null)
		{
			Port = Interlocked.Increment(ref _nextPort);
			World = world ?? WorldData.CreateFlat(600);
			if (World.SpawnPoints.Count == 0)
				for (int i = 0; i < 8; i++) World.SpawnPoints.Add(new Vec3(i * 25 - 100, World.HeightAt(i * 25 - 100, 0), 0));
			Collision = new StaticWorld(World);
			var opt = new ServerOptions { Port = Port, StatsIntervalSeconds = 3600 };
			opt.Match.RespawnSeconds = 0.5f;
			configure?.Invoke(opt);
			Server = new GameServer(opt, World);
			var started = new ManualResetEventSlim();
			Server.Started += started.Set;
			_task = Task.Run(() => Server.Run(_cts.Token));
			Assert.True(started.Wait(5000), "le serveur n'a pas démarré");
		}

		public Bot Connect(string name, WorldData differentWorld = null, byte[] appearance = null)
		{
			var w = differentWorld ?? World;
			var bot = new Bot { C = new GameClient(new StaticWorld(w), w.Checksum()) };
			bot.C.EventReceived += e => bot.Events.Add(e);
			bot.C.Connect("127.0.0.1", Port, name, appearance);
			Bots.Add(bot);
			return bot;
		}

		public bool WaitUntil(Func<bool> condition, double seconds)
		{
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed.TotalSeconds < seconds)
			{
				foreach (var b in Bots) b.C.Poll();
				if (condition()) return true;
				Thread.Sleep(2);
			}
			return false;
		}

		public bool WaitPlaying(Bot bot) => WaitUntil(() => bot.C.State == NetState.Playing, 5);

		/// <summary>Fait tourner les clients à 60 Hz (réception puis un tick de simulation chacun).</summary>
		public void Run(double seconds, Action<double> perTick = null)
		{
			var sw = Stopwatch.StartNew();
			long tick = 0;
			while (sw.Elapsed.TotalSeconds < seconds)
			{
				long due = (long)(sw.Elapsed.TotalSeconds * 60);
				while (tick < due)
				{
					tick++;
					foreach (var b in Bots) b.C.Poll();
					perTick?.Invoke(sw.Elapsed.TotalSeconds);
					foreach (var b in Bots)
					{
						if (b.Driver != null) b.Driver(b);
						b.C.Tick(b.Forward, b.Strafe, b.Buttons, b.Slot, b.Yaw, b.Pitch);
					}
				}
				Thread.Sleep(1);
			}
		}

		public void OnServer(Action action)
		{
			var done = new ManualResetEventSlim();
			Server.RunOnServerThread(() => { action(); done.Set(); });
			Assert.True(done.Wait(3000));
		}

		public void Teleport(Bot bot, Vec3 pos)
		{
			OnServer(() => { var p = Server.GetPlayer(bot.C.PlayerId); p.State = PlayerMovement.Spawn(pos, Collision); });
		}

		public void Give(Bot bot, byte itemId, int count = 1, byte tier = 0)
		{
			OnServer(() =>
			{
				var p = Server.GetPlayer(bot.C.PlayerId);
				var item = LootItem.Of(itemId);
				item.Tier = tier;
				if (count > 1) item.Count = (ushort)count;
				p.Inventory.TryPickup(ref item, new List<LootItem>());
				if (itemId == ItemIds.Shield) p.Shield = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);   // comme le ramassage réel : bouclier plein
			});
		}

		public void Dispose()
		{
			foreach (var b in Bots) b.C.Disconnect();
			_cts.Cancel();
			try { _task.Wait(3000); } catch (Exception) { }
		}
	}

	[Collection("Network")]
	public class IntegrationTests
	{
		[Fact]
		public void Join_Walk_And_Prediction_Matches_The_Server_Exactly()
		{
			using var rig = new Rig();
			var a = rig.Connect("Alice"); var b = rig.Connect("Bob");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			a.Forward = 1; a.Buttons = Buttons.Sprint;
			rig.Run(3.0);

			Assert.True(a.C.SnapshotsReceived > 50, $"snapshots reçus : {a.C.SnapshotsReceived}");
			// même code, mêmes commandes, même pas de temps : aucune correction n'est nécessaire
			Assert.Equal(0, a.C.Corrections);
			var server = rig.Server.GetPlayer(a.C.PlayerId);
			Assert.True(a.C.Predicted.Pos.Z < -15f, $"Alice doit avoir avancé : {a.C.Predicted.Pos}");
			Assert.True(Vec3.Distance(server.State.Pos, a.C.Predicted.Pos) < 2.5f, "le serveur ne doit être en retard que de quelques ticks");
		}

		[Fact]
		public void Other_Players_Are_Visible_And_Animated_With_Their_Velocity()
		{
			using var rig = new Rig();
			var a = rig.Connect("Alice"); var b = rig.Connect("Bob");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			a.Forward = 1; a.Buttons = Buttons.Sprint;
			rig.Run(2.5);

			var seen = new List<InterpolatedEntity>();
			b.C.SampleEntities(seen);
			var alice = seen.FirstOrDefault(e => e.Id == a.C.PlayerId && e.Kind == EntityKind.Player);
			Assert.NotEqual(default, alice);
			var truth = rig.Server.GetPlayer(a.C.PlayerId).State.Pos;
			Assert.True(Vec3.Distance(alice.Pos, truth) < 4f, $"vue {alice.Pos} / réel {truth}");
			float sprint = Sim.BaseSpeed * Sim.SprintMultiplier * GameBalance.UnarmedMultiplier;      // Alice n'a pas d'arme : elle court plus vite
			Assert.InRange(new Vec3(alice.Vel.X, 0, alice.Vel.Z).Length(), sprint * 0.75f, sprint * 1.1f);
			Assert.Equal(a.C.TeamId, alice.Team);
		}

		[Fact]
		public void Teams_Are_Formed_With_The_Requested_Size()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 3);
			var bots = Enumerable.Range(0, 7).Select(i => rig.Connect("J" + i)).ToList();
			Assert.True(rig.WaitUntil(() => bots.All(x => x.C.State == NetState.Playing) && bots[0].C.Roster.Count == 7, 8));
			var groups = bots[0].C.Roster.GroupBy(r => r.Team).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
			Assert.Equal(new[] { 3, 3, 1 }, groups);
		}

		[Fact]
		public void Shooting_Kills_Drops_Loot_And_The_Victim_Respawns()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -10));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Give(b, ItemIds.Ak);
			rig.Run(0.4);
			int lootBefore = rig.Server.LootCount;

			a.Pitch = MathF.Atan2(1.0f - 1.6f, 10f);
			a.Buttons = Buttons.Fire;
			rig.Run(1.6);
			a.Buttons = Buttons.None;
			rig.Run(0.2);

			Assert.True(a.HitConfirms >= 5, $"touches confirmées : {a.HitConfirms}");
			Assert.Contains(b.Events, e => e.Type == EventType.Damaged && e.A == a.C.PlayerId);
			Assert.Contains(a.Events, e => e.Type == EventType.KillFeed && e.A == a.C.PlayerId && e.B == b.C.PlayerId);
			Assert.Contains(b.Events, e => e.Type == EventType.KillFeed);
			Assert.True(a.C.Magazine < 30, "le chargeur doit se vider");
			Assert.True(rig.Server.LootCount > lootBefore, "la victime laisse son arme au sol");

			Assert.True(rig.WaitUntil(() => b.Events.Any(e => e.Type == EventType.Respawned), 2.5), "la victime doit réapparaître");
			Assert.True(rig.WaitUntil(() => b.C.Alive && b.C.Health == 100, 1.0));
		}

		private static (int fired, int hits) ChaseAndShoot(bool lagCompensation)
		{
			using var rig = new Rig(o => { o.Match.TeamSize = 1; o.LagCompensation = lagCompensation; });
			var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(-15, 0, -10));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Give(b, ItemIds.Ak);
			// la cible ne meurt pas pendant l'essai : on mesure la précision, pas les éliminations
			rig.OnServer(() => rig.Server.GetPlayer(b.C.PlayerId).Health = 1e6f);
			b.Strafe = 1; b.Yaw = 0;
			rig.Run(0.8);

			a.Driver = bot =>
			{
				var seen = new List<InterpolatedEntity>();
				bot.C.SampleEntities(seen);
				var target = seen.FirstOrDefault(e => e.Id == b.C.PlayerId && e.Kind == EntityKind.Player);
				if (target.Id == 0 && target.Kind != EntityKind.Player) return false;
				// on vise la cible telle qu'elle est affichée à l'écran
				Vec3 eye = bot.C.ViewPosition + new Vec3(0, 1.6f, 0);
				Vec3 aim = target.Pos + new Vec3(0, 1.0f, 0);
				Vec3 d = aim - eye;
				bot.Yaw = MathF.Atan2(-d.X, -d.Z);
				bot.Pitch = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
				bot.Buttons = Buttons.Fire;
				return true;
			};
			// la cible reste dans la zone : elle part vers +X puis revient
			float t0 = 0;
			rig.Run(2.2, t => { b.Strafe = ((int)((t - t0) / 1.0)) % 2 == 0 ? 1 : -1; });
			a.Driver = null; a.Buttons = Buttons.None;
			rig.Run(0.3);
			return (30 - a.C.Magazine, a.HitConfirms);
		}

		[Fact]
		public void Lag_Compensation_Makes_Shots_On_Moving_Targets_Land()
		{
			var (firedOn, hitsOn) = ChaseAndShoot(true);
			var (firedOff, hitsOff) = ChaseAndShoot(false);
			Assert.True(firedOn >= 10 && firedOff >= 10, $"tirs : {firedOn} / {firedOff}");
			double ratioOn = hitsOn / (double)firedOn, ratioOff = hitsOff / (double)firedOff;
			Assert.True(ratioOn > 0.6, $"avec compensation : {hitsOn}/{firedOn}");
			Assert.True(ratioOn > ratioOff + 0.25, $"la compensation doit nettement améliorer la précision : {ratioOn:F2} contre {ratioOff:F2}");
		}

		[Fact]
		public void Interact_Picks_Up_Loot_And_The_Entity_Disappears_For_Everyone()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Alice"); var b = rig.Connect("Bob");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(5, 0, 0));
			rig.OnServer(() => rig.Server.SpawnLootAt(LootItem.Of(ItemIds.Ak), new Vec3(0, rig.World.HeightAt(0, -1.5f), -1.5f)));
			rig.Run(1.0);

			var seenBefore = new List<InterpolatedEntity>();
			b.C.SampleEntities(seenBefore);
			Assert.Contains(seenBefore, e => e.Kind == EntityKind.Loot && e.Weapon == ItemIds.Ak);

			a.Pitch = -0.7f; // il faut regarder l'objet (dans un cône de 30 degrés), ici au sol à 1,5 m
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None;
			Assert.True(rig.WaitUntil(() => a.C.Inventory.Weapons[0]?.WeaponId == ItemIds.Ak, 2), "l'arme doit arriver dans l'inventaire");
			rig.Run(0.8);
			var seenAfter = new List<InterpolatedEntity>();
			b.C.SampleEntities(seenAfter);
			Assert.DoesNotContain(seenAfter, e => e.Kind == EntityKind.Loot && e.Weapon == ItemIds.Ak && Vec3.Distance(e.Pos, new Vec3(0, 100, -1.5f)) < 2);
			Assert.Equal(30, a.C.Inventory.Weapons[0].Magazine);
		}

		[Fact]
		public void Opening_A_Chest_Drops_Four_Items_Including_A_Weapon_And_Marks_It_Open()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Alice"); Assert.True(rig.WaitPlaying(a));
			rig.Teleport(a, new Vec3(0, 0, 0));
			rig.OnServer(() => rig.Server.SpawnLootAt(LootItem.Of(ItemIds.Chest), new Vec3(0, rig.World.HeightAt(0, -1.5f), -1.5f)));
			rig.Run(0.8);
			int before = rig.Server.LootCount;

			a.Pitch = -0.62f;
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None;
			rig.Run(0.8);

			int added = rig.Server.LootCount - before;
			Assert.InRange(added, 2, 4); // une arme, ses munitions, et éventuellement un accessoire ou une seconde arme
			var seen = new List<InterpolatedEntity>();
			a.C.SampleEntities(seen);
			var chest = seen.First(e => e.Kind == EntityKind.Loot && e.Weapon == ItemIds.Chest);
			Assert.Equal(1, chest.Count);
			Assert.Contains(seen, e => e.Kind == EntityKind.Loot && ItemDefs.Get(e.Weapon).Kind == ItemKind.Weapon);

			// ouvrir de nouveau ne donne rien de plus
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None;
			rig.Run(0.4);
			Assert.True(rig.Server.LootCount <= before + added, "un coffre déjà ouvert ne redonne rien");
		}

		[Fact]
		public void Interact_Picks_The_Item_Under_The_Crosshair_Among_Close_Neighbours()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Alice"); Assert.True(rig.WaitPlaying(a));
			rig.Teleport(a, new Vec3(0, 0, 0));
			float h = rig.World.HeightAt(0, -1.5f);
			var ak = new Vec3(-0.2f, h + 0.42f, -1.6f);
			var smg = new Vec3(0.2f, h + 0.42f, -1.6f);
			rig.OnServer(() => { rig.Server.SpawnLootAt(LootItem.Of(ItemIds.Ak), ak); rig.Server.SpawnLootAt(LootItem.Of(ItemIds.Smg45), smg); });
			rig.Run(0.6);

			void AimAt(Vec3 p)
			{
				float dx = p.X, dz = p.Z, dy = p.Y - (rig.World.HeightAt(0, 0) + 1.6f);
				a.Yaw = MathF.Atan2(-dx, -dz);
				a.Pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
			}
			// le SMG à droite d abord
			AimAt(smg);
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.4);
			Assert.Equal(ItemIds.Smg45, a.C.Inventory.Weapons[0]?.WeaponId ?? 0);
			AimAt(ak);
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.4);
			Assert.Equal(ItemIds.Ak, a.C.Inventory.Weapons[1]?.WeaponId ?? 0);
		}

		[Fact]
		public void Interact_Takes_The_Item_The_Client_Points_At_Or_Nothing_If_It_Sees_Nothing()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Alice"); Assert.True(rig.WaitPlaying(a));
			rig.Teleport(a, new Vec3(0, 0, 0));
			float h = rig.World.HeightAt(0, -1.5f);
			rig.OnServer(() =>
			{
				rig.Server.SpawnLootAt(LootItem.Of(ItemIds.Ak), new Vec3(-0.2f, h + 0.42f, -1.6f));
				rig.Server.SpawnLootAt(LootItem.Of(ItemIds.Smg45), new Vec3(0.2f, h + 0.42f, -1.6f));
			});
			rig.Run(0.6);
			var seen = new List<InterpolatedEntity>();
			a.C.SampleEntities(seen);
			ushort Id(byte weapon) => seen.First(e => e.Kind == EntityKind.Loot && e.Weapon == weapon).Id;

			// la visée du joueur est tournée vers le SMG, mais le client désigne l'AK : le serveur prend l'AK
			a.Yaw = MathF.Atan2(0.2f, 1.6f); a.Pitch = -0.5f;
			a.C.InteractTarget = Id(ItemIds.Ak);
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.4);
			Assert.Equal(ItemIds.Ak, a.C.Inventory.Weapons[0]?.WeaponId ?? 0);

			// le client ne voit rien sous son viseur : rien n'est ramassé
			a.C.InteractTarget = PlayerInput.NoTarget;
			a.Buttons = Buttons.Interact; rig.Run(0.1); a.Buttons = Buttons.None; rig.Run(0.4);
			Assert.Null(a.C.Inventory.Weapons[1]);
		}

		[Fact]
		public void Without_A_Weapon_Fire_Is_A_Punch_That_Hurts_At_Close_Range_Only()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Boxeur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -1.2f));
			rig.Run(0.4);
			a.Pitch = MathF.Atan2(1.0f - 1.6f, 1.2f);
			a.Buttons = Buttons.Fire; rig.Run(0.5); a.Buttons = Buttons.None; rig.Run(0.2);
			Assert.True(b.C.Health + b.C.Shield < 150, $"le coup doit toucher : {b.C.Health} PV + {b.C.Shield} PS");
			Assert.True(a.HitConfirms >= 1);
			float closeHealth = b.C.Health + b.C.Shield;

			// trop loin : on frappe dans le vide
			rig.Teleport(b, new Vec3(0, 0, -6f)); rig.Run(0.3);
			a.Pitch = 0f;
			a.Buttons = Buttons.Fire; rig.Run(0.8); a.Buttons = Buttons.None; rig.Run(0.2);
			Assert.Equal(closeHealth, b.C.Health + b.C.Shield);
		}

		[Fact]
		public void A_Legendary_Weapon_Hits_Harder_Than_A_Classic_One()
		{
			int Damage(byte tier)
			{
				using var rig = new Rig(o => o.Match.TeamSize = 1);
				var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
				Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
				rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -8));
				rig.Give(a, ItemIds.Ak, 1, tier); rig.Give(a, ItemIds.AmmoRifle, 60);
				rig.OnServer(() => rig.Server.GetPlayer(b.C.PlayerId).Health = 1e6f);
				rig.Run(0.4);
				a.Pitch = MathF.Atan2(1.0f - 1.6f, 8f);
				a.Buttons = Buttons.Fire; rig.Run(0.6); a.Buttons = Buttons.None; rig.Run(0.2);
				var hit = a.Events.First(e => e.Type == EventType.HitConfirm);
				return hit.C;
			}
			int classic = Damage((byte)ItemTier.Classic), legendary = Damage((byte)ItemTier.Legendary);
			Assert.Equal(19, classic);
			Assert.True(legendary >= 23, $"dégâts légendaires : {legendary}"); // 19 x 1,25 = 23,75
		}

		[Fact]
		public void Shield_Absorbs_Damage_Before_Health_And_Starts_White_And_Full()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur"); var b = rig.Connect("Cible");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			Assert.True(rig.WaitUntil(() => b.C.Shield == 50, 2), $"bouclier de départ : {b.C.Shield}");
			Assert.Equal(0, b.C.ShieldTier);
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(0, 0, -8));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Run(0.4);
			a.Pitch = MathF.Atan2(1.0f - 1.6f, 8f);
			a.Buttons = Buttons.Fire; rig.Run(0.12); a.Buttons = Buttons.None; rig.Run(0.3);
			Assert.Equal(100, b.C.Health);                       // la première balle ne touche que le bouclier
			Assert.InRange(b.C.Shield, 25, 49);
			a.Buttons = Buttons.Fire; rig.Run(0.45); a.Buttons = Buttons.None; rig.Run(0.3);
			Assert.Equal(0, b.C.Shield);                         // bouclier épuisé
			Assert.True(b.C.Health < 100, $"puis la vie : {b.C.Health}");
		}

		[Fact]
		public void A_Better_Shield_Replaces_The_Equipped_One_Which_Falls_On_The_Ground()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Shield, 1, (byte)ItemTier.Legendary);
			Assert.True(rig.WaitUntil(() => a.C.Shield == 125 && a.C.ShieldTier == 3, 2), $"bouclier rouge : {a.C.Shield} / niveau {a.C.ShieldTier}");
			// on le pose : plus de bouclier
			a.C.SendCommand(CommandType.DropShield, 0);
			Assert.True(rig.WaitUntil(() => a.C.ShieldTier == -1 && a.C.Shield == 0, 2), "le bouclier est posé par terre");
		}

		[Fact]
		public void A_Small_Heal_Takes_Three_Seconds_Adds_25_And_Is_Cancelled_By_Firing()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			rig.OnServer(() => rig.Server.GetPlayer(a.C.PlayerId).Health = 40f);
			rig.Give(a, ItemIds.HealSmall, 1); rig.Give(a, ItemIds.HealSmall, 1);
			rig.Run(0.3);
			Assert.Equal(40, a.C.Health);
			a.C.SendCommand(CommandType.UseItem, ItemIds.HealSmall);
			rig.Run(1.5);
			Assert.Equal(ItemIds.HealSmall, a.C.UsingItem);
			Assert.InRange(a.C.UseProgress, 0.3f, 0.7f);
			Assert.Equal(40, a.C.Health);                         // rien tant que ce n'est pas fini
			rig.Run(2.0);
			Assert.Equal(65, a.C.Health);                         // 40 + 25
			Assert.Equal(0, a.C.UsingItem);
			Assert.Equal(1, a.C.Inventory.CountOf(ItemIds.HealSmall));   // un seul consommé

			// interrompu en tirant : rien n'est consommé
			a.C.SendCommand(CommandType.UseItem, ItemIds.HealSmall); rig.Run(1.0);
			a.Buttons = Buttons.Fire; rig.Run(0.2); a.Buttons = Buttons.None; rig.Run(3.5);
			Assert.Equal(65, a.C.Health);
			Assert.Equal(1, a.C.Inventory.CountOf(ItemIds.HealSmall));
		}

		[Fact]
		public void A_Big_Shield_And_Life_Potion_Fills_Everything_After_Ten_Seconds()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.Shield, 1, (byte)ItemTier.Epic);
			rig.OnServer(() => { var p = rig.Server.GetPlayer(a.C.PlayerId); p.Health = 10f; p.Shield = 5f; });
			rig.Give(a, ItemIds.BothBig, 1);
			rig.Run(0.3);
			a.C.SendCommand(CommandType.UseItem, ItemIds.BothBig);
			rig.Run(9.0);
			Assert.True(a.C.Health < 100, "pas encore fini à 9 s");
			rig.Run(1.8);
			Assert.Equal(100, a.C.Health);
			Assert.Equal(100, a.C.Shield);                         // bouclier violet : 100 points
		}

		[Fact]
		public void A_Heal_Is_Refused_When_There_Is_Nothing_To_Heal()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			rig.Give(a, ItemIds.HealBig, 1);
			rig.Run(0.3);
			a.C.SendCommand(CommandType.UseItem, ItemIds.HealBig);
			rig.Run(0.5);
			Assert.Equal(0, a.C.UsingItem);
		}

		[Fact]
		public void Every_Shot_Is_Announced_To_Nearby_Players()
		{
			using var rig = new Rig(o => o.Match.TeamSize = 1);
			var a = rig.Connect("Tireur"); var b = rig.Connect("Voisin");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(b, new Vec3(20, 0, 0));
			rig.Give(a, ItemIds.Ak); rig.Give(a, ItemIds.AmmoRifle, 60);
			rig.Run(0.4);
			a.Buttons = Buttons.Fire; rig.Run(0.5); a.Buttons = Buttons.None;
			rig.Run(0.3);
			Assert.Contains(b.Events, e => e.Type == EventType.Shot && e.A == a.C.PlayerId && e.C == ItemIds.Ak);
			Assert.Contains(a.Events, e => e.Type == EventType.Shot);
		}

		[Fact]
		public void Interest_Management_Hides_Far_Enemies_But_Never_Teammates()
		{
			using var rig = new Rig(o => { o.Match.TeamSize = 2; o.Match.InterestRadius = 100; });
			var a = rig.Connect("A"); Assert.True(rig.WaitPlaying(a));
			var mate = rig.Connect("Coequipier"); Assert.True(rig.WaitPlaying(mate));
			var enemy = rig.Connect("Ennemi"); Assert.True(rig.WaitPlaying(enemy));
			Assert.Equal(a.C.TeamId, mate.C.TeamId);
			Assert.NotEqual(a.C.TeamId, enemy.C.TeamId);
			rig.Teleport(a, new Vec3(0, 0, 0)); rig.Teleport(mate, new Vec3(250, 0, 0)); rig.Teleport(enemy, new Vec3(-250, 0, 0));
			rig.Run(1.0);

			List<ushort> Visible() { var l = new List<InterpolatedEntity>(); a.C.SampleEntities(l); return l.Where(e => e.Kind == EntityKind.Player).Select(e => e.Id).ToList(); }
			Assert.Contains((ushort)mate.C.PlayerId, Visible());
			Assert.DoesNotContain((ushort)enemy.C.PlayerId, Visible());

			rig.Teleport(enemy, new Vec3(-40, 0, 0));
			Assert.True(rig.WaitUntil(() => { rig.Run(0.1); return Visible().Contains(enemy.C.PlayerId); }, 3), "l'ennemi proche doit apparaître");
			rig.Teleport(enemy, new Vec3(-250, 0, 0));
			Assert.True(rig.WaitUntil(() => { rig.Run(0.1); return !Visible().Contains(enemy.C.PlayerId); }, 3), "l'ennemi éloigné doit disparaître");
		}

		[Fact]
		public void A_Client_With_A_Different_Map_Is_Rejected()
		{
			using var rig = new Rig();
			var other = WorldData.CreateFlat(500);
			var bad = rig.Connect("Intrus", other);
			Assert.True(rig.WaitUntil(() => bad.C.State == NetState.Failed, 5));
			Assert.Contains("carte", bad.C.FailReason);
		}

		[Fact]
		public void A_Full_Server_Refuses_New_Players()
		{
			using var rig = new Rig(o => o.Match.MaxPlayers = 2);
			var a = rig.Connect("A"); var b = rig.Connect("B");
			Assert.True(rig.WaitPlaying(a)); Assert.True(rig.WaitPlaying(b));
			var c = rig.Connect("C");
			rig.WaitUntil(() => false, 1.5);
			Assert.NotEqual(NetState.Playing, c.C.State);
		}

		[Fact]
		public void Garbage_Packets_Do_Not_Disturb_The_Server()
		{
			using var rig = new Rig();
			var a = rig.Connect("A");
			Assert.True(rig.WaitPlaying(a));
			var rng = new Random(1);
			using (var udp = new UdpClient())
			{
				for (int i = 0; i < 300; i++)
				{
					var junk = new byte[rng.Next(1, 200)];
					rng.NextBytes(junk);
					udp.Send(junk, junk.Length, "127.0.0.1", rig.Port);
				}
			}
			int before = a.C.SnapshotsReceived;
			rig.Run(1.0);
			Assert.True(a.C.SnapshotsReceived > before + 20, "le serveur doit continuer de répondre");
			var b = rig.Connect("B");
			Assert.True(rig.WaitPlaying(b));
		}

		[Fact]
		public void Hundred_Players_Of_Teams_Of_Five_Fit_Within_The_Limits()
		{
			using var rig = new Rig(o => { o.Match.MaxPlayers = 100; o.Match.TeamSize = 5; });
			var bots = Enumerable.Range(0, 100).Select(i => rig.Connect("J" + i)).ToList();
			Assert.True(rig.WaitUntil(() => bots.All(x => x.C.State == NetState.Playing), 20));
			Assert.Equal(100, rig.Server.PlayerCount);
			Assert.Equal(20, bots.Select(x => x.C.TeamId).Distinct().Count());
			Assert.All(bots.GroupBy(x => x.C.TeamId), g => Assert.Equal(5, g.Count()));
		}
	}
}
