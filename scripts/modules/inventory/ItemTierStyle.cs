using Openesport.Utils;
using Godot;

namespace Openesport.Inventory
{
	/// <summary>
	/// Couleurs et noms des niveaux d'équipement : blanc (classique), bleu (rare), violet (épique), rouge (légendaire).
	/// Les règles (probabilités, bonus) sont dans le noyau réseau : voir OpenEsport.Core.Tiers et balance.json.
	/// </summary>
	public static class ItemTierStyle
	{
		public static readonly Color[] Colors =
		{
			new Color(0.92f, 0.94f, 0.96f),   // classique : blanc
			new Color(0.28f, 0.58f, 1.00f),   // rare : bleu
			new Color(0.72f, 0.38f, 1.00f),   // épique : violet
			new Color(1.00f, 0.25f, 0.25f),   // légendaire : rouge
		};

		public static Color ColorOf(int tier) => Colors[Mathf.Clamp(tier, 0, Colors.Length - 1)];
		public static string NameOf(int tier) => I18n.T(OpenEsport.Core.Tiers.Name((byte)Mathf.Clamp(tier, 0, 3)));

		/// <summary>Niveau d'un objet : celui de l'arme, fixe pour les sacs à dos (le premier est rare, le second épique), classique sinon.</summary>
		public static int TierOf(ItemDefinition def, int weaponTier)
		{
			if (def == null) return 0;
			if (def.Type == ItemType.Weapon || def.Type == ItemType.Shield) return weaponTier;
			if (def.Type == ItemType.Backpack) return def.BackpackLevel >= 3 ? 2 : 1;
			return 0;
		}

		/// <summary>Image du bouclier d'un niveau (concepts Meshy : blanc, bleu, violet, rouge).</summary>
		public static readonly string[] ShieldIcons =
		{
			"res://assets/textures/Shield/White Shield.png", "res://assets/textures/Shield/Blue Shield.png",
			"res://assets/textures/Shield/Violet Shield.png", "res://assets/textures/Shield/Red Shield.png",
		};

		public static string ShieldIcon(int tier) => ShieldIcons[Mathf.Clamp(tier, 0, 3)];

		/// <summary>Points de bouclier d'un niveau.</summary>
		public static int ShieldPoints(int tier) => tier < 0 ? 0 : (int)OpenEsport.Core.GameBalance.ShieldMax[Mathf.Clamp(tier, 0, 3)];

		/// <summary>Niveau du sac à dos d'un niveau donné (2 = rare, 3 = épique).</summary>
		public static int TierOfBackpackLevel(int level) => level >= 3 ? 2 : level == 2 ? 1 : 0;
	}
}
