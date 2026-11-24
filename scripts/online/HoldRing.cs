using Godot;

namespace Openesport.Online
{
	/// <summary>Anneau de progression autour du viseur : il se remplit pendant qu'on maintient la touche (échange d'arme ou de bouclier).</summary>
	public partial class HoldRing : Control
	{
		private float _progress;
		public Color Tint = Colors.White;

		/// <summary>0 = caché, 1 = plein.</summary>
		public float Progress
		{
			get => _progress;
			set
			{
				value = Mathf.Clamp(value, 0f, 1f);
				if (Mathf.IsEqualApprox(value, _progress)) return;
				_progress = value;
				Visible = value > 0f;
				QueueRedraw();
			}
		}

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Ignore;
			Visible = false;
		}

		public override void _Draw()
		{
			var c = Size / 2f;
			const float radius = 30f, width = 5f;
			DrawArc(c, radius, 0f, Mathf.Tau, 64, new Color(0f, 0f, 0f, 0.45f), width + 3f, true);
			DrawArc(c, radius, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Mathf.Tau * _progress, 64, Tint, width, true);
		}
	}
}
