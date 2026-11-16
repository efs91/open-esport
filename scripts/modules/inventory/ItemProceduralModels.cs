using Godot;

namespace Openesport.Inventory
{
	/// <summary>
	/// Modèles 3D provisoires des boucliers et des soins, construits par code : un bouclier est une cellule hexagonale lumineuse
	/// (de la couleur de son niveau), un soin est un flacon dont le liquide est rouge (vie), bleu (bouclier) ou violet (les deux).
	/// Quand les modèles Meshy existeront, il suffira de renseigner ItemDefinition.ScenePath : ils prendront la place de ceux-ci.
	/// </summary>
	public static class ItemProceduralModels
	{
		private static StandardMaterial3D Glow(Color c, float energy, float alpha = 1f)
		{
			var m = new StandardMaterial3D
			{
				AlbedoColor = new Color(c.R, c.G, c.B, alpha),
				EmissionEnabled = true,
				Emission = c,
				EmissionEnergyMultiplier = energy,
				Roughness = 0.35f,
				Metallic = 0.2f,
			};
			if (alpha < 1f) m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			return m;
		}

		public static Node3D Build(ItemDefinition def, int tier)
		{
			return def.Type == ItemType.Shield ? Shield(tier) : Bottle(def);
		}

		/// <summary>Plastron hexagonal : cellule centrale, anneau et deux épaulières, tout en lumière de la couleur du niveau.</summary>
		private static Node3D Shield(int tier)
		{
			var c = ItemTierStyle.ColorOf(tier);
			var disc = new Node3D { Name = "ShieldModel" };
			disc.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.2f, Height = 0.05f, RadialSegments = 6, Rings = 1 }, MaterialOverride = Glow(c.Darkened(0.55f), 0.5f) });
			disc.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.15f, Height = 0.065f, RadialSegments = 6, Rings = 1 }, MaterialOverride = Glow(c, 2.2f, 0.85f) });
			for (int side = -1; side <= 1; side += 2)
				disc.AddChild(new MeshInstance3D
				{
					Mesh = new CylinderMesh { TopRadius = 0.085f, BottomRadius = 0.085f, Height = 0.04f, RadialSegments = 6, Rings = 1 },
					MaterialOverride = Glow(c, 1.6f, 0.9f),
					Position = new Vector3(side * 0.24f, 0f, -0.03f)
				});
			disc.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.07f, 0.018f) }, MaterialOverride = Glow(Colors.White, 3f), Position = new Vector3(0, 0.03f, 0.1f) });
			// posé à plat : la cellule est un cylindre vertical, on le couche légèrement vers l'avant pour qu'il se lise de loin
			var root = new Node3D { Name = "ShieldRoot" };
			disc.Position = new Vector3(0, 0.03f, 0);
			root.AddChild(disc);
			return root;
		}

		/// <summary>Flacon : verre translucide, liquide lumineux (deux couleurs pour bouclier + vie), bouchon et base métalliques.</summary>
		private static Node3D Bottle(ItemDefinition def)
		{
			var root = new Node3D { Name = "BottleModel" };
			var metal = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.17f, 0.2f), Metallic = 0.85f, Roughness = 0.35f };
			root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.062f, BottomRadius = 0.066f, Height = 0.02f, RadialSegments = 16 }, MaterialOverride = metal, Position = new Vector3(0, 0.01f, 0) });
			var glass = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.92f, 1f, 0.22f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.05f, Metallic = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
			root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.058f, BottomRadius = 0.058f, Height = 0.15f, RadialSegments = 16 }, MaterialOverride = glass, Position = new Vector3(0, 0.095f, 0) });

			// liquide : chaque couche est sous un pivot placé à sa base, pour que le niveau baisse par le haut quand on réduit l'échelle Y
			Node3D Layer(string name, Color color, float baseY, float height)
			{
				var pivot = new Node3D { Name = name, Position = new Vector3(0, baseY, 0) };
				pivot.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = height, RadialSegments = 16 }, MaterialOverride = Glow(color, 2.2f), Position = new Vector3(0, height / 2f, 0) });
				root.AddChild(pivot);
				return pivot;
			}
			if (def.GivesHealth && def.GivesShield)
			{
				Layer("LiquidA", new Color(1f, 0.2f, 0.18f), 0.025f, 0.065f);
				Layer("LiquidB", new Color(0.2f, 0.5f, 1f), 0.09f, 0.065f);
			}
			else
				Layer("LiquidA", def.LiquidColor, 0.025f, 0.13f);

			root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.058f, Height = 0.04f, RadialSegments = 16 }, MaterialOverride = metal, Position = new Vector3(0, 0.19f, 0) });
			// croix lumineuse sur la face avant
			root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.016f, 0.004f) }, MaterialOverride = Glow(Colors.White, 3f), Position = new Vector3(0, 0.095f, 0.06f) });
			root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.016f, 0.05f, 0.004f) }, MaterialOverride = Glow(Colors.White, 3f), Position = new Vector3(0, 0.095f, 0.06f) });
			return root;
		}
	}
}
