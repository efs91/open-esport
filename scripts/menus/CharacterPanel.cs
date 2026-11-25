using Openesport.Utils;
using System;
using Godot;

namespace Openesport.Menus
{
	/// <summary>
	/// Panneau « Personnage » : les personnages déjà créés (choisir celui avec lequel on joue), et la création, la
	/// modification ou la suppression d'un personnage avec l'éditeur MakeHuman. Le personnage choisi sert aux parties solo et en ligne.
	/// </summary>
	public partial class CharacterPanel : CanvasLayer
	{
		public event Action Closed;

		/// <summary>Si renseigné (menu Multijoueur) : le bouton principal choisit le personnage puis lance cette action (la partie en ligne).</summary>
		public Action OnPlay;

		/// <summary>Scène « Mon profil » : ajoute la colonne des statistiques et du classement (compte connecté).</summary>
		public bool ShowStats;

		private ItemList _list;
		private Button _choose, _edit, _delete;
		private Label _info;
		private CharacterAnimator _animator;
		private Node3D _turntable;
		private double _spin;

		public CharacterPanel() { Layer = 120; }

		private Camera3D _camera;
		private SubViewportContainer _previewBox;
		private Label _charName;

		public override void _Ready()
		{
			CharacterKit.EnsureLoaded();
			Input.MouseMode = Input.MouseModeEnum.Visible;

			var root = new Control { Theme = MenuStyle.SharedTheme };
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);
			root.AddChild(MenuStyle.Backdrop());

			var margin = new MarginContainer();
			margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
			root.AddChild(margin);
			var page = new VBoxContainer();
			page.AddThemeConstantOverride("separation", 14);
			margin.AddChild(page);

			// barre du haut : le titre de la page, et (profil) le compte connecté avec la déconnexion
			var top = new HBoxContainer();
			top.AddThemeConstantOverride("separation", 14);
			var title = new Label { Text = OnPlay != null ? I18n.T("Choisis ton personnage") : ShowStats ? I18n.T("Mon profil") : I18n.T("Personnages"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			title.AddThemeFontSizeOverride("font_size", 40);
			top.AddChild(title);
			if (ShowStats) AddAccountBar(top);
			page.AddChild(top);

			// le corps : la liste des personnages (carte étroite), le personnage en grand, les statistiques (carte)
			var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			body.AddThemeConstantOverride("separation", 16);
			page.AddChild(body);
			body.AddChild(MenuStyle.Card(BuildLeft(), 300));
			body.AddChild(BuildPreview());
			if (ShowStats)
			{
				var stats = MenuStyle.Card(new ProfileStats(), 430);
				stats.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				stats.SizeFlagsStretchRatio = 1.25f;
				body.AddChild(stats);
			}

			Refresh(CharacterLibrary.SelectedIndex);
		}

		/// <summary>En haut à droite du profil : « Connecté : identifiant » et le bouton de déconnexion.</summary>
		private void AddAccountBar(HBoxContainer top)
		{
			var who = new Label { Text = I18n.T("Connecté : {0}", Openesport.Online.AccountClient.Login), VerticalAlignment = VerticalAlignment.Center };
			who.AddThemeFontSizeOverride("font_size", 22);
			who.AddThemeColorOverride("font_color", MenuStyle.Muted);
			top.AddChild(who);
			var logout = new Button { Text = I18n.T("Se déconnecter"), CustomMinimumSize = new Vector2(170, 40) };
			logout.Pressed += async () =>
			{
				await Openesport.Online.AccountClient.LogoutAsync();
				QueueFree();
				Main.Instance?.ShowLogin();
			};
			top.AddChild(logout);
		}

		private Control BuildLeft()
		{
			var box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 8);

			var title = new Label { Text = I18n.T("Mes personnages") };
			title.AddThemeFontSizeOverride("font_size", 22);
			box.AddChild(title);

			_list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Single };
			_list.AddThemeFontSizeOverride("font_size", 18);
			_list.ItemSelected += i => ShowCharacter((int)i);
			_list.ItemActivated += i => ChooseIndex((int)i); // double-clic
			box.AddChild(_list);

			_info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_info.AddThemeColorOverride("font_color", MenuStyle.Muted);
			box.AddChild(_info);

			if (OnPlay != null)
			{
				var mode = new Label { Text = I18n.T("Mode : {0}", GameText.Label(Openesport.Online.MatchSettings.Mode, OpenEsport.Core.Modes.ValidTeamSize(Openesport.Online.MatchSettings.Mode, Openesport.Online.MatchSettings.TeamSize))) };
				box.AddChild(mode);
			}

			_choose = new Button { ThemeTypeVariation = "MenuPrimary", Text = OnPlay != null ? I18n.T("Jouer avec ce personnage") : I18n.T("Choisir ce personnage"), CustomMinimumSize = new Vector2(0, 48) };
			_choose.Pressed += () => ChooseIndex(Current());
			box.AddChild(_choose);

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 6);
			var create = new Button { Text = I18n.T("Nouveau"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			create.Pressed += () => OpenEditor(-1);
			_edit = new Button { Text = I18n.T("Modifier"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_edit.Pressed += () => OpenEditor(Current());
			_delete = new Button { Text = I18n.T("Supprimer"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_delete.Pressed += AskDelete;
			row.AddChild(create); row.AddChild(_edit); row.AddChild(_delete);
			box.AddChild(row);

			var back = new Button { Text = I18n.T("Retour"), CustomMinimumSize = new Vector2(0, 40) };
			back.Pressed += () => { Closed?.Invoke(); QueueFree(); };
			box.AddChild(back);
			return box;
		}

		private Control BuildPreview()
		{
			// le personnage se détache directement du décor : pas de fond (fenêtre 3D transparente)
			var holder = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) };
			holder.AddThemeConstantOverride("separation", 4);
			var container = new SubViewportContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Stretch = true };
			_previewBox = container;
			var vp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X, HandleInputLocally = false, TransparentBg = true };
			container.AddChild(vp);
			vp.AddChild(new WorldEnvironment
			{
				Environment = new Godot.Environment
				{
					AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.7f
				}
			});
			vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, 35, 0), LightEnergy = 1.1f, ShadowEnabled = true });
			vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-20, -140, 0), LightEnergy = 0.35f });
			vp.AddChild(new MeshInstance3D
			{
				Mesh = new CylinderMesh { TopRadius = 0.85f, BottomRadius = 0.85f, Height = 0.04f, RadialSegments = 48 },
				Position = new Vector3(0, -0.02f, 0),
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.06f, 0.09f, 0.75f), Roughness = 0.9f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha }
			});
			_turntable = new Node3D();
			vp.AddChild(_turntable);
			_animator = new CharacterAnimator { Appearance = CharacterLibrary.SelectedAppearance(), UseNinjaIdle = true };
			_turntable.AddChild(_animator);
			_camera = new Camera3D { Current = true, Fov = 32f, Position = new Vector3(0, 0.95f, 4.6f) };
			vp.AddChild(_camera);

			// drag pour tourner
			container.GuiInput += e =>
			{
				if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0)
					_spin -= m.Relative.X * 0.01f;
			};
			holder.AddChild(container);

			_charName = new Label { HorizontalAlignment = HorizontalAlignment.Center };
			_charName.AddThemeFontSizeOverride("font_size", 26);
			_charName.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
			_charName.AddThemeConstantOverride("outline_size", 8);
			holder.AddChild(_charName);
			var hint = new Label { Text = I18n.T("Glisse pour faire tourner"), HorizontalAlignment = HorizontalAlignment.Center };
			hint.AddThemeColorOverride("font_color", MenuStyle.Muted);
			holder.AddChild(hint);
			return holder;
		}

		public override void _Process(double delta)
		{
			if (_animator == null) return;
			_animator.Tick(Vector3.Zero, true, false, delta);
			_turntable.Rotation = new Vector3(0, (float)_spin + Mathf.Pi, 0);

			// le personnage entier doit toujours tenir dans la zone, même étroite : on recule la caméra si besoin
			if (_camera != null && _previewBox != null && _previewBox.Size.Y > 1f)
			{
				float aspect = _previewBox.Size.X / _previewBox.Size.Y;
				float t = Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f));
				float distance = Mathf.Max(1.3f / t, 0.9f / (t * Mathf.Max(aspect, 0.2f)));
				_camera.Position = new Vector3(0, 0.95f, distance);
			}
		}

		// ------------------------------------------------------------------ liste

		private int Current()
		{
			var sel = _list.GetSelectedItems();
			return sel.Length > 0 ? sel[0] : CharacterLibrary.SelectedIndex;
		}

		private void Refresh(int select)
		{
			_list.Clear();
			for (int i = 0; i < CharacterLibrary.All.Count; i++)
			{
				bool playing = i == CharacterLibrary.SelectedIndex;
				_list.AddItem((playing ? "✔  " : "     ") + CharacterLibrary.All[i].Name);
			}
			select = Math.Clamp(select, 0, CharacterLibrary.All.Count - 1);
			_list.Select(select);
			ShowCharacter(select);
		}

		private void ShowCharacter(int index)
		{
			var c = CharacterLibrary.All[index];
			_animator?.SetAppearance(c.Appearance.Clone());
			bool playing = index == CharacterLibrary.SelectedIndex;
			_info.Text = playing ? I18n.T("« {0} » est le personnage actuel.", c.Name) : $"« {c.Name} »";
			if (_charName != null) _charName.Text = c.Name;
			_choose.Disabled = playing && OnPlay == null;
			_delete.Disabled = CharacterLibrary.All.Count <= 1;
		}

		private void ChooseIndex(int index)
		{
			CharacterLibrary.Select(index);
			if (OnPlay != null)
			{
				// menu Multijoueur : le personnage est choisi, on rejoint la partie
				var play = OnPlay;
				QueueFree();
				play();
				return;
			}
			Refresh(index);
		}

		// ------------------------------------------------------------------ édition

		/// <summary>Ouvre l'éditeur : index &lt; 0 = nouveau personnage.</summary>
		private void OpenEditor(int index)
		{
			bool isNew = index < 0;
			var editor = new CharacterCreator
			{
				Name = "CharacterEditor",
				Layer = 130,
				Initial = isNew ? new CharacterAppearance() : CharacterLibrary.All[index].Appearance.Clone(),
				CharacterName = isNew ? "" : CharacterLibrary.All[index].Name
			};
			editor.Confirmed += app =>
			{
				if (isNew)
				{
					int created = CharacterLibrary.Add(editor.CharacterName, app); // le nouveau personnage devient le personnage actuel
					Refresh(created);
				}
				else
				{
					CharacterLibrary.Update(index, editor.CharacterName, app);
					Refresh(index);
				}
			};
			GetTree().Root.AddChild(editor);
		}

		private void AskDelete()
		{
			int index = Current();
			var dialog = new ConfirmationDialog
			{
				Title = I18n.T("Supprimer le personnage"),
				DialogText = I18n.T("Supprimer « {0} » ?", CharacterLibrary.All[index].Name),
				OkButtonText = I18n.T("Supprimer"),
				CancelButtonText = I18n.T("Annuler"),
				Exclusive = true
			};
			dialog.Confirmed += () => { CharacterLibrary.Remove(index); Refresh(CharacterLibrary.SelectedIndex); dialog.QueueFree(); };
			dialog.Canceled += dialog.QueueFree;
			AddChild(dialog);
			dialog.PopupCentered(new Vector2I(400, 130));
		}
	}
}
