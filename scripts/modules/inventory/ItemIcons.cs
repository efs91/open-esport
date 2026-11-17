using System.Collections.Generic;
using Godot;

namespace Openesport.Inventory
{
	/// <summary>
	/// Miniatures des objets pour les menus (inventaire, menus circulaires, boutique). Un objet qui a déjà une image (soins, boucliers) la garde ;
	/// pour les autres (armes, viseurs, munitions, grenades, bombe) la miniature est le modèle 3D de l'objet, photographié une fois
	/// (fond transparent, vue de dessus pour les armes, de trois quarts pour le reste) puis gardé en mémoire.
	/// </summary>
	public partial class ItemIcons : Node
	{
		public const int Size = 128;

		private static ItemIcons _host;
		private static readonly Dictionary<string, Texture2D> Cache = new();

		/// <summary>La miniature de l'objet (identifiant de <see cref="ItemDatabase"/>), ou null s'il n'a pas de modèle. Elle apparaît à l'image suivante la première fois.</summary>
		public static Texture2D Get(string itemId)
		{
			if (string.IsNullOrEmpty(itemId)) return null;
			if (Cache.TryGetValue(itemId, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
			var def = ItemDatabase.Get(itemId);
			if (def == null) return null;
			Texture2D tex = null;
			if (!string.IsNullOrEmpty(def.IconPath) && ResourceLoader.Exists(def.IconPath)) tex = GD.Load<Texture2D>(def.IconPath);
			else if (!string.IsNullOrEmpty(def.ScenePath)) tex = Render(def);
			if (tex != null) Cache[itemId] = tex;
			return tex;
		}

		private static Texture2D Render(ItemDefinition def)
		{
			if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return null;
			if (_host == null || !GodotObject.IsInstanceValid(_host))
			{
				_host = new ItemIcons { Name = "ItemIcons" };
				tree.Root.AddChild(_host);
			}

			var vp = new SubViewport
			{
				Size = new Vector2I(Size, Size),
				TransparentBg = true,
				OwnWorld3D = true,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
				Msaa3D = Viewport.Msaa.Msaa4X,
				GuiDisableInput = true,
			};
			_host.AddChild(vp);

			var model = ItemVisuals.BuildGroundModel(def);
			vp.AddChild(model);
			var key = new DirectionalLight3D { LightEnergy = 1.15f, RotationDegrees = new Vector3(-55, 35, 0) };
			var fill = new DirectionalLight3D { LightEnergy = 0.55f, RotationDegrees = new Vector3(-30, -150, 0) };
			vp.AddChild(key); vp.AddChild(fill);

			var box = ItemVisuals.ComputeLocalAabb(model) ?? new Aabb(new Vector3(-0.1f, 0, -0.1f), new Vector3(0.2f, 0.2f, 0.2f));
			var center = box.GetCenter();
			var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Near = 0.01f, Far = 50f };
			vp.AddChild(cam);
			bool weapon = def.Type == ItemType.Weapon;
			if (weapon)
			{
				// l'arme est couchée sur le flanc : on la regarde d'en haut, sa longueur à l'horizontale de l'image
				bool alongX = box.Size.X >= box.Size.Z;
				cam.Size = Mathf.Max(box.Size.X, box.Size.Z) * 1.12f;
				cam.LookAtFromPosition(center + Vector3.Up * 2f, center, alongX ? Vector3.Forward : Vector3.Right);
			}
			else
			{
				float extent = box.Size.Length();
				cam.Size = extent * 0.95f;
				cam.LookAtFromPosition(center + new Vector3(0.8f, 0.95f, 1.2f).Normalized() * 2f, center, Vector3.Up);
			}
			return vp.GetTexture();
		}
	}
}
