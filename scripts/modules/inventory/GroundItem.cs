using Godot;
using System.Collections.Generic;

namespace Openesport.Inventory
{
	/// <summary>
	/// Objet posé au sol, ramassable par le joueur.
	/// Peut être placé dans une map (propriété ItemId) ou créé par code via <see cref="Spawn"/>.
	/// </summary>
	public partial class GroundItem : Node3D
	{
		/// <summary>Tous les objets actuellement au sol.</summary>
		public static readonly List<GroundItem> All = new();

		[Export]
		public string ItemId = "";

		/// <summary>Colle l'objet au sol (raycast vers le bas) à son apparition.</summary>
		[Export]
		public bool SnapToGround = true;

		/// <summary>Hauteur au-dessus de l'objet d'où part la recherche du sol.</summary>
		[Export]
		public float SnapRayHeight = 0.5f;

		/// <summary>Quantité contenue (munitions). 0 = quantité par défaut de l'objet.</summary>
		[Export]
		public int Count;

		/// <summary>Niveau de rareté (0 classique, 1 rare, 2 épique, 3 légendaire) : couleur du halo et du nom.</summary>
		public int Tier;

		/// <summary>État de l'arme (chargeur, viseur) quand l'objet est une arme.</summary>
		public WeaponSlotState WeaponState;

		public ItemDefinition Definition => ItemDatabase.Get(ItemId);

		private int _snapAttempts = 120;

		public static GroundItem Spawn(Node parent, string itemId, Vector3 position, float yaw = 0f, WeaponSlotState weaponState = null, float snapRayHeight = 0.5f, int count = 0, bool snapToGround = true, int tier = 0)
		{
			var item = new GroundItem
			{
				ItemId = itemId,
				Count = count,
				SnapToGround = snapToGround,
				WeaponState = weaponState,
				SnapRayHeight = snapRayHeight,
				Tier = tier,
				Name = $"GroundItem_{itemId}"
			};
			parent.AddChild(item, true);
			item.GlobalPosition = position;
			item.Rotation = new Vector3(0, yaw, 0);
			return item;
		}

		public override void _EnterTree()
		{
			All.Add(this);
		}

		public override void _ExitTree()
		{
			All.Remove(this);
		}

		public override void _Ready()
		{
			using var _trace = Openesport.Diagnostics.LagTracer.Scope("GroundItem._Ready " + ItemId);
			var def = Definition;
			if (def == null)
			{
				GD.PrintErr($"[GroundItem] Objet inconnu : '{ItemId}'");
				QueueFree();
				return;
			}

			if (def.Type == ItemType.Weapon)
				WeaponState ??= new WeaponSlotState { ItemId = ItemId };
			if (Count <= 0)
				Count = def.Type == ItemType.Ammo ? def.PickupAmount : 1;

			if (def.Type == ItemType.Weapon && WeaponState != null) WeaponState.Tier = Tier;
			Tier = ItemTierStyle.TierOf(def, Tier); // les sacs à dos ont un niveau fixe
			AddChild(ItemVisuals.BuildGroundModel(def, Tier));
			if (def.Type == ItemType.Weapon || def.Type == ItemType.Backpack || def.Type == ItemType.Shield || def.Type == ItemType.Consumable) AddAura();
			SetPhysicsProcess(SnapToGround);
		}

		/// <summary>Boîte englobante de l'objet tel qu'il est affiché, dans le monde.</summary>
		public Aabb WorldBounds()
		{
			Aabb? total = null;
			foreach (var child in FindChildren("*", "VisualInstance3D", true, false))
			{
				if (child is not VisualInstance3D v || !v.Visible) continue;
				var box = v.GlobalTransform * v.GetAabb();
				total = total == null ? box : total.Value.Merge(box);
			}
			return total ?? new Aabb(GlobalPosition - Vector3.One * 0.15f, Vector3.One * 0.3f);
		}

		/// <summary>
		/// Halo de rareté, visible de loin : un disque lumineux au sol et, dès le niveau rare, un faisceau vertical d'autant plus
		/// haut que le niveau est élevé. Couleur : blanc, bleu, violet, rouge.
		/// </summary>
		private void AddAura()
		{
			var color = ItemTierStyle.ColorOf(Tier);
			Material Glow(float alpha) => new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color(color.R, color.G, color.B, alpha),
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				NoDepthTest = false,
			};
			var disc = new MeshInstance3D
			{
				Name = "TierDisc",
				Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 0.01f, RadialSegments = 32, Rings = 1 },
				MaterialOverride = Glow(Tier == 0 ? 0.30f : 0.55f),
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position = new Vector3(0, 0.03f, 0)
			};
			AddChild(disc);
			if (Tier <= 0) return;
			float height = 0.7f + 0.55f * Tier;
			AddChild(new MeshInstance3D
			{
				Name = "TierBeam",
				Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.16f, Height = height, RadialSegments = 12, Rings = 1 },
				MaterialOverride = Glow(0.38f),
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position = new Vector3(0, height * 0.5f + 0.03f, 0)
			});
		}

		/// <summary>Nom affiché dans l'invite de ramassage.</summary>
		public string GetPromptName()
		{
			var def = Definition;
			if (def == null)
				return ItemId;
			if (def.Type == ItemType.Ammo)
				return $"{def.DisplayName} x{Count}";
			var optic = WeaponState?.Optic;
			string name = def.Type == ItemType.Weapon || def.Type == ItemType.Backpack ? $"{def.DisplayName} — {ItemTierStyle.NameOf(Tier)}"
				: def.Type == ItemType.Shield ? $"{def.DisplayName} — {ItemTierStyle.NameOf(Tier)} ({ItemTierStyle.ShieldPoints(Tier)} PS)" : def.DisplayName;
			return optic != null ? $"{name} + {optic.DisplayName}" : name;
		}

		public override void _PhysicsProcess(double delta)
		{
			// Le sol (terrain) peut ne pas avoir de collision dès la première frame : on réessaie un moment
			var from = GlobalPosition + Vector3.Up * SnapRayHeight;
			var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 60.0f);
			var exclude = new Godot.Collections.Array<Rid>();

			for (int i = 0; i < 4; i++)
			{
				query.Exclude = exclude;
				var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
				if (hit.Count == 0)
					break;

				// On ne se pose pas sur un personnage ou un objet mobile
				var collider = hit["collider"].As<GodotObject>();
				if (collider is CharacterBody3D || collider is RigidBody3D)
				{
					exclude.Add(hit["rid"].AsRid());
					continue;
				}

				GlobalPosition = hit["position"].AsVector3();
				SetPhysicsProcess(false);
				return;
			}

			if (--_snapAttempts <= 0)
				SetPhysicsProcess(false);
		}
	}
}
