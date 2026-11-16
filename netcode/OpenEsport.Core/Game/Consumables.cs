using System;

namespace OpenEsport.Core
{
	/// <summary>Effet d'un soin : vie et bouclier rendus (<see cref="Full"/> = tout remplir) et durée d'utilisation.</summary>
	public readonly struct ConsumableEffect
	{
		public readonly float Heal, Shield, Seconds;
		public const float Full = 999f;
		public ConsumableEffect(float heal, float shield, float seconds) { Heal = heal; Shield = shield; Seconds = seconds; }
	}

	public static class Consumables
	{
		public static bool Is(byte itemId) => itemId >= ItemIds.HealSmall && itemId <= ItemIds.BothBig;

		public static ConsumableEffect Of(byte itemId)
		{
			int i = itemId - ItemIds.HealSmall;
			if (i < 0 || i >= 6) return default;
			return new ConsumableEffect(GameBalance.ConsumableHeal[i], GameBalance.ConsumableShield[i], GameBalance.ConsumableSeconds[i]);
		}

		/// <summary>Points de bouclier maximum d'un niveau (−1 : pas de bouclier équipé).</summary>
		public static float ShieldMaxOf(int shieldTier) => shieldTier < 0 ? 0f : GameBalance.ShieldMax[Math.Min(shieldTier, Tiers.Count - 1)];

		/// <summary>Un soin servirait-il à ce joueur ? (pas de gaspillage quand tout est déjà plein)</summary>
		public static bool Useful(byte itemId, float health, float shield, int shieldTier)
		{
			var e = Of(itemId);
			bool heals = e.Heal > 0f && health < 100f - 0.01f;
			bool shields = e.Shield > 0f && shieldTier >= 0 && shield < ShieldMaxOf(shieldTier) - 0.01f;
			return heals || shields;
		}
	}
}
