using Godot;

namespace Openesport.Menus
{
	/// <summary>Petit anneau qui tourne : un contenu est en cours de chargement.</summary>
	public partial class LoadingRing : Control
	{
		private const float Radius = 11f, Width = 3f, TurnsPerSecond = 0.9f;

		public LoadingRing()
		{
			CustomMinimumSize = new Vector2(0, 36);
			SizeFlagsHorizontal = SizeFlags.ExpandFill;
			MouseFilter = MouseFilterEnum.Ignore;
		}

		public override void _Process(double delta) { if (IsVisibleInTree()) QueueRedraw(); }

		public override void _Draw()
		{
			// l'angle vient de l'horloge : l'anneau continue son tour même si le panneau est reconstruit
			float start = Time.GetTicksMsec() / 1000f * TurnsPerSecond % 1f * Mathf.Tau;
			DrawArc(Size / 2f, Radius, 0f, Mathf.Tau, 40, new Color(MenuStyle.Cyan, 0.15f), Width, true);
			DrawArc(Size / 2f, Radius, start, start + Mathf.Tau * 0.3f, 20, MenuStyle.Cyan, Width, true);
		}
	}
}
