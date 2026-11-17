using System;
using System.Collections.Generic;
using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Menu circulaire (comme la roue de ping d'Apex ou les roues d'équipement de Fortnite et de CoD) : on maintient la touche, on déplace la souris vers la part voulue,
	/// on relâche la touche pour valider (ou clic gauche : valider et utiliser tout de suite). Échap ou clic droit annulent ; relâcher au centre aussi.
	/// Pendant le menu, la souris pilote le menu et non la caméra, et le tir est suspendu.
	/// </summary>
	public partial class RadialMenu : CanvasLayer
	{
		public struct Slice
		{
			public string Label, Detail;
			/// <summary>Miniature de l'objet (voir ItemIcons), dessinée au-dessus du texte.</summary>
			public Texture2D Icon;
			public Color Color;
			public bool Enabled;
		}

		public enum Result { Chosen, Tap, Cancelled }

		public bool IsOpen { get; private set; }
		public int Hover { get; private set; } = -1;

		/// <summary>Part validée (index) ; le second paramètre vaut vrai quand on a validé d'un clic gauche (« utiliser tout de suite »).</summary>
		public Action<int, bool> OnChoose;

		private readonly List<Slice> _slices = new();
		private string _title = "", _hint = "";
		private Vector2 _cursor;
		private double _openedAt;
		private Canvas _canvas;

		private const float Outer = 215f, Inner = 86f, DeadZone = 38f, CursorMax = 150f;

		public RadialMenu() { Layer = 130; }

		public override void _Ready()
		{
			_canvas = new Canvas { Menu = this, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(_canvas);
		}

		public override void _ExitTree()
		{
			if (IsOpen) Openesport_Unblock();
		}

		private static void Openesport_Unblock() { InputManager.UiBlock = false; }

		public void Open(string title, string hint, List<Slice> slices)
		{
			_title = title; _hint = hint;
			_slices.Clear(); _slices.AddRange(slices);
			_cursor = Vector2.Zero; Hover = -1;
			_openedAt = Time.GetTicksMsec() / 1000.0;
			IsOpen = true;
			_canvas.Visible = true;
			InputManager.UiBlock = true;
			_canvas.QueueRedraw();
		}

		public void Close()
		{
			if (!IsOpen) return;
			IsOpen = false;
			_canvas.Visible = false;
			InputManager.UiBlock = false;
			InputManager.ArmClickGuard();          // le clic qui a validé ne doit pas tirer
		}

		/// <summary>La touche du menu est relâchée : valide la part survolée. Un appui bref sans choix (« tap ») est signalé à l'appelant, qui peut y associer un raccourci.</summary>
		public Result Release()
		{
			if (!IsOpen) return Result.Cancelled;
			int h = Hover;
			bool brief = Time.GetTicksMsec() / 1000.0 - _openedAt < 0.22;
			bool valid = h >= 0 && _slices[h].Enabled;
			Close();
			if (valid) { OnChoose?.Invoke(h, false); return Result.Chosen; }
			return h < 0 && brief ? Result.Tap : Result.Cancelled;
		}

		public override void _Input(InputEvent e)
		{
			if (!IsOpen) return;
			if (e is InputEventMouseMotion mm)
			{
				_cursor += mm.Relative;
				if (_cursor.Length() > CursorMax) _cursor = _cursor.Normalized() * CursorMax;
				UpdateHover();
				GetViewport().SetInputAsHandled();
			}
			else if (e is InputEventMouseButton { Pressed: true } mb)
			{
				if (mb.ButtonIndex == MouseButton.Left)
				{
					int h = Hover;
					if (h >= 0 && _slices[h].Enabled) { Close(); OnChoose?.Invoke(h, true); }
				}
				else if (mb.ButtonIndex == MouseButton.Right) Close();
				GetViewport().SetInputAsHandled();
			}
			else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
			{
				Close();
				GetViewport().SetInputAsHandled();
			}
		}

		private void UpdateHover()
		{
			int old = Hover;
			if (_cursor.Length() < DeadZone || _slices.Count == 0) Hover = -1;
			else
			{
				float step = Mathf.Tau / _slices.Count;
				float angle = Mathf.Atan2(_cursor.X, -_cursor.Y);       // 0 = en haut, sens horaire
				Hover = ((int)Mathf.Floor((angle + step / 2f) / step) % _slices.Count + _slices.Count) % _slices.Count;
			}
			if (Hover != old) _canvas.QueueRedraw();
			else if (IsOpen) _canvas.QueueRedraw();
		}

		private partial class Canvas : Control
		{
			public RadialMenu Menu;

			private static Vector2 Pt(Vector2 c, float angle, float r) => c + new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * r;

			public override void _Draw()
			{
				var m = Menu;
				if (m == null || !m.IsOpen) return;
				var font = ThemeDB.FallbackFont;
				var c = Size * 0.5f;
				DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.35f));
				int n = m._slices.Count;
				float step = Mathf.Tau / Mathf.Max(1, n), gap = 0.025f;
				for (int i = 0; i < n; i++)
				{
					var s = m._slices[i];
					float a0 = i * step - step / 2f + gap, a1 = i * step + step / 2f - gap;
					var poly = new List<Vector2>();
					const int seg = 14;
					for (int k = 0; k <= seg; k++) poly.Add(Pt(c, a0 + (a1 - a0) * k / seg, Outer));
					for (int k = seg; k >= 0; k--) poly.Add(Pt(c, a0 + (a1 - a0) * k / seg, Inner));
					bool hot = i == m.Hover;
					var fill = !s.Enabled ? new Color(0.08f, 0.09f, 0.11f, 0.78f)
						: hot ? new Color(s.Color.R, s.Color.G, s.Color.B, 0.88f) : new Color(0.08f, 0.1f, 0.14f, 0.84f);
					DrawColoredPolygon(poly.ToArray(), fill);
					var closed = new List<Vector2>(poly) { poly[0] };
					var edge = closed.ToArray();
					DrawPolyline(edge, hot ? Colors.White : new Color(1, 1, 1, 0.28f), hot ? 3f : 1.5f, true);
					var mid = Pt(c, i * step, (Outer + Inner) * 0.5f);
					var text = !s.Enabled ? new Color(1, 1, 1, 0.32f) : hot ? Colors.Black : Colors.White;
					float dy = s.Icon != null ? 30f : 0f;
					if (s.Icon != null) DrawTextureRect(s.Icon, new Rect2(mid + new Vector2(-30, -58), new Vector2(60, 60)), false, !s.Enabled ? new Color(1, 1, 1, 0.3f) : Colors.White);
					DrawString(font, mid + new Vector2(-80, -4 + dy), s.Label, HorizontalAlignment.Center, 160, 20, text);
					if (!string.IsNullOrEmpty(s.Detail))
						DrawString(font, mid + new Vector2(-80, 18 + dy), s.Detail, HorizontalAlignment.Center, 160, 18, !s.Enabled ? new Color(1, 1, 1, 0.3f) : hot ? new Color(0, 0, 0, 0.8f) : s.Color);
				}
				// centre : titre, ou rappel de la part survolée
				DrawCircle(c, Inner - 8f, new Color(0.04f, 0.05f, 0.08f, 0.9f));
				DrawArc(c, Inner - 8f, 0, Mathf.Tau, 64, new Color(1, 1, 1, 0.4f), 2f, true);
				string head = m.Hover >= 0 ? m._slices[m.Hover].Label : m._title;
				DrawString(font, c + new Vector2(-70, -4), head, HorizontalAlignment.Center, 140, 22, Colors.White);
				DrawString(font, c + new Vector2(-70, 22), m._hint, HorizontalAlignment.Center, 140, 13, new Color(1, 1, 1, 0.65f));
				// curseur
				DrawLine(c, c + m._cursor, new Color(1, 1, 1, 0.35f), 2f, true);
				DrawCircle(c + m._cursor, 8f, new Color(0, 0, 0, 0.8f));
				DrawCircle(c + m._cursor, 5.5f, Colors.White);
			}
		}
	}
}
