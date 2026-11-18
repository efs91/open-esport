using Godot;
using System;
using System.IO;
using Openesport.Modules;
using Openesport.Utils;
using Openesport.Structures;
using Openesport.Managers;
using Openesport.Inventory;


namespace Openesport.Modules
{
	public partial class CasualGames : Module
	{
		private LogManager _logManager;
		private MenuManager _menuManager;
		private SceneManager _sceneManager;
		private GameManager _gameManager;
		private string _mapPath = "res://scenes/maps/Frontiere01.tscn";
		private string configPath => "res://config/modules/casualgames.cfg";

		public override string ModuleName => "casualgames";

		protected override void SetupModule()
		{
			_logManager = GetNode<LogManager>(GlobalPaths.Managers.LOG_MANAGER);
			_menuManager = GetNode<MenuManager>(GlobalPaths.Managers.MENU_MANAGER);
			_sceneManager = GetNode<SceneManager>(GlobalPaths.Managers.SCENE_MANAGER);
			_gameManager = GetNode<GameManager>(GlobalPaths.Managers.GAME_MANAGER);

			LoadConfiguration();
			
			// (le lien « Partie standard » a été retiré : on joue en ligne)
			_menuManager.RegisterActionHandler("start_casual_game", this);

			// Parties en ligne : même carte, mais le serveur décide de tout
			var online = new MenuElement(
				id: 1249797513,
				name: "MENU_ONLINE_GAMES",
				type: MenuElementType.Button,
				parentId: 0,
				poids: 11,
				uiPath: "res://scenes/ui/menu/buttons/standard_button.tscn"
			);
			online.Properties["action"] = "start_online_game";
			_menuManager.AddElement(online);
			_menuManager.RegisterActionHandler("start_online_game", this);

			// Profil : ses personnages (choisir, créer, modifier) et ses statistiques / classements
			var character = new MenuElement(
				id: 1249797514,
				name: "MENU_CHARACTER",
				type: MenuElementType.Button,
				parentId: 0,
				poids: 9
			);
			character.Properties["action"] = "open_character_panel";
			_menuManager.AddElement(character);
			_menuManager.RegisterActionHandler("open_character_panel", this);
		}

		private void LoadConfiguration()
		{
			try
			{
				_logManager.Info($"[{ModuleName}] Tentative de chargement du fichier : {configPath}");
				
				string globalConfigPath = ProjectSettings.GlobalizePath(configPath);
				_logManager.Info($"[{ModuleName}] Chemin globalisé : {globalConfigPath}");
				
				if (File.Exists(globalConfigPath))
				{
					_logManager.Info($"[{ModuleName}] Fichier trouvé, lecture en cours");
					string[] lines = File.ReadAllLines(globalConfigPath);
					
					foreach (string line in lines)
					{
						if (line.StartsWith("map="))
						{
							_mapPath = line.Substring(4).Trim();
							_logManager.Info($"[{ModuleName}] Map trouvée dans la configuration : {_mapPath}");
							break;
						}
					}
				}
				else
				{
					_logManager.Warning($"[{ModuleName}] Fichier non trouvé à l'emplacement : {globalConfigPath}");
					_logManager.Info($"[{ModuleName}] Utilisation de la map par défaut : {_mapPath}");
				}
			}
			catch (Exception ex)
			{
				_logManager.Error($"[{ModuleName}] Erreur lors du chargement de la configuration : {ex.Message}");
				_logManager.Error($"[{ModuleName}] Stack trace : {ex.StackTrace}");
				_logManager.Info($"[{ModuleName}] Utilisation de la map par défaut : {_mapPath}");
			}
		}

		/// <summary>
		/// Connecte le joueur à un serveur. Adresse : option --server=hôte:port, sinon 127.0.0.1:27015.
		/// Nom : option --name=, sinon le nom de session Windows.
		/// </summary>
		private void StartOnline(Player player)
		{
			string host = Openesport.Online.MatchSettings.AssignedHost ?? "127.0.0.1", name = OS.GetEnvironment("USERNAME");
			int port = Openesport.Online.MatchSettings.AssignedHost != null ? Openesport.Online.MatchSettings.AssignedPort : 27015;
			foreach (var arg in OS.GetCmdlineUserArgs())
			{
				if (arg.StartsWith("--map=")) { _mapPath = arg.Substring(6); }
				else if (arg.StartsWith("--name=")) name = arg.Substring(7);
			}
			if (Openesport.Online.AccountClient.LoggedIn) name = Openesport.Online.AccountClient.Login;      // le pseudo en jeu est l'identifiant du compte
			if (string.IsNullOrWhiteSpace(name)) name = "Joueur";

			// pas de robot local : les autres joueurs viennent du serveur
			_sceneManager.CurrentScene.GetNodeOrNull("AI_player")?.QueueFree();

			var game = new Openesport.Online.OnlineGame { Name = "OnlineGame" };
			_sceneManager.CurrentScene.AddChild(game);
			game.Begin(player, _sceneManager.CurrentScene, host, port, name, Openesport.Online.OnlineGame.WorldPathFor(_mapPath));
			_logManager.Info($"[{ModuleName}] Partie en ligne : {host}:{port} en tant que {name}");
		}

		protected override void CleanupModule()
		{
			_logManager.Info($"[{ModuleName}] Nettoyage du module");
		}

		public override void Process(double delta)
		{
			if (!IsActive) return;
		}

		public override void ExecuteAction(string action)
		{
			if (action == "open_character_panel")
			{
				var panel = new Openesport.Menus.CharacterPanel { Name = "CharacterPanel", ShowStats = true };
				((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, panel);
				return;
			}

			if (action == "start_online_game")
			{
				// Multijoueur : d'abord le mode de jeu, puis le choix du personnage, puis la partie en ligne
				var modeScreen = new Openesport.Menus.ModeScreen { Name = "ModeScreen" };
				modeScreen.OnContinue = () =>
				{
					var panel = new Openesport.Menus.CharacterPanel { Name = "CharacterPanel" };
					// hors battle royale : après le personnage, on choisit son équipement de départ
					panel.OnPlay = () =>
					{
						if (Openesport.Online.MatchSettings.Mode == OpenEsport.Core.GameMode.BattleRoyale) { FindOnlineGame(); return; }
						var loadout = new Openesport.Menus.LoadoutScreen { Name = "LoadoutScreen", OnContinue = FindOnlineGame };
						loadout.OnBack = () =>
						{
							var again = new Openesport.Menus.CharacterPanel { Name = "CharacterPanel", OnPlay = panel.OnPlay };
							again.Closed += () => ExecuteAction("start_online_game");
							((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, again);
						};
						((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, loadout);
					};
					panel.Closed += () => ExecuteAction("start_online_game");       // retour : la page du mode
					((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, panel);
				};
				((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, modeScreen);
				return;
			}

			if (action != "start_casual_game" && action != "start_online_game")
			{
				_logManager.Warning($"[{ModuleName}] Action non supportée : {action}");
				return;
			}

			// la partie démarre tout de suite avec le personnage choisi dans le panneau Personnage
			StartGame(action);
		}

		/// <summary>
		/// Après le choix du personnage et du mode : le matchmaking trouve (ou lance) une partie de ce mode et donne l'adresse du serveur.
		/// Avec <c>--server=hôte:port</c> (développement), on se connecte directement.
		/// </summary>
		private void FindOnlineGame()
		{
			if (Openesport.Online.MatchSettings.TryDirectServer(out string host, out int port))
			{
				Openesport.Online.MatchSettings.AssignedHost = host; Openesport.Online.MatchSettings.AssignedPort = port;
				StartGame("start_online_game");
				return;
			}
			var screen = new Openesport.Menus.MatchmakingScreen
			{
				Name = "MatchmakingScreen",
				OnFound = (h, p) => { Openesport.Online.MatchSettings.AssignedHost = h; Openesport.Online.MatchSettings.AssignedPort = p; StartGame("start_online_game"); },
				OnCancel = () => ExecuteAction("start_online_game"),     // retour au choix du personnage
			};
			((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, screen);
		}

		private void StartGame(string action)
		{
			_logManager.Info($"[{ModuleName}] Exécution de l'action : {action}");

			bool online = action == "start_online_game";
			if (action != "start_casual_game" && !online)
			{
				_logManager.Warning($"[{ModuleName}] Action non supportée : {action}");
				return;
			}

			try
			{
				if (online) _mapPath = OpenEsport.Core.Modes.Get(Openesport.Online.MatchSettings.Mode).MapScene;      // chaque mode a sa carte
				foreach (var arg in OS.GetCmdlineUserArgs())
					if (arg.StartsWith("--map=")) _mapPath = arg.Substring(6);       // carte à charger (sinon celle du mode)
				if (string.IsNullOrEmpty(_mapPath))
				{
					_logManager.Error($"[{ModuleName}] Chemin de la map non défini");
					return;
				}

				_logManager.Info($"[{ModuleName}] Chargement de la map : {_mapPath}");
				_sceneManager.LoadScene(_mapPath);

				// Trouver le spawn point dans la scène chargée
				var spawnPoint = _sceneManager.CurrentScene.GetNode<Marker3D>("SpawnPoint");
				if (spawnPoint == null)
				{
					_logManager.Error($"[{ModuleName}] Spawn point non trouvé dans la scène");
					return;
				}

				// Charger le personnage
				_logManager.Info($"[{ModuleName}] Chargement du personnage");
				var characterScene = GD.Load<PackedScene>("res://scenes/modules/Player/player.tscn");
				var character = characterScene.Instantiate<FPSController3D>();

				// Ajouter le personnage à la scène AVANT de définir sa position
				_sceneManager.CurrentScene.AddChild(character);

				// Positionner le personnage sur le spawn point
				character.GlobalPosition = spawnPoint.GlobalPosition;
				character.GlobalRotation = spawnPoint.GlobalRotation;

				_logManager.Info($"[{ModuleName}] Personnage ajouté à la scène");

				if (online)
					StartOnline((Player)character);
				else
					// Armes, viseurs et sacs à dos posés au sol devant le point d'apparition
					LootSpawner.SpawnStarterLoot(_sceneManager.CurrentScene, spawnPoint.GlobalTransform);

				_gameManager.ChangeState(GameManager.GameState.Gameplay);
				_logManager.Info($"[{ModuleName}] Map chargée avec succès");
			}
			catch (Exception ex)
			{
				_logManager.Error($"[{ModuleName}] Erreur lors du chargement de la map : {ex.Message}");
				_logManager.Error($"[{ModuleName}] Stack trace : {ex.StackTrace}");
			}
		}
	}
} 
