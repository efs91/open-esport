using Openesport.Utils;
using Godot;
using System.Linq;
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
	private Label _balance;
	private bool _dirty, _posePreview, _faceFocus;
	private MeshInstance3D _eyes;
	private Vector3 _eyeLandmark;
	private int _sampleEyes;
	private readonly Dictionary<string,HSlider> _detailSliders = new();

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
		_animator.SetModelingPose(true);
		_eyes = _animator.FindChild("eyes_low_poly", true, false) as MeshInstance3D;
		Refresh();
	}

	// ------------------------------------------------------------------ interface

	private Control BuildPanel()
	{
		var panel = new VBoxContainer { CustomMinimumSize = new Vector2(400,0) };
		panel.AddThemeConstantOverride("separation", 12);
		var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		VBoxContainer Page(string name)
		{
			var scroller = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			tabs.AddChild(scroller);
			var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			content.AddThemeConstantOverride("separation", 8);
			scroller.AddChild(content);
			return content;
		}
		var box = panel;
		var title = new Label { Text = I18n.T("Création du personnage") };
		title.AddThemeFontSizeOverride("font_size", 26);
		box.AddChild(title);


		_nameEdit = new LineEdit { Text = CharacterName, PlaceholderText = I18n.T("Nom du personnage"), MaxLength = 24 };
		box.AddChild(_nameEdit);

		panel.AddChild(tabs);
		box = Page(I18n.T("Gabarit"));
		AddSlider(box, "gender", I18n.T("Masculin  ↔  Féminin"), 0f, 1f, v => _app.Gender = v);
		AddSlider(box, "height", I18n.T("Taille"), -1f, 1f, v => _app.Height = v);
		AddSlider(box, "weight", I18n.T("Corpulence"), -1f, 1f, v => _app.Weight = v);
		AddSlider(box, "muscle", I18n.T("Musculature"), -1f, 1f, v => _app.Muscle = v);
		AddSlider(box, "age", I18n.T("Âge"), 18f, 90f, _app.SetYears);
		_balance = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		box.AddChild(_balance);
		BuildDetails(Page(I18n.T("Détails")));
		box = Page(I18n.T("Style"));

		box.AddChild(Section(I18n.T("Apparence")));
		var skins = new List<string>();
		foreach (var s in CharacterKit.Skins) skins.Add(s.Name);
		AddOption(box, "skin", I18n.T("Peau"), skins, i => _app.Skin = i);
		AddPieceOption(box, "clothes", I18n.T("Tenue"));
		AddPieceOption(box, "shoes", I18n.T("Chaussures"));
		AddPieceOption(box, "headwear", I18n.T("Couvre-chef"));
		AddPieceOption(box, "hair", I18n.T("Cheveux"));
		AddPieceOption(box, "eyebrows", I18n.T("Sourcils"));

		box = Page(I18n.T("Aperçu"));
		var weapons = new List<string> { I18n.T("Mains nues") };
		_weaponIds.Clear(); _weaponIds.Add(null);
		foreach (var id in ItemDatabase.Ids)
		{
			var d = ItemDatabase.Get(id);
			if (d != null && d.Type == ItemType.Weapon) { weapons.Add(d.DisplayName); _weaponIds.Add(id); }
		}
		_weaponOption = AddOption(box, "weapon", I18n.T("Arme en main"), weapons, _ => UpdatePreviewState(), selected: 0);
		_poseOption = AddOption(box, "pose", I18n.T("Animation"), new List<string> { I18n.T("Repos"), I18n.T("Marche"), I18n.T("Course"), I18n.T("Glissade"), I18n.T("Coup de poing") }, _ => UpdatePreviewState());

		var views = new HBoxContainer();
		views.AddThemeConstantOverride("separation", 6);
		foreach (var (label, yaw) in new[] { (I18n.T("Face"), Mathf.Pi), (I18n.T("Profil"), Mathf.Pi / 2f), (I18n.T("Dos"), 0f), (I18n.T("Dessus"), -1f) })
		{
			var b = new Button { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			float y = yaw;
			b.Pressed += () => SetView(y);
			views.AddChild(b);
		}
		box.AddChild(views);
		var zooms = new HBoxContainer();
		zooms.AddThemeConstantOverride("separation", 6);
		foreach (var (label, h, d, fov) in new[] { (I18n.T("Corps entier"), 1.0f, 3.6f, 32f), (I18n.T("Buste"), 1.35f, 2.0f, 32f), (I18n.T("Bras et mains"), 1.25f, 1.45f, 32f) })
		{
			var b = new Button { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			float hh = h, dd = d, ff = fov;
			b.Pressed += () => { _camHeight = hh; _camDistance = dd; _camFov = ff; };
			zooms.AddChild(b);
		}
		box.AddChild(zooms);

		box = panel;
		box.AddChild(new HSeparator());
		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 8);
		var random = new Button { Text = I18n.T("Aléatoire"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		random.Pressed += () => { var rng = new RandomNumberGenerator(); rng.Randomize(); _app.Randomize(rng); Refresh(); };
		var reset = new Button { Text = I18n.T("Par défaut"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		reset.Pressed += () => { _app = new CharacterAppearance(); Refresh(); };
		actions.AddChild(random);
		actions.AddChild(reset);
		box.AddChild(actions);

		var play = new Button { Text = I18n.T("Enregistrer le personnage"), CustomMinimumSize = new Vector2(0, 52) };
		var primary = new StyleBoxFlat { BgColor = new Color("#286a62") };
		primary.SetCornerRadiusAll(6);
		play.AddThemeStyleboxOverride("normal", primary);
		var hover = (StyleBoxFlat)primary.Duplicate();
		hover.BgColor = new Color("#36877d");
		play.AddThemeStyleboxOverride("hover", hover);
		play.Pressed += () =>
		{
			// le panneau Personnage range le résultat dans la bibliothèque et le choisit
			CharacterName = _nameEdit.Text.Trim();
			Confirmed?.Invoke(_app.Clone());
			QueueFree();
		};
		box.AddChild(play);
		var back = new Button { Text = I18n.T("Retour") };
		back.Pressed += () => { Cancelled?.Invoke(); QueueFree(); };
		box.AddChild(back);
		return panel;
	}

	private void BuildDetails(VBoxContainer box)
	{
		CharacterDetails.LoadCatalog();
		var region = new OptionButton();
		var zones = CharacterDetails.Dials.Select(d => d.Page).Distinct().OrderBy(p => p).ToList();
		foreach (var zone in zones) region.AddItem(zone);
		region.Select(zones.IndexOf("Visage"));
		var section = new OptionButton();
		var search = new LineEdit { PlaceholderText = I18n.T("Chercher dans toutes les régions"), ClearButtonEnabled = true };
		var list = new VBoxContainer();
		box.AddChild(search);
		box.AddChild(region);
		box.AddChild(section);
		box.AddChild(list);

		void Show()
		{
			_detailSliders.Clear();
			foreach (var child in list.GetChildren()) { list.RemoveChild(child); child.QueueFree(); }
			string query = search.Text.Trim();
			region.Visible = section.Visible = query.Length == 0;
			foreach (var d in CharacterDetails.Dials)
			{
				bool match = query.Length == 0
					? d.Page == region.Text && d.Section == section.Text
					: (d.Label + " " + d.Section + " " + d.Id).Contains(query,StringComparison.OrdinalIgnoreCase);
				if (!match) continue;
				string label = query.Length == 0 ? d.Label : d.Section + " · " + d.Label;
				var slider = Slider(list, label, d.Signed ? -1 : 0, 1, .01, _app.Detail(d.Id), v =>
				{
					_app.SetDetail(d.Id, v);
					if (d.Id.StartsWith("macrodetails/")) RefreshDetails();
					if (query.Length > 0 && d.Face) { _faceFocus = true; _camDistance = .78f; }
					_dirty = true;
				});
				_detailSliders[d.Id] = slider;
			}
			if (_detailSliders.Count == 0) list.AddChild(new Label { Text = I18n.T("Aucun réglage trouvé") });
		}
		void FocusRegion()
		{
			string part = section.Text.ToLowerInvariant();
			bool face = region.Text == "Visage";
			_faceFocus = face;
			_camHeight = face ? 1.61f : part.Contains("pied") || part.Contains("cheville") ? .16f :
				part.Contains("jambe") || part.Contains("cuisse") ? .63f : 1.05f;
			_camDistance = face ? .78f : _camHeight < 1 ? 1.65f : 3.6f;
			_camFov = 32;
		}
		void Sections()
		{
			section.Clear();
			foreach (var name in CharacterDetails.Dials.Where(d => d.Page == region.Text).Select(d => d.Section).Distinct().OrderBy(n=>n))
				section.AddItem(name);
			int first = 0;
			for (int i = 0; i < section.ItemCount; i++)
				if (section.GetItemText(i) == "Forme de la tête") first = i;
			section.Select(first);
			Show();
		}
		Sections();
		region.ItemSelected += _ => { Sections(); FocusRegion(); };
		section.ItemSelected += _ => { Show(); FocusRegion(); };
		search.TextChanged += _ => Show();
		var tabs = (TabContainer)box.GetParent().GetParent();
		tabs.TabChanged += index =>
		{
			_posePreview = index == 3;
			_sampleEyes = 2;
			UpdatePreviewState();
			_animator?.SetModelingPose(!_posePreview);
			if (index == 1) FocusRegion();
			else { _faceFocus = false; _camHeight = 1; _camDistance = 3.6f; }
		};
	}

	private void RefreshDetails()
	{
		bool updating = _updating;
		_updating = true;
		foreach (var kv in _detailSliders) kv.Value.Value = _app.Detail(kv.Key);
		_updating = updating;
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
		_sliders[key] = Slider(box,label,min,max,key == "age" ? 1 : .01,min,v => { set(v); _dirty = true; });
	}

	private HSlider Slider(VBoxContainer box, string label, float min, float max, double step, float value, Action<float> set)
	{
		var header = new HBoxContainer();
		header.AddChild(new Label { Text = label, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var number = new Label { Modulate = new Color("#93dcca") };
		string Format(double v) => step == 1 ? $"{v:0} ans" : $"{v * 100:0} %";
		number.Text = Format(value);
		header.AddChild(number);
		box.AddChild(header);
		var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(0, 24) };
		slider.ValueChanged += v => { number.Text = Format(v); if (!_updating) set((float)v); };
		box.AddChild(slider);
		return slider;
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
		var names = new List<string> { I18n.T("Aucun") };
		foreach (var it in CharacterKit.Items(group)) names.Add(group == "headwear" ? I18n.T(it.Name) : it.Name);
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



	/// <summary>Reporte l'apparence courante sur les contrôles.</summary>
	private void Refresh()
	{
		_updating = true;
		_sliders["gender"].Value = _app.Gender;
		_sliders["height"].Value = _app.Height;
		_sliders["weight"].Value = _app.Weight;
		_sliders["muscle"].Value = _app.Muscle;
		_sliders["age"].Value = _app.Years;
		RefreshDetails();
		_options["skin"].Select(Mathf.Clamp(_app.Skin, 0, Math.Max(0, CharacterKit.Skins.Count - 1)));
		foreach (var g in CharacterKit.Choosable) _options[g].Select(_app.Piece(g) + 1);
		_updating = false;
		Apply();
		UpdatePreviewState();
	}

	private void Apply()
	{
		_dirty = false;
		_sampleEyes = 2;
		_animator?.SetAppearance(_app.Clone());
		var look = _app.Pack();
		float speed = OpenEsport.Core.BodyBalance.Speed(look);
		var run = new OpenEsport.Core.Vec3(OpenEsport.Core.Sim.BaseSpeed * OpenEsport.Core.Sim.SprintMultiplier * speed, 0, 0);
		float rest = OpenEsport.Core.BodyBalance.BulletDamage(look, OpenEsport.Core.Vec3.Zero);
		float sprint = OpenEsport.Core.BodyBalance.BulletDamage(look, run);
		_balance.Text = $"Vitesse à pied    {speed * 100:0.#} %\nDégâts de balle reçus    {rest * 100:0.#} % à l’arrêt  ·  {sprint * 100:0.#} % au sprint";
	}

	private void UpdatePreviewState()
	{
		if (_animator == null) return;
		string weapon = _weaponIds[Mathf.Clamp(_weaponOption.Selected, 0, _weaponIds.Count - 1)];
		_animator.Hold(!_posePreview || weapon == null ? null : ItemDatabase.Get(weapon));
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
		if (_dirty) Apply();
		_animator.Attacking = _poseOption.Selected == 4 && _weaponOption.Selected == 0; // à mains nues seulement : un coup après l'autre
		if (_posePreview) _animator.Tick(_velocity, true, _sliding, delta);
		_turntable.Rotation = new Vector3(0, _yaw, 0);
		_camera.Fov = Mathf.Lerp(_camera.Fov, _camFov, 0.2f);
		var focus = new Vector3(0, _camDistance < .5f ? .9f : _camHeight - .05f, 0);
		if (_sampleEyes > 0 && --_sampleEyes == 0 && _eyes != null)
		{
			using var deformed = _eyes.BakeMeshFromCurrentSkeletonPose();
			if (deformed != null)
				_eyeLandmark = _turntable.ToLocal(_eyes.ToGlobal(deformed.GetAabb().GetCenter()));
		}
		if (_faceFocus && _eyeLandmark != Vector3.Zero)
			focus = _turntable.ToGlobal(_eyeLandmark) + Vector3.Down * .035f;
		var target = _faceFocus ? focus + Vector3.Back * _camDistance : new Vector3(0, _camHeight, _camDistance);
		_camera.Position = _camera.Position.Lerp(target, 0.15f);
		_camera.LookAt(focus, _camDistance < 0.5f ? Vector3.Back : Vector3.Up);
	}
}
