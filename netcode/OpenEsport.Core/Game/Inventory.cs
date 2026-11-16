using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>Un objet posé au sol (ou en cours de ramassage). Pour une arme, <see cref="Optic"/> et <see cref="Magazine"/> la suivent.</summary>
	public struct LootItem
	{
		public byte ItemId;
		public ushort Count;
		public byte Optic;
		/// <summary>-1 = chargeur plein.</summary>
		public short Magazine;
		/// <summary>Niveau de l'arme (voir <see cref="ItemTier"/>) : 0 classique, 1 rare, 2 épique, 3 légendaire.</summary>
		public byte Tier;

		/// <summary>Cet objet avec un niveau tiré au hasard s'il s'agit d'une arme (le reste n'a pas de niveau).</summary>
		public static LootItem Rolled(byte itemId, Random rng, byte quality = 0)
		{
			var item = Of(itemId);
			var d = ItemDefs.Get(itemId);
			if (d != null && (d.Kind == ItemKind.Weapon || d.Kind == ItemKind.Shield)) item.Tier = Tiers.Roll(rng, quality);
			return item;
		}

		public static LootItem Of(byte itemId)
		{
			var d = ItemDefs.Get(itemId);
			return new LootItem { ItemId = itemId, Count = (ushort)(d == null ? 1 : d.Kind == ItemKind.Ammo ? d.PickupAmount : d.Kind == ItemKind.Chest ? 0 : 1), Magazine = -1 };
		}
	}

	public sealed class WeaponSlot
	{
		public byte WeaponId;
		public int Magazine;
		public byte OpticId;
		public byte Tier;
	}

	public struct ItemStack
	{
		public byte Id;
		public int Count;
	}

	/// <summary>
	/// Inventaire d'un joueur : 2 emplacements d'arme et un sac à dos de 8, 10 ou 12 emplacements.
	/// Les règles vivent ici, sans moteur, pour que le serveur décide et que le client puisse les afficher.
	/// </summary>
	public sealed class InventoryState
	{
		public const int WeaponSlotCount = 2;

		public readonly WeaponSlot[] Weapons = new WeaponSlot[WeaponSlotCount];
		public int ActiveSlot = -1;
		public int BackpackLevel = 1;
		/// <summary>Niveau du bouclier équipé (0 blanc, 1 bleu, 2 violet, 3 rouge) ; −1 = aucun. Tout joueur démarre avec un bouclier blanc.</summary>
		public int ShieldTier = 0;
		public readonly List<ItemStack> Items = new List<ItemStack>();

		public int Capacity => ItemDefs.CapacityForBackpack(BackpackLevel);
		public WeaponSlot Active => ActiveSlot >= 0 ? Weapons[ActiveSlot] : null;
		public byte ActiveWeaponId => Active?.WeaponId ?? ItemIds.None;

		public int GetAmmo(byte ammoType)
		{
			int total = 0;
			foreach (var s in Items)
			{
				var d = ItemDefs.Get(s.Id);
				if (d != null && d.Kind == ItemKind.Ammo && d.AmmoType == ammoType) total += s.Count;
			}
			return total;
		}

		/// <summary>Retire jusqu'à <paramref name="amount"/> munitions du sac. Retourne la quantité réellement retirée.</summary>
		public int TakeAmmo(byte ammoType, int amount)
		{
			int taken = 0;
			for (int i = Items.Count - 1; i >= 0 && taken < amount; i--)
			{
				var d = ItemDefs.Get(Items[i].Id);
				if (d == null || d.Kind != ItemKind.Ammo || d.AmmoType != ammoType) continue;
				int take = Math.Min(Items[i].Count, amount - taken);
				var st = Items[i];
				st.Count -= take;
				taken += take;
				if (st.Count <= 0) Items.RemoveAt(i); else Items[i] = st;
			}
			return taken;
		}

		/// <summary>
		/// Ramasse un objet. Retourne vrai si quelque chose a été pris ; pour les munitions, <paramref name="item"/>.Count
		/// contient ce qui reste au sol. Les objets remplacés (ancienne arme, ancien sac) sont ajoutés à <paramref name="dropped"/>.
		/// </summary>
		public bool TryPickup(ref LootItem item, List<LootItem> dropped)
		{
			var def = ItemDefs.Get(item.ItemId);
			if (def == null) return false;

			switch (def.Kind)
			{
				case ItemKind.Weapon:
				{
					int slot = Array.IndexOf(Weapons, null);
					byte optic = item.Optic;
					if (slot < 0)
					{
						slot = ActiveSlot >= 0 ? ActiveSlot : 0;
						var old = Weapons[slot];
						byte oldOptic = old.OpticId;
						// on garde son viseur : il passe sur la nouvelle arme (si elle en accepte), et celui de l'arme ramassée tombe au sol
						if (oldOptic != ItemIds.None && def.AcceptsOptic)
						{
							optic = oldOptic;
							oldOptic = ItemIds.None;
							if (item.Optic != ItemIds.None) dropped.Add(LootItem.Of(item.Optic));
						}
						dropped.Add(new LootItem { ItemId = old.WeaponId, Count = 1, Optic = oldOptic, Magazine = (short)old.Magazine, Tier = old.Tier });
					}
					var wd = WeaponDefs.Get(item.ItemId);
					int mag = item.Magazine < 0 ? (wd == null ? 0 : Tiers.Magazine(wd, item.Tier)) : item.Magazine;
					Weapons[slot] = new WeaponSlot { WeaponId = item.ItemId, Magazine = mag, OpticId = optic, Tier = item.Tier };
					ActiveSlot = slot;
					return true;
				}
				case ItemKind.Optic:
				{
					var w = Active;
					if (w != null && ItemDefs.Get(w.WeaponId).AcceptsOptic && w.OpticId == ItemIds.None)
					{
						w.OpticId = item.ItemId;
						return true;
					}
					if (Items.Count >= Capacity) return false;
					Items.Add(new ItemStack { Id = item.ItemId, Count = 1 });
					return true;
				}
				case ItemKind.Backpack:
				{
					if (def.BackpackLevel <= BackpackLevel) return false;
					if (BackpackLevel > 1)
						dropped.Add(LootItem.Of(BackpackItemFor(BackpackLevel)));
					BackpackLevel = def.BackpackLevel;
					return true;
				}
				case ItemKind.Shield:
				{
					// on prend le bouclier au sol et on repose celui qu'on portait
					if (ShieldTier >= 0) dropped.Add(new LootItem { ItemId = ItemIds.Shield, Count = 1, Magazine = -1, Tier = (byte)ShieldTier });
					ShieldTier = Math.Min((int)item.Tier, Tiers.Count - 1);
					return true;
				}
				case ItemKind.Consumable:
				{
					int left = AddAmmo(def, item.Count);
					bool tookSome = left < item.Count;
					item.Count = (ushort)left;
					return tookSome;
				}
				case ItemKind.Ammo:
				{
					int left = AddAmmo(def, item.Count);
					bool tookSome = left < item.Count;
					item.Count = (ushort)left;
					return tookSome;
				}
			}
			return false;
		}

		private static byte BackpackItemFor(int level)
		{
			foreach (var d in ItemDefs.All)
				if (d.Kind == ItemKind.Backpack && d.BackpackLevel == level) return d.Id;
			return ItemIds.None;
		}

		private int AddAmmo(ItemDef def, int amount)
		{
			for (int i = 0; i < Items.Count && amount > 0; i++)
			{
				if (Items[i].Id != def.Id || Items[i].Count >= def.StackSize) continue;
				int add = Math.Min(def.StackSize - Items[i].Count, amount);
				var st = Items[i];
				st.Count += add;
				Items[i] = st;
				amount -= add;
			}
			while (amount > 0 && Items.Count < Capacity)
			{
				int add = Math.Min(def.StackSize, amount);
				Items.Add(new ItemStack { Id = def.Id, Count = add });
				amount -= add;
			}
			return amount;
		}

		public bool SelectSlot(int slot)
		{
			if (slot == -1) { ActiveSlot = -1; return true; }
			if (slot < 0 || slot >= WeaponSlotCount || Weapons[slot] == null) return false;
			ActiveSlot = slot;
			return true;
		}

		public bool DropWeapon(int slot, out LootItem item)
		{
			item = default;
			if (slot < 0 || slot >= WeaponSlotCount || Weapons[slot] == null) return false;
			var w = Weapons[slot];
			item = new LootItem { ItemId = w.WeaponId, Count = 1, Optic = w.OpticId, Magazine = (short)w.Magazine, Tier = w.Tier };
			Weapons[slot] = null;
			if (ActiveSlot == slot) ActiveSlot = -1;
			return true;
		}

		/// <summary>Pose le bouclier équipé par terre.</summary>
		public bool DropShield(out LootItem item)
		{
			item = default;
			if (ShieldTier < 0) return false;
			item = new LootItem { ItemId = ItemIds.Shield, Count = 1, Magazine = -1, Tier = (byte)ShieldTier };
			ShieldTier = -1;
			return true;
		}

		/// <summary>Combien d'exemplaires de cet objet dans le sac.</summary>
		public int CountOf(byte id)
		{
			int total = 0;
			foreach (var s in Items) if (s.Id == id) total += s.Count;
			return total;
		}

		/// <summary>Retire un exemplaire (le dernier de la dernière pile).</summary>
		public bool RemoveOne(byte id)
		{
			for (int i = Items.Count - 1; i >= 0; i--)
			{
				if (Items[i].Id != id) continue;
				var st = Items[i];
				st.Count--;
				if (st.Count <= 0) Items.RemoveAt(i); else Items[i] = st;
				return true;
			}
			return false;
		}

		public bool DropItem(int index, out LootItem item)
		{
			item = default;
			if (index < 0 || index >= Items.Count) return false;
			item = new LootItem { ItemId = Items[index].Id, Count = (ushort)Items[index].Count, Magazine = -1 };
			Items.RemoveAt(index);
			return true;
		}

		/// <summary>Monte sur l'arme en main le viseur rangé dans le sac (échange avec celui déjà monté).</summary>
		public bool EquipOptic(int index)
		{
			var w = Active;
			if (w == null || index < 0 || index >= Items.Count) return false;
			var def = ItemDefs.Get(Items[index].Id);
			if (def == null || def.Kind != ItemKind.Optic || !ItemDefs.Get(w.WeaponId).AcceptsOptic) return false;
			byte previous = w.OpticId;
			w.OpticId = def.Id;
			if (previous != ItemIds.None) Items[index] = new ItemStack { Id = previous, Count = 1 };
			else Items.RemoveAt(index);
			return true;
		}

		public bool UnequipOptic(int slot)
		{
			if (slot < 0 || slot >= WeaponSlotCount || Weapons[slot] == null || Weapons[slot].OpticId == ItemIds.None) return false;
			if (Items.Count >= Capacity) return false;
			Items.Add(new ItemStack { Id = Weapons[slot].OpticId, Count = 1 });
			Weapons[slot].OpticId = ItemIds.None;
			return true;
		}

		/// <summary>Vide l'inventaire (mort du joueur) et retourne tout ce qu'il contenait.</summary>
		public void EmptyInto(List<LootItem> output)
		{
			for (int i = 0; i < WeaponSlotCount; i++)
				if (DropWeapon(i, out var w)) output.Add(w);
			foreach (var s in Items)
				output.Add(new LootItem { ItemId = s.Id, Count = (ushort)s.Count, Magazine = -1 });
			Items.Clear();
			if (DropShield(out var sh)) output.Add(sh);
			if (BackpackLevel > 1) output.Add(LootItem.Of(BackpackItemFor(BackpackLevel)));
			BackpackLevel = 1;
			ActiveSlot = -1;
		}

		public void Write(BitWriter w)
		{
			w.WriteBits((uint)(ActiveSlot + 1), 2);
			w.WriteBits((uint)BackpackLevel, 2);
			w.WriteBits((uint)(ShieldTier + 1), 3);
			for (int i = 0; i < WeaponSlotCount; i++)
			{
				var s = Weapons[i];
				w.WriteBool(s != null);
				if (s == null) continue;
				w.WriteByte(s.WeaponId);
				w.WriteUInt16((ushort)s.Magazine);
				w.WriteByte(s.OpticId);
				w.WriteBits(s.Tier, 2);
			}
			w.WriteBits((uint)Items.Count, 5);
			foreach (var st in Items)
			{
				w.WriteByte(st.Id);
				w.WriteUInt16((ushort)st.Count);
			}
		}

		public static InventoryState Read(BitReader r)
		{
			var inv = new InventoryState();
			inv.ActiveSlot = (int)r.ReadBits(2) - 1;
			inv.BackpackLevel = Math.Clamp((int)r.ReadBits(2), 1, 3);
			inv.ShieldTier = Math.Min((int)r.ReadBits(3), Tiers.Count) - 1;
			for (int i = 0; i < WeaponSlotCount; i++)
			{
				if (!r.ReadBool()) continue;
				inv.Weapons[i] = new WeaponSlot { WeaponId = r.ReadByte(), Magazine = r.ReadUInt16(), OpticId = r.ReadByte(), Tier = (byte)r.ReadBits(2) };
			}
			int n = (int)r.ReadBits(5);
			for (int i = 0; i < n && !r.Overflow; i++)
				inv.Items.Add(new ItemStack { Id = r.ReadByte(), Count = r.ReadUInt16() });
			return inv;
		}
	}
}
