using Godot;
using System.Collections.Generic;

namespace Openesport.Online
{
	/// <summary>Interface propre au mode en ligne : état de connexion, marqueur de touche, dégâts reçus, éliminations et équipe.</summary>
	public partial class OnlineHud : CanvasLayer
	{
		private Label _status, _hit, _dead;
		private ColorRect _flash;
		private VBoxContainer _feed, _team;
		private Minimap _radar;
		private FullMap _fullMap;
		private Vitals _vitals;
		private Label _hint, _flight, _stats, _downText, _zoneLabel, _zoneWarn;
		private ColorRect _zoneVignette;
		private ProgressBar _reviveBar;
		private Control _endPanel;
		private Label _endTitle, _endDetail;
		private ColorRect _downVignette;
		/// <summary>Appelé par le bouton « Retour au menu » de l'écran de fin de partie.</summary>
		public System.Action OnLeave;
		private bool _loading;
		private UseBar _useBar;
		private double _hitTime, _flashTime;
		private Color _flashColor = new Color(0.8f, 0.05f, 0.05f);
		private readonly List<(Label label, double until)> _feedItems = new();

		public override void _Ready()
		{
			AddToGroup("game_hud");
			Layer = 3;
			var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);

			_flash = new ColorRect { Color = new Color(0.8f, 0.05f, 0.05f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
			_flash.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(_flash);

			_status = MakeLabel(root, 22, Colors.White);
			_status.AnchorLeft = 0; _status.AnchorRight = 1; _status.OffsetTop = 6; _status.OffsetBottom = 34;
			_status.HorizontalAlignment = HorizontalAlignment.Center;

			_hit = MakeLabel(root, 34, Colors.White);
			_hit.Text = "✕";
			_hit.AnchorLeft = 0.5f; _hit.AnchorRight = 0.5f; _hit.AnchorTop = 0.5f; _hit.AnchorBottom = 0.5f;
			_hit.OffsetLeft = -20; _hit.OffsetRight = 20; _hit.OffsetTop = -22; _hit.OffsetBottom = 22;
			_hit.HorizontalAlignment = HorizontalAlignment.Center;
			_hit.Modulate = new Color(1, 1, 1, 0);

			_dead = MakeLabel(root, 40, new Color(1f, 0.4f, 0.35f));
			_dead.AnchorLeft = 0; _dead.AnchorRight = 1; _dead.AnchorTop = 0.35f; _dead.AnchorBottom = 0.35f; _dead.OffsetBottom = 60;
			_dead.HorizontalAlignment = HorizontalAlignment.Center;
			_dead.Visible = false;

			_feed = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.AddChild(_feed);
			_feed.AnchorLeft = 1; _feed.AnchorRight = 1; _feed.OffsetLeft = -430; _feed.OffsetRight = -14; _feed.OffsetTop = 16 + Minimap.Size + 60;
			_feed.Alignment = BoxContainer.AlignmentMode.Begin;

			// minimap en haut à droite (dessin de la carte autour du joueur) et grande carte sur M
			_radar = new Minimap { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.AddChild(_radar);
			_radar.AnchorLeft = 1; _radar.AnchorRight = 1; _radar.AnchorTop = 0; _radar.AnchorBottom = 0;
			_radar.OffsetLeft = -16 - Minimap.Size; _radar.OffsetRight = -16; _radar.OffsetTop = 16; _radar.OffsetBottom = 16 + Minimap.Size;
			// vie + bouclier en bas à gauche, barre d'utilisation d'un soin au centre en bas
			_vitals = new Vitals { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.AddChild(_vitals);
			_vitals.AnchorLeft = 0; _vitals.AnchorRight = 0; _vitals.AnchorTop = 1; _vitals.AnchorBottom = 1;
			_vitals.OffsetLeft = 22; _vitals.OffsetRight = 22 + Vitals.W; _vitals.OffsetTop = -150; _vitals.OffsetBottom = -150 + Vitals.H;
			_useBar = new UseBar { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.AddChild(_useBar);
			_useBar.AnchorLeft = 0.5f; _useBar.AnchorRight = 0.5f; _useBar.AnchorTop = 1; _useBar.AnchorBottom = 1;
			_useBar.OffsetLeft = -UseBar.W / 2f; _useBar.OffsetRight = UseBar.W / 2f; _useBar.OffsetTop = -190; _useBar.OffsetBottom = -190 + UseBar.H;
			_hint = MakeLabel(root, 22, Colors.White);
			_hint.AnchorLeft = 0; _hint.AnchorRight = 1; _hint.AnchorTop = 0.62f; _hint.AnchorBottom = 0.62f; _hint.OffsetBottom = 34;
			_hint.HorizontalAlignment = HorizontalAlignment.Center;
			_flight = MakeLabel(root, 26, new Color(1f, 0.93f, 0.55f));
			_flight.AnchorLeft = 0; _flight.AnchorRight = 1; _flight.AnchorTop = 0; _flight.AnchorBottom = 0; _flight.OffsetTop = 52; _flight.OffsetBottom = 90;
			_flight.HorizontalAlignment = HorizontalAlignment.Center;
			// zone : voile violet et alerte quand on est dehors, minuteur sous la minimap
			_zoneVignette = new ColorRect { Color = new Color(0.45f, 0.1f, 0.8f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_zoneVignette.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(_zoneVignette);
			root.MoveChild(_zoneVignette, 0);
			_zoneWarn = MakeLabel(root, 28, new Color(0.85f, 0.55f, 1f));
			_zoneWarn.AnchorLeft = 0; _zoneWarn.AnchorRight = 1; _zoneWarn.AnchorTop = 0.2f; _zoneWarn.AnchorBottom = 0.2f; _zoneWarn.OffsetBottom = 70;
			_zoneWarn.HorizontalAlignment = HorizontalAlignment.Center;
			_zoneWarn.Visible = false;
			_zoneLabel = MakeLabel(root, 18, Colors.White);
			_zoneLabel.AnchorLeft = 1; _zoneLabel.AnchorRight = 1; _zoneLabel.OffsetLeft = -16 - Minimap.Size; _zoneLabel.OffsetRight = -16;
			_zoneLabel.OffsetTop = 16 + Minimap.Size + 4; _zoneLabel.OffsetBottom = 16 + Minimap.Size + 112;
			_zoneLabel.HorizontalAlignment = HorizontalAlignment.Right;
			// statistiques de la partie : vivants, éliminations, dégâts
			_stats = MakeLabel(root, 22, Colors.White);
			_stats.Position = new Vector2(16, 12);
			_stats.Visible = false;
			// à terre : voile rouge, compte à rebours avant la mort, barre de réanimation
			_downVignette = new ColorRect { Color = new Color(0.7f, 0.02f, 0.02f, 0.22f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_downVignette.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(_downVignette);
			root.MoveChild(_downVignette, 0);
			_downText = MakeLabel(root, 30, new Color(1f, 0.45f, 0.4f));
			_downText.AnchorLeft = 0; _downText.AnchorRight = 1; _downText.AnchorTop = 0.3f; _downText.AnchorBottom = 0.3f; _downText.OffsetBottom = 90;
			_downText.HorizontalAlignment = HorizontalAlignment.Center;
			_downText.Visible = false;
			_reviveBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			root.AddChild(_reviveBar);
			_reviveBar.AnchorLeft = 0.5f; _reviveBar.AnchorRight = 0.5f; _reviveBar.AnchorTop = 0.68f; _reviveBar.AnchorBottom = 0.68f;
			_reviveBar.OffsetLeft = -150; _reviveBar.OffsetRight = 150; _reviveBar.OffsetTop = 0; _reviveBar.OffsetBottom = 18;

			// écran de fin : victoire ou élimination, avec le bilan
			_endPanel = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
			_endPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(_endPanel);
			_endPanel.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.72f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
			var endCenter = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
			endCenter.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			_endPanel.AddChild(endCenter);
			var endBox = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
			endBox.AddThemeConstantOverride("separation", 14);
			endCenter.AddChild(endBox);
			_endTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
			_endTitle.AddThemeFontSizeOverride("font_size", 54);
			endBox.AddChild(_endTitle);
			_endDetail = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_endDetail.AddThemeFontSizeOverride("font_size", 24);
			endBox.AddChild(_endDetail);
			var leave = new Button { Text = "Retour au menu", CustomMinimumSize = new Vector2(0, 50) };
			leave.Pressed += () => OnLeave?.Invoke();
			endBox.AddChild(leave);

			_fullMap = new FullMap { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_fullMap.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(_fullMap);

			_team = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
			root.AddChild(_team);
			_team.OffsetLeft = 14; _team.OffsetTop = 120;
			_team.AddThemeConstantOverride("separation", 2);
		}

		private static Label MakeLabel(Control parent, int size, Color color)
		{
			var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
			l.AddThemeFontSizeOverride("font_size", size);
			l.AddThemeColorOverride("font_color", color);
			l.AddThemeColorOverride("font_outline_color", Colors.Black);
			l.AddThemeConstantOverride("outline_size", 6);
			parent.AddChild(l);
			return l;
		}

		/// <summary>Met à jour la minimap : ma position, mon cap (radians, 0 = nord = -Z) et les joueurs vivants alentour.</summary>
		public void SetRadar(Vector3 me, float yaw, List<MapMark> marks) { _radar.Set(me, yaw, marks); if (_fullMap.Visible) _fullMap.Set(me, yaw, marks); }

		/// <summary>Image de la carte et étendue du monde qu'elle couvre (mètres).</summary>
		public void SetMap(Texture2D tex, float minX, float maxX, float minZ, float maxZ) { _radar.SetMap(tex, minX, maxX, minZ, maxZ); _fullMap.SetMap(tex, minX, maxX, minZ, maxZ); }

		/// <summary>Trajectoire de l'avion (départ et arrivée, en mètres) sur la minimap et la grande carte.</summary>
		public void SetFlightPath(Vector3 from, Vector3 to, bool show) { _radar.SetFlight(from, to, show); _fullMap.SetFlight(from, to, show); }

		/// <summary>Écran de chargement : la carte en plein écran avec la trajectoire de l'avion, à la place d'un écran figé.</summary>
		public void SetLoading(bool on, string text = "")
		{
			_loading = on;
			_fullMap.Loading = on;
			_fullMap.LoadingText = text;
			_fullMap.Visible = on || _fullMap.Visible;
			if (!on) _fullMap.Visible = false;
			_fullMap.QueueRedraw();
		}

		/// <summary>La zone : cercle sûr actuel, zone suivante (si annoncée) et texte du minuteur ; <paramref name="outside"/> : le joueur est hors de la zone.</summary>
		public void SetZone(bool show, float cx, float cz, float r, bool nextVisible, float nx, float nz, float nr, string timer, bool outside, string warning)
		{
			_radar.SetZone(show, cx, cz, r, nextVisible, nx, nz, nr);
			_fullMap.SetZone(show, cx, cz, r, nextVisible, nx, nz, nr);
			if (_zoneLabel.Text != timer) _zoneLabel.Text = timer;
			_zoneLabel.Visible = show;
			_zoneWarn.Visible = show && outside;
			if (_zoneWarn.Text != warning) _zoneWarn.Text = warning;
			_zoneVignette.Visible = show && outside;
			if (outside) _zoneVignette.Color = new Color(0.45f, 0.1f, 0.8f, 0.14f + 0.08f * Mathf.Sin(Time.GetTicksMsec() / 180f));
		}

		/// <summary>Vivants, éliminations et dégâts (partie battle royale).</summary>
		public void SetMatchStats(bool show, int playersAlive, int teamsAlive, int teamSize, int kills, int damage)
		{
			_stats.Visible = show;
			if (!show) return;
			string alive = teamSize > 1 ? $"Vivants : {playersAlive}   (équipes : {teamsAlive})" : $"Vivants : {playersAlive}";
			string text = $"{alive}\nÉliminations : {kills}\nDégâts : {damage}";
			if (_stats.Text != text) _stats.Text = text;
		}

		/// <summary>À terre : temps restant avant la mort, réanimation en cours (0-100) ; ou réanimation possible d'un coéquipier à portée.</summary>
		public void SetDowned(bool down, int secondsLeft, int revive, bool canRevive)
		{
			_downVignette.Visible = down;
			_downText.Visible = down;
			if (down) _downText.Text = revive > 0 ? "Un coéquipier te relève…" : $"À TERRE — un coéquipier peut te relever\nIl te reste {secondsLeft} s";
			_reviveBar.Visible = revive > 0;
			_reviveBar.Value = revive;
		}

		/// <summary>Écran de fin (victoire, élimination ou partie terminée) avec le bilan ; la souris est libérée pour le bouton.</summary>
		public void ShowEnd(bool show, string title = "", string detail = "", Color? color = null)
		{
			if (show == _endPanel.Visible && (!show || _endTitle.Text == title)) return;
			_endPanel.Visible = show;
			if (!show) return;
			_endTitle.Text = title;
			_endTitle.AddThemeColorOverride("font_color", color ?? Colors.White);
			_endDetail.Text = detail;
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}

		/// <summary>Texte du largage (compte à rebours, consignes de chute).</summary>
		public void SetFlight(string text) { if (_flight.Text != text) _flight.Text = text; }

		public override void _UnhandledInput(InputEvent e)
		{
			if (_loading) return;
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M })
			{
				_fullMap.Visible = !_fullMap.Visible;
				GetViewport().SetInputAsHandled();
			}
		}

		/// <summary>Indication contextuelle au centre de l'écran (ex. « E : prendre la tyrolienne »).</summary>
		public void SetHint(string text) { if (_hint.Text != text) _hint.Text = text; }

		public void SetStatus(string text) { _status.Text = text; }

		public void HitMarker(bool kill)
		{
			_hit.Modulate = kill ? new Color(1f, 0.25f, 0.2f, 1f) : new Color(1f, 1f, 1f, 1f);
			_hitTime = kill ? 0.5 : 0.2;
		}

		/// <summary>Dégât reçu : flash rouge (la vie est touchée) ou bleu clair (le bouclier a tout absorbé).</summary>
		public void DamageFlash(bool shieldOnly = false) { _flashTime = 0.35; _flashColor = shieldOnly ? new Color(0.2f, 0.55f, 1f) : new Color(0.8f, 0.05f, 0.05f); }

		public void SetVitals(float health, float shield, int tier, byte usingId, float progress)
		{
			_vitals.Set(health, shield, tier, usingId, progress);
			_useBar.Set(usingId, progress);
		}

		public void SetDead(bool dead) { _dead.Visible = dead; _dead.Text = "ÉLIMINÉ — réapparition…"; }

		public void AddFeed(string text)
		{
			var l = MakeLabelIn(_feed, text);
			_feedItems.Add((l, Time.GetTicksMsec() / 1000.0 + 6.0));
			while (_feedItems.Count > 6) { _feedItems[0].label.QueueFree(); _feedItems.RemoveAt(0); }
		}

		private static Label MakeLabelIn(Container parent, string text)
		{
			var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = Control.MouseFilterEnum.Ignore };
			l.AddThemeFontSizeOverride("font_size", 16);
			l.AddThemeColorOverride("font_outline_color", Colors.Black);
			l.AddThemeConstantOverride("outline_size", 5);
			parent.AddChild(l);
			return l;
		}

		/// <summary>Liste des coéquipiers : nom et points de vie.</summary>
		public void SetTeam(IList<(string name, int health, bool bot)> mates)
		{
			while (_team.GetChildCount() > mates.Count + 1) _team.GetChild(_team.GetChildCount() - 1).QueueFree();
			while (_team.GetChildCount() < mates.Count + 1) MakeLabelIn(_team, "");
			((Label)_team.GetChild(0)).Text = mates.Count > 0 ? "ÉQUIPE" : "";
			((Label)_team.GetChild(0)).HorizontalAlignment = HorizontalAlignment.Left;
			for (int i = 0; i < mates.Count; i++)
			{
				var l = (Label)_team.GetChild(i + 1);
				l.HorizontalAlignment = HorizontalAlignment.Left;
				l.Text = $"{mates[i].name}   {mates[i].health}";
				l.Modulate = mates[i].health > 0 ? new Color(0.5f, 0.9f, 1f) : new Color(0.6f, 0.6f, 0.6f);
			}
		}

		public override void _Process(double delta)
		{
			if (_hitTime > 0)
			{
				_hitTime -= delta;
				_hit.Modulate = new Color(_hit.Modulate.R, _hit.Modulate.G, _hit.Modulate.B, Mathf.Clamp((float)(_hitTime / 0.2), 0f, 1f));
			}
			if (_flashTime > 0)
			{
				_flashTime -= delta;
				_flash.Color = new Color(_flashColor.R, _flashColor.G, _flashColor.B, Mathf.Clamp((float)(_flashTime / 0.35) * 0.35f, 0f, 0.35f));
			}
			double now = Time.GetTicksMsec() / 1000.0;
			for (int i = _feedItems.Count - 1; i >= 0; i--)
				if (now > _feedItems[i].until) { _feedItems[i].label.QueueFree(); _feedItems.RemoveAt(i); }
		}
	}

	/// <summary>Un point de la carte : joueur (équipier ou ennemi qui fait du bruit).</summary>
	public struct MapMark
	{
		public Vector3 Pos;
		public bool Friendly;
		public float Alpha;
		public string Name;
	}

	/// <summary>Dessin de la carte avec la conversion monde -> image (mètres vers pixels).</summary>
	public abstract partial class MapBase : Control
	{
		protected Texture2D Tex;
		protected float MinX, MaxX, MinZ, MaxZ;
		protected Vector3 Me;
		protected float Yaw;
		protected List<MapMark> Marks = new();

		public void SetMap(Texture2D tex, float minX, float maxX, float minZ, float maxZ) { Tex = tex; MinX = minX; MaxX = maxX; MinZ = minZ; MaxZ = maxZ; QueueRedraw(); }
		public void Set(Vector3 me, float yaw, List<MapMark> marks) { Me = me; Yaw = yaw; Marks = marks; QueueRedraw(); }
		protected float ZCx, ZCz, ZR, ZNx, ZNz, ZNr;
		protected bool ZShow, ZNextVisible;
		public void SetZone(bool show, float cx, float cz, float r, bool nextVisible, float nx, float nz, float nr)
		{ ZShow = show; ZCx = cx; ZCz = cz; ZR = r; ZNextVisible = nextVisible; ZNx = nx; ZNz = nz; ZNr = nr; QueueRedraw(); }

		/// <summary>La zone : tout ce qui est hors du cercle sûr est teinté de violet, le cercle sûr est bleu, la zone suivante (annoncée) est blanche.</summary>
		protected void DrawZone(System.Func<float, float, Vector2> w2p, float pxPerMeter)
		{
			if (!ZShow) return;
			var c = w2p(ZCx, ZCz); float r = ZR * pxPerMeter;
			const float Big = 6000f;
			if (r > 0.5f) DrawArc(c, r + Big * 0.5f, 0f, Mathf.Tau, 160, new Color(0.45f, 0.1f, 0.8f, 0.28f), Big, false);
			else DrawRect(new Rect2(c - new Vector2(Big, Big) * 0.5f, new Vector2(Big, Big)), new Color(0.45f, 0.1f, 0.8f, 0.28f));
			if (r > 0.5f) DrawArc(c, r, 0f, Mathf.Tau, 128, new Color(0.25f, 0.6f, 1f, 0.95f), 3f, true);
			if (ZNextVisible && ZNr > 0.5f / pxPerMeter) DrawArc(w2p(ZNx, ZNz), ZNr * pxPerMeter, 0f, Mathf.Tau, 128, new Color(1f, 1f, 1f, 0.95f), 2.5f, true);
		}

		protected Vector3 FlightFrom, FlightTo;
		protected bool ShowFlight;
		public void SetFlight(Vector3 from, Vector3 to, bool show) { FlightFrom = from; FlightTo = to; ShowFlight = show; QueueRedraw(); }

		/// <summary>Trajectoire de l'avion : trait pointillé jaune avec des flèches dans le sens du vol. <paramref name="w2p"/> convertit un point du monde en pixels.</summary>
		protected void DrawFlight(System.Func<Vector3, Vector2> w2p, float width, float dash)
		{
			if (!ShowFlight) return;
			var a = w2p(FlightFrom); var b = w2p(FlightTo);
			var d = b - a; float len = d.Length();
			if (len < 1f) return;
			var u = d / len; var n = new Vector2(-u.Y, u.X);
			var col = new Color(1f, 0.9f, 0.3f, 0.95f);
			for (float t = 0; t < len; t += dash * 2f)
				DrawLine(a + u * t, a + u * Mathf.Min(t + dash, len), col, width, true);
			for (float t = dash * 6f; t < len - dash * 3f; t += dash * 12f)
			{
				var c = a + u * t;
				DrawColoredPolygon(new[] { c + u * dash * 1.6f, c - u * dash * 0.6f + n * dash, c - u * dash * 0.6f - n * dash }, col);
			}
		}
	}

	/// <summary>Minimap carrée en haut à droite : le dessin de la carte autour du joueur (nord en haut). Équipiers en bleu, ennemis bruyants en rouge.</summary>
	public partial class Minimap : MapBase
	{
		public const float Size = 210f;
		private const float HalfRange = 105f;        // mètres visibles de chaque côté

		public override void _Ready() { ClipContents = true; }

		public override void _Draw()
		{
			var c = new Vector2(Size, Size) * 0.5f;
			DrawRect(new Rect2(0, 0, Size, Size), new Color(0.04f, 0.06f, 0.08f, 0.9f));
			float k = Size * 0.5f / HalfRange;                       // pixels par mètre
			if (Tex != null)
			{
				DrawSetTransform(c, 0f, new Vector2(k, k));           // le monde en mètres, nord en haut (les noms restent lisibles)
				DrawTextureRect(Tex, new Rect2(MinX - Me.X, MinZ - Me.Z, MaxX - MinX, MaxZ - MinZ), false);
				DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
			}
			DrawZone((x, z) => c + new Vector2(x - Me.X, z - Me.Z) * k, k);
			DrawFlight(w => c + new Vector2(w.X - Me.X, w.Z - Me.Z) * k, 2.5f, 8f);
			foreach (var m in Marks)
			{
				var rel = m.Pos - Me;
				var p = new Vector2(rel.X, rel.Z) * k;
				var half = Size * 0.5f - 6f;
				if (Mathf.Abs(p.X) > half || Mathf.Abs(p.Y) > half)   // hors cadre : collé au bord
					p = p * (half / Mathf.Max(Mathf.Abs(p.X), Mathf.Abs(p.Y)));
				var col = m.Friendly ? new Color(0.35f, 0.78f, 1f, m.Alpha) : new Color(1f, 0.22f, 0.18f, m.Alpha);
				DrawCircle(c + p, 6.5f, new Color(0, 0, 0, 0.75f * m.Alpha));
				DrawCircle(c + p, 4.8f, col);
				if (!m.Friendly) DrawArc(c + p, 9f + (1f - m.Alpha) * 6f, 0, Mathf.Tau, 20, new Color(1f, 0.3f, 0.2f, m.Alpha * 0.6f), 1.5f, true);
			}
			// moi : flèche blanche au centre, tournée comme mon regard, avec un cône de vision
			var dir = new Vector2(-Mathf.Sin(Yaw), -Mathf.Cos(Yaw));
			var perp = new Vector2(-dir.Y, dir.X);
			DrawColoredPolygon(new[] { c, c + (dir * 1.0f + perp * 0.62f) * 62f, c + (dir * 1.0f - perp * 0.62f) * 62f }, new Color(1, 1, 1, 0.16f));
			DrawColoredPolygon(new[] { c + dir * 10f, c - dir * 7f + perp * 7f, c - dir * 2.5f, c - dir * 7f - perp * 7f }, new Color(0, 0, 0, 0.85f));
			DrawColoredPolygon(new[] { c + dir * 8f, c - dir * 5.5f + perp * 5.5f, c - dir * 2f, c - dir * 5.5f - perp * 5.5f }, Colors.White);
			DrawRect(new Rect2(0, 0, Size, Size), new Color(1, 1, 1, 0.85f), false, 2.5f);
			DrawString(ThemeDB.FallbackFont, new Vector2(Size / 2f - 4f, 15f), "N", HorizontalAlignment.Left, -1, 13, new Color(1, 1, 1, 0.9f));
		}
	}

	/// <summary>Grande carte (touche M) : toute la carte, ma position et celle de mes équipiers.</summary>
	public partial class FullMap : MapBase
	{
		public bool Loading;
		public string LoadingText = "";

		public override void _Draw()
		{
			var vp = Size;
			DrawRect(new Rect2(Vector2.Zero, vp), new Color(0.02f, 0.03f, 0.05f, Loading ? 1f : 0.82f));
			float side = Mathf.Min(vp.X, vp.Y) * 0.88f;
			var origin = new Vector2((vp.X - side) / 2f, (vp.Y - side) / 2f + 10f);
			if (Tex != null) DrawTextureRect(Tex, new Rect2(origin, new Vector2(side, side)), false);
			DrawRect(new Rect2(origin, new Vector2(side, side)), new Color(1, 1, 1, 0.9f), false, 3f);
			Vector2 ToMap(Vector3 w) => origin + new Vector2((w.X - MinX) / (MaxX - MinX), (w.Z - MinZ) / (MaxZ - MinZ)) * side;

			DrawZone((x, z) => ToMap(new Vector3(x, 0, z)), side / (MaxX - MinX));
			DrawFlight(ToMap, 4f, 14f);
			foreach (var m in Marks)
			{
				if (!m.Friendly) continue;
				var p = ToMap(m.Pos);
				DrawCircle(p, 9f, new Color(0, 0, 0, 0.8f));
				DrawCircle(p, 7f, new Color(0.35f, 0.78f, 1f));
				if (!string.IsNullOrEmpty(m.Name))
					DrawString(ThemeDB.FallbackFont, p + new Vector2(11, 5), m.Name, HorizontalAlignment.Left, -1, 15, new Color(0.7f, 0.9f, 1f));
			}
			// moi : flèche orientée comme mon regard (0 = nord = -Z)
			var me = ToMap(Me);
			var dir = new Vector2(-Mathf.Sin(Yaw), -Mathf.Cos(Yaw));
			var perp = new Vector2(-dir.Y, dir.X);
			DrawColoredPolygon(new[] { me + dir * 15, me - dir * 9 + perp * 9, me - dir * 3, me - dir * 9 - perp * 9 }, new Color(0, 0, 0, 0.85f));
			DrawColoredPolygon(new[] { me + dir * 12, me - dir * 7 + perp * 7, me - dir * 2.5f, me - dir * 7 - perp * 7 }, new Color(1f, 0.92f, 0.3f));
			string title = Loading ? "CHARGEMENT  —  trajectoire de l'avion en jaune, vous êtes dessus" : "CARTE  —  vous : flèche jaune, équipiers : points bleus, avion : trait jaune   (M pour fermer)";
			DrawString(ThemeDB.FallbackFont, new Vector2(origin.X, origin.Y - 8), title, HorizontalAlignment.Left, -1, 17, Colors.White);
			if (Loading && !string.IsNullOrEmpty(LoadingText))
				DrawString(ThemeDB.FallbackFont, new Vector2(origin.X, origin.Y + side + 28), LoadingText, HorizontalAlignment.Left, -1, 20, new Color(1f, 0.93f, 0.55f));
		}
	}
}

