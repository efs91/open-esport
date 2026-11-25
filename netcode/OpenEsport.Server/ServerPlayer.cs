using System.Collections.Generic;
using LiteNetLib;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>Tout ce que le serveur sait d'un joueur connecté.</summary>
	public sealed class ServerPlayer
	{
		public ushort Id;
		public NetPeer Peer;
		public string Name;
		public byte Team;
		/// <summary>Apparence choisie par le joueur (relayée aux autres, jamais interprétée par le serveur).</summary>
		public byte[] Appearance;
		/// <summary>Bot : pas de connexion réseau, ses commandes viennent de <see cref="GameServer"/>.</summary>
		public bool IsBot;
		/// <summary>Dernière commande jouée (le chef de saut la prête à ses coéquipiers pendant la chute) et état du bouton « passer le commandement ».</summary>
		public PlayerInput LastInput;
		public ControlVehicle Ride;
		public int Seat;
		public uint RideInputTick;
		public bool PrevPass;
		internal object Brain;

		public PlayerState State;
		public bool Alive = true;
		public float Health = 100f;
		/// <summary>Bouclier : absorbe les dégâts avant la vie. Maximum selon le niveau du bouclier équipé (voir InventoryState.ShieldTier).</summary>
		public float Shield = Consumables.ShieldMaxOf(0);
		/// <summary>Soin en cours d'utilisation (0 = aucun), temps restant et durée totale.</summary>
		public byte UseId;
		public float UseLeft, UseTotal;
		public float Yaw, Pitch;
		/// <summary>Phase du pas (0..1) : pose des jambes pour les hitbox, répliquée pour l'animation.</summary>
		public float Gait;
		public Buttons LastButtons;

		// commandes reçues, en attente d'être jouées (une par tick)
		private readonly PlayerInput[] _inputs = new PlayerInput[32];
		private int _head, _count;
		public ushort LastReceivedSeq;
		public bool AnyInput;
		public ushort LastProcessedSeq;
		public float CatchUpBudget;

		public InventoryState Inventory = new InventoryState();
		public readonly PositionHistory History = new PositionHistory();
		public ClientReplication Rep = new ClientReplication();
		/// <summary>Connexion perdue en cours de partie : le personnage reste en jeu (immobile) jusqu'à ce tick, le temps que le joueur revienne. 0 = connecté.</summary>
		public uint AwayUntil;
		public bool Away => AwayUntil != 0;

		// armes
		public float ShotCooldown;
		/// <summary>Balles tirées et durée depuis la première, tant que la détente reste enfoncée (armes automatiques).</summary>
		public int HeldShots;
		public float HeldShotSeconds;
		/// <summary>Délai avant de pouvoir lancer une autre grenade, et temps d'aveuglement restant (flash).</summary>
		public float GrenadeCooldown, BlindLeft;
		/// <summary>Instant (tick) avant lequel un nouveau ping est refusé.</summary>
		public uint NextPingTick;
		/// <summary>Emplacement de l'arme tenue avant de sortir une grenade (on la reprend après le lancer).</summary>
		public int PreGrenadeSlot = -1;
		public bool Reloading;
		public float ReloadLeft;
		/// <summary>Arme en cours de rechargement : si elle n'est plus en main à la fin, le rechargement est perdu.</summary>
		public WeaponSlot ReloadWeapon;
		public int FireFlagTicks;
		public int LastSlotCommand;
		public bool PrevFire, PrevReload, PrevInteract;
		public uint RespawnTick;
		/// <summary>Modes hors battle royale : classe choisie, invulnérabilité après réapparition, argent et kit de désamorçage (Sabotage), bouton d'interaction tenu.</summary>
		public byte ClassId;
		public uint ProtectUntil;
		public int Money;
		public bool HasKit, Holding, DiedThisRound;

		// partie battle royale
		public bool Downed;
		public int Kills, Deaths;
		/// <summary>Compte du joueur (service de comptes) ; 0 = sans compte (bot, ou serveur sans comptes).</summary>
		public long AccountId;
		/// <summary>Groupe du joueur (0 = aucun).</summary>
		public long PartyId;
		public float DamageDealt;
		/// <summary>Classement de l'équipe quand elle est éliminée (0 = en jeu).</summary>
		public byte Placement;
		/// <summary>Tick d'arrivée dans la partie et tick d'élimination (battle royale ; 0 = encore en jeu) : le temps de jeu rapporté en fin de partie.</summary>
		public uint JoinTick, OutTick;
		public ServerPlayer LastAttacker;
		/// <summary>Tick de la dernière blessure (Deathmatch : la vie remonte seule quelques secondes après) et temps accumulé de régénération.</summary>
		public uint LastHurtTick;
		public float RegenClock;
		/// <summary>Temps (s) avant la prochaine recharge possible à une station de ravitaillement.</summary>
		public float ResupplyClock;
		/// <summary>Coéquipier à terre qu'on est en train de relever, et temps restant.</summary>
		public ServerPlayer ReviveTarget;
		public float ReviveLeft;
		/// <summary>Temps passé hors de la zone depuis le dernier coup de zone.</summary>
		public float ZoneTimer;
		/// <summary>Coups de zone consécutifs reçus sans être rentré dans la zone (les dégâts montent avec).</summary>
		public int ZoneTicks;

		// tampons réutilisés pour construire les snapshots sans allocation
		public readonly BitWriter Out = new BitWriter(1400);
		public readonly BitWriter[] Extra = { new BitWriter(1400), new BitWriter(1400) };
		public int ExtraCount;
		public readonly List<int> Scratch = new List<int>(512);

		public int PendingInputs => _count;

		public void EnqueueInput(in PlayerInput input)
		{
			if (AnyInput && !Seq.Newer(input.Seq, LastReceivedSeq)) return; // doublon (commandes envoyées en redondance)
			AnyInput = true;
			LastReceivedSeq = input.Seq;
			if (_count == _inputs.Length) { _head = (_head + 1) % _inputs.Length; _count--; }
			_inputs[(_head + _count) % _inputs.Length] = input;
			_count++;
		}

		/// <summary>Joueur absent : une commande neutre par tick (il reste sur place, la gravité continue de s'appliquer).</summary>
		public void EnqueueIdle()
		{
			if (_count > 0) return;
			var idle = LastInput;
			idle.Forward = 0; idle.Strafe = 0; idle.ButtonBits = 0; idle.Seq = LastReceivedSeq;
			_inputs[_head] = idle;
			_count = 1;
		}

		/// <summary>Retour du joueur sur une nouvelle connexion : ses commandes repartent de zéro.</summary>
		public void ResetInputs()
		{
			_head = 0; _count = 0;
			AnyInput = false; LastReceivedSeq = 0; LastProcessedSeq = 0; CatchUpBudget = 0f;
		}

		public bool TryDequeueInput(out PlayerInput input)
		{
			if (_count == 0) { input = default; return false; }
			input = _inputs[_head];
			_head = (_head + 1) % _inputs.Length;
			_count--;
			return true;
		}
	}

	public struct LootEntity
	{
		public bool Active;
		public LootItem Item;
		public Vec3 Pos;
		/// <summary>Niveau de butin réglé sur le point d'origine (voir LootSpawn.Quality) ; pour un coffre, celui de son contenu.</summary>
		public byte Quality;
		/// <summary>Numéro (à partir de 1) du point de la carte d'où vient ce coffre ; 0 = aucun. Sert à relire sa liste d'objets à l'ouverture.</summary>
		public int SpawnPlusOne;
	}
}
