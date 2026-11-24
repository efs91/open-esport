using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenEsport.Accounts
{
	/// <summary>Résultat d'une opération de tournoi : un message d'erreur en français, ou null.</summary>
	public sealed class TournamentResult
	{
		public string Error;
		public long Id;
		public static TournamentResult Fail(string message) => new TournamentResult { Error = message };
		public static TournamentResult Ok(long id = 0) => new TournamentResult { Id = id };
	}

	/// <summary>Le match qu'un joueur doit jouer : sa clé, son camp (0 ou 1) et les noms des deux camps.</summary>
	public sealed class MyMatch
	{
		public string Key;
		public int Side;
		public string Mine, Opponent;
	}

	/// <summary>
	/// Les tournois du jeu. L'organisation (inscrits, lobbys ou poules, places ou scores, classements) est faite par ManageComp ; ce service garde le lien
	/// entre un tournoi du jeu et sa compétition, applique les règles (qui peut créer, s'inscrire, lancer, quand) et pousse les résultats des parties.
	/// <para>
	/// Inscription : seul, ou par équipes. Le chef inscrit son équipe puis invite ses coéquipiers un par un ; chacun voit l'invitation dans son jeu et l'accepte
	/// ou la refuse. Une équipe incomplète au départ est retirée. Avant le départ, les équipes n'existent que dans le jeu (référence négative) ;
	/// elles sont inscrites dans ManageComp au lancement.
	/// </para>
	/// <para>
	/// Cycle : <c>open</c> (inscriptions selon la période choisie, check-in avant le départ) → <c>running</c> (phase après phase, chacune dans son mode :
	/// séries de manches de battle royale ou de Capture du drapeau, poules et tableau de 1 VS 1 ou de Capture du drapeau ; les qualifiés d'une phase passent
	/// à la suivante) → <c>done</c>. <c>cancelled</c> : annulé par l'organisateur, ou au départ automatique faute d'inscrits.
	/// </para>
	/// Les manches des séries ont un numéro global (toutes phases confondues) : c'est lui qui identifie la partie réservée d'un lobby.
	/// </summary>
	public sealed class TournamentService
	{
		public const int MaxOpenPerOwner = 5, MaxPlayers = TournamentConfig.MaxEntriesLimit;

		private readonly AccountStore _store;
		private readonly ManageCompApi _api;

		public bool Configured => _api.Configured;

		public TournamentService(AccountStore store, ManageCompApi api) { _store = store; _api = api; }

		private static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("dd/MM/yyyy");
		public static TournamentConfig ConfigOf(TournamentRow t) => TournamentConfig.FromJson(t.Config) ?? TournamentConfig.Legacy(t);
		private int Entries(long tournament) => _store.TournamentEntries(tournament).Select(p => p.Reference).Distinct().Count();
		private string Login(long account) => _store.FindById(account)?.Login ?? "?";
		/// <summary>Phase en cours d'un tournoi lancé (null avant le départ ou après la dernière).</summary>
		private static PhaseConfig PhaseOf(TournamentRow t, TournamentConfig cfg) => t.State == "running" && t.Phase < cfg.Phases.Count ? cfg.Phases[t.Phase] : null;

		// ------------------------------------------------------------------ création et configuration

		/// <summary>Ancienne forme (une phase, départ à heure fixe) : battle royale en <paramref name="races"/> manches, ou duels poules + tableau.</summary>
		public Task<TournamentResult> Create(long owner, string title, long startAt, int races, int lobbySize, string mode = "br", int teamSize = 1, int scoreLimit = 0)
		{
			bool duel = mode == "duel";
			var cfg = new TournamentConfig
			{
				Title = title, Format = mode, TeamSize = teamSize, StartAt = startAt, StartMode = "fixed", MinEntries = duel ? 2 : 1,
				Phases = TournamentConfig.DefaultPhases(mode, races, lobbySize <= 0 ? 20 : lobbySize, scoreLimit <= 0 ? 10 : scoreLimit),
			};
			return Create(owner, cfg);
		}

		/// <summary>Crée un tournoi d'après sa configuration (page de création du jeu).</summary>
		public async Task<TournamentResult> Create(long owner, TournamentConfig cfg)
		{
			if (!_api.Configured) return TournamentResult.Fail("Les tournois ne sont pas encore ouverts sur ce serveur.");
			if (cfg == null) return TournamentResult.Fail("Configuration manquante.");
			string error = cfg.Validate(AccountStore.Now(), creating: true);
			if (error != null) return TournamentResult.Fail(error);
			if (_store.OpenTournamentsOf(owner) >= MaxOpenPerOwner) return TournamentResult.Fail($"Tu organises déjà {MaxOpenPerOwner} tournois : termine-en un d'abord.");
			try
			{
				string contest = await _api.CreateContest(cfg.Title, Date(cfg.StartAt));
				return TournamentResult.Ok(_store.CreateTournament(contest, cfg, owner));
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>L'organisateur modifie son tournoi avant le départ. La taille des équipes inscrites est figée dès qu'il y a un inscrit.</summary>
		public TournamentResult Update(long tournament, long who, TournamentConfig cfg)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.Owner != who) return TournamentResult.Fail("Seul l'organisateur peut modifier le tournoi.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé : sa configuration ne change plus.");
			if (cfg == null) return TournamentResult.Fail("Configuration manquante.");
			var old = ConfigOf(t);
			if (Entries(t.Id) > 0 && cfg.TeamSize != old.TeamSize)
				return TournamentResult.Fail("Il y a déjà des inscrits : la taille des équipes ne peut plus changer.");
			if (cfg.RegistrationOpensAt <= 0) cfg.RegistrationOpensAt = old.RegistrationOpensAt;
			string error = cfg.Validate(AccountStore.Now(), creating: cfg.StartAt != old.StartAt);
			if (error != null) return TournamentResult.Fail(error);
			if (Entries(t.Id) > cfg.MaxEntries) return TournamentResult.Fail("Il y a déjà plus d'inscrits que ce maximum.");
			_store.UpdateTournament(t.Id, cfg);
			return TournamentResult.Ok(t.Id);
		}

		// ------------------------------------------------------------------ inscriptions et équipes

		private static string RegistrationClosed(TournamentRow t, TournamentConfig cfg, long now)
		{
			if (t.State != "open") return "Les inscriptions sont closes.";
			if (cfg.RegistrationOpensAt > now) return "Les inscriptions ne sont pas encore ouvertes.";
			if (cfg.FixedStart ? now >= cfg.ClosesAt : cfg.RegistrationClosesAt > 0 && now >= cfg.RegistrationClosesAt) return "Les inscriptions sont closes.";
			return null;
		}

		/// <summary>
		/// Inscrit un joueur. En solo, c'est fait ; par équipes, il devient le chef d'une équipe à compléter : il invite ses coéquipiers (<see cref="Invite"/>),
		/// qui acceptent ou refusent. Ses propres invitations dans ce tournoi sont alors retirées.
		/// </summary>
		public Task<TournamentResult> Join(long tournament, long account, string login = null)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return Task.FromResult(TournamentResult.Fail("Tournoi introuvable."));
			var cfg = ConfigOf(t);
			long now = AccountStore.Now();
			string closed = RegistrationClosed(t, cfg, now);
			if (closed != null) return Task.FromResult(TournamentResult.Fail(closed));
			var entries = _store.TournamentEntries(t.Id);
			if (entries.Any(p => p.Account == account)) return Task.FromResult(TournamentResult.Ok(t.Id));
			if (entries.Select(p => p.Reference).Distinct().Count() >= cfg.MaxEntries) return Task.FromResult(TournamentResult.Fail("Le tournoi est complet."));
			int reference = _store.NextLocalReference(t.Id);
			_store.AddTournamentPlayer(t.Id, account, reference, captain: true);
			_store.RemoveTournamentInvites(t.Id, account, null);
			// inscrit pendant le check-in : sa présence est confirmée d'office
			if (cfg.CheckInOpensAt > 0 && now >= cfg.CheckInOpensAt) _store.CheckInTournament(t.Id, reference);
			return Task.FromResult(TournamentResult.Ok(t.Id));
		}

		/// <summary>Le chef d'une équipe invite un joueur (par son nom de compte) ; l'invité voit l'invitation dans son jeu.</summary>
		public TournamentResult Invite(long tournament, long captain, string login)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé : les équipes ne changent plus.");
			var cfg = ConfigOf(t);
			if (cfg.TeamSize <= 1) return TournamentResult.Fail("Ce tournoi se joue en solo.");
			var entries = _store.TournamentEntries(t.Id);
			var mine = entries.FirstOrDefault(e => e.Account == captain);
			if (mine.Account == 0) return TournamentResult.Fail("Inscris d'abord ton équipe.");
			if (!mine.Captain) return TournamentResult.Fail("Seul le chef de l'équipe invite des joueurs.");
			var target = _store.FindByLogin((login ?? "").Trim());
			if (target == null) return TournamentResult.Fail("Aucun joueur ne porte ce nom.");
			if (target.Id == captain) return TournamentResult.Fail("Tu fais déjà partie de ton équipe.");
			if (entries.Any(e => e.Account == target.Id)) return TournamentResult.Fail($"{target.Login} est déjà dans une équipe de ce tournoi.");
			var invites = _store.TournamentInvites(t.Id).Where(i => i.Reference == mine.Reference).ToList();
			if (invites.Any(i => i.Account == target.Id)) return TournamentResult.Ok(t.Id);
			if (entries.Count(e => e.Reference == mine.Reference) + invites.Count >= cfg.TeamSize)
				return TournamentResult.Fail($"Ton équipe est au complet ({cfg.TeamSize} joueurs, invitations en attente comprises).");
			_store.AddTournamentInvite(t.Id, target.Id, mine.Reference, captain);
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>L'invité accepte de rejoindre une équipe (la première qui l'a invité si <paramref name="reference"/> vaut 0).</summary>
		public TournamentResult Accept(long tournament, long account, int reference = 0)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé : les équipes ne changent plus.");
			var cfg = ConfigOf(t);
			var invite = _store.TournamentInvites(t.Id).FirstOrDefault(i => i.Account == account && (reference == 0 || i.Reference == reference));
			if (invite.Account == 0) return TournamentResult.Fail("Cette invitation n'existe plus.");
			var entries = _store.TournamentEntries(t.Id);
			if (entries.Any(e => e.Account == account)) { _store.RemoveTournamentInvites(t.Id, account, null); return TournamentResult.Fail("Tu fais déjà partie d'une équipe de ce tournoi."); }
			int members = entries.Count(e => e.Reference == invite.Reference);
			if (members == 0) { _store.RemoveTournamentInvites(t.Id, account, invite.Reference); return TournamentResult.Fail("Cette équipe n'existe plus."); }
			if (members >= cfg.TeamSize) { _store.RemoveTournamentInvites(t.Id, account, invite.Reference); return TournamentResult.Fail("Cette équipe est déjà au complet."); }
			_store.AddTournamentPlayer(t.Id, account, invite.Reference);
			_store.RemoveTournamentInvites(t.Id, account, null);                     // ses autres invitations dans ce tournoi tombent
			if (entries.Any(e => e.Reference == invite.Reference && e.CheckedIn)) _store.CheckInTournament(t.Id, invite.Reference);
			if (members + 1 >= cfg.TeamSize) _store.RemoveTournamentInvites(t.Id, 0, invite.Reference);       // équipe complète
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>L'invité refuse une invitation (toutes celles de ce tournoi si <paramref name="reference"/> vaut 0).</summary>
		public TournamentResult Decline(long tournament, long account, int reference = 0)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			_store.RemoveTournamentInvites(t.Id, account, reference == 0 ? null : reference);
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>Le chef retire un coéquipier ou annule une invitation (par le nom du joueur).</summary>
		public TournamentResult Kick(long tournament, long captain, string login)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé : les équipes ne changent plus.");
			var entries = _store.TournamentEntries(t.Id);
			var mine = entries.FirstOrDefault(e => e.Account == captain);
			if (mine.Account == 0 || !mine.Captain) return TournamentResult.Fail("Seul le chef de l'équipe gère ses joueurs.");
			var target = _store.FindByLogin((login ?? "").Trim());
			if (target == null) return TournamentResult.Fail("Aucun joueur ne porte ce nom.");
			if (target.Id == captain) return TournamentResult.Fail("Pour quitter le tournoi, désinscris ton équipe.");
			if (entries.Any(e => e.Account == target.Id && e.Reference == mine.Reference)) _store.RemoveTournamentPlayer(t.Id, target.Id);
			_store.RemoveTournamentInvites(t.Id, target.Id, mine.Reference);
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>Désinscription : le chef retire toute son équipe (et ses invitations) ; un coéquipier quitte seulement l'équipe.</summary>
		public async Task<TournamentResult> Leave(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé : tu ne peux plus te désinscrire.");
			var all = _store.TournamentEntries(t.Id);
			var mine = all.FirstOrDefault(p => p.Account == account);
			if (mine.Account == 0) { _store.RemoveTournamentInvites(t.Id, account, null); return TournamentResult.Ok(t.Id); }
			var team = all.Where(p => p.Reference == mine.Reference).ToList();
			bool whole = mine.Captain || team.Count == 1 || !team.Any(p => p.Captain);       // anciens tournois : pas de chef, l'équipe part entière
			if (!whole) { _store.RemoveTournamentPlayer(t.Id, account); return TournamentResult.Ok(t.Id); }
			try { await RemoveEntry(t, mine.Reference, team); }
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>Check-in : un inscrit confirme sa présence (pour toute son équipe) dans les minutes qui précèdent le départ.</summary>
		public TournamentResult CheckIn(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé.");
			var cfg = ConfigOf(t);
			if (cfg.CheckInOpensAt == 0) return TournamentResult.Fail("Ce tournoi n'a pas de check-in.");
			if (AccountStore.Now() < cfg.CheckInOpensAt) return TournamentResult.Fail($"Le check-in ouvre {cfg.CheckInMinutes} minutes avant le départ.");
			var mine = _store.TournamentEntries(t.Id).FirstOrDefault(p => p.Account == account);
			if (mine.Account == 0) return TournamentResult.Fail("Tu n'es pas inscrit à ce tournoi.");
			_store.CheckInTournament(t.Id, mine.Reference);
			return TournamentResult.Ok(t.Id);
		}

		// ------------------------------------------------------------------ départ, annulation

		/// <summary>L'organisateur annule son tournoi (avant la fin) ; la compétition ManageComp est supprimée.</summary>
		public async Task<TournamentResult> Cancel(long tournament, long who)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.Owner != who) return TournamentResult.Fail("Seul l'organisateur peut annuler le tournoi.");
			if (t.State == "done" || t.State == "cancelled") return TournamentResult.Fail("Le tournoi est déjà terminé.");
			await CancelCore(t, "Annulé par l'organisateur.");
			return TournamentResult.Ok(t.Id);
		}

		private async Task CancelCore(TournamentRow t, string reason)
		{
			_store.SetTournamentState(t.Id, "cancelled", t.Race);
			_store.SetTournamentNote(t.Id, reason);
			try { await _api.Delete(t.Contest); } catch (ManageCompException) { }
		}

		/// <summary>Lancement par l'organisateur (départ manuel, ou plus tôt que l'heure prévue).</summary>
		public Task<TournamentResult> Start(long tournament, long who) => StartCore(tournament, who, automatic: false);

		/// <summary>Retire une équipe avant le départ (dans ManageComp aussi si elle y était déjà : anciens tournois).</summary>
		private async Task RemoveEntry(TournamentRow t, int reference, IEnumerable<TournamentEntry> members)
		{
			if (reference > 0) await _api.RemovePlayer(t.Contest, reference);
			foreach (var e in members) _store.RemoveTournamentPlayer(t.Id, e.Account);
			_store.RemoveTournamentInvites(t.Id, 0, reference);
		}

		/// <summary>
		/// Départ : une équipe sans check-in confirmé est retirée, comme une équipe incomplète ; s'il reste assez d'inscrits, ils sont inscrits dans ManageComp,
		/// la formule est construite et la première phase est composée (lobbys ou poules). Au départ automatique, un tournoi sans assez d'inscrits est annulé.
		/// </summary>
		private async Task<TournamentResult> StartCore(long tournament, long who, bool automatic)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (!automatic && t.Owner != who) return TournamentResult.Fail("Seul l'organisateur peut lancer le tournoi.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a déjà commencé.");
			var cfg = ConfigOf(t);
			try
			{
				int incomplete = 0;
				foreach (var team in _store.TournamentEntries(t.Id).GroupBy(e => e.Reference).ToList())
				{
					bool absent = cfg.CheckInOpensAt > 0 && !team.Any(e => e.CheckedIn);
					bool partial = team.Count() < cfg.TeamSize;
					if (partial && !absent) incomplete++;
					if (absent || partial) await RemoveEntry(t, team.Key, team);
				}
				foreach (var i in _store.TournamentInvites(t.Id)) _store.RemoveTournamentInvites(t.Id, i.Account, null);
				int n = Entries(t.Id);
				int needed = Math.Max(cfg.MinEntries, cfg.HasMatches ? 2 : 1);
				if (n < needed)
				{
					string why = (n == 0 ? "Aucun inscrit présent." : $"Pas assez d'inscrits présents ({n} sur {cfg.MinEntries} demandés).")
						+ (incomplete > 0 ? $" {incomplete} équipe(s) incomplète(s) retirée(s)." : "");
					if (automatic) { await CancelCore(t, "Annulé au départ : " + why); return TournamentResult.Fail(why); }
					return TournamentResult.Fail(why);
				}
				// les équipes inscrites dans le jeu entrent dans ManageComp (nom : leurs joueurs)
				foreach (var team in _store.TournamentEntries(t.Id).GroupBy(e => e.Reference).Where(g => g.Key < 0).OrderByDescending(g => g.Key).ToList())      // ordre d'inscription (-1, -2…)
				{
					int reference = await _api.AddPlayer(t.Contest, string.Join(" & ", team.Select(e => Login(e.Account))));
					_store.RenumberTournamentEntry(t.Id, team.Key, reference);
				}
				if (incomplete > 0) _store.SetTournamentNote(t.Id, $"{incomplete} équipe(s) incomplète(s) retirée(s) au départ.");
				await _api.BuildFormula(t.Contest, cfg.Phases);
				_store.SetTournamentState(t.Id, "running", 0);
				_store.SetTournamentPhase(t.Id, 0, 0);
				await EnterNextPhase(t.Id, cfg, phase: 0, raceBase: 0);
				await AdvanceLobbies(t.Id);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>
		/// « Tour suivant » jusqu'à la phase jouée <paramref name="phase"/> (en passant la composition des poules), puis la règle : lobbys composés
		/// d'après le nombre de qualifiés, ou poules / tableau réglés. Classement général atteint : le tournoi est terminé.
		/// </summary>
		private async Task EnterNextPhase(long tournament, TournamentConfig cfg, int phase, int raceBase)
		{
			var t = _store.Tournament(tournament);
			var playable = await _api.PlayableStages(t.Contest);
			if (phase >= playable.Count || phase >= cfg.Phases.Count) { _store.SetTournamentState(t.Id, "done", t.Race); return; }
			int target = playable[phase].index;
			for (int guard = 0; guard < 6; guard++)
			{
				var (_, current) = await _api.CurrentStage(t.Contest);
				if (current >= target) break;
				await _api.Next(t.Contest);
			}
			var p = cfg.Phases[phase];
			switch (playable[phase].kind)
			{
				case "lobby":
					int n = await _api.LobbyCompetitors(t.Contest, target);
					if (n <= 0) n = Entries(t.Id);
					await _api.ConfigureLobby(t.Contest, target, p, (n + p.LobbySize - 1) / p.LobbySize);
					// Capture du drapeau entre équipes inscrites : une équipe seule dans son lobby (nombre impair) est exemptée, elle gagne ses matchs
					if (p.TwoSideLobby && !p.RandomTeams)
					{
						var data = await _api.StageData(t.Contest, target);
						foreach (var l in data.GetProperty("lobbies").EnumerateArray())
						{
							var rows = l.GetProperty("rows").EnumerateArray().ToList();
							if (rows.Count != 1) continue;
							for (int race = 0; race < p.Races; race++) await _api.SetFinish(t.Contest, target, l.GetProperty("index").GetInt32(), race, rows[0].GetProperty("reference").GetInt32(), 1);
						}
					}
					break;
				case "pool_supervisor": await _api.ConfigurePools(t.Contest, target, p); break;
				case "table": await _api.ConfigureTable(t.Contest, target, p); break;
			}
			_store.SetTournamentPhase(t.Id, phase, raceBase);
		}

		/// <summary>Toutes les 30 s : les tournois à départ fixe dont l'heure est venue sont lancés (ou annulés faute d'inscrits).</summary>
		public async Task Tick()
		{
			if (!_api.Configured) return;
			foreach (var t in _store.TournamentsDue(AccountStore.Now()))
				if (ConfigOf(t).FixedStart) await StartCore(t.Id, t.Owner, automatic: true);
		}

		// ------------------------------------------------------------------ séries de manches (battle royale, Capture du drapeau)

		/// <summary>Phase de lobbys en cours (son indice ManageComp et sa configuration), ou null.</summary>
		private async Task<(int stage, PhaseConfig phase)?> CurrentLobby(TournamentRow t)
		{
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null || !phase.IsLobby) return null;
			var playable = await _api.PlayableStages(t.Contest);
			if (t.Phase >= playable.Count || playable[t.Phase].kind != "lobby") return null;
			return (playable[t.Phase].index, phase);
		}

		/// <summary>
		/// Le serveur d'une partie de tournoi rapporte les places d'une manche (1 = vainqueur). Seule la manche en cours est acceptée ; les places sont
		/// saisies dans le lobby de chaque joueur, et les inscrits du lobby qui n'ont pas joué comptent comme abandon. Quand tous les lobbys ont joué
		/// la dernière manche de la phase, les qualifiés passent à la suivante.
		/// </summary>
		public async Task<TournamentResult> ReportRace(long tournament, int race, int lobby, IReadOnlyList<(long account, int place)> placements)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "running" || race != t.Race) return TournamentResult.Fail("Cette manche n'est pas en cours.");
			var refs = _store.TournamentEntries(t.Id).ToDictionary(p => p.Account, p => p.Reference);
			try
			{
				var current = await CurrentLobby(t);
				if (current == null) return TournamentResult.Fail("Pas de série de manches en cours.");
				var (stage, phase) = current.Value;
				int local = race - t.RaceBase;
				// une équipe : sa meilleure place (tous ses joueurs ont la même en battle royale et en Capture du drapeau)
				var places = new Dictionary<int, int>();
				foreach (var (account, place) in placements)
					if (refs.TryGetValue(account, out int reference) && place > 0)
						places[reference] = places.TryGetValue(reference, out int had) ? Math.Min(had, place) : place;
				var data = await _api.StageData(t.Contest, stage);
				var rows = data.GetProperty("lobbies").EnumerateArray().Where(l => l.GetProperty("index").GetInt32() == lobby).SelectMany(l => l.GetProperty("rows").EnumerateArray()).ToList();
				foreach (var row in rows)
				{
					int reference = row.GetProperty("reference").GetInt32();
					if (RaceFilled(row, local) && !places.ContainsKey(reference)) continue;
					await _api.SetFinish(t.Contest, stage, lobby, local, reference, places.TryGetValue(reference, out int place) ? place : 0);       // absent : abandon
				}
				await AdvanceLobbies(t.Id);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>
		/// Avance la série en cours tant que ses manches sont jouées dans tous les lobbys : manche suivante, puis phase suivante (ou la fin).
		/// Les exemptés ont leurs places d'avance : plusieurs pas peuvent s'enchaîner.
		/// </summary>
		private async Task AdvanceLobbies(long tournament)
		{
			for (int guard = 0; guard < 64; guard++)
			{
				var t = _store.Tournament(tournament);
				if (t == null || t.State != "running") return;
				var current = await CurrentLobby(t);
				if (current == null) return;
				var (stage, phase) = current.Value;
				int local = t.Race - t.RaceBase;
				var data = await _api.StageData(t.Contest, stage);
				bool allDone = data.GetProperty("lobbies").EnumerateArray().All(l => l.GetProperty("rows").EnumerateArray().All(r => RaceFilled(r, local)));
				if (!allDone) return;
				_store.SetTournamentState(t.Id, "running", t.Race + 1);
				if (local + 1 < phase.Races) continue;
				// phase finie : la suivante (ou la fin)
				var cfg = ConfigOf(t);
				if (t.Phase + 1 >= cfg.Phases.Count) { await FinishContest(t); return; }
				await EnterNextPhase(t.Id, cfg, t.Phase + 1, t.Race + 1);
			}
		}

		/// <summary>Dernière phase jouée : « Tour suivant » jusqu'au classement général, le tournoi est terminé.</summary>
		private async Task FinishContest(TournamentRow t)
		{
			for (int guard = 0; guard < 3; guard++)
			{
				string next = await _api.TryAdvance(t.Contest);
				if (next == null || next == "classification") break;
			}
			_store.SetTournamentState(t.Id, "done", _store.Tournament(t.Id).Race);
		}

		private static bool RaceFilled(JsonElement row, int race)
		{
			var finishes = row.GetProperty("finishes");
			return race < finishes.GetArrayLength() && finishes[race].ValueKind != JsonValueKind.Null;
		}

		/// <summary>Le lobby de chaque inscrit encore en lice (compte → numéro de lobby) dans la série en cours ; vide hors d'une série.</summary>
		public async Task<Dictionary<long, int>> Lobbies(long tournament)
		{
			var result = new Dictionary<long, int>();
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running") return result;
			var byRef = _store.TournamentEntries(t.Id).GroupBy(p => p.Reference).ToDictionary(g => g.Key, g => g.Select(x => x.Account).ToList());
			try
			{
				var current = await CurrentLobby(t);
				if (current == null) return result;
				var data = await _api.StageData(t.Contest, current.Value.stage);
				foreach (var l in data.GetProperty("lobbies").EnumerateArray())
				{
					int index = l.GetProperty("index").GetInt32();
					foreach (var row in l.GetProperty("rows").EnumerateArray())
						if (byRef.TryGetValue(row.GetProperty("reference").GetInt32(), out var accs)) foreach (var acc in accs) result[acc] = index;
				}
			}
			catch (ManageCompException) { }
			return result;
		}

		/// <summary>Mélange stable (même ordre pour une même manche et un même lobby, quel que soit le processus) : sert au tirage des équipes.</summary>
		private static List<long> Shuffle(IEnumerable<long> accounts, long seed)
		{
			static ulong Mix(ulong z) { z += 0x9E3779B97F4A7C15UL; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return z ^ (z >> 31); }
			return accounts.OrderBy(a => Mix((ulong)a ^ Mix((ulong)seed))).ThenBy(a => a).ToList();
		}

		/// <summary>
		/// La partie d'un lobby pour son serveur : comptes admis, mode, taille des équipes en jeu, score à atteindre, et les équipes —
		/// battle royale : un groupe par équipe inscrite ou tirée au sort ; Capture du drapeau : le camp de chacun (deux équipes inscrites, ou tirage).
		/// </summary>
		public async Task<object> LobbyGame(long tournament, int lobby)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running") return null;
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null || !phase.IsLobby) return null;
			var entries = _store.TournamentEntries(t.Id);
			var lobbies = await Lobbies(tournament);
			var accounts = lobbies.Where(kv => kv.Value == lobby).Select(kv => kv.Key).OrderBy(a => a).ToList();
			var refOf = entries.ToDictionary(e => e.Account, e => e.Reference);
			int teamSize = phase.PlayTeamSize(cfg.TeamSize);
			var groups = new Dictionary<string, long>();
			var sides = new Dictionary<string, int>();
			long seed = t.Id * 1_000_003L + t.Race * 1_009L + lobby;
			if (phase.GameMode == "ctf")
			{
				if (phase.RandomTeams)
				{
					var order = Shuffle(accounts, seed);
					for (int i = 0; i < order.Count; i++) sides[order[i].ToString()] = i % 2;
				}
				else
				{
					var teams = accounts.Select(a => refOf[a]).Distinct().OrderBy(r => r).ToList();
					foreach (var a in accounts) sides[a.ToString()] = Math.Min(1, teams.IndexOf(refOf[a]));
				}
			}
			else if (phase.RandomTeams)
			{
				var order = Shuffle(accounts, seed);
				for (int i = 0; i < order.Count; i++) groups[order[i].ToString()] = 1 + i / Math.Max(1, teamSize);
			}
			else if (cfg.TeamSize > 1)
				foreach (var a in accounts) groups[a.ToString()] = refOf[a];
			return new { race = t.Race, races = t.Races, mode = phase.GameMode, teamSize, scoreLimit = phase.GameMode == "ctf" ? phase.MaxScore : 0, accounts, groups, sides };
		}

		// ------------------------------------------------------------------ matchs (poules, tableau)

		/// <summary>Le match en attente d'un joueur (le premier dans l'ordre de ManageComp), ou null s'il n'a rien à jouer pour l'instant.</summary>
		public async Task<MyMatch> NextMatch(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running") return null;
			var phase = PhaseOf(t, ConfigOf(t));
			if (phase == null || phase.IsLobby) return null;
			int mine = _store.TournamentEntries(t.Id).FirstOrDefault(p => p.Account == account).Reference;
			if (mine == 0) return null;
			try
			{
				var m = (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.RefA == mine || x.RefB == mine);
				if (m == null) return null;
				bool a = m.RefA == mine;
				return new MyMatch { Key = m.Key, Side = a ? 0 : 1, Mine = a ? m.NameA : m.NameB, Opponent = a ? m.NameB : m.NameA };
			}
			catch (ManageCompException) { return null; }
		}

		/// <summary>Les comptes de chaque camp d'un match en attente, son mode et son score à atteindre (null si le match n'existe plus) ; sert au serveur de partie.</summary>
		public async Task<object> MatchGame(long tournament, string key)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running") return null;
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null || phase.IsLobby) return null;
			try
			{
				var m = (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.Key == key);
				if (m == null) return null;
				var players = _store.TournamentEntries(t.Id);
				return new
				{
					a = players.Where(p => p.Reference == m.RefA).Select(p => p.Account).ToList(), b = players.Where(p => p.Reference == m.RefB).Select(p => p.Account).ToList(),
					scoreLimit = phase.MaxScore, teamSize = phase.PlayTeamSize(cfg.TeamSize), mode = phase.GameMode, loadout = phase.GameMode == "1v1" ? phase.Loadout : "",
				};
			}
			catch (ManageCompException) { return null; }
		}

		/// <summary>Le serveur rapporte le score d'un match (camp A = camp 0). Une égalité n'est pas enregistrée (le match reste à jouer). Phase terminée : la suivante.</summary>
		public async Task<TournamentResult> ReportMatch(long tournament, string key, int scoreA, int scoreB)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "running") return TournamentResult.Fail("Le tournoi n'est pas en cours.");
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null || phase.IsLobby) return TournamentResult.Fail("La phase en cours se joue en série de manches, pas en matchs.");
			try
			{
				var m = (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.Key == key);
				if (m == null) return TournamentResult.Fail("Ce match n'est pas à jouer (déjà saisi ?).");
				await _api.ScoreMatch(t.Contest, m, scoreA, scoreB);
				// phase terminée : poules → (poules) → tableau → classement
				if ((await _api.PendingMatches(t.Contest)).Count > 0) return TournamentResult.Ok(t.Id);
				var (kind, _) = await _api.CurrentStage(t.Contest);
				if (kind == "table" || t.Phase + 1 >= cfg.Phases.Count)
				{
					// dernière phase (le tableau est toujours la dernière) : « Tour suivant » n'aboutit qu'une fois tous ses tours joués
					string next = await _api.TryAdvance(t.Contest);
					if (next == "classification") _store.SetTournamentState(t.Id, "done", t.Race);
					return TournamentResult.Ok(t.Id);
				}
				await EnterNextPhase(t.Id, cfg, t.Phase + 1, t.Race);
				await AdvanceLobbies(t.Id);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>Ce que le joueur doit jouer maintenant (manche d'une série avec son lobby, ou match) ; null s'il n'y a rien à jouer.</summary>
		public async Task<object> MyRace(long tournament, long account)
		{
			var t = _store.Tournament(tournament);
			if (t == null || t.State != "running") return null;
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null) return null;
			int teamSize = phase.PlayTeamSize(cfg.TeamSize);
			if (!phase.IsLobby)
			{
				var m = await NextMatch(tournament, account);
				return m == null ? null : new { kind = "duel", tournament = t.Id, match = m.Key, side = m.Side, mine = m.Mine, opponent = m.Opponent, teamSize, mode = phase.GameMode };
			}
			var lobbies = await Lobbies(tournament);
			if (!lobbies.TryGetValue(account, out int lobby)) return null;
			return new { kind = "br", tournament = t.Id, race = t.Race, races = t.Races, lobby, teamSize, mode = phase.GameMode };
		}

		// ------------------------------------------------------------------ affichage

		/// <summary>Résumé commun à la liste et au détail : configuration, état, phase et manche en cours, équipe et invitations du joueur.</summary>
		private object Summary(TournamentRow t, long viewer, Func<long, string> login, List<TournamentEntry> entries, List<TournamentInvite> invites)
		{
			var cfg = ConfigOf(t);
			var phase = t.Phase < cfg.Phases.Count ? cfg.Phases[t.Phase] : null;
			int localRace = t.Race - t.RaceBase;
			var mine = entries.FirstOrDefault(e => e.Account == viewer);
			long now = AccountStore.Now();
			bool registrationOpen = RegistrationClosed(t, cfg, now) == null;
			object myTeam = null;
			if (mine.Account != 0)
			{
				var team = entries.Where(e => e.Reference == mine.Reference).ToList();
				long captain = team.FirstOrDefault(e => e.Captain).Account;
				myTeam = new
				{
					captain = mine.Captain, captainName = login(captain != 0 ? captain : team[0].Account), members = team.Select(e => login(e.Account)).ToList(),
					invites = invites.Where(i => i.Reference == mine.Reference).Select(i => login(i.Account)).ToList(), complete = team.Count >= cfg.TeamSize,
				};
			}
			var invitations = viewer == 0 || mine.Account != 0 ? new List<object>()
				: invites.Where(i => i.Account == viewer).Select(i => (object)new { reference = i.Reference, captain = login(i.Inviter) }).ToList();
			return new
			{
				id = t.Id, title = t.Title, description = cfg.Description, mode = cfg.Format, teamSize = cfg.TeamSize, state = t.State, note = t.Note,
				modes = cfg.Phases.Select(p => p.GameMode).Distinct().ToList(),
				startAt = cfg.StartAt, startMode = cfg.StartMode, registrationOpensAt = cfg.RegistrationOpensAt, registrationClosesAt = cfg.ClosesAt, checkInMinutes = cfg.CheckInMinutes,
				checkInOpensAt = cfg.CheckInOpensAt, minEntries = cfg.MinEntries, maxEntries = cfg.MaxEntries, registrationOpen,
				owner = login(t.Owner), isOwner = viewer != 0 && t.Owner == viewer,
				players = entries.Select(e => e.Reference).Distinct().Count(), completeEntries = entries.GroupBy(e => e.Reference).Count(g => g.Count() >= cfg.TeamSize),
				joined = mine.Account != 0, checkedIn = mine.CheckedIn, myTeam, invitations,
				phase = t.Phase, phases = cfg.Phases.Count, phaseName = phase == null ? "" : PhaseName(cfg, t.Phase), phaseMode = phase?.GameMode ?? "", phaseKind = phase?.Kind ?? "",
				race = t.Race, races = t.Races, phaseRace = phase != null && phase.IsLobby ? Math.Min(localRace + 1, phase.Races) : 0, phaseRaces = phase != null && phase.IsLobby ? phase.Races : 0,
				scoreLimit = phase?.MaxScore ?? t.ScoreLimit, lobbySize = phase?.LobbySize ?? t.LobbySize,
			};
		}

		/// <summary>Nom d'une phase : le sien, sinon « Poules », « Tableau », « Finale » pour la dernière série d'un tournoi à plusieurs phases, « Phase n » pour les autres.</summary>
		public static string PhaseName(TournamentConfig cfg, int index)
		{
			var p = cfg.Phases[index];
			if (!string.IsNullOrWhiteSpace(p.Name)) return p.Name;
			if (p.Kind == "pools") return "Poules";
			if (p.Kind == "table") return "Tableau";
			if (cfg.Phases.Count > 1 && index == cfg.Phases.Count - 1) return "Finale";
			return cfg.Phases.Count > 1 ? $"Phase {index + 1}" : "Manches";
		}

		/// <summary>Détails d'un tournoi pour les joueurs : configuration, équipes (complètes ou non, invitations en attente) et classement de ManageComp.</summary>
		public async Task<object> Details(long tournament, long viewer, Func<long, string> login)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return null;
			var cfg = ConfigOf(t);
			var entries = _store.TournamentEntries(t.Id);
			var invites = _store.TournamentInvites(t.Id);
			var standings = new List<object>();
			if ((t.State == "running" || t.State == "done") && _api.Configured)
			{
				try { standings = await Standings(t, cfg); }
				catch (ManageCompException) { }
			}
			var teams = entries.GroupBy(e => e.Reference).Select(g => new
			{
				name = string.Join(" & ", g.Select(e => login(e.Account))), checkedIn = g.Any(e => e.CheckedIn), complete = g.Count() >= cfg.TeamSize,
				invites = invites.Where(i => i.Reference == g.Key).Select(i => login(i.Account)).ToList(),
			});
			return new { summary = Summary(t, viewer, login, entries, invites), config = cfg, teams, standings };
		}

		/// <summary>
		/// Classement à afficher : celui de la phase en cours (points cumulés d'une série de lobbys), ou le classement général à la fin.
		/// Lignes communes : place, nom, détail.
		/// </summary>
		private async Task<List<object>> Standings(TournamentRow t, TournamentConfig cfg)
		{
			var list = new List<object>();
			if (t.State == "done")
			{
				// tournoi en une seule série : son classement (avec les points) ; sinon le classement général
				if (cfg.Phases.Count == 1 && cfg.Phases[0].IsLobby)
				{
					var playable = await _api.PlayableStages(t.Contest);
					if (playable.Count > 0) return LobbyRanking(await _api.StageData(t.Contest, playable[0].index));
				}
				var data = await _api.FinalRanking(t.Contest);
				foreach (var r in data.GetProperty("classification").GetProperty("rows").EnumerateArray())
				{
					try { var cells = r.GetProperty("cells"); list.Add(new { place = cells[0].GetString(), name = cells[2].GetString(), detail = "" }); }
					catch (Exception e) when (e is KeyNotFoundException || e is InvalidOperationException || e is IndexOutOfRangeException) { }
				}
				return list;
			}
			var current = await CurrentLobby(t);
			if (current != null) return LobbyRanking(await _api.StageData(t.Contest, current.Value.stage));
			return list;
		}

		private static List<object> LobbyRanking(JsonElement data)
		{
			var list = new List<object>();
			if (!data.TryGetProperty("ranking", out var ranking) || ranking.ValueKind != JsonValueKind.Array) return list;
			foreach (var r in ranking.EnumerateArray())
			{
				try
				{
					var st = r.GetProperty("standing");
					string lobby = r.TryGetProperty("lobby", out var l) && l.ValueKind == JsonValueKind.Number ? $" · lobby {l.GetInt32()}" : "";
					list.Add(new { place = r.GetProperty("place").GetInt32().ToString(), name = r.GetProperty("name").GetString(), detail = st.GetProperty("points").GetInt32() + " pts" + lobby });
				}
				catch (Exception e) when (e is KeyNotFoundException || e is InvalidOperationException) { }
			}
			return list;
		}

		public object List(long viewer, Func<long, string> login) => _store.Tournaments().Select(t => Summary(t, viewer, login, _store.TournamentEntries(t.Id), _store.TournamentInvites(t.Id))).ToList();
	}
}
