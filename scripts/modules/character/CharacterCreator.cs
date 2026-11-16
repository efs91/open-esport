using Godot;
using System;
using System.Collections.Generic;
using Openesport.Inventory;

/// <summary>
/// Panneau de création du personnage, affiché avant une partie solo ou en ligne : morphologie MakeHuman (sliders),
/// peau, tenue, chaussures, cheveux. L'aperçu 3D est animé avec les vraies animations et peut tenir une arme,
/// pour vérifier que les bras, les mains et l'arme sont bien placés.
/// </summary>
public partial class CharacterCreator : CanvasLayer
{
	public event Action<CharacterAppearance> Confirmed;
	public event Action Cancelled;

	/// <summary>Apparence de départ (sinon le personnage choisi).</summary>
	public CharacterAppearance Initial;
	/// <summary>Nom du personnage : modifiable dans le panneau, relu à la validation.</summary>
	public string CharacterName = "";
	private LineEdit _nameEdit;

	private CharacterAppearance _app;
	private CharacterAnimator _animator;
	private Node3D _turntable;
	private Camera3D _camera;
	private SubViewportContainer _viewContainer;
	private float _yaw = Mathf.Pi; // de face
	private float _camHeight = 1.0f, _camDistance = 3.6f, _camFov = 32f;
	private Vector3 _velocity;
	private bool _sliding;
	private bool _updating;

	private readonly Dictionary<string, HSlider> _sliders = new();
	private readonly Dictionary<string, OptionButton> _options = new();
	private OptionButton _weaponOption, _poseOption;

	public CharacterCreator() { Layer = 100; }

	public override void _Ready()
	{
		CharacterKit.EnsureLoaded();
		_app = (Initial ?? CharacterAppearance.Current).Clone();
		Input.MouseMode = Input.MouseModeEnum.Visible;

		var root = new Control { Name = "Root" };
		root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(root);
		root.AddChild(new ColorRect { Color = new Color(0.07f, 0.08f, 0.1f), AnchorRight = 1, AnchorBottom = 1 });

		var split = new HBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 16, OffsetTop = 16, OffsetRight = -16, OffsetBottom = -16 };
		split.AddThemeConstantOverride("separation", 16);
		root.AddChild(split);

		split.AddChild(BuildPanel());
		split.AddChild(BuildPreview());
		Refresh();
	}

	// ------------------------------------------------------------------ interface

	private Control BuildPanel()
	{
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(430, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		box.AddThemeConstantOverride("separation", 8);
		scroll.AddChild(box);

		var title = new Label { Text = "Création du personnage" };
		title.AddThemeFontSizeOverride("font_size", 26);
		box.AddChild(title);

		box.AddChild(Section("Nom"));
		_nameEdit = new LineEdit { Text = CharacterName, PlaceholderText = "Nom du personnage", MaxLength = 24 };
		box.AddChild(_nameEdit);

		box.AddChild(Section("Morphologie"));
		AddSlider(box, "gender", "Masculin  ↔  Féminin", 0f, 1f, v => _app.Gender = v);
		AddSlider(box, "height", "Taille", -1f, 1f, v => _app.Height = v);
		AddSlider(box, "weight", "Corpulence", -1f, 1f, v => _app.Weight = v);
		AddSlider(box, "muscle", "Musculature", -1f, 1f, v => _app.Muscle = v);
		AddSlider(box, "age", "Âge", -1f, 1f, v => _app.Age = v);

		box.AddChild(Section("Apparence"));
		var skins = new List<string>();
		foreach (var s in CharacterKit.Skins) skins.Add(s.Name);
		AddOption(box, "skin", "Peau", skins, i => _app.Skin = i);
		AddPieceOption(box, "clothes", "Tenue");
		AddPieceOption(box, "shoes", "Chaussures");
		AddPieceOption(box, "hair", "Cheveux");
		AddPieceOption(box, "eyebrows", "Sourcils");

		box.AddChild(Section("Aperçu"));
		var weapons = new List<string> { "Mains nues" };
		_weaponIds.Clear(); _weaponIds.Add(null);
		foreach (var id in ItemDatabase.Ids)
		{
			var d = ItemDatabase.Get(id);
			if (d != null && d.Type == ItemType.Weapon) { weapons.Add(d.DisplayName); _weaponIds.Add(id); }
		}
		_weaponOption = AddOption(box, "weapon", "Arme en main", weapons, _ => UpdatePreviewState(), selected: _weaponIds.IndexOf("ak"));
		_poseOption = AddOption(box, "pose", "Animation", new List<string> { "Repos", "Marche", "Course", "Glissade", "Coup de poing" }, _ => UpdatePreviewState());

		var views = new HBoxContainer();
		views.AddThemeConstantOverride("separation", 6);
		foreach (var (label, yaw) in new[] { ("Face", Mathf.Pi), ("Profil", Mathf.Pi / 2f), ("Dos", 0f), ("Dessus", -1f) })
		{
			var b = new Button { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			float y = yaw;
			b.Pressed += () => SetView(y);
			views.AddChild(b);
		}
		box.AddChild(views);
		var zooms = new HBoxContainer();
		zooms.AddThemeConstantOverride("separation", 6);
		foreach (var (label, h, d, fov) in new[] { ("Corps entier", 1.0f, 3.6f, 32f), ("Buste", 1.35f, 2.0f, 32f), ("Bras et mains", 1.25f, 1.45f, 32f) })
		{
			var b = new Button { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			float hh = h, dd = d, ff = fov;
			b.Pressed += () => { _camHeight = hh; _camDistance = dd; _camFov = ff; };
			zooms.AddChild(b);
		}
		box.AddChild(zooms);

		box.AddChild(new HSeparator());
		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 8);
		var random = new Button { Text = "Aléatoire", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		random.Pressed += () => { var rng = new RandomNumberGenerator(); rng.Randomize(); _app.Randomize(rng); Refresh(); };
		var reset = new Button { Text = "Par défaut", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		reset.Pressed += () => { _app = new CharacterAppearance(); Refresh(); };
		actions.AddChild(random);
		actions.AddChild(reset);
		box.AddChild(actions);

		var play = new Button { Text = "Enregistrer le personnage", CustomMinimumSize = new Vector2(0, 52) };
		play.Pressed += () =>
		{
			// le panneau Personnage range le résultat dans la bibliothèque et le choisit
			CharacterName = _nameEdit.Text.Trim();
			Confirmed?.Invoke(_app.Clone());
			QueueFree();
		};
		box.AddChild(play);
		var back = new Button { Text = "Retour" };
		back.Pressed += () => { Cancelled?.Invoke(); QueueFree(); };
		box.AddChild(back);
		return scroll;
	}

	private readonly List<string> _weaponIds = new();

	private static Label Section(string text)
	{
		var l = new Label { Text = text.ToUpperInvariant() };
		l.AddThemeColorOverride("font_color", new Color(0.55f, 0.75f, 1f));
		l.AddThemeFontSizeOverride("font_size", 14);
		return l;
	}

	private void AddSlider(VBoxContainer box, string key, string label, float min, float max, Action<float> set)
	{
		box.AddChild(new Label { Text = label });
		var s = new HSlider { MinValue = min, MaxValue = max, Step = 0.01, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 22) };
		s.ValueChanged += v => { if (_updating) return; set((float)v); Apply(); };
		box.AddChild(s);
		_sliders[key] = s;
	}

	private OptionButton AddOption(VBoxContainer box, string key, string label, List<string> items, Action<int> onSelected, int selected = 0)
	{
		box.AddChild(new Label { Text = label });
		var o = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		foreach (var it in items) o.AddItem(it);
		if (selected >= 0 && selected < items.Count) o.Select(selected);
		o.ItemSelected += i => { if (_updating) return; onSelected((int)i); Apply(); };
		box.AddChild(o);
		_options[key] = o;
		return o;
	}

	private void AddPieceOption(VBoxContainer box, string group, string label)
	{
		var names = new List<string> { "Aucun" };
		foreach (var it in CharacterKit.Items(group)) names.Add(it.Name);
		AddOption(box, group, label, names, i => _app.Pieces[group] = i - 1);
	}

	private Control BuildPreview()
	{
		_viewContainer = new SubViewportContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Stretch = true };
		var vp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X, HandleInputLocally = false };
		_viewContainer.AddChild(vp);

		vp.AddChild(new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.16f, 0.18f, 0.22f),
				AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.6f
			}
		});
		vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, 35, 0), LightEnergy = 1.1f, ShadowEnabled = true });
		vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-20, -140, 0), LightEnergy = 0.35f });

		var floor = new MeshInstance3D
		{
			Mesh = new CylinderMesh { TopRadius = 0.9f, BottomRadius = 0.9f, Height = 0.04f, RadialSegments = 48 },
			Position = new Vector3(0, -0.02f, 0),
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.11f, 0.14f), Roughness = 0.9f }
		};
		vp.AddChild(floor);

		_turntable = new Node3D { Name = "Turntable" };
		vp.AddChild(_turntable);
		_animator = new CharacterAnimator { Appearance = _app.Clone(), UseNinjaIdle = true };
		_turntable.AddChild(_animator);

		_camera = new Camera3D { Current = true, Fov = _camFov };
		vp.AddChild(_camera);

		_viewContainer.GuiInput += e =>
		{
			if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0)
				_yaw -= m.Relative.X * 0.01f;
		};
		return _viewContainer;
	}

	// ------------------------------------------------------------------ état

	private void SetView(float yaw)
	{
		if (yaw < 0) { _yaw = Mathf.Pi; _camHeight = 3.0f; _camDistance = 0.1f; } // dessus
		else _yaw = yaw;
	}

	private bool _top;

	/// <summary>Reporte l'apparence courante sur les contrôles.</summary>
	private void Refresh()
	{
		_updating = true;
		_sliders["gender"].Value = _app.Gender;
		_sliders["height"].Value = _app.Height;
		_sliders["weight"].Value = _app.Weight;
		_sliders["muscle"].Value = _app.Muscle;
		_sliders["age"].Value = _app.Age;
		_options["skin"].Select(Mathf.Clamp(_app.Skin, 0, Math.Max(0, CharacterKit.Skins.Count - 1)));
		foreach (var g in CharacterKit.Choosable) _options[g].Select(_app.Piece(g) + 1);
		_updating = false;
		Apply();
		UpdatePreviewState();
	}

	private void Apply() => _animator?.SetAppearance(_app.Clone());

	private void UpdatePreviewState()
	{
		if (_animator == null) return;
		string weapon = _weaponIds[Mathf.Clamp(_weaponOption.Selected, 0, _weaponIds.Count - 1)];
		_animator.Hold(weapon == null ? null : ItemDatabase.Get(weapon));
		(_velocity, _sliding) = _poseOption.Selected switch
		{
			1 => (new Vector3(0, 0, -2.4f), false),
			2 => (new Vector3(0, 0, -6.5f), false),
			3 => (new Vector3(0, 0, -9f), true),
			4 => (Vector3.Zero, false),
			_ => (Vector3.Zero, false)
		};
	}

	public override void _Process(double delta)
	{
		if (_animator == null) return;
		_animator.Attacking = _poseOption.Selected == 4 && _weaponOption.Selected == 0; // à mains nues seulement : un coup après l'autre
		_animator.Tick(_velocity, true, _sliding, delta);
		_turntable.Rotation = new Vector3(0, _yaw, 0);
		_camera.Fov = Mathf.Lerp(_camera.Fov, _camFov, 0.2f);
		var target = new Vector3(0, _camHeight, _camDistance);
		_camera.Position = _camera.Position.Lerp(target, 0.15f);
		_camera.LookAt(new Vector3(0, _camDistance < 0.5f ? 0.9f : _camHeight - 0.05f, 0), _camDistance < 0.5f ? Vector3.Back : Vector3.Up);
	}
}
