using Godot;
using System;

namespace Openesport
{
	public partial class HUD : Node2D
	{
		private Label _speedLabel;
		private Label _gravityLabel;
		private Label _groundLabel;
		private Label _walkLabel;
		private Label _backLabel;
		private Label _strafLeftLabel;
		private Label _strafRightLabel;
		private Label _sprintLabel;
		private Label _crouchLabel;
		private Label _jumpLabel;
		private Label _slideLabel;
		private Label _ammoLabel;
		private Label _healthLabel;
		private float _lastYVelocity = 0f;
		private float _currentGravity = 0f;
		private FPSController3D _player;
		private Node _fpsHands;
		private HealthAbility3D _healthAbility;

		public override void _Ready()
		{
			AddToGroup("game_hud"); // caché pendant le menu (Échap)
			SetupHUD();
			_player = GetParent<FPSController3D>();
			_fpsHands = _player.GetNode("Head/Camera/WeaponManager");
			_fpsHands.Connect(WeaponManager.SignalName.UpdateAmmo, Callable.From((int magazine, int inventoryAmmo, string ammoType) => UpdateAmmo(magazine, inventoryAmmo)));
			
			_healthAbility = _player.GetNode<HealthAbility3D>("Health Ability 3D");
			if (_healthAbility != null)
			{
				_healthAbility.OnHealthChanged += UpdateHealth;
				UpdateHealth(_healthAbility.GetCurrentHealth());
			}
		}

		private void SetupHUD()
		{
			GD.Print("HUD: Setting up HUD");
			
			// Création des labels pour la vitesse et l'état au sol
			_speedLabel = CreateInfoLabel("Vitesse: 0", new Vector2(20, 20), Colors.White);
			_gravityLabel = CreateInfoLabel("Gravité: 0 | Friction: 0", new Vector2(200, 20), Colors.White);
			_groundLabel = CreateInfoLabel("Au sol: OUI", new Vector2(20, 50), Colors.Green);

			// Labels pour les drapeaux d'état sur une seule ligne
			int xOffset = 20;
			int yOffset = 80;
			int spacing = 150;

			_walkLabel = CreateStateLabel("Deplacements", xOffset, yOffset);
			xOffset += spacing;

			_sprintLabel = CreateStateLabel("Sprint", xOffset, yOffset);
			xOffset += spacing;

			_crouchLabel = CreateStateLabel("Crouch", xOffset, yOffset);
			xOffset += spacing;

			_jumpLabel = CreateStateLabel("Jump", xOffset, yOffset);
			xOffset += spacing;

			_slideLabel = CreateStateLabel("Slide", xOffset, yOffset);

			// Label pour les munitions en bas à gauche
			_ammoLabel = CreateInfoLabel("Munitions: 0/0", new Vector2(20, GetViewportRect().Size.Y - 40), Colors.White);
			
			// Label pour la santé au-dessus des munitions
			_healthLabel = CreateInfoLabel("Santé: 100/100", new Vector2(20, GetViewportRect().Size.Y - 80), Colors.Green);

			SetDebugVisible(false);      // le panneau de débogage reste caché tant qu'on n'appuie pas sur F3
			GD.Print("HUD: Setup complete");
		}

		private Label CreateInfoLabel(string text, Vector2 position, Color color)
		{
			var label = new Label();
			label.Text = text;
			label.Position = position;
			label.AddThemeColorOverride("font_color", color);
			AddChild(label);
			return label;
		}

		private Label CreateStateLabel(string text, int x, int y)
		{
			var label = new Label();
			label.Text = $"{text}: NON";
			label.Position = new Vector2(x, y);
			label.AddThemeColorOverride("font_color", Colors.Red);
			AddChild(label);
			return label;
		}

		public override void _PhysicsProcess(double delta)
		{
			// la fenêtre peut changer de taille : les libellés du bas restent collés au bord bas-gauche
			float bottom = GetViewportRect().Size.Y;
			if (_ammoLabel != null) _ammoLabel.Position = new Vector2(20, bottom - 40);
			if (_healthLabel != null) _healthLabel.Position = new Vector2(20, bottom - 80);
			// On va mettre à jour le HUD avec les informations du personnage
			// Cette partie sera complétée une fois que nous aurons accès au personnage
			UpdateHUD();
		}

		private void UpdateHUD()
		{
			if (_player == null) return;

			// En ligne, la santé vient du serveur
			var online = Openesport.Online.OnlineGame.Instance;
			if (online?.Client != null && online.Client.State == OpenEsport.Core.NetState.Playing)
			{
				UpdateHealth(online.Client.Health);
				if (_healthLabel != null) _healthLabel.Visible = false;     // en ligne, la vie et le bouclier ont leurs propres barres
			}

			// Informations de débogage (vitesse, sol, friction, glissade…) : cachées, F3 les affiche
			if (_debug)
			{
			// Vitesse
			if (_speedLabel != null)
			{
				var velocity = _player.Velocity;
				// Le contrôleur visuel local recalcule sa propre vitesse : en multijoueur,
				// afficher celle de la simulation prédite, réconciliée avec le serveur.
				if (online?.Client != null && online.Client.State == OpenEsport.Core.NetState.Playing)
				{
					var predictedVelocity = online.Client.Predicted.Vel;
					velocity = new Vector3(predictedVelocity.X, predictedVelocity.Y, predictedVelocity.Z);
				}
				var speed = new Vector2(velocity.X, velocity.Z).Length();
				_speedLabel.Text = $"Vitesse: {speed:F1} m/s";
			}

			// Gravité et friction
			if (_gravityLabel != null)
			{
				var currentYVelocity = _player.Velocity.Y;
				if (!_player.IsOnFloor())
				{
					_currentGravity = (currentYVelocity - _lastYVelocity) / (float)GetProcessDeltaTime();
				}
				else
				{
					_currentGravity = 0f;
				}
				_lastYVelocity = currentYVelocity;

				_gravityLabel.Text = $"Gravité: {_currentGravity:F1} | Friction: {_player.Friction:F3}";
				_gravityLabel.AddThemeColorOverride("font_color", _currentGravity < 0 ? Colors.Red : Colors.Green);
			}

			// État au sol
			if (_groundLabel != null)
			{
				_groundLabel.Text = $"Au sol: {(_player.IsOnFloor() ? "OUI" : "NON")}";
				_groundLabel.AddThemeColorOverride("font_color", _player.IsOnFloor() ? Colors.Green : Colors.Red);
			}

			// Walk
			if (_walkLabel != null)
			{
				var isWalking = _player.Velocity.Length() > 0.1f;
				_walkLabel.Text = $"Walk: {(isWalking ? "OUI" : "NON")}";
				_walkLabel.AddThemeColorOverride("font_color", isWalking ? Colors.Green : Colors.Red);
			}

			// Sprint
			if (_sprintLabel != null)
			{
				_sprintLabel.Text = $"Sprint: {(_player.IsSprinting() ? "OUI" : "NON")}";
				_sprintLabel.AddThemeColorOverride("font_color", _player.IsSprinting() ? Colors.Green : Colors.Red);
			}

			// Crouch
			if (_crouchLabel != null)
			{
				_crouchLabel.Text = $"Crouch: {(_player.IsCrouching() ? "OUI" : "NON")}";
				_crouchLabel.AddThemeColorOverride("font_color", _player.IsCrouching() ? Colors.Green : Colors.Red);
			}

			// Jump
			if (_jumpLabel != null)
			{
				var isJumping = !_player.IsOnFloor();
				_jumpLabel.Text = $"Jump: {(isJumping ? "OUI" : "NON")}";
				_jumpLabel.AddThemeColorOverride("font_color", isJumping ? Colors.Green : Colors.Red);
			}

			// Slide
			if (_slideLabel != null)
			{
				var slideAbility = _player.GetNode<SlideAbility3D>("Slide Ability 3D");
				var isSliding = slideAbility != null && slideAbility.IsActived();
				_slideLabel.Text = $"Slide: {(isSliding ? "OUI" : "NON")}";
				_slideLabel.AddThemeColorOverride("font_color", isSliding ? Colors.Green : Colors.Red);
			}
			}
		}

		private bool _debug;
		private Label[] _debugLabels;

		/// <summary>F3 : affiche ou cache le panneau de débogage (vitesse, sol, friction, glissade…). Caché par défaut.</summary>
		public override void _UnhandledInput(InputEvent e)
		{
			if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
			{
				_debug = !_debug;
				SetDebugVisible(_debug);
			}
		}

		private void SetDebugVisible(bool on)
		{
			_debugLabels ??= new[] { _speedLabel, _gravityLabel, _groundLabel, _walkLabel, _sprintLabel, _crouchLabel, _jumpLabel, _slideLabel };
			foreach (var l in _debugLabels) if (l != null) l.Visible = on;
		}

		public void UpdateAmmo(int magazine, int inventoryAmmo)
		{
			if (_ammoLabel != null)
			{
				_ammoLabel.Text = $"Munitions: {magazine}/{inventoryAmmo}";
			}
		}

		private void UpdateHealth(float currentHealth)
		{
			if (_healthLabel != null && _healthAbility != null)
			{
				float maxHealth = _healthAbility.MaxHealth;
				_healthLabel.Text = $"Santé: {currentHealth}/{maxHealth}";
				
				// Changer la couleur en fonction de la santé
				if (currentHealth > maxHealth * 0.5f)
					_healthLabel.AddThemeColorOverride("font_color", Colors.Green);
				else if (currentHealth > maxHealth * 0.25f)
					_healthLabel.AddThemeColorOverride("font_color", Colors.Yellow);
				else
					_healthLabel.AddThemeColorOverride("font_color", Colors.Red);
			}
		}
	}
}	
