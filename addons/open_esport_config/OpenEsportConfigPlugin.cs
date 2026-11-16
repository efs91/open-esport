#if TOOLS
using Godot;

namespace Openesport.EditorTools
{
	/// <summary>
	/// Panneau de configuration du jeu, dans l'éditeur Godot (onglet « Open Esport » à côté de 2D, 3D, Script) :
	/// les réglages de jeu (armes, soins, boucliers, déplacement, butin, bots…), les zones du battle royale avec aperçu, et les objets.
	/// Tout ce qui est réglable passe par là : aucun fichier à éditer à la main.
	/// </summary>
	[Tool]
	public partial class OpenEsportConfigPlugin : EditorPlugin
	{
		private ConfigPanel _panel;

		public override bool _HasMainScreen() => true;
		public override string _GetPluginName() => "Open Esport";
		public override Texture2D _GetPluginIcon() => EditorInterface.Singleton.GetEditorTheme().GetIcon("Tools", "EditorIcons");

		public override void _EnterTree()
		{
			_panel = new ConfigPanel { Name = "OpenEsportPanel" };
			var screen = EditorInterface.Singleton.GetEditorMainScreen();
			screen.AddChild(_panel);
			_panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			_MakeVisible(false);
			// l'éditeur annonce l'écran affiché (2D, 3D, Script, Open Esport…) : on n'est visible que sur notre onglet, jamais à côté de la vue 3D
			MainScreenChanged += name => { if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.Visible = name == _GetPluginName(); };
		}

		public override void _ExitTree()
		{
			_panel?.QueueFree();
			_panel = null;
		}

		public override void _MakeVisible(bool visible)
		{
			if (_panel != null) _panel.Visible = visible;
		}
	}
}
#endif
