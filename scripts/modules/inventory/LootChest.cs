using Godot;

namespace Openesport.Inventory
{
	/// <summary>
	/// Coffre de butin (modèle généré avec Meshy, assets/models/chest/chest.glb : « Chest_Base » + « Chest_Lid » dont l'origine est la charnière).
	/// Fermé, il luit légèrement ; à l'ouverture le couvercle pivote, une lumière chaude sort du coffre et la lueur s'éteint.
	/// Il ne contient rien lui-même : le serveur (ou le mode solo) pose les objets autour quand il s'ouvre.
	/// </summary>
	public partial class LootChest : Node3D
	{
		public const string ModelPath = "res://assets/models/chest/chest.glb";
		/// <summary>Taille voulue (longueur en mètres) : le modèle fait 1,5 m.</summary>
		public const float ModelLength = 1.5f;
		private const float OpenAngle = Mathf.Pi * 0.62f; // ~110°
		private const float OpenSeconds = 0.9f;

		public bool IsOpen { get; private set; }

		private Node3D _lid;
		private OmniLight3D _glow;
		private Tween _tween;

		public static LootChest Spawn(Node parent, Vector3 position, float yaw, bool open = false)
		{
			var chest = new LootChest { Name = "Chest", Position = position, Rotation = new Vector3(0, yaw, 0) };
			parent.AddChild(chest);
			chest.GlobalPosition = position;
			if (open) chest.SetOpen(true, animate: false);
			return chest;
		}

		public override void _Ready()
		{
			var scene = GD.Load<PackedScene>(ModelPath);
			if (scene == null) { GD.PrintErr("[LootChest] modèle introuvable : " + ModelPath); return; }
			var model = scene.Instantiate<Node3D>();
			AddChild(model);
			_lid = FindByName(model, "Lid");
			// volume plein : les balles s'y arrêtent et on peut se cacher derrière (même taille que la boîte du serveur : 1,5 x 0,63 x 0,84 m)
			var body = new StaticBody3D { Name = "Collision" };
			body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.5f, 0.63f, 0.84f) }, Position = new Vector3(0, 0.315f, 0) });
			AddChild(body);
			_glow = new OmniLight3D
			{
				Name = "Glow", LightColor = new Color(1f, 0.62f, 0.15f), LightEnergy = 0.8f, OmniRange = 3.5f,
				Position = new Vector3(0, 0.35f, 0), ShadowEnabled = false
			};
			AddChild(_glow);
			if (IsOpen) ApplyOpenPose();
		}

		private static Node3D FindByName(Node root, string part)
		{
			foreach (var n in root.FindChildren("*", "Node3D", true, false))
				if (n is Node3D node && node.Name.ToString().Contains(part)) return node;
			return null;
		}

		/// <summary>Ouvre (avec animation) ou ferme le coffre.</summary>
		public void SetOpen(bool open, bool animate = true)
		{
			Openesport.Diagnostics.LagTracer.Event(open ? "coffre : ouverture" : "coffre : fermeture");
			if (open == IsOpen) return;
			IsOpen = open;
			if (_lid == null) return; // pas encore dans l'arbre : _Ready appliquera la pose
			if (!animate) { ApplyOpenPose(); return; }

			_tween?.Kill();
			_tween = CreateTween().SetParallel(true);
			float target = open ? OpenAngle : 0f;
			// le couvercle monte d'un coup sec, puis retombe un peu en butée (rebond)
			_tween.TweenProperty(_lid, "rotation:x", target, OpenSeconds).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
			if (_glow != null)
			{
				_tween.TweenProperty(_glow, "light_energy", open ? 2.6f : 0.8f, OpenSeconds * 0.5f);
				_tween.Chain().TweenProperty(_glow, "light_energy", open ? 0.25f : 0.8f, 1.6f);
			}
		}

		private void ApplyOpenPose()
		{
			if (_lid != null) _lid.Rotation = new Vector3(IsOpen ? OpenAngle : 0f, 0, 0);
			if (_glow != null) _glow.LightEnergy = IsOpen ? 0.25f : 0.8f;
		}
	}
}
