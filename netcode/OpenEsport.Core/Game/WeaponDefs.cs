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
			Add(new WeaponDef { Id = ItemIds.C19, Damage = 22, RoundsPerMinute = 450, MagazineSize = 19, Range = 120, SpreadDegrees = 0.5f, ReloadSeconds = 1.6f, AmmoType = 1 });
			Add(new WeaponDef { Id = ItemIds.Smg45, Damage = 13, RoundsPerMinute = 900, MagazineSize = 25, Automatic = true, Range = 150, SpreadDegrees = 1.2f, ReloadSeconds = 1.8f, AmmoType = 1 });
			Add(new WeaponDef { Id = ItemIds.Ak, Damage = 19, RoundsPerMinute = 600, MagazineSize = 30, Automatic = true, Range = 400, SpreadDegrees = 0.3f, ReloadSeconds = 2.3f, AmmoType = 2 });
			Add(new WeaponDef { Id = ItemIds.Lmg63, Damage = 17, RoundsPerMinute = 700, MagazineSize = 100, Automatic = true, Range = 400, SpreadDegrees = 0.5f, ReloadSeconds = 4.5f, AmmoType = 2 });
			Add(new WeaponDef { Id = ItemIds.Sniper, Damage = 75, RoundsPerMinute = 45, MagazineSize = 5, Range = 800, SpreadDegrees = 0.02f, ReloadSeconds = 3.2f, AmmoType = 4, BulletSpeed = 600 });
			Add(new WeaponDef { Id = ItemIds.Sawnoff, Damage = 11, RoundsPerMinute = 120, MagazineSize = 2, Range = 40, SpreadDegrees = 4f, Pellets = 8, ReloadSeconds = 2.4f, AmmoType = 3 });
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
				Set("pellets", v => d.Pellets = Math.Max(1, (int)v));
				Set("reloadSeconds", v => d.ReloadSeconds = v);
				Set("bulletSpeed", v => d.BulletSpeed = Math.Max(0f, v));
			}
		}
	}
}
