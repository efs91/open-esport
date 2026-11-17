using Godot;
using System.Collections.Generic;

namespace Openesport.Inventory
{
	public enum ItemType
	{
		Weapon,
		Optic,
		Backpack,
		Ammo,
		Shield,
		Consumable,
		/// <summary>Grenade à lancer (touche F) : fragmentation, collante, incendiaire ou flash.</summary>
		Grenade,
		/// <summary>Sabotage : la bombe du porteur (X : la lâcher).</summary>
		Bomb
	}

	/// <summary>
	/// Description statique d'un objet ramassable (arme, viseur, sac à dos).
	/// </summary>
	public class ItemDefinition
	{
		public string Id;
		public string DisplayName;
		public ItemType Type;

		/// <summary>Scène FPS (armes) ou modèle .glb (accessoires, sacs).</summary>
		public string ScenePath;

		/// <summary>Plus grande dimension du modèle posé au sol, en mètres (0 = taille d'origine).</summary>
		public float GroundSize;

		// --- Armes ---
		public bool AcceptsOptic;
		/// <summary>Décalage du viseur par rapport au point de montage calculé (repère de l'arme : X droite, Y haut, Z vers l'avant), en mètres.</summary>
		public Vector3 OpticOffset;
		/// <summary>Position du viseur le long de l'arme : 0 = arrière, 1 = bouche du canon.</summary>
		public float OpticAlong = 0.35f;
		/// <summary>Les armes du kit sont plus grandes que nature en vue FPS : le viseur est agrandi d'autant.</summary>
		public float OpticScale = 1.6f;

		// --- Viseurs ---
		public float Zoom = 1.0f;
		/// <summary>Distance entre l'œil et l'arrière du viseur en visée, en mètres.</summary>
		public float EyeRelief = 0.15f;
		/// <summary>Hauteur de la cale sous le viseur, pour que sa ligne de visée passe au-dessus des organes de visée de l'arme.</summary>
		public float MountRise;

		/// <summary>Rotation (degrés, autour de Y) qui amène l'avant du modèle vers +Z. Les viseurs du kit pointent vers +X.</summary>
		public float ModelYawDegrees = -90.0f;
		/// <summary>Plus grande dimension du viseur monté, en mètres (0 = taille d'origine, point de fixation à l'origine du modèle).</summary>
		public float ModelSize;
		/// <summary>Correction du point de visée (repère du viseur, avant agrandissement, en mètres : X droite, Y haut). Si le réticule paraît en haut à droite du centre de l'écran, augmente X et Y.</summary>
		public Vector3 CenterOffset;
		/// <summary>Affiche un point rouge lumineux au centre de la lentille.</summary>
		public bool RedDot;

		// --- Sacs à dos ---
		public int BackpackLevel;

		// --- Munitions ---
		/// <summary>Type de munitions, tel que défini par la métadonnée « ammo_type » des armes.</summary>
		public string AmmoType;
		/// <summary>Nombre maximal de munitions par emplacement du sac.</summary>
		public int StackSize = 1;
		/// <summary>Nombre de munitions dans une boîte posée au sol.</summary>
		public int PickupAmount = 1;

		// --- Soins / boucliers ---
		/// <summary>Image de l'objet (icône d'inventaire) ; pour un bouclier, voir ItemTierStyle.ShieldIcon.</summary>
		public string IconPath;
		/// <summary>Couleur du liquide d'un soin (rouge : vie, bleu : bouclier, violet : les deux).</summary>
		public Color LiquidColor = Colors.White;
		public bool GivesHealth, GivesShield;
		/// <summary>Grand soin : une boîte dont le couvercle s'ouvre. Sinon un bidon (petit soin), qui se vide quand on le boit.</summary>
		public bool IsBox;
	}

	/// <summary>
	/// Contenu d'un emplacement du sac à dos : un objet et sa quantité (munitions empilables).
	/// </summary>
	public class ItemStack
	{
		public string Id;
		public int Count = 1;

		public ItemDefinition Definition => ItemDatabase.Get(Id);
	}

	/// <summary>
	/// État d'une arme possédée (ou posée au sol) : chargeur et accessoires la suivent.
	/// </summary>
	public class WeaponSlotState
	{
		public string ItemId;
		/// <summary>-1 = chargeur plein.</summary>
		public int Magazine = -1;
		public string OpticId;
		/// <summary>Niveau de l'arme : 0 classique (blanc), 1 rare (bleu), 2 épique (violet), 3 légendaire (rouge).</summary>
		public int Tier;
		/// <summary>L'animation de prise en main n'est jouée que la première fois.</summary>
		public bool TakePlayed;

		public ItemDefinition Definition => ItemDatabase.Get(ItemId);
		public ItemDefinition Optic => OpticId == null ? null : ItemDatabase.Get(OpticId);
	}

	public static class ItemDatabase
	{
		public const int WeaponSlotCount = 2;
		public const int MaxBackpackLevel = 3;

		private const string Hands = "res://addons/fps-hands/";
		private const string Scopes = "res://assets/accessoires/CuiGames_Accessory_Kit_Legacy_K1_Rail/Scopes/";
		// Lunettes du kit percées dans Blender pour voir à travers (sources .blend dans le même dossier)
		private const string SeeThroughScopes = "res://assets/accessoires/scopes_see_through/";
		private const string Ammo = "res://assets/accessoires/ammo/";
		private const string Backpacks = "res://assets/accessoires/Backpack/";

		private static readonly Dictionary<string, ItemDefinition> _items = new();

		/// <summary>Identifiants dans l'ordre de déclaration.</summary>
		public static readonly List<string> Ids = new();

		static ItemDatabase()
		{
			// Armes du kit fps-hands
			Add(new ItemDefinition { Id = "knife", DisplayName = "Couteau", Type = ItemType.Weapon, ScenePath = Hands + "fps-knife/fps-knife.tscn" });
			Add(new ItemDefinition { Id = "c19", DisplayName = "C19", Type = ItemType.Weapon, ScenePath = Hands + "fps-c19/fps-c19.tscn", AcceptsOptic = true });
			Add(new ItemDefinition { Id = "smg45", DisplayName = "SMG-45", Type = ItemType.Weapon, ScenePath = Hands + "fps-smg45/fps-smg45.tscn", AcceptsOptic = true, OpticAlong = 0.68f, OpticOffset = new Vector3(0, -0.045f, 0) });
			Add(new ItemDefinition { Id = "ak", DisplayName = "AK-74", Type = ItemType.Weapon, ScenePath = Hands + "fps-ak/fps-ak.tscn", AcceptsOptic = true, OpticOffset = new Vector3(0, -0.05f, 0) });
			Add(new ItemDefinition { Id = "lmg63", DisplayName = "LMG-63", Type = ItemType.Weapon, ScenePath = Hands + "fps-lmg63/fps-lmg63.tscn", AcceptsOptic = true, OpticAlong = 0.40f, OpticOffset = new Vector3(0, -0.065f, 0) });
			Add(new ItemDefinition { Id = "sawnoff", DisplayName = "Fusil à canon scié", Type = ItemType.Weapon, ScenePath = Hands + "fps-sawnoff/fps-sawnoff.tscn", AcceptsOptic = true });

			// Fusil de précision à verrou (kit fps-hands généré par tools/build_sniper_viewmodel.gd ; le modèle n'a pas de lunette : on monte les nôtres)
			Add(new ItemDefinition { Id = "sniper", DisplayName = "Sniper", Type = ItemType.Weapon, ScenePath = Hands + "fps-sniper/fps-sniper.tscn", AcceptsOptic = true, OpticAlong = 0.45f, OpticOffset = new Vector3(0, -0.02f, 0) });

			// Viseurs
			Add(new ItemDefinition { Id = "optic_holo", DisplayName = "Viseur holographique 1x", Type = ItemType.Optic, ScenePath = Scopes + "H02_Holo/H02_Holo_Legacy_K1_Rail_Top.glb", Zoom = 1.25f, EyeRelief = 0.30f });
			Add(new ItemDefinition { Id = "optic_4x", DisplayName = "Lunette compacte 4x", Type = ItemType.Optic, ScenePath = SeeThroughScopes + "P42_4x_Compact_see_through.glb", Zoom = 4.0f, EyeRelief = 0.8f, MountRise = 0.022f });
			Add(new ItemDefinition { Id = "optic_6x", DisplayName = "Lunette 6x", Type = ItemType.Optic, ScenePath = SeeThroughScopes + "R6_6x_Scope_see_through.glb", Zoom = 6.0f, EyeRelief = 0.8f, MountRise = 0.008f });

			Add(new ItemDefinition { Id = "optic_reddot", DisplayName = "Viseur point rouge 1x", Type = ItemType.Optic, ScenePath = "res://assets/accessoires/reddot/fps_red_dot_sight_game_model.glb", Zoom = 1.15f, EyeRelief = 0.11f, ModelYawDegrees = 180.0f, ModelSize = 0.12f, RedDot = true, GroundSize = 0.09f });

			// Viseur holographique ouvert x2 (modèle généré par ChatGPT, remis à l'endroit par viseur_x2.tscn)
			Add(new ItemDefinition { Id = "optic_2x", DisplayName = "Viseur holographique x2", Type = ItemType.Optic, ScenePath = "res://assets/accessoires/x2/viseur_x2.tscn", Zoom = 2.0f, EyeRelief = 0.12f, ModelYawDegrees = 0.0f, ModelSize = 0.11f, GroundSize = 0.11f, CenterOffset = new Vector3(-0.0065f, 0.0078f, 0f) });

			// Munitions (modèles générés avec Meshy)
			Add(new ItemDefinition { Id = "ammo_9mm", DisplayName = "Munitions 9mm", Type = ItemType.Ammo, ScenePath = Ammo + "ammo_9mm.glb", AmmoType = "9mm", StackSize = 60, PickupAmount = 20, GroundSize = 0.2f });
			Add(new ItemDefinition { Id = "ammo_rifle", DisplayName = "Munitions de fusil", Type = ItemType.Ammo, ScenePath = Ammo + "ammo_rifle.glb", AmmoType = "rifle", StackSize = 60, PickupAmount = 30, GroundSize = 0.28f });
			Add(new ItemDefinition { Id = "ammo_shell", DisplayName = "Cartouches", Type = ItemType.Ammo, ScenePath = Ammo + "ammo_shell.glb", AmmoType = "shell", StackSize = 16, PickupAmount = 8, GroundSize = 0.22f });

			Add(new ItemDefinition { Id = "ammo_sniper", DisplayName = "Munitions de précision", Type = ItemType.Ammo, ScenePath = "res://assets/munitions/sniper_ammo_box_game-ready.glb", AmmoType = "sniper", StackSize = 20, PickupAmount = 10, GroundSize = 0.3f });

			// Sacs à dos
			Add(new ItemDefinition { Id = "backpack_2", DisplayName = "Sac à dos niv. 2", Type = ItemType.Backpack, ScenePath = Backpacks + "military_backpack.glb", BackpackLevel = 2, GroundSize = 0.55f });
			Add(new ItemDefinition { Id = "backpack_3", DisplayName = "Sac à dos niv. 3", Type = ItemType.Backpack, ScenePath = Backpacks + "military_backpack-1.glb", BackpackLevel = 3, GroundSize = 0.6f });

			AddGrenades();
			Add(new ItemDefinition { Id = "bomb", DisplayName = "Bombe", Type = ItemType.Bomb, StackSize = 1, ScenePath = "res://assets/grenades/basic_explosive.glb", GroundSize = 0.3f });
			AddSupplies();
		}

		private const string Grenades = "res://assets/grenades/";

		/// <summary>Grenades (modèles dans assets/grenades) : les caractéristiques de jeu sont celles du serveur (GrenadeDefs).</summary>
		private static void AddGrenades()
		{
			Add(new ItemDefinition { Id = "grenade_frag", DisplayName = "Grenade à fragmentation", Type = ItemType.Grenade, StackSize = 3, ScenePath = Grenades + "frag_grenade.glb", GroundSize = 0.15f });
			Add(new ItemDefinition { Id = "grenade_sticky", DisplayName = "Grenade collante", Type = ItemType.Grenade, StackSize = 2, ScenePath = Grenades + "arc_star.glb", GroundSize = 0.17f });
			Add(new ItemDefinition { Id = "grenade_fire", DisplayName = "Grenade incendiaire", Type = ItemType.Grenade, StackSize = 2, ScenePath = Grenades + "incendiary_grenade.glb", GroundSize = 0.2f });
			Add(new ItemDefinition { Id = "grenade_flash", DisplayName = "Grenade flash", Type = ItemType.Grenade, StackSize = 3, ScenePath = Grenades + "flashbang_grenade.glb", GroundSize = 0.18f });
		}

		private const string Soin = "res://assets/textures/Soin/";
		private const string SoinModels = "res://assets/accessoires/Soin/";

		/// <summary>Boucliers et soins : modèles provisoires construits par code (ItemProceduralModels) tant que ceux de Meshy n'existent pas.
		/// Pour utiliser un vrai modèle, renseigner ScenePath avec son .glb.</summary>
		private static void AddSupplies()
		{
			Add(new ItemDefinition { Id = "shield", DisplayName = "Bouclier", Type = ItemType.Shield, ScenePath = "res://assets/accessoires/Shield/shield.glb", GroundSize = 0.42f });
			Add(new ItemDefinition { Id = "heal_small", DisplayName = "Petit soin", Type = ItemType.Consumable, StackSize = 4, GroundSize = 0.26f, ScenePath = SoinModels + "heal_small.glb", IconPath = Soin + "Petit Soin vie.png", LiquidColor = new Color(1f, 0.2f, 0.18f), GivesHealth = true });
			Add(new ItemDefinition { Id = "heal_big", DisplayName = "Grand soin", Type = ItemType.Consumable, StackSize = 2, GroundSize = 0.40f, ScenePath = SoinModels + "heal_big.glb", IsBox = true, IconPath = Soin + "Gros soin Vie.png", LiquidColor = new Color(1f, 0.2f, 0.18f), GivesHealth = true });
			Add(new ItemDefinition { Id = "shield_small", DisplayName = "Petit bouclier", Type = ItemType.Consumable, StackSize = 4, GroundSize = 0.26f, ScenePath = SoinModels + "shield_small.glb", IconPath = Soin + "Petit Bouclier .png", LiquidColor = new Color(0.2f, 0.5f, 1f), GivesShield = true });
			Add(new ItemDefinition { Id = "shield_big", DisplayName = "Grand bouclier", Type = ItemType.Consumable, StackSize = 2, GroundSize = 0.40f, ScenePath = SoinModels + "shield_big.glb", IsBox = true, IconPath = Soin + "Gros bouclier.png", LiquidColor = new Color(0.2f, 0.5f, 1f), GivesShield = true });
			Add(new ItemDefinition { Id = "both_small", DisplayName = "Petit bouclier + vie", Type = ItemType.Consumable, StackSize = 2, GroundSize = 0.26f, ScenePath = SoinModels + "both_small.glb", IconPath = Soin + "Petit Bouclier et Vie.png", LiquidColor = new Color(0.75f, 0.3f, 1f), GivesHealth = true, GivesShield = true });
			Add(new ItemDefinition { Id = "both_big", DisplayName = "Grand bouclier + vie", Type = ItemType.Consumable, StackSize = 1, GroundSize = 0.40f, ScenePath = SoinModels + "both_big.glb", IsBox = true, IconPath = Soin + "Gros bouclmier et vie.png", LiquidColor = new Color(0.75f, 0.3f, 1f), GivesHealth = true, GivesShield = true });
		}

		private static void Add(ItemDefinition def)
		{
			_items[def.Id] = def;
			Ids.Add(def.Id);
		}

		public static ItemDefinition Get(string id)
		{
			return id != null && _items.TryGetValue(id, out var def) ? def : null;
		}

		/// <summary>8 emplacements de base, 10 avec un sac niv. 2, 12 avec un sac niv. 3.</summary>
		public static int SlotsForBackpackLevel(int level)
		{
			return 8 + 2 * (Mathf.Clamp(level, 1, MaxBackpackLevel) - 1);
		}

		public static string BackpackIdForLevel(int level)
		{
			foreach (var id in Ids)
			{
				var def = _items[id];
				if (def.Type == ItemType.Backpack && def.BackpackLevel == level)
					return id;
			}
			return null;
		}
	}
}
