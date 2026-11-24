using System;
using System.Collections.Generic;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>Règles de jeu côté serveur : déplacement des joueurs, armes, tirs, butin, mort et réapparition.</summary>
	public sealed partial class GameServer
	{
		private Vec3 PickSpawn()
		{
			// On tire quelques emplacements et on garde celui qui est le plus loin des joueurs en vie.
			Vec3 best = Vec3.Zero;
			float bestScore = -1f;
			for (int attempt = 0; attempt < 8; attempt++)
			{
				Vec3 c;
				if (_opt.SpawnClusterRadius > 0f)
				{
					double a = _rng.NextDouble() * Math.PI * 2.0;
					double r = Math.Sqrt(_rng.NextDouble()) * _opt.SpawnClusterRadius;
					c = new Vec3((float)(Math.Cos(a) * r), 0f, (float)(Math.Sin(a) * r));
				}
				else if (_data.SpawnPoints.Count > 0)
					c = _data.SpawnPoints[_rng.Next(_data.SpawnPoints.Count)];
				else
					c = Vec3.Zero;

				float nearest = float.MaxValue;
				for (int i = 0; i < _players.Count; i++)
				{
					if (!_players[i].Alive) continue;
					float d = Vec3.DistanceSquared(_players[i].State.Pos, c);
					if (d < nearest) nearest = d;
				}
				if (nearest > bestScore) { bestScore = nearest; best = c; }
			}
			return best;
		}

		// ------------------------------------------------------------------ simulation d'un joueur

		private void SimulatePlayer(ServerPlayer p, float dt)
		{
			// Une commande par tick. Si le joueur a pris du retard, on en joue parfois deux, dans la limite d'un budget :
			// sans cette limite, un client truqué pourrait envoyer plus de commandes que de ticks et courir plus vite.
			p.CatchUpBudget = MathF.Min(3f, p.CatchUpBudget + 0.125f);
			if (p.Away) p.EnqueueIdle();        // joueur déconnecté : son personnage reste sur place en attendant son retour
			int toRun = 0;
			if (p.PendingInputs > 0)
			{
				toRun = 1;
				if (p.PendingInputs > 3 && p.CatchUpBudget >= 1f) { toRun = 2; p.CatchUpBudget -= 1f; }
			}

			if (p.Alive)
			{
				p.ShotCooldown -= dt;
				if (p.UseId != 0) TickUse(p, dt);
				if (p.Reloading)
				{
					p.ReloadLeft -= dt;
					if (p.ReloadLeft <= 0f) FinishReload(p);
				}
			}

			// chute en équipe : un passager suit les commandes de son chef de saut (les siennes sont lues pour être acquittées, puis ignorées)
			if (p.Alive && InAir(p))
			{
				var lead = ControllerOf(p);
				if (lead != p)
				{
					while (p.TryDequeueInput(out var qi)) p.LastProcessedSeq = qi.Seq;
					PassengerStep(p, lead, dt);
					return;
				}
			}

			for (int k = 0; k < toRun; k++)
			{
				p.TryDequeueInput(out var inp);
				p.LastProcessedSeq = inp.Seq;
				if (!p.Alive) { p.Holding = false; continue; }
				if (p.Ride != null)
				{
					if (Phase == MatchPhase.Playing) VehicleInput(p, inp);
					continue;
				}
				float modeScale = 1f;
				if (Modal)
				{
					// phase d'achat : on ne bouge pas ; l'état d'interaction (pose, désamorçage) est suivi par le mode
					if (_mode.Frozen(p)) { inp.Forward = 0; inp.Strafe = 0; inp.ButtonBits &= unchecked((ushort)~((ushort)Buttons.Jump | (ushort)Buttons.Sprint | (ushort)Buttons.Fire | (ushort)Buttons.Crouch)); }
					p.Holding = inp.Has(Buttons.Interact);
					modeScale = _mode.SpeedScale(p);
				}
				var before = p.State.Stance;
				PlayerMovement.Step(ref p.State, inp, _world, dt, (p.Inventory.Active == null ? GameBalance.UnarmedMultiplier : 1f) * modeScale * BodyBalance.Speed(p.Appearance));
				ControlVehicle.Separate(_vehicles,ref p.State);
				p.Yaw = inp.Yaw;
				p.Pitch = p.State.Stance == Stance.Diving ? PlayerMovement.DivePitch(PlayerMovement.DiveAmount(inp.Pitch, inp.Forward > 12)) : inp.Pitch;
				p.LastButtons = inp.Buttons;
				p.LastInput = inp;
				bool pass = inp.Has(Buttons.PassLead);
				if (pass && !p.PrevPass && (before == Stance.Plane || before == Stance.Diving) && LeaderOf(p.Team) == p) PassLead(p);
				p.PrevPass = pass;
				ApplyActions(p, inp);
			}
		}

		private void ApplyActions(ServerPlayer p, in PlayerInput inp)
		{
			// dans l'avion ou en chute libre : ni tir, ni ramassage, ni soin (on est occupé à tomber)
			if (p.State.Stance == Stance.Plane || p.State.Stance == Stance.Diving || p.State.Stance == Stance.Downed || (Phased && Phase != MatchPhase.Playing)) { p.PrevFire = inp.Has(Buttons.Fire); TickRevive(p, false, 0f); return; }
			int slot = inp.Slot;
			if (slot != 0 && slot != p.LastSlotCommand)
			{
				int target = slot == 1 ? 0 : (slot == 2 ? 1 : -1);
				if (p.Inventory.SelectSlot(target))
				{
					p.Reloading = false;
					p.UseId = 0;
					p.ShotCooldown = MathF.Max(p.ShotCooldown, 0.35f);
					SendInventory(p);
				}
			}
			p.LastSlotCommand = slot;

			bool interact = inp.Has(Buttons.Interact);
			TickRevive(p, interact && inp.Target == PlayerInput.NoTarget, 1f / _m.TickRate);
			if (interact && !p.PrevInteract) TryInteract(p, inp.Target);
			p.PrevInteract = interact;

			bool reload = inp.Has(Buttons.Reload);
			if (reload && !p.PrevReload) { StartReload(p); p.UseId = 0; }
			p.PrevReload = reload;

			bool fire = inp.Has(Buttons.Fire);
			if (fire && p.UseId != 0) p.UseId = 0;     // tirer ou frapper interrompt un soin
			var weapon = p.Inventory.Active;
			if (weapon != null)
			{
				var def = WeaponDefs.Get(weapon.WeaponId);
				bool wantShoot = def.Automatic ? fire : (fire && !p.PrevFire);
				// chargeur vide et détente tenue (même sans nouvel appui, arme semi-automatique comprise) : rechargement automatique
				if (fire && !wantShoot && !p.Reloading && !def.Melee && def.MagazineSize > 0 && weapon.Magazine <= 0) StartReload(p);
				if (wantShoot && !p.Reloading && p.ShotCooldown <= 0f)
					Shoot(p, weapon, def, inp);
			}
			else if (p.Inventory.GrenadeInHand != 0)
			{
				// grenade en main : le clic gauche la lance (un clic, pas une rafale)
				if (fire && !p.PrevFire) ThrowGrenade(p, p.Inventory.GrenadeInHand);
			}
			else if (fire && !p.Reloading && p.ShotCooldown <= 0f && !p.Inventory.BombInHand)       // la bombe en main : le clic sert à la poser, pas à frapper
			{
				// aucune arme en main : on frappe à mains nues
				Shoot(p, _fistSlot, WeaponDefs.Fists, inp);
			}
			p.PrevFire = fire;
		}

		private static readonly WeaponSlot _fistSlot = new WeaponSlot();

		// ------------------------------------------------------------------ armes

		private void StartReload(ServerPlayer p)
		{
			var w = p.Inventory.Active;
			if (w == null || p.Reloading) return;
			var def = WeaponDefs.Get(w.WeaponId);
			if (def == null || def.Melee || def.MagazineSize == 0 || w.Magazine >= Tiers.Magazine(def, w.Tier)) return;
			if (p.Inventory.GetAmmo(def.AmmoType) <= 0) return;
			p.Reloading = true;
			p.ReloadLeft = def.ReloadSeconds * Tiers.Reload(w.Tier);
		}

		private void FinishReload(ServerPlayer p)
		{
			p.Reloading = false;
			var w = p.Inventory.Active;
			if (w == null) return;
			var def = WeaponDefs.Get(w.WeaponId);
			int taken = p.Inventory.TakeAmmo(def.AmmoType, Tiers.Magazine(def, w.Tier) - w.Magazine);
			w.Magazine += taken;
			SendInventory(p);
		}

		private void Shoot(ServerPlayer p, WeaponSlot weapon, WeaponDef def, in PlayerInput inp)
		{
			if (!def.Melee)
			{
				if (weapon.Magazine <= 0) { StartReload(p); return; }
				weapon.Magazine--;
			}
			TotalShots++;
			p.ShotCooldown = def.SecondsBetweenShots + MathF.Max(p.ShotCooldown, -1f / _m.TickRate);
			p.FireFlagTicks = 4;

			Vec3 origin = p.State.Pos + new Vec3(0, Sim.EyeFor(p.State.Stance), 0);
			float cp = MathF.Cos(inp.Pitch), sp = MathF.Sin(inp.Pitch), sy = MathF.Sin(inp.Yaw), cy = MathF.Cos(inp.Yaw);
			Vec3 fwd = new Vec3(-sy * cp, sp, -cy * cp);
			Vec3 right = new Vec3(cy, 0, -sy);
			Vec3 up = right.Cross(fwd);

			// compensation de latence : on juge le tir sur les positions que le tireur voyait à l'écran
			uint viewTick = _opt.LagCompensation ? inp.ViewTick : _tick;
			if (viewTick > _tick) viewTick = _tick;
			if (_tick - viewTick > Protocol.MaxRewindTicks) viewTick = _tick - Protocol.MaxRewindTicks;

			float spreadTan = def.Melee ? 0f : MathF.Tan(def.SpreadDegrees * Tiers.Spread(weapon.Tier) * MathF.PI / 180f);
			for (int pellet = 0; pellet < def.Pellets; pellet++)
			{
				var rng = new FastRng((uint)(p.Id * 73856093u) ^ (_tick * 19349663u) ^ ((uint)pellet * 83492791u) | 1u);
				Vec3 dir = (fwd + right * (rng.NextSigned() * spreadTan) + up * (rng.NextSigned() * spreadTan)).Normalized();

				// armes à feu : une vraie balle (temps de vol, chute) ; seul le couteau touche instantanément
				if (!def.Melee) { SpawnProjectile(p, weapon.WeaponId, def.Damage * Tiers.Damage(weapon.Tier), def, origin, dir, viewTick); continue; }

				float maxDist = def.Range;
				if (_world.Raycast(origin, dir, def.Range, out float wallDist, out int pane)) maxDist = wallDist;

				ServerPlayer best = null;
				float bestT = maxDist;
				HitZone bestZone = HitZone.Torso;
				for (int i = 0; i < _players.Count; i++)
				{
					var o = _players[i];
					if (o == p || !o.Alive) continue;
					if (!_m.FriendlyFire && o.Team == p.Team) continue;
					if (!o.History.TryGet(viewTick, out var h) || !h.Alive) continue;
					float dx = h.Pos.X - origin.X, dz = h.Pos.Z - origin.Z;
					float reach = bestT + 1f;
					if (dx * dx + dz * dz > reach * reach) continue;
					if (Hitboxes.RayVsBody(h, origin, dir, bestT, out float t, out var zone) && t < bestT)
					{
						best = o; bestT = t; bestZone = zone;
					}
				}
				if (best != null)
					ApplyDamage(p, best, def.Damage * Tiers.Damage(weapon.Tier) * Hitboxes.Multiplier(bestZone), bestZone, weapon.WeaponId);
				else if (pane >= 0) HitWindow(pane, def.Damage * Tiers.Damage(weapon.Tier));
			}
		}

		private void ApplyDamage(ServerPlayer attacker, ServerPlayer victim, float damage, HitZone zone, byte weaponId)
		{
			if (Phased && Phase != MatchPhase.Playing) return;          // pas de dégâts en salle d'attente ni après la fin
			if (Modal && !_mode.CanDamage(attacker, victim)) return;     // invulnérabilité à la réapparition, phase d'achat
			if (!victim.Alive) return;
			if (victim.State.Stance == Stance.Plane) return;
			// le bouclier absorbe d'abord ; ce qui dépasse touche la vie
			float shieldBefore = victim.Shield;
			float absorbed = MathF.Min(victim.Shield, damage);
			victim.Shield -= absorbed;
			victim.Health -= damage - absorbed;
			bool killed = victim.Health <= 0f;
			attacker.DamageDealt += absorbed + MathF.Min(damage - absorbed, victim.Health + (damage - absorbed));
			victim.LastAttacker = attacker;
			victim.LastHurtTick = _tick; victim.RegenClock = 0f;
			byte dmg = (byte)Math.Clamp((int)MathF.Round(damage), 0, 255);

			// le tireur voit la couleur du bouclier touché, ses dégâts sur la cible, et entend le bouclier casser
			int shieldInfo = Math.Clamp(victim.Inventory.ShieldTier + 1, 0, 7) | (absorbed > 0f ? 0x10 : 0) | (shieldBefore > 0f && victim.Shield <= 0f ? 0x20 : 0);
			float height = zone == HitZone.Head ? 1.65f : zone == HitZone.Torso ? 1.2f : 0.6f;
			if (victim.State.Stance == Stance.Crouching || victim.State.Stance == Stance.Sliding) height *= 0.65f;
			SendEvent(attacker, new ServerEvent { Type = EventType.HitConfirm, A = victim.Id, B = (ushort)shieldInfo, C = dmg, D = (byte)((byte)zone | (killed ? 0x80 : 0)),
				Pos = new Vec3(victim.State.Pos.X, victim.State.Pos.Y + height, victim.State.Pos.Z) });
			SendEvent(victim, new ServerEvent { Type = EventType.Damaged, A = attacker.Id, C = dmg, D = (byte)(killed ? 1 : 0), Pos = attacker.State.Pos });
			if (attacker.Team != victim.Team) NotifyTeam(attacker.Team, EventType.TeamHit, victim.Id);
			if (killed) Kill(attacker, victim, weaponId);
		}

		private void Kill(ServerPlayer killer, ServerPlayer victim, byte weaponId)
		{
			if (Modal) { ModalKill(killer, victim, weaponId); return; }
			if (Royale && Phase == MatchPhase.Playing) { OnLethal(killer, victim, weaponId); return; }
			TotalKills++;
			victim.Alive = false;
			victim.Health = 0f;
			victim.Shield = 0f;
			victim.UseId = 0;
			victim.Reloading = false;
			DropAllLoot(victim);
			SendInventory(victim);
			if (_m.RespawnSeconds > 0f)
				victim.RespawnTick = _tick + (uint)(_m.RespawnSeconds * _m.TickRate);

			var feed = new ServerEvent { Type = EventType.KillFeed, A = killer.Id, B = victim.Id, C = weaponId };
			for (int i = 0; i < _players.Count; i++) SendEvent(_players[i], feed);
		}

		private void HandleRespawns()
		{
			if (Modal) { ModalRespawns(); return; }
			if (_m.RespawnSeconds <= 0f) return;
			for (int i = 0; i < _players.Count; i++)
			{
				var p = _players[i];
				if (p.Alive || _tick < p.RespawnTick) continue;
				p.Alive = true;
				p.Health = 100f;
				p.Inventory = new InventoryState();
				p.Shield = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);   // on repart avec le bouclier blanc, plein
				p.UseId = 0;
				GiveStartingLoadout(p);
				p.State = (_opt.Dropship && !p.IsBot) ? NewFlight() : PlayerMovement.Spawn(PickSpawn(), _world);
				p.ShotCooldown = 0f;
				SendInventory(p);
				SendEvent(p, new ServerEvent { Type = EventType.Respawned, Pos = p.State.Pos });
			}
		}

		/// <summary>
		/// Un vol d'avion aléatoire : une droite qui traverse la carte (angle et décalage tirés au hasard), avec une marge hors de la carte
		/// avant l'entrée (le temps de charger) ; l'avion expulse tout le monde à la sortie de la carte.
		/// </summary>
		public PlayerState NewFlight()
		{
			float cx = (_data.MinX + _data.MaxX) * 0.5f, cz = (_data.MinZ + _data.MaxZ) * 0.5f;
			float hx = (_data.MaxX - _data.MinX) * 0.5f, hz = (_data.MaxZ - _data.MinZ) * 0.5f;
			double ang = _rng.NextDouble() * Math.PI * 2.0;
			float dx = (float)Math.Cos(ang), dz = (float)Math.Sin(ang);
			float off = ((float)_rng.NextDouble() * 2f - 1f) * 0.45f * MathF.Min(hx, hz);
			float ox = cx - dz * off, oz = cz + dx * off;       // la droite passe à `off` du centre
			// entrée et sortie du rectangle de la carte (méthode des « slabs »)
			float t0 = float.NegativeInfinity, t1 = float.PositiveInfinity;
			void Slab(float o, float d, float lo, float hi)
			{
				if (MathF.Abs(d) < 1e-6f) return;
				float a = (lo - o) / d, b = (hi - o) / d;
				if (a > b) (a, b) = (b, a);
				t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
			}
			Slab(ox, dx, cx - hx, cx + hx); Slab(oz, dz, cz - hz, cz + hz);
			if (!(t1 > t0)) { t0 = -hx; t1 = hx; }
			float alt = _data.MaxHeight + Sim.PlaneHeightAboveMap;
			var from = new Vec3(ox + dx * (t0 - Sim.PlaneMargin), alt, oz + dz * (t0 - Sim.PlaneMargin));
			var to = new Vec3(ox + dx * (t1 + Sim.PlaneMargin), alt, oz + dz * (t1 + Sim.PlaneMargin));
			float enter = Sim.PlaneMargin / Sim.PlaneSpeed;
			float exit = (Sim.PlaneMargin + (t1 - t0)) / Sim.PlaneSpeed;
			return PlayerMovement.BeginFlight(from, to, enter, exit);
		}

		private void GiveStartingLoadout(ServerPlayer p)
		{
			if (!_opt.StartingLoadout) return;
			var none = new List<LootItem>();
			var bag = LootItem.Of(ItemIds.Backpack3); p.Inventory.TryPickup(ref bag, none);
			var ammo = new LootItem { ItemId = ItemIds.AmmoRifle, Count = 60 };
			for (int i = 0; i < 5; i++) { var a = ammo; p.Inventory.TryPickup(ref a, none); }
			var ak = LootItem.Of(ItemIds.Ak); p.Inventory.TryPickup(ref ak, none);
		}

		// ------------------------------------------------------------------ butin

		/// <summary>Le point est-il garni cette partie ? Les points « toujours » le sont ; les autres tirent au sort (chance propre au point, sinon réglage commun).</summary>
		private bool RollSpawn(in LootSpawn spawn)
		{
			if (spawn.Mode == LootSpawn.ModeAlways) return true;
			float chance = spawn.HasChance ? spawn.Chance : (spawn.IsChest ? GameBalance.ChestPointChance : GameBalance.ItemPointChance);
			return _rng.NextDouble() < chance;
		}

		private void SpawnInitialLoot()
		{
			if (Arena) { SpawnArenaLoot(); return; }
			var chestBoxes = new List<Aabb>();
			for (int si = 0; si < _data.LootSpawns.Count; si++)
			{
				var spawn = _data.LootSpawns[si];
				if (!RollSpawn(spawn)) continue;
				bool chest = spawn.IsChest;
				// un point sans objet imposé tire dans la table de butin à chaque partie
				byte itemId = chest ? ItemIds.Chest
					: spawn.HasPool ? spawn.PickFromPool(_rng)                                     // liste précise d'objets, avec leurs poids
					: (spawn.ItemId != ItemIds.None ? spawn.ItemId : LootTable.Roll(_rng));
				var item = BoostAmmo(LootItem.Rolled(itemId, _rng, spawn.Quality)); // les armes tirent leur niveau (blanc, bleu, violet, rouge)
				if (spawn.Count > 0 && !chest) item.Count = spawn.Count;
				if (!AddLoot(item, spawn.Pos, spawn.Quality, si + 1)) break;
				if (chest) { chestBoxes.Add(WorldData.ChestBox(spawn.Pos, spawn.Yaw)); continue; }
				// toute arme au sol a ses munitions à côté, et toute boîte de munitions son arme
				byte partner = PartnerOf(itemId);
				if (partner != ItemIds.None)
				{
					float x = spawn.Pos.X + 0.6f, z = spawn.Pos.Z + 0.4f;
					if (!AddLoot(BoostAmmo(LootItem.Rolled(partner, _rng)), new Vec3(x, LootGroundY(x, z, spawn.Pos.Y), z))) break;
				}
				// des munitions traînent aussi toutes seules, même sans arme à côté : une boîte de plus près de 4 emplacements sur 10
				var kind = ItemDefs.Get(itemId)?.Kind;
				if (kind != ItemKind.Ammo && _rng.NextDouble() < GameBalance.ExtraAmmoChance)
				{
					float x = spawn.Pos.X - 0.7f, z = spawn.Pos.Z - 0.5f;
					if (!AddLoot(BoostAmmo(LootItem.Rolled(LootTable.RollAmmo(_rng), _rng)), new Vec3(x, LootGroundY(x, z, spawn.Pos.Y), z))) break;
				}
				// et souvent un bouclier ou un soin posé à côté
				if (_rng.NextDouble() < GameBalance.ExtraSupplyChance)
				{
					float x = spawn.Pos.X + 0.1f, z = spawn.Pos.Z - 0.9f;
					if (!AddLoot(LootItem.Rolled(LootTable.RollSupply(_rng), _rng), new Vec3(x, LootGroundY(x, z, spawn.Pos.Y), z))) break;
				}
			}
			// les coffres de cette partie deviennent des obstacles pleins (le client pose les mêmes en les recevant)
			_world.SetDynamicBoxes(chestBoxes);
		}

		/// <summary>Les boîtes de munitions posées par la carte contiennent plus (réglage loot.ammoAmountMultiplier) : il n'y en avait pas assez.</summary>
		private static LootItem BoostAmmo(LootItem item)
		{
			var d = ItemDefs.Get(item.ItemId);
			if (d != null && d.Kind == ItemKind.Ammo) item.Count = (ushort)Math.Clamp((int)MathF.Round(item.Count * GameBalance.AmmoAmountMultiplier), 1, 65535);
			return item;
		}

		/// <summary>Munitions correspondant à une arme, ou arme correspondant à des munitions (au hasard parmi celles qui les utilisent).</summary>
		private byte PartnerOf(byte itemId)
		{
			var def = ItemDefs.Get(itemId);
			if (def == null) return ItemIds.None;
			if (def.Kind == ItemKind.Weapon)
			{
				var w = WeaponDefs.Get(itemId);
				return w != null && w.AmmoType != 0 ? ItemDefs.AmmoItemFor(w.AmmoType) : ItemIds.None;
			}
			if (def.Kind != ItemKind.Ammo) return ItemIds.None;
			var guns = new List<byte>();
			foreach (var d in ItemDefs.All)
				if (d.Kind == ItemKind.Weapon && WeaponDefs.Get(d.Id)?.AmmoType == def.AmmoType) guns.Add(d.Id);
			return guns.Count == 0 ? ItemIds.None : guns[_rng.Next(guns.Count)];
		}

		private bool AddLoot(LootItem item, Vec3 pos, byte quality = 0, int spawnPlusOne = 0) => AddLoot(item, pos, out _, quality, spawnPlusOne);

		private bool AddLoot(LootItem item, Vec3 pos, out int idx, byte quality = 0, int spawnPlusOne = 0)
		{
			if (_freeLoot.Count > 0) idx = _freeLoot.Pop();
			else if (_lootHigh < LootCapacity) idx = _lootHigh++;
			else { idx = -1; return false; }
			_loot[idx] = new LootEntity { Active = true, Item = item, Pos = pos, Quality = quality, SpawnPlusOne = spawnPlusOne };
			_lootDirty = true;
			return true;
		}

		/// <summary>Pose un objet au sol à proximité d'une position.</summary>
		private void DropLootAround(LootItem item, Vec3 around)
		{
			float x = around.X + (float)(_rng.NextDouble() * 2 - 1) * 1.5f;
			float z = around.Z + (float)(_rng.NextDouble() * 2 - 1) * 1.5f;
			AddLoot(item, new Vec3(x, LootGroundY(x, z, around.Y), z));
		}

		/// <summary>Hauteur où poser un objet en (x, z) : le sol du même niveau que le point de référence (étage, passerelle, toit) et non le relief en dessous.
		/// Si ce niveau n'existe pas à cet endroit (vide d'un atrium, bord de dalle), l'objet reste à la hauteur du point de référence.</summary>
		private float LootGroundY(float x, float z, float refY)
		{
			float g = _world.GroundAt(x, z, refY, 0.6f);
			return MathF.Abs(g - refY) <= 0.7f ? g : refY;
		}

		private void DropAllLoot(ServerPlayer p)
		{
			_dropScratch.Clear();
			p.Inventory.EmptyInto(_dropScratch);
			foreach (var item in _dropScratch) DropLootAround(item, p.State.Pos);
		}

		private void RebuildLootGrid()
		{
			_lootGrid.Clear();
			for (int i = 0; i < _lootHigh; i++)
				if (_loot[i].Active) _lootGrid.Insert(LootIdBase + i, _loot[i].Pos.X, _loot[i].Pos.Z);
			_lootGrid.Build();
			_lootDirty = false;
		}

		private const float PickupRange = 3f;
		private const float PickupCone = 0.866f; // cosinus de 30 degrés

		private const float PickupSlack = 1.5f; // marge sur la portée pour la cible choisie par le client (latence, déplacement)

		private void TryInteract(ServerPlayer p, ushort target = 0)
		{
			if (target == PlayerInput.NoTarget) return; // le client n'a rien sous son viseur
			Vec3 eye = p.State.Pos + new Vec3(0, Sim.EyeFor(p.State.Stance), 0);
			float cp = MathF.Cos(p.Pitch);
			Vec3 fwd = new Vec3(-MathF.Sin(p.Yaw) * cp, MathF.Sin(p.Pitch), -MathF.Cos(p.Yaw) * cp);

			if (target >= WindowState.TargetBase)
			{
				_world.Raycast(eye, fwd, WindowState.Reach, out _, out int pane, includeBroken: true);
				if (pane >= 0 && target == _world.Windows[pane].ActionTarget(pane))
					SetWindow(pane, _world.Windows[pane].RestoreOrReinforce());
				return;
			}

			int best = -1;
			float bestScore = float.MaxValue;
			if (target != 0)
			{
				// le client a désigné l'objet qu'il voit sous son viseur : on vérifie qu'il existe et qu'il est à portée
				int ti = target - LootIdBase;
				if (ti >= 0 && ti < _lootHigh && _loot[ti].Active)
				{
					bool chestOpen = _loot[ti].Item.ItemId == ItemIds.Chest && _loot[ti].Item.Count > 0;
					if (!chestOpen && Vec3.Distance(_loot[ti].Pos + new Vec3(0, 0.1f, 0), eye) <= PickupRange + PickupSlack) best = ti;
				}
				if (best < 0) return;
			}
			_lootScratch.Clear();
			if (best < 0) _lootGrid.Query(p.State.Pos.X, p.State.Pos.Z, PickupRange, _lootScratch);
			foreach (int id in _lootScratch)
			{
				int idx = id - LootIdBase;
				if (idx < 0 || idx >= _lootHigh || !_loot[idx].Active) continue;
				bool isChest = _loot[idx].Item.ItemId == ItemIds.Chest;
				if (isChest && _loot[idx].Item.Count > 0) continue; // coffre déjà ouvert
				Vec3 to = _loot[idx].Pos + new Vec3(0, isChest ? 0.4f : 0.1f, 0) - eye;
				float dist = to.Length();
				if (dist > PickupRange || dist < 1e-4f) continue;
				float cos = fwd.Dot(to / dist);
				if (cos < PickupCone) continue;
				// on prend l'objet le plus proche de la ligne de visée (distance en mètres entre l'objet et le rayon), pas celui qui
				// a le meilleur angle : dans un coffre les objets sont à 30 cm les uns des autres, l'angle seul se trompe de voisin
				float along = fwd.Dot(to);
				float off2 = to.LengthSquared() - along * along;
				float score = MathF.Sqrt(MathF.Max(0f, off2)) + dist * 0.01f;
				if (score < bestScore) { bestScore = score; best = idx; }
			}
			if (best < 0) return;

			if (Arena) { TryArenaInteract(p, best); return; }      // arène : seules les armes se prennent à la main
			if (_loot[best].Item.ItemId == ItemIds.Chest) { OpenChest(best); return; }

			var item = _loot[best].Item;
			_dropScratch.Clear();
			if (!p.Inventory.TryPickup(ref item, _dropScratch)) return;
			if (ItemDefs.Get(item.ItemId).Kind == ItemKind.Shield) p.Shield = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);   // un bouclier ramassé est plein

			if (item.Count > 0 && ItemDefs.Get(item.ItemId).Kind == ItemKind.Ammo)
				_loot[best].Item = item; // il en reste au sol
			else
			{
				_loot[best].Active = false;
				_freeLoot.Push(best);
			}
			_lootDirty = true;
			p.Reloading = false;
			foreach (var dropped in _dropScratch) DropLootAround(dropped, p.State.Pos);
			SendInventory(p);
		}

		/// <summary>Emplacements à l'intérieur d'un coffre (décalage par rapport à son centre, en mètres ; le coffre fait 1,5 m de long). Un coffre ne contient jamais plus d'objets que d'emplacements.</summary>
		private static readonly Vec3[] ChestSlots =
		{
			new Vec3(-0.4f, 0.42f, -0.2f), new Vec3(-0.4f, 0.42f, 0.2f), new Vec3(0.4f, 0.42f, -0.2f), new Vec3(0.4f, 0.42f, 0.2f)
		};

		private readonly byte[] _chestRoll = new byte[4];

		/// <summary>
		/// Ouvre un coffre : les clients jouent l'animation, et ses objets (au plus un par emplacement, une arme au moins) apparaissent
		/// à l'intérieur. Ils ne sortent pas : on les prend directement dans le coffre.
		/// </summary>
		private void OpenChest(int index)
		{
			var chestPos = _loot[index].Pos;
			byte chestQuality = _loot[index].Quality;
			_loot[index].Item.Count = 1;
			// le contenu dépend du hasard (1 ou 2 armes, accessoire ou non) et de la place : jamais plus d'objets que d'emplacements
			int sp = _loot[index].SpawnPlusOne;
			float chestYaw = sp > 0 ? _data.LootSpawns[sp - 1].Yaw : 0f;
			int n;
			if (sp > 0 && _data.LootSpawns[sp - 1].HasPool)
			{
				// liste précise réglée dans l'éditeur : les 4 objets sont tirés dans la liste, selon les poids
				n = ChestSlots.Length;
				for (int i = 0; i < n; i++) _chestRoll[i] = _data.LootSpawns[sp - 1].PickFromPool(_rng);
			}
			else n = Math.Min(LootTable.RollChest(_rng, _chestRoll), ChestSlots.Length);
			for (int i = 0; i < n; i++)
			{
				if (_chestRoll[i] == ItemIds.None) continue;
				var o = WorldData.ChestOffset(ChestSlots[i], chestYaw);
				if (!AddLoot(BoostAmmo(LootItem.Rolled(_chestRoll[i], _rng, chestQuality)), new Vec3(chestPos.X + o.X, chestPos.Y + o.Y, chestPos.Z + o.Z))) break;
			}
			_lootDirty = true;
		}

		// ------------------------------------------------------------------ soins

		/// <summary>Commence à utiliser un soin du sac : seulement s'il est utile (rien à soigner = refusé) ; il n'est consommé qu'à la fin.</summary>
		private void StartUse(ServerPlayer p, byte itemId)
		{
			if (!Consumables.Is(itemId) || p.Inventory.CountOf(itemId) <= 0) return;
			if (p.UseId == itemId) return;
			if (!Consumables.Useful(itemId, p.Health, p.Shield, p.Inventory.ShieldTier)) return;
			var e = Consumables.Of(itemId);
			p.UseId = itemId;
			p.UseTotal = MathF.Max(0.1f, e.Seconds);
			p.UseLeft = p.UseTotal;
			p.Reloading = false;
		}

		private void TickUse(ServerPlayer p, float dt)
		{
			p.UseLeft -= dt;
			if (p.UseLeft > 0f) return;
			byte id = p.UseId;
			p.UseId = 0;
			if (!p.Inventory.RemoveOne(id)) return;
			var e = Consumables.Of(id);
			if (e.Heal > 0f) p.Health = MathF.Min(100f, p.Health + (e.Heal >= ConsumableEffect.Full ? 100f : e.Heal));
			float max = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);
			if (e.Shield > 0f && max > 0f) p.Shield = MathF.Min(max, p.Shield + (e.Shield >= ConsumableEffect.Full ? max : e.Shield));
			SendInventory(p);
		}

		private void HandleCommand(ServerPlayer p)
		{
			var type = (CommandType)_reader.ReadByte();
			int arg = _reader.ReadByte();
			if (_reader.Overflow) return;
			if (type == CommandType.SelectClass || type == CommandType.Buy || type == CommandType.DuelPick) { HandleModeCommand(p, type, arg); return; }
			if (!p.Alive) return;
			if (type == CommandType.BoardVehicle) { ToggleVehicle(p, arg); return; }
			if (p.Ride != null) return;

			// arène : on ne jette rien et les viseurs restent sur leur arme
			if (Arena && type is CommandType.DropWeapon or CommandType.DropItem or CommandType.DropShield or CommandType.EquipOptic or CommandType.EquipOpticOn or CommandType.DropOptic or CommandType.UnequipOptic) return;
			bool changed = false;
			switch (type)
			{
				case CommandType.DropWeapon:
					if (p.Inventory.DropWeapon(arg, out var w)) { DropLootAround(w, p.State.Pos); p.Reloading = false; changed = true; }
					break;
				case CommandType.DropBomb: if (Modal) _mode.OnDropBomb(p); break;
				case CommandType.EquipBomb: if (Modal) _mode.OnEquipBomb(p, arg != 0); break;
				case CommandType.EquipGrenade: EquipGrenade(p, (byte)arg); break;
				case CommandType.DropItem:
					if (Modal && arg >= 0 && arg < p.Inventory.Items.Count && p.Inventory.Items[arg].Id == ItemIds.Bomb) { _mode.OnDropBomb(p); break; }     // la bombe se lâche par le mode, pas comme un objet au sol
					if (p.Inventory.DropItem(arg, out var it)) { DropLootAround(it, p.State.Pos); changed = true; }
					break;
				case CommandType.DropShield:
					if (p.Inventory.DropShield(out var sh)) { DropLootAround(sh, p.State.Pos); p.Shield = 0f; changed = true; }
					break;
				case CommandType.UseItem: StartUse(p, (byte)arg); break;
				case CommandType.ThrowGrenade: ThrowGrenade(p, (byte)arg); break;
				case CommandType.CancelUse: p.UseId = 0; break;
				case CommandType.EquipOptic: changed = p.Inventory.EquipOptic(arg); break;
				case CommandType.EquipOpticOn: changed = p.Inventory.EquipOpticOn(arg >> 4, arg & 15); break;
				case CommandType.DropOptic: if (p.Inventory.TakeOptic(arg, out var optic)) { DropLootAround(optic, p.State.Pos); changed = true; } break;
				case CommandType.UnequipOptic: changed = p.Inventory.UnequipOptic(arg); break;
			}
			if (changed) SendInventory(p);
		}
	}
}
