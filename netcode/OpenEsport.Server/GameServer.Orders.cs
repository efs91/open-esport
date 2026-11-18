using System;
using System.Collections.Generic;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Ordres d'équipe donnés par les pings : un ping sur un adversaire demande aux bots de l'équipe de l'attaquer, un ping sur un endroit leur demande de le défendre
	/// (rester autour et abattre ceux qui s'en approchent). Un ordre dure jusqu'à ce que son auteur le retire (autre ping, ou clic sur le ping dans la carte).
	/// Une équipe n'a qu'un ordre à la fois : le plus récent.
	/// </summary>
	public sealed partial class GameServer
	{
		private sealed class TeamOrder
		{
			public bool Attack;
			public Vec3 Pos;
			public ushort Target;
			public ushort Owner;
		}

		/// <summary>Valeur du type de ping qui retire un ordre (l'octet « cible » donne alors l'auteur du ping à retirer).</summary>
		public const byte PingCancel = 255;

		/// <summary>Rayon autour du ping dans lequel les défenseurs se placent, et distance au-delà de laquelle ils lâchent un adversaire.</summary>
		private const float DefendMinRadius = 3f, DefendRingStep = 1.8f, DefendLeash = 40f;

		private readonly Dictionary<byte, TeamOrder> _orders = new Dictionary<byte, TeamOrder>();

		/// <summary>L'ordre en cours de l'équipe, ou null. Il tombe si son auteur est parti ou si la cible de l'attaque est morte.</summary>
		private TeamOrder OrderFor(byte team)
		{
			if (!_orders.TryGetValue(team, out var o)) return null;
			var owner = GetPlayer(o.Owner);
			if (owner == null || owner.Team != team) { DropOrder(team, o, owner); return null; }
			if (o.Attack)
			{
				var t = GetPlayer(o.Target);
				if (t == null || !t.Alive || t.Team == team) { DropOrder(team, o, owner); return null; }
			}
			return o;
		}

		private void DropOrder(byte team, TeamOrder o, ServerPlayer owner)
		{
			_orders.Remove(team);
			NotifyPingCleared(team, o.Owner);
		}

		/// <summary>Dit aux joueurs de l'équipe d'effacer le ping de cet auteur.</summary>
		private void NotifyPingCleared(byte team, ushort owner)
		{
			var ev = new ServerEvent { Type = EventType.Ping, A = owner, C = PingCancel, Pos = Vec3.Zero };
			foreach (var q in _humans) if (q.Team == team) SendEvent(q, ev);
		}

		/// <summary>Ordre donné par un ping : cible (si on désigne un adversaire en vie) ou endroit. Remplace l'ordre précédent de l'équipe.</summary>
		private void SetOrder(ServerPlayer p, byte type, Vec3 pos, ushort target)
		{
			var o = new TeamOrder { Pos = pos, Owner = p.Id };
			if (type == 2)
			{
				var t = GetPlayer(target);
				if (t != null && t.Alive && t.Team != p.Team) { o.Attack = true; o.Target = t.Id; }
			}
			if (_orders.TryGetValue(p.Team, out var previous) && previous.Owner != p.Id) NotifyPingCleared(p.Team, previous.Owner);      // son marqueur n'a plus de sens
			_orders[p.Team] = o;
		}

		/// <summary>Place d'un défenseur autour du ping : chacun la sienne (angle d'or), sur un anneau de 3 à 8 m.</summary>
		private static Vec3 DefendSpot(ServerPlayer p, Vec3 center, int tries)
		{
			// « tries » : à chaque place inaccessible (dans un obstacle), le bot en essaie une autre
			float angle = p.Id * 2.3999632f + tries * 1.3f, radius = DefendMinRadius + ((p.Id + tries) % 4) * DefendRingStep;
			return center + new Vec3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
		}

		private static float FlatDistance(Vec3 a, Vec3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }

		/// <summary>Le bot a-t-il une arme utilisable ? Sinon il cherche de quoi s'équiper au lieu d'obéir.</summary>
		private static bool BotArmed(ServerPlayer p)
		{
			for (int i = 0; i < p.Inventory.Weapons.Length; i++)
				if (p.Inventory.Weapons[i] != null && BotUsable(p, p.Inventory.Weapons[i])) return true;
			return false;
		}

		/// <summary>Un ordre s'applique à ce bot ? Non s'il porte la bombe (la manche doit avancer) ou s'il n'a pas d'arme ; null sinon.</summary>
		private TeamOrder BotOrder(ServerPlayer p)
		{
			if (p.Inventory.BombInHand || p.Inventory.CountOf(ItemIds.Bomb) > 0 || !BotArmed(p)) return null;
			return OrderFor(p.Team);
		}
	}
}
