using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>Famille d'effet d'une grenade.</summary>
	public enum GrenadeEffect : byte { Blast = 0, Fire = 1, Flash = 2 }

	/// <summary>Caractéristiques d'une grenade, décidées par le serveur (réglables : « grenade.* » dans balance.json).</summary>
	public sealed class GrenadeDef
	{
		public byte Id;
		public GrenadeEffect Effect;
		/// <summary>Délai (s) entre le lancer et l'effet.</summary>
		public float Fuse;
		/// <summary>Rayon (m) : de l'explosion, de la zone de feu, ou portée de l'éclair.</summary>
		public float Radius;
		/// <summary>Explosion : dégâts au centre (décroissent avec la distance, 20 % au bord).</summary>
		public float Damage;
		/// <summary>Colle à la première surface (ou au premier joueur) touchée.</summary>
		public bool Sticky;
		/// <summary>Déclenche l'effet dès le premier impact (sans attendre le délai).</summary>
		public bool OnImpact;
		/// <summary>Zone de feu : durée (s) et dégâts par seconde.</summary>
		public float BurnSeconds, BurnDps;
		/// <summary>Éclair : durée maximale (s) de l'aveuglement, de face et de près.</summary>
		public float FlashSeconds;

		/// <summary>Numéro envoyé sur le réseau (0 = frag, 1 = collante, 2 = incendiaire, 3 = flash ; 4 = zone de feu, voir <see cref="GrenadeDefs.FireZoneKind"/>).</summary>
		public byte Kind => (byte)(Id - ItemIds.GrenadeFrag);
	}

	public static class GrenadeDefs
	{
		/// <summary>Numéro réseau d'une zone de feu (un foyer qui brûle après une grenade incendiaire).</summary>
		public const byte FireZoneKind = 4;

		/// <summary>Vitesse de lancer (m/s) et gravité (m/s²) des grenades : un arc court, bien plus lourd qu'une balle.</summary>
		public const float ThrowSpeed = 19f, Gravity = 22f;
		/// <summary>Durée (s) pendant laquelle on ne peut pas relancer.</summary>
		public const float ThrowCooldown = 0.9f;

		private static readonly GrenadeDef[] _byId = new GrenadeDef[256];
		public static readonly List<GrenadeDef> All = new List<GrenadeDef>();

		static GrenadeDefs()
		{
			Add(new GrenadeDef { Id = ItemIds.GrenadeFrag, Effect = GrenadeEffect.Blast, Fuse = 3.0f, Radius = 7f, Damage = 110f });
			Add(new GrenadeDef { Id = ItemIds.GrenadeSticky, Effect = GrenadeEffect.Blast, Fuse = 2.2f, Radius = 5f, Damage = 90f, Sticky = true });
			Add(new GrenadeDef { Id = ItemIds.GrenadeFire, Effect = GrenadeEffect.Fire, Fuse = 2.5f, Radius = 4.5f, OnImpact = true, BurnSeconds = 7f, BurnDps = 18f });
			Add(new GrenadeDef { Id = ItemIds.GrenadeFlash, Effect = GrenadeEffect.Flash, Fuse = 1.6f, Radius = 30f, FlashSeconds = 5f });
		}

		private static void Add(GrenadeDef d) { _byId[d.Id] = d; All.Add(d); }

		public static GrenadeDef Get(byte itemId) => _byId[itemId];
		public static bool IsGrenade(byte itemId) => _byId[itemId] != null;

		/// <summary>Nom de la grenade dans le fichier de réglages (« frag », « sticky »…).</summary>
		public static string NameOf(GrenadeDef d) => ItemDefs.Get(d.Id).Name.Substring("grenade_".Length);

		/// <summary>Reprend les valeurs du registre de réglages.</summary>
		public static void ApplyBalance()
		{
			foreach (var d in All)
			{
				string n = NameOf(d);
				void Set(string field, Action<float> set) { string key = $"grenade.{n}.{field}"; if (GameBalance.Has(key)) set(GameBalance.F(key)); }
				Set("fuse", v => d.Fuse = Math.Max(0.2f, v));
				Set("radius", v => d.Radius = Math.Max(0.5f, v));
				Set("damage", v => d.Damage = v);
				Set("burnSeconds", v => d.BurnSeconds = v);
				Set("burnDps", v => d.BurnDps = v);
				Set("flashSeconds", v => d.FlashSeconds = v);
			}
		}
	}

	/// <summary>Une grenade ou une zone de feu en vol / au sol, telle que la voient les clients.</summary>
	public struct GrenadeInfo
	{
		public ushort Id;
		/// <summary>0 à 3 : le type de grenade ; 4 : zone de feu.</summary>
		public byte Kind;
		/// <summary>Collée : elle ne bouge plus.</summary>
		public bool Stuck;
		public Vec3 Pos, Vel;
	}
}
