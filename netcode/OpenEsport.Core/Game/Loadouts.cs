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
		/// <summary>Grenades emportées (une entrée par grenade : ItemIds.GrenadeFrag…).</summary>
		public byte[] Grenades;
	}

	public static class Loadouts
	{
		public static readonly ClassKit[] Kits =
		{
			new ClassKit { Id = 0, Name = "Fusilier", Description = "AK-74 avec viseur holographique x2, pistolet C19. Polyvalent à toutes les distances.", Primary = ItemIds.Ak, Secondary = ItemIds.C19, PrimaryOptic = ItemIds.Optic2x, ShieldTier = 1, HealSmall = 1, ShieldSmall = 1, Grenades = new[] { ItemIds.GrenadeFrag, ItemIds.GrenadeFrag } },
			new ClassKit { Id = 1, Name = "Éclaireur", Description = "SMG-45 avec viseur point rouge, pistolet C19. Rapide et mortel de près, plus de soins.", Primary = ItemIds.Smg45, Secondary = ItemIds.C19, PrimaryOptic = ItemIds.OpticRedDot, ShieldTier = 0, HealSmall = 2, ShieldSmall = 1, Grenades = new[] { ItemIds.GrenadeFlash, ItemIds.GrenadeFlash, ItemIds.GrenadeSticky } },
			new ClassKit { Id = 2, Name = "Appui", Description = "LMG-63, pistolet C19. Beaucoup de balles et un bouclier plus solide, mais moins de chargeurs de réserve.", Primary = ItemIds.Lmg63, Secondary = ItemIds.C19, ReserveMagazines = 2, ShieldTier = 2, HealSmall = 1, Grenades = new[] { ItemIds.GrenadeFrag, ItemIds.GrenadeFire } },
			new ClassKit { Id = 3, Name = "Briseur", Description = "Fusil à canon scié, pistolet C19. Dévastateur au corps à corps, vulnérable de loin.", Primary = ItemIds.Sawnoff, Secondary = ItemIds.C19, ShieldTier = 1, HealSmall = 2, Grenades = new[] { ItemIds.GrenadeFrag, ItemIds.GrenadeFlash } },
			new ClassKit { Id = 4, Name = "Tireur d'élite", Description = "Sniper à verrou avec lunette 4x, pistolet C19. Un tir précis peut suffire, mais il faut le placer.", Primary = ItemIds.Sniper, Secondary = ItemIds.C19, PrimaryOptic = ItemIds.Optic4x, ReserveMagazines = 4, ShieldTier = 0, HealSmall = 2, ShieldSmall = 1, Grenades = new[] { ItemIds.GrenadeFlash, ItemIds.GrenadeSticky } },
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
		/// <summary>Munitions : nombre de balles par achat.</summary>
		public int Quantity = 1;
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
			new ShopItem { Code = 20, Name = "Viseur holographique", Price = 350, ItemId = ItemIds.OpticHolo },
			new ShopItem { Code = 14, Name = "Viseur holographique x2", Price = 400, ItemId = ItemIds.Optic2x },
			new ShopItem { Code = 12, Name = "Viseur 4x", Price = 500, ItemId = ItemIds.Optic4x },
			new ShopItem { Code = 21, Name = "Viseur 6x", Price = 700, ItemId = ItemIds.Optic6x },
			new ShopItem { Code = 15, Name = "Fusil de précision (sniper)", Price = 4750, ItemId = ItemIds.Sniper },
			new ShopItem { Code = 16, Name = "Grenade à fragmentation", Price = 300, ItemId = ItemIds.GrenadeFrag },
			new ShopItem { Code = 17, Name = "Grenade flash", Price = 200, ItemId = ItemIds.GrenadeFlash },
			new ShopItem { Code = 18, Name = "Grenade collante", Price = 350, ItemId = ItemIds.GrenadeSticky },
			new ShopItem { Code = 19, Name = "Grenade incendiaire", Price = 400, ItemId = ItemIds.GrenadeFire },
			new ShopItem { Code = 22, Name = "Munitions légères (x30)", Price = 100, ItemId = ItemIds.Ammo9mm, Quantity = 30 },
			new ShopItem { Code = 23, Name = "Munitions lourdes (x30)", Price = 150, ItemId = ItemIds.AmmoRifle, Quantity = 30 },
			new ShopItem { Code = 24, Name = "Cartouches (x8)", Price = 100, ItemId = ItemIds.AmmoShell, Quantity = 8 },
			new ShopItem { Code = 25, Name = "Munitions de sniper (x8)", Price = 150, ItemId = ItemIds.AmmoSniper, Quantity = 8 },
			new ShopItem { Code = 13, Name = "Kit de désamorçage", Price = 400, DefuseKit = true, Side = 0 },
		};

		public static ShopItem Get(int code) { foreach (var i in Items) if (i.Code == code) return i; return null; }
	}
}
