using Godot;
using Openesport.Inventory;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Un autre joueur, vu en entier : personnage animé ; nom et points de vie au-dessus de la tête pour les équipiers seulement.
	/// Un adversaire n'a rien au-dessus de lui, sauf pendant 3 s après que notre équipe l'a touché : une barre de vie et une barre de bouclier.
	/// </summary>
	public partial class RemotePlayer : Node3D
	{
		private CharacterAnimator _animator;
		private Label3D _label;
		private Vector3 _lastPos;
		private bool _hasLast;
		private float _vy;
		private float _stepClock;
		private bool _wasOnGround = true;

		public ushort Id;
		/// <summary>Jusqu'à quand (secondes d'horloge) montrer les barres de vie et de bouclier de cet adversaire (il vient d'être touché par notre équipe).</summary>
		public double BarsUntil;
		private Sprite3D _bars;
		private Image _barsImage;
		private ImageTexture _barsTexture;
		private int _barsKey = -1;
		private const int BarsW = 64, BarsH = 11;
		public void HideLabel() { if (_label != null) _label.Visible = false; _hideLabel = true; }
		private bool _hideLabel;

		/// <summary>Jusqu'à quand cet adversaire fait un bruit perceptible (secondes d'horloge), et jusqu'à quelle distance on l'entend.</summary>
		public double NoiseUntil;
		public float NoiseRange;

		public void Noise(double seconds, float range)
		{
			double now = Time.GetTicksMsec() / 1000.0;
			if (NoiseUntil < now) NoiseRange = 0f;          // l'ancien bruit est fini : on repart de zéro
			NoiseUntil = System.Math.Max(NoiseUntil, now + seconds);
			NoiseRange = Mathf.Max(NoiseRange, range);
		}

		/// <summary>Bouche du canon de l'arme qu'il tient (sinon, à hauteur de poitrine devant lui).</summary>
		public Vector3 MuzzlePosition => _animator?.MuzzlePosition ?? GlobalTransform * new Vector3(0.14f, 1.3f, -0.9f);

		private byte _weaponId;
		private bool _wasFiring;
		private byte[] _look;

		/// <summary>Apparence choisie par ce joueur (reçue du serveur).</summary>
		public void SetLook(byte[] appearance)
		{
			if (appearance == null || _animator == null) return;
			if (_look != null && System.Linq.Enumerable.SequenceEqual(_look, appearance)) return;
			_look = (byte[])appearance.Clone();
			_animator.SetAppearance(CharacterAppearance.Unpack(appearance));
		}

		private void SetWeapon(byte weaponId)
		{
			_weaponId = weaponId;
			var def = weaponId == 0 ? null : ItemDefs.Get(weaponId);
			var godotDef = def != null && def.Kind == ItemKind.Weapon ? ItemDatabase.Get(def.Name) : null;
			_animator?.Hold(godotDef);
		}

		public override void _Ready()
		{
			_animator = new CharacterAnimator { Name = "Animator" };
			AddChild(_animator);

			_label = new Label3D
			{
				Name = "Nameplate",
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				NoDepthTest = true,
				FontSize = 44,
				PixelSize = 0.004f,
				OutlineSize = 10,
				Position = new Vector3(0, 2.2f, 0),
				FixedSize = false
			};
			AddChild(_label);

			// barres d'un adversaire touché : petite image (bouclier en haut, vie en bas), redessinée seulement quand les valeurs changent
			_barsImage = Image.CreateEmpty(BarsW, BarsH, false, Image.Format.Rgba8);
			_barsTexture = ImageTexture.CreateFromImage(_barsImage);
			_bars = new Sprite3D
			{
				Name = "EnemyBars", Texture = _barsTexture, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
				PixelSize = 0.012f, Position = new Vector3(0, 2.15f, 0), Visible = false, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
				Shaded = false, DoubleSided = true
			};
			AddChild(_bars);
		}

		private void DrawBars(int health, int shield, float shieldMax)
		{
			int key = health * 1000 + shield * 3 + (shieldMax > 0.5f ? 1 : 0);
			if (key == _barsKey) return;
			_barsKey = key;
			_barsImage.Fill(new Color(0, 0, 0, 0));
			void Row(int y, int h, float frac, Color fill)
			{
				_barsImage.FillRect(new Rect2I(0, y, BarsW, h), new Color(0, 0, 0, 0.75f));
				int w = Mathf.Clamp(Mathf.RoundToInt((BarsW - 2) * frac), 0, BarsW - 2);
				if (w > 0) _barsImage.FillRect(new Rect2I(1, y + 1, w, h - 2), fill);
			}
			if (shieldMax > 0.5f) Row(0, 5, Mathf.Clamp(shield / shieldMax, 0f, 1f), new Color(0.5f, 0.8f, 1f));
			Row(5, 6, Mathf.Clamp(health / 100f, 0f, 1f), new Color(0.95f, 0.25f, 0.2f));
			_barsTexture.Update(_barsImage);
		}

		public void Apply(in InterpolatedEntity e, string name, bool friendly, float delta)
		{
			var pos = new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z);
			GlobalPosition = pos;
			Rotation = new Vector3(0, e.Yaw, 0);

			if (e.Weapon != _weaponId) SetWeapon(e.Weapon);

			bool dead = e.Has(EntityFlags.Dead);
			Visible = !dead && e.Zip != 3;        // dans l'avion : pas sur la carte
			if (dead) { _hasLast = false; return; }

			// la vitesse verticale n'est pas répliquée : on la déduit du déplacement, pour animer les sauts
			if (_hasLast && delta > 0.0001f)
				_vy = Mathf.Lerp(_vy, (pos.Y - _lastPos.Y) / delta, 0.5f);
			_lastPos = pos;
			_hasLast = true;

			bool onGround = e.Has(EntityFlags.OnGround);
			bool sliding = e.Has(EntityFlags.Sliding);

			// sons positionnés : pas (plus rapprochés en courant), saut, atterrissage ; pas de bruit de pas en glissade ou accroupi
			float hs = new Vector2(e.Vel.X, e.Vel.Z).Length();
			if (onGround && !sliding && hs > 1.5f && !e.Has(EntityFlags.Crouching))
			{
				_stepClock += delta * hs;
				if (_stepClock >= 2.1f) { _stepClock = 0f; GameAudio.Step(GetParent(), pos, hs > 5.5f); }
			}
			else _stepClock = Mathf.Min(_stepClock, 1.4f);
			if (_wasOnGround && !onGround && _vy > 2f) { GameAudio.Jump(GetParent(), pos); Noise(1.0, 45f); }
			if (!_wasOnGround && onGround) { GameAudio.Land(GetParent(), pos); Noise(1.0, 50f); }
			// bruits perceptibles (sert à la minimap) : course et sprint, glissade, tirs
			if (onGround && !e.Has(EntityFlags.Crouching) && hs > 4.2f) Noise(0.9, hs > 5.8f ? 60f : 40f);
			if (sliding) Noise(1.0, 55f);
			if (e.Has(EntityFlags.Firing)) Noise(2.5, 150f);
			_wasOnGround = onGround;
			// à mains nues, un tir signalé par le serveur est un coup de poing : les bras jouent la boxe un moment après le dernier coup
			bool firing = e.Has(EntityFlags.Firing);
			if (_weaponId == 0 && firing && !_wasFiring) _animator.RequestPunch();
			_wasFiring = firing;
			bool zip = e.Zip == 1;
			_animator.SetClimbing(e.Zip == 2);
			_animator.Skydive(e.Zip == 4 || e.Zip == 5 ? e.Zip : 0, OpenEsport.Core.PlayerMovement.DivePose(e.Pitch));
			if (e.Zip == 4) Noise(1.0, 120f);
			if (e.Zip == 2) Noise(1.0, 20f);
			_animator.SetZipline(zip);
			if (zip) Noise(1.0, 100f);                      // sur un câble : audible à 100 m, pas plus loin
			_animator.SetUsing(e.Has(EntityFlags.Using), ItemDatabase.Get("heal_small"));   // le flacon exact n'est pas répliqué : un flacon générique
			_animator.Tick(new Vector3(e.Vel.X, onGround ? 0f : _vy, e.Vel.Z), onGround, sliding, delta);

			// pas d'animation d'accroupi pour l'instant : on écrase légèrement le personnage
			float crouch = e.Has(EntityFlags.Crouching) ? 0.72f : 1f;
			_animator.Scale = new Vector3(1f, Mathf.Lerp(_animator.Scale.Y, crouch, 0.3f), 1f);

			string soin = e.Has(EntityFlags.Using) ? "  ✚" : "";
			// nom et vie au-dessus de la tête : équipiers seulement ; un adversaire touché montre ses barres 3 s
			_label.Visible = friendly && !_hideLabel;
			bool bars = !friendly && BarsUntil > Time.GetTicksMsec() / 1000.0;
			_bars.Visible = bars;
			if (bars) DrawBars(e.Health, e.Shield, e.ShieldTier > 0 ? Consumables.ShieldMaxOf(e.ShieldTier - 1) : 0f);
			_label.Text = e.ShieldTier > 0 || e.Shield > 0 ? $"{name}  {e.Health} + {e.Shield}{soin}" : $"{name}  {e.Health}{soin}";
			_label.Modulate = friendly ? new Color(0.45f, 0.85f, 1f) : new Color(1f, 0.45f, 0.4f);
			_label.OutlineModulate = Colors.Black;
		}
	}
}
