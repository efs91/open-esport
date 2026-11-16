using System;

namespace OpenEsport.Core
{
	/// <summary>Niveau d'équipement : blanc (classique), bleu (rare), violet (épique), rouge (légendaire).</summary>
	public enum ItemTier : byte { Classic = 0, Rare = 1, Epic = 2, Legendary = 3 }

	/// <summary>
	/// Règles des niveaux : probabilité de tomber sur chaque niveau, et effet sur les armes (dégâts, chargeur, rechargement, dispersion).
	/// Toutes les valeurs sont réglables dans balance.json (clés « tier.* »). Les sacs à dos ont un niveau fixe : le premier est rare, le second épique.
	/// </summary>
	public static class Tiers
	{
		public const int Count = 4;
		public static readonly string[] Keys = { "classic", "rare", "epic", "legendary" };
		public static readonly string[] FrenchNames = { "Classique", "Rare", "Épique", "Légendaire" };

		public static string Name(byte tier) => FrenchNames[Math.Min(tier, (byte)(Count - 1))];

		/// <summary>Niveau effectif d'un objet : fixe pour les sacs à dos, celui de l'instance pour les armes, classique pour le reste.</summary>
		public static byte Of(byte itemId, byte tier)
		{
			var d = ItemDefs.Get(itemId);
			if (d == null) return 0;
			if (d.Kind == ItemKind.Backpack) return (byte)(d.BackpackLevel >= 3 ? ItemTier.Epic : ItemTier.Rare);
			return d.Kind == ItemKind.Weapon || d.Kind == ItemKind.Shield ? (byte)Math.Min(tier, (byte)(Count - 1)) : (byte)0;
		}

		/// <summary>
		/// Niveau tiré pour un point de butin réglé dans l'éditeur : 0 = tirage normal ; 1 à 4 = le niveau (classique, rare, épique, légendaire)
		/// avec la probabilité loot.qualityMajority, sinon tirage normal ; 5 à 8 = toujours ce niveau.
		/// </summary>
		public static byte Roll(Random rng, byte quality)
		{
			if (quality >= 5 && quality <= 8) return (byte)(quality - 5);
			if (quality >= 1 && quality <= 4 && rng.NextDouble() < GameBalance.QualityMajority) return (byte)(quality - 1);
			return Roll(rng);
		}

		/// <summary>Tire un niveau au hasard selon les probabilités réglées (tier.chance.*).</summary>
		public static byte Roll(Random rng)
		{
			double total = 0;
			for (int i = 0; i < Count; i++) total += GameBalance.TierChance[i];
			if (total <= 0) return 0;
			double r = rng.NextDouble() * total;
			for (int i = 0; i < Count; i++)
			{
				r -= GameBalance.TierChance[i];
				if (r < 0) return (byte)i;
			}
			return 0;
		}

		public static float Damage(byte tier) => GameBalance.TierDamage[Math.Min(tier, (byte)(Count - 1))];
		public static float Reload(byte tier) => GameBalance.TierReload[Math.Min(tier, (byte)(Count - 1))];
		public static float Spread(byte tier) => GameBalance.TierSpread[Math.Min(tier, (byte)(Count - 1))];

		/// <summary>Taille du chargeur d'une arme de ce niveau.</summary>
		public static int Magazine(WeaponDef def, byte tier)
			=> def.MagazineSize <= 0 ? 0 : Math.Max(1, (int)Math.Round(def.MagazineSize * GameBalance.TierMagazine[Math.Min(tier, (byte)(Count - 1))]));
	}
}
