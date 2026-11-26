using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Accounts
{
	/// <summary>Une partie de tournoi en cours sur un serveur, telle que le matchmaking la décrit (toutes les quelques secondes).</summary>
	public sealed class LiveGame
	{
		public long Tournament { get; set; }
		/// <summary>Manche et lobby d'une série ; clé du match (poules, tableau), vide pour une série.</summary>
		public int Race { get; set; }
		public int Lobby { get; set; }
		public string Match { get; set; } = "";
		public string Mode { get; set; } = "";
		/// <summary>Adresse du serveur, donnée aux seuls observateurs.</summary>
		public string Host { get; set; } = "";
		public int Port { get; set; }
		/// <summary>0 : salle d'attente, 1 : en cours, 2 : terminée.</summary>
		public int Phase { get; set; }
		public int Humans { get; set; }
		public int TimeLeft { get; set; }
		public int Score0 { get; set; }
		public int Score1 { get; set; }
	}

	public sealed class LiveReport { public List<LiveGame> Games { get; set; } = new List<LiveGame>(); }

	/// <summary>Un message du tchat d'un tournoi.</summary>
	public sealed record ChatLine(long Id, long At, string Login, string Text);

	/// <summary>
	/// Le lobby d'un tournoi, côté service : tchat (en mémoire, les 200 derniers messages), joueurs présents (ceux dont le jeu affiche le lobby)
	/// et parties en cours (rapportées par le matchmaking). Rien de tout cela n'a besoin de survivre à un redémarrage du service.
	/// </summary>
	public sealed class TournamentLobby
	{
		public const int MaxLines = 200, MaxText = 200;
		/// <summary>Un joueur est « présent » s'il a interrogé le lobby dans les dernières secondes (le jeu le fait toutes les 3 s).</summary>
		public const double PresentSeconds = 12;
		/// <summary>Une partie que le matchmaking ne rapporte plus est oubliée au bout de ce délai.</summary>
		public const double LiveSeconds = 15;

		private sealed class Room
		{
			public readonly List<ChatLine> Lines = new List<ChatLine>();
			public readonly ConcurrentDictionary<long, DateTime> Seen = new ConcurrentDictionary<long, DateTime>();
			public readonly ConcurrentDictionary<long, DateTime> LastPost = new ConcurrentDictionary<long, DateTime>();
		}

		private readonly ConcurrentDictionary<long, Room> _rooms = new ConcurrentDictionary<long, Room>();
		private long _nextLine;
		private volatile List<(DateTime at, LiveGame game)> _live = new List<(DateTime, LiveGame)>();

		private Room RoomOf(long tournament) => _rooms.GetOrAdd(tournament, _ => new Room());

		/// <summary>Le joueur regarde le lobby : il compte parmi les présents.</summary>
		public void Seen(long tournament, long account) { if (account != 0) RoomOf(tournament).Seen[account] = DateTime.UtcNow; }

		public List<long> Present(long tournament)
		{
			if (!_rooms.TryGetValue(tournament, out var room)) return new List<long>();
			var limit = DateTime.UtcNow.AddSeconds(-PresentSeconds);
			foreach (var kv in room.Seen) if (kv.Value < limit) room.Seen.TryRemove(kv.Key, out _);
			return room.Seen.Keys.ToList();
		}

		/// <summary>Ajoute un message ; retourne une erreur en français, ou null.</summary>
		public string Post(long tournament, long account, string login, string text)
		{
			text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
			if (text.Length == 0) return "Message vide.";
			if (text.Length > MaxText) text = text.Substring(0, MaxText);
			var room = RoomOf(tournament);
			var now = DateTime.UtcNow;
			if (room.LastPost.TryGetValue(account, out var last) && (now - last).TotalSeconds < 1.5) return "Doucement : un message toutes les deux secondes au plus.";
			room.LastPost[account] = now;
			lock (room.Lines)
			{
				room.Lines.Add(new ChatLine(System.Threading.Interlocked.Increment(ref _nextLine), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), login, text));
				if (room.Lines.Count > MaxLines) room.Lines.RemoveRange(0, room.Lines.Count - MaxLines);
			}
			Seen(tournament, account);
			return null;
		}

		/// <summary>Les messages après <paramref name="since"/> (0 : les derniers).</summary>
		public List<ChatLine> Lines(long tournament, long since)
		{
			if (!_rooms.TryGetValue(tournament, out var room)) return new List<ChatLine>();
			lock (room.Lines) return room.Lines.Where(l => l.Id > since).ToList();
		}

		/// <summary>Le matchmaking rapporte toutes les parties de tournoi qu'il fait tourner : la liste remplace la précédente.</summary>
		public void ReportLive(IEnumerable<LiveGame> games)
		{
			var now = DateTime.UtcNow;
			_live = (games ?? Enumerable.Empty<LiveGame>()).Where(g => g != null && g.Tournament > 0).Select(g => (now, g)).ToList();
		}

		public List<LiveGame> Live(long tournament)
		{
			var limit = DateTime.UtcNow.AddSeconds(-LiveSeconds);
			return _live.Where(x => x.at >= limit && x.game.Tournament == tournament).Select(x => x.game).ToList();
		}
	}
}
