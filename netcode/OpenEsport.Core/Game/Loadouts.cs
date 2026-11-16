using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>Un équipement de départ prédéfini : dans les modes hors battle royale, on choisit sa classe à chaque réapparition (pas de butin au sol).</summary>
	public sealed class ClassKit
	{
		public byte Id;
		public string Name, Description;
		/// <summary>Armes (arme principale puis secondaire), avec viseur éventuel.</summary>
		public byte Primary, Secondary, PrimaryOptic;
		/// <summary>Chargeurs de réserve par arme.</summary>
		public int ReserveMagazines = 3;
		/// <summary>Bouclier : niveau 0 à 3, ou −1 pour aucun.</summary>
		public int ShieldTier;
		/// <summary>Soins emportés : petits soins de vie et petits boucliers.</summary>
		public int HealSmall, ShieldSmall;
	}

	public static class Loadouts
	{
		public static readonly ClassKit[] Kits =
		{
			new ClassKit { Id = 0, Name = "Assaut", Description = "Fusil d'assaut polyvalent, viseur holographique, pistolet.", Primary = ItemIds.Ak, Secondary = ItemIds.C19, PrimaryOptic = ItemIds.OpticHolo, ShieldTier = 1, HealSmall = 1, ShieldSmall = 1 },
			new ClassKit { Id = 1, Name = "Appui", Description = "Mitrailleuse lourde : beaucoup de balles, bouclier plus solide, mais lourde.", Primary = ItemIds.Lmg63, Secondary = ItemIds.C19, ReserveMagazines = 2, ShieldTier = 2, HealSmall = 1 },
			new ClassKit { Id = 2, Name = "Éclaireur", Description = "Pistolet-mitrailleur rapide à courte portée, viseur point rouge.", Primary = ItemIds.Smg45, Secondary = ItemIds.C19, PrimaryOptic = ItemIds.OpticRedDot, ShieldTier = 0, HealSmall = 2, ShieldSmall = 1 },
			new ClassKit { Id = 3, Name = "Fusil à pompe", Description = "Dévastateur de près, vulnérable de loin.", Primary = ItemIds.Sawnoff, Secondary = ItemIds.C19, ShieldTier = 1, HealSmall = 2 },
			new ClassKit { Id = 4, Name = "Tireur", Description = "Fusil d'assaut avec viseur 4x pour les longues distances.", Primary = ItemIds.Ak, Secondary = ItemIds.C19, PrimaryOptic = ItemIds.Optic4x, ReserveMagazines = 2, ShieldTier = 0, HealSmall = 1, ShieldSmall = 2 },
		};

		public static ClassKit Get(int id) => Kits[Math.Clamp(id, 0, Kits.Length - 1)];
	}

	/// <summary>Article de la boutique du mode Sabotage.</summary>
	public sealed class ShopItem
	{
		public byte Code;
		public string Name;
		public int Price;
		/// <summary>Identifiant d'objet acheté (arme, viseur, soin), ou 0 pour un bouclier ou un kit.</summary>
		public byte ItemId;
		public int ShieldTier = -1;
		public bool DefuseKit;
		/// <summary>Réservé à un côté : 0 = défenseurs, 1 = attaquants, 255 = les deux.</summary>
		public byte Side = 255;
	}

	public static class Shop
	{
		public static readonly ShopItem[] Items =
		{
			new ShopItem { Code = 1, Name = "Pistolet C19", Price = 200, ItemId = ItemIds.C19 },
			new ShopItem { Code = 2, Name = "Pistolet-mitrailleur SMG45", Price = 1200, ItemId = ItemIds.Smg45 },
			new ShopItem { Code = 3, Name = "Fusil à pompe", Price = 1100, ItemId = ItemIds.Sawnoff },
			new ShopItem { Code = 4, Name = "Fusil d'assaut AK", Price = 2700, ItemId = ItemIds.Ak },
			new ShopItem { Code = 5, Name = "Mitrailleuse lourde LMG", Price = 5000, ItemId = ItemIds.Lmg63 },
			new ShopItem { Code = 6, Name = "Bouclier blanc", Price = 650, ShieldTier = 0 },
			new ShopItem { Code = 7, Name = "Bouclier bleu", Price = 1000, ShieldTier = 1 },
			new ShopItem { Code = 8, Name = "Bouclier violet", Price = 1600, ShieldTier = 2 },
			new ShopItem { Code = 9, Name = "Petit soin", Price = 100, ItemId = ItemIds.HealSmall },
			new ShopItem { Code = 10, Name = "Petit bouclier (recharge)", Price = 200, ItemId = ItemIds.ShieldSmall },
			new ShopItem { Code = 11, Name = "Viseur point rouge", Price = 300, ItemId = ItemIds.OpticRedDot },
			new ShopItem { Code = 12, Name = "Viseur 4x", Price = 500, ItemId = ItemIds.Optic4x },
			new ShopItem { Code = 13, Name = "Kit de désamorçage", Price = 400, DefuseKit = true, Side = 0 },
		};

		public static ShopItem Get(int code) { foreach (var i in Items) if (i.Code == code) return i; return null; }
	}
}
