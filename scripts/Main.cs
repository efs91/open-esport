using Godot;
using System;
using Openesport.Utils;
using Openesport.Managers;

public partial class Main : Node
{
	private GameManager _gameManager;
	private SceneManager _sceneManager;
	private MenuManager _menuManager;
	private InputManager _inputManager;
	private LogManager _logManager;
	public static string language = "français";
	/// <summary>Pour rouvrir la scène de connexion (déconnexion depuis le profil).</summary>
	public static Main Instance;
	private static string _currentScene = "res://scenes/menus/MainMenu.tscn";

	public override void _Ready()
	{
		_logManager = GetNode<LogManager>(GlobalPaths.Managers.LOG_MANAGER);
		Instance = this;
		_logManager.Info("[Main] _Ready appelé");
		language = LocalizationManager.SavedLanguage;
		LocalizationManager.LoadLanguage(language);
		InitializeManagers();
		StartGame();
	}

	private void InitializeManagers()
	{
		_logManager.Info("[Main] Initialisation des managers");
		_gameManager = GetNode<GameManager>(GlobalPaths.Managers.GAME_MANAGER);
		_sceneManager = GetNode<SceneManager>(GlobalPaths.Managers.SCENE_MANAGER);
		_menuManager = GetNode<MenuManager>(GlobalPaths.Managers.MENU_MANAGER);
		_inputManager = GetNode<InputManager>(GlobalPaths.Managers.INPUT_MANAGER);

		// Initialiser la configuration des touches
		InputConfig.Initialize(_logManager);
	}

	private void StartGame()
	{
		_logManager.Info("[Main] Démarrage du jeu");
		// Première scène : la connexion. Le menu principal ne s'affiche qu'une fois le joueur connecté.
		ShowLogin();
	}

	/// <summary>Affiche la scène de connexion par-dessus tout ; une fois connecté, on arrive au menu principal.</summary>
	public void ShowLogin()
	{
		var login = new Openesport.Menus.LoginScreen { Name = "LoginScreen" };
		login.OnSuccess = () => _gameManager.ChangeState(GameManager.GameState.MainMenu);
		GetTree().Root.CallDeferred(Node.MethodName.AddChild, login);
	}
}
