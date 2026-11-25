using System;
using System.Collections.Generic;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Butin d'arène du Deathmatch (voir <see cref="ArenaRules"/>) : chaque point de butin de la carte est un emplacement fixe.
	/// Une arme y est posée une fois ; prise, elle revient quand son porteur meurt (ou la remplace par une autre). Soins, boucliers et munitions
	/// se prennent en marchant dessus, agissent tout de suite et reviennent après un délai. Le serveur seul décide : le client voit des objets au sol ordinaires.
	/// </summary>
	public sealed partial class GameServer
	{
		private sealed class ArenaSpot
		{
			public LootItem Item;
			public Vec3 Pos;
			/// <summary>Indice de l'objet au sol (−1 : pris).</summary>
			public int Loot = -1;
			/// <summary>Arme : celui qui la porte.</summary>
			public ServerPlayer Holder;
			/// <summary>Tick où l'objet revient (0 : pas de retour prévu).</summary>
			public uint ReturnTick;
			public bool IsWeapon => ItemDefs.Get(Item.ItemId)?.Kind == ItemKind.Weapon;
		}

		private readonly List<ArenaSpot> _arena = new List<ArenaSpot>();
		/// <summary>Les deux modes Deathmatch jouent avec les règles d'arène.</summary>
		private bool Arena => Modes.UsesArenaRules(_opt.Mode);
		public int ArenaSpotCount => _arena.Count;

		/// <summary>État d'un emplacement d'arène (tests, diagnostic).</summary>
		public struct ArenaSpotInfo { public byte Item, Tier, Optic; public Vec3 Pos; public bool Present; public int HolderId; public float ReturnIn; }

		/// <summary>Les emplacements d'arène et leur état. À appeler depuis le thread du serveur.</summary>
		public List<ArenaSpotInfo> ArenaSpots()
		{
			var list = new List<ArenaSpotInfo>();
			foreach (var s in _arena)
				list.Add(new ArenaSpotInfo
				{
					Item = s.Item.ItemId, Tier = s.Item.Tier, Optic = s.Item.Optic, Pos = s.Pos, Present = s.Loot >= 0, HolderId = s.Holder?.Id ?? -1,
					ReturnIn = s.ReturnTick == 0 ? -1f : Math.Max(0f, (s.ReturnTick - (float)_tick) / _m.TickRate),
				});
			return list;
		}

		/// <summary>Le joueur prend l'arme <paramref name="itemId"/> de l'emplacement le plus proche où elle est posée (tests, administration). À appeler depuis le thread du serveur.</summary>
		public bool ArenaGrab(int playerId, byte itemId)
		{
			var p = GetPlayer(playerId);
			if (p == null) return false;
			ArenaSpot best = null;
			foreach (var s in _arena)
				if (s.Loot >= 0 && s.Item.ItemId == itemId && s.IsWeapon && (best == null || Vec3.DistanceSquared(s.Pos, p.State.Pos) < Vec3.DistanceSquared(best.Pos, p.State.Pos))) best = s;
			if (best == null) return false;
			ArenaTakeWeapon(p, best);
			return true;
		}

		/// <summary>Pose le butin d'arène : un objet par point de la carte (objet imposé ou premier de sa liste), sans les munitions et soins ajoutés au hasard du battle royale.</summary>
		private void SpawnArenaLoot()
		{
			_arena.Clear();
			foreach (var spawn in _data.LootSpawns)
			{
				if (spawn.IsChest) continue;
				byte id = spawn.ItemId != ItemIds.None ? spawn.ItemId : spawn.HasPool ? spawn.PickFromPool(_rng) : ItemIds.None;
				var def = ItemDefs.Get(id);
				if (def == null) continue;
				var item = LootItem.Rolled(id, _rng, spawn.Quality);
				if (def.Kind == ItemKind.Weapon)
				{
					item.Optic = ArenaRules.OpticFor(id);
					if (spawn.Quality == 0) item.Tier = (byte)ItemTier.Epic;      // une arme d'arène est au moins épique
				}
				else if (spawn.Count > 0) item.Count = spawn.Count;
				else if (def.Kind == ItemKind.Ammo) item = BoostAmmo(item);
				var spot = new ArenaSpot { Item = item, Pos = spawn.Pos };
				_arena.Add(spot);
				PlaceArenaItem(spot);
			}
		}

		private void PlaceArenaItem(ArenaSpot spot)
		{
			spot.Holder = null; spot.ReturnTick = 0;
			spot.Loot = AddLoot(spot.Item, spot.Pos, out int idx) ? idx : -1;
		}

		private void TakeArenaItem(ArenaSpot spot)
		{
			if (spot.Loot >= 0 && spot.Loot < _lootHigh && _loot[spot.Loot].Active)
			{
				_loot[spot.Loot].Active = false;
				_freeLoot.Push(spot.Loot);
				_lootDirty = true;
			}
			spot.Loot = -1;
		}

		private ArenaSpot SpotOfLoot(int lootIndex)
		{
			foreach (var s in _arena) if (s.Loot == lootIndex) return s;
			return null;
		}

		/// <summary>Un tick d'arène : ramassage en marchant, retour des objets, armes dont le porteur est mort.</summary>
		private void TickArena()
		{
			if (Phase != MatchPhase.Playing) return;
			// la vie (pas le bouclier) remonte seule, 5 s après la dernière blessure
			float dt = 1f / _m.TickRate;
			uint delay = (uint)(ArenaRules.RegenDelay * _m.TickRate);
			foreach (var p in _players)
			{
				if (!p.Alive || p.Health >= 100f || _tick - p.LastHurtTick < delay) { p.RegenClock = 0f; continue; }
				p.RegenClock += dt;
				while (p.RegenClock >= ArenaRules.RegenInterval && p.Health < 100f) { p.RegenClock -= ArenaRules.RegenInterval; p.Health = MathF.Min(100f, p.Health + 1f); }
			}
			if (_arena.Count == 0) return;
			float r2 = ArenaRules.WalkPickupRange * ArenaRules.WalkPickupRange;
			foreach (var spot in _arena)
			{
				if (spot.Loot < 0)
				{
					if (spot.IsWeapon && spot.ReturnTick == 0 && !StillHeld(spot)) spot.ReturnTick = _tick + (uint)(ArenaRules.WeaponRespawn * _m.TickRate);
					if (spot.ReturnTick != 0 && _tick >= spot.ReturnTick) PlaceArenaItem(spot);
					continue;
				}
				for (int i = 0; i < _players.Count; i++)
				{
					var p = _players[i];
					if (!p.Alive || p.Downed) continue;
					float dx = p.State.Pos.X - spot.Pos.X, dz = p.State.Pos.Z - spot.Pos.Z, dy = p.State.Pos.Y - spot.Pos.Y;
					if (dx * dx + dz * dz > r2 || dy < -1.2f || dy > 1.4f) continue;
					if (spot.IsWeapon) { if (p.IsBot ? BotWantsWeapon(p, spot.Item) : !Holds(p, spot.Item.ItemId)) { ArenaTakeWeapon(p, spot); break; } continue; }     // une arme se prend en passant dessus (sauf si on l'a déjà)
					if (ArenaConsume(p, spot)) break;
				}
			}
		}

		/// <summary>Le porteur a-t-il toujours l'arme de cet emplacement ? (mort, parti ou arme remplacée : elle revient)</summary>
		private bool StillHeld(ArenaSpot spot)
		{
			var h = spot.Holder;
			if (h == null || !h.Alive || !_players.Contains(h)) return false;
			int have = 0, held = 0;
			foreach (var w in h.Inventory.Weapons) if (w != null && w.WeaponId == spot.Item.ItemId) have++;
			foreach (var s in _arena) if (s.Holder == h && s.Item.ItemId == spot.Item.ItemId && s.Loot < 0) held++;
			// deux emplacements de la même arme tenus pour une seule arme en main : celui-ci est libéré
			if (held > have) { spot.Holder = null; return false; }
			return true;
		}

		/// <summary>Soin, bouclier ou munitions : effet immédiat s'il sert, puis l'objet disparaît jusqu'à son retour.</summary>
		private bool ArenaConsume(ServerPlayer p, ArenaSpot spot)
		{
			var def = ItemDefs.Get(spot.Item.ItemId);
			float delay;
			if (Consumables.Is(spot.Item.ItemId))
			{
				if (!Consumables.Useful(spot.Item.ItemId, p.Health, p.Shield, p.Inventory.ShieldTier)) return false;
				var e = Consumables.Of(spot.Item.ItemId);
				if (e.Heal > 0f) p.Health = MathF.Min(100f, p.Health + (e.Heal >= ConsumableEffect.Full ? 100f : e.Heal));
				float max = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);
				if (e.Shield > 0f && max > 0f) p.Shield = MathF.Min(max, p.Shield + (e.Shield >= ConsumableEffect.Full ? max : e.Shield));
				delay = e.Shield > 0f && e.Heal <= 0f ? ArenaRules.ShieldRespawn : ArenaRules.HealRespawn;
			}
			else if (def.Kind == ItemKind.Shield)
			{
				float max = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);
				if (p.Shield >= max - 0.01f) return false;
				p.Shield = max;
				delay = ArenaRules.ShieldRespawn;
			}
			else if (def.Kind == ItemKind.Ammo)
			{
				var item = spot.Item;
				_dropScratch.Clear();
				if (!p.Inventory.TryPickup(ref item, _dropScratch)) return false;
				delay = ArenaRules.AmmoRespawn;
			}
			else return false;
			TakeArenaItem(spot);
			spot.ReturnTick = _tick + (uint)(delay * _m.TickRate);
			SendInventory(p);
			SendEvent(p, new ServerEvent { Type = EventType.ArenaPickup, A = p.Id, C = spot.Item.ItemId, Pos = spot.Pos });
			return true;
		}

		/// <summary>Prend l'arme d'un emplacement : elle va dans un emplacement libre, sinon remplace celle en main (qui disparaît : son emplacement la récupère).
		/// Viseur fixé d'office, chargeur plein et deux chargeurs de réserve.</summary>
		private void ArenaTakeWeapon(ServerPlayer p, ArenaSpot spot)
		{
			var inv = p.Inventory;
			var wd = WeaponDefs.Get(spot.Item.ItemId);
			int slot = Array.IndexOf(inv.Weapons, null);
			for (int i = 0; i < inv.Weapons.Length; i++) if (inv.Weapons[i] != null && inv.Weapons[i].WeaponId == spot.Item.ItemId) slot = i;     // la même arme : on la remplace
			if (slot < 0) slot = inv.ActiveSlot >= 0 ? inv.ActiveSlot : 0;
			inv.Weapons[slot] = new WeaponSlot { WeaponId = spot.Item.ItemId, Magazine = wd == null ? 0 : Tiers.Magazine(wd, spot.Item.Tier), OpticId = spot.Item.Optic, Tier = spot.Item.Tier };
			inv.ActiveSlot = slot;
			inv.GrenadeInHand = 0;
			if (wd != null && wd.AmmoType != 0 && wd.MagazineSize > 0)
			{
				var ammoId = ItemDefs.AmmoItemFor(wd.AmmoType);
				if (ammoId != ItemIds.None)
				{
					int want = wd.Reserve(ArenaRules.MagazinesWithWeapon) - inv.GetAmmo(wd.AmmoType);
					if (want > 0) { var a = LootItem.Of(ammoId); a.Count = (ushort)want; _dropScratch.Clear(); inv.TryPickup(ref a, _dropScratch); }
				}
			}
			TakeArenaItem(spot);
			spot.Holder = p;
			spot.ReturnTick = 0;
			p.Reloading = false;
			SendInventory(p);
			SendEvent(p, new ServerEvent { Type = EventType.ArenaPickup, A = p.Id, C = spot.Item.ItemId, Pos = spot.Pos });
		}

		private static bool Holds(ServerPlayer p, byte weaponId)
		{
			foreach (var w in p.Inventory.Weapons) if (w != null && w.WeaponId == weaponId) return true;
			return false;
		}

		/// <summary>Ramassage avec la touche d'action (armes seulement : le reste se prend en marchant dessus).</summary>
		private bool TryArenaInteract(ServerPlayer p, int lootIndex)
		{
			var spot = SpotOfLoot(lootIndex);
			if (spot == null || !spot.IsWeapon) return false;
			ArenaTakeWeapon(p, spot);
			return true;
		}

		/// <summary>Un bot prend une arme si elle vaut mieux que la moins bonne des siennes (niveau, puis dégâts par seconde).</summary>
		private static bool BotWantsWeapon(ServerPlayer p, LootItem item)
		{
			float Value(byte id, byte tier)
			{
				var w = WeaponDefs.Get(id);
				return w == null ? 0f : tier * 100f + w.Damage * w.RoundsPerMinute * w.Pellets / 60f;
			}
			float worst = float.MaxValue;
			foreach (var w in p.Inventory.Weapons)
			{
				if (w == null) return true;
				if (w.WeaponId == item.ItemId) return false;
				worst = MathF.Min(worst, Value(w.WeaponId, w.Tier));
			}
			return Value(item.ItemId, item.Tier) > worst;
		}

		/// <summary>Bots du Deathmatch hors combat : l'emplacement le plus intéressant (soin s'il est blessé, bouclier, meilleure arme), sinon rien.</summary>
		private bool ArenaBotGoal(ServerPlayer p, out Vec3 goal)
		{
			goal = default;
			float best = float.MaxValue;
			float shieldMax = Consumables.ShieldMaxOf(p.Inventory.ShieldTier);
			foreach (var s in _arena)
			{
				if (s.Loot < 0) continue;
				var def = ItemDefs.Get(s.Item.ItemId);
				bool wanted = def.Kind == ItemKind.Weapon ? BotWantsWeapon(p, s.Item)
					: Consumables.Is(s.Item.ItemId) ? Consumables.Useful(s.Item.ItemId, p.Health, p.Shield, p.Inventory.ShieldTier)
					: def.Kind == ItemKind.Ammo ? p.Inventory.Active != null && WeaponDefs.Get(p.Inventory.Active.WeaponId)?.AmmoType == def.AmmoType && p.Inventory.GetAmmo(def.AmmoType) < 60
					: def.Kind == ItemKind.Shield && p.Shield < shieldMax;
				if (!wanted) continue;
				float d = Vec3.DistanceSquared(s.Pos, p.State.Pos) + MathF.Abs(s.Pos.Y - p.State.Pos.Y) * 400f;
				if (def.Kind == ItemKind.Weapon) d *= 0.5f;
				if (d < best) { best = d; goal = s.Pos; }
			}
			return best < float.MaxValue;
		}
	}
}
