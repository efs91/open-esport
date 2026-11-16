using System;
using System.Net;
using System.Text;
using LiteNetLib;

namespace OpenEsport.Core
{
	/// <summary>
	/// Protocole entre le jeu et le service de matchmaking (toujours allumé, local ou en ligne). Messages UDP « hors connexion » en texte :
	/// demande <c>MM?|version|taille d'équipe</c> ; réponses <c>MM!OK|hôte|port|humains|requis|taille d'équipe</c>, <c>MM!WAIT|message</c>,
	/// <c>MM!ERR|message</c>. Le jeu répète sa demande chaque seconde jusqu'à recevoir OK : le service lance au besoin un serveur de partie
	/// (dans un conteneur Docker) et répond WAIT tant qu'il n'est pas prêt.
	/// </summary>
	public static class Matchmaking
	{
		public const int DefaultPort = 27000;
		public const string RequestTag = "MM?";
		public const string ReplyTag = "MM!";

		/// <summary>Nom du mode de jeu pour une taille d'équipe : sert aussi à nommer les conteneurs (jamais de nom anonyme).</summary>
		public static string ModeName(int teamSize) => teamSize switch { 1 => "solo", 2 => "duo", 3 => "trio", 4 => "quad", _ => "quint" };

		public static string ModeLabel(int teamSize) => teamSize switch { 1 => "Solo", 2 => "Équipe de 2", 3 => "Équipe de 3", 4 => "Équipe de 4", _ => "Équipe de 5" };

		public static string BuildRequest(int teamSize) => BuildRequest(GameMode.BattleRoyale, teamSize);

		/// <summary>Demande de partie : <c>MM?|version|taille d'équipe|mode</c> (le mode est l'identifiant court : br, dm, tdm, ctf, sab, ctl).</summary>
		public static string BuildRequest(GameMode mode, int teamSize) => $"{RequestTag}|{Protocol.Version}|{teamSize}|{Modes.Get(mode).Key}";
	}

	public enum MatchmakingState { Idle, Searching, Found, Failed }

	/// <summary>Côté jeu : demande une partie au service de matchmaking et attend l'adresse du serveur à rejoindre. À faire tourner avec <see cref="Poll"/> à chaque image.</summary>
	public sealed class MatchmakerClient : IDisposable
	{
		private readonly EventBasedNetListener _listener = new EventBasedNetListener();
		private readonly NetManager _net;
		private IPEndPoint _target;
		private int _teamSize;
		private GameMode _mode = GameMode.BattleRoyale;
		private double _nextSend, _startedAt;
		private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

		public MatchmakingState State { get; private set; } = MatchmakingState.Idle;
		/// <summary>Serveur à rejoindre (quand <see cref="State"/> = Found).</summary>
		public string Host { get; private set; }
		public int ServerPort { get; private set; }
		/// <summary>Dernier message du service, à afficher au joueur.</summary>
		public string Message { get; private set; } = "";
		public int Humans, Needed;
		/// <summary>Abandon si aucune réponse utile au bout de ce délai (s).</summary>
		public double TimeoutSeconds = 120;

		public MatchmakerClient()
		{
			_net = new NetManager(_listener) { UnconnectedMessagesEnabled = true, UnsyncedEvents = false };
			_listener.NetworkReceiveUnconnectedEvent += OnReply;
		}

		public void Start(string host, int port, int teamSize) => Start(host, port, GameMode.BattleRoyale, teamSize);

		public void Start(string host, int port, GameMode mode, int teamSize)
		{
			_mode = mode;
			_teamSize = teamSize;
			try { _target = NetUtils.MakeEndPoint(host, port); }
			catch (Exception e) { State = MatchmakingState.Failed; Message = "Adresse du matchmaking invalide : " + e.Message; return; }
			if (!_net.IsRunning) _net.Start();
			State = MatchmakingState.Searching;
			Message = "Recherche d'une partie…";
			_startedAt = _clock.Elapsed.TotalSeconds;
			_nextSend = 0;
		}

		public void Cancel() { State = MatchmakingState.Idle; }

		public void Poll()
		{
			_net.PollEvents();
			if (State != MatchmakingState.Searching) return;
			double now = _clock.Elapsed.TotalSeconds;
			if (now - _startedAt > TimeoutSeconds) { State = MatchmakingState.Failed; Message = "Le service de matchmaking ne répond pas (ou aucun serveur n'a pu démarrer)."; return; }
			if (now >= _nextSend)
			{
				_nextSend = now + 1.0;
				var bytes = Encoding.UTF8.GetBytes(Matchmaking.BuildRequest(_mode, _teamSize));
				_net.SendUnconnectedMessage(bytes, 0, bytes.Length, _target);
			}
		}

		private void OnReply(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
		{
			if (State != MatchmakingState.Searching) return;
			string text;
			try { text = Encoding.UTF8.GetString(reader.RawData, reader.UserDataOffset, reader.UserDataSize); }
			catch (Exception) { return; }
			if (!text.StartsWith(Matchmaking.ReplyTag, StringComparison.Ordinal)) return;
			var parts = text.Substring(Matchmaking.ReplyTag.Length).Split('|');
			switch (parts[0])
			{
				case "OK" when parts.Length >= 6:
					Host = parts[1];
					ServerPort = int.Parse(parts[2]);
					int.TryParse(parts[3], out Humans); int.TryParse(parts[4], out Needed);
					Message = "Partie trouvée";
					State = MatchmakingState.Found;
					break;
				case "WAIT": Message = parts.Length > 1 ? parts[1] : "En attente…"; break;
				case "ERR": Message = parts.Length > 1 ? parts[1] : "Erreur du matchmaking"; State = MatchmakingState.Failed; break;
			}
		}

		public void Dispose() { _net.Stop(); }
	}
}
