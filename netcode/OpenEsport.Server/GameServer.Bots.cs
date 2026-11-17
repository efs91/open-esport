using System;
using System.Collections.Generic;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Bots côté serveur : ils complètent les parties non pleines. Ils n'ont pas de connexion réseau, mais passent par la même
	/// simulation que les humains (mêmes déplacements, mêmes tirs, même butin) : ils produisent des commandes comme le ferait un joueur.
	/// Quand un humain arrive dans une partie pleine, un bot lui cède sa place (de préférence dans une équipe qui compte déjà des humains).
	/// </summary>
	public sealed partial class GameServer
	{
		private static readonly string[] BotNames =
		{
			"Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliet", "Kilo", "Lima", "Mike", "November",
			"Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "Xray", "Yankee", "Zulu"
		};

		private sealed class BotBrain
		{
			public ushort Seq;
			public readonly Random Rng;
			public float Skill;
			public float Yaw, Pitch;
			public float ThinkTimer, StrafeTimer, BurstTimer, PauseTimer, JumpTimer;
			public int StrafeDir = 1;
			public ServerPlayer Target;
			public int LootIdx = -1;
			public bool DiveForward;
			public Vec3 Goal;
			public bool HasGoal;
			public bool Toggle;
			// navigation : chemin vers le but (points à suivre), but du chemin, et détection de blocage
			public readonly List<Vec3> Path = new List<Vec3>();
			public int PathIdx;
			public Vec3 PathGoal, SteerPoint, LastPos;
			public float RepathTimer, SteerTimer, StuckTimer;
			public int StuckCount;
			public bool PathFailed;
			/// <summary>Dans l'avion : instant du saut (temps de vol) et point d'atterrissage visé.</summary>
			public float JumpAt;
			public Vec3 LandTarget;
			/// <summary>Erreur de visée (radians), tirée de temps en temps : un bot faible garde un moment la même mauvaise visée.</summary>
			public float AimYawErr, AimPitchErr, AimTimer; // erreurs de visée en mètres à la cible
			/// <summary>Temps depuis lequel la cible n'est plus en vue ; délai pendant lequel il ne recule plus (coincé contre un mur) ; blocages successifs en combat.</summary>
			public float SinceSeen, NoRetreat, GrenadeTimer;
			public int FightStuck;

			public BotBrain(int seed, float skill)
			{
				Rng = new Random(seed);
				Skill = skill;
				Yaw = (float)(Rng.NextDouble() * Math.PI * 2);
				ThinkTimer = (float)Rng.NextDouble() * 0.2f;
			}
		}

		public int BotCount { get { int n = 0; for (int i = 0; i < _players.Count; i++) if (_players[i].IsBot) n++; return n; } }
		public int HumanCount => _humans.Count;
		public int BotsWithWeapon { get { int n = 0; for (int i = 0; i < _players.Count; i++) if (_players[i].IsBot && (_players[i].Inventory.Weapons[0] != null || _players[i].Inventory.Weapons[1] != null)) n++; return n; } }
		public string DebugBots() { var sb = new System.Text.StringBuilder(); for (int i = 0; i < _players.Count && i < 6; i++) { var p = _players[i]; var b = p.Brain as BotBrain; sb.AppendLine($"{p.Name} pos={p.State.Pos} alive={p.Alive} pending={p.PendingInputs} lootIdx={b?.LootIdx} target={b?.Target?.Name} yaw={b?.Yaw:F2} goal={b?.Goal} w0={p.Inventory.Weapons[0]?.WeaponId} items={p.Inventory.Items.Count}"); } return sb.ToString(); }
		public long TotalKills { get; private set; }
		public long TotalShots { get; private set; }

		private int _botNameCounter;

		/// <summary>Grille de navigation des bots (construite à la demande : un serveur sans bots n'en a pas besoin).</summary>
		private NavGrid _nav;
		/// <summary>Recherches de chemin permises pendant ce tick : elles sont étalées dans le temps pour ne pas gonfler un tick.</summary>
		private int _pathBudget;

		/// <summary>
		/// Point vers lequel un bot doit marcher pour atteindre <paramref name="goal"/> : le but lui-même si la ligne droite est libre,
		/// sinon le prochain virage d'un chemin A* qui contourne les obstacles. Le chemin est recalculé quand le but bouge, toutes les
		/// ~1,5 s, ou quand le bot est coincé.
		/// </summary>
		private Vec3 Steer(ServerPlayer p, BotBrain b, Vec3 goal, float dt)
		{
			b.SteerTimer -= dt;
			if (b.SteerTimer > 0f) return b.SteerPoint;
			b.SteerTimer = 0.12f;
			_nav ??= new NavGrid(_data);
			if (b.PathFailed) { b.PathFailed = false; }
			Vec3 pos = p.State.Pos;
			b.RepathTimer -= 0.12f;

			if (_nav.LineClear(pos, goal)) { b.Path.Clear(); b.SteerPoint = goal; return goal; }

			float gdx = b.PathGoal.X - goal.X, gdz = b.PathGoal.Z - goal.Z;
			if (b.Path.Count == 0 || b.RepathTimer <= 0f || gdx * gdx + gdz * gdz > 36f)
			{
				if (_pathBudget <= 0) { b.SteerTimer = 0.03f; return b.Path.Count > 0 ? b.Path[Math.Min(b.PathIdx, b.Path.Count - 1)] : goal; }   // trop de recherches ce tick : on réessaie
				_pathBudget--;
				b.RepathTimer = 2.0f + (float)b.Rng.NextDouble() * 1.5f;
				b.PathIdx = 0;
				b.PathGoal = goal;
				if (!_nav.FindPath(pos, goal, b.Path, 14000)) { b.Path.Clear(); b.SteerPoint = goal; b.PathFailed = true; return goal; }   // pas de chemin : tout droit, et on changera de but
			}
			if (b.Path.Count == 0) { b.SteerPoint = goal; return goal; }      // départ et but dans la même case : rien à suivre
			// on avance dans le chemin quand le point est atteint, ou visible et proche
			while (b.PathIdx < b.Path.Count - 1)
			{
				var w = b.Path[b.PathIdx];
				float dx = w.X - pos.X, dz = w.Z - pos.Z;
				if (dx * dx + dz * dz < 2.2f * 2.2f) b.PathIdx++; else break;
			}
			b.SteerPoint = b.Path[Math.Min(b.PathIdx, b.Path.Count - 1)];
			return b.SteerPoint;
		}

		private int BotFillTarget => _opt.BotFillTarget > 0 ? Math.Min(_opt.BotFillTarget, _m.MaxPlayers) : _m.MaxPlayers;

		/// <summary>Garde la partie remplie : ajoute des bots s'il manque du monde, en retire s'il y en a trop.</summary>
		private void MaintainBots()
		{
			if (!_opt.FillBots) return;
			int missing = BotFillTarget - _players.Count;
			if (_opt.MaxBots >= 0) missing = Math.Min(missing, _opt.MaxBots - BotCount);
			if (_opt.MaxBots >= 0 && BotCount > _opt.MaxBots) missing = Math.Min(missing, -(BotCount - _opt.MaxBots));
			// quelques bots par seconde, pour ne pas surcharger un tick
			for (int i = 0; i < missing && i < 6; i++) AddBot();
			for (int i = missing; i < 0; i++)
			{
				var bot = PickBotToRemove(-1);
				if (bot == null) break;
				RemoveBot(bot);
			}
		}

		private void AddBot()
		{
			int slot = -1;
			for (int i = 0; i < _slots.Length; i++) if (_slots[i] == null) { slot = i; break; }
			if (slot < 0) return;

			int n = _botNameCounter++;
			string name = "BOT " + BotNames[n % BotNames.Length] + (n >= BotNames.Length ? " " + (n / BotNames.Length + 1) : "");
			var bot = new ServerPlayer { Id = (ushort)slot, Name = name, Team = (byte)JoinTeam(), IsBot = true };
			bot.State = PlayerMovement.Spawn(Modal ? ModalSpawn(bot) : PickSpawn(), _world);
			bot.Appearance = RandomAppearance(7919 * (slot + 1) + n);
			bot.Brain = new BotBrain(7919 * (slot + 1) + n, _opt.BotSkill);
			GiveStartingLoadout(bot);
			_slots[slot] = bot;
			_players.Add(bot);
			BroadcastRoster();
		}

		/// <summary>Apparence au hasard d'un bot : peau, morphologie, tenue. Les octets sont lus modulo par le client.</summary>
		private static byte[] RandomAppearance(int seed)
		{
			// chaque octet est une empreinte de la graine : un xorshift démarré de graines voisines donne des tirages presque
			// identiques, et tous les bots auraient la même coiffure
			float F(int i) => (Mix((uint)seed * 2654435761u + (uint)(i + 1) * 0x9E3779B9u) & 0xFFFFFF) / (float)0x1000000;
			var a = new byte[Protocol.AppearanceSize];
			bool female = F(0) < 0.3f;
			a[0] = (byte)(F(1) * 255);
			a[1] = female ? (byte)255 : (byte)0;
			a[2] = (byte)(F(2) * 160 + 48);
			a[3] = (byte)(F(3) * 160 + 48);
			a[4] = (byte)(F(4) * 120 + 70);
			a[5] = (byte)(F(5) * 100 + 60);
			for (int i = 6; i < 10; i++) a[i] = (byte)(1 + (int)(F(i + 5) * 254));
			return a;
		}

		private static uint Mix(uint x)
		{
			x ^= x >> 16; x *= 0x7feb352du;
			x ^= x >> 15; x *= 0x846ca68bu;
			x ^= x >> 16;
			return x;
		}

		private void RemoveBot(ServerPlayer bot)
		{
			if (_slots[bot.Id] != bot) return;
			if (bot.Alive && !Modal) DropAllLoot(bot);
			_slots[bot.Id] = null;
			_players.Remove(bot);
			LeaveTeam(bot.Team);
			BroadcastRoster();
		}

		/// <summary>Choisit le bot qui laisse sa place : dans une équipe qui a déjà des humains si possible, sinon le dernier arrivé.</summary>
		private ServerPlayer PickBotToRemove(int preferredTeam)
		{
			ServerPlayer best = null;
			int bestHumans = -1;
			for (int i = _players.Count - 1; i >= 0; i--)
			{
				var b = _players[i];
				if (!b.IsBot) continue;
				int humansInTeam = 0;
				for (int j = 0; j < _humans.Count; j++) if (_humans[j].Team == b.Team) humansInTeam++;
				if (humansInTeam > bestHumans) { bestHumans = humansInTeam; best = b; }
			}
			return best;
		}

		private bool TryFreeSlotFromBot(out int slot)
		{
			slot = -1;
			var bot = PickBotToRemove(-1);
			if (bot == null) return false;
			slot = bot.Id;
			RemoveBot(bot);
			return true;
		}

		// ------------------------------------------------------------------ comportement

		private void ThinkBots(float dt)
		{
			_pathBudget = 2;
			for (int i = 0; i < _players.Count; i++)
			{
				var p = _players[i];
				if (p.IsBot && p.Alive && !p.Downed && (!Phased || Phase == MatchPhase.Playing))
				{
					// un bot qui plante ne doit jamais faire sauter le tick des autres joueurs
					try { if (p.State.Stance == Stance.Plane || p.State.Stance == Stance.Diving) { if (ControllerOf(p) == p) ThinkBotAir(p, (BotBrain)p.Brain, dt); } else Think(p, (BotBrain)p.Brain, dt); }
					catch (Exception ex) { if (_tick % 600 == 0) Console.WriteLine($"[bot {p.Name}] {ex.GetType().Name} : {ex.Message}"); }
				}
			}
		}

		private static float WrapAngle(float a)
		{
			while (a > MathF.PI) a -= MathF.PI * 2f;
			while (a < -MathF.PI) a += MathF.PI * 2f;
			return a;
		}

		/// <summary>Distances (m) : il s'arrête à FollowDistance du joueur, court au-delà de FollowSprintDistance, ignore ennemis et butin au-delà de FollowAbandonDistance.</summary>
		/// <summary>Rayon (m) à l'intérieur duquel deux joueurs se repoussent.</summary>
		private const float SeparationRadius = 1.4f;
		private const float FollowDistance = 5f, FollowSprintDistance = 10f, FollowLootDistance = 12f, FollowAbandonDistance = 20f;

		/// <summary>Probabilité qu'un bot vise juste à un instant donné : facile 0,05, normal 0,3, difficile 0,6, expert ~0,95.</summary>
		private static float AccurateChance(float skill)
		{
			return GameBalance.BotAccuracy(skill); // réglable (balance.json : bot.accuracy.*)
		}

		private void Think(ServerPlayer p, BotBrain b, float dt)
		{
			b.ThinkTimer -= dt;
			if (b.ThinkTimer <= 0f)
			{
				b.ThinkTimer = 0.12f + (float)b.Rng.NextDouble() * 0.1f;
				Perceive(p, b);
			}

			var inv = p.Inventory;
			var weapon = inv.Active;
			var def = weapon != null ? WeaponDefs.Get(weapon.WeaponId) : null;
			Vec3 pos = p.State.Pos;
			float forward = 1f, strafe = 0f, desiredYaw = b.Yaw, desiredPitch = 0f;
			var buttons = Buttons.None;
			int slot = 0;
			b.Toggle = !b.Toggle;

			// arme absente ou vide (plus de balles) : il passe à l'autre arme si elle est utilisable
			if ((weapon == null || !BotUsable(p, weapon)) && !p.Inventory.BombInHand)
			{
				int alt = -1;
				for (int i = 0; i < 2 && i < inv.Weapons.Length; i++)
					if (inv.Weapons[i] != null && inv.Weapons[i] != weapon && BotUsable(p, inv.Weapons[i])) { alt = i; break; }
				if (alt < 0 && weapon == null)
					for (int i = 0; i < 2 && i < inv.Weapons.Length; i++) if (inv.Weapons[i] != null) { alt = i; break; }
				if (alt >= 0) slot = alt + 1;
			}
			b.SinceSeen += dt;
			b.NoRetreat -= dt;

			// un bot dont l'équipe compte un joueur humain reste près de lui, et le rejoint s'il s'en éloigne
			ServerPlayer leader = null;
			float leaderDist = float.MaxValue;
			foreach (var q in _humans)
			{
				if (!q.Alive || q.Team != p.Team) continue;
				float ldx = q.State.Pos.X - pos.X, ldz = q.State.Pos.Z - pos.Z;
				float ld = MathF.Sqrt(ldx * ldx + ldz * ldz);
				if (ld < leaderDist) { leaderDist = ld; leader = q; }
			}
			if (Modal) leader = null;      // dans les modes à objectif, un bot joue l'objectif au lieu de suivre un humain
			// la zone : un bot hors de la zone visée (ou qui n'est pas assez au centre) y retourne, avant de suivre un joueur ou de chercher du butin
			bool zoneRun = BotZoneGoal(p, b, out Vec3 zoneGoal);
			if (zoneRun && (b.Target == null || !b.Target.Alive || !_zone.Inside(pos.X, pos.Z))) { leader = null; b.LootIdx = -1; }
			else zoneRun = false;
			bool regroup = leader != null && leaderDist > FollowAbandonDistance; // trop loin : plus de combat ni de butin, on le rejoint

			bool fighting = !regroup && b.Target != null && b.Target.Alive && weapon != null;
			bool modeGoal = false; Vec3 mGoal = default; float mArrive = 2f; Buttons mHold = Buttons.None;
			if (Modal && !fighting) modeGoal = _mode.BotGoal(p, b, out mGoal, out mArrive, out mHold);
			if (fighting)
			{
				Vec3 eye = pos + new Vec3(0, Sim.EyeFor(p.State.Stance), 0);
				Vec3 d = b.Target.State.Pos + new Vec3(0, 1.0f, 0) - eye;
				float horiz = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
				desiredYaw = MathF.Atan2(-d.X, -d.Z);
				desiredPitch = MathF.Atan2(d.Y, horiz);
				// la balle tombe : il vise un peu au-dessus de la cible, selon le temps de vol
				float flight = horiz / (def != null ? def.Speed : BulletSpeed);
				desiredPitch += 0.5f * BulletGravity * flight * flight / MathF.Max(horiz, 1f);

				// Un bot moins bon vise moins bien. Toutes les ~0,5 s il « décide » de sa visée : juste avec une probabilité qui dépend
				// de son niveau (facile : 1 fois sur 20), sinon à côté de plus d'un mètre, donc la balle rate toujours. L'erreur est
				// persistante pendant ces ~0,5 s (sinon elle se moyenne et le bot toucherait presque toujours) et exprimée en mètres
				// à la distance de la cible (un écart angulaire fixe toucherait encore de près).
				b.AimTimer -= dt;
				if (b.AimTimer <= 0f)
				{
					b.AimTimer = 0.35f + (float)b.Rng.NextDouble() * 0.35f;
					if (b.Rng.NextDouble() < AccurateChance(b.Skill))
					{
						b.AimYawErr = (float)(b.Rng.NextDouble() * 2 - 1) * 0.12f;
						b.AimPitchErr = (float)(b.Rng.NextDouble() * 2 - 1) * 0.10f;
					}
					else
					{
						float side = b.Rng.Next(2) == 0 ? -1f : 1f;
						b.AimYawErr = side * (0.9f + (float)b.Rng.NextDouble() * 1.7f);   // la cible fait ~0,8 m de large
						b.AimPitchErr = ((float)b.Rng.NextDouble() * 2 - 1) * 1.2f;
					}
				}
				float aimDist = MathF.Max(1f, MathF.Sqrt(horiz * horiz + d.Y * d.Y));
				desiredYaw += MathF.Atan2(b.AimYawErr, aimDist);
				desiredPitch += MathF.Atan2(b.AimPitchErr, aimDist);

				b.StrafeTimer -= dt;
				if (b.StrafeTimer <= 0f) { b.StrafeDir = -b.StrafeDir; b.StrafeTimer = 0.5f + (float)b.Rng.NextDouble() * 1.0f; }
				strafe = b.StrafeDir;
				forward = horiz > (def.Range > 100 ? 40f : 15f) ? 1f : (horiz < 6f ? -1f : 0f);
				if (b.NoRetreat > 0f && forward < 0f) forward = 0f;      // coincé contre un mur : il ne recule plus
				if (b.SinceSeen > 0.6f)
				{
					// cible perdue de vue (derrière un mur) : il la poursuit en contournant les obstacles, sans tirer dans le décor
					var chase = Steer(p, b, b.Target.State.Pos, dt);
					desiredYaw = MathF.Atan2(-(chase.X - pos.X), -(chase.Z - pos.Z));
					desiredPitch = 0f;
					forward = 1f; strafe = 0f;
					buttons |= Buttons.Sprint;
				}

				// rafales : il tire un moment, puis fait une pause
				b.BurstTimer -= dt;
				if (b.BurstTimer <= 0f)
				{
					if (b.PauseTimer > 0f) { b.PauseTimer = 0f; b.BurstTimer = 0.4f + (float)b.Rng.NextDouble() * 0.8f; }
					else { b.PauseTimer = 0.15f + (float)b.Rng.NextDouble() * 0.4f * (1.2f - b.Skill); b.BurstTimer = b.PauseTimer; }
				}
				bool inBurst = b.PauseTimer <= 0f;
				if (inBurst && b.SinceSeen <= 0.6f && !p.Reloading && weapon.Magazine > 0 && (def.Automatic || b.Toggle)) buttons |= Buttons.Fire;
				if (b.Rng.Next(120) == 0) buttons |= Buttons.Crouch;
				b.GrenadeTimer -= dt;
				if (b.GrenadeTimer <= 0f && b.SinceSeen < 0.3f) { b.GrenadeTimer = 3f; TryBotGrenade(p, b, horiz, d.Y); }
			}
			else if (modeGoal)
			{
				// objectif du mode : aller au point visé, puis tenir position (et la touche d'action : pose, désamorçage)
				float mdx = mGoal.X - pos.X, mdz = mGoal.Z - pos.Z;
				float mh = MathF.Sqrt(mdx * mdx + mdz * mdz);
				if (mh > mArrive)
				{
					var steer = Steer(p, b, mGoal, dt);
					if (b.PathFailed) b.PathFailed = false;
					desiredYaw = MathF.Atan2(-(steer.X - pos.X), -(steer.Z - pos.Z));
					if (mh > 8f) buttons |= Buttons.Sprint;
					b.JumpTimer -= dt;
					if (b.JumpTimer <= 0f) { b.JumpTimer = 3f + (float)b.Rng.NextDouble() * 5f; if (b.Toggle && mh > 12f) buttons |= Buttons.Jump; }
				}
				else
				{
					forward = 0f;
					buttons |= mHold;
					b.StrafeTimer -= dt;
					if (b.StrafeTimer <= 0f) { b.StrafeDir = -b.StrafeDir; b.StrafeTimer = 1f + (float)b.Rng.NextDouble() * 2f; }
					if (mHold == Buttons.None) strafe = 0.5f * b.StrafeDir;
				}
			}
			else if (!regroup && (leader == null || leaderDist < FollowLootDistance) && b.LootIdx >= 0 && b.LootIdx < _lootHigh && _loot[b.LootIdx].Active)
			{
				Vec3 d = _loot[b.LootIdx].Pos - pos;
				float horiz = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
				var steer = horiz < 4f ? _loot[b.LootIdx].Pos : Steer(p, b, _loot[b.LootIdx].Pos, dt);
				if (b.PathFailed) { b.LootIdx = -1; b.PathFailed = false; }      // objet inaccessible : on l'oublie
				desiredYaw = MathF.Atan2(-(steer.X - pos.X), -(steer.Z - pos.Z));
				if (horiz < 2.4f)
				{
					forward = 0f;
					desiredPitch = -0.7f; // il faut regarder l'objet pour le ramasser
					if (b.Toggle) buttons |= Buttons.Interact;
				}
				else buttons |= Buttons.Sprint;
			}
			else if (leader != null)
			{
				// chaque bot a sa place autour du joueur (angle d'or) : ils ne se rejoignent pas tous au même point
				float slotAngle = p.Id * 2.3999632f, slotRadius = 2.5f + (p.Id % 3);
				Vec3 spot = leader.State.Pos + new Vec3(MathF.Cos(slotAngle) * slotRadius, 0, MathF.Sin(slotAngle) * slotRadius);
				Vec3 d = (leaderDist > FollowSprintDistance ? leader.State.Pos : spot) - pos;
				float horiz = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
				if (horiz > (leaderDist > FollowSprintDistance ? FollowDistance : 1.2f))
				{
					var steer = Steer(p, b, leaderDist > FollowSprintDistance ? leader.State.Pos : spot, dt);
					desiredYaw = MathF.Atan2(-(steer.X - pos.X), -(steer.Z - pos.Z));
					if (horiz > FollowSprintDistance) buttons |= Buttons.Sprint;
					b.JumpTimer -= dt;
					if (b.JumpTimer <= 0f) { b.JumpTimer = 2f + (float)b.Rng.NextDouble() * 4f; if (b.Toggle && horiz > FollowSprintDistance) buttons |= Buttons.Jump; }
				}
				else
				{
					// arrivé près du joueur : il s'arrête et regarde dans la même direction que lui
					forward = 0f;
					desiredYaw = leader.Yaw;
				}
			}
			else
			{
				// il se promène vers un point choisi au hasard
				if (b.PathFailed) { b.HasGoal = false; b.PathFailed = false; }      // but inaccessible : un autre
				if (zoneRun) { b.Goal = zoneGoal; b.HasGoal = true; }
				else if (b.HasGoal) b.Goal = ZoneClamp(b.Goal);
				if (!b.HasGoal || Vec3.DistanceSquared(pos, b.Goal) < 64f)
				{
					float gx = pos.X + (float)(b.Rng.NextDouble() * 2 - 1) * 250f, gz = pos.Z + (float)(b.Rng.NextDouble() * 2 - 1) * 250f;
					b.Goal = ZoneClamp(new Vec3(Math.Clamp(gx, _data.MinX + 20, _data.MaxX - 20), 0, Math.Clamp(gz, _data.MinZ + 20, _data.MaxZ - 20)));
					b.HasGoal = true;
				}
				var wander = Steer(p, b, b.Goal, dt);
				desiredYaw = MathF.Atan2(-(wander.X - pos.X), -(wander.Z - pos.Z));
				buttons |= Buttons.Sprint;
				b.JumpTimer -= dt;
				if (b.JumpTimer <= 0f) { b.JumpTimer = 3f + (float)b.Rng.NextDouble() * 6f; if (b.Toggle) buttons |= Buttons.Jump; }
			}

			// coincé ? (il veut avancer mais ne bouge pas) : on recalcule le chemin, puis il saute, puis il change de but
			b.StuckTimer += dt;
			if (b.StuckTimer >= 1f)
			{
				float mdx = pos.X - b.LastPos.X, mdz = pos.Z - b.LastPos.Z;
				// en combat : s'il ne bouge pas malgré ses déplacements, il ne recule plus, change de côté, puis abandonne sa cible
				if (fighting && mdx * mdx + mdz * mdz < 0.3f * 0.3f)
				{
					b.NoRetreat = 2f; b.StrafeDir = -b.StrafeDir; b.StrafeTimer = 1f;
					if (++b.FightStuck >= 3) { b.Target = null; b.FightStuck = 0; b.Path.Clear(); b.RepathTimer = 0f; }
				}
				else if (fighting) b.FightStuck = 0;
				bool wantsToMove = !fighting && forward > 0f && !(b.LootIdx >= 0 && !regroup && Vec3.DistanceSquared(pos, _loot[b.LootIdx].Pos) < 6f);
				if (wantsToMove && mdx * mdx + mdz * mdz < 0.5f * 0.5f && p.Alive)
				{
					b.StuckCount++;
					b.RepathTimer = 0f; b.SteerTimer = 0f;
					if (b.StuckCount >= 2) buttons |= Buttons.Jump;
					if (b.StuckCount >= 4) { b.HasGoal = false; b.LootIdx = -1; b.Path.Clear(); b.StuckCount = 0; }
				}
				else b.StuckCount = 0;
				b.LastPos = pos;
				b.StuckTimer = 0f;
			}

			// il tourne à une vitesse limitée, comme une souris
			float maxTurn = (200f + 520f * b.Skill) * MathF.PI / 180f * dt;
			float delta = WrapAngle(desiredYaw - b.Yaw);
			b.Yaw += Math.Clamp(delta, -maxTurn, maxTurn);
			b.Pitch += Math.Clamp(desiredPitch - b.Pitch, -maxTurn, maxTurn);

			if (weapon != null && def != null && !def.Melee && def.MagazineSize > 0 && !p.Reloading && b.Toggle
				&& (weapon.Magazine == 0 || (!fighting && weapon.Magazine < def.MagazineSize / 2)))
				buttons |= Buttons.Reload;

			// pas de collision entre joueurs côté serveur : un bot s'écarte de ceux qui le touchent au lieu de se superposer
			Vec3 push = Vec3.Zero;
			for (int k = 0; k < _players.Count; k++)
			{
				var o = _players[k];
				if (o == p || !o.Alive || o.State.Stance == Stance.Plane) continue;
				float sx = pos.X - o.State.Pos.X, sz = pos.Z - o.State.Pos.Z;
				float sd2 = sx * sx + sz * sz;
				if (sd2 >= SeparationRadius * SeparationRadius) continue;
				float sd = MathF.Sqrt(sd2);
				if (sd < 0.01f) { sx = (p.Id % 2 == 0 ? 1f : -1f); sz = 0.5f; sd = 1f; }
				float w = 1f - sd / SeparationRadius;
				push = push + new Vec3(sx / sd * w, 0, sz / sd * w);
			}
			if (push.X != 0f || push.Z != 0f)
			{
				float sy = MathF.Sin(b.Yaw), cy = MathF.Cos(b.Yaw);
				forward = Math.Clamp(forward + (-sy * push.X - cy * push.Z) * 2f, -1f, 1f);
				strafe = Math.Clamp(strafe + (cy * push.X - sy * push.Z) * 2f, -1f, 1f);
			}

			// un bot ramasse seulement l'objet qu'il est venu chercher (SeekLoot ne choisit que ce dont il a besoin) : sans cible, le serveur prenait le plus proche de son regard
			ushort lootTarget = (buttons & Buttons.Interact) != 0 && b.LootIdx >= 0 ? (ushort)(LootIdBase + b.LootIdx) : PlayerInput.NoTarget;
			p.EnqueueInput(PlayerInput.Create(++b.Seq, forward, strafe, buttons, slot, b.Yaw, b.Pitch, _tick, lootTarget));
		}

		/// <summary>Regarde autour du bot : ennemi visible le plus proche, sinon objet utile à ramasser.</summary>
		private void Perceive(ServerPlayer p, BotBrain b)
		{
			Vec3 pos = p.State.Pos;
			Vec3 eye = pos + new Vec3(0, Sim.EyeFor(p.State.Stance), 0);
			var scratch = _lootScratch;

			if (p.BlindLeft > 0f) { b.Target = null; return; }      // aveuglé par une grenade flash : il ne voit plus rien

			// sans arme utilisable, un bot ne cherche pas la bagarre : il cherche de quoi s'équiper
			var held = p.Inventory.Active;
			var heldDef = held != null ? WeaponDefs.Get(held.WeaponId) : null;
			bool canFight = false;
			for (int wi = 0; wi < p.Inventory.Weapons.Length; wi++)
				if (p.Inventory.Weapons[wi] != null && BotUsable(p, p.Inventory.Weapons[wi])) canFight = true;
			if (!canFight) b.Target = null;

			ServerPlayer best = null;
			if (canFight)
			{
				scratch.Clear();
				_playerGrid.Query(pos.X, pos.Z, 90f, scratch);
				best = FindVisibleEnemy(p, eye, pos, scratch);
				// il garde sa cible un instant même s'il la perd de vue
				if (best != null) b.SinceSeen = 0f;
				// cible perdue de vue : on la garde quelques secondes (poursuite), puis on l'oublie
				if (best != null || b.Target == null || !b.Target.Alive || b.SinceSeen > 4f) b.Target = best;
				if (b.Target != null) { b.LootIdx = -1; return; }
			}
			SeekLoot(p, b, pos);
		}

		/// <summary>Un bot qui voit sa cible à portée de lancer jette une grenade (au plus une toutes les 8 à 16 s) : fragmentation de près, flash ou autre sinon.</summary>
		private void TryBotGrenade(ServerPlayer p, BotBrain b, float horiz, float rise)
		{
			if (horiz < 6f || horiz > 15f || p.GrenadeCooldown > 0f) return;
			byte pick = 0;
			byte[] order = horiz < 11f
				? new[] { ItemIds.GrenadeFrag, ItemIds.GrenadeSticky, ItemIds.GrenadeFire, ItemIds.GrenadeFlash }
				: new[] { ItemIds.GrenadeFlash, ItemIds.GrenadeFrag, ItemIds.GrenadeFire, ItemIds.GrenadeSticky };
			foreach (var id in order) if (p.Inventory.CountOf(id) > 0) { pick = id; break; }
			if (pick == 0) return;
			// angle de lancer pour que la portée à plat égale la distance : v² sin(2a) / g
			float s = Math.Clamp((horiz + rise * 0.5f) * GrenadeDefs.Gravity / (GrenadeDefs.ThrowSpeed * GrenadeDefs.ThrowSpeed), 0.05f, 0.98f);
			b.GrenadeTimer = 8f + (float)b.Rng.NextDouble() * 8f;
			ThrowGrenade(p, pick, 0.5f * MathF.Asin(s), b.Yaw);
		}

		/// <summary>Une arme est utilisable si elle est blanche ou s'il lui reste des balles (chargeur ou réserve).</summary>
		private static bool BotUsable(ServerPlayer p, WeaponSlot w)
		{
			var d = WeaponDefs.Get(w.WeaponId);
			return d != null && (d.Melee || w.Magazine > 0 || p.Inventory.GetAmmo(d.AmmoType) > 0);
		}

		private ServerPlayer FindVisibleEnemy(ServerPlayer p, Vec3 eye, Vec3 pos, List<int> scratch)
		{

			ServerPlayer best = null;
			float bestD2 = 90f * 90f;
			for (int i = 0; i < scratch.Count; i++)
			{
				var o = _slots[scratch[i]];
				if (o == null || o == p || !o.Alive || o.State.Stance == Stance.Plane || o.Team == p.Team) continue;
				float d2 = Vec3.DistanceSquared(o.State.Pos, pos);
				if (d2 >= bestD2) continue;
				// ligne de vue : rien ne doit bloquer entre ses yeux et la cible
				Vec3 to = o.State.Pos + new Vec3(0, 1.0f, 0) - eye;
				float dist = to.Length();
				if (_world.Raycast(eye, to / dist, dist, out float wall) && wall < dist - 0.5f) continue;
				bestD2 = d2;
				best = o;
			}
			return best;
		}

		/// <summary>Une arme au sol vaut le détour si elle a des balles dans son chargeur ou si le bot a des munitions pour elle.</summary>
		private static bool WorthTaking(InventoryState inv, byte weaponId, short magazine)
		{
			var wd = WeaponDefs.Get(weaponId);
			return wd != null && (wd.Melee || magazine != 0 || inv.GetAmmo(wd.AmmoType) > 0);
		}

		private void SeekLoot(ServerPlayer p, BotBrain b, Vec3 pos)
		{
			var scratch = _lootScratch;
			// objet utile à proximité
			var inv = p.Inventory;
			// munitions voulues : pour chacune de ses armes dont la réserve est basse (deux types au plus)
			byte ammoA = 0, ammoB = 0;
			bool anyUsable = false, freeSlot = false;
			for (int wi = 0; wi < inv.Weapons.Length; wi++)
			{
				var w = inv.Weapons[wi];
				if (w == null) { freeSlot = true; continue; }
				var wd = WeaponDefs.Get(w.WeaponId);
				if (wd == null) continue;
				if (BotUsable(p, w)) anyUsable = true;
				if (wd.AmmoType != 0 && inv.GetAmmo(wd.AmmoType) < wd.MagazineSize * 2)
				{
					if (ammoA == 0) ammoA = wd.AmmoType; else if (ammoA != wd.AmmoType) ammoB = wd.AmmoType;
				}
			}
			bool needWeapon = !anyUsable;                 // plus rien pour tirer : arme ou munitions, vite
			bool wantWeapon = needWeapon || freeSlot;
			bool wantBackpack = inv.BackpackLevel < 3;

			scratch.Clear();
			_lootGrid.Query(pos.X, pos.Z, needWeapon ? 130f : 70f, scratch);
			int bestIdx = -1;
			float bestLoot = float.MaxValue;
			for (int i = 0; i < scratch.Count; i++)
			{
				int idx = scratch[i] - LootIdBase;
				if (idx < 0 || idx >= _lootHigh || !_loot[idx].Active) continue;
				var def = ItemDefs.Get(_loot[idx].Item.ItemId);
				if (def == null) continue;
				bool wanted = (wantWeapon && def.Kind == ItemKind.Weapon && WorthTaking(inv, def.Id, _loot[idx].Item.Magazine))
					|| (def.Kind == ItemKind.Ammo && def.AmmoType != 0 && (def.AmmoType == ammoA || def.AmmoType == ammoB))
					|| (def.Kind == ItemKind.Backpack && wantBackpack && def.BackpackLevel > inv.BackpackLevel)
					|| (def.Kind == ItemKind.Optic && !needWeapon && inv.Items.Count < inv.Capacity)
					|| (def.Kind == ItemKind.Grenade && !needWeapon && inv.CountOf(def.Id) < def.StackSize && (inv.CountOf(def.Id) > 0 || inv.Items.Count < inv.Capacity));
				if (!wanted) continue;
				float d2 = Vec3.DistanceSquared(_loot[idx].Pos, pos);
				if (d2 < bestLoot) { bestLoot = d2; bestIdx = idx; }
			}
			b.LootIdx = bestIdx;
		}
	}
}
