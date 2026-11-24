using System;
using Godot;

namespace Openesport.Utils
{
	/// <summary>
	/// Réglages graphiques (menu Paramètres → Graphismes), mémorisés dans user://graphics.cfg et appliqués au démarrage :
	/// affichage (plein écran ou fenêtre), synchro verticale, limite d'images, puis la qualité (préréglage ou réglages fins :
	/// résolution de rendu, anticrénelage, ombres, effets de lumière) pour les machines moins puissantes.
	/// Les cartes gardent leurs propres réglages : la qualité ne fait qu'en retirer (jamais d'effet qu'une carte n'a pas).
	/// </summary>
	public static class GraphicsSettings
	{
		private const string Path = "user://graphics.cfg";

		public enum Preset { Low, Medium, High, Ultra, Custom }
		public enum AntiAliasing { None, Fxaa, Msaa2, Msaa4 }
		public enum Level { Off, Low, Medium, High }

		public static bool Fullscreen = true;
		public static bool VSync = true;
		/// <summary>Images par seconde au plus (0 = sans limite).</summary>
		public static int MaxFps;
		public static Preset Quality = Preset.High;
		/// <summary>Part de la résolution de l'écran rendue en 3D (agrandie ensuite par FSR) : 1 = pleine résolution.</summary>
		public static float RenderScale = 1f;
		public static AntiAliasing Aa = AntiAliasing.Msaa2;
		public static Level Shadows = Level.High;
		/// <summary>Effets de lumière : Off = aucun, Low = halo seulement, Medium = + occlusion ambiante, High = tous ceux de la carte (reflets, brouillard volumétrique…).</summary>
		public static Level Effects = Level.High;

		public static readonly float[] RenderScales = { 0.5f, 0.67f, 0.77f, 0.85f, 1f };
		public static readonly int[] FpsLimits = { 0, 30, 60, 120, 144, 240 };

		private static SceneTree _tree;

		/// <summary>Copie des réglages (la page Graphismes la reprend si on la quitte sans appliquer).</summary>
		public readonly record struct State(bool Fullscreen, bool VSync, int MaxFps, Preset Quality, float RenderScale, AntiAliasing Aa, Level Shadows, Level Effects);

		public static State Current => new(Fullscreen, VSync, MaxFps, Quality, RenderScale, Aa, Shadows, Effects);

		public static void Restore(State s)
		{
			Fullscreen = s.Fullscreen; VSync = s.VSync; MaxFps = s.MaxFps; Quality = s.Quality;
			RenderScale = s.RenderScale; Aa = s.Aa; Shadows = s.Shadows; Effects = s.Effects;
		}

		/// <summary>Au démarrage : relit les réglages, les applique et suit les scènes chargées ensuite (cartes, lumières, environnements).</summary>
		public static void Install(SceneTree tree)
		{
			_tree = tree;
			Load();
			ApplyAll();
			tree.NodeAdded += node =>
			{
				if (node is Light3D || node is WorldEnvironment) Callable.From(() => ApplyNode(node)).CallDeferred();
			};
		}

		/// <summary>Préréglage : fixe les réglages fins correspondants.</summary>
		public static void SetPreset(Preset p)
		{
			Quality = p;
			switch (p)
			{
				case Preset.Low: RenderScale = 0.67f; Aa = AntiAliasing.Fxaa; Shadows = Level.Low; Effects = Level.Off; break;
				case Preset.Medium: RenderScale = 0.85f; Aa = AntiAliasing.Fxaa; Shadows = Level.Medium; Effects = Level.Low; break;
				case Preset.High: RenderScale = 1f; Aa = AntiAliasing.Msaa2; Shadows = Level.High; Effects = Level.High; break;
				case Preset.Ultra: RenderScale = 1f; Aa = AntiAliasing.Msaa4; Shadows = Level.High; Effects = Level.High; break;
			}
		}

		public static void Save()
		{
			var cfg = new ConfigFile();
			cfg.SetValue("display", "fullscreen", Fullscreen);
			cfg.SetValue("display", "vsync", VSync);
			cfg.SetValue("display", "max_fps", MaxFps);
			cfg.SetValue("quality", "preset", (int)Quality);
			cfg.SetValue("quality", "render_scale", RenderScale);
			cfg.SetValue("quality", "aa", (int)Aa);
			cfg.SetValue("quality", "shadows", (int)Shadows);
			cfg.SetValue("quality", "effects", (int)Effects);
			cfg.Save(Path);
		}

		private static void Load()
		{
			var cfg = new ConfigFile();
			if (cfg.Load(Path) != Error.Ok) return;
			Fullscreen = (bool)cfg.GetValue("display", "fullscreen", Fullscreen);
			VSync = (bool)cfg.GetValue("display", "vsync", VSync);
			MaxFps = (int)cfg.GetValue("display", "max_fps", MaxFps);
			Quality = (Preset)Math.Clamp((int)cfg.GetValue("quality", "preset", (int)Quality), 0, 4);
			RenderScale = Mathf.Clamp((float)cfg.GetValue("quality", "render_scale", RenderScale), 0.5f, 1f);
			Aa = (AntiAliasing)Math.Clamp((int)cfg.GetValue("quality", "aa", (int)Aa), 0, 3);
			Shadows = (Level)Math.Clamp((int)cfg.GetValue("quality", "shadows", (int)Shadows), 0, 3);
			Effects = (Level)Math.Clamp((int)cfg.GetValue("quality", "effects", (int)Effects), 0, 3);
		}

		/// <summary>Applique tout : fenêtre, rendu, puis les lumières et environnements déjà chargés.</summary>
		public static void ApplyAll()
		{
			ApplyDisplay();
			ApplyRendering();
			if (_tree != null) Walk(_tree.Root);
		}

		public static void ApplyDisplay()
		{
			var mode = DisplayServer.WindowGetMode();
			if (Fullscreen && mode != DisplayServer.WindowMode.Fullscreen && mode != DisplayServer.WindowMode.ExclusiveFullscreen)
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
			else if (!Fullscreen && (mode == DisplayServer.WindowMode.Fullscreen || mode == DisplayServer.WindowMode.ExclusiveFullscreen))
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
			DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
			Engine.MaxFps = MaxFps;
		}

		private static void ApplyRendering()
		{
			if (_tree == null) return;
			var vp = _tree.Root;
			vp.Scaling3DMode = RenderScale < 0.99f ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
			vp.Scaling3DScale = RenderScale;
			vp.Msaa3D = Aa == AntiAliasing.Msaa4 ? Viewport.Msaa.Msaa4X : Aa == AntiAliasing.Msaa2 ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled;
			vp.ScreenSpaceAA = Aa == AntiAliasing.Fxaa ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;

			// taille des cartes d'ombres et douceur de leurs bords
			int directional = Shadows switch { Level.Low => 1024, Level.Medium => 2048, _ => 4096 };
			RenderingServer.DirectionalShadowAtlasSetSize(directional, true);
			vp.PositionalShadowAtlasSize = Shadows switch { Level.Off or Level.Low => 0, Level.Medium => 2048, _ => 4096 };
			var soft = Shadows switch { Level.Off or Level.Low => RenderingServer.ShadowQuality.Hard, Level.Medium => RenderingServer.ShadowQuality.SoftLow, _ => RenderingServer.ShadowQuality.SoftMedium };
			RenderingServer.DirectionalSoftShadowFilterSetQuality(soft);
			RenderingServer.PositionalSoftShadowFilterSetQuality(soft);
		}

		private static void Walk(Node node)
		{
			ApplyNode(node);
			foreach (var child in node.GetChildren()) Walk(child);
		}

		/// <summary>Une lumière ou un environnement du monde principal (les aperçus des menus, dans leur propre monde, ne sont pas touchés).</summary>
		private static void ApplyNode(Node node)
		{
			if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree() || _tree == null || node.GetViewport() != _tree.Root) return;
			if (node is DirectionalLight3D sun)
			{
				bool shadow = Original(sun, "oe_shadow", sun.ShadowEnabled);
				float distance = Original(sun, "oe_shadow_distance", sun.DirectionalShadowMaxDistance);
				sun.ShadowEnabled = shadow && Shadows != Level.Off;
				sun.DirectionalShadowMaxDistance = distance * (Shadows switch { Level.Low => 0.4f, Level.Medium => 0.7f, _ => 1f });
			}
			else if (node is Light3D light)
			{
				// lampes et projecteurs : leurs ombres coûtent cher, gardées à partir de la qualité moyenne
				bool shadow = Original(light, "oe_shadow", light.ShadowEnabled);
				light.ShadowEnabled = shadow && Shadows >= Level.Medium;
			}
			else if (node is WorldEnvironment we && we.Environment is Godot.Environment env)
			{
				env.GlowEnabled = Original(env, "oe_glow", env.GlowEnabled) && Effects >= Level.Low;
				env.SsaoEnabled = Original(env, "oe_ssao", env.SsaoEnabled) && Effects >= Level.Medium;
				env.SsilEnabled = Original(env, "oe_ssil", env.SsilEnabled) && Effects >= Level.High;
				env.SsrEnabled = Original(env, "oe_ssr", env.SsrEnabled) && Effects >= Level.High;
				env.SdfgiEnabled = Original(env, "oe_sdfgi", env.SdfgiEnabled) && Effects >= Level.High;
				env.VolumetricFogEnabled = Original(env, "oe_vfog", env.VolumetricFogEnabled) && Effects >= Level.High;
			}
		}

		/// <summary>Valeur d'origine (celle de la carte), retenue la première fois qu'on la change.</summary>
		private static bool Original(GodotObject o, string key, bool current)
		{
			if (!o.HasMeta(key)) o.SetMeta(key, current);
			return o.GetMeta(key).AsBool();
		}

		private static float Original(GodotObject o, string key, float current)
		{
			if (!o.HasMeta(key)) o.SetMeta(key, current);
			return (float)o.GetMeta(key).AsDouble();
		}
	}
}
