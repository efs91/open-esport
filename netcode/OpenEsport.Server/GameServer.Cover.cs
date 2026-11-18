using System;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	/// <summary>
	/// Bots : se battre en s'abritant. Un bot tire un moment à découvert, puis court se cacher derrière un obstacle proche (rocher, caisse, mur) en s'accroupissant,
	/// attend un instant, puis ressort pour tirer de nouveau. Et il ne ramasse pas de butin tant qu'un vrai joueur de son équipe est à côté.
	/// </summary>
	public sealed partial class GameServer
	{
		/// <summary>Distance en dessous de laquelle un bot laisse le butin à un joueur de son équipe (il l'attend plutôt que de sauter dessus).</summary>
		private const float LootYieldDistance = 12f;

		/// <summary>Rayon (m) autour du point regardé, et portée (m) du regard, dans lesquels un coéquipier humain « réserve » un objet : les bots ne le lui piquent pas, mais ramassent le reste.</summary>
		private const float LootClaimRadius = 2.5f, LootClaimRange = 12f;

		/// <summary>Un coéquipier humain est tout près de cet objet, ou le regarde (ou regarde juste à côté).</summary>
		private bool HumanClaims(ServerPlayer p, Vec3 itemPos)
		{
			for (int i = 0; i < _humans.Count; i++)
			{
				var q = _humans[i];
				if (q == p || !q.Alive || q.Team != p.Team) continue;
				var eye = q.State.Pos + new Vec3(0, Sim.EyeFor(q.State.Stance), 0);
				var to = itemPos - eye;
				float dist = to.Length();
				if (dist <= 1.8f) return true;                                  // il est dessus
				if (dist > LootClaimRange) continue;
				float cp = MathF.Cos(q.Pitch);
				var dir = new Vec3(-MathF.Sin(q.Yaw) * cp, MathF.Sin(q.Pitch), -MathF.Cos(q.Yaw) * cp);
				float along = to.X * dir.X + to.Y * dir.Y + to.Z * dir.Z;
				if (along <= 0f) continue;                                      // derrière lui
				float off2 = dist * dist - along * along;                       // distance de l'objet à l'axe du regard
				if (off2 <= LootClaimRadius * LootClaimRadius) return true;
			}
			return false;
		}

		private bool HumanTeammateNear(ServerPlayer p, float radius)
		{
			for (int i = 0; i < _humans.Count; i++)
			{
				var q = _humans[i];
				if (q == p || !q.Alive || q.Team != p.Team) continue;
				if (FlatDistance(q.State.Pos, p.State.Pos) <= radius) return true;
			}
			return false;
		}

		/// <summary>
		/// Cherche un abri tout proche : un point que le bot rejoint en ligne droite et d'où la tête d'un joueur accroupi n'est pas visible de la menace
		/// (un obstacle est sur la ligne). Choisit le plus proche, en évitant de s'avancer vers l'adversaire.
		/// </summary>
		private bool FindCover(ServerPlayer p, BotBrain b, Vec3 pos, Vec3 threat)
		{
			Vec3 threatEye = threat + new Vec3(0, 1.6f, 0);
			float baseAngle = (float)(b.Rng.NextDouble() * Math.PI * 2.0);
			float bestScore = float.MaxValue;
			Vec3 best = default;
			bool found = false;
			float myThreatDist = FlatDistance(pos, threat);
			for (int ring = 0; ring < 4; ring++)
			{
				float r = ring == 0 ? 2.5f : ring == 1 ? 4.5f : ring == 2 ? 7f : 10f;
				for (int i = 0; i < 24; i++)
				{
					float a = baseAngle + i * (MathF.PI * 2f / 24f);
					var c = new Vec3(pos.X + MathF.Cos(a) * r, pos.Y, pos.Z + MathF.Sin(a) * r);
					if (c.X < _data.MinX + 2f || c.X > _data.MaxX - 2f || c.Z < _data.MinZ + 2f || c.Z > _data.MaxZ - 2f) continue;
					Vec3 head = c + new Vec3(0, 1.0f, 0);               // tête d'un joueur accroupi
					Vec3 to = head - threatEye;
					float dist = to.Length();
					if (dist < 1f) continue;
					if (!(_world.Raycast(threatEye, to / dist, dist, out float wall) && wall < dist - 0.4f)) continue;      // visible : pas un abri
					if (!_nav.LineClear(pos, c)) continue;                                                                  // il faut pouvoir s'y rendre tout droit
					float score = r + (FlatDistance(c, threat) < myThreatDist - 2f ? 6f : 0f);                             // plutôt pas vers l'adversaire
					if (score < bestScore) { bestScore = score; best = c; found = true; }
				}
				if (found && ring < 3) break;      // un abri tout proche suffit
			}
			if (found) { b.CoverPos = best; b.HasCover = true; }
			return found;
		}
	}
}
