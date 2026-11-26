using Openesport.Utils;
using Openesport.Diagnostics;
using Openesport.Inventory;
using Godot;
using System.Linq;
using OpenEsport.Core;

namespace Openesport.Online
{
	/// <summary>
	/// Observateur d'une partie de tournoi : pas de personnage, une caméra à la troisième personne derrière le joueur suivi (la souris tourne autour,
	/// la molette rapproche ou éloigne), clic gauche / clic droit pour passer au joueur suivant / précédent. Le serveur envoie toute la partie autour
	/// du joueur suivi : les autres joueurs, les tirs, la zone, les scores. Limites : on ne voit ni son inventaire ni sa visée exacte.
	/// </summary>
	public partial class OnlineGame
	{
		/// <summary>Regarder la partie sans jouer (organisateur ou observateur d'un tournoi), et le tournoi regardé.</summary>
		public bool Spectating;
		public long SpectatedTournament;

		private ushort _follow = 0xFFFF;
		private byte _followTeam;
		private Vec3 _followPos;
		private bool _followKnown;
		private float _orbit = 3.6f;
		private bool _spectatorReady;

		/// <summary>L'équipe « amie » à l'écran : la mienne, ou celle du joueur suivi pour un observateur.</summary>
		private byte MyTeam => Spectating ? _followTeam : Client.TeamId;

		/// <summary>Observateur, à chaque tick : acquitte les snapshots et change de joueur suivi au clic.</summary>
		private void SpectatorPhysics()
		{
			Client.TickSpectator();
			if (!_spectatorReady)
			{
				_spectatorReady = true;
				_weapons.Visible = false;
				_weapons.HideViewmodels = true;
				_weapons.ProcessMode = ProcessModeEnum.Disabled;
				_player.NetworkVelocity = Vector3.Zero;
			}
			if (_follow == 0xFFFF || !_roster.ContainsKey(_follow)) PickFollow(1);
			if (Input.MouseMode != Input.MouseModeEnum.Captured) return;
			if (Input.IsActionJustPressed(InputManager.Actions.SHOOT)) PickFollow(1);
			else if (Input.IsActionJustPressed(InputManager.Actions.AIM)) PickFollow(-1);
		}

		public override void _UnhandledInput(InputEvent e)
		{
			if (!Spectating || e is not InputEventMouseButton { Pressed: true } b) return;
			if (b.ButtonIndex == MouseButton.WheelUp) _orbit = Mathf.Max(1.5f, _orbit - 0.4f);
			else if (b.ButtonIndex == MouseButton.WheelDown) _orbit = Mathf.Min(9f, _orbit + 0.4f);
		}

		/// <summary>Le joueur suivant (ou précédent) de la liste, en vie de préférence ; le serveur suit le même.</summary>
		private void PickFollow(int step)
		{
			var order = Client.Roster.OrderBy(r => r.IsBot ? 1 : 0).ThenBy(r => r.Id).ToList();
			if (order.Count == 0) return;
			int start = order.FindIndex(r => r.Id == _follow);
			for (int k = 1; k <= order.Count; k++)
			{
				var r = order[((start < 0 ? -1 : start) + step * k % order.Count + order.Count * 2) % order.Count];
				bool dead = _seen.Any(e => e.Id == r.Id && e.Kind == EntityKind.Player && e.Has(EntityFlags.Dead));
				if (dead && k < order.Count) continue;
				Choose(r);
				return;
			}
		}

		private void Choose(RosterEntry r)
		{
			_follow = r.Id;
			_followTeam = r.Team;
			_followKnown = false;
			Client.Follow(r.Id);
		}

		/// <summary>Observateur, à chaque image : la caméra derrière le joueur suivi, le HUD (sa vie, son bouclier), la zone, les scores, la fin.</summary>
		private void SpectatorFrame(float alpha, float delta)
		{
			using (LagTracer.Scope("UpdateRemotes")) UpdateRemotes(alpha, delta);
			InterpolatedEntity target = default;
			bool found = false;
			foreach (var e in _seen) if (e.Id == _follow && e.Kind == EntityKind.Player) { target = e; found = true; break; }
			if (found)
			{
				_followPos = _followKnown ? Vec3.Lerp(_followPos, target.Pos, 1f - Mathf.Exp(-20f * delta)) : target.Pos;
				_followKnown = true;
				_followTeam = target.Team;
				_hud.SetVitals(target.Health, target.Shield, target.ShieldTier - 1, 0, 0f);
			}
			var v = _followPos;
			if (_followKnown)
			{
				_player.GlobalPosition = new Vector3(v.X, v.Y + _player.ColliderHeight * 0.5f, v.Z);
				_head.Position = new Vector3(_head.Position.X, v.Y + 1.5f - _player.GlobalPosition.Y, _head.Position.Z);
			}
			// caméra en orbite derrière la tête du joueur suivi, raccourcie si un mur est entre les deux
			var cam = _head.GetNodeOrNull<Camera3D>("Camera");
			if (cam != null)
			{
				float back = _orbit;
				var space = _player.GetWorld3D()?.DirectSpaceState;
				if (space != null)
				{
					var from = _head.GlobalPosition;
					var to = _head.GlobalTransform * new Vector3(0f, 0.3f, _orbit);
					var query = PhysicsRayQueryParameters3D.Create(from, to);
					query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
					var hit = space.IntersectRay(query);
					if (hit.Count > 0) back = Mathf.Max(0.4f, from.DistanceTo(hit["position"].AsVector3()) - 0.3f);
				}
				cam.Position = new Vector3(0f, 0.3f * back / _orbit, back);
			}
			UpdateZone(v);
			UpdateMode(v);
			UpdateVehicles(delta);
			if (_modeHud != null && IsInstanceValid(_modeHud)) _modeHud.Spectator = true;

			// écran de chargement, salle d'attente, bandeau d'aide, fin de partie
			double now = Time.GetTicksMsec() / 1000.0;
			if (_joinedAt < 0) _joinedAt = now;
			if (_loading && _weapons.WarmUpDone && now - _joinedAt > 0.8) _loading = false;
			var m = Client.Match;
			bool lobby = Client.MatchKnown && m.Phase == MatchPhase.Lobby;
			string overlayText = _loading ? I18n.T("Chargement des armes et des objets…") : lobby ? I18n.T("Observateur — la partie attend ses joueurs : {0}/{1}", m.Lobby, m.Needed) : "";
			bool overlay = _loading || lobby;
			if (overlay != _overlayOn || overlayText != _overlayText) { _overlayOn = overlay; _overlayText = overlayText; _hud.SetLoading(overlay, overlayText); }
			string name = _follow != 0xFFFF ? NameOf(_follow) : "—";
			_hud.SetHint(I18n.T("OBSERVATEUR — tu suis {0}   ·   clic gauche : joueur suivant   ·   clic droit : précédent   ·   molette : distance", name));
			bool royale = Client.MatchKnown && !Client.ModeKnown;
			_hud.SetMatchStats(royale && m.Phase != MatchPhase.Lobby, m.PlayersAlive, m.TeamsAlive, m.TeamSize, m.Kills, m.Damage);
			if (_disconnectReason != null || (Client.MatchKnown && m.Phase == MatchPhase.Over))
				_hud.ShowEnd(true, I18n.T("PARTIE TERMINÉE"), I18n.T("Retour au lobby du tournoi."), Colors.White);
			else _hud.ShowEnd(false);
		}
	}
}
