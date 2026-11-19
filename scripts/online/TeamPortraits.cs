using System;
using System.Collections.Generic;
using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Portraits des équipiers (tête et épaules) pour le HUD : le personnage, avec son apparence, est photographié dans une petite scène à part,
	/// quelques images le temps que le modèle se charge, puis l'image est figée (plus aucun coût de rendu). Un portrait par apparence, gardé en mémoire.
	/// </summary>
	public partial class TeamPortraits : Node
	{
		public const int Size = 96;
		private const int FramesBeforeFreeze = 20;

		private static TeamPortraits _host;
		private static readonly Dictionary<string, SubViewport> Cache = new();

		private readonly List<(SubViewport vp, int frames)> _pending = new();

		/// <summary>Le portrait d'une apparence (octets reçus du serveur), ou null si elle est inconnue.</summary>
		public static Texture2D Get(byte[] appearance)
		{
			if (appearance == null || appearance.Length == 0) return null;
			string key = Convert.ToBase64String(appearance);
			if (Cache.TryGetValue(key, out var vp) && GodotObject.IsInstanceValid(vp)) return vp.GetTexture();
			if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return null;
			if (_host == null || !GodotObject.IsInstanceValid(_host))
			{
				_host = new TeamPortraits { Name = "TeamPortraits" };
				tree.Root.AddChild(_host);
			}
			vp = _host.Render(appearance);
			Cache[key] = vp;
			return vp.GetTexture();
		}

		private SubViewport Render(byte[] appearance)
		{
			var vp = new SubViewport
			{
				Size = new Vector2I(Size, Size),
				TransparentBg = true,
				OwnWorld3D = true,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
				Msaa3D = Viewport.Msaa.Msaa4X,
				GuiDisableInput = true,
			};
			AddChild(vp);
			vp.AddChild(new WorldEnvironment
			{
				Environment = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.75f }
			});
			vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30, 25, 0), LightEnergy = 1.1f });
			vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-15, -150, 0), LightEnergy = 0.35f });
			var animator = new CharacterAnimator { Appearance = CharacterAppearance.Unpack(appearance) };
			vp.AddChild(animator);
			// tête et épaules, de face (le personnage regarde vers +Z, comme dans le panneau Personnage)
			var cam = new Camera3D { Current = true, Fov = 26f };
			vp.AddChild(cam);
			cam.LookAtFromPosition(new Vector3(0, 1.58f, 1.05f), new Vector3(0, 1.52f, 0), Vector3.Up);
			_pending.Add((vp, 0));
			return vp;
		}

		public override void _Process(double delta)
		{
			for (int i = _pending.Count - 1; i >= 0; i--)
			{
				var (vp, frames) = _pending[i];
				if (!GodotObject.IsInstanceValid(vp)) { _pending.RemoveAt(i); continue; }
				if (++frames < FramesBeforeFreeze) { _pending[i] = (vp, frames); continue; }
				vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;        // l'image reste, le rendu s'arrête
				_pending.RemoveAt(i);
			}
		}
	}
}
