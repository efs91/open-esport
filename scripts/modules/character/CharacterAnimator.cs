using System.Collections.Generic;
using Godot;

/// <summary>
/// Corps entier d'un joueur, tel que le voient les autres : personnage MakeHuman animé avec les clips Mixamo.
/// Il ne décide de rien : le joueur lui donne son état (vitesse, au sol, glissade) et il choisit l'animation.
/// </summary>
public partial class CharacterAnimator : Node3D
{
	[Export]
	public string CharacterPath = CharacterKit.ModelPath;

	/// <summary>Durée du fondu entre deux animations, en secondes.</summary>
	[Export]
	public float BlendTime = 0.2f;

	/// <summary>Vitesse (m/s) en dessous de laquelle le personnage reste immobile.</summary>
	[Export]
	public float IdleThreshold = 0.4f;

	/// <summary>Vitesse (m/s) à partir de laquelle il court au lieu de marcher.</summary>
	[Export]
	/// <summary>Vitesse au sol à partir de laquelle on joue la course plutôt que la marche. Négatif = automatique : à mi-chemin entre la vitesse normale et le sprint.</summary>
	public float RunThreshold = -1f;
	private float RunAt => RunThreshold > 0f ? RunThreshold : OpenEsport.Core.Sim.BaseSpeed * (1f + OpenEsport.Core.Sim.SprintMultiplier) * 0.5f;

	private static readonly string[] ClipNames = { "idle", "idle_unarmed", "walk", "run", "jump_up", "jump_loop", "jump_down", "slide", "hang_start", "hang_idle", "climb" };

	/// <summary>Animations des bras seuls (sans arme) : garde devant soi, et coups de poing.</summary>
	private static readonly string[] ArmClipNames = { "fight_idle", "punch" };
	private AnimationPlayer _armsPlayer;

	/// <summary>Le personnage frappe (sans arme) : les bras jouent la boxe tant que c'est vrai.</summary>
	public bool Attacking;

	/// <summary>Aperçu des menus : sans arme et à l'arrêt, le personnage joue l'animation « Ninja Idle » (corps entier) au lieu de la garde de boxe.</summary>
	public bool UseNinjaIdle;
	private bool _drinking;
	private bool NinjaIdleNow => UseNinjaIdle && !Armed && !Attacking && _groundSpeed < IdleThreshold;
	private float _groundSpeed;

	private bool Armed => _heldRoot != null && GodotObject.IsInstanceValid(_heldRoot);
	private string IdleClip => NinjaIdleNow ? "idle_unarmed" : "idle";

	private enum Phase { Ground, Rising, Air, Landing }

	private Node3D _character;
	private AnimationPlayer _player;
	private Phase _phase = Phase.Ground;
	private bool _wasOnFloor = true;
	private double _landingTime;

	/// <summary>Animation en cours, pour les tests.</summary>
	public string CurrentClip => _player?.CurrentAnimation ?? "";

	public Skeleton3D Skeleton { get; private set; }

	/// <summary>Apparence affichée ; à changer par <see cref="SetAppearance"/> une fois le personnage prêt.</summary>
	public CharacterAppearance Appearance;

	private readonly System.Collections.Generic.Dictionary<string, Texture2D> _skinCache = new();
	private StandardMaterial3D _skinMaterial;
	private readonly System.Collections.Generic.Dictionary<MeshInstance3D,ArrayMesh> _kitMeshes = new();

	/// <summary>Applique morphologie, peau et pièces au modèle.</summary>
	public void SetAppearance(CharacterAppearance a)
	{
		Appearance = a;
		if (_character == null || a == null) return;
		CharacterKit.EnsureLoaded();

		var visible = new System.Collections.Generic.HashSet<string>();
		foreach (var g in CharacterKit.Choosable)
		{
			var items = CharacterKit.Items(g);
			int i = a.Piece(g);
			if (i >= 0 && i < items.Count) visible.Add(items[i].Id);
		}
		foreach (var g in CharacterKit.Always)
			foreach (var it in CharacterKit.Items(g)) visible.Add(it.Id);

		if (visible.Contains("hat_mandalorian"))
			foreach (var group in new[] { "hair", "eyebrows", "eyes", "eyelashes" })
				foreach (var item in CharacterKit.Items(group)) visible.Remove(item.Id);

		var outfit = CharacterKit.Items("clothes");
		int selected = a.Piece("clothes");
		if (selected >= 0 && selected < outfit.Count && outfit[selected].IncludesShoes)
			foreach (var shoe in CharacterKit.Items("shoes")) visible.Remove(shoe.Id);
		int coverage = ClothingCoverage.Equipped(visible);
		var detailMoves = CharacterDetails.Displacements(a);
		var detailJoints = detailMoves == null ? null : CharacterDetails.JointOffsets(detailMoves);
		if (_offsets != null)
		{
			var total = new System.Collections.Generic.Dictionary<string, Vector3>();
			void Add(string morph, float value)
			{
				if (value <= 0f || !CharacterKit.BoneOffsets.TryGetValue(morph, out var bones)) return;
				foreach (var kv in bones) total[kv.Key] = (total.TryGetValue(kv.Key, out var cur) ? cur : Vector3.Zero) + kv.Value * value;
			}
			Add("female", a.Gender); Add("heavy", Mathf.Max(a.Weight, 0)); Add("light", Mathf.Max(-a.Weight, 0));
			Add("muscular", Mathf.Max(a.Muscle, 0)); Add("frail", Mathf.Max(-a.Muscle, 0));
			Add("tall", Mathf.Max(a.Height, 0) * 0.5f); Add("short", Mathf.Max(-a.Height, 0) * 0.5f);
			Add("elder", Mathf.Max(a.Age, 0)); Add("youth", Mathf.Max(-a.Age, 0) * (7f / 4.8f));
			if (detailJoints != null) foreach (var (bone, delta) in detailJoints)
				total[bone] = total.GetValueOrDefault(bone) + delta;
			_offsets.SetOffsets(total);
			_shoulderLift = total.TryGetValue("RightUpperArm", out var sh) ? sh.Y : 0f;
			if (_heldRoot != null && GodotObject.IsInstanceValid(_heldRoot))
			{
				_heldRoot.Position = HeldPositionNow();
				_ik.HeldPosition = _heldRoot.Position;
			}
		}

		float w = a.Weight, m = a.Muscle, h = a.Height, age = a.Age;
		var shapes = new System.Collections.Generic.Dictionary<string, float>
		{
			{ "female", a.Gender }, { "heavy", Mathf.Max(w, 0) }, { "light", Mathf.Max(-w, 0) },
			{ "muscular", Mathf.Max(m, 0) }, { "frail", Mathf.Max(-m, 0) }, { "tall", Mathf.Max(h, 0) * 0.5f }, { "short", Mathf.Max(-h, 0) * 0.5f },
			{ "elder", Mathf.Max(age, 0) }, { "youth", Mathf.Max(-age, 0) * (7f / 4.8f) },
		};

		foreach (var node in _character.FindChildren("*", "MeshInstance3D", true, false))
		{
			var mi = (MeshInstance3D)node;
			string n = mi.Name;
			if (n != "Body") mi.Visible = visible.Contains(n);
			if (!_kitMeshes.TryGetValue(mi, out var original))
			{
				original = mi.Mesh as ArrayMesh;
				if (original == null) continue;
				_kitMeshes[mi] = original;
			}
			if (!mi.Visible) continue;
			var covered = ClothingCoverage.Apply(original, n, coverage);
			mi.Mesh = detailMoves == null || covered.GetSurfaceCount() == 0 ? covered : CharacterDetails.Fit(covered,mi,detailMoves,shapes,detailJoints);
			if (detailMoves == null) foreach (var kv in shapes)
			{
				int idx = mi.FindBlendShapeByName(kv.Key);
				if (idx >= 0) mi.SetBlendShapeValue(idx, kv.Value);
			}
			if (n == "Body" && CharacterKit.Skins.Count > 0)
			{
				var skin = CharacterKit.Skins[Mathf.Clamp(a.Skin, 0, CharacterKit.Skins.Count - 1)];
				if (!_skinCache.TryGetValue(skin.File, out var tex))
					_skinCache[skin.File] = tex = GD.Load<Texture2D>(CharacterKit.Folder + skin.File);
				_skinMaterial ??= new StandardMaterial3D { Roughness = 0.75f };
				_skinMaterial.AlbedoTexture = tex;
				mi.MaterialOverride = _skinMaterial;
			}
		}
	}

	private BoneOffsets _offsets;
	private float _shoulderLift;
	private ArmIK _ik;
	private Node3D _heldRoot;
	private Node3D _gripRight, _gripLeft;
	private Node3D _muzzle;

	/// <summary>Position monde de la bouche du canon de l'arme tenue (null si mains nues).</summary>
	public Vector3? MuzzlePosition => _muzzle != null && GodotObject.IsInstanceValid(_muzzle) && _muzzle.IsInsideTree() ? _muzzle.GlobalPosition : null;
	private Vector3 _primaryPalm;

	/// <summary>Position souhaitée de la paume droite, dans le repère du joueur (vers l'avant = -Z).</summary>
	public static Vector3 HeldPosition = new Vector3(0.16f, 1.22f, -0.30f);

	/// <summary>L'arme est tenue à hauteur de poitrine : on suit l'épaule du personnage (grand, petit…).</summary>
	private Vector3 HeldPositionNow()
	{
		int sh = Skeleton != null ? Skeleton.FindBone("RightUpperArm") : -1;
		if (sh < 0) return HeldPosition - _primaryPalm;
		float shoulderY = Skeleton.GetBoneGlobalRest(sh).Origin.Y + _shoulderLift;
		return new Vector3(HeldPosition.X, shoulderY - 0.28f, HeldPosition.Z) - _primaryPalm;
	}

	/// <summary>Met une arme dans les mains du personnage (null = mains nues). Les deux mains sont posées par IK.</summary>
	public void Hold(Openesport.Inventory.ItemDefinition def)
	{
		if (GodotObject.IsInstanceValid(_heldRoot)) { _heldRoot.Hide(); _heldRoot.QueueFree(); _heldRoot = null; }
		_muzzle = null;
		if (_ik != null) { _ik.RightTarget = null; _ik.LeftTarget = null; _ik.RightPose = null; _ik.LeftPose = null; _ik.Weapon = null; _ik.HeldRoot = null; }
		_primaryPalm = Vector3.Zero;
		if (def == null || Skeleton == null) return;

		_heldRoot = new Node3D { Name = "HeldWeapon", Position = HeldPositionNow() };
		AddChild(_heldRoot);
		var model = Openesport.Inventory.ItemVisuals.BuildHeldModel(def, out var heldBounds, out var right, out var left);
		_heldRoot.AddChild(model);
		// bouche du canon : le repère « Marker3D » de l'arme FPS, sinon le devant du modèle
		_muzzle = model.FindChild("Marker3D", true, false) as Node3D;
		if (_muzzle == null)
		{
			_muzzle = new Node3D { Name = "Muzzle", Position = new Vector3(0, heldBounds.End.Y - Mathf.Min(0.07f, heldBounds.Size.Y * 0.35f), heldBounds.Position.Z) };
			model.AddChild(_muzzle);
		}
		_primaryPalm = right?.Palm.Origin ?? Vector3.Zero;
		_heldRoot.Position = HeldPositionNow();
		_ik.HeldRoot = _heldRoot;
		_ik.Weapon = _heldRoot;
		_ik.LongGun = left != null && heldBounds.Size.Z >= .45f;
		_ik.Knife = def.Id == "knife";
		_ik.HeldPosition = _heldRoot.Position;
		if (right != null)
		{
			_gripRight = new Node3D { Name = "GripRight", Transform = right.Palm };
			_heldRoot.AddChild(_gripRight);
			_ik.RightTarget = _gripRight;
			_ik.RightPose = right;
		}
		else GD.PushWarning($"[CharacterAnimator] Pas de prise droite pour {def.Id}.");
		if (left != null)
		{
			_gripLeft = new Node3D { Name = "GripLeft", Transform = left.Palm };
			_heldRoot.AddChild(_gripLeft);
			_ik.LeftTarget = _gripLeft;
			_ik.LeftPose = left;
		}
	}

	// ------------------------------------------------------------------ tyrolienne : suspendu par une main à la poignée du chariot

	private bool _zipping;
	private Node3D _zipGrip;
	private (Node3D right, WeaponHandPose pose) _zipSaved;
	/// <summary>Décalage vertical (m) du corps pendant la glissade : la main doit être à la hauteur de la poignée.</summary>
	public static float ZipBodyOffset = 0f;

	public bool Zipping => _zipping;

	private static WeaponHandPose BottleGrip()
	{
		if (_bottleGrip == null)
		{
			var pistol = Openesport.Inventory.ItemDatabase.Get("c19");
			var tmp = Openesport.Inventory.ItemVisuals.BuildHeldModel(pistol, out _, out var right, out _);
			tmp.QueueFree();
			_bottleGrip = right;
		}
		return _bottleGrip;
	}

	/// <summary>
	/// Sur un câble : le corps joue « Jump To Hang » puis « Hanging Idle » (Mixamo), la main droite tient la poignée au-dessus de la tête
	/// (IK), la main gauche garde l'arme. Hors de la glissade, tout revient à la normale.
	/// </summary>
	public void SetZipline(bool on)
	{
		if (on == _zipping) return;
		_zipping = on;
		if (_ik == null || Skeleton == null || _player == null) return;
		if (on)
		{
			_zipSaved = (_ik.RightTarget, _ik.RightPose);
			var grip = BottleGrip();
			if (grip != null)
			{
				_zipGrip = new Node3D { Name = "ZipGrip", Transform = new Transform3D(grip.Palm.Basis, new Vector3(0.04f, OpenEsport.Core.Sim.ZipHoldHeight - 0.32f, -0.05f)) };
				AddChild(_zipGrip);
				_ik.RightTarget = _zipGrip;
				_ik.RightPose = grip;
			}
			_player.Play("hang_start", 0.1);
			_player.SpeedScale = 1.4f;
		}
		else
		{
			if (GodotObject.IsInstanceValid(_zipGrip)) _zipGrip.QueueFree();
			_zipGrip = null;
			_ik.RightTarget = _zipSaved.right; _ik.RightPose = _zipSaved.pose;
			_player.Play(IdleClip, 0.2);
			_player.SpeedScale = 1f;
		}
	}

	private void UpdateZipline()
	{
		Position = new Vector3(0, ZipBodyOffset, 0);
		if (_player.CurrentAnimation == "hang_start" && !_player.IsPlaying()) { _player.Play("hang_idle", 0.2); _player.SpeedScale = 1f; }
		else if (_player.CurrentAnimation != "hang_start" && _player.CurrentAnimation != "hang_idle") _player.Play("hang_idle", 0.2);
		if (_ik != null) _ik.Grounded = false;
	}

	// ------------------------------------------------------------------ chute libre et parachute

	private bool _skydive;
	public bool Skydiving => _skydive;

	/// <summary>
	/// Chute libre (mode 4) : le corps est couché vers l'avant, à plat ventre (dive = 0) ou en piqué, tête la première (dive = 1), bras écartés
	/// (clip « jump_loop », en attendant une vraie animation de chute libre). À terre (mode 5) : couché au sol. Mode 0 : fin.
	/// </summary>
	public void Skydive(int mode, float dive)
	{
		bool on = mode == 4 || mode == 5;       // 4 = chute libre, 5 = à terre
		if (on != _skydive)
		{
			_skydive = on;
			if (_ik != null) _ik.Active = !on;
			if (_ik != null && GodotObject.IsInstanceValid(_ik.HeldRoot)) _ik.HeldRoot.Visible = !on;
			if (_player != null) { _player.SpeedScale = 1f; _player.Play(on ? (mode == 4 ? "jump_loop" : "idle_unarmed") : IdleClip, 0.15); }
			if (!on) { Rotation = Vector3.Zero; Position = Vector3.Zero; }
		}
		if (!on) return;
		// chute libre : à plat ventre (dive = 0) ou en piqué ; à terre : couché à plat ventre, au ras du sol
		// dive = 0 : redressé, presque à plat ventre (descente lente) ; dive = 1 : tête la première
		float tilt = mode == 4 ? Mathf.DegToRad(-72f - 68f * Mathf.Clamp(dive, 0f, 1f)) : Mathf.DegToRad(-88f);
		// on tourne autour du centre du corps (≈ 0,95 m) et non des pieds
		var center = new Vector3(0, mode == 4 ? 0.95f : 0.22f, 0);
		var basis = Basis.FromEuler(new Vector3(tilt, 0, 0));
		Basis = basis;
		Position = center - basis * center;
	}

	// ------------------------------------------------------------------ escalade : on se hisse sur un rebord

	private bool _climbing;
	public bool Climbing => _climbing;

	/// <summary>Pendant l'escalade, tout le corps joue « Freehang Climb » (les deux mains sur le rebord) : l'arme est rangée le temps du hissage.</summary>
	public void SetClimbing(bool on)
	{
		if (on == _climbing) return;
		_climbing = on;
		if (_player == null) return;
		if (_ik != null) _ik.Active = !on;
		if (_ik != null && GodotObject.IsInstanceValid(_ik.HeldRoot)) _ik.HeldRoot.Visible = !on;
		if (on)
		{
			_player.Play("climb", 0.1);
			var clip = _player.GetAnimation("climb");
			_player.SpeedScale = clip != null ? (float)(clip.Length / OpenEsport.Core.Sim.ClimbTime) : 1f;
		}
		else
		{
			_player.SpeedScale = 1f;
			_player.Play(IdleClip, 0.2);
		}
	}

	// ------------------------------------------------------------------ soin : le flacon est levé à la bouche et incliné

	private Node3D _drinkRoot;
	private double _drinkTime;
	private bool _drinkFps;
	private float _useSeconds = 3f;
	private Node3D _liquidA, _liquidB, _lid;
	private System.Collections.Generic.List<ShaderMaterial> _emptyMats;
	private bool _drinkBox;
	private float _drinkHeight;
	private static WeaponHandPose _bottleGrip;
	private (Node3D right, Node3D left, WeaponHandPose rightPose, WeaponHandPose leftPose, Node3D held, Vector3 heldPos) _savedIk;

	public bool Drinking => _drinking;

	/// <summary>
	/// Pendant l'utilisation d'un soin : l'arme est rangée, la main droite saisit un flacon (pose de prise d'un pistolet) et le lève
	/// jusqu'à la bouche en l'inclinant. Aucune animation externe : seule la cible de l'IK du bras bouge.
	/// <paramref name="firstPerson"/> : vue du joueur local, le flacon reste plus bas pour ne pas cacher l'écran.
	/// </summary>
	public void SetUsing(bool on, Openesport.Inventory.ItemDefinition def, bool firstPerson = false)
	{
		if (on == _drinking) return;
		if (!on) { StopDrinking(); return; }
		if (_ik == null || Skeleton == null || def == null) return;

		BottleGrip();
		if (_bottleGrip == null) return;

		_savedIk = (_ik.RightTarget, _ik.LeftTarget, _ik.RightPose, _ik.LeftPose, _ik.HeldRoot, _ik.HeldPosition);
		if (Armed) _heldRoot.Visible = false;
		_drinkFps = firstPerson;
		_drinkTime = 0;
		var netDef = OpenEsport.Core.ItemDefs.ByName(def.Id);
		_useSeconds = netDef == null ? 3f : Mathf.Max(0.5f, OpenEsport.Core.Consumables.Of(netDef.Id).Seconds);
		_drinkRoot = new Node3D { Name = "Drink" };
		AddChild(_drinkRoot);
		// le flacon est traversé par la paume : la racine est à la paume, pour que la rotation se fasse autour de la main
		var palm = _bottleGrip.Palm.Origin;
		var bottle = Openesport.Inventory.ItemVisuals.BuildGroundModel(def, 0);
		_drinkBox = def.IsBox;
		_drinkRoot.AddChild(bottle);
		_drinkHeight = Openesport.Inventory.ItemVisuals.HeightOf(bottle);
		// bidon : la main tient le corps (milieu) ; boîte : la main tient la poignée du dessus, la boîte pend dessous
		bottle.Position = new Vector3(0, _drinkBox ? -_drinkHeight * 0.92f : -_drinkHeight * 0.5f, 0);
		if (_drinkBox) bottle.Rotation = new Vector3(0, Mathf.Pi / 2f, 0);
		_lid = bottle.FindChild("Lid", true, false) as Node3D;
		_emptyMats = (!_drinkBox && !string.IsNullOrEmpty(def.ScenePath)) ? Openesport.Inventory.ItemVisuals.AddEmptyOverlay(bottle) : null;
		_liquidA = bottle.FindChild("LiquidA", true, false) as Node3D;
		_liquidB = bottle.FindChild("LiquidB", true, false) as Node3D;
		var grip = new Node3D { Name = "GripRight", Transform = new Transform3D(_bottleGrip.Palm.Basis, Vector3.Zero) };
		_drinkRoot.AddChild(grip);
		_ik.RightTarget = grip;
		_ik.RightPose = _bottleGrip;
		_ik.LeftTarget = null; _ik.LeftPose = null;
		_ik.HeldRoot = _drinkRoot;
		_drinking = true;
		UpdateDrinking(0);
	}

	private void StopDrinking()
	{
		_drinking = false;
		if (GodotObject.IsInstanceValid(_drinkRoot)) { _drinkRoot.Hide(); _drinkRoot.QueueFree(); }
		_drinkRoot = null; _lid = null; _emptyMats = null; _liquidA = null; _liquidB = null;
		if (_ik != null)
		{
			_ik.RightTarget = _savedIk.right; _ik.LeftTarget = _savedIk.left; _ik.RightPose = _savedIk.rightPose; _ik.LeftPose = _savedIk.leftPose;
			_ik.HeldRoot = _savedIk.held; _ik.HeldPosition = _savedIk.heldPos;
		}
		if (Armed) _heldRoot.Visible = true;
	}

	private void UpdateDrinking(double delta)
	{
		if (!_drinking || !GodotObject.IsInstanceValid(_drinkRoot) || Skeleton == null) return;
		_drinkTime += delta;
		int sh = Skeleton.FindBone("RightUpperArm");
		float shoulderY = (sh >= 0 ? Skeleton.GetBoneGlobalRest(sh).Origin.Y : 1.4f) + _shoulderLift;
		var low = new Vector3(0.18f, shoulderY - 0.30f, -0.28f);
		var mouth = _drinkFps ? new Vector3(0.16f, shoulderY - 0.06f, -0.36f) : new Vector3(0.06f, shoulderY + 0.13f, -0.20f);
		float raise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((float)(_drinkTime / 0.55), 0f, 1f));
		if (_drinkBox)
		{
			// boîte : tenue devant la poitrine, le couvercle s'ouvre puis la boîte reste ouverte jusqu'à la fin du soin
			var chest = new Vector3(0.08f, shoulderY - 0.30f + _drinkHeight * 0.55f, -0.30f);
			_drinkRoot.Position = low.Lerp(chest, raise);
			_drinkRoot.Rotation = new Vector3(Mathf.DegToRad(-8f) * raise, 0f, 0f);
			if (_lid != null) _lid.Rotation = new Vector3(-Mathf.DegToRad(112f) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp((float)((_drinkTime - 0.5) / 0.7), 0f, 1f)), 0f, 0f);
			_ik.HeldPosition = _drinkRoot.Position;
			return;
		}
		float sip = Mathf.Sin((float)_drinkTime * 2.4f) * 0.07f * raise;       // petites gorgées
		float tilt = raise * Mathf.DegToRad(_drinkFps ? 62f : 105f) + sip;
		_drinkRoot.Position = low.Lerp(mouth, raise);
		_drinkRoot.Rotation = new Vector3(tilt, 0f, raise * 0.22f);
		_ik.HeldPosition = _drinkRoot.Position;

		// le flacon se vide au fil du soin (après la levée du bras) : en deux temps pour un flacon à deux couleurs, la couche du haut d'abord
		float fill = 1f - Mathf.Clamp((float)((_drinkTime - 0.4) / Mathf.Max(0.5f, _useSeconds - 0.4f)), 0f, 1f);
		if (_liquidB != null)
		{
			SetLevel(_liquidB, (fill - 0.5f) * 2f);
			SetLevel(_liquidA, fill * 2f);
		}
		else SetLevel(_liquidA, fill);
		if (_emptyMats != null) foreach (var m in _emptyMats) m.SetShaderParameter("level", fill);
	}

	private static void SetLevel(Node3D layer, float level)
	{
		if (layer == null || !GodotObject.IsInstanceValid(layer)) return;
		level = Mathf.Clamp(level, 0f, 1f);
		layer.Scale = new Vector3(1f, Mathf.Max(level, 0.001f), 1f);
		layer.Visible = level > 0.01f;
	}

	public override void _Ready()
	{
		var scene = GD.Load<PackedScene>(CharacterPath);
		if (scene == null)
		{
			GD.PrintErr($"[CharacterAnimator] Personnage introuvable : {CharacterPath}");
			return;
		}

		// Le modèle regarde vers +Z, un joueur regarde vers -Z
		var pivot = new Node3D { Name = "Pivot", Rotation = new Vector3(0, Mathf.Pi, 0) };
		AddChild(pivot);
		_character = scene.Instantiate<Node3D>();
		pivot.AddChild(_character);
		Skeleton = _character.GetNodeOrNull<Skeleton3D>("%GeneralSkeleton");
		if (Skeleton != null)
		{
			_offsets = new BoneOffsets { Name = "BoneOffsets" };
			Skeleton.AddChild(_offsets); // d'abord : les autres travaillent sur le squelette déjà morphé
			_ik = new ArmIK { Name = "ArmIK" };
			Skeleton.AddChild(_ik); // les bras libres ont leur propre pose
		}
		SetAppearance(Appearance ?? CharacterAppearance.Current);

		var library = new AnimationLibrary();
		foreach (var name in ClipNames)
		{
			var clip = GD.Load<Animation>($"res://assets/animations/clips/{name}.res");
			if (clip == null)
			{
				GD.PrintErr($"[CharacterAnimator] Animation manquante : {name}");
				continue;
			}
			library.AddAnimation(name, clip);
		}

		// Les pistes d'animation (%GeneralSkeleton:Hips…) sont relatives à la racine du modèle
		_player = new AnimationPlayer { Name = "AnimationPlayer" };
		_character.AddChild(_player);
		_player.AddAnimationLibrary("", library);
		Play(IdleClip);

		// Bras seuls : un second lecteur, placé après le premier, écrase l'animation des bras sans toucher aux jambes ni au tronc
		var armLibrary = new AnimationLibrary();
		foreach (var name in ArmClipNames)
		{
			var clip = GD.Load<Animation>($"res://assets/animations/clips/{name}.res");
			if (clip == null) { GD.PrintErr($"[CharacterAnimator] Animation de bras manquante : {name}"); continue; }
			armLibrary.AddAnimation(name, clip);
		}
		_armsPlayer = new AnimationPlayer { Name = "ArmsPlayer" };
		_character.AddChild(_armsPlayer);
		_armsPlayer.AddAnimationLibrary("", armLibrary);
	}

	public void SetModelingPose(bool active)
	{
		if (_player == null || Skeleton == null) return;
		_ik.Active = !active;
		if (active)
		{
			_player.Stop();
			_armsPlayer.Stop();
			Skeleton.ResetBonePoses();
		}
		else Play(IdleClip);
	}

	public const float HoverSaddleHeight = 1.06f;
	private bool _hoverSeated;

	public void SetHoverSeated(bool seated)
	{
		if (_hoverSeated == seated || Skeleton == null) return;
		_hoverSeated = seated;
		_player.Stop();
		_armsPlayer.Stop();
		_ik.Active = !seated;
		if (GodotObject.IsInstanceValid(_heldRoot)) _heldRoot.Visible = !seated;
		Skeleton.ResetBonePoses();
		if (!seated)
		{
			Position = Vector3.Zero;
			Play(IdleClip);
			return;
		}

		// The skeleton faces +Z. Bend both knees and bring the hands to the controls.
		foreach (var (side, sign) in new[] { ("Left", 1f), ("Right", -1f) })
		{
			PointLimb(side + "UpperLeg", side + "LowerLeg", new Vector3(.24f * sign, -.18f, 1));
			PointLimb(side + "LowerLeg", side + "Foot", new Vector3(.06f * sign, -1, -.12f));
			PointLimb(side + "Foot", side + "Toes", Vector3.Back);
			PointLimb(side + "UpperArm", side + "LowerArm", new Vector3(.24f * sign, -.8f, .55f));
			PointLimb(side + "LowerArm", side + "Hand", new Vector3(-.15f * sign, .18f, 1));
		}
		int hips = Skeleton.FindBone("Hips");
		if (hips >= 0)
			Position = Vector3.Down * ToLocal(Skeleton.ToGlobal(Skeleton.GetBoneGlobalPose(hips).Origin)).Y;
	}

	private void PointLimb(string joint, string end, Vector3 direction)
	{
		int bone = Skeleton.FindBone(joint), child = Skeleton.FindBone(end);
		if (bone < 0 || child < 0) return;
		var pose = Skeleton.GetBoneGlobalPose(bone);
		var axis = Skeleton.GetBoneGlobalPose(child).Origin - pose.Origin;
		var rotation = new Basis(new Quaternion(axis.Normalized(), direction.Normalized()));
		var parent = Skeleton.GetBoneGlobalPose(Skeleton.GetBoneParent(bone)).Basis;
		Skeleton.SetBonePoseRotation(bone, (parent.Inverse() * rotation * pose.Basis).Orthonormalized().GetRotationQuaternion());
	}

	/// <summary>Met à jour l'animation d'après l'état du joueur. À appeler à chaque image physique.</summary>
	public void Tick(Vector3 velocity, bool onFloor, bool sliding, double delta)
	{
		if (_player == null || _hoverSeated)
			return;

		float groundSpeed = new Vector2(velocity.X, velocity.Z).Length();
		_groundSpeed = groundSpeed;
		if (_ik != null) _ik.Sliding = sliding;
		UpdateDrinking(delta);
		if (_zipping) { UpdateZipline(); return; }
		if (_climbing) { if (_ik != null) _ik.Grounded = false; return; }
		if (_skydive) { if (_ik != null) _ik.Grounded = false; return; }
		UpdateArms();
		// Sans arme, les bras suivent l'animation de garde (ou de boxe) : la pose de bras libres (Relax) n'est plus utilisée
		if (_ik != null) { _ik.GroundSpeed = groundSpeed; _ik.Grounded = onFloor && !sliding && Armed; }

		if (_wasOnFloor && !onFloor)
		{
			// Saut ou chute : l'animation de saut ne démarre que si on monte
			_phase = velocity.Y > 1.0f ? Phase.Rising : Phase.Air;
			Play(_phase == Phase.Rising ? "jump_up" : "jump_loop");
		}
		else if (!_wasOnFloor && onFloor)
		{
			_phase = Phase.Landing;
			_landingTime = 0;
			Play("jump_down");
		}
		_wasOnFloor = onFloor;

		switch (_phase)
		{
			case Phase.Rising:
				// Quand l'élan est fini, ou que l'animation de départ s'achève, on passe à la boucle en l'air
				if (velocity.Y < 0.5f || (_player.CurrentAnimation == "jump_up" && !_player.IsPlaying()))
				{
					_phase = Phase.Air;
					Play("jump_loop");
				}
				return;
			case Phase.Air:
				return;
			case Phase.Landing:
				_landingTime += delta;
				// L'atterrissage s'interrompt vite si le joueur repart
				if (_landingTime < (groundSpeed > IdleThreshold ? 0.25 : 0.5))
					return;
				_phase = Phase.Ground;
				break;
		}

		if (sliding)
			Play("slide");
		else if (groundSpeed < IdleThreshold)
			Play(IdleClip);
		else if (groundSpeed < RunAt)
			Play("walk", GroundSpeedScale("walk", groundSpeed));
		else
			Play("run", GroundSpeedScale("run", groundSpeed));
	}

	/// <summary>Sans arme : garde les deux mains devant soi, ou boxe si on frappe. Avec une arme : les bras sont posés par l'IK.</summary>
	private void UpdateArms()
	{
		if (_armsPlayer == null) return;
		if (Armed || NinjaIdleNow || _drinking)
		{
			if (_armsPlayer.IsPlaying()) _armsPlayer.Stop();
			return;
		}
		// Un coup se joue toujours en entier, tout de suite : lâcher le clic n'interrompt pas l'animation.
		bool punching = _armsPlayer.IsPlaying() && _armsPlayer.CurrentAnimation == "punch";
		if (!punching && (_punchRequested || Attacking))
		{
			_punchRequested = false;
			_armsPlayer.Play("punch", 0.0); // sans fondu : le coup part dès le clic
			_armsPlayer.SpeedScale = PunchSpeed;
			return;
		}
		_punchRequested = false;
		if (!punching && (_armsPlayer.CurrentAnimation != "fight_idle" || !_armsPlayer.IsPlaying()))
		{
			_armsPlayer.Play("fight_idle", 0.1);
			_armsPlayer.SpeedScale = 1.0f;
		}
	}

	private bool _punchRequested;
	/// <summary>Vitesse de lecture du coup : 0,55 s à vitesse 1 ; un peu plus vif que le rythme de frappe du serveur (0,43 s).</summary>
	private const float PunchSpeed = 1.3f;

	/// <summary>Lance un coup de poing maintenant (il se jouera en entier).</summary>
	public void RequestPunch() => _punchRequested = true;

	/// <summary>Vitesse de lecture qui cale les pas sur la vitesse réelle, pour éviter que les pieds glissent.</summary>
	private float GroundSpeedScale(string clip, float groundSpeed)
	{
		var animation = _player.GetAnimation(clip);
		float reference = animation != null && animation.HasMeta("ground_speed") ? animation.GetMeta("ground_speed").AsSingle() : 0.0f;
		return reference > 0.1f ? Mathf.Clamp(groundSpeed / reference, 0.6f, 1.8f) : 1.0f;
	}

	private void Play(string clip, float speed = 1.0f)
	{
		if (_player.CurrentAnimation == clip && _player.IsPlaying())
		{
			_player.SpeedScale = speed;
			return;
		}
		_player.Play(clip, BlendTime);
		_player.SpeedScale = speed;
	}
}
