using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
	/// Cycle : <c>open</c> (inscriptions selon la période choisie, check-in avant le départ) → <c>running</c> (étape après étape, chacune dans son mode et son
	/// déroulement : séries, poules, poules de division, tableaux ; les qualifiés d'une étape passent à la suivante) → <c>done</c>.
	/// <c>cancelled</c> : annulé par l'organisateur, ou au départ automatique faute d'inscrits.
	/// </para>
	/// <para>
	/// Une séparation en divisions donne, comme dans ManageComp, une compétition par division : la première continue dans ce tournoi, chacune des autres
	/// devient un tournoi rattaché, déjà en cours, qui joue la suite de la formule avec ses inscrits.
	/// </para>
	/// Les manches des séries ont un numéro global (toutes étapes confondues) : c'est lui qui identifie la partie réservée d'un lobby. Un match est désigné
	/// par la clé de ManageComp, précédée de son étape et suivie du numéro de la partie s'il se joue en plusieurs (<c>f1p0m2g1</c>).
	/// </summary>
	public sealed class TournamentService
	{
		public const int MaxOpenPerOwner = 5, MaxPlayers = TournamentConfig.MaxEntriesLimit;

		private readonly AccountStore _store;
		private readonly ManageCompApi _api;

		public bool Configured => _api.Configured;

		public TournamentService(AccountStore store, ManageCompApi api) { _store = store; _api = api; }

		/// <summary>Une opération à la fois sur un tournoi en cours : deux lobbys qui finissent ensemble ne font pas avancer la série deux fois.</summary>
		private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new ConcurrentDictionary<long, SemaphoreSlim>();

		private async Task<TournamentResult> Exclusive(long tournament, Func<Task<TournamentResult>> body)
		{
			var gate = _gates.GetOrAdd(tournament, _ => new SemaphoreSlim(1));
			await gate.WaitAsync();
			try { return await body(); }
			finally { _flows.TryRemove(tournament, out _); _standings.TryRemove(tournament, out _); gate.Release(); }
		}

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
			string error = cfg.Validate(AccountStore.Now(), creating: true) ?? ResolveObservers(cfg, owner);
			if (error != null) return TournamentResult.Fail(error);
			cfg.CheckInOpenedAt = 0;
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
			cfg.CheckInOpenedAt = old.CheckInOpenedAt;          // check-in ouvert à la main : il le reste
			string error = cfg.Validate(AccountStore.Now(), creating: cfg.StartAt != old.StartAt) ?? ResolveObservers(cfg, t.Owner);
			if (error != null) return TournamentResult.Fail(error);
			if (Entries(t.Id) > cfg.MaxEntries) return TournamentResult.Fail("Il y a déjà plus d'inscrits que ce maximum.");
			_store.UpdateTournament(t.Id, cfg);
			return TournamentResult.Ok(t.Id);
		}

		/// <summary>Les observateurs invités doivent exister ; leurs noms sont remis tels que le compte les écrit (l'organisateur l'est d'office).</summary>
		private string ResolveObservers(TournamentConfig cfg, long owner)
		{
			var names = new List<string>();
			foreach (var o in cfg.Observers)
			{
				var a = _store.FindByLogin(o);
				if (a == null) return $"Observateur inconnu : aucun joueur ne s'appelle « {o} ».";
				if (a.Id != owner && !names.Contains(a.Login, StringComparer.OrdinalIgnoreCase)) names.Add(a.Login);
			}
			cfg.Observers = names;
			return null;
		}

		/// <summary>L'organisateur, ou un observateur invité : il voit les parties en cours depuis le lobby du tournoi (et des divisions qui en sont issues).</summary>
		public bool IsObserver(TournamentRow t, long account)
		{
			if (t == null || account == 0) return false;
			if (t.Owner == account) return true;
			string login = _store.FindById(account)?.Login;
			if (login == null) return false;
			var root = t.Parent != 0 ? _store.Tournament(t.Parent) ?? t : t;
			return ConfigOf(root).Observers.Contains(login, StringComparer.OrdinalIgnoreCase) || ConfigOf(t).Observers.Contains(login, StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>L'organisateur ouvre le check-in tout de suite, quelle que soit l'heure prévue (même sans check-in prévu : il devient obligatoire).</summary>
		public TournamentResult OpenCheckIn(long tournament, long who)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.Owner != who) return TournamentResult.Fail("Seul l'organisateur ouvre le check-in.");
			if (t.State != "open") return TournamentResult.Fail("Le tournoi a commencé.");
			var cfg = ConfigOf(t);
			long now = AccountStore.Now();
			if (cfg.CheckInOpensAt > 0 && cfg.CheckInOpensAt <= now) return TournamentResult.Fail("Le check-in est déjà ouvert.");
			cfg.CheckInOpenedAt = now;
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
			// annuler le tournoi d'origine annule aussi les divisions qui en sont issues
			if (t.Parent == 0)
				foreach (var d in _store.TournamentDivisions(t))
					if (d.Id != t.Id && d.State == "running") await CancelCore(d, "Annulé par l'organisateur.");
			return TournamentResult.Ok(t.Id);
		}

		private async Task CancelCore(TournamentRow t, string reason)
		{
			_store.SetTournamentState(t.Id, "cancelled", t.Race);
			_store.SetTournamentNote(t.Id, reason);
			try { await _api.Delete(t.Contest); } catch (ManageCompException) { }
		}

		/// <summary>Lancement par l'organisateur (départ manuel, ou plus tôt que l'heure prévue).</summary>
		public Task<TournamentResult> Start(long tournament, long who) => Exclusive(tournament, () => StartCore(tournament, who, automatic: false));

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
				await Settle(t.Id);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		}

		/// <summary>
		/// « Tour suivant » jusqu'à l'étape <paramref name="phase"/> (en réglant au passage la composition des poules ou des divisions), puis la règle :
		/// lobbys composés d'après le nombre de qualifiés, poules ou tableau. Une séparation crée aussitôt les tournois des divisions et passe à la suite.
		/// Classement général atteint : le tournoi est terminé.
		/// </summary>
		private async Task EnterNextPhase(long tournament, TournamentConfig cfg, int phase, int raceBase)
		{
			var t = _store.Tournament(tournament);
			var playable = await _api.PlayableStages(t.Contest);
			if (phase >= playable.Count || phase >= cfg.Phases.Count) { _store.SetTournamentState(t.Id, "done", t.Race); return; }
			var (target, kind) = playable[phase];
			var p = cfg.Phases[phase];
			for (int guard = 0; guard < 10; guard++)
			{
				var (currentKind, current) = await _api.CurrentStage(t.Contest);
				if (current >= target) break;
				if (currentKind == "divisions") await _api.ConfigureDivisions(t.Contest, current, p);
				else if (currentKind == "pool_allocator" && kind == "pool_supervisor" && p.PoolSize > 0) await _api.ConfigurePoolSize(t.Contest, current, p.PoolSize);
				var before = currentKind == "checkin" ? await _api.CheckinNames(t.Contest) : null;
				await _api.Next(t.Contest);
				if (before == null) continue;
				// à la fin de l'appel, ManageComp renumérote les participants (par ordre alphabétique) : chacun est retrouvé par son nom
				var after = await _api.CheckinNames(t.Contest);
				_store.RemapTournamentEntries(t.Id, before.Where(x => after.ContainsKey(x.Key)).ToDictionary(x => x.Value, x => after[x.Key]));
			}
			switch (kind)
			{
				case "lobby":
					int n = await _api.LobbyCompetitors(t.Contest, target);
					if (n <= 0) n = Entries(t.Id);
					await _api.ConfigureLobby(t.Contest, target, p, (n + p.LobbySize - 1) / p.LobbySize);
					// deux équipes inscrites par lobby : une équipe seule dans le sien (nombre impair) est exemptée, elle gagne ses matchs
					if (p.TwoSideLobby && !p.RandomTeams)
					{
						var data = await _api.StageData(t.Contest, target);
						foreach (var l in data.GetProperty("lobbies").EnumerateArray())
						{
							var rows = l.GetProperty("rows").EnumerateArray().ToList();
							if (rows.Count != 1) continue;
							int lobby = l.GetProperty("index").GetInt32(), reference = rows[0].GetProperty("reference").GetInt32();
							for (int race = 0; race < p.Races; race++)
							{
								await _api.SetFinish(t.Contest, target, lobby, race, reference, 1);
								_store.SetRaceResult(t.Id, new RaceResult(raceBase + race, reference, lobby, 1, 0, ManageCompApi.RacePoints(p, 1, 0)));
							}
						}
					}
					break;
				case "divisions":
				case "pool_supervisor": await _api.ConfigurePools(t.Contest, target, p); break;
				case "table": await _api.ConfigureTable(t.Contest, target, p); break;
				case "splitting":
					await Split(t, cfg, phase, target);
					await EnterNextPhase(tournament, cfg, phase + 1, raceBase);
					return;
			}
			_store.SetTournamentPhase(t.Id, phase, raceBase);
		}

		/// <summary>Séparation en divisions : ManageComp crée une compétition par division (sauf la première), le jeu un tournoi rattaché à chacune, avec ses inscrits.</summary>
		private async Task Split(TournamentRow t, TournamentConfig cfg, int phase, int stage)
		{
			var (names, players) = await _api.ConfigureSplit(t.Contest, stage, cfg.Phases[phase]);
			var before = (await _api.Contests()).Select(c => c.id).ToHashSet();
			await _api.Next(t.Contest);
			var created = (await _api.Contests()).Where(c => !before.Contains(c.id)).ToList();
			for (int i = 0; i < names.Count; i++)
			{
				var members = players.Where(x => x.Value.division == i + 1).ToList();
				if (members.Count == 0) continue;
				string contest = created.FirstOrDefault(c => c.name.EndsWith(" - " + names[i], StringComparison.Ordinal)).id;
				if (contest == null) throw new ManageCompException($"ManageComp : la compétition de « {names[i]} » est introuvable.");
				var child = cfg.Copy();
				child.Phases = cfg.Phases.Skip(phase + 1).ToList();
				child.Title = cfg.Title;
				long id = _store.CreateDivision(t.Parent != 0 ? t.Parent : t.Id, i + 2, contest, child, t.Owner);
				var refs = await _api.CheckinNames(contest);
				foreach (var (reference, (_, name)) in members)
					if (refs.TryGetValue(name, out int moved)) _store.MoveTournamentEntry(t.Id, reference, id, moved);
				await EnterNextPhase(id, child, 0, 0);
				await Settle(id);
			}
			if (names.Count > 0 && players.Values.Any(x => x.division > 0)) _store.SetTournamentDivision(t.Id, Math.Max(1, t.Division));
		}

		/// <summary>Toutes les 30 s : les tournois à départ fixe dont l'heure est venue sont lancés (ou annulés faute d'inscrits).</summary>
		public async Task Tick()
		{
			if (!_api.Configured) return;
			foreach (var t in _store.TournamentsDue(AccountStore.Now()))
				if (ConfigOf(t).FixedStart) await Exclusive(t.Id, () => StartCore(t.Id, t.Owner, automatic: true));
		}

		// ------------------------------------------------------------------ séries de manches

		/// <summary>Étape de lobbys en cours (son indice ManageComp et sa configuration), ou null.</summary>
		private async Task<(int stage, PhaseConfig phase)?> CurrentLobby(TournamentRow t)
		{
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null || !phase.IsLobby) return null;
			var playable = await _api.PlayableStages(t.Contest);
			if (t.Phase >= playable.Count || playable[t.Phase].kind != "lobby") return null;
			return (playable[t.Phase].index, phase);
		}

		/// <summary>Places d'une manche, sans les éliminations.</summary>
		public Task<TournamentResult> ReportRace(long tournament, int race, int lobby, IReadOnlyList<(long account, int place)> placements)
			=> ReportRace(tournament, race, lobby, placements.Select(p => (p.account, p.place, 0)).ToList());

		/// <summary>
		/// Le serveur d'une partie de tournoi rapporte les places (1 = vainqueur) et les éliminations d'une manche. Seule la manche en cours est acceptée ;
		/// les résultats sont saisis dans le lobby de chaque joueur, et les inscrits du lobby qui n'ont pas joué comptent comme abandon. Quand tous les lobbys
		/// ont joué la dernière manche de l'étape, les qualifiés passent à la suivante.
		/// </summary>
		public Task<TournamentResult> ReportRace(long tournament, int race, int lobby, IReadOnlyList<(long account, int place, int kills)> results) => Exclusive(tournament, async () =>
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
				// une équipe : sa meilleure place (tous ses joueurs ont la même dans les modes par équipes) et le total de ses éliminations
				var teams = new Dictionary<int, (int place, int kills)>();
				foreach (var (account, place, kills) in results)
					if (refs.TryGetValue(account, out int reference) && place > 0)
						teams[reference] = teams.TryGetValue(reference, out var had) ? (Math.Min(had.place, place), had.kills + kills) : (place, kills);
				var data = await _api.StageData(t.Contest, stage);
				var rows = data.GetProperty("lobbies").EnumerateArray().Where(l => l.GetProperty("index").GetInt32() == lobby).SelectMany(l => l.GetProperty("rows").EnumerateArray()).ToList();
				foreach (var row in rows)
				{
					int reference = row.GetProperty("reference").GetInt32();
					bool played = teams.TryGetValue(reference, out var r);
					if (RaceFilled(row, local) && !played) continue;
					await _api.SetFinish(t.Contest, stage, lobby, local, reference, played ? ManageCompApi.EncodedPlace(phase, r.place, r.kills) : 0);       // absent : abandon
					_store.SetRaceResult(t.Id, new RaceResult(race, reference, lobby, played ? r.place : 0, r.kills, played ? ManageCompApi.RacePoints(phase, r.place, r.kills) : phase.AbandonPoints));
				}
				await Settle(t.Id);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		});

		/// <summary>
		/// Fait avancer le tournoi tant que c'est possible : manche suivante d'une série jouée dans tous ses lobbys, étape suivante quand celle en cours est
		/// terminée, fin du tournoi. Les exemptés ont leurs places d'avance : plusieurs pas peuvent s'enchaîner.
		/// </summary>
		private async Task Settle(long tournament)
		{
			for (int guard = 0; guard < 64; guard++)
			{
				var t = _store.Tournament(tournament);
				if (t == null || t.State != "running") return;
				var cfg = ConfigOf(t);
				var phase = PhaseOf(t, cfg);
				if (phase == null) return;
				int race = t.Race;
				if (phase.IsLobby)
				{
					var current = await CurrentLobby(t);
					if (current == null) return;
					int local = t.Race - t.RaceBase;
					var data = await _api.StageData(t.Contest, current.Value.stage);
					if (!data.GetProperty("lobbies").EnumerateArray().All(l => l.GetProperty("rows").EnumerateArray().All(r => RaceFilled(r, local)))) return;
					_store.SetTournamentState(t.Id, "running", ++race);
					if (local + 1 < phase.Races) continue;
				}
				else if ((await _api.PendingMatches(t.Contest)).Count > 0) return;
				// étape finie : la suivante (ou la fin)
				if (t.Phase + 1 >= cfg.Phases.Count)
				{
					if (await FinishContest(t) || phase.IsLobby) _store.SetTournamentState(t.Id, "done", race);
					return;
				}
				await EnterNextPhase(t.Id, cfg, t.Phase + 1, race);
			}
		}

		/// <summary>Dernière étape jouée : « Tour suivant » jusqu'au classement général. Faux s'il n'est pas atteint.</summary>
		private async Task<bool> FinishContest(TournamentRow t)
		{
			for (int guard = 0; guard < 3; guard++)
			{
				string next = await _api.TryAdvance(t.Contest);
				if (next == "classification") return true;
				if (next == null) return false;
			}
			return false;
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
		/// battle royale : un groupe par équipe inscrite ou tirée au sort ; modes à deux équipes : le camp de chacun (deux équipes inscrites, ou tirage).
		/// Des bots complètent le battle royale et les équipes tirées au sort ; ailleurs, on joue entre inscrits.
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
			if (phase.TwoTeams)
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
			return new { race = t.Race, races = t.Races, mode = phase.GameMode, teamSize, scoreLimit = phase.GameMode == "br" ? 0 : phase.MaxScore, bots = phase.FillBots, maxBots = phase.FillBots ? phase.MaxBots : 0, accounts, groups, sides };
		}

		// ------------------------------------------------------------------ matchs (poules, tableau)

		private static readonly Regex KeyPattern = new Regex(@"^(?:f(\d+))?(p\d+m\d+|t\d+n\d+)(?:g(\d+))?$", RegexOptions.Compiled);

		/// <summary>La clé d'une partie : l'étape (sauf la première), le match, et le numéro de la partie à jouer (sauf la première).</summary>
		private string GameKey(TournamentRow t, string match)
		{
			int played = GamesOf(t, match).Count;
			return (t.Phase > 0 ? "f" + t.Phase : "") + match + (played > 0 ? "g" + played : "");
		}

		/// <summary>Les parties déjà jouées d'un match de l'étape en cours.</summary>
		private List<(int a, int b)> GamesOf(TournamentRow t, string match) => _store.MatchGames(t.Id).Where(g => g.phase == t.Phase && g.match == match).Select(g => (g.a, g.b)).ToList();

		/// <summary>Le match que désigne la clé d'une partie, si c'est bien la prochaine partie de ce match ; sinon null.</summary>
		private async Task<DuelMatch> MatchOf(TournamentRow t, string key)
		{
			var k = KeyPattern.Match(key ?? "");
			if (!k.Success) return null;
			string match = k.Groups[2].Value;
			if (GameKey(t, match) != key) return null;
			return (await _api.PendingMatches(t.Contest)).FirstOrDefault(x => x.Key == match);
		}

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
				return new MyMatch { Key = GameKey(t, m.Key), Side = a ? 0 : 1, Mine = a ? m.NameA : m.NameB, Opponent = a ? m.NameB : m.NameA };
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
				var m = await MatchOf(t, key);
				if (m == null) return null;
				var players = _store.TournamentEntries(t.Id);
				return new
				{
					a = players.Where(p => p.Reference == m.RefA).Select(p => p.Account).ToList(), b = players.Where(p => p.Reference == m.RefB).Select(p => p.Account).ToList(),
					scoreLimit = phase.MaxScore, teamSize = phase.PlayTeamSize(cfg.TeamSize), mode = phase.GameMode, loadout = phase.GameMode == "1v1" ? phase.Loadout : "", bots = phase.FillBots && phase.GameMode != "1v1", maxBots = phase.FillBots ? phase.MaxBots : 0,
				};
			}
			catch (ManageCompException) { return null; }
		}

		/// <summary>
		/// Le serveur rapporte le score d'une partie d'un match (camp A = camp 0). Une égalité n'est pas enregistrée (la partie reste à jouer). Un match en
		/// plusieurs parties est saisi dans ManageComp quand un camp en a gagné assez. Étape terminée : la suivante.
		/// </summary>
		public Task<TournamentResult> ReportMatch(long tournament, string key, int scoreA, int scoreB) => Exclusive(tournament, async () =>
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (t.State != "running") return TournamentResult.Fail("Le tournoi n'est pas en cours.");
			var cfg = ConfigOf(t);
			var phase = PhaseOf(t, cfg);
			if (phase == null || !phase.IsMatches) return TournamentResult.Fail("La phase en cours se joue en série de manches, pas en matchs.");
			try
			{
				var m = await MatchOf(t, key);
				if (m == null) return TournamentResult.Fail("Ce match n'est pas à jouer (déjà saisi ?).");
				if (scoreA == scoreB) return TournamentResult.Fail("Égalité : le match est à rejouer.");
				if (phase.BestOf > 1)
				{
					var games = GamesOf(t, m.Key);
					games.Add((scoreA, scoreB));
					int winsA = games.Count(g => g.a > g.b), winsB = games.Count(g => g.b > g.a);
					if (Math.Max(winsA, winsB) >= phase.WinsNeeded) await _api.ScoreMatch(t.Contest, m, winsA, winsB);
					_store.AddMatchGame(t.Id, t.Phase, m.Key, games.Count - 1, scoreA, scoreB);
				}
				else await _api.ScoreMatch(t.Contest, m, scoreA, scoreB);
				await Settle(t.Id);
				return TournamentResult.Ok(t.Id);
			}
			catch (ManageCompException e) { return TournamentResult.Fail(e.Message); }
		});

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
				modes = cfg.Phases.Where(p => !p.IsSplit).Select(p => p.GameMode).Distinct().ToList(), division = t.Division, parent = t.Parent, myReference = mine.Reference,
				match = phase != null && phase.IsMatches,
				startAt = cfg.StartAt, startMode = cfg.StartMode, registrationOpensAt = cfg.RegistrationOpensAt, registrationClosesAt = cfg.ClosesAt, checkInMinutes = cfg.CheckInMinutes,
				checkInOpensAt = cfg.CheckInOpensAt, minEntries = cfg.MinEntries, maxEntries = cfg.MaxEntries, registrationOpen,
				owner = login(t.Owner), isOwner = viewer != 0 && t.Owner == viewer, isObserver = IsObserver(t, viewer),
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
			if (p.Kind == "divisions") return "Poules de division";
			if (p.Kind == "split") return "Séparation en divisions";
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
				if (_standings.TryGetValue(t.Id, out var known) && known.until > DateTime.UtcNow) standings = known.rows;
				else
				{
					try { standings = await Standings(t, cfg); _standings[t.Id] = (DateTime.UtcNow.AddSeconds(FlowSeconds), standings); }
					catch (ManageCompException) { }
				}
			}
			var teams = entries.GroupBy(e => e.Reference).Select(g => new
			{
				name = string.Join(" & ", g.Select(e => login(e.Account))), checkedIn = g.Any(e => e.CheckedIn), complete = g.Count() >= cfg.TeamSize,
				invites = invites.Where(i => i.Reference == g.Key).Select(i => login(i.Account)).ToList(),
			});
			var divisions = _store.TournamentDivisions(t).Select(d => new { id = d.Id, division = d.Division, state = d.State, mine = viewer != 0 && _store.TournamentEntries(d.Id).Any(e => e.Account == viewer) }).ToList();
			return new { summary = Summary(t, viewer, login, entries, invites), config = cfg, teams, standings, divisions, flow = await Flow(t, cfg) };
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
				foreach (var (place, name) in await _api.FinalRanking(t.Contest)) list.Add(new { place, name, detail = "" });
				return list;
			}
			var current = await CurrentLobby(t);
			if (current != null) return LobbyRanking(await _api.StageData(t.Contest, current.Value.stage));
			return list;
		}

		private readonly ConcurrentDictionary<long, (DateTime until, List<object> flow)> _flows = new ConcurrentDictionary<long, (DateTime, List<object>)>();
		/// <summary>Classement provisoire gardé quelques secondes, comme le déroulé : le lobby d'un tournoi l'affiche à tous ses joueurs.</summary>
		private readonly ConcurrentDictionary<long, (DateTime until, List<object> rows)> _standings = new ConcurrentDictionary<long, (DateTime, List<object>)>();
		private const double FlowSeconds = 8;

		/// <summary>
		/// Le déroulé d'un tournoi lancé : pour chaque étape, son état et ses résultats — lobbys (classement et manches de chacun), poules (classement et matchs)
		/// ou tours d'un tableau. Gardé quelques secondes : beaucoup de joueurs le regardent en même temps, et un résultat reçu l'efface.
		/// </summary>
		private async Task<List<object>> Flow(TournamentRow t, TournamentConfig cfg)
		{
			if ((t.State != "running" && t.State != "done") || !_api.Configured) return new List<object>();
			if (_flows.TryGetValue(t.Id, out var known) && known.until > DateTime.UtcNow) return known.flow;
			var list = new List<object>();
			try
			{
				var playable = await _api.PlayableStages(t.Contest);
				var results = _store.RaceResults(t.Id).ToDictionary(r => (r.Race, r.Reference));
				var games = _store.MatchGames(t.Id).GroupBy(g => (g.phase, g.match)).ToDictionary(g => g.Key, g => g.Select(x => x.a + "-" + x.b).ToList());
				for (int i = 0, raceBase = 0; i < cfg.Phases.Count && i < playable.Count; i++)
				{
					var p = cfg.Phases[i];
					string state = t.State == "done" || i < t.Phase ? "done" : i == t.Phase ? "current" : "upcoming";
					var groups = new List<object>();
					if (state != "upcoming")
					{
						if (p.IsLobby) groups = LobbyFlow(await _api.StageData(t.Contest, playable[i].index), p, raceBase, results);
						else if (p.IsMatches)
						{
							string kind = p.Kind == "table" ? "table" : "pool_supervisor";
							var matches = await _api.Matches(t.Contest, playable[i].index, kind);
							var rows = kind == "table" ? null : PoolRows(await _api.StageData(t.Contest, playable[i].index));
							groups = MatchFlow(matches, rows, p, i, games);
						}
					}
					list.Add(new { index = i, state, groups });
					if (p.IsLobby) raceBase += p.Races;
				}
			}
			catch (ManageCompException) { return list; }
			_flows[t.Id] = (DateTime.UtcNow.AddSeconds(FlowSeconds), list);
			return list;
		}

		private static List<object> LobbyFlow(JsonElement data, PhaseConfig p, int raceBase, Dictionary<(int, int), RaceResult> results)
		{
			var groups = new List<object>();
			foreach (var l in data.GetProperty("lobbies").EnumerateArray())
			{
				var rows = new List<(int place, object row)>();
				foreach (var r in l.GetProperty("rows").EnumerateArray())
				{
					int reference = r.GetProperty("reference").GetInt32();
					var races = new List<object>();
					for (int k = 0; k < p.Races; k++)
						races.Add(results.TryGetValue((raceBase + k, reference), out var x) ? new { place = x.Place, kills = x.Kills, points = x.Points }
							: RaceFilled(r, k) ? new { place = r.GetProperty("finishes")[k].ValueKind == JsonValueKind.Number ? r.GetProperty("finishes")[k].GetInt32() : 0, kills = 0, points = 0 } : null);
					int place = r.TryGetProperty("place", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt32() : 0;
					int points = r.TryGetProperty("standing", out var st) && st.TryGetProperty("points", out var pts) ? pts.GetInt32() : 0;
					string name = r.TryGetProperty("name", out var nm) ? nm.GetString() : "";
					rows.Add((place, new { place, name, reference, points, races }));
				}
				groups.Add(new { type = "lobby", number = l.GetProperty("index").GetInt32() + 1, rows = rows.OrderBy(x => x.place == 0 ? int.MaxValue : x.place).Select(x => x.row).ToList() });
			}
			return groups;
		}

		/// <summary>Les lignes de chaque poule : place, nom, victoires, différence.</summary>
		private static Dictionary<int, List<object>> PoolRows(JsonElement data)
		{
			var result = new Dictionary<int, List<object>>();
			foreach (var pool in data.GetProperty("pools").EnumerateArray())
			{
				var rows = new List<(int place, object row)>();
				if (pool.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
					foreach (var r in list.EnumerateArray())
					{
						int place = 0, wins = 0; string diff = "";
						if (r.TryGetProperty("dashboard", out var d) && d.ValueKind == JsonValueKind.Object)
						{
							int.TryParse(d.GetProperty("rank").ToString(), out place); int.TryParse(d.GetProperty("victories").ToString(), out wins);
							diff = d.GetProperty("indice").ToString();
						}
						rows.Add((place, new { place, name = r.GetProperty("name").GetString(), reference = r.GetProperty("reference").GetInt32(), points = wins, diff }));
					}
				result[pool.GetProperty("index").GetInt32()] = rows.OrderBy(x => x.place == 0 ? int.MaxValue : x.place).Select(x => x.row).ToList();
			}
			return result;
		}

		private static List<object> MatchFlow(List<DuelMatch> matches, Dictionary<int, List<object>> poolRows, PhaseConfig p, int phase, Dictionary<(int, string), List<string>> games)
		{
			string Score(string text) => !text.StartsWith("V", StringComparison.Ordinal) ? text : text.Length > 1 ? text.Substring(1) : p.MatchScore.ToString();
			object Json(DuelMatch m) => new
			{
				key = m.Key, a = m.NameA, b = m.NameB, refA = m.RefA, refB = m.RefB, scoreA = Score(m.ScoreA), scoreB = Score(m.ScoreB), winner = m.Winner,
				games = games.TryGetValue((phase, m.Key), out var g) ? g : new List<string>(),
			};
			if (poolRows != null)
				return poolRows.OrderBy(x => x.Key).Select(x =>
				{
					var own = matches.Where(m => m.Pool == x.Key).ToList();
					return (object)new { type = "pool", number = x.Key + 1, division = own.Count > 0 ? own[0].Division : 0, rows = x.Value, matches = own.Select(Json).ToList() };
				}).ToList();
			// tableau : du premier tour à la finale, puis les matchs de classement
			return matches.GroupBy(m => (m.Place, m.RoundSize)).OrderBy(g => g.Key.Place).ThenByDescending(g => g.Key.RoundSize)
				.Select(g => (object)new { type = "round", size = g.Key.RoundSize, place = g.Key.Place, matches = g.Select(Json).ToList() }).ToList();
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

		// ------------------------------------------------------------------ lobby du tournoi

		/// <summary>Tchat, joueurs présents et parties en cours des lobbys de tournoi.</summary>
		public readonly TournamentLobby Room = new TournamentLobby();

		/// <summary>Peut écrire dans le tchat du lobby : un inscrit, un observateur, l'organisateur.</summary>
		private bool Member(TournamentRow t, long account) => account != 0 && (IsObserver(t, account) || _store.TournamentEntries(t.Id).Any(e => e.Account == account));

		/// <summary>
		/// Le lobby d'un tournoi : le détail (règles, inscrits, classement provisoire, déroulé), les parties en cours (adresse des serveurs pour les observateurs),
		/// le tchat depuis le message <paramref name="since"/>, les joueurs présents et les observateurs. Le spectateur du lobby y compte comme présent.
		/// </summary>
		public async Task<object> Lobby(long tournament, long viewer, long since, Func<long, string> login)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return null;
			Room.Seen(t.Id, viewer);
			var details = await Details(tournament, viewer, login);
			bool observer = IsObserver(t, viewer);
			var root = t.Parent != 0 ? _store.Tournament(t.Parent) ?? t : t;
			var observers = new List<string> { login(t.Owner) };
			observers.AddRange(ConfigOf(root).Observers.Concat(ConfigOf(t).Observers).Where(o => !observers.Contains(o, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
			var live = Room.Live(t.Id).OrderBy(g => g.Match).ThenBy(g => g.Race).ThenBy(g => g.Lobby).Select(g => new
			{
				race = g.Race, lobby = g.Lobby, match = g.Match, mode = g.Mode, phase = g.Phase, humans = g.Humans, timeLeft = g.TimeLeft, score0 = g.Score0, score1 = g.Score1,
				host = observer ? g.Host : "", port = observer ? g.Port : 0,
			}).ToList();
			return new
			{
				details, live, observer, canChat = Member(t, viewer),
				chat = Room.Lines(t.Id, since).Select(l => new { id = l.Id, at = l.At, login = l.Login, text = l.Text }).ToList(),
				present = Room.Present(t.Id).Select(login).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
				observers,
			};
		}

		/// <summary>Un message dans le tchat du lobby.</summary>
		public TournamentResult Chat(long tournament, long account, string text)
		{
			var t = _store.Tournament(tournament);
			if (t == null) return TournamentResult.Fail("Tournoi introuvable.");
			if (!Member(t, account)) return TournamentResult.Fail("Le tchat est réservé aux inscrits et aux observateurs du tournoi.");
			string error = Room.Post(t.Id, account, Login(account), text);
			return error == null ? TournamentResult.Ok(t.Id) : TournamentResult.Fail(error);
		}

		public object List(long viewer, Func<long, string> login) => _store.Tournaments().Select(t => Summary(t, viewer, login, _store.TournamentEntries(t.Id), _store.TournamentInvites(t.Id))).ToList();
	}
}
