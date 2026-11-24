using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;

namespace OpenEsport.Server
{
	public sealed partial class GameServer
	{
		/// <summary>
		/// 1 VS 1 : deux joueurs, des manches. Chaque manche commence par une courte préparation (immobiles, l'équipement s'affiche), puis on se bat
		/// sans réapparition ; le premier à <see cref="DuelRules.RoundsToWin"/> manches gagne (ou la limite d'un match de tournoi).
		/// Équipement : tiré au hasard à chaque manche et le même pour les deux, choisi par chacun au début de la partie, ou imposé (<see cref="ServerOptions.DuelPolicy"/>).
		/// </summary>
		private sealed class DuelMode : ModeLogic
		{
			private DuelStage _stage;
			private float _stageTimer;
			private int _round;
			private byte _roundWinner = 255;
			private DuelLoadout _shared;
			/// <summary>Choix libre : l'équipement validé de chacun, et celui qu'il est en train de régler.</summary>
			private readonly Dictionary<ServerPlayer, DuelLoadout> _picks = new Dictionary<ServerPlayer, DuelLoadout>(), _drafts = new Dictionary<ServerPlayer, DuelLoadout>();

			public DuelMode(GameServer s, ModeInfo info) : base(s, info) { }

			private DuelPolicy Policy => S._opt.DuelPolicy;
			private int RoundsToWin => S._opt.ScoreLimit > 0 ? S._opt.ScoreLimit : DuelRules.RoundsToWin;

			public override string Debug() => $"stage={_stage} timer={_stageTimer:F1} round={_round} score={Score[0]}-{Score[1]} loadout={_shared}";
			public override bool Frozen(ServerPlayer p) => _stage == DuelStage.Pick || _stage == DuelStage.Prepare;
			public override bool CanDamage(ServerPlayer attacker, ServerPlayer victim) => _stage == DuelStage.Round;
			public override float RespawnDelay(ServerPlayer p) => 0f;
			public override ClassKit KitFor(ServerPlayer p) => DuelRules.Kit(LoadoutOf(p));

			public DuelLoadout LoadoutOf(ServerPlayer p) => Policy switch
			{
				DuelPolicy.Fixed => S._opt.DuelFixed,
				DuelPolicy.Choice => p != null && (_picks.TryGetValue(p, out var l) || _drafts.TryGetValue(p, out l)) ? l : _shared,
				_ => _shared,
			};

			public override void OnStart()
			{
				Score[0] = Score[1] = 0; _round = 0; _roundWinner = 255; _picks.Clear(); _drafts.Clear();
				_shared = DuelRules.RandomLoadout(S._rng);
				if (Policy != DuelPolicy.Choice) { StartRound(); return; }
				_stage = DuelStage.Pick; _stageTimer = DuelRules.PickSeconds;
				foreach (var p in Players)
				{
					p.Alive = true; p.Health = 100f; p.State = PlayerMovement.Spawn(Spawn(p), S._world);
					if (p.Brain is BotBrain bb) _picks[p] = DuelRules.RandomLoadout(bb.Rng);
					S.ApplyKit(p, KitFor(p));
				}
				GameServer.Log("1 VS 1 : choix de l'équipement");
			}

			private void StartRound()
			{
				_round++;
				_roundWinner = 255;
				if (Policy == DuelPolicy.Random) _shared = DuelRules.RandomLoadout(S._rng);
				_stage = DuelStage.Prepare; _stageTimer = DuelRules.PrepareSeconds;
				foreach (var p in Players)
				{
					p.Alive = true; p.Health = 100f; p.LastAttacker = null; p.UseId = 0; p.Reloading = false; p.ProtectUntil = 0; p.DiedThisRound = false;
					p.State = PlayerMovement.Spawn(Spawn(p), S._world);
					p.ShotCooldown = 0f;
					S.ApplyKit(p, KitFor(p));
				}
				GameServer.Log($"1 VS 1 : manche {_round} ({Score[0]}-{Score[1]}), équipement {(Policy == DuelPolicy.Choice ? "choisi" : LoadoutOf(null).ToString())}");
			}

			/// <summary>Munitions infinies : la réserve de chaque arme reste à <see cref="DuelRules.ReserveMagazines"/> chargeurs pleins (on recharge quand même).</summary>
			private void TopUpAmmo(ServerPlayer p)
			{
				var inv = p.Inventory;
				if (inv == null) return;
				bool changed = false;
				var none = new List<LootItem>();
				foreach (var w in inv.Weapons)
				{
					var def = w == null ? null : WeaponDefs.Get(w.WeaponId);
					if (def == null || def.Melee || def.MagazineSize <= 0 || def.AmmoType == 0) continue;
					var ammoId = ItemDefs.AmmoItemFor(def.AmmoType);
					if (ammoId == ItemIds.None) continue;
					int want = Tiers.Magazine(def, w.Tier) * DuelRules.ReserveMagazines;
					for (int guard = 0; guard < 10; guard++)
					{
						int missing = want - inv.GetAmmo(def.AmmoType);
						if (missing <= 0) break;
						var a = LootItem.Of(ammoId); a.Count = (ushort)Math.Min(missing, ItemDefs.Get(ammoId).StackSize);
						int before = a.Count;
						inv.TryPickup(ref a, none);
						if (a.Count == before) break;       // plus de place dans le sac
						changed = true;
					}
				}
				if (changed) S.SendInventory(p);
			}

			private int AliveOf(int team) { int n = 0; foreach (var p in Players) if (p.Alive && p.Team == team) n++; return n; }
			private float LifeOf(int team) { float v = 0f; foreach (var p in Players) if (p.Alive && p.Team == team) v += p.Health + p.Shield; return v; }

			public override void Tick(float dt)
			{
				_stageTimer -= dt;
				foreach (var p in Players) if (p.Alive && !p.Reloading) TopUpAmmo(p);
				TimeLeft = MathF.Max(0f, _stageTimer);
				switch (_stage)
				{
					case DuelStage.Pick:
						if (_stageTimer <= 0f || Players.All(p => _picks.ContainsKey(p)))
						{
							foreach (var p in Players) if (!_picks.ContainsKey(p)) _picks[p] = DuelRules.RandomLoadout(S._rng);       // pas de choix à temps : au hasard
							StartRound();
						}
						break;
					case DuelStage.Prepare:
						if (_stageTimer <= 0f) { _stage = DuelStage.Round; _stageTimer = DuelRules.RoundSeconds; }
						break;
					case DuelStage.Round:
						int a0 = AliveOf(0), a1 = AliveOf(1);
						if (a0 == 0 || a1 == 0) EndRound(a0 == a1 ? (byte)255 : (byte)(a0 > 0 ? 0 : 1));
						else if (_stageTimer <= 0f)
						{
							// temps écoulé : la manche va à celui à qui il reste le plus de vie + bouclier
							float l0 = LifeOf(0), l1 = LifeOf(1);
							EndRound(MathF.Abs(l0 - l1) < 0.5f ? (byte)255 : (byte)(l0 > l1 ? 0 : 1));
						}
						break;
					case DuelStage.RoundEnd:
						if (_stageTimer <= 0f) StartRound();
						break;
				}
			}

			/// <summary>Fin de manche : le gagnant marque un point (255 = manche nulle, rejouée).</summary>
			private void EndRound(byte winner)
			{
				_roundWinner = winner;
				_stage = DuelStage.RoundEnd; _stageTimer = DuelRules.EndSeconds;
				if (winner != 255) Score[winner]++;
				GameServer.Log(winner == 255 ? $"1 VS 1 : manche {_round} nulle" : $"1 VS 1 : manche {_round} gagnée par l'équipe {winner} ({Score[0]}-{Score[1]})");
				if (winner != 255 && Score[winner] >= RoundsToWin) S.EndModal(winner);
			}

			public override void OnKill(ServerPlayer killer, ServerPlayer victim, byte weapon)
			{
				if (killer != null && killer != victim && killer.Team != victim.Team) killer.Kills++;
			}

			/// <summary>Un joueur s'en va pour de bon en pleine partie : l'autre gagne.</summary>
			public override void OnPlayerLeft(ServerPlayer p)
			{
				_picks.Remove(p); _drafts.Remove(p);
				if (S.Phase != MatchPhase.Playing) return;
				if (Players.Any(q => q != p && q.Team == p.Team) || !Players.Any(q => q != p && q.Team != p.Team)) return;
				byte other = (byte)(1 - p.Team);
				Score[other] = Math.Max(Score[other], RoundsToWin);
				GameServer.Log($"1 VS 1 : {p.Name} est parti, l'équipe {other} gagne");
				S.EndModal(other);
			}

			public override void OnDuelPick(ServerPlayer p, byte arg)
			{
				if (Policy != DuelPolicy.Choice || _stage != DuelStage.Pick || S.Phase != MatchPhase.Playing || _picks.ContainsKey(p)) return;
				var current = LoadoutOf(p);
				if (arg == DuelRules.ConfirmPick) { _picks[p] = current; return; }
				_drafts[p] = DuelRules.WithPick(current, arg);
				S.ApplyKit(p, KitFor(p));         // on voit tout de suite l'arme choisie en main
			}

			public override void FillState(ref ModeState st, ServerPlayer viewer)
			{
				st.Stage = (byte)_stage; st.Round = (byte)_round; st.Target = (ushort)RoundsToWin; st.RoundWinner = _roundWinner;
				st.DuelPolicy = Policy;
				st.Loadout = LoadoutOf(viewer);
				st.Picked = _picks.ContainsKey(viewer);
				var opp = Players.FirstOrDefault(q => q.Team != viewer.Team);
				st.OpponentLoadout = LoadoutOf(opp);
				// en choix libre, l'équipement adverse n'est révélé qu'une fois les choix terminés (Weapon = 255 : inconnu)
				if (Policy == DuelPolicy.Choice && _stage == DuelStage.Pick || opp == null) st.OpponentLoadout.Weapon = 255;
			}

			public override bool BotGoal(ServerPlayer p, BotBrain b, out Vec3 goal, out float arrive, out Buttons hold)
			{
				hold = Buttons.None; arrive = 5f; goal = default;
				if (_stage != DuelStage.Round) return false;
				var opp = Players.FirstOrDefault(q => q.Alive && q.Team != p.Team);
				if (opp == null) return false;
				goal = opp.State.Pos;
				return true;
			}
		}
	}
}
