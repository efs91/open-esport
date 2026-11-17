using System;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Pings : un joueur désigne un endroit (ici, danger, ennemi) ; le serveur le valide puis le renvoie à toute son équipe (l'auteur compris).
	/// Valable dans tous les modes : l'équipe est celle du joueur (en Sabotage : son camp). Seul un joueur en vie peut pinger, et pas plus de trois fois par seconde.
	/// </summary>
	public sealed partial class GameServer
	{
		private const float PingMaxDistance = 900f;

		private void HandlePing(ServerPlayer p)
		{
			byte type = _reader.ReadByte();
			Vec3 pos = _reader.ReadVec3();
			if (_reader.Overflow || !p.Alive) return;
			if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z)) return;
			if (_tick < p.NextPingTick) return;
			p.NextPingTick = _tick + (uint)Math.Max(1, _m.TickRate / 3);
			// l'endroit désigné doit être sur la carte et à une distance raisonnable du joueur
			if (pos.X < _data.MinX - 5f || pos.X > _data.MaxX + 5f || pos.Z < _data.MinZ - 5f || pos.Z > _data.MaxZ + 5f) return;
			if (Vec3.Distance(pos, p.State.Pos) > PingMaxDistance) return;
			if (type > 2) type = 0;

			var ev = new ServerEvent { Type = EventType.Ping, A = p.Id, C = type, Pos = pos };
			foreach (var q in _humans)
				if (q.Team == p.Team) SendEvent(q, ev);
		}
	}
}
