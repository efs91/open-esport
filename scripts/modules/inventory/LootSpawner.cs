using Godot;
using System.Collections.Generic;

namespace Openesport.Inventory
{
	/// <summary>
	/// Pose l'équipement de départ au sol devant le point d'apparition :
	/// les armes au fond, les viseurs et les sacs à dos au milieu, les boîtes de munitions devant.
	/// </summary>
	public static class LootSpawner
	{
		private const float Spacing = 1.1f;

		public static void SpawnStarterLoot(Node parent, Transform3D spawn)
		{
			var weapons = new List<string>();
			var accessories = new List<string>();
			var ammo = new List<string>();

			foreach (var id in ItemDatabase.Ids)
			{
				switch (ItemDatabase.Get(id).Type)
				{
					case ItemType.Weapon:
						weapons.Add(id);
						break;
					case ItemType.Ammo:
						// Deux boîtes de chaque type
						ammo.Add(id);
						ammo.Add(id);
						break;
					default:
						accessories.Add(id);
						break;
				}
			}

			SpawnRow(parent, spawn, ammo, 1.6f);
			SpawnRow(parent, spawn, accessories, 2.8f);
			SpawnRow(parent, spawn, weapons, 4.2f);
		}

		private static void SpawnRow(Node parent, Transform3D spawn, List<string> ids, float distance)
		{
			var forward = -spawn.Basis.Z.Normalized();
			var right = spawn.Basis.X.Normalized();
			float yaw = spawn.Basis.GetEuler().Y;

			for (int i = 0; i < ids.Count; i++)
			{
				float offset = (i - (ids.Count - 1) * 0.5f) * Spacing;
				var position = spawn.Origin + forward * distance + right * offset;
				// Le terrain peut monter ou descendre : on cherche le sol depuis bien au-dessus
				GroundItem.Spawn(parent, ids[i], position, yaw, null, 6.0f);
			}
		}
	}
}
