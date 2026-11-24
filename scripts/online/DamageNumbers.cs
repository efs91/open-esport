using System.Collections.Generic;
using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Dégâts infligés affichés sur la cible : un nombre par coup, à l'endroit touché, toujours visible (même derrière un décor),
	/// de la couleur du bouclier touché (blanc, bleu, violet, rouge) ou blanc sur la vie, plus gros pour un tir à la tête.
	/// Affiché en haut à gauche du point touché (décalage fixe à l'écran, quelle que soit la distance) pour ne pas cacher la cible ;
	/// il monte un peu puis s'efface.
	/// </summary>
	public partial class DamageNumbers : Node3D
	{
		private const float Life = 0.9f;
		/// <summary>Décalage à l'écran (en points du texte) : à gauche et au-dessus de la cible, puis la montée.</summary>
		private static readonly Vector2 Offset = new Vector2(-110f, 140f);
		private const float Rise = 60f;
		private readonly List<(Label3D label, float age, Vector3 from)> _items = new();
		private readonly RandomNumberGenerator _rng = new();

		public void Add(Vector3 at, int damage, Color color, bool head)
		{
			var label = new Label3D
			{
				Text = damage.ToString(),
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				NoDepthTest = true,
				FixedSize = true,
				PixelSize = 0.0009f,
				FontSize = head ? 64 : 52,
				OutlineSize = 14,
				Modulate = color,
				OutlineModulate = new Color(0, 0, 0, 0.85f),
				Offset = Offset,
				RenderPriority = 10,
				OutlineRenderPriority = 9,
			};
			// léger décalage : les coups rapprochés ne s'empilent pas exactement
			var from = at + new Vector3(_rng.RandfRange(-0.1f, 0.1f), _rng.RandfRange(0f, 0.15f), _rng.RandfRange(-0.1f, 0.1f));
			AddChild(label);
			label.GlobalPosition = from;
			_items.Add((label, 0f, from));
		}

		public override void _Process(double delta)
		{
			for (int i = _items.Count - 1; i >= 0; i--)
			{
				var (label, age, from) = _items[i];
				age += (float)delta;
				if (age >= Life || !IsInstanceValid(label)) { if (IsInstanceValid(label)) label.QueueFree(); _items.RemoveAt(i); continue; }
				float k = age / Life;
				label.Offset = Offset + new Vector2(0f, Rise * k);
				var c = label.Modulate; c.A = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f; label.Modulate = c;
				var o = label.OutlineModulate; o.A = 0.85f * c.A; label.OutlineModulate = o;
				_items[i] = (label, age, from);
			}
		}
	}
}
