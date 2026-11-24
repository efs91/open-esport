using System;
using System.Linq;

namespace OpenEsport.Core
{
	/// <summary>Comment l'équipement du 1 VS 1 est décidé.</summary>
	public enum DuelPolicy : byte
	{
		/// <summary>Tiré au hasard à chaque manche, le même pour les deux joueurs (partie normale).</summary>
		Random = 0,
		/// <summary>Chaque joueur choisit le sien au début de la partie et le garde pour toutes les manches.</summary>
		Choice = 1,
		/// <summary>Imposé pour toute la partie (réglé au départ, par exemple par l'organisateur d'un tournoi).</summary>
		Fixed = 2,
	}

	/// <summary>Étape d'une partie de 1 VS 1.</summary>
	public enum DuelStage : byte { Pick = 0, Prepare = 1, Round = 2, RoundEnd = 3 }

	/// <summary>
	/// Équipement d'un joueur en 1 VS 1 : deux armes avec chacune son viseur, une grenade, un soin. Chaque champ est un numéro dans la liste correspondante
	/// (<see cref="DuelRules.Weapons"/>…), ce qui tient en six octets sur le réseau. Les munitions sont infinies.
	/// </summary>
	public struct DuelLoadout : IEquatable<DuelLoadout>
	{
		public byte Weapon, Optic, Weapon2, Optic2, Grenade, Heal;

		public DuelLoadout(int weapon, int optic, int weapon2, int optic2, int grenade, int heal)
		{
			Weapon = (byte)Math.Clamp(weapon, 0, DuelRules.Weapons.Length - 1);
			Optic = (byte)Math.Clamp(optic, 0, DuelRules.Optics.Length - 1);
			Weapon2 = (byte)Math.Clamp(weapon2, 0, DuelRules.Weapons.Length - 1);
			Optic2 = (byte)Math.Clamp(optic2, 0, DuelRules.Optics.Length - 1);
			Grenade = (byte)Math.Clamp(grenade, 0, DuelRules.Grenades.Length - 1);
			Heal = (byte)Math.Clamp(heal, 0, DuelRules.Heals.Length - 1);
		}

		public long Pack() => Weapon | (long)Optic << 8 | (long)Weapon2 << 16 | (long)Optic2 << 24 | (long)Grenade << 32 | (long)Heal << 40;
		/// <summary>Relit les six octets tels quels (Weapon = 255 : équipement inconnu, voir <see cref="Known"/>).</summary>
		public static DuelLoadout Unpack(long v) => new DuelLoadout
		{
			Weapon = (byte)(v & 0xFF), Optic = (byte)((v >> 8) & 0xFF), Weapon2 = (byte)((v >> 16) & 0xFF), Optic2 = (byte)((v >> 24) & 0xFF), Grenade = (byte)((v >> 32) & 0xFF), Heal = (byte)((v >> 40) & 0xFF),
		};
		public bool Known => Weapon < DuelRules.Weapons.Length && Optic < DuelRules.Optics.Length && Weapon2 < DuelRules.Weapons.Length && Optic2 < DuelRules.Optics.Length
			&& Grenade < DuelRules.Grenades.Length && Heal < DuelRules.Heals.Length;

		public DuelChoice WeaponChoice => DuelRules.Weapons[Weapon];
		public DuelChoice OpticChoice => DuelRules.Optics[Optic];
		public DuelChoice Weapon2Choice => DuelRules.Weapons[Weapon2];
		public DuelChoice Optic2Choice => DuelRules.Optics[Optic2];
		public DuelChoice GrenadeChoice => DuelRules.Grenades[Grenade];
		public DuelChoice HealChoice => DuelRules.Heals[Heal];

		/// <summary>« ak,x2,smg,point,frag,soins » (arme, viseur, arme 2, viseur 2, grenade, soin) : le format des réglages (ligne de commande, configuration d'un tournoi).</summary>
		public override string ToString() => $"{WeaponChoice.Key},{OpticChoice.Key},{Weapon2Choice.Key},{Optic2Choice.Key},{GrenadeChoice.Key},{HealChoice.Key}";

		public static bool TryParse(string text, out DuelLoadout loadout)
		{
			loadout = default;
			var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
			if (parts.Length != 6) return false;
			int w = Index(DuelRules.Weapons, parts[0]), o = Index(DuelRules.Optics, parts[1]), w2 = Index(DuelRules.Weapons, parts[2]), o2 = Index(DuelRules.Optics, parts[3]);
			int g = Index(DuelRules.Grenades, parts[4]), h = Index(DuelRules.Heals, parts[5]);
			if (w < 0 || o < 0 || w2 < 0 || o2 < 0 || g < 0 || h < 0) return false;
			loadout = new DuelLoadout(w, o, w2, o2, g, h);
			return true;
		}

		private static int Index(DuelChoice[] list, string key) => Array.FindIndex(list, c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

		public bool Equals(DuelLoadout o) => Pack() == o.Pack();
		public override bool Equals(object obj) => obj is DuelLoadout o && Equals(o);
		public override int GetHashCode() => Pack().GetHashCode();
	}

	/// <summary>Une option d'équipement du 1 VS 1 : clé (réglages), libellé, objet donné et quantité.</summary>
	public sealed class DuelChoice
	{
		public string Key, Label;
		public byte Item;
		public int Count = 1;
		public DuelChoice(string key, string label, byte item, int count = 1) { Key = key; Label = label; Item = item; Count = count; }
	}

	/// <summary>
	/// Règles du 1 VS 1 : deux joueurs, une petite salle, des manches. Chaque manche, les deux repartent de leur côté avec toute leur vie,
	/// un bouclier bleu plein et leur équipement ; la manche va à celui qui élimine l'autre (au temps : à celui à qui il reste le plus de vie + bouclier).
	/// Le premier à <see cref="RoundsToWin"/> manches gagne.
	/// </summary>
	public static class DuelRules
	{
		public const int RoundsToWin = 5;
		public const float PickSeconds = 25f, PrepareSeconds = 3f, RoundSeconds = 90f, EndSeconds = 3f;
		/// <summary>Bouclier porté par les deux joueurs à chaque manche (bleu, plein).</summary>
		public const int ShieldTier = 1;
		/// <summary>Chargeurs de réserve, gardés pleins tout le temps : les munitions sont infinies (on recharge quand même).</summary>
		public const int ReserveMagazines = 3;
		/// <summary>Commande <c>DuelPick</c> qui valide le choix.</summary>
		public const byte ConfirmPick = 0xF0;

		/// <summary>Argument de la commande <c>DuelPick</c> : une catégorie (0 arme, 1 viseur, 2 grenade, 3 soin, 4 arme 2, 5 viseur 2) et un numéro dans sa liste.</summary>
		public static byte PickArg(int category, int index) => (byte)((category & 0x0F) << 4 | (index & 0x0F));

		/// <summary>Applique un argument de <c>DuelPick</c> à un équipement.</summary>
		public static DuelLoadout WithPick(DuelLoadout l, byte arg)
		{
			int cat = arg >> 4, i = arg & 0x0F;
			return cat switch
			{
				0 => new DuelLoadout(i, l.Optic, l.Weapon2, l.Optic2, l.Grenade, l.Heal),
				1 => new DuelLoadout(l.Weapon, i, l.Weapon2, l.Optic2, l.Grenade, l.Heal),
				2 => new DuelLoadout(l.Weapon, l.Optic, l.Weapon2, l.Optic2, i, l.Heal),
				3 => new DuelLoadout(l.Weapon, l.Optic, l.Weapon2, l.Optic2, l.Grenade, i),
				4 => new DuelLoadout(l.Weapon, l.Optic, i, l.Optic2, l.Grenade, l.Heal),
				5 => new DuelLoadout(l.Weapon, l.Optic, l.Weapon2, i, l.Grenade, l.Heal),
				_ => l,
			};
		}

		public static readonly DuelChoice[] Weapons =
		{
			new DuelChoice("c19", "Pistolet C19", ItemIds.C19),
			new DuelChoice("p2020", "Pistolet P2020", ItemIds.P2020),
			new DuelChoice("smg", "SMG-45", ItemIds.Smg45),
			new DuelChoice("ak", "AK-74", ItemIds.Ak),
			new DuelChoice("lmg", "LMG-63", ItemIds.Lmg63),
			new DuelChoice("pompe", "Fusil à canon scié", ItemIds.Sawnoff),
			new DuelChoice("sniper", "Sniper", ItemIds.Sniper),
		};

		public static readonly DuelChoice[] Optics =
		{
			new DuelChoice("aucun", "Sans viseur", ItemIds.None),
			new DuelChoice("point", "Point rouge", ItemIds.OpticRedDot),
			new DuelChoice("holo", "Holographique", ItemIds.OpticHolo),
			new DuelChoice("x2", "Holographique x2", ItemIds.Optic2x),
			new DuelChoice("x4", "Lunette 4x", ItemIds.Optic4x),
			new DuelChoice("x6", "Lunette 6x", ItemIds.Optic6x),
		};

		public static readonly DuelChoice[] Grenades =
		{
			new DuelChoice("aucune", "Sans grenade", ItemIds.None, 0),
			new DuelChoice("frag", "Grenade à fragmentation", ItemIds.GrenadeFrag),
			new DuelChoice("flash", "Grenade flash", ItemIds.GrenadeFlash),
			new DuelChoice("collante", "Grenade collante", ItemIds.GrenadeSticky),
			new DuelChoice("feu", "Grenade incendiaire", ItemIds.GrenadeFire),
		};

		public static readonly DuelChoice[] Heals =
		{
			new DuelChoice("soins", "2 petits soins", ItemIds.HealSmall, 2),
			new DuelChoice("recharges", "2 petites recharges de bouclier", ItemIds.ShieldSmall, 2),
			new DuelChoice("grand-soin", "1 grand soin", ItemIds.HealBig, 1),
			new DuelChoice("grande-recharge", "1 grande recharge de bouclier", ItemIds.ShieldBig, 1),
		};

		/// <summary>
		/// Un équipement au hasard : deux armes différentes, chacune avec un viseur adapté à sa portée (pas de lunette 6x sur un pompe, pas de nu sur un sniper),
		/// une vraie grenade, un soin.
		/// </summary>
		public static DuelLoadout RandomLoadout(Random rng)
		{
			int w = rng.Next(Weapons.Length), w2 = (w + 1 + rng.Next(Weapons.Length - 1)) % Weapons.Length;
			var optics = OpticsFor(Weapons[w].Item); var optics2 = OpticsFor(Weapons[w2].Item);
			return new DuelLoadout(w, optics[rng.Next(optics.Length)], w2, optics2[rng.Next(optics2.Length)], 1 + rng.Next(Grenades.Length - 1), rng.Next(Heals.Length));
		}

		/// <summary>Viseurs que le tirage au sort donne à une arme (le choix libre, lui, les permet tous).</summary>
		public static int[] OpticsFor(byte weapon) => weapon switch
		{
			ItemIds.Sniper => new[] { 4, 5 },
			ItemIds.Sawnoff => new[] { 0, 1, 2 },
			ItemIds.Smg45 or ItemIds.C19 or ItemIds.P2020 => new[] { 0, 1, 2, 3 },
			_ => new[] { 1, 2, 3, 4 },
		};

		/// <summary>« random », « choice » ou « fixed:ak,x2,smg,point,frag,soins ».</summary>
		public static bool TryParsePolicy(string text, out DuelPolicy policy, out DuelLoadout loadout)
		{
			loadout = default; policy = DuelPolicy.Random;
			text = (text ?? "").Trim();
			if (text.Length == 0 || text.Equals("random", StringComparison.OrdinalIgnoreCase)) return true;
			if (text.Equals("choice", StringComparison.OrdinalIgnoreCase)) { policy = DuelPolicy.Choice; return true; }
			if (text.StartsWith("fixed:", StringComparison.OrdinalIgnoreCase) && DuelLoadout.TryParse(text.Substring(6), out loadout)) { policy = DuelPolicy.Fixed; return true; }
			return false;
		}

		public static string PolicyText(DuelPolicy policy, DuelLoadout fixedLoadout) => policy switch
		{
			DuelPolicy.Choice => "choice",
			DuelPolicy.Fixed => "fixed:" + fixedLoadout,
			_ => "random",
		};

		/// <summary>L'équipement complet d'une manche (sac, deux armes chargées et leurs munitions, grenade, soin, bouclier bleu plein).</summary>
		public static ClassKit Kit(DuelLoadout l)
		{
			var heal = l.HealChoice; var gren = l.GrenadeChoice;
			return new ClassKit
			{
				Id = 254, Name = "1 VS 1", Primary = l.WeaponChoice.Item, PrimaryOptic = l.OpticChoice.Item, Secondary = l.Weapon2Choice.Item, SecondaryOptic = l.Optic2Choice.Item, ReserveMagazines = ReserveMagazines, ShieldTier = ShieldTier,
				HealSmall = heal.Item == ItemIds.HealSmall ? heal.Count : 0, ShieldSmall = heal.Item == ItemIds.ShieldSmall ? heal.Count : 0,
				HealBig = heal.Item == ItemIds.HealBig ? heal.Count : 0, ShieldBig = heal.Item == ItemIds.ShieldBig ? heal.Count : 0,
				Grenades = gren.Item == ItemIds.None ? Array.Empty<byte>() : Enumerable.Repeat(gren.Item, gren.Count).ToArray(),
			};
		}
	}
}
