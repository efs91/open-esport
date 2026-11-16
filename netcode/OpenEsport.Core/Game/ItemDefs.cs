using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	public enum ItemKind : byte { None = 0, Weapon = 1, Optic = 2, Backpack = 3, Ammo = 4, Chest = 5, Shield = 6, Consumable = 7 }

	/// <summary>Identifiants des objets du jeu. Ils sont envoyés tels quels sur le réseau.</summary>
	public static class ItemIds
	{
		public const byte None = 0;
		public const byte Knife = 1, C19 = 2, Smg45 = 3, Ak = 4, Lmg63 = 5, Sawnoff = 6;
		public const byte OpticHolo = 10, Optic4x = 11, Optic6x = 12, OpticRedDot = 13;
		public const byte Backpack2 = 20, Backpack3 = 21;
		public const byte Ammo9mm = 30, AmmoRifle = 31, AmmoShell = 32;
		/// <summary>Coffre de butin : réplique comme un objet au sol (Count = 0 fermé, 1 ouvert) mais ne se ramasse pas, il s'ouvre.</summary>
		public const byte Chest = 40;
		/// <summary>Bouclier : un seul objet, son niveau (blanc, bleu, violet, rouge) donne 50 / 75 / 100 / 125 points.</summary>
		public const byte Shield = 50;
		/// <summary>Soins : petit/grand soin de vie, petit/grand bouclier, petit/grand « bouclier + vie ».</summary>
		public const byte HealSmall = 60, HealBig = 61, ShieldSmall = 62, ShieldBig = 63, BothSmall = 64, BothBig = 65;
	}

	public sealed class ItemDef
	{
		public byte Id;
		/// <summary>Identifiant utilisé par le client Godot (ItemDatabase).</summary>
		public string Name;
		public ItemKind Kind;
		public int StackSize = 1;
		public int PickupAmount = 1;
		/// <summary>Munitions : 1 = 9mm, 2 = fusil, 3 = cartouches (métadonnée « ammo_type » des armes).</summary>
		public byte AmmoType;
		public int BackpackLevel;
		public bool AcceptsOptic;
	}

	public static class ItemDefs
	{
		private static readonly ItemDef[] _byId = new ItemDef[256];
		private static readonly Dictionary<string, ItemDef> _byName = new Dictionary<string, ItemDef>();
		public static readonly List<ItemDef> All = new List<ItemDef>();

		static ItemDefs()
		{
			Add(new ItemDef { Id = ItemIds.Knife, Name = "knife", Kind = ItemKind.Weapon });
			Add(new ItemDef { Id = ItemIds.C19, Name = "c19", Kind = ItemKind.Weapon, AcceptsOptic = true });
			Add(new ItemDef { Id = ItemIds.Smg45, Name = "smg45", Kind = ItemKind.Weapon, AcceptsOptic = true });
			Add(new ItemDef { Id = ItemIds.Ak, Name = "ak", Kind = ItemKind.Weapon, AcceptsOptic = true });
			Add(new ItemDef { Id = ItemIds.Lmg63, Name = "lmg63", Kind = ItemKind.Weapon, AcceptsOptic = true });
			Add(new ItemDef { Id = ItemIds.Sawnoff, Name = "sawnoff", Kind = ItemKind.Weapon, AcceptsOptic = true });
			Add(new ItemDef { Id = ItemIds.OpticHolo, Name = "optic_holo", Kind = ItemKind.Optic });
			Add(new ItemDef { Id = ItemIds.Optic4x, Name = "optic_4x", Kind = ItemKind.Optic });
			Add(new ItemDef { Id = ItemIds.Optic6x, Name = "optic_6x", Kind = ItemKind.Optic });
			Add(new ItemDef { Id = ItemIds.OpticRedDot, Name = "optic_reddot", Kind = ItemKind.Optic });
			Add(new ItemDef { Id = ItemIds.Backpack2, Name = "backpack_2", Kind = ItemKind.Backpack, BackpackLevel = 2 });
			Add(new ItemDef { Id = ItemIds.Backpack3, Name = "backpack_3", Kind = ItemKind.Backpack, BackpackLevel = 3 });
			Add(new ItemDef { Id = ItemIds.Chest, Name = "chest", Kind = ItemKind.Chest });
			Add(new ItemDef { Id = ItemIds.Shield, Name = "shield", Kind = ItemKind.Shield });
			Add(new ItemDef { Id = ItemIds.HealSmall, Name = "heal_small", Kind = ItemKind.Consumable, StackSize = 4 });
			Add(new ItemDef { Id = ItemIds.HealBig, Name = "heal_big", Kind = ItemKind.Consumable, StackSize = 2 });
			Add(new ItemDef { Id = ItemIds.ShieldSmall, Name = "shield_small", Kind = ItemKind.Consumable, StackSize = 4 });
			Add(new ItemDef { Id = ItemIds.ShieldBig, Name = "shield_big", Kind = ItemKind.Consumable, StackSize = 2 });
			Add(new ItemDef { Id = ItemIds.BothSmall, Name = "both_small", Kind = ItemKind.Consumable, StackSize = 2 });
			Add(new ItemDef { Id = ItemIds.BothBig, Name = "both_big", Kind = ItemKind.Consumable, StackSize = 1 });
			Add(new ItemDef { Id = ItemIds.Ammo9mm, Name = "ammo_9mm", Kind = ItemKind.Ammo, AmmoType = 1, StackSize = 60, PickupAmount = 20 });
			Add(new ItemDef { Id = ItemIds.AmmoRifle, Name = "ammo_rifle", Kind = ItemKind.Ammo, AmmoType = 2, StackSize = 60, PickupAmount = 30 });
			Add(new ItemDef { Id = ItemIds.AmmoShell, Name = "ammo_shell", Kind = ItemKind.Ammo, AmmoType = 3, StackSize = 16, PickupAmount = 8 });
		}

		private static void Add(ItemDef d)
		{
			_byId[d.Id] = d;
			_byName[d.Name] = d;
			All.Add(d);
		}

		public static ItemDef Get(byte id) => _byId[id];
		public static ItemDef ByName(string name) => name != null && _byName.TryGetValue(name, out var d) ? d : null;

		public static byte AmmoItemFor(byte ammoType)
		{
			foreach (var d in All)
				if (d.Kind == ItemKind.Ammo && d.AmmoType == ammoType) return d.Id;
			return ItemIds.None;
		}

		/// <summary>8 emplacements de base, 10 avec un sac niveau 2, 12 avec un sac niveau 3.</summary>
		public static int CapacityForBackpack(int level) => 8 + 2 * (Math.Clamp(level, 1, 3) - 1);
	}

	public static class LootTable
	{
		/// <summary>Contenu d'un coffre : probabilité d'avoir une seconde arme (sinon une seule), et d'avoir un accessoire.</summary>
		public static double ChestSecondWeaponChance => GameBalance.ChestSecondWeaponChance;
		public static double ChestAccessoryChance => GameBalance.ChestAccessoryChance;

		/// <summary>
		/// Contenu d'un coffre (4 objets au plus) : une arme (80 % du temps ; 20 % du temps deux), un accessoire 70 % du temps
		/// (viseur ou sac à dos), et les munitions des armes. Retourne le nombre d'objets écrits dans <paramref name="into"/>.
		/// Si tout ne rentre pas, les munitions passent en dernier.
		/// </summary>
		public static int RollChest(Random rng, byte[] into)
		{
			int cap = Math.Min(4, into.Length);
			int n = 0;
			byte first = (byte)(ItemIds.C19 + rng.Next(5));
			into[n++] = first;
			byte second = ItemIds.None;
			if (rng.NextDouble() < ChestSecondWeaponChance)
			{
				second = (byte)(ItemIds.C19 + rng.Next(5));
				into[n++] = second;
			}
			if (n < cap && rng.NextDouble() < ChestAccessoryChance)
				into[n++] = rng.Next(4) == 0 ? (rng.Next(2) == 0 ? ItemIds.Backpack3 : ItemIds.Backpack2) : (byte)(ItemIds.OpticHolo + rng.Next(4));
			// fournitures : un bouclier ou un soin, plus ou moins rares (loot.weight.*)
			if (n < cap && rng.NextDouble() < GameBalance.ChestSupplyChance)
				into[n++] = RollSupply(rng);

			// munitions : une boîte par type d'arme
			byte a1 = ItemDefs.AmmoItemFor(WeaponDefs.Get(first).AmmoType);
			byte a2 = second == ItemIds.None ? ItemIds.None : ItemDefs.AmmoItemFor(WeaponDefs.Get(second).AmmoType);
			if (a1 != ItemIds.None && n < cap) into[n++] = a1;
			if (a2 != ItemIds.None && a2 != a1 && n < cap) into[n++] = a2;
			// un coffre contient toujours 4 objets : on complète avec des munitions de l'arme (si elle en a) puis des fournitures
			while (n < cap)
				into[n++] = (a1 != ItemIds.None && rng.Next(2) == 0) ? a1 : RollSupply(rng);
			return n;
		}

		/// <summary>Un bouclier ou un soin, tiré selon les poids réglés (loot.weight.*) : petits soins fréquents, grands soins rares.</summary>
		public static byte RollSupply(Random rng)
		{
			double total = 0;
			for (int i = 0; i < GameBalance.SupplyWeights.Length; i++) total += GameBalance.SupplyWeights[i];
			double r = rng.NextDouble() * total;
			for (int i = 0; i < GameBalance.SupplyWeights.Length; i++)
			{
				r -= GameBalance.SupplyWeights[i];
				if (r < 0) return i == 0 ? ItemIds.Shield : (byte)(ItemIds.HealSmall + i - 1);
			}
			return ItemIds.HealSmall;
		}

		public static byte Roll(Random rng)
		{
			int r = rng.Next(100);
			if (r < 12) return (byte)(ItemIds.C19 + rng.Next(5));      // armes
			if (r < 52) return (byte)(ItemIds.Ammo9mm + rng.Next(3));  // munitions (beaucoup : on en trouve même sans arme)
			if (r < 62) return (byte)(ItemIds.OpticHolo + rng.Next(4)); // viseurs
			if (r < 66) return rng.Next(3) == 0 ? ItemIds.Backpack3 : ItemIds.Backpack2;
			if (r < 88) return RollSupply(rng);                         // boucliers (niveau tiré à la création) et soins
			return (byte)(ItemIds.Ammo9mm + rng.Next(3));
		}
	}
}
