using System;
using Godot;
using Openesport.Online;

namespace Openesport.Menus
{
	/// <summary>
	/// Base des écrans de connexion et de création de compte : même décor que le menu principal (image de fond, logo, titre), un formulaire au centre,
	/// un message d'état en dessous. Ils couvrent tout l'écran tant que le joueur n'est pas connecté.
	/// </summary>
	public abstract partial class AuthScreen : CanvasLayer
	{
		/// <summary>Appelé une fois le joueur connecté.</summary>
		public Action OnSuccess;

		protected VBoxContainer Form;
		protected Label Status;
		protected readonly System.Collections.Generic.List<Control> Inputs = new();

		protected AuthScreen() { Layer = 200; }

		protected abstract string Subtitle { get; }
		protected abstract void BuildForm();

		public override void _Ready()
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
			var root = new Control();
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			AddChild(root);

			// le décor du menu principal : image de fond, voile sombre, logo et titre
			var bg = new TextureRect
			{
				Texture = GD.Load<Texture2D>("res://assets/textures/Menu/MenuBackgroug.png"),
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(bg);
			var veil = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
			veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(veil);

			var center = new CenterContainer();
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.AddChild(center);
			Form = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
			Form.AddThemeConstantOverride("separation", 14);
			center.AddChild(Form);

			Form.AddChild(new TextureRect
			{
				Texture = GD.Load<Texture2D>("res://assets/textures/logo/logo_294x155.png"),
				CustomMinimumSize = new Vector2(0, 120), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
			});
			var title = new Label { Text = "Open Esport 2025", HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 64);
			Form.AddChild(title);
			var sub = new Label { Text = Subtitle, HorizontalAlignment = HorizontalAlignment.Center };
			sub.AddThemeFontSizeOverride("font_size", 26);
			Form.AddChild(sub);

			BuildForm();

			Status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 40) };
			Form.AddChild(Status);
		}

		protected LineEdit Field(string placeholder, bool secret = false, string text = "")
		{
			var e = new LineEdit { PlaceholderText = placeholder, Secret = secret, Text = text, CustomMinimumSize = new Vector2(0, 42) };
			e.AddThemeFontSizeOverride("font_size", 20);
			Form.AddChild(e);
			Inputs.Add(e);
			return e;
		}

		protected Button Action(string text, Action pressed, bool main = false)
		{
			var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, main ? 48 : 40), Flat = !main };
			b.Pressed += pressed;
			Form.AddChild(b);
			Inputs.Add(b);
			return b;
		}

		protected void Say(string text, bool error = false)
		{
			Status.Text = text;
			Status.AddThemeColorOverride("font_color", error ? new Color(1f, 0.45f, 0.4f) : new Color(0.85f, 0.88f, 0.92f));
		}

		protected void Busy(bool busy, string text = "")
		{
			foreach (var i in Inputs) { if (i is Button b) b.Disabled = busy; else if (i is LineEdit l) l.Editable = !busy; }
			if (busy) Say(text);
		}

		protected void Done()
		{
			var done = OnSuccess;
			QueueFree();
			done?.Invoke();
		}
	}

	/// <summary>Première scène du jeu : identifiant et mot de passe, avec un lien vers la création de compte. Reprend tout seul la session mémorisée.</summary>
	public partial class LoginScreen : AuthScreen
	{
		private LineEdit _login, _password;
		private CheckBox _remember;
		private bool _resuming;

		protected override string Subtitle => "Connexion";

		protected override void BuildForm()
		{
			_login = Field("Identifiant", text: AccountClient.LastLogin);
			_password = Field("Mot de passe", secret: true);
			_remember = new CheckBox { Text = "Rester connecté sur cet ordinateur", ButtonPressed = true };
			Form.AddChild(_remember);
			Inputs.Add(_remember);
			_login.TextSubmitted += _ => _password.GrabFocus();
			_password.TextSubmitted += _ => Submit();
			Action("Se connecter", Submit, main: true);
			Action("Pas encore de compte ? Créer un compte", OpenRegister);
			Action("Quitter", () => GetTree().Quit());
			CallDeferred(nameof(Start));
		}

		private async void Start()
		{
			// session mémorisée : on se reconnecte sans rien demander
			_resuming = true;
			Busy(true, "Reconnexion…");
			bool ok = false;
			try { ok = await AccountClient.ResumeAsync(); } catch (Exception e) { GD.PrintErr("[Compte] reprise de session : " + e.Message); }
			_resuming = false;
			if (!IsInstanceValid(this)) return;
			if (ok) { Done(); return; }
			Busy(false);
			Say("");
			(string.IsNullOrEmpty(_login.Text) ? _login : _password).GrabFocus();
		}

		private async void Submit()
		{
			if (_resuming) return;
			string login = _login.Text.Trim();
			if (login.Length == 0 || _password.Text.Length == 0) { Say("Entre ton identifiant et ton mot de passe.", true); return; }
			Busy(true, "Connexion…");
			string error = await AccountClient.LoginAsync(login, _password.Text, _remember.ButtonPressed);
			if (!IsInstanceValid(this)) return;
			if (error != null) { Busy(false); _password.Text = ""; Say(error, true); _password.GrabFocus(); return; }
			Done();
		}

		private void OpenRegister()
		{
			var register = new RegisterScreen { Name = "RegisterScreen" };
			register.OnSuccess = () => { var done = OnSuccess; QueueFree(); done?.Invoke(); };
			register.OnBack = () => Visible = true;
			Visible = false;
			GetTree().Root.AddChild(register);
		}
	}

	/// <summary>Création de compte : identifiant, mot de passe (deux fois). Une fois créé, le joueur est connecté.</summary>
	public partial class RegisterScreen : AuthScreen
	{
		public Action OnBack;
		private LineEdit _login, _password, _confirm;

		protected override string Subtitle => "Créer un compte";

		protected override void BuildForm()
		{
			_login = Field("Identifiant (3 à 20 caractères : lettres, chiffres, _ et -)");
			_password = Field("Mot de passe (8 caractères au moins)", secret: true);
			_confirm = Field("Confirme le mot de passe", secret: true);
			_login.TextSubmitted += _ => _password.GrabFocus();
			_password.TextSubmitted += _ => _confirm.GrabFocus();
			_confirm.TextSubmitted += _ => Submit();
			Action("Créer le compte", Submit, main: true);
			Action("Retour à la connexion", Back);
			CallDeferred(nameof(FocusLogin));
		}

		private void FocusLogin() => _login.GrabFocus();

		private async void Submit()
		{
			string login = _login.Text.Trim();
			if (login.Length < 3) { Say("L'identifiant doit faire au moins 3 caractères.", true); return; }
			if (_password.Text.Length < 8) { Say("Le mot de passe doit faire au moins 8 caractères.", true); return; }
			if (_password.Text != _confirm.Text) { Say("Les deux mots de passe ne sont pas identiques.", true); _confirm.GrabFocus(); return; }
			Busy(true, "Création du compte…");
			string error = await AccountClient.RegisterAsync(login, _password.Text);
			if (!IsInstanceValid(this)) return;
			if (error != null) { Busy(false); Say(error, true); return; }
			Done();
		}

		private void Back()
		{
			var back = OnBack;
			QueueFree();
			back?.Invoke();
		}
	}
}
