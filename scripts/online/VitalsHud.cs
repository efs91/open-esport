using Godot;
using Openesport.Inventory;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Vie et bouclier du joueur local, en bas à gauche : barre de bouclier segmentée (couleur du niveau) au-dessus de la barre de vie.
	/// Pendant un soin, les barres se remplissent en direct jusqu'à la valeur finale.
	/// </summary>
	public partial class Vitals : Control
	{
		public const float W = 320f, H = 74f;

		private float _health = 100f, _shield, _shownHealth = 100f, _shownShield;
		private int _tier;
		private byte _using;
		private float _progress;
		private double _time, _hitFlash;

		public override void _Ready() { CustomMinimumSize = new Vector2(W, H); }

		public void Set(float health, float shield, int tier, byte usingId, float progress)
		{
			if (health < _health - 0.5f || shield < _shield - 0.5f) _hitFlash = 0.35;
			_health = health; _shield = shield; _tier = tier; _using = usingId; _progress = progress;
		}

		public override void _Process(double delta)
		{
			_time += delta;
			if (_hitFlash > 0) _hitFlash -= delta;
			// valeur affichée : celle du serveur, plus ce qu'un soin en cours est en train d'apporter (le serveur l'applique à la fin)
			float targetH = _health, targetS = _shield;
			if (_using != 0)
			{
				var e = Consumables.Of(_using);
				float maxS = Consumables.ShieldMaxOf(_tier);
				float finalH = e.Heal <= 0f ? _health : Mathf.Min(100f, _health + (e.Heal >= ConsumableEffect.Full ? 100f : e.Heal));
				float finalS = e.Shield <= 0f || maxS <= 0f ? _shield : Mathf.Min(maxS, _shield + (e.Shield >= ConsumableEffect.Full ? maxS : e.Shield));
				targetH = Mathf.Lerp(_health, finalH, _progress);
				targetS = Mathf.Lerp(_shield, finalS, _progress);
			}
			float k = 1f - Mathf.Exp(-12f * (float)delta);
			_shownHealth = Mathf.Lerp(_shownHealth, targetH, k);
			_shownShield = Mathf.Lerp(_shownShield, targetS, k);
			QueueRedraw();
		}

		public override void _Draw()
		{
			var font = ThemeDB.FallbackFont;
			float maxS = Consumables.ShieldMaxOf(_tier);
			var tc = _tier < 0 ? new Color(0.4f, 0.4f, 0.45f) : ItemTierStyle.ColorOf(_tier);

			// --- bouclier : un segment par 25 points
			const float sy = 6f, sh = 16f;
			if (maxS > 0f)
			{
				int segs = Mathf.CeilToInt(maxS / 25f);
				float gap = 3f, segW = (W - 70f - gap * (segs - 1)) / segs;
				for (int i = 0; i < segs; i++)
				{
					float x = i * (segW + gap);
					float fill = Mathf.Clamp((_shownShield - i * 25f) / 25f, 0f, 1f);
					DrawRect(new Rect2(x, sy, segW, sh), new Color(0.05f, 0.07f, 0.1f, 0.78f));
					if (fill > 0f)
					{
						var col = tc;
						if (_using != 0 && Consumables.Of(_using).Shield > 0f)   // remplissage : le segment en cours pulse
							col = tc.Lerp(Colors.White, 0.25f + 0.25f * Mathf.Sin((float)_time * 9f + i));
						DrawRect(new Rect2(x, sy, segW * fill, sh), col);
					}
					DrawRect(new Rect2(x, sy, segW, sh), new Color(tc.R, tc.G, tc.B, 0.7f), false, 1.5f);
				}
				DrawString(font, new Vector2(W - 62f, sy + sh - 2f), $"{Mathf.CeilToInt(_shownShield)}", HorizontalAlignment.Left, -1, 17, tc);
			}
			else
				DrawString(font, new Vector2(0, sy + sh - 2f), "Pas de bouclier", HorizontalAlignment.Left, -1, 14, new Color(0.7f, 0.7f, 0.75f));

			// --- vie
			const float hy = 32f, hh = 20f;
			float hw = W - 70f;
			DrawRect(new Rect2(0, hy, hw, hh), new Color(0.05f, 0.07f, 0.1f, 0.78f));
			float frac = Mathf.Clamp(_shownHealth / 100f, 0f, 1f);
			var hc = frac > 0.5f ? new Color(0.35f, 0.9f, 0.4f) : frac > 0.25f ? new Color(1f, 0.82f, 0.25f) : new Color(1f, 0.3f, 0.25f);
			if (_using != 0 && Consumables.Of(_using).Heal > 0f) hc = hc.Lerp(Colors.White, 0.2f + 0.2f * Mathf.Sin((float)_time * 9f));
			DrawRect(new Rect2(0, hy, hw * frac, hh), hc);
			// reflet liquide : une ondulation qui défile sur le bord pendant un soin
			if (_using != 0 && Consumables.Of(_using).Heal > 0f)
				for (int i = 0; i < 18; i++)
				{
					float x = hw * frac - 6f + Mathf.Sin((float)_time * 8f + i * 0.9f) * 3f;
					float y = hy + (i + 0.5f) * hh / 18f;
					DrawRect(new Rect2(Mathf.Clamp(x, 0, hw - 2), y, 3f, 1.5f), new Color(1, 1, 1, 0.5f));
				}
			DrawRect(new Rect2(0, hy, hw, hh), new Color(1, 1, 1, 0.6f), false, 1.5f);
			DrawString(font, new Vector2(hw + 8f, hy + hh - 3f), $"{Mathf.CeilToInt(_shownHealth)}", HorizontalAlignment.Left, -1, 19, hc);
			if (_hitFlash > 0) DrawRect(new Rect2(-4, 0, W + 8, H), new Color(1f, 0.1f, 0.1f, (float)(_hitFlash / 0.35) * 0.25f));
		}
	}

	/// <summary>
	/// Barre d'utilisation d'un soin, au centre en bas : un flacon se remplit au fil des secondes (liquide qui ondule, bulles),
	/// avec le nom du soin, le temps restant et le rappel pour annuler.
	/// </summary>
	public partial class UseBar : Control
	{
		public const float W = 380f, H = 58f;
		private byte _id;
		private float _progress;
		private double _time;

		public override void _Ready() { CustomMinimumSize = new Vector2(W, H); Visible = false; }

		public void Set(byte usingId, float progress)
		{
			_id = usingId; _progress = progress;
			Visible = usingId != 0;
		}

		public override void _Process(double delta)
		{
			_time += delta;
			if (Visible) QueueRedraw();
		}

		public override void _Draw()
		{
			if (_id == 0) return;
			var net = ItemDefs.Get(_id);
			var def = net == null ? null : ItemDatabase.Get(net.Name);
			var e = Consumables.Of(_id);
			bool heal = e.Heal > 0f, shield = e.Shield > 0f;
			var colA = heal ? new Color(1f, 0.25f, 0.2f) : new Color(0.25f, 0.55f, 1f);
			var colB = shield ? new Color(0.25f, 0.55f, 1f) : colA;
			var font = ThemeDB.FallbackFont;

			DrawRect(new Rect2(0, 0, W, H), new Color(0.03f, 0.05f, 0.08f, 0.82f));
			DrawRect(new Rect2(0, 0, W, H), new Color(1, 1, 1, 0.55f), false, 2f);

			// flacon à gauche
			var bx = 10f; var by = 6f; var bw = 30f; var bh = H - 12f;
			DrawRect(new Rect2(bx, by, bw, bh), new Color(0.8f, 0.92f, 1f, 0.16f));
			float level = bh * _progress;
			DrawRect(new Rect2(bx + 2, by + bh - level, bw - 4, level), colA.Lerp(colB, 0.5f));
			for (int i = 0; i < 4; i++)   // bulles qui montent
			{
				float t = (float)((_time * 0.7 + i * 0.27) % 1.0);
				DrawCircle(new Vector2(bx + 6f + (i * 7) % (bw - 10f), by + bh - t * level), 1.6f + (i % 2), new Color(1, 1, 1, 0.55f * (1f - t)));
			}
			DrawRect(new Rect2(bx, by, bw, bh), new Color(1, 1, 1, 0.8f), false, 1.5f);

			// barre de progression, liquide qui ondule au bord
			float x0 = 56f, w = W - x0 - 12f, y0 = 28f, h = 16f;
			DrawRect(new Rect2(x0, y0, w, h), new Color(0.05f, 0.07f, 0.1f, 0.9f));
			float fw = w * _progress;
			for (int px = 0; px < (int)fw; px += 2)
			{
				float u = fw <= 1f ? 0f : px / fw;
				DrawRect(new Rect2(x0 + px, y0, 2, h), colA.Lerp(colB, shield && heal ? (px / w) : 0f).Lightened(0.12f * Mathf.Sin((float)_time * 7f - px * 0.15f)));
			}
			for (int i = 0; i < 8; i++)
			{
				float x = x0 + fw - 4f + Mathf.Sin((float)_time * 9f + i) * 3f;
				DrawRect(new Rect2(Mathf.Clamp(x, x0, x0 + w - 2), y0 + i * h / 8f, 2.5f, 1.5f), new Color(1, 1, 1, 0.6f));
			}
			DrawRect(new Rect2(x0, y0, w, h), new Color(1, 1, 1, 0.6f), false, 1.5f);

			float total = e.Seconds;
			DrawString(font, new Vector2(x0, 20f), $"{def?.DisplayName ?? "Soin"}", HorizontalAlignment.Left, -1, 17, Colors.White);
			string left = $"{Mathf.Max(0f, total * (1f - _progress)):F1} s";
			DrawString(font, new Vector2(x0 + w - 48f, 20f), left, HorizontalAlignment.Left, -1, 17, new Color(1f, 0.95f, 0.6f));
			DrawString(font, new Vector2(x0, H - 3f), "tirer ou la même touche : annuler", HorizontalAlignment.Left, -1, 11, new Color(0.75f, 0.8f, 0.85f));
		}
	}
}
