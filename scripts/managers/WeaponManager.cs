using Godot;
using System;
using System.Collections.Generic;
using Openesport.Utils;
using Openesport.Modules;
using Openesport.Managers;
using Openesport.Inventory;

/// <summary>
/// Affiche et fait fonctionner l'arme tenue en main (tir, rechargement, visée, viseur monté).
/// Le choix de l'arme tenue est piloté par <see cref="PlayerInventory"/> via <see cref="SetHeldWeapon"/>.
/// </summary>
public partial class WeaponManager : Node3D
{
	// Signaux
	[Signal]
	public delegate void GiveDamageEventHandler(Node3D obj, float damage, Vector3 point);

	[Signal]
	public delegate void UpdateAmmoEventHandler(int magazine, int inventoryAmmo, string ammoType);

	/// <summary>Réserve de munitions : le sac à dos du joueur.</summary>
	public PlayerInventory AmmoSource;

	[Export]
	public float MeleeDamage = 100;

	[Export]
	public bool ShowMuzzleSmoke = true;

	// Configuration de la caméra
	[Export]
	public Camera3D Camera;

	[Export]
	public bool Recoil = true;

	[Export]
	public Node3D NodeRecoilX;

	[Export]
	public Node3D NodeRecoilY;

	[Export]
	public float RecoilXClampMax = 1.4f;

	[Export]
	public float RecoilXClampMin = -1.4f;

	[Export]
	public bool Sway = false;

	[Export]
	public float SwayLookSens = 50.0f;

	[Export]
	public float SwayMoveSens = 80.0f;

	[Export]
	public bool Shake = true;

	[Export]
	public float ShakeStrength = 0.5f;

	[Export]
	public float ShakeDecay = 3.0f;

	[Export]
	public Vector2 ShakeMaxOffset = new Vector2(0.01f, 0.01f);

	[Export]
	public float ShakeMaxRoll = 0.01f;

	// Multiplicateurs
	[Export]
	public float DeltaMultiplier = 1.0f;

	[Export]
	public float DamageMultiplier = 1.0f;

	/// <summary>Vitesse de transition du zoom en visée.</summary>
	[Export]
	public float ZoomSpeed = 24.0f;
	/// <summary>Vitesse de montée de l'arme en visée (par seconde) : plus grand = plus rapide. Indépendant du nombre d'images par seconde.</summary>
	[Export]
	public float AdsSpeed = 26.0f;

	/// <summary>En ligne : le serveur compte les munitions et décide des tirs ; ici on ne joue que les animations.</summary>
	public bool Online;

	public bool IsAiming => _ads;

	/// <summary>Chargeur de l'arme en main, tel que le serveur le compte.</summary>
	public void SetServerMagazine(int magazine)
	{
		if (_weapon == null || _switching || magazine == _magazine) return;      // en plein changement d'arme, le chargeur reçu est celui de la nouvelle arme
		_magazine = magazine;
		EmitAmmo();
	}

	/// <summary>Remplace les bras du kit par ceux du personnage du joueur (peau et manche).</summary>
	[Export]
	public bool UseCharacterArms = true;

	/// <summary>Distance à laquelle la balle rejoint la ligne de visée quand rien n'est visé.</summary>
	[Export]
	public float AimDistance = 1000.0f;

	// Scènes
	private PackedScene _bulletScene;

	// Arme tenue
	private WeaponSlotState _state;
	private Node3D _weapon;
	private AnimationTree _animation;
	private AnimationNodeStateMachinePlayback _stateMachine;
	private Node3D _muzzlePoint;

	/// <summary>Position monde de la bouche du canon de l'arme en main (vue FPS), ou null.</summary>
	public Vector3? MuzzleGlobalPosition => _muzzlePoint != null && GodotObject.IsInstanceValid(_muzzlePoint) && _muzzlePoint.IsInsideTree() ? _muzzlePoint.GlobalPosition : null;
	private bool _isReloading;
	private bool _isMelee;

	// Changement d'arme
	private WeaponSlotState _pending;
	private bool _switching;
	private int _switchToken;

	// Viseur monté
	private Node3D _opticNode;
	private bool _opticPlaced;
	private Vector3 _adsPosOptic;

	private Vector3 _startPos;
	private Vector3 _adsPos;
	private bool _ads;
	private bool _isAuto;
	private float _delta;
	private float _deltaAds = 0.1f;

	private int _maxMagazine;
	private int _magazine;

	// Zoom
	private float _baseFov = 75.0f;
	private float _zoom = 1.0f;
	private HeadMovement3D _head;
	private bool _sensitivityOverridden;
	private float _savedSensitivity;

	// FOV du shader de vue FPS (arme, bras, viseur) : resserré en visée avec une lunette grossissante
	private readonly List<(ShaderMaterial material, float baseFov)> _viewMaterials = new();
	private readonly List<ShaderMaterial> _opticMaterials = new();
	private float _viewmodelFov = 75.0f;
	private float _adsViewmodelFov;
	private float _adsBlend;
	private float _appliedFovBlend = -1.0f;

	/// <summary>Part de la hauteur d'écran occupée par le corps de la lunette en visée (diamètre extérieur).</summary>
	private const float ScopeScreenFraction = 0.58f;

	private LogManager _logManager;

	private bool _canShoot = true;
	private bool _wasShooting = false;

	/// <summary>Arme actuellement en main (null = mains nues).</summary>
	public WeaponSlotState HeldWeapon => _state;

	public override void _Ready()
	{
		_logManager = GetNode<LogManager>(GlobalPaths.Managers.LOG_MANAGER);
		_logManager.Info("WeaponManager initialisé");

		var inputManager = GetNode<InputManager>(GlobalPaths.Managers.INPUT_MANAGER);
		if (inputManager != null)
		{
			var inputEvents = inputManager.GetInputEvents();
			inputEvents.OnAimTriggered += OnAimTriggered;
			inputEvents.OnAimReleased += OnAimReleased;
			inputEvents.OnShootStateChanged += OnShootStateChanged;
			inputEvents.OnReloadTriggered += OnReloadTriggered;
		}

		_bulletScene = GD.Load<PackedScene>("res://scenes/modules/weapons/bullet.tscn");

		if (Sway && Camera != null)
		{
			TopLevel = true;
		}

		// mains nues visibles à la première personne quand aucune arme n'est en main
		if (Camera != null)
		{
			_fists = new FistsViewmodel { Name = "Fists" };
			Camera.CallDeferred(Node.MethodName.AddChild, _fists); // la caméra monte encore ses enfants
		}

		if (Camera != null)
		{
			_baseFov = Camera.Fov;
			_head = Camera.GetParent() as HeadMovement3D;
		}

		// Le joueur commence à mains nues
		EmitAmmo();
	}

	public override void _ExitTree()
	{
		var inputManager = GetNodeOrNull<InputManager>(GlobalPaths.Managers.INPUT_MANAGER);
		if (inputManager != null)
		{
			var inputEvents = inputManager.GetInputEvents();
			inputEvents.OnAimTriggered -= OnAimTriggered;
			inputEvents.OnAimReleased -= OnAimReleased;
			inputEvents.OnShootStateChanged -= OnShootStateChanged;
			inputEvents.OnReloadTriggered -= OnReloadTriggered;
		}
	}

	// ------------------------------------------------------------------
	// Arme tenue
	// ------------------------------------------------------------------

	/// <summary>
	/// Change l'arme tenue en main. <c>null</c> = mains nues.
	/// L'arme actuelle est rangée (animation « hide ») avant de sortir la nouvelle.
	/// </summary>
	public void SetHeldWeapon(WeaponSlotState state)
	{
		Openesport.Diagnostics.LagTracer.Event($"arme en main : {state?.ItemId ?? "mains nues"} (niveau {state?.Tier})");
		using var _trace = Openesport.Diagnostics.LagTracer.Scope("WeaponManager.SetHeldWeapon");
		if (!_switching && state == _state)
			return;

		_pending = state;

		if (_switching)
			return;

		if (_weapon == null)
		{
			ShowPending();
			return;
		}

		_switching = true;
		_ads = false;
		if (Openesport.Utils.GameSettings.Aim == Openesport.Utils.AimMode.Switch) _aimWanted = false;     // « basculer » : changer d'arme sort de la visée
		_isReloading = false;

		int token = ++_switchToken;
		// Sécurité si l'animation « hide » ne se termine pas
		GetTree().CreateTimer(1.0f).Timeout += () =>
		{
			if (IsInstanceValid(this) && IsInsideTree() && _switching && token == _switchToken)
				FinishHide();
		};

		if (_stateMachine != null)
			_stateMachine.Travel("hide");
		else
			FinishHide();
	}

	private void FinishHide()
	{
		if (!_switching)
			return;

		_switching = false;
		_switchToken++;
		RemoveWeapon();
		ShowPending();
	}

	private void ShowPending()
	{
		var state = _pending;
		_pending = null;

		if (state != null)
			ShowWeapon(state);
		else
			EmitAmmo();
	}

	private void RemoveWeapon()
	{
		ClearOptic();
		SetViewmodelFovBlend(0.0f);
		_viewMaterials.Clear();
		_adsBlend = 0.0f;

		if (_animation != null)
		{
			_animation.AnimationFinished -= OnAnimationFinished;
			_animation.AnimationStarted -= OnAnimationStarted;
		}

		if (_weapon != null)
		{
			RemoveChild(_weapon);
			_weapon.QueueFree();
		}

		_weapon = null;
		_state = null;
		_animation = null;
		_stateMachine = null;
		_muzzlePoint = null;
		_isReloading = false;
		_ads = false;
		_maxMagazine = 0;
		_magazine = 0;
	}

	/// <summary>
	/// Précharge toutes les armes et accessoires (chargement des modèles, des textures et compilation des shaders) tant qu'on n'en a pas besoin :
	/// la première arme ramassée ou sortie d'un coffre ne fige plus le jeu pendant une seconde.
	/// </summary>
	/// <summary>Vrai quand le préchargement des armes et des objets est terminé (l'écran de chargement peut disparaître).</summary>
	public bool WarmUpDone { get; private set; }

	public async void WarmUp()
	{
		Openesport.Diagnostics.LagTracer.Event("préchargement des armes : début");
		var holder = new Node3D { Position = new Vector3(0, 0, -2f), Scale = Vector3.One * 0.02f };
		AddChild(holder);
		foreach (var id in ItemDatabase.Ids)
		{
			var def = ItemDatabase.Get(id);
			if (def == null || string.IsNullOrEmpty(def.ScenePath)) continue;
			var scene = Openesport.Inventory.ItemVisuals.LoadCached(def.ScenePath);
			if (scene == null) continue;
			var node = scene.Instantiate<Node3D>();
			holder.AddChild(node);
			if (def.Type == ItemType.Weapon && UseCharacterArms) FpsArms.Apply(node);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // une image visible : les shaders se compilent maintenant
			if (!IsInstanceValid(node)) continue;
			node.QueueFree();
		}
		holder.QueueFree();
		WarmUpDone = true;
		Openesport.Diagnostics.LagTracer.Event("préchargement des armes : fini");
	}

	private void ShowWeapon(WeaponSlotState state)
	{
		using var _trace = Openesport.Diagnostics.LagTracer.Scope("WeaponManager.ShowWeapon " + state?.ItemId);
		var def = state.Definition;
		var scene = def != null ? Openesport.Inventory.ItemVisuals.LoadCached(def.ScenePath) : null;
		if (scene == null)
		{
			_logManager.Error($"Scène d'arme introuvable pour '{state.ItemId}'");
			EmitAmmo();
			return;
		}

		_state = state;
		_weapon = scene.Instantiate<Node3D>();
		_weapon.Visible = true;
		AddChild(_weapon);
		if (UseCharacterArms)
			FpsArms.Apply(_weapon);
		_logManager.Debug($"Arme en main : {def.DisplayName}");

		_animation = _weapon.GetNodeOrNull<AnimationTree>("AnimationTree");
		if (_animation != null)
		{
			_animation.AnimationFinished += OnAnimationFinished;
			_animation.AnimationStarted += OnAnimationStarted;
			_stateMachine = _animation.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
		}
		else
		{
			_logManager.Warning("AnimationTree non trouvé dans l'arme");
		}

		// Point de départ des balles
		_muzzlePoint = _weapon.FindChild("Marker3D") as Node3D;
		_isMelee = _weapon.GetMeta("melee", false).AsBool();
		if (_muzzlePoint == null && !_isMelee)
			_logManager.Error("Marker3D non trouvé dans l'arme");

		_startPos = _weapon.Position;
		_adsPos = _weapon.GetMeta("ads_pos", _startPos).As<Vector3>();
		_isAuto = _weapon.GetMeta("auto", false).As<bool>();
		_delta = _weapon.GetMeta("delta", 1.0f).As<float>();

		_maxMagazine = _weapon.GetMeta("max_magazine", 0).As<int>();
		// le niveau de l'arme agrandit le chargeur (comme sur le serveur) : sans cela on ne pourrait pas remplir au-delà de la valeur de base
		if (_maxMagazine > 0 && state.Tier > 0)
			_maxMagazine = Mathf.Max(1, Mathf.RoundToInt(_maxMagazine * OpenEsport.Core.GameBalance.TierMagazine[Mathf.Clamp(state.Tier, 0, 3)]));
		_magazine = state.Magazine < 0 ? _maxMagazine : Mathf.Min(state.Magazine, _maxMagazine);
		state.Magazine = _magazine;

		_canShoot = true;
		_isReloading = false;
		_ads = false;

		_viewMaterials.Clear();
		_appliedFovBlend = -1.0f;
		CollectViewMaterials(_weapon);
		_viewmodelFov = _viewMaterials.Count > 0 ? _viewMaterials[0].baseFov : 75.0f;

		// L'arme est mise en pose de repos tout de suite : le viseur est posé dessus immédiatement
		if (_stateMachine != null)
		{
			_stateMachine.Start("idle");
			_animation.Advance(0);
		}
		RefreshAttachments(true);

		if (!state.TakePlayed && _stateMachine != null)
		{
			// Animation de prise en main : uniquement la première fois que l'arme est prise
			state.TakePlayed = true;
			_stateMachine.Start("take");
			_animation.Advance(0);
		}
		else
		{
			// Les fois suivantes, l'arme remonte simplement en position
			_weapon.Position = _startPos + new Vector3(0, -0.3f, 0);
		}

		EmitAmmo();
	}

	private void CollectViewMaterials(Node node)
	{
		if (node is MeshInstance3D mi && mi.Mesh != null)
		{
			for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
			{
				if (mi.GetActiveMaterial(i) is not ShaderMaterial sm)
					continue;
				var value = sm.GetShaderParameter("fov");
				if (value.VariantType != Variant.Type.Float || _viewMaterials.Exists(m => m.material == sm))
					continue;
				_viewMaterials.Add((sm, value.AsSingle()));
			}
		}

		foreach (var child in node.GetChildren())
			CollectViewMaterials(child);
	}

	/// <summary>
	/// Resserre le FOV de la vue FPS : 0 = FOV normal, 1 = FOV de visée à la lunette.
	/// Les matériaux des armes sont des ressources partagées : on revient toujours à 0 avant de ranger l'arme.
	/// </summary>
	private void SetViewmodelFovBlend(float blend)
	{
		if (_adsViewmodelFov <= 0.0f)
			blend = 0.0f;
		if (Mathf.IsEqualApprox(blend, _appliedFovBlend))
			return;
		_appliedFovBlend = blend;

		foreach (var (material, baseFov) in _viewMaterials)
			material.SetShaderParameter("fov", Mathf.Lerp(baseFov, _adsViewmodelFov, blend));
		foreach (var material in _opticMaterials)
			material.SetShaderParameter("fov", Mathf.Lerp(_viewmodelFov, _adsViewmodelFov, blend));
	}

	private string CurrentAmmoType()
	{
		return _weapon != null ? _weapon.GetMeta("ammo_type", "none").AsString() : "none";
	}

	private int GetReserve(string ammoType)
	{
		return AmmoSource != null ? AmmoSource.GetAmmo(ammoType) : 0;
	}

	/// <summary>Les munitions du sac ont changé : met à jour l'affichage.</summary>
	public void NotifyAmmoChanged()
	{
		EmitAmmo();
	}

	private void EmitAmmo()
	{
		if (_state != null && !_switching)
			_state.Magazine = _magazine;

		string ammoType = CurrentAmmoType();
		EmitSignal(SignalName.UpdateAmmo, _weapon != null ? _magazine : 0, GetReserve(ammoType), ammoType);
	}

	// ------------------------------------------------------------------
	// Viseur
	// ------------------------------------------------------------------

	/// <summary>
	/// (Re)construit le viseur monté sur l'arme tenue d'après son état. À appeler quand le viseur change.
	/// </summary>
	public void RefreshAttachments()
	{
		RefreshAttachments(false);
	}

	/// <param name="poseIsIdle">L'arme est déjà en pose de repos : le viseur peut être posé tout de suite.</param>
	private void RefreshAttachments(bool poseIsIdle)
	{
		ClearOptic();

		if (_weapon == null || _state?.Optic == null)
			return;

		_opticNode = ItemVisuals.BuildViewmodelOptic(_state.Optic, _viewmodelFov);
		CollectOpticMaterials(_opticNode);

		// Le placement se mesure sur la pose de repos de l'arme ; sinon on attend qu'elle y revienne (voir _Process)
		if (poseIsIdle || _stateMachine == null || _stateMachine.GetCurrentNode() == "idle")
			PlaceOptic();
	}

	private void CollectOpticMaterials(Node node)
	{
		if (node is MeshInstance3D mi && mi.Mesh != null)
		{
			for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
			{
				if (mi.GetSurfaceOverrideMaterial(i) is ShaderMaterial sm && !_opticMaterials.Contains(sm))
					_opticMaterials.Add(sm);
			}
		}

		foreach (var child in node.GetChildren())
			CollectOpticMaterials(child);
	}

	private void ClearOptic()
	{
		if (_opticNode != null)
		{
			_opticNode.GetParent()?.RemoveChild(_opticNode);
			_opticNode.QueueFree();
		}
		_opticNode = null;
		_opticPlaced = false;
		_opticMaterials.Clear();
		SetViewmodelFovBlend(0.0f);
		_adsViewmodelFov = 0.0f;
	}

	/// <summary>
	/// Fixe le viseur sur le dessus de l'arme et calcule la position de visée qui amène
	/// le centre de la lentille au centre de l'écran.
	/// </summary>
	private void PlaceOptic()
	{
		var def = _state.Definition;
		var optic = _state.Optic;

		// Le viseur suit la pièce principale de l'arme (celle qui porte la bouche du canon)
		var mount = (_weapon.FindChild("MuzzlePoint") as Node3D)?.GetParent() as Node3D ?? _weapon;
		var toLocal = GlobalTransform.AffineInverse();

		var mountLocal = ItemVisuals.ComputeLocalAabb(mount);
		if (mountLocal == null)
		{
			mount = _weapon;
			mountLocal = ItemVisuals.ComputeLocalAabb(mount) ?? new Aabb();
		}

		// Dans le repère du WeaponManager : X = droite, Y = haut, Z = avant de l'arme
		var box = (toLocal * mount.GlobalTransform) * mountLocal.Value;
		var position = new Vector3(
			box.GetCenter().X,
			box.End.Y,
			Mathf.Lerp(box.Position.Z, box.End.Z, def.OpticAlong)) + def.OpticOffset;

		mount.AddChild(_opticNode);
		_opticNode.GlobalTransform = GlobalTransform * new Transform3D(Basis.Identity.Scaled(Vector3.One * def.OpticScale), position);

		// Position de visée : centre de la lentille au centre de l'écran, arrière du viseur à distance fixe de l'œil
		var center = _opticNode.GetNode<Node3D>(ItemVisuals.OpticalCenterName);
		var centerOffset = toLocal * center.GlobalPosition - _weapon.Position;
		var opticLocal = ItemVisuals.ComputeLocalAabb(_opticNode) ?? new Aabb();
		var opticBox = (toLocal * _opticNode.GlobalTransform) * opticLocal;
		float rearOffset = opticBox.Position.Z - _weapon.Position.Z;

		_adsPosOptic = new Vector3(-centerOffset.X, -centerOffset.Y, optic.EyeRelief - rearOffset);
		_opticPlaced = true;

		// Lunette grossissante : en visée, le FOV de la vue FPS est resserré pour que le corps de la lunette
		// (hauteur au-dessus de l'axe optique et demi-largeur) occupe une part fixe de l'écran
		_adsViewmodelFov = 0.0f;
		if (optic.Zoom > 2.0f)      // lunettes (4x, 6x) ; un viseur ouvert x2 garde la présentation d'un viseur de type holographique
		{
			var centerLocal = toLocal * center.GlobalPosition;
			float halfOuter = Mathf.Max(
				opticBox.End.Y - centerLocal.Y,
				Mathf.Max(opticBox.End.X - centerLocal.X, centerLocal.X - opticBox.Position.X));
			float rearDistance = Mathf.Max(optic.EyeRelief, 0.05f);
			float fov = Mathf.RadToDeg(2.0f * Mathf.Atan(halfOuter / rearDistance / ScopeScreenFraction));
			_adsViewmodelFov = Mathf.Min(fov, _viewmodelFov);
		}
		_appliedFovBlend = -1.0f;
	}

	// ------------------------------------------------------------------
	// Entrées
	// ------------------------------------------------------------------

	private static bool IsGameplayInputActive()
	{
		return Input.MouseMode == Input.MouseModeEnum.Captured;
	}

	private bool IsReadyForAction()
	{
		if (_weapon == null || _stateMachine == null || _switching)
			return false;
		string node = _stateMachine.GetCurrentNode();
		return node == "idle" || node == "fire";
	}

	/// <summary>Mode « maintenir » : la visée s'arrête dès que le bouton est relâché.</summary>
	private void OnAimReleased()
	{
		if (Openesport.Utils.GameSettings.Aim == Openesport.Utils.AimMode.Hold)
		{
			_ads = false;
			_aimWanted = false;
		}
	}

	/// <summary>Le joueur veut viser (bouton tenu en mode « maintenir », visée enclenchée en mode « basculer ») : si l'arme n'est pas prête
	/// (rechargement, sortie de l'arme), la visée reprend toute seule dès qu'elle l'est, sans avoir à relâcher et réappuyer.</summary>
	private bool _aimWanted;

	private void OnAimTriggered()
	{
		if (!IsGameplayInputActive())
			return;

		bool toggle = Openesport.Utils.GameSettings.Aim == Openesport.Utils.AimMode.Switch;
		if (_ads || (toggle && _aimWanted))
		{
			// en mode « basculer », un deuxième clic retire la visée (ou l'annule si elle attendait la fin du rechargement) ;
			// en « maintenir » on reste en visée jusqu'au relâchement
			if (toggle) { _ads = false; _aimWanted = false; }
			return;
		}

		_aimWanted = true;
		if (_isMelee || !IsReadyForAction())
			return;      // reprise automatique dès que l'arme est prête (voir _Process)

		_ads = true;
	}

	private FistsViewmodel _fists;
	private bool _holdingFire;
	/// <summary>Cache tous les bras et armes de la vue à la première personne (avion, chute libre, à terre) : on ne les voit qu'une fois arrivé.</summary>
	public bool HideViewmodels { get; set; }

	private void OnShootStateChanged(bool isShooting)
	{
		bool wasHolding = _holdingFire;
		_holdingFire = isShooting && IsGameplayInputActive();
		if (_holdingFire && !wasHolding && _weapon == null && _bombHeld == null) _fists?.Punch(); // à mains nues, le coup part dès le clic
		// Si le bouton est relâché (ou qu'un menu est ouvert), on réinitialise l'état de tir
		if (!isShooting || !IsGameplayInputActive())
		{
			_canShoot = true;
			_wasShooting = false;
			return;
		}

		if (_isReloading || _weapon == null || _switching)
			return;

		if (!_isMelee && _magazine <= 0 && _maxMagazine > 0)
		{
			// chargeur vide : appuyer sur tirer lance le rechargement (s'il reste des munitions) au lieu de ne rien faire
			if (!_wasShooting) { _wasShooting = true; Reload(); }
			return;
		}

		if (!_canShoot)
			return;

		// Pour les armes automatiques, on tire tant que le bouton est maintenu
		// Pour les armes semi-automatiques, on ne tire qu'une fois par appui
		if (_isAuto || !_wasShooting)
		{
			_canShoot = false;
			_wasShooting = true;

			if (_stateMachine != null)
				_stateMachine.Travel("fire");

			if (_isMelee)
				return;

			if (Online)
				return; // le serveur décide du tir, du chargeur et des dégâts
			_magazine--;
			EmitAmmo();
			SpawnBullet();
		}
	}

	private void SpawnBullet()
	{
		if (Camera == null || _muzzlePoint == null)
		{
			_logManager.Error("Camera ou Marker3D non trouvé");
			return;
		}

		var bullet = _bulletScene.Instantiate<Bullet>();

		// La balle ne doit jamais entrer en collision avec le tireur,
		// sinon elle le pousse/l'entraîne au moment du tir
		PhysicsBody3D shooterBody = null;
		for (Node node = GetParent(); node != null; node = node.GetParent())
		{
			if (node is PhysicsBody3D shooter)
			{
				shooterBody = shooter;
				bullet.AddCollisionExceptionWith(shooter);
				break;
			}
		}

		GetTree().Root.AddChild(bullet);
		bullet.GlobalPosition = _muzzlePoint.GlobalPosition;

		// La balle part du canon mais doit toucher le point visé au centre de l'écran
		// (centre du viseur en visée) : elle est dirigée vers ce que la caméra regarde.
		Vector3 forward = -Camera.GlobalTransform.Basis.Z.Normalized();
		Vector3 origin = Camera.GlobalPosition;
		Vector3 aimPoint = origin + forward * AimDistance;

		var query = PhysicsRayQueryParameters3D.Create(origin, aimPoint);
		if (shooterBody != null)
			query.Exclude = new Godot.Collections.Array<Rid> { shooterBody.GetRid() };
		var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count > 0)
			aimPoint = hit["position"].AsVector3();

		Vector3 direction = (aimPoint - bullet.GlobalPosition).Normalized();
		// Cible collée au canon : on tire droit devant plutôt que de travers
		if (origin.DistanceTo(aimPoint) < 1.5f || direction.Dot(forward) < 0.9f)
			direction = forward;

		float spread = _weapon.GetMeta("spread", 0.0f).As<float>();
		if (spread > 0)
		{
			direction = direction.Rotated(Camera.GlobalTransform.Basis.Y.Normalized(), (float)GD.RandRange(-spread, spread));
			direction = direction.Rotated(Camera.GlobalTransform.Basis.X.Normalized(), (float)GD.RandRange(-spread, spread));
		}

		bullet.set_initial_direction(direction);
	}

	private void OnReloadTriggered()
	{
		if (IsGameplayInputActive())
			Reload();
	}

	private void OnAnimationFinished(StringName animName)
	{
		if (animName == "hide")
		{
			FinishHide();
		}
		else if (animName == "reload")
		{
			_isReloading = false;
			string ammoType = CurrentAmmoType();
			int needed = Mathf.Max(_maxMagazine - _magazine, 0);
			_magazine += AmmoSource != null ? AmmoSource.TakeAmmo(ammoType, needed) : 0;
			EmitAmmo();
		}
		else if (animName == "fire")
		{
			_canShoot = true;
		}
	}

	private void OnAnimationStarted(StringName animName)
	{
		if (animName == "reload")
		{
			_isReloading = true;
		}
	}

	public void Reload()
	{
		if (_isReloading || !IsReadyForAction())
		{
			_logManager.Debug("Tentative de rechargement impossible : rechargement déjà en cours ou arme non disponible");
			return;
		}

		int ammoCount = GetReserve(CurrentAmmoType());
		if (ammoCount <= 0 || _magazine >= _maxMagazine)
		{
			_logManager.Debug($"Tentative de rechargement impossible - Munitions : {ammoCount}, Chargeur : {_magazine}/{_maxMagazine}");
			return;
		}

		_isReloading = true;
		if (_ads) _aimWanted = true;     // on reviendra en visée à la fin du rechargement
		_ads = false;
		_stateMachine.Travel("reload");
	}

	// ------------------------------------------------------------------
	// Mise à jour
	// ------------------------------------------------------------------

	private float _wallPush;
	private const float WeaponReach = 1.0f;     // distance de la caméra au bout du canon (m)
	private const float MaxWallPush = 0.55f;

	/// <summary>Combien l'arme doit reculer : le décor est plus près que le bout du canon.</summary>
	private float WallPushDistance()
	{
		if (Camera == null || !Camera.IsInsideTree()) return 0f;
		var space = GetWorld3D()?.DirectSpaceState;
		if (space == null) return 0f;
		var exclude = new Godot.Collections.Array<Rid>();
		for (Node node = GetParent(); node != null; node = node.GetParent())
			if (node is PhysicsBody3D body) { exclude.Add(body.GetRid()); break; }

		var basis = Camera.GlobalTransform.Basis;
		Vector3 origin = Camera.GlobalPosition;
		float nearest = WeaponReach;
		// trois rayons : l'axe de visée, puis le bas et le côté droit où se trouve l'arme
		foreach (var offset in new[] { Vector3.Zero, new Vector3(0.14f, -0.2f, 0f), new Vector3(0f, -0.28f, 0f) })
		{
			Vector3 from = origin + basis * offset;
			var query = PhysicsRayQueryParameters3D.Create(from, from - basis.Z * WeaponReach);
			query.Exclude = exclude;
			var hit = space.IntersectRay(query);
			if (hit.Count > 0)
				nearest = Mathf.Min(nearest, from.DistanceTo(hit["position"].AsVector3()));
		}
		return Mathf.Clamp(WeaponReach - nearest, 0f, MaxWallPush);
	}

	private bool _usingSupply;
	private Node3D _bombHeld;

	private string _heldId;

	/// <summary>Montre un objet devant les mains (mains nues) tant qu'il est en main : la bombe (le clic la pose) ou une grenade (le clic la lance). null = rien.</summary>
	public void SetHeldObject(string itemId, float scale = 1.6f)
	{
		bool alive = _bombHeld != null && GodotObject.IsInstanceValid(_bombHeld);
		if (itemId == _heldId && (itemId == null || alive)) return;
		if (alive) _bombHeld.QueueFree();
		_bombHeld = null; _heldId = itemId;
		if (itemId == null) return;
		var def = ItemDatabase.Get(itemId);
		if (def == null || Camera == null) return;
		_bombHeld = new Node3D { Name = "ObjetEnMain" };
		var model = ItemVisuals.BuildGroundModel(def);
		model.Scale = Vector3.One * scale;                    // un peu plus grand qu'au sol : on le voit bien
		model.Position = new Vector3(0, -0.04f, 0);
		_bombHeld.AddChild(model);
		_bombHeld.Position = new Vector3(0.12f, -0.22f, -0.38f);
		_bombHeld.RotationDegrees = new Vector3(8, -25, 0);
		Camera.AddChild(_bombHeld);
	}

	/// <summary>Un soin est en cours (en ligne) : l'arme est rangée et les mains lèvent le flacon à la bouche.</summary>
	public void SetUsingSupply(string itemId)
	{
		bool on = !string.IsNullOrEmpty(itemId);
		if (on == _usingSupply) return;
		_usingSupply = on;
		if (_weapon != null) _weapon.Visible = !on;
		_fists?.SetUsing(on, on ? ItemDatabase.Get(itemId) : null);
	}

	public override void _Process(double delta)
	{
		if (_fists != null)
		{
			_fists.Visible = !HideViewmodels && (_weapon == null || _usingSupply);
			if (_bombHeld != null && GodotObject.IsInstanceValid(_bombHeld)) _bombHeld.Visible = !HideViewmodels && !_usingSupply;
			_fists.Attacking = _weapon == null && !_usingSupply && _bombHeld == null && _holdingFire; // à mains nues, tenir le clic = boxer (pas avec la bombe en main : le clic la pose)
		}

		if (_weapon != null && !_switching)
		{
			// chargeur vide en tenant la détente (même en pleine rafale) : le rechargement part tout seul
			if (_holdingFire && !_isMelee && !_isReloading && _maxMagazine > 0 && _magazine <= 0 && IsReadyForAction() && GetReserve(CurrentAmmoType()) > 0)
				Reload();
			// visée demandée pendant le rechargement : elle reprend dès que l'arme est prête
			if (_aimWanted && !_ads && !_isMelee && !_isReloading && IsReadyForAction() && IsGameplayInputActive())
				_ads = true;
		}

		if (_weapon != null)
		{
			if (_opticNode != null && !_opticPlaced && !_switching && _stateMachine != null && _stateMachine.GetCurrentNode() == "idle")
			{
				PlaceOptic();
			}

			// Gestion de l'AIM
			Vector3 target = _startPos;
			if (_ads)
				target = _opticPlaced ? _adsPosOptic : _adsPos;
			// contre un mur ou une pente raide, l'arme recule au lieu de traverser le décor
			_wallPush = Mathf.Lerp(_wallPush, WallPushDistance(), 1.0f - Mathf.Exp(-20.0f * (float)delta));
			target += new Vector3(0, -_wallPush * 0.25f, -_wallPush);
			_weapon.Position = _weapon.Position.Lerp(target, 1.0f - Mathf.Exp(-AdsSpeed * (float)delta));
		}

		UpdateZoom((float)delta);
	}

	private void UpdateZoom(float delta)
	{
		if (Camera == null)
			return;

		float targetZoom = 1.0f;
		if (_ads && _weapon != null && _opticPlaced && _state?.Optic != null)
			targetZoom = Mathf.Max(1.0f, _state.Optic.Zoom);

		_zoom = Mathf.Lerp(_zoom, targetZoom, 1.0f - Mathf.Exp(-ZoomSpeed * delta));
		if (Mathf.Abs(_zoom - targetZoom) < 0.002f)
			_zoom = targetZoom;

		float halfFov = Mathf.DegToRad(_baseFov) * 0.5f;
		Camera.Fov = Mathf.RadToDeg(2.0f * Mathf.Atan(Mathf.Tan(halfFov) / _zoom));

		// Le FOV de la vue FPS suit la visée à la lunette
		float blendTarget = _ads && _weapon != null && _opticPlaced ? 1.0f : 0.0f;
		_adsBlend = Mathf.Lerp(_adsBlend, blendTarget, 1.0f - Mathf.Exp(-ZoomSpeed * delta));
		if (Mathf.Abs(_adsBlend - blendTarget) < 0.002f)
			_adsBlend = blendTarget;
		SetViewmodelFovBlend(_adsBlend);

		// La sensibilité de la souris suit le zoom
		if (_head == null)
			return;

		if (_zoom > 1.001f)
		{
			if (!_sensitivityOverridden)
			{
				_savedSensitivity = _head.MouseSensitivity;
				_sensitivityOverridden = true;
			}
			_head.MouseSensitivity = _savedSensitivity / _zoom;
		}
		else if (_sensitivityOverridden)
		{
			_head.MouseSensitivity = _savedSensitivity;
			_sensitivityOverridden = false;
		}
	}
}

public interface IDamageable
{
	void TakeDamage(float damage);
}
