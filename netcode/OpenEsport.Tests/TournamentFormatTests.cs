using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OpenEsport.Accounts;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Formules de tournoi : règles des étapes, points par élimination, modèles.</summary>
	public class TournamentFormatTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe-format-" + Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { try { File.Delete(_db); } catch { } }

		private static string Validate(int teamSize, params PhaseConfig[] phases) => new TournamentConfig { Title = "Essai", TeamSize = teamSize, StartAt = AccountStore.Now() + 3600, Phases = phases.ToList() }.Validate(AccountStore.Now(), true);

		[Fact]
		public void Tous_les_modes_se_jouent_et_les_etapes_s_enchainent_librement()
		{
			foreach (var mode in PhaseConfig.TwoTeamModes)
			{
				int score = PhaseConfig.ScoreRange(mode).min + 2;
				foreach (var kind in new[] { "lobby", "pools", "table", "divisions" })
					Assert.Null(Validate(5, new PhaseConfig { Kind = kind, Mode = mode, MaxScore = score, BestOf = kind == "lobby" ? 1 : 3 }));
				Assert.Null(Validate(1, new PhaseConfig { Kind = "lobby", Mode = mode, MaxScore = score, Teams = "random", TeamSize = 4 }));
			}
			Assert.Null(Validate(1, new PhaseConfig { Kind = "lobby", Mode = "dm", LobbySize = 10, MaxScore = 20 }));
			Assert.Contains("10 joueurs", Validate(1, new PhaseConfig { Kind = "lobby", Mode = "dm", LobbySize = 20, MaxScore = 20 }));
			Assert.Contains("solo", Validate(3, new PhaseConfig { Kind = "lobby", Mode = "dm", LobbySize = 5, MaxScore = 20 }));
			Assert.Contains("équipes", Validate(1, new PhaseConfig { Kind = "pools", Mode = "sab", MaxScore = 13 }));
			// un tableau peut être suivi d'une autre étape ; une séparation en divisions aussi, mais elle ne commence ni ne finit un tournoi
			Assert.Null(Validate(1, new PhaseConfig { Kind = "table", Mode = "1v1", MaxScore = 3, Qualified = 4 }, new PhaseConfig { Kind = "lobby", Mode = "br" }));
			var split = new PhaseConfig { Kind = "split", Divisions = 2 };
			Assert.Null(Validate(1, new PhaseConfig { Kind = "lobby", Mode = "br" }, split, new PhaseConfig { Kind = "lobby", Mode = "br" }));
			Assert.Contains("après une étape", Validate(1, split, new PhaseConfig { Kind = "lobby", Mode = "br" }));
			Assert.Contains("suivie", Validate(1, new PhaseConfig { Kind = "lobby", Mode = "br" }, split));
			Assert.Contains("1, 3 ou 5", Validate(1, new PhaseConfig { Kind = "table", Mode = "1v1", MaxScore = 3, BestOf = 2 }));
		}

		[Fact]
		public void Points_par_elimination_chaque_place_saisie_vaut_le_score_de_la_manche()
		{
			var p = new PhaseConfig { Kind = "lobby", Mode = "br", KillPoints = 2, Scale = new List<int> { 12, 9, 7 } };
			var scale = ManageCompApi.EncodedScale(p);
			foreach (var (place, kills) in new[] { (1, 0), (2, 5), (3, 1), (9, 4), (9, 0), (1, 500) })
				Assert.Equal(ManageCompApi.RacePoints(p, place, kills), scale[ManageCompApi.EncodedPlace(p, place, kills) - 1]);
			Assert.Equal(19, ManageCompApi.RacePoints(p, 2, 5));
			Assert.Equal(1, ManageCompApi.EncodedPlace(new PhaseConfig { Kind = "lobby" }, 1, 7));          // sans points par élimination, la place est saisie telle quelle
		}

		[Fact]
		public void Les_modeles_du_jeu_sont_valables_et_ceux_des_joueurs_sont_prives_par_defaut()
		{
			foreach (var b in TournamentTemplates.BuiltIn)
			{
				var cfg = b.config.Copy(); cfg.Title = b.name;
				Assert.Null(cfg.ValidateTemplate());
			}
			var store = new AccountStore(_db);
			var templates = new TournamentTemplates(store);
			long a = store.CreateAccount("alice", new byte[] { 1 }, new byte[] { 1 }, 1), b2 = store.CreateAccount("bob", new byte[] { 1 }, new byte[] { 1 }, 1);
			JsonElement List(long who) => JsonDocument.Parse(JsonSerializer.Serialize(templates.List(who))).RootElement;
			var cfgA = TournamentTemplates.BuiltIn[0].config.Copy();
			Assert.NotNull(templates.Save(a, 0, "x", false, cfgA).Error);
			var saved = templates.Save(a, 0, "Ma soirée", false, cfgA);
			Assert.Null(saved.Error);
			Assert.Equal(1, List(a).GetProperty("mine").GetArrayLength());
			Assert.Equal(0, List(b2).GetProperty("shared").GetArrayLength());                         // privé : les autres ne le voient pas
			Assert.NotNull(templates.Share(b2, saved.Id, true).Error);
			Assert.Null(templates.Share(a, saved.Id, true).Error);
			Assert.Equal("Ma soirée", List(b2).GetProperty("shared")[0].GetProperty("name").GetString());
			Assert.Equal(0, List(a).GetProperty("shared").GetArrayLength());                          // les siens ne sont pas en double
			Assert.Equal(saved.Id, templates.Save(a, 0, "ma soirée", false, cfgA).Id);                // même nom : remplacé
			Assert.NotNull(templates.Save(b2, saved.Id, "Volé", false, cfgA).Error);
			Assert.NotNull(templates.Delete(b2, saved.Id).Error);
			Assert.Null(templates.Delete(a, saved.Id).Error);
			Assert.Equal(0, List(a).GetProperty("mine").GetArrayLength());
		}
	}

	/// <summary>
	/// Tournois entiers joués contre un vrai ManageComp local (variables OE_MANAGECOMP_TEST_URL et OE_MANAGECOMP_TEST_KEY) ; sans elles, rien n'est vérifié ici.
	/// </summary>
	public class TournamentLiveTests : IDisposable
	{
		private readonly string _db = Path.Combine(Path.GetTempPath(), "oe-live-" + Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { try { File.Delete(_db); } catch { } }
		private static readonly string Url = Environment.GetEnvironmentVariable("OE_MANAGECOMP_TEST_URL"), Key = Environment.GetEnvironmentVariable("OE_MANAGECOMP_TEST_KEY");

		private readonly Random _rng = new Random(5);

		/// <summary>Joue tout ce qu'un tournoi attend (manches et matchs) jusqu'à sa fin ; retourne le nombre de parties jouées.</summary>
		private async Task<int> Play(TournamentService svc, AccountStore store, long id, bool logical = false)
		{
			int games = 0;
			for (int guard = 0; guard < 400; guard++)
			{
				var t = store.Tournament(id);
				if (t.State != "running") return games;
				var phase = TournamentService.ConfigOf(t).Phases[t.Phase];
				if (phase.IsLobby)
				{
					var lobbies = await svc.Lobbies(id);
					Assert.NotEmpty(lobbies);
					foreach (var lobby in lobbies.GroupBy(l => l.Value))
					{
						var refs = store.TournamentEntries(id).ToDictionary(e => e.Account, e => e.Reference);
						var order = lobby.Select(l => refs[l.Key]).Distinct().OrderBy(_ => _rng.Next()).ToList();
						// « logique » : le joueur au plus petit numéro finit toujours devant
						if (logical) order = lobby.OrderBy(l => l.Key).Select(l => refs[l.Key]).Distinct().ToList();
						if (store.Tournament(id).Race != t.Race) break;
						var r = await svc.ReportRace(id, t.Race, lobby.Key, lobby.Select(l => (l.Key, order.IndexOf(refs[l.Key]) + 1, logical ? 2 : _rng.Next(6))).ToList());
						// un lobby déjà rempli (exempté) ou une étape passée : la manche peut être finie avant ce rapport
						if (r.Error != null) Assert.Contains("manche", r.Error);
						games++;
					}
				}
				else
				{
					var players = store.TournamentEntries(id).GroupBy(e => e.Reference).Select(g => g.First().Account).ToList();
					bool any = false;
					foreach (long p in players)
					{
						var m = await svc.NextMatch(id, p);
						if (m == null) continue;
						any = true;
						Assert.True(m.Key.Length <= 12);
						Assert.NotNull(await svc.MatchGame(id, m.Key));
						bool a = _rng.Next(2) == 0;
						if (logical) a = (m.Side == 0) == (string.CompareOrdinal(m.Mine, m.Opponent) < 0);
						var r = await svc.ReportMatch(id, m.Key, a ? phase.MaxScore : 1, a ? 0 : phase.MaxScore);
						Assert.Null(r.Error);
						games++;
					}
					Assert.True(any, $"{store.Tournament(id).Title} : étape {t.Phase} bloquée");
				}
			}
			return games;
		}

		private async Task<(TournamentService svc, AccountStore store, long id)> Launch(TournamentConfig cfg, int entries)
		{
			var store = new AccountStore(_db);
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(), Url, Key));
			long Make(string l) => store.FindByLogin(l)?.Id ?? store.CreateAccount(l, new byte[] { 1 }, new byte[] { 1 }, 1);
			cfg = cfg.Copy();
			cfg.Title = "Essai"; cfg.StartMode = "manual"; cfg.StartAt = AccountStore.Now() + 3600;
			var r = await svc.Create(Make("orga"), cfg);
			Assert.Null(r.Error);
			for (int e = 0; e < entries; e++)
				for (int k = 0; k < cfg.TeamSize; k++)
				{
					long acc = Make($"Joueur{e}_{k}");
					if (k == 0) { Assert.Null((await svc.Join(r.Id, acc)).Error); continue; }
					Assert.Null(svc.Invite(r.Id, Make($"Joueur{e}_0"), $"Joueur{e}_{k}").Error);
					Assert.Null(svc.Accept(r.Id, acc).Error);
				}
			Assert.Null((await svc.Start(r.Id, store.FindByLogin("orga").Id)).Error);
			return (svc, store, r.Id);
		}

		[Fact]
		public async Task Chaque_modele_du_jeu_se_joue_jusqu_au_bout()
		{
			if (string.IsNullOrEmpty(Url)) return;
			foreach (var b in TournamentTemplates.BuiltIn)
			{
				int entries = b.config.TeamSize > 1 ? 9 : 21;
				var (svc, store, id) = await Launch(b.config, entries);
				Assert.True(await Play(svc, store, id) > 0);
				var t = store.Tournament(id);
				foreach (var d in store.TournamentDivisions(t)) if (d.Id != id) await Play(svc, store, d.Id);
				var ids = store.TournamentDivisions(t).Select(d => d.Id).DefaultIfEmpty(id).ToList();
				if (b.config.Phases.Any(p => p.IsSplit)) Assert.Equal(3, ids.Count);
				int ranked = 0;
				foreach (long each in ids)
				{
					Assert.True(store.Tournament(each).State == "done", $"{b.key} : {store.Tournament(each).State}");
					var details = JsonDocument.Parse(JsonSerializer.Serialize(await svc.Details(each, 0, _ => "x"))).RootElement;
					ranked += details.GetProperty("standings").GetArrayLength();
					foreach (var f in details.GetProperty("flow").EnumerateArray())
						if (!TournamentService.ConfigOf(store.Tournament(each)).Phases[f.GetProperty("index").GetInt32()].IsSplit) Assert.True(f.GetProperty("groups").GetArrayLength() > 0, b.key);
				}
				Assert.True(ranked >= entries, $"{b.key} : {ranked} classés pour {entries} inscrits");
				foreach (long each in ids) await svc.Cancel(each, t.Owner);
			}
		}

		[Fact]
		public async Task Les_resultats_vont_aux_bons_joueurs_jusque_dans_les_divisions()
		{
			if (string.IsNullOrEmpty(Url)) return;
			var cfg = new TournamentConfig
			{
				TeamSize = 1, MinEntries = 2, MaxEntries = 50,
				Phases = new List<PhaseConfig>
				{
					new PhaseConfig { Kind = "lobby", Mode = "br", Races = 2, LobbySize = 20, KillPoints = 1, Scale = Enumerable.Range(0, 20).Select(i => 40 - 2 * i).ToList() },
					new PhaseConfig { Kind = "split", Divisions = 3 },
					new PhaseConfig { Kind = "pools", Mode = "1v1", MaxScore = 3, BestOf = 3 },
				},
			};
			// inscrits dans l'ordre inverse de l'alphabet : ManageComp les renumérote à la fin de l'appel
			var store = new AccountStore(_db);
			var svc = new TournamentService(store, new ManageCompApi(new HttpClient(), Url, Key));
			cfg.Title = "Ordre"; cfg.StartMode = "manual"; cfg.StartAt = AccountStore.Now() + 3600;
			string[] names = { "Aline", "Bruno", "Chloe", "Denis", "Elsa", "Fanny", "Gael", "Hugo", "Ines" };
			var accounts = names.Select(n => store.CreateAccount(n, new byte[] { 1 }, new byte[] { 1 }, 1)).ToList();
			var r = await svc.Create(accounts[0], cfg);
			foreach (long acc in Enumerable.Reverse(accounts)) Assert.Null((await svc.Join(r.Id, acc)).Error);
			Assert.Null((await svc.Start(r.Id, accounts[0])).Error);
			await Play(svc, store, r.Id, logical: true);
			var divisions = store.TournamentDivisions(store.Tournament(r.Id));
			Assert.Equal(3, divisions.Count);
			for (int k = 0; k < 3; k++)
			{
				await Play(svc, store, divisions[k].Id, logical: true);
				Assert.Equal("done", store.Tournament(divisions[k].Id).State);
				Assert.Equal(accounts.Skip(3 * k).Take(3).OrderBy(x => x), store.TournamentEntries(divisions[k].Id).Select(e => e.Account).OrderBy(x => x));
				var details = JsonDocument.Parse(JsonSerializer.Serialize(await svc.Details(divisions[k].Id, 0, _ => "x"))).RootElement;
				Assert.Equal(names.Skip(3 * k).Take(3), details.GetProperty("standings").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Take(3));
			}
		}

		[Fact]
		public async Task Match_pour_la_troisieme_place_tableau_puis_serie_et_division_impaire()
		{
			if (string.IsNullOrEmpty(Url)) return;
			var cfg = new TournamentConfig
			{
				TeamSize = 1, MinEntries = 2, MaxEntries = 50,
				Phases = new List<PhaseConfig>
				{
					new PhaseConfig { Kind = "table", Mode = "1v1", MaxScore = 3, BestOf = 3, Places = "all", Qualified = 5 },
					new PhaseConfig { Kind = "divisions", Mode = "1v1", MaxScore = 3, Divisions = 4, PoolSize = 3 },
					new PhaseConfig { Kind = "split", Divisions = 8 },
					new PhaseConfig { Kind = "lobby", Mode = "dm", Races = 2, LobbySize = 10, MaxScore = 20, KillPoints = 1 },
				},
			};
			var (svc, store, id) = await Launch(cfg, 7);
			Assert.True(await Play(svc, store, id) > 12);
			Assert.Equal("done", store.Tournament(id).State);
			var divisions = store.TournamentDivisions(store.Tournament(id));
			Assert.Equal(2, divisions.Count);                                        // 5 qualifiés : deux divisions (3 et 2), pas huit
			Assert.Equal(5, divisions.Sum(d => store.TournamentPlayers(d.Id).Count) - 2);      // les deux éliminés du tableau restent dans la première
			foreach (var d in divisions) { await Play(svc, store, d.Id); Assert.Equal("done", store.Tournament(d.Id).State); }
			var flow = JsonDocument.Parse(JsonSerializer.Serialize(await svc.Details(id, 0, _ => "x"))).RootElement.GetProperty("flow");
			Assert.Contains(flow[0].GetProperty("groups").EnumerateArray(), g => g.GetProperty("place").GetInt32() == 3);      // le match pour la 3e place a été joué
			Assert.Contains("-", flow[0].GetProperty("groups")[0].GetProperty("matches")[0].GetProperty("games")[0].GetString());
		}
	}
}
