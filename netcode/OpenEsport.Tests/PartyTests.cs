using OpenEsport.Accounts;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class PartyTests
	{
		[Fact]
		public void Invitation_acceptee_cree_un_groupe_avec_chef()
		{
			var b = new PartyBook();
			Assert.Null(b.Invite(1, 2));
			Assert.Equal(1, b.View(2).Invites.Count);
			Assert.Null(b.Accept(2, b.View(2).Invites[0].partyId));
			var v = b.View(1);
			Assert.Equal(new long[] { 1, 2 }, v.Party.Members);
			Assert.Equal(1, v.Party.Leader);
			Assert.Equal(b.PartyOf(1), b.PartyOf(2));
		}

		[Fact]
		public void Seul_le_chef_invite_et_le_groupe_est_limite_a_5()
		{
			var b = new PartyBook();
			for (long i = 2; i <= 5; i++) { b.Invite(1, i); b.Accept(i, b.PartyOf(1)); }
			Assert.Equal("Le groupe est complet.", b.Invite(1, 6));
			Assert.NotNull(b.Invite(2, 6));
		}

		[Fact]
		public void Le_chef_qui_part_passe_la_main_et_le_groupe_vide_disparait()
		{
			var b = new PartyBook();
			b.Invite(1, 2); b.Accept(2, b.PartyOf(1));
			b.Leave(1);
			Assert.Equal(0, b.PartyOf(2));                // un joueur seul n'est plus « en groupe »
			b.Invite(3, 4); b.Accept(4, b.PartyOf(3)); b.Invite(3, 5); b.Accept(5, b.PartyOf(3));
			b.Leave(3);
			Assert.Equal(4, b.View(4).Party.Leader);
		}

		[Fact]
		public void Le_chef_publie_la_partie_pour_les_autres()
		{
			var b = new PartyBook();
			b.Invite(1, 2); b.Accept(2, b.PartyOf(1));
			Assert.NotNull(b.SetMatch(2, "h", 1, "br", 3));
			Assert.Null(b.SetMatch(1, "h", 27100, "br", 3));
			Assert.Equal(27100, b.View(2).Party.Match.Port);
		}

		[Fact]
		public void Equipe_reservee_au_groupe_les_solos_n_y_entrent_pas_mais_les_bots_oui()
		{
			var t = new TeamRoster(3);
			int party = t.JoinReserved();
			int solo = t.Join();
			Assert.NotEqual(party, solo);
			Assert.True(t.JoinTeam(party));
			Assert.Equal(party, t.Join(allowReserved: true));       // un bot complète l'équipe du groupe
			Assert.False(t.JoinTeam(party));                        // pleine
		}
	}
}

namespace OpenEsport.Tests
{
	public class FriendTests : System.IDisposable
	{
		private readonly string _db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oe-friends-" + System.Guid.NewGuid().ToString("N") + ".db");
		public void Dispose() { try { System.IO.File.Delete(_db); } catch { } }

		private static long Make(OpenEsport.Accounts.AccountStore s, string login) => s.CreateAccount(login, new byte[] { 1 }, new byte[] { 1 }, 1);

		[Fact]
		public void Demande_puis_acceptation_rend_amis_mutuels()
		{
			var s = new OpenEsport.Accounts.AccountStore(_db);
			long a = Make(s, "alice"), b = Make(s, "bob");
			Assert.Null(s.AddFriend(a, b));
			Assert.Equal("sent", s.Friends(a)[0].state);
			Assert.Equal("received", s.Friends(b)[0].state);
			Assert.True(s.AcceptFriend(b, a));
			Assert.Equal("friend", s.Friends(a)[0].state);
			Assert.Equal("friend", s.Friends(b)[0].state);
		}

		[Fact]
		public void Demandes_croisees_s_acceptent_et_retirer_efface_des_deux_cotes()
		{
			var s = new OpenEsport.Accounts.AccountStore(_db);
			long a = Make(s, "alice"), b = Make(s, "bob");
			s.AddFriend(a, b); s.AddFriend(b, a);
			Assert.Equal("friend", s.Friends(a)[0].state);
			s.RemoveFriend(b, a);
			Assert.Empty(s.Friends(a));
			Assert.NotNull(s.AddFriend(a, a));
		}
	}
}
