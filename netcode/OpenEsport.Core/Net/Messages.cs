using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	public enum MsgType : byte
	{
		// client -> serveur
		Join = 1,
		Input = 2,
		Command = 3,
		/// <summary>Ping : le joueur désigne un endroit à son équipe. Octet de type (0 ici, 1 danger, 2 ennemi, 255 retirer), la position, puis l'adversaire désigné (type 2) ou l'auteur du ping à retirer (type 255) ; 0xFFFF = aucun.</summary>
		Ping = 4,
		// serveur -> client
		JoinAccept = 10,
		JoinReject = 11,
		Snapshot = 12,
		Event = 13,
		Roster = 14,
		Inventory = 15,
		/// <summary>Réglages de jeu du serveur (déplacement, armes…), envoyés avant la réponse à la connexion : voir GameBalance.</summary>
		Balance = 16,
		/// <summary>État de la partie (salle d'attente, en cours, terminée) et statistiques du joueur : voir <see cref="MatchStatus"/>.</summary>
		Match = 17,
		/// <summary>État de la zone (cercle sûr, zone suivante, phase) : voir <see cref="ZoneState"/>.</summary>
		Zone = 18,
		/// <summary>État propre au mode de jeu (scores, minuteur, points, drapeaux, bombe, argent) : voir <see cref="ModeState"/>.</summary>
		ModeState = 19,
		/// <summary>Grenades en vol et zones de feu autour du joueur (non fiable, 20 fois par seconde) : voir <see cref="Messages.WriteGrenades"/>.</summary>
		Grenades = 20,
	}

	public enum MatchPhase : byte { Lobby = 0, Playing = 1, Over = 2 }

	/// <summary>Ce que le serveur dit d'une partie à un joueur : salle d'attente, vivants, ses statistiques, son état (à terre, réanimation).</summary>
	public struct MatchStatus
	{
		public MatchPhase Phase;
		/// <summary>Salle d'attente : humains présents, nombre requis pour lancer, secondes avant le départ (0 = pas de compte à rebours).</summary>
		public byte Lobby, Needed, Countdown;
		public ushort PlayersAlive;
		public byte TeamsAlive, TeamSize;
		public ushort Kills, Damage;
		/// <summary>Classement de l'équipe quand elle est éliminée (0 = encore en jeu), équipe gagnante à la fin (255 = aucune).</summary>
		public byte Placement, WinnerTeam;
		/// <summary>À terre : secondes restantes avant la mort (0 = debout).</summary>
		public byte DownLeft;
		/// <summary>Réanimation en cours (0-100), qu'on réanime ou qu'on soit réanimé.</summary>
		public byte Revive;
		/// <summary>Un coéquipier à terre est à portée : on peut le réanimer (maintenir E).</summary>
		public bool CanRevive;
		/// <summary>Chef de saut de mon équipe (identifiant de joueur, 0xFFFF = aucun) et, si je suis passager, celui dont les commandes dirigent ma chute (0xFFFF = je me dirige moi-même).</summary>
		public ushort JumpLeader, ControlledBy;

		public void Write(BitWriter w)
		{
			w.WriteByte((byte)MsgType.Match);
			w.WriteByte((byte)Phase); w.WriteByte(Lobby); w.WriteByte(Needed); w.WriteByte(Countdown);
			w.WriteUInt16(PlayersAlive); w.WriteByte(TeamsAlive); w.WriteByte(TeamSize);
			w.WriteUInt16(Kills); w.WriteUInt16(Damage);
			w.WriteByte(Placement); w.WriteByte(WinnerTeam); w.WriteByte(DownLeft); w.WriteByte(Revive); w.WriteBool(CanRevive);
			w.WriteUInt16(JumpLeader); w.WriteUInt16(ControlledBy);
		}

		/// <summary>Lit un message (l'octet de type est déjà lu).</summary>
		public static MatchStatus Read(BitReader r)
		{
			var m = new MatchStatus();
			m.Phase = (MatchPhase)r.ReadByte(); m.Lobby = r.ReadByte(); m.Needed = r.ReadByte(); m.Countdown = r.ReadByte();
			m.PlayersAlive = r.ReadUInt16(); m.TeamsAlive = r.ReadByte(); m.TeamSize = r.ReadByte();
			m.Kills = r.ReadUInt16(); m.Damage = r.ReadUInt16();
			m.Placement = r.ReadByte(); m.WinnerTeam = r.ReadByte(); m.DownLeft = r.ReadByte(); m.Revive = r.ReadByte(); m.CanRevive = r.ReadBool();
			m.JumpLeader = r.ReadUInt16(); m.ControlledBy = r.ReadUInt16();
			return m;
		}
	}

	public enum EventType : byte
	{
		/// <summary>
		/// Au tireur : A = cible, C = dégâts, D = zone (bits 0-1) | 0x80 si la cible est morte,
		/// B = bouclier de la cible : niveau + 1 (bits 0-2, 0 = aucun) | 0x10 si le coup a touché le bouclier | 0x20 si le bouclier vient de casser,
		/// Pos = point touché (à la hauteur de la zone) pour afficher les dégâts sur la cible.
		/// </summary>
		HitConfirm = 1,
		/// <summary>À la victime : A = tireur, C = dégâts, D = 1 si elle est morte, Pos = position du tireur.</summary>
		Damaged = 2,
		/// <summary>À tous : A = tueur, B = victime, C = arme.</summary>
		KillFeed = 3,
		/// <summary>Au joueur qui réapparaît : Pos = lieu d'apparition.</summary>
		Respawned = 4,
		/// <summary>À ceux qui sont à portée (non fiable) : une balle part. A = tireur, C = arme, Pos = origine, Dir = direction (unitaire). Le serveur simule la balle (vitesse, chute) ; le client dessine la même trajectoire.</summary>
		Shot = 5,
		/// <summary>À tous : un joueur tombe « à terre ». A = auteur, B = victime, C = arme.</summary>
		Downed = 6,
		/// <summary>À ceux qui sont à portée : une grenade agit. C = type (0 frag, 1 collante, 2 incendiaire, 3 flash), Pos = lieu.</summary>
		Explosion = 7,
		/// <summary>Au joueur aveuglé par une grenade flash : C = durée en dixièmes de seconde, D = part de l'éclair vue de face (0-255, sert à l'éblouissement).</summary>
		Flashed = 8,
		/// <summary>Aux équipiers (et à l'auteur) : un ping. A = auteur, C = type (0 ici, 1 danger, 2 ennemi), Pos = lieu désigné.</summary>
		Ping = 9,
		WindowChanged = 10, // A = pane index, B = health, C = reinforced
		/// <summary>Au tireur et à ses équipiers : un adversaire vient d'être touché (A = adversaire). Sert au repérage sur les cartes en Sabotage.</summary>
		TeamHit = 11,
		/// <summary>Sabotage, à une équipe : un adversaire est vu par un de ses joueurs (A = adversaire). Il apparaît 3 s sur la mini-carte et la grande carte.</summary>
		Spotted = 12,
		/// <summary>Deathmatch, au joueur : il vient de prendre un objet d'arène (A = joueur, C = objet, Pos = emplacement) ; son de ramassage.</summary>
		ArenaPickup = 13,

	}

	public enum CommandType : byte
	{
		DropWeapon = 1,
		DropItem = 2,
		EquipOptic = 3,
		UnequipOptic = 4,
		/// <summary>Commence à utiliser un soin du sac (arg = identifiant de l'objet).</summary>
		UseItem = 5,
		/// <summary>Interrompt l'utilisation en cours.</summary>
		CancelUse = 6,
		/// <summary>Pose le bouclier équipé par terre.</summary>
		DropShield = 7,
		/// <summary>Choisit sa classe d'équipement (arg = numéro) : appliquée à la prochaine réapparition (tout de suite en salle d'attente).</summary>
		SelectClass = 8,
		/// <summary>Sabotage : achète un article de la boutique (arg = code) pendant la phase d'achat.</summary>
		Buy = 9,
		/// <summary>Lance une grenade (arg = identifiant de l'objet) dans la direction où le joueur regarde.</summary>
		ThrowGrenade = 10,
		/// <summary>Sabotage : lâche la bombe portée (elle tombe devant le joueur).</summary>
		DropBomb = 11,
		/// <summary>Glisser-déposer : monte le viseur du sac sur une arme précise (arg = emplacement de l'arme × 16 + index dans le sac).</summary>
		EquipOpticOn = 12,
		/// <summary>Glisser-déposer : jette à terre le viseur monté sur l'arme de cet emplacement (arg).</summary>
		DropOptic = 13,
		/// <summary>Sabotage : sort la bombe en main (arg 1) ou la range et reprend une arme (arg 0).</summary>
		EquipBomb = 14,
		/// <summary>Sort une grenade en main (arg = identifiant de l'objet) ou la range (0) : le clic gauche la lance ensuite.</summary>
		EquipGrenade = 15,
		BoardVehicle = 16,
		/// <summary>1 VS 1, étape de choix : arg = catégorie × 16 + numéro (catégorie 0 arme, 1 viseur, 2 grenade, 3 soin ; <see cref="DuelRules.ConfirmPick"/> = valider).</summary>
		DuelPick = 17,
	}

	public static class Protocol
	{
		public const ushort Version = 34;
		/// <summary>Octets décrivant l'apparence d'un personnage (peau, morphologie, tenue) : le serveur les relaie sans les interpréter.</summary>
		public const int AppearanceSize = 10;
		public const string ConnectionKey = "open-esport-1";
		public const int MaxInputsPerPacket = 4;
		/// <summary>
		/// Taille maximale d'un paquet de snapshot. LiteNetLib refuse les paquets non fiables de plus de 1020 octets ; on garde de la marge.
		/// </summary>
		public const int SnapshotBudgetBytes = 960;
		/// <summary>Nombre maximal de paquets par snapshot quand beaucoup d'entités ont changé.</summary>
		public const int MaxPacketsPerSnapshot = 3;
		public const int MaxRewindTicks = 15; // 250 ms à 60 Hz
	}

	public struct JoinAccept
	{
		public ushort PlayerId;
		public byte Team;
		public byte TickRate, SnapshotDivisor, InterpTicks, TeamSize;
		public ushort MaxPlayers;
		public float MinX, MinY, MinZ;
		public uint ServerTick;
		public uint WorldChecksum;
		public PlayerState Spawn;

		public void Write(BitWriter w)
		{
			w.WriteByte((byte)MsgType.JoinAccept);
			w.WriteUInt16(PlayerId); w.WriteByte(Team);
			w.WriteByte(TickRate); w.WriteByte(SnapshotDivisor); w.WriteByte(InterpTicks); w.WriteByte(TeamSize);
			w.WriteUInt16(MaxPlayers);
			w.WriteFloat(MinX); w.WriteFloat(MinY); w.WriteFloat(MinZ);
			w.WriteUInt32(ServerTick); w.WriteUInt32(WorldChecksum);
			Spawn.Write(w);
		}

		public static JoinAccept Read(BitReader r)
		{
			var m = new JoinAccept();
			m.PlayerId = r.ReadUInt16(); m.Team = r.ReadByte();
			m.TickRate = r.ReadByte(); m.SnapshotDivisor = r.ReadByte(); m.InterpTicks = r.ReadByte(); m.TeamSize = r.ReadByte();
			m.MaxPlayers = r.ReadUInt16();
			m.MinX = r.ReadFloat(); m.MinY = r.ReadFloat(); m.MinZ = r.ReadFloat();
			m.ServerTick = r.ReadUInt32(); m.WorldChecksum = r.ReadUInt32();
			m.Spawn = PlayerState.Read(r);
			return m;
		}
	}

	public sealed class ServerEvent
	{
		public EventType Type;
		public ushort A, B;
		public byte C, D;
		public Vec3 Pos;
		/// <summary>Seulement pour <see cref="EventType.Shot"/>.</summary>
		public Vec3 Dir;

		public void Write(BitWriter w)
		{
			w.WriteByte((byte)MsgType.Event);
			w.WriteByte((byte)Type);
			w.WriteUInt16(A); w.WriteUInt16(B); w.WriteByte(C); w.WriteByte(D);
			w.WriteVec3(Pos);
			if (Type == EventType.Shot) w.WriteVec3(Dir);
		}

		public static ServerEvent Read(BitReader r)
		{
			var e = new ServerEvent { Type = (EventType)r.ReadByte(), A = r.ReadUInt16(), B = r.ReadUInt16(), C = r.ReadByte(), D = r.ReadByte(), Pos = r.ReadVec3() };
			if (e.Type == EventType.Shot) e.Dir = r.ReadVec3();
			return e;
		}
	}

	public struct RosterEntry
	{
		public ushort Id;
		public byte Team;
		public string Name;
		public bool IsBot;
		public byte[] Appearance;
	}

	public static class Messages
	{
		public static void WriteJoin(BitWriter w, string name, byte[] appearance = null, string token = null)
		{
			w.WriteByte((byte)MsgType.Join);
			w.WriteUInt16(Protocol.Version);
			w.WriteString(name, 24);
			WriteAppearance(w,appearance);
			w.WriteString(token ?? "", 64);      // jeton de session du compte (le serveur le fait vérifier par le service de comptes)
		}

		private const int MaxAppearanceSize = Protocol.AppearanceSize + 283;
		private static void WriteAppearance(BitWriter w,byte[] appearance)
		{
			int count = Math.Clamp(appearance?.Length ?? 0, Protocol.AppearanceSize, MaxAppearanceSize);
			w.WriteUInt16((ushort)count);
			for (int i=0;i<count;i++) w.WriteByte(i < (appearance?.Length ?? 0) ? appearance[i] : (byte)(i >= 2 && i <= 5 ? 128 : 0));
		}

		public static byte[] ReadAppearance(BitReader r)
		{
			int count = r.ReadUInt16();
			if (count < Protocol.AppearanceSize || count > MaxAppearanceSize) { r.Invalidate(); return Array.Empty<byte>(); }
			var a = new byte[count];
			for (int i = 0; i < a.Length; i++) a[i] = r.ReadByte();
			return a;
		}

		public static void WriteInputs(BitWriter w, ushort ackSeq, uint ackBits, List<PlayerInput> pending)
		{
			int count = Math.Min(Protocol.MaxInputsPerPacket, pending.Count);
			w.WriteByte((byte)MsgType.Input);
			w.WriteUInt16(ackSeq);
			w.WriteUInt32(ackBits);
			w.WriteBits((uint)count, 4);
			for (int i = pending.Count - count; i < pending.Count; i++)
				pending[i].Write(w);
		}

		public static void WriteCommand(BitWriter w, CommandType type, byte arg)
		{
			w.WriteByte((byte)MsgType.Command);
			w.WriteByte((byte)type);
			w.WriteByte(arg);
		}

		public static void WritePing(BitWriter w, byte type, Vec3 pos, ushort target = 0xFFFF)
		{
			w.WriteByte((byte)MsgType.Ping);
			w.WriteByte(type);
			w.WriteVec3(pos);
			w.WriteUInt16(target);
		}

		public static void WriteReject(BitWriter w, string reason)
		{
			w.WriteByte((byte)MsgType.JoinReject);
			w.WriteString(reason, 48);
		}

		public static void WriteBalance(BitWriter w)
		{
			var values = GameBalance.ToWire();
			w.WriteByte((byte)MsgType.Balance);
			w.WriteUInt16((ushort)values.Length);
			foreach (var v in values) w.WriteFloat(v);
		}

		public static float[] ReadBalance(BitReader r)
		{
			int n = r.ReadUInt16();
			if (r.Overflow || n > 4096 || n * 32 > r.BitsRemaining) return null;
			var values = new float[n];
			for (int i = 0; i < n; i++) values[i] = r.ReadFloat();
			return values;
		}

		public static void WriteRoster(BitWriter w, List<RosterEntry> entries)
		{
			w.WriteByte((byte)MsgType.Roster);
			w.WriteByte((byte)Math.Min(entries.Count, 255));
			for (int i = 0; i < entries.Count && i < 255; i++)
			{
				w.WriteUInt16(entries[i].Id);
				w.WriteByte(entries[i].Team);
				w.WriteBool(entries[i].IsBot);
				w.WriteString(entries[i].Name, 24);
				WriteAppearance(w,entries[i].Appearance);
			}
		}

		public static List<RosterEntry> ReadRoster(BitReader r)
		{
			int n = r.ReadByte();
			var list = new List<RosterEntry>(n);
			for (int i = 0; i < n && !r.Overflow; i++)
				{
				var e = new RosterEntry { Id = r.ReadUInt16(), Team = r.ReadByte(), IsBot = r.ReadBool(), Name = r.ReadString() };
				e.Appearance = ReadAppearance(r);
				list.Add(e);
			}
			return list;
		}

		public const int MaxGrenadesPerMessage = 28;

		public static void WriteGrenades(BitWriter w, List<GrenadeInfo> list)
		{
			int n = Math.Min(list.Count, MaxGrenadesPerMessage);
			w.WriteByte((byte)MsgType.Grenades);
			w.WriteByte((byte)n);
			for (int i = 0; i < n; i++)
			{
				var g = list[i];
				w.WriteUInt16(g.Id); w.WriteByte(g.Kind); w.WriteBool(g.Stuck);
				w.WriteVec3(g.Pos);
				if (!g.Stuck) w.WriteVec3(g.Vel);
			}
		}

		public static List<GrenadeInfo> ReadGrenades(BitReader r)
		{
			int n = r.ReadByte();
			var list = new List<GrenadeInfo>(n);
			for (int i = 0; i < n && i < MaxGrenadesPerMessage && !r.Overflow; i++)
			{
				var g = new GrenadeInfo { Id = r.ReadUInt16(), Kind = r.ReadByte(), Stuck = r.ReadBool() };
				g.Pos = r.ReadVec3();
				if (!g.Stuck) g.Vel = r.ReadVec3();
				list.Add(g);
			}
			return list;
		}

		public static void WriteInventory(BitWriter w, InventoryState inv)
		{
			w.WriteByte((byte)MsgType.Inventory);
			inv.Write(w);
		}
	}
}
