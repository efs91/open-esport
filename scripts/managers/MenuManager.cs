using Godot;
using Openesport.Menus;
using System;
using System.Collections.Generic;
using Openesport.Structures;
using System.Linq;
using Openesport.Utils;
using Openesport.Modules;
using Openesport.Managers;

public partial class MenuManager : Control
{
    private GameManager _gameManager;
    private Node _currentMenu;
    private LogManager _logManager;
    private Dictionary<int, MenuElement> _menuElements = new Dictionary<int, MenuElement>();
    private Dictionary<string, Module> _actionHandlers = new Dictionary<string, Module>();
    private const int RETURN_BUTTON_WEIGHT = -1; // Poids spécial pour le bouton retour
    /// <summary>Vrai quand le menu est ouvert depuis une partie (Échap) : pas de nouvelle partie, « Quitter » ramène au menu principal.</summary>
    private bool _inGame;
    /// <summary>Le compte connecté, affiché en haut à droite du menu.</summary>
    private Label _accountLabel;
    /// <summary>Tournois, groupe et amis (en haut à gauche) : seulement dans le menu principal, pas pendant une partie.</summary>
    private Openesport.Menus.MenuSocial _social;
    private Openesport.Menus.MenuAccountCard _accountCard;

    public override void _Ready()
    {
        Theme = MenuStyle.SharedTheme;
        GetNode<TextureRect>("Background").Texture = GD.Load<Texture2D>(MenuStyle.BackgroundPath);
        GetNode<TextureRect>("Background").StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        GetNode<TextureRect>("Background").MouseFilter = MouseFilterEnum.Ignore;
        GetNode<TextureRect>("Background").AddChild(MenuStyle.Veil());
        _logManager = GetNode<LogManager>(GlobalPaths.Managers.LOG_MANAGER);
        _accountLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = MouseFilterEnum.Ignore };
        _accountLabel.AddThemeFontSizeOverride("font_size", 22);
        _accountLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _accountLabel.AddThemeConstantOverride("outline_size", 6);
        _accountLabel.SetAnchorsPreset(LayoutPreset.TopRight);
        _accountLabel.OffsetLeft = -420; _accountLabel.OffsetRight = -24; _accountLabel.OffsetTop = 18; _accountLabel.OffsetBottom = 52;
        AddChild(_accountLabel);
        _social = new Openesport.Menus.MenuSocial { Name = "MenuSocial" };
        AddChild(_social);
        // sous le nom du compte : classement général, dernière partie et lien vers le profil complet
        _accountCard = new Openesport.Menus.MenuAccountCard { Name = "MenuAccountCard", OpenProfile = () => ExecuteAction("open_character_panel") };
        AddChild(_accountCard);
        //_logManager.Info("[MenuManager] _Ready appelé");

        _gameManager = GetNode<GameManager>(GlobalPaths.Managers.GAME_MANAGER);
        //_logManager.Info("[MenuManager] GameManager récupéré");

        // S'abonner au signal de changement d'état
        _gameManager.GameStateChanged += OnGameStateChanged;
        //_logManager.Info("[MenuManager] Abonnement au signal GameStateChanged");

        //_logManager.Info($"[MenuManager] État initial : Visible={Visible}, ProcessMode={ProcessMode}");
    }

    public override void _Process(double delta)
    {
        // le nom du compte connecté, en haut à droite du menu (visible avec lui)
        string who = Openesport.Online.AccountClient.LoggedIn ? Openesport.Utils.I18n.T("Connecté : {0}", Openesport.Online.AccountClient.Login) : "";
        if (_accountLabel != null && _accountLabel.Text != who) _accountLabel.Text = who;
        if (_social != null) _social.Visible = !_inGame;
        if (_accountCard != null) _accountCard.Visible = !_inGame && Openesport.Online.AccountClient.LoggedIn;
        if (Visible) FitMenuToWindow();
    }

    private void OnGameStateChanged(int newState)
    {
        var state = (GameManager.GameState)newState;
        //_logManager.Info($"[MenuManager] État du jeu changé vers : {state}");

        switch (state)
        {
            case GameManager.GameState.MainMenu:
                _inGame = false;
                ShowMainMenu();
                break;
            case GameManager.GameState.Pause:
                _inGame = true;
                GetTree().CallGroup("game_hud", "set_visible", false); // le HUD disparaît pendant le menu
                ShowMainMenu();
                break;
            case GameManager.GameState.Gameplay:
                GetTree().CallGroup("game_hud", "set_visible", true);
                //_logManager.Info("[MenuManager] Transition vers le masquage du menu");
                HideMainMenu();
                break;
        }
    }

    private void ShowMainMenu()
    {
        //_logManager.Info($"[MenuManager] Tentative d'affichage du menu - État actuel : Visible={Visible}");
        Show();
        Openesport.Utils.Telemetry.MainMenuShown(true);
        DisplaySubMenu(0); // Afficher le menu racine
        //_logManager.Info($"[MenuManager] Menu affiché - Nouvel état : Visible={Visible}");

        // Vérifier si le menu est bien visible
        if (!Visible)
        {
            _logManager.Warning("[MenuManager] Le menu n'est pas visible après Show()");
        }
    }

    private void HideMainMenu()
    {
        //_logManager.Info($"[MenuManager] Tentative de masquage du menu - État actuel : Visible={Visible}");
        Hide();
        Openesport.Utils.Telemetry.MainMenuShown(false);
        //_logManager.Info($"[MenuManager] Menu masqué - Nouvel état : Visible={Visible}");
    }

    public void AddElement(MenuElement element)
    {
        //_logManager.Info($"[MenuManager] Tentative d'ajout d'un élément : {element.Name} (ID: {element.Id})");

        if (_menuElements.ContainsKey(element.Id))
        {
            _logManager.Warning($"[MenuManager] Un élément avec l'ID {element.Id} existe déjà");
            return;
        }

        _menuElements[element.Id] = element;
        //_logManager.Info($"[MenuManager] Élément ajouté avec succès : {element.Name} (ID: {element.Id})");
        //_logManager.Info($"[MenuManager] Poids : {element.Poids}");
        //_logManager.Info($"[MenuManager] Nombre total d'éléments : {_menuElements.Count}");

        // Créer et ajouter l'élément visuel dans le menu
        var container = GetNode<VBoxContainer>("MenuCenterContainer/MenuVBoxContainer/ButtonsContainer");
        if (container != null)
        {
            Node uiElement = CreateUIElement(element);
            if (uiElement != null)
            {
                container.AddChild(uiElement);
                //_logManager.Info($"[MenuManager] Élément créé et ajouté pour l'élément : {element.Name}");
            }
        }
        else
        {
            _logManager.Error("[MenuManager] Container non trouvé");
        }
    }

    public void RemoveElement(int id)
    {
        //_logManager.Info($"[MenuManager] Tentative de suppression de l'élément ID: {id}");

        if (!_menuElements.ContainsKey(id))
        {
            _logManager.Warning($"[MenuManager] Aucun élément trouvé avec l'ID {id}");
            return;
        }

        var elementName = _menuElements[id].Name;
        _menuElements.Remove(id);
        //_logManager.Info($"[MenuManager] Élément supprimé avec succès : {elementName} (ID: {id})");
        //_logManager.Info($"[MenuManager] Nombre total d'éléments : {_menuElements.Count}");
    }

    public void ClearElements()
    {
        //_logManager.Info($"[MenuManager] Tentative de suppression de tous les éléments (actuellement {_menuElements.Count})");
        _menuElements.Clear();
        //_logManager.Info("[MenuManager] Tous les éléments ont été supprimés");
    }

    public void UpdateElement(MenuElement element)
    {
        //_logManager.Info($"[MenuManager] Tentative de mise à jour de l'élément : {element.Name} (ID: {element.Id})");

        if (!_menuElements.ContainsKey(element.Id))
        {
            _logManager.Warning($"[MenuManager] Aucun élément trouvé avec l'ID {element.Id} pour la mise à jour");
            return;
        }

        var oldName = _menuElements[element.Id].Name;
        _menuElements[element.Id] = element;
        //_logManager.Info($"[MenuManager] Élément mise à jour avec succès : {oldName} -> {element.Name} (ID: {element.Id})");
    }

    public MenuElement? GetElement(int id)
    {
        //_logManager.Info($"[MenuManager] Tentative de récupération de l'élément ID: {id}");

        if (_menuElements.ContainsKey(id))
        {
            //_logManager.Info($"[MenuManager] Élément trouvé : {_menuElements[id].Name} (ID: {id})");
            return _menuElements[id];
        }

        _logManager.Warning($"[MenuManager] Aucun élément trouvé avec l'ID {id}");
        return null;
    }

    public void RegisterActionHandler(string action, Module module)
    {
        //_logManager.Info($"[MenuManager] Enregistrement du module {module.ModuleName} pour l'action {action}");
        _actionHandlers[action] = module;
    }

    public void ExecuteAction(string action)
    {
        //_logManager.Info($"[MenuManager] Tentative d'exécution de l'action : {action}");

        if (string.IsNullOrEmpty(action))
        {
            _logManager.Warning("[MenuManager] Action vide");
            return;
        }

        try
        {
            if (action == "GetTree().Quit()")
            {
                //_logManager.Info("[MenuManager] Exécution de l'action de fermeture");
                GetTree().Quit();
            }
            else if (action == "resume_game")
            {
                _gameManager.ChangeState(GameManager.GameState.Gameplay);
            }
            else if (action == "leave_game")
            {
                ConfirmLeaveGame();
            }
            else
            {
                //_logManager.Info($"[MenuManager] Action handlers disponibles : {string.Join(", ", _actionHandlers.Keys)}");
                if (_actionHandlers.TryGetValue(action, out var module))
                {
                    //_logManager.Info($"[MenuManager] Exécution de l'action {action} par le module {module.ModuleName}");
                    module.ExecuteAction(action);
                }
                else
                {
                    _logManager.Warning($"[MenuManager] Aucun module trouvé pour l'action : {action}");
                }
            }
        }
        catch (Exception ex)
        {
            _logManager.Error($"[MenuManager] Erreur lors de l'exécution de l'action {action}: {ex.Message}");
        }
    }

    public void SaveMenuState()
    {
        //_logManager.Info("[MenuManager] Tentative de sauvegarde de l'état du menu");
        // TODO: Implémenter la sauvegarde de l'état
        //_logManager.Info("[MenuManager] État du menu sauvegardé");
    }

    public void RestoreMenuState()
    {
        //_logManager.Info("[MenuManager] Tentative de restauration de l'état du menu");
        // TODO: Implémenter la restauration de l'état
        //_logManager.Info("[MenuManager] État du menu restauré");
    }

    public void ResetMenu()
    {
        //_logManager.Info("[MenuManager] Tentative de réinitialisation du menu");
        ClearElements();
        //_logManager.Info("[MenuManager] Menu réinitialisé");
    }

    public override void _ExitTree()
    {
        //_logManager.Info("[MenuManager] _ExitTree appelé");
        // Se désabonner du signal quand le nœud est supprimé
        if (_gameManager != null)
        {
            _gameManager.GameStateChanged -= OnGameStateChanged;
            //_logManager.Info("[MenuManager] Désabonnement du signal GameStateChanged");
        }
    }

    /// <summary>Demande confirmation avant de quitter la partie en cours.</summary>
    private void ConfirmLeaveGame()
    {
        var dialog = new ConfirmationDialog
        {
            Title = LocalizationManager.GetTranslation("MENU_LEAVE_GAME"),
            DialogText = LocalizationManager.GetTranslation("LEAVE_GAME_CONFIRM"),
            OkButtonText = LocalizationManager.GetTranslation("LEAVE_GAME_YES"),
            CancelButtonText = LocalizationManager.GetTranslation("LEAVE_GAME_NO"),
            Exclusive = true
        };
        dialog.Confirmed += LeaveGame;
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(420, 140));
    }

    /// <summary>Fin de partie : revient au menu principal tout de suite (sans demander confirmation).</summary>
    public void LeaveGameNow() { LeaveGame(); }

    /// <summary>Ferme la partie (déconnexion comprise) et revient au menu principal.</summary>
    private void LeaveGame()
    {
        // la scène de jeu contient le joueur, le HUD et la partie en ligne : la libérer déconnecte du serveur
        GetNode<SceneManager>(GlobalPaths.Managers.SCENE_MANAGER).UnloadScene();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _gameManager.ChangeState(GameManager.GameState.MainMenu);
    }

    private bool HasChildren(int parentId)
    {
        return _menuElements.Values.Any(element => element.ParentId == parentId);
    }

    private void LoadUI(string uiPath)
    {
        // Réglages des commandes (touches et mode de visée) : écran construit en code
        if (uiPath.EndsWith("clavier.tscn"))
        {
            var settings = new Openesport.Menus.KeyBindingsMenu { Name = "KeyBindingsMenu" };
            settings.Closed += Show; // au retour, on retrouve le menu
            GetTree().Root.AddChild(settings);
            Hide();
            return;
        }

        // Graphismes (plein écran ou fenêtre, qualité) : écran construit en code
        if (uiPath == "graphics")
        {
            var graphics = new Openesport.Menus.GraphicsScreen { Name = "GraphicsScreen" };
            graphics.Closed += Show;
            GetTree().Root.AddChild(graphics);
            Hide();
            return;
        }

        // Crédits (auteurs et licences des ressources utilisées) : écran construit en code
        if (uiPath == "credits")
        {
            var credits = new Openesport.Menus.CreditsScreen { Name = "CreditsScreen" };
            credits.Closed += Show;
            GetTree().Root.AddChild(credits);
            Hide();
            return;
        }

        Hide();
    }

    private void OnElementPressed(MenuElement element)
    {
        //_logManager.Info($"[MenuManager] Élément pressé : {element.Name} (ID: {element.Id})");
        Openesport.Utils.Telemetry.Track("menu", element.Name);

        // 1. Si une action existe, l'exécuter
        if (element.Properties.TryGetValue("action", out var actionObj) && actionObj is string action)
        {
            //_logManager.Info($"[MenuManager] Exécution de l'action : {action}");
            ExecuteAction(action);
        }
        // 2. Si une UI existe, la charger
        if (!string.IsNullOrEmpty(element.UiPath))
        {
            //_logManager.Info($"[MenuManager] Chargement de l'UI : {element.UiPath}");
            LoadUI(element.UiPath);
        }
        // 3. Sinon, si des éléments ont cet élément comme parent, afficher le sous-menu
        else if (HasChildren(element.Id))
        {
            //_logManager.Info($"[MenuManager] Affichage du sous-menu pour : {element.Name}");
            DisplaySubMenu(element.Id);
        }
    }

    private void DisplaySubMenu(int parentId)
    {
        //_logManager.Info($"[MenuManager] Affichage du sous-menu (ParentId: {parentId})");

        // Nettoyer le conteneur
        var container = GetNode<VBoxContainer>("MenuCenterContainer/MenuVBoxContainer/ButtonsContainer");
        foreach (var child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }

        // Récupérer et trier les éléments par poids
        var elements = _menuElements.Values
            .Where(element => element.ParentId == parentId)
            .OrderByDescending(element => element.Poids)
            .ToList();

        // Menu ouvert pendant une partie : on garde les réglages (clavier, langue), mais plus de nouvelle partie ni de « Quitter
        // l'application » ; on propose de reprendre ou de quitter la partie pour revenir au menu principal.
        if (_inGame && parentId == 0)
        {
            elements = elements
                .Where(e => e.Name != "MENU_QUIT"
                    && !(e.Properties.TryGetValue("action", out var a) && a is string act && (act.StartsWith("start_") || act == "open_character_panel" || act == "logout")))
                .ToList();
            var resume = new MenuElement(-10, "MENU_RESUME", MenuElementType.Button, 0, 1000);
            resume.Properties["action"] = "resume_game";
            var leave = new MenuElement(-11, "MENU_LEAVE_GAME", MenuElementType.Button, 0, -1000,
                iconPath: "res://assets/textures/Menu/boutons/quit.png");
            leave.Properties["action"] = "leave_game";
            elements.Insert(0, resume);
            elements.Add(leave);
        }

        // Afficher tous les éléments du niveau
        foreach (var element in elements)
        {
            Node uiElement = CreateUIElement(element);
            if (uiElement != null)
            {
                container.AddChild(uiElement);
            }
        }

        // Si on n'est pas au niveau racine, ajouter le bouton retour en dernier
        if (parentId != 0)
        {
            var backButton = CreateBackButton();
            backButton.Pressed += () => DisplaySubMenu(_menuElements[parentId].ParentId);
            container.AddChild(backButton);
        }

    }

    /// <summary>
    /// Le menu (logo, titre, boutons) se réduit au besoin pour tenir en hauteur dans la fenêtre, quel que soit le nombre de boutons.
    /// Recalculé à chaque image (voir _Process) : la mise en page suit les changements de sous-menu et de taille de fenêtre.
    /// </summary>
    private void FitMenuToWindow()
    {
        var box = GetNodeOrNull<Control>("MenuCenterContainer/MenuVBoxContainer");
        if (box == null) return;
        float need = box.GetCombinedMinimumSize().Y;
        float have = GetViewportRect().Size.Y * 0.96f;
        box.PivotOffset = box.Size / 2f;
        float s = Mathf.Min(1f, Mathf.Min(have / Mathf.Max(need, 1f),
            (GetViewportRect().Size.X - 32f) / Mathf.Max(box.GetCombinedMinimumSize().X, 1f)));
        box.Scale = new Vector2(s, s);
    }

    private Node CreateUIElement(MenuElement element)
    {
        switch (element.Type)
        {
            case MenuElementType.Button:
                return CreateStyledButton(element);
            case MenuElementType.Label:
                return CreateStyledLabel(element);
            case MenuElementType.VBoxContainer:
                return CreateStyledVBoxContainer(element);
            case MenuElementType.HBoxContainer:
                return CreateStyledHBoxContainer(element);
            case MenuElementType.Select:
                return CreateStyledSelect(element);
            default:
                _logManager.Warning($"[MenuManager] Type d'élément non supporté : {element.Type}");
                return null;
        }
    }

    private Button CreateStyledButton(MenuElement element)
    {
        string icon = element.Name switch
        {
            "MENU_ONLINE_GAMES" or "MENU_RESUME" => "play",
            "MENU_CHARACTER" => "profile",
            "MENU_SETTINGS" => "settings",
            "MENU_SETTINGS_GRAPHICS" => "graphics",
            "MENU_SETTINGS_KEYBOARD" => "controls",
            "SETTINGS_LANGUAGE" => "language",
            "MENU_CREDITS" => "credits",
            "MENU_LOGOUT" or "MENU_LEAVE_GAME" => "logout",
            "MENU_QUIT" => "quit",
            _ => "menu"
        };
        string variant = element.Name switch
        {
            "MENU_ONLINE_GAMES" or "MENU_RESUME" => "MenuPrimary",
            "MENU_QUIT" or "MENU_LEAVE_GAME" => "MenuDanger",
            _ => "MenuNavigation"
        };
        var button = MenuStyle.NavigationButton(LocalizationManager.GetTranslation(element.Name), icon, variant);
        // Additional modules can provide their own icon.
        if (icon == "menu" && !string.IsNullOrEmpty(element.IconPath))
            button.Icon = GD.Load<Texture2D>(element.IconPath);
        button.Disabled = !element.Enabled;
        button.Pressed += () => OnElementPressed(element);
        return button;
    }

    private Label CreateStyledLabel(MenuElement element)
    {
        Label label = new Label();
        label.Text = element.Name;
        ApplyCommonStyles(label, element);
        return label;
    }

    private VBoxContainer CreateStyledVBoxContainer(MenuElement element)
    {
        VBoxContainer container = new VBoxContainer();
        ApplyCommonStyles(container, element);
        return container;
    }

    private HBoxContainer CreateStyledHBoxContainer(MenuElement element)
    {
        HBoxContainer container = new HBoxContainer();
        ApplyCommonStyles(container, element);
        return container;
    }

    private Button CreateBackButton() => MenuStyle.NavigationButton(
        LocalizationManager.GetTranslation("MENU_BACK"), "back");

    private void ApplyCommonStyles(Control control, MenuElement element)
    {
        control.Theme = MenuStyle.SharedTheme;
    }

    private OptionButton CreateStyledSelect(MenuElement element)
    {
        OptionButton select = new OptionButton();
        select.CustomMinimumSize = new Vector2(250, 50);
        select.Text = element.Name;

        select.Theme = MenuStyle.SharedTheme;

        // Ajouter les options avec drapeaux
        if (element.Properties.TryGetValue("options", out var optionsObj) && optionsObj is List<SelectOption> options)
        {
            _logManager.Info($"[MenuManager] Langue actuelle : {Main.language}");
            int defaultIndex = 0;
            
            foreach (var option in options)
            {
                // Ajouter l'option au select
                select.AddItem(option.label, select.GetItemCount());
                
                // Si c'est la langue actuelle, on note son index
                if (option.value == Main.language)
                {
                    defaultIndex = select.GetItemCount() - 1;
                    _logManager.Info($"[MenuManager] Langue actuelle trouvée à l'index {defaultIndex}");
                }
                
                // Charger et redimensionner le drapeau
                var flag = GD.Load<Texture2D>(option.iconPath);
                if (flag != null)
                {
                    var resizedTexture = new ImageTexture();
                    var image = flag.GetImage();
                    image.Resize(24, 24);
                    resizedTexture = ImageTexture.CreateFromImage(image);
                    select.SetItemIcon(select.GetItemCount() - 1, resizedTexture);
                }
                else
                {
                    _logManager.Warning($"[MenuManager] Drapeau non trouvé : {option.iconPath}");
                }
            }
            
            // Sélectionner la langue par défaut
            select.Selected = defaultIndex;
            _logManager.Info($"[MenuManager] Langue par défaut sélectionnée : {options[defaultIndex].label}");
        }
        select.Pressed += () => {
            _logManager.Info($"[MenuManager] Le select a été pressé");
        };

        // Gérer le changement de sélection
        select.ItemSelected += (long index) => {
            _logManager.Info($"[MenuManager] La sélection a été modifiée : {index}");
            _logManager.Info($"[MenuManager] Propriétés disponibles : {string.Join(", ", element.Properties.Keys)}");
            
            if (element.Properties.TryGetValue("options", out var selectedOptionsObj) && 
                selectedOptionsObj is List<SelectOption> selectedOptions)
            {
                var selectedOption = selectedOptions[(int)index];
                _logManager.Info($"[MenuManager] Option sélectionnée : {selectedOption.label} ({selectedOption.value})");
                
                if (element.Properties.TryGetValue("action", out var actionObj) && 
                    actionObj is string action)
                {
                    _logManager.Info($"[MenuManager] Action trouvée : '{action}'");
                    if (action == "SetLanguage")
                    {
                        _logManager.Info($"[MenuManager] Exécution de l'action : {action} {selectedOption.value}");
                        _logManager.Info($"[MenuManager] Ancienne langue : {Main.language}");
                        Main.language = selectedOption.value;
                        _logManager.Info($"[MenuManager] Nouvelle langue : {Main.language}");
                    
                        
                        // Recharger les traductions avec la nouvelle langue
                        LocalizationManager.LoadLanguage(Main.language);
                        LocalizationManager.SavedLanguage = Main.language;      // conservée pour la prochaine fois
                        
                        // Recréer le menu pour mettre à jour les textes
                        DisplaySubMenu(0);
                    }
                    else
                    {
                        _logManager.Info($"[MenuManager] Action incorrecte : '{action}' != 'SetLanguage'");
                    }
                }
            }
        };

        return select;
    }


}
