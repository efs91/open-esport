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

		private ItemList _list;
		private Button _choose, _edit, _delete;
		private Label _info;
		private CharacterAnimator _animator;
		private Node3D _turntable;
		private double _spin;

		public CharacterPanel() { Layer = 120; }

		public override void _Ready()
		{
			CharacterKit.EnsureLoaded();
			Input.MouseMode = Input.MouseModeEnum.Visible;

			var root = new Control();
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);
			root.AddChild(new ColorRect { Color = new Color(0.07f, 0.08f, 0.1f), AnchorRight = 1, AnchorBottom = 1 });

			var split = new HBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 16, OffsetTop = 16, OffsetRight = -16, OffsetBottom = -16 };
			split.AddThemeConstantOverride("separation", 16);
			root.AddChild(split);
			split.AddChild(BuildLeft());
			split.AddChild(BuildPreview());

			Refresh(CharacterLibrary.SelectedIndex);
		}

		private Control BuildLeft()
		{
			var box = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
			box.AddThemeConstantOverride("separation", 8);

			var title = new Label { Text = OnPlay != null ? "Choisis ton personnage" : "Personnages" };
			title.AddThemeFontSizeOverride("font_size", 26);
			box.AddChild(title);
			box.AddChild(new Label { Text = "Choisis le personnage avec lequel tu joues, ou crée-en un nouveau." });

			_list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Single };
			_list.ItemSelected += i => ShowCharacter((int)i);
			_list.ItemActivated += i => ChooseIndex((int)i); // double-clic
			box.AddChild(_list);

			_info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			box.AddChild(_info);

			if (OnPlay != null)
			{
				box.AddChild(new Label { Text = "Mode : " + OpenEsport.Core.Modes.Label(Openesport.Online.MatchSettings.Mode, OpenEsport.Core.Modes.ValidTeamSize(Openesport.Online.MatchSettings.Mode, Openesport.Online.MatchSettings.TeamSize)) });
			}

			_choose = new Button { Text = OnPlay != null ? "Jouer en ligne avec ce personnage" : "Jouer avec ce personnage", CustomMinimumSize = new Vector2(0, 48) };
			_choose.Pressed += () => ChooseIndex(Current());
			box.AddChild(_choose);

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 8);
			var create = new Button { Text = "Nouveau", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			create.Pressed += () => OpenEditor(-1);
			_edit = new Button { Text = "Modifier", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_edit.Pressed += () => OpenEditor(Current());
			_delete = new Button { Text = "Supprimer", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_delete.Pressed += AskDelete;
			row.AddChild(create); row.AddChild(_edit); row.AddChild(_delete);
			box.AddChild(row);

			var back = new Button { Text = "Retour" };
			back.Pressed += () => { Closed?.Invoke(); QueueFree(); };
			box.AddChild(back);
			return box;
		}

		private Control BuildPreview()
		{
			var container = new SubViewportContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Stretch = true };
			var vp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X, HandleInputLocally = false };
			container.AddChild(vp);
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
			vp.AddChild(new MeshInstance3D
			{
				Mesh = new CylinderMesh { TopRadius = 0.9f, BottomRadius = 0.9f, Height = 0.04f, RadialSegments = 48 },
				Position = new Vector3(0, -0.02f, 0),
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.11f, 0.14f), Roughness = 0.9f }
			});
			_turntable = new Node3D();
			vp.AddChild(_turntable);
			_animator = new CharacterAnimator { Appearance = CharacterLibrary.SelectedAppearance(), UseNinjaIdle = true };
			_turntable.AddChild(_animator);
			var camera = new Camera3D { Current = true, Fov = 32f, Position = new Vector3(0, 1.0f, 4.4f) };
			vp.AddChild(camera);
			camera.LookAt(new Vector3(0, 0.95f, 0));

			// drag pour tourner
			container.GuiInput += e =>
			{
				if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0)
					_spin -= m.Relative.X * 0.01f;
			};
			return container;
		}

		public override void _Process(double delta)
		{
			if (_animator == null) return;
			_animator.Tick(Vector3.Zero, true, false, delta);
			_turntable.Rotation = new Vector3(0, (float)_spin + Mathf.Pi, 0);
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
			_info.Text = playing ? $"« {c.Name} » est le personnage actuel." : $"« {c.Name} »";
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
				Title = "Supprimer le personnage",
				DialogText = $"Supprimer « {CharacterLibrary.All[index].Name} » ?",
				OkButtonText = "Supprimer",
				CancelButtonText = "Annuler",
				Exclusive = true
			};
			dialog.Confirmed += () => { CharacterLibrary.Remove(index); Refresh(CharacterLibrary.SelectedIndex); dialog.QueueFree(); };
			dialog.Canceled += dialog.QueueFree;
			AddChild(dialog);
			dialog.PopupCentered(new Vector2I(400, 130));
		}
	}
}
