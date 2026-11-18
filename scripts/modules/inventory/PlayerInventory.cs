using Godot;
using System;
using System.Collections.Generic;
using Openesport.Utils;

namespace Openesport.Inventory
{
	/// <summary>
	/// Inventaire du joueur : 2 emplacements d'arme, un sac à dos (8 / 10 / 12 emplacements selon son niveau)
	/// et le ramassage des objets au sol.
	/// </summary>
	/// <summary>Pont vers le serveur en mode en ligne : les actions deviennent des demandes, l'état vient du serveur.</summary>
	public interface IInventoryNetwork
	{
		void RequestInteract();
		/// <summary>-1 = mains nues, 0 ou 1 = emplacement d'arme.</summary>
		void RequestSlot(int slot);
		void SendCommand(OpenEsport.Core.CommandType type, int arg);
	}

	public partial class PlayerInventory : Node
	{
		/// <summary>Renseigné en ligne : le serveur décide de tout, cet inventaire n'est qu'un reflet.</summary>
		public IInventoryNetwork Network;

		/// <summary>Distance maximale de ramassage, en mètres.</summary>
		[Export]
		public float PickupRange = 3.0f;

		/// <summary>Angle maximal (degrés) entre le regard et l'objet pour pouvoir le ramasser.</summary>
		[Export]
		public float PickupAngle = 30.0f;

		/// <summary>Les 2 emplacements d'arme (null = vide).</summary>
		public readonly WeaponSlotState[] Weapons = new WeaponSlotState[ItemDatabase.WeaponSlotCount];

		/// <summary>Contenu du sac à dos : un objet (et sa quantité) par emplacement.</summary>
		public readonly List<ItemStack> Items = new();

		/// <summary>Emplacement d'arme en main, -1 = mains nues.</summary>
		public int ActiveWeapon { get; private set; } = -1;

		public int BackpackLevel { get; private set; } = 1;

		public int Capacity => ItemDatabase.SlotsForBackpackLevel(BackpackLevel);

		/// <summary>Objet au sol actuellement visé (null si aucun).</summary>
		public GroundItem Target { get; private set; }

		/// <summary>L'inventaire a changé (armes, sac, objets, arme en main).</summary>
		public event Action Changed;

		/// <summary>Message d'information à afficher au joueur.</summary>
		public event Action<string> Message;

		private CharacterBody3D _player;
		private Camera3D _camera;
		private WeaponManager _weaponManager;
		private InputEvents _inputEvents;

		public override void _Ready()
		{
			_player = GetParent<CharacterBody3D>();
			_camera = _player.GetNode<Camera3D>("Head/Camera");
			_weaponManager = _player.GetNode<WeaponManager>("Head/Camera/WeaponManager");

			_weaponManager.AmmoSource = this;

			_inputEvents = GetNode<InputManager>(GlobalPaths.Managers.INPUT_MANAGER).GetInputEvents();
			_inputEvents.OnInteractTriggered += OnInteract;
			_inputEvents.OnWeaponSlotTriggered += OnWeaponSlot;
			_inputEvents.OnSwitchWeaponUpTriggered += OnSwitchUp;
			_inputEvents.OnSwitchWeaponDownTriggered += OnSwitchDown;
		}

		public override void _ExitTree()
		{
			if (_inputEvents == null)
				return;
			_inputEvents.OnInteractTriggered -= OnInteract;
			_inputEvents.OnWeaponSlotTriggered -= OnWeaponSlot;
			_inputEvents.OnSwitchWeaponUpTriggered -= OnSwitchUp;
			_inputEvents.OnSwitchWeaponDownTriggered -= OnSwitchDown;
		}

		public override void _Process(double delta)
		{
			var target = IsGameplayInputActive() ? FindTarget() : null;
			if (target != Target)
			{
				Target = target;
				Changed?.Invoke();
			}
		}

		private static bool IsGameplayInputActive()
		{
			return Input.MouseMode == Input.MouseModeEnum.Captured;
		}

		// ------------------------------------------------------------------
		// Entrées
		// ------------------------------------------------------------------

		private void OnInteract()
		{
			if (Network != null) { if (IsGameplayInputActive()) Network.RequestInteract(); return; }
			if (IsGameplayInputActive() && Target != null)
				TryPickup(Target);
		}

		private void OnWeaponSlot(int slot)
		{
			if (IsGameplayInputActive())
				SelectWeapon(slot);
		}

		private void OnSwitchUp()
		{
			if (IsGameplayInputActive())
				CycleWeapon();
		}

		private void OnSwitchDown()
		{
			if (IsGameplayInputActive())
				CycleWeapon();
		}

		// ------------------------------------------------------------------
		// Armes
		// ------------------------------------------------------------------

		/// <summary>Prend en main l'arme de l'emplacement donné (-1 = mains nues).</summary>
		public void SelectWeapon(int slot)
		{
			if (Network != null)
			{
				if (slot >= Weapons.Length) return;
				if (slot >= 0 && Weapons[slot] == null) { Message?.Invoke($"Emplacement d'arme {slot + 1} vide"); return; }
				Network.RequestSlot(slot);
				return;
			}
			if (slot >= Weapons.Length)
				return;

			if (slot >= 0 && Weapons[slot] == null)
			{
				Message?.Invoke($"Emplacement d'arme {slot + 1} vide");
				return;
			}

			ActiveWeapon = slot;
			_weaponManager.SetHeldWeapon(slot >= 0 ? Weapons[slot] : null);
			Changed?.Invoke();
		}

		/// <summary>Molette : passe à l'autre arme (avec 2 emplacements, les deux sens reviennent au même).</summary>
		public void CycleWeapon()
		{
			// Sabotage : la bombe du porteur fait partie du tour (arme 1, arme 2, bombe, arme 1…)
			if (Network != null && Items.Exists(s => s.Id == "bomb"))
			{
				var order = new System.Collections.Generic.List<int>();
				for (int i = 0; i < Weapons.Length; i++) if (Weapons[i] != null) order.Add(i);
				order.Add(-2);                                                   // -2 = la bombe
				int cur = BombInHand ? order.Count - 1 : order.IndexOf(ActiveWeapon);
				int next = order[(cur + 1 + order.Count) % order.Count];
				if (next == -2) Network.SendCommand(OpenEsport.Core.CommandType.EquipBomb, 1); else SelectWeapon(next);
				return;
			}
			for (int i = 1; i <= Weapons.Length; i++)
			{
				int slot = (Mathf.Max(ActiveWeapon, -1) + i) % Weapons.Length;
				if (slot != ActiveWeapon && Weapons[slot] != null)
				{
					SelectWeapon(slot);
					return;
				}
			}
		}

		/// <summary>Jette l'arme de l'emplacement donné, avec son viseur.</summary>
		/// <summary>Niveau du bouclier équipé (0 blanc à 3 rouge), −1 = aucun. Le serveur décide ; cette valeur est son reflet.</summary>
		public int ShieldTier = 0;

		/// <summary>Sabotage : la bombe est en main (reflet du serveur). Les mains sont nues, le clic gauche maintenu la pose.</summary>
		public bool BombInHand;
		/// <summary>Grenade en main (identifiant de l'objet du noyau, 0 = aucune) : le clic gauche la lance.</summary>
		public byte GrenadeInHand;

		public void DropShield()
		{
			if (Network == null || ShieldTier < 0) { Message?.Invoke(I18n.T("Pas de bouclier à jeter")); return; }
			Network.SendCommand(OpenEsport.Core.CommandType.DropShield, 0);
		}

		/// <summary>Utilise un soin du sac (en ligne : le serveur l'applique au bout de quelques secondes).</summary>
		public void UseConsumable(string itemId)
		{
			var net = OpenEsport.Core.ItemDefs.ByName(itemId);
			if (Network == null || net == null) return;
			Network.SendCommand(OpenEsport.Core.CommandType.UseItem, net.Id);
		}

		public void DropWeapon(int slot)
		{
			if (Network != null) { Network.SendCommand(OpenEsport.Core.CommandType.DropWeapon, slot); return; }
			if (slot < 0 || slot >= Weapons.Length || Weapons[slot] == null)
				return;

			var state = Weapons[slot];
			Weapons[slot] = null;
			if (ActiveWeapon == slot)
			{
				ActiveWeapon = -1;
				_weaponManager.SetHeldWeapon(null);
			}

			DropToGround(state.ItemId, state);
			Changed?.Invoke();
		}

		// ------------------------------------------------------------------
		// Viseurs
		// ------------------------------------------------------------------

		/// <summary>Monte sur l'arme en main le viseur rangé à l'index donné du sac.</summary>
		public void EquipOpticFromBackpack(int index)
		{
			if (Network != null) { Network.SendCommand(OpenEsport.Core.CommandType.EquipOptic, index); return; }
			if (index < 0 || index >= Items.Count)
				return;

			var def = Items[index].Definition;
			if (def == null || def.Type != ItemType.Optic)
				return;

			var weapon = ActiveWeapon >= 0 ? Weapons[ActiveWeapon] : null;
			if (weapon == null)
			{
				Message?.Invoke(I18n.T("Prenez une arme en main pour y monter un viseur"));
				return;
			}

			if (!weapon.Definition.AcceptsOptic)
			{
				Message?.Invoke(I18n.T("{0} n'accepte pas de viseur", weapon.Definition.DisplayName));
				return;
			}

			// Échange avec le viseur déjà monté, s'il y en a un
			string previous = weapon.OpticId;
			weapon.OpticId = def.Id;
			if (previous != null)
				Items[index] = new ItemStack { Id = previous };
			else
				Items.RemoveAt(index);

			OnWeaponAttachmentsChanged(weapon);
		}

		/// <summary>Retire le viseur de l'arme de l'emplacement donné et le range dans le sac.</summary>
		public void UnequipOptic(int weaponSlot)
		{
			if (Network != null) { Network.SendCommand(OpenEsport.Core.CommandType.UnequipOptic, weaponSlot); return; }
			if (weaponSlot < 0 || weaponSlot >= Weapons.Length)
				return;

			var weapon = Weapons[weaponSlot];
			if (weapon?.OpticId == null)
				return;

			if (Items.Count >= Capacity)
			{
				Message?.Invoke(I18n.T("Sac à dos plein"));
				return;
			}

			Items.Add(new ItemStack { Id = weapon.OpticId });
			weapon.OpticId = null;
			OnWeaponAttachmentsChanged(weapon);
		}

		/// <summary>Glisser-déposer : monte le viseur du sac sur l'arme de cet emplacement, même si elle n'est pas en main.</summary>
		public void EquipOpticOn(int weaponSlot, int index)
		{
			if (Network != null) { Network.SendCommand(OpenEsport.Core.CommandType.EquipOpticOn, weaponSlot * 16 + index); return; }
			if (weaponSlot == ActiveWeapon) EquipOpticFromBackpack(index);
			else Message?.Invoke(I18n.T("Prenez cette arme en main pour y monter un viseur"));
		}

		/// <summary>Glisser-déposer : jette à terre le viseur monté sur l'arme de cet emplacement.</summary>
		public void DropOptic(int weaponSlot)
		{
			if (Network != null) { Network.SendCommand(OpenEsport.Core.CommandType.DropOptic, weaponSlot); return; }
			Message?.Invoke(I18n.T("Jeter un viseur monté n'est possible qu'en ligne"));
		}

		private void OnWeaponAttachmentsChanged(WeaponSlotState weapon)
		{
			if (_weaponManager.HeldWeapon == weapon)
				_weaponManager.RefreshAttachments();
			Changed?.Invoke();
		}

		// ------------------------------------------------------------------
		// Sac à dos
		// ------------------------------------------------------------------

		/// <summary>Jette l'objet rangé à l'index donné du sac.</summary>
		public void DropItem(int index)
		{
			if (Network != null) { Network.SendCommand(OpenEsport.Core.CommandType.DropItem, index); return; }
			if (index < 0 || index >= Items.Count)
				return;

			var stack = Items[index];
			Items.RemoveAt(index);
			DropToGround(stack.Id, null, stack.Count);
			if (stack.Definition?.Type == ItemType.Ammo)
				_weaponManager.NotifyAmmoChanged();
			Changed?.Invoke();
		}

		// ------------------------------------------------------------------
		// Munitions
		// ------------------------------------------------------------------

		/// <summary>Nombre de munitions de ce type dans le sac.</summary>
		public int GetAmmo(string ammoType)
		{
			int total = 0;
			foreach (var stack in Items)
			{
				var def = stack.Definition;
				if (def != null && def.Type == ItemType.Ammo && def.AmmoType == ammoType)
					total += stack.Count;
			}
			return total;
		}

		/// <summary>Retire jusqu'à <paramref name="amount"/> munitions du sac (rechargement). Retourne la quantité réellement retirée.</summary>
		public int TakeAmmo(string ammoType, int amount)
		{
			if (Network != null) return 0; // en ligne, le serveur retire les munitions
			int taken = 0;
			// On vide d'abord les dernières piles, pour libérer des emplacements
			for (int i = Items.Count - 1; i >= 0 && taken < amount; i--)
			{
				var def = Items[i].Definition;
				if (def == null || def.Type != ItemType.Ammo || def.AmmoType != ammoType)
					continue;

				int take = Mathf.Min(Items[i].Count, amount - taken);
				Items[i].Count -= take;
				taken += take;
				if (Items[i].Count <= 0)
					Items.RemoveAt(i);
			}

			if (taken > 0)
				Changed?.Invoke();
			return taken;
		}

		/// <summary>Range des munitions dans le sac (piles existantes d'abord). Retourne la quantité qui n'a pas pu être rangée.</summary>
		private int AddAmmo(ItemDefinition def, int amount)
		{
			foreach (var stack in Items)
			{
				if (amount <= 0)
					break;
				if (stack.Id != def.Id || stack.Count >= def.StackSize)
					continue;
				int add = Mathf.Min(def.StackSize - stack.Count, amount);
				stack.Count += add;
				amount -= add;
			}

			while (amount > 0 && Items.Count < Capacity)
			{
				int add = Mathf.Min(def.StackSize, amount);
				Items.Add(new ItemStack { Id = def.Id, Count = add });
				amount -= add;
			}

			return amount;
		}

		// ------------------------------------------------------------------
		// Ramassage
		// ------------------------------------------------------------------

		/// <summary>
		/// En ligne : recopie l'inventaire décidé par le serveur. Les objets déjà connus sont mis à jour sur place,
		/// pour que l'arme en main ne soit pas rangée puis ressortie à chaque message.
		/// </summary>
		public void ApplyServerInventory(OpenEsport.Core.InventoryState st)
		{
			Openesport.Diagnostics.LagTracer.Event($"inventaire reçu : arme active {st.ActiveSlot}, {st.Items.Count} objets, bouclier {st.ShieldTier}");
			using var _trace = Openesport.Diagnostics.LagTracer.Scope("PlayerInventory.ApplyServerInventory");
			string NameOf(byte id) => id == 0 ? null : OpenEsport.Core.ItemDefs.Get(id)?.Name;

			bool opticChanged = false;
			for (int i = 0; i < Weapons.Length; i++)
			{
				var w = st.Weapons[i];
				if (w == null) { Weapons[i] = null; continue; }
				string id = NameOf(w.WeaponId), optic = NameOf(w.OpticId);
				var cur = Weapons[i];
				if (cur != null && cur.ItemId == id)
				{
					if (cur.OpticId != optic) opticChanged = true;
					cur.Magazine = w.Magazine;
					cur.OpticId = optic;
					cur.Tier = w.Tier;
				}
				else
					Weapons[i] = new WeaponSlotState { ItemId = id, Magazine = w.Magazine, OpticId = optic, Tier = w.Tier };
			}

			Items.Clear();
			foreach (var s in st.Items) Items.Add(new ItemStack { Id = NameOf(s.Id), Count = s.Count });
			BackpackLevel = st.BackpackLevel;
			ShieldTier = st.ShieldTier;
			BombInHand = st.BombInHand;
			GrenadeInHand = st.GrenadeInHand;
			// objet en main : la bombe, ou la grenade sortie
			_weaponManager.SetHeldObject(BombInHand ? "bomb" : st.GrenadeInHand != 0 ? OpenEsport.Core.ItemDefs.Get(st.GrenadeInHand)?.Name : null, BombInHand ? 1.6f : 1.0f);

			var previouslyHeld = _weaponManager.HeldWeapon;
			var wanted = st.ActiveSlot >= 0 ? Weapons[st.ActiveSlot] : null;
			ActiveWeapon = st.ActiveSlot;
			if (wanted != previouslyHeld)
				_weaponManager.SetHeldWeapon(wanted);
			else if (wanted != null && opticChanged)
				_weaponManager.RefreshAttachments();
			Changed?.Invoke();
		}

		public bool TryPickup(GroundItem item)
		{
			var def = item?.Definition;
			if (def == null || !IsInstanceValid(item))
				return false;

			bool taken = def.Type switch
			{
				ItemType.Weapon => PickupWeapon(item),
				ItemType.Optic => PickupOptic(def),
				ItemType.Backpack => PickupBackpack(def),
				ItemType.Ammo => PickupAmmo(item, def),
				_ => false
			};

			if (taken)
			{
				if (Target == item)
					Target = null;
				item.GetParent()?.RemoveChild(item);
				item.QueueFree();
				Changed?.Invoke();
			}

			return taken;
		}

		private bool PickupWeapon(GroundItem item)
		{
			var state = item.WeaponState ?? new WeaponSlotState { ItemId = item.ItemId };

			// Emplacement libre en priorité, sinon on remplace l'arme en main (ou la première)
			int slot = Array.IndexOf(Weapons, null);
			if (slot < 0)
			{
				slot = ActiveWeapon >= 0 ? ActiveWeapon : 0;
				var old = Weapons[slot];
				DropToGround(old.ItemId, old);
			}

			Weapons[slot] = state;
			ActiveWeapon = slot;
			_weaponManager.SetHeldWeapon(state);
			return true;
		}

		private bool PickupOptic(ItemDefinition def)
		{
			// Monté directement sur l'arme en main si elle n'a pas encore de viseur
			var weapon = ActiveWeapon >= 0 ? Weapons[ActiveWeapon] : null;
			if (weapon != null && weapon.Definition.AcceptsOptic && weapon.OpticId == null)
			{
				weapon.OpticId = def.Id;
				if (_weaponManager.HeldWeapon == weapon)
					_weaponManager.RefreshAttachments();
				Message?.Invoke(I18n.T("{0} monté sur {1}", def.DisplayName, weapon.Definition.DisplayName));
				return true;
			}

			if (Items.Count >= Capacity)
			{
				Message?.Invoke(I18n.T("Sac à dos plein"));
				return false;
			}

			Items.Add(new ItemStack { Id = def.Id });
			return true;
		}

		private bool PickupAmmo(GroundItem item, ItemDefinition def)
		{
			int left = AddAmmo(def, item.Count);
			if (left == item.Count)
			{
				Message?.Invoke(I18n.T("Sac à dos plein"));
				return false;
			}

			_weaponManager.NotifyAmmoChanged();

			// Tout n'a pas pu être rangé : le reste demeure au sol
			if (left > 0)
			{
				item.Count = left;
				Message?.Invoke(I18n.T("Sac à dos plein : {0} laissées au sol", left));
				Changed?.Invoke();
				return false;
			}

			return true;
		}

		private bool PickupBackpack(ItemDefinition def)
		{
			if (def.BackpackLevel <= BackpackLevel)
			{
				Message?.Invoke(I18n.T("Vous portez déjà un sac à dos de niveau {0}", BackpackLevel));
				return false;
			}

			// L'ancien sac (s'il y en avait un) est reposé au sol
			string previous = BackpackLevel > 1 ? ItemDatabase.BackpackIdForLevel(BackpackLevel) : null;
			BackpackLevel = def.BackpackLevel;
			if (previous != null)
				DropToGround(previous, null);

			Message?.Invoke(I18n.T("{0} équipé : {1} emplacements", def.DisplayName, Capacity));
			return true;
		}

		// ------------------------------------------------------------------
		// Monde
		// ------------------------------------------------------------------

		private GroundItem FindTarget()
		{
			GroundItem best = null;
			float bestScore = float.MaxValue;
			var origin = _camera.GlobalPosition;
			var forward = -_camera.GlobalTransform.Basis.Z.Normalized();
			float maxAngle = Mathf.DegToRad(PickupAngle);

			foreach (var item in GroundItem.All)
			{
				if (!IsInstanceValid(item) || !item.IsInsideTree() || item.IsQueuedForDeletion())
					continue;

				var to = item.GlobalPosition + Vector3.Up * 0.1f - origin;
				float distance = to.Length();
				if (distance > PickupRange || distance < 0.001f)
					continue;

				// On vise l'objet tel qu'il est dessiné : on mesure à quelle distance du rayon de visée passe sa boîte englobante.
				// (L'ancien calcul prenait l'angle vers l'origine du modèle : dans un coffre, un objet voisin ou un objet long
				// dont l'origine est décalée était choisi à tort.)
				var bounds = item.WorldBounds();
				Vector3 center = bounds.GetCenter();
				float along = forward.Dot(center - origin);
				if (along <= 0f)
					continue;
				Vector3 closest = origin + forward * along;
				// rayon : le point du rayon le plus proche de la boîte
				Vector3 clamped = new Vector3(
					Mathf.Clamp(closest.X, bounds.Position.X, bounds.End.X),
					Mathf.Clamp(closest.Y, bounds.Position.Y, bounds.End.Y),
					Mathf.Clamp(closest.Z, bounds.Position.Z, bounds.End.Z));
				float gap = closest.DistanceTo(clamped);
				// la boîte d'un long fusil est grande : on tolère un léger écart, pas plus
				if (gap > 0.06f)
					continue;

				// parmi ceux qu'on vise, le plus proche du centre de leur boîte, puis le plus près du joueur
				float score = closest.DistanceTo(center) + distance * 0.01f;
				if (score < bestScore)
				{
					bestScore = score;
					best = item;
				}
			}

			return best;
		}

		/// <summary>Pose un objet au sol juste devant le joueur.</summary>
		private void DropToGround(string itemId, WeaponSlotState weaponState, int count = 0)
		{
			var basis = _player.GlobalTransform.Basis;
			var forward = -basis.Z.Normalized();
			var right = basis.X.Normalized();
			var position = _player.GlobalPosition
				+ forward * (float)GD.RandRange(1.0, 1.4)
				+ right * (float)GD.RandRange(-0.5, 0.5);

			GroundItem.Spawn(_player.GetParent(), itemId, position, (float)GD.RandRange(0, Mathf.Tau), weaponState, 0.5f, count);
		}
	}
}
