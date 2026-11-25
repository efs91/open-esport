using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>Caractéristiques d'une arme, décidées par le serveur. Les valeurs sont regroupées ici pour être équilibrées au même endroit.</summary>
	public sealed class WeaponDef
	{
		public byte Id;
		public float Damage;
		public float RoundsPerMinute;
		public int MagazineSize;
		public bool Automatic;
		public float Range;
		public float SpreadDegrees;
		public int Pellets = 1;
		public float ReloadSeconds;
		public byte AmmoType;
		public bool Melee;
		/// <summary>Vitesse de la balle (m/s) ; 0 = vitesse commune (bullet.speed).</summary>
		public float BulletSpeed;
		/// <summary>Dispersion en visée (°) ; 0 = la même qu'à la hanche.</summary>
		public float AimSpreadDegrees;
		/// <summary>Perte de puissance avec la distance : dégâts entiers jusqu'à FalloffStart (m), puis de moins en moins, nuls à FalloffEnd (0 = pas de perte).</summary>
		public float FalloffStart, FalloffEnd;
		/// <summary>Arme automatique : durée (s) d'appui avant la deuxième balle, pour qu'un clic bref n'en tire qu'une (0 = cadence normale tout de suite).</summary>
		public float TapSeconds;
		/// <summary>Le niveau de l'arme n'agrandit pas le chargeur (fusils à pompe).</summary>
		public bool FixedMagazine;
		/// <summary>Cartouches glissées une par une : le rechargement dure d'autant moins qu'il en manque peu (ReloadSeconds = chargeur vide).</summary>
		public bool ShellReload;
		/// <summary>Munitions de réserve données au minimum avec l'arme (classes, arène) : un chargeur de deux cartouches ne suffit pas à en déduire la réserve.</summary>
		public int MinReserve;

		/// <summary>Part du rechargement cartouche par cartouche qui ne dépend pas de leur nombre (pencher l'arme, la redresser), calée sur l'animation.</summary>
		public const float ShellReloadFixed = 0.063f;

		/// <summary>Durée du rechargement pour <paramref name="missing"/> munitions manquantes (niveau classique).</summary>
		public float ReloadTime(int missing) => ShellReload && MagazineSize > 0
			? ReloadSeconds * (ShellReloadFixed + (1f - ShellReloadFixed) * Math.Clamp(missing, 1, MagazineSize) / MagazineSize) : ReloadSeconds;

		/// <summary>Part des dégâts qui reste après <paramref name="distance"/> mètres (1 = tout, 0 = rien).</summary>
		public float DamageAt(float distance) => FalloffEnd <= 0f || distance <= FalloffStart ? 1f
			: distance >= FalloffEnd ? 0f : (FalloffEnd - distance) / (FalloffEnd - FalloffStart);

		public float Spread(bool aiming) => aiming && AimSpreadDegrees > 0f ? AimSpreadDegrees : SpreadDegrees;

		/// <summary>Munitions de réserve pour ce nombre de chargeurs.</summary>
		public int Reserve(int magazines) => Math.Max(MagazineSize * magazines, MinReserve);

		public float SecondsBetweenShots => 60f / RoundsPerMinute;

		/// <summary>Vitesse réelle de la balle : celle de l'arme, sinon la vitesse commune.</summary>
		public float Speed => BulletSpeed > 0f ? BulletSpeed : GameBalance.BulletSpeed;
	}

	public static class WeaponDefs
	{
		/// <summary>Les poings : ce que le joueur utilise quand il n'a aucune arme en main (coup au corps à corps, rafale tant qu'on tient le clic).</summary>
		public static readonly WeaponDef Fists = new WeaponDef { Id = 0, Damage = 12, RoundsPerMinute = 140, Melee = true, Range = 1.8f, Automatic = true };

		private static readonly WeaponDef[] _byId = new WeaponDef[256];

		static WeaponDefs()
		{
			Add(new WeaponDef { Id = ItemIds.Knife, Damage = 40, RoundsPerMinute = 90, Melee = true, Range = 2.2f, Automatic = false });
			Add(new WeaponDef { Id = ItemIds.C19, Damage = 45, RoundsPerMinute = 150, MagazineSize = 6, Range = 300, SpreadDegrees = 0.25f, ReloadSeconds = 2.3f, AmmoType = 1, BulletSpeed = 500 });
			// pistolet léger (façon P2020) : peu de dégâts, tir semi-automatique rapide ; c'est l'arme de départ du Deathmatch
			Add(new WeaponDef { Id = ItemIds.P2020, Damage = 18, RoundsPerMinute = 420, MagazineSize = 10, Range = 200, SpreadDegrees = 0.35f, ReloadSeconds = 1.3f, AmmoType = 1, BulletSpeed = 480 });
			Add(new WeaponDef { Id = ItemIds.Smg45, Damage = 12, RoundsPerMinute = 1080, MagazineSize = 20, Automatic = true, Range = 200, SpreadDegrees = 0.7f, ReloadSeconds = 1.9f, AmmoType = 1, BulletSpeed = 500 });
			Add(new WeaponDef { Id = ItemIds.Ak, Damage = 14, RoundsPerMinute = 810, MagazineSize = 18, Automatic = true, Range = 400, SpreadDegrees = 0.3f, ReloadSeconds = 2.2f, AmmoType = 2, BulletSpeed = 560, TapSeconds = 0.15f });
			Add(new WeaponDef { Id = ItemIds.Lmg63, Damage = 19, RoundsPerMinute = 540, MagazineSize = 35, Automatic = true, Range = 400, SpreadDegrees = 0.35f, ReloadSeconds = 3.6f, AmmoType = 2, BulletSpeed = 540 });
			Add(new WeaponDef { Id = ItemIds.Sniper, Damage = 80, RoundsPerMinute = 33, MagazineSize = 4, Range = 800, SpreadDegrees = 0.02f, ReloadSeconds = 3.3f, AmmoType = 4, BulletSpeed = 800 });
			// fusils à pompe : plombs étalés à la hanche, groupés en visant (le canon scié, très court, disperse plus que le PeaceMaker) ; ils perdent leur force après 6 m et ne font plus rien à 30 m.
			// canon scié : 8 plombs de 13, deux coups qu'on peut enchaîner ; PeaceMaker : 8 plombs de 13, 6 cartouches, la pompe à actionner entre deux tirs
			Add(new WeaponDef { Id = ItemIds.Sawnoff, Damage = 13, RoundsPerMinute = 240, MagazineSize = 2, Range = 30, SpreadDegrees = 4.5f, AimSpreadDegrees = 2.2f, Pellets = 8, ReloadSeconds = 2.4f, AmmoType = 3, FalloffStart = 6, FalloffEnd = 30, FixedMagazine = true, MinReserve = 16 });
			Add(new WeaponDef { Id = ItemIds.Peacemaker, Damage = 13, RoundsPerMinute = 66, MagazineSize = 6, Range = 30, SpreadDegrees = 3.2f, AimSpreadDegrees = 1.2f, Pellets = 8, ReloadSeconds = 4.8f, AmmoType = 3, FalloffStart = 6, FalloffEnd = 30, FixedMagazine = true, ShellReload = true, MinReserve = 16 });
		}

		private static void Add(WeaponDef d) => _byId[d.Id] = d;

		public static WeaponDef Get(byte id) => _byId[id];

		/// <summary>Toutes les armes (poings compris, en dernier), dans un ordre fixe : sert au registre des réglages.</summary>
		public static IEnumerable<WeaponDef> All
		{
			get
			{
				for (int i = 0; i < _byId.Length; i++) if (_byId[i] != null) yield return _byId[i];
				yield return Fists;
			}
		}

		/// <summary>Nom de l'arme dans le fichier de réglages (« ak », « fists »…).</summary>
		public static string NameOf(WeaponDef d) => d == Fists ? "fists" : ItemDefs.Get(d.Id).Name;

		/// <summary>Reprend les valeurs du registre de réglages (fichier du serveur ou valeurs reçues).</summary>
		public static void ApplyBalance()
		{
			foreach (var d in All)
			{
				string n = NameOf(d);
				void Set(string field, Action<float> set) { string key = $"weapon.{n}.{field}"; if (GameBalance.Has(key)) set(GameBalance.F(key)); }
				Set("damage", v => d.Damage = v);
				Set("roundsPerMinute", v => d.RoundsPerMinute = Math.Max(1f, v));
				Set("magazine", v => d.MagazineSize = (int)v);
				Set("range", v => d.Range = v);
				Set("spread", v => d.SpreadDegrees = v);
				Set("aimSpread", v => d.AimSpreadDegrees = Math.Max(0f, v));
				Set("falloffStart", v => d.FalloffStart = Math.Max(0f, v));
				Set("falloffEnd", v => d.FalloffEnd = Math.Max(0f, v));
				Set("tapSeconds", v => d.TapSeconds = Math.Max(0f, v));
				Set("pellets", v => d.Pellets = Math.Max(1, (int)v));
				Set("reloadSeconds", v => d.ReloadSeconds = v);
				Set("bulletSpeed", v => d.BulletSpeed = Math.Max(0f, v));
			}
		}
	}
}
