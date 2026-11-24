using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenEsport.Core
{
	/// <summary>Les modes de jeu. Le battle royale est le seul à avoir son avion, sa zone et ses joueurs à terre ; les autres se jouent sur de petites cartes dédiées, avec réapparition ou manches.</summary>
	public enum GameMode : byte
	{
		BattleRoyale = 0,
		/// <summary>Chacun pour soi : réapparition, le premier à 20 éliminations gagne.</summary>
		Deathmatch = 1,
		/// <summary>Deux équipes (2 à 5 joueurs chacune) : réapparition, la première équipe à 50 éliminations gagne.</summary>
		TeamDeathmatch = 2,
		/// <summary>Deux équipes de 4, 5 ou 10 : on prend le drapeau adverse et on le rapporte au sien ; 3 prises pour gagner.</summary>
		CaptureTheFlag = 3,
		/// <summary>Deux équipes de 4 ou 5, à la Counter-Strike : manches, bombe à poser ou à désamorcer, économie et achats.</summary>
		Sabotage = 4,
		/// <summary>Deux équipes de 5 à 10, cinq relais A à E à tenir, 100 points pour gagner.</summary>
		Control = 5,
		/// <summary>1 VS 1 : deux joueurs, une petite salle, des manches ; même équipement pour les deux (tiré au hasard), ou choisi, ou imposé.</summary>
		Duel = 6,
	}

	public sealed class ModeInfo
	{
		public GameMode Mode;
		/// <summary>Identifiant court (ligne de commande, nom de conteneur).</summary>
		public string Key;
		public string Label, Description;
		/// <summary>Tailles d'équipe proposées (1 = chacun pour soi).</summary>
		public int[] TeamSizes;
		/// <summary>Deux équipes (bleue = 0, rouge = 1) ; sinon chacun a sa propre équipe (battle royale, deathmatch).</summary>
		public bool TwoTeams;
		/// <summary>Fichier de carte exporté (dans assets/maps) et scène Godot correspondante.</summary>
		public string MapFile, MapScene;
		/// <summary>Joueurs maximum d'une partie pour une taille d'équipe (humains + bots).</summary>
		public Func<int, int> PlayersFor;
		/// <summary>Réapparition (s) avant de revenir en jeu ; 0 = pas de réapparition dans la manche.</summary>
		public float RespawnSeconds;
	}

	public static class Modes
	{
		public static readonly ModeInfo[] All =
		{
			new ModeInfo { Mode = GameMode.BattleRoyale, Key = "br", Label = "Battle Royale", Description = "Largage en avion, butin, zone qui se referme : le dernier en vie gagne.",
				TeamSizes = new[] { 1, 2, 3, 4, 5 }, TwoTeams = false, MapFile = "frontiere01.world", MapScene = "res://scenes/maps/Frontiere01.tscn", PlayersFor = s => 30, RespawnSeconds = 0f },
			new ModeInfo { Mode = GameMode.Deathmatch, Key = "dm", Label = "Deathmatch", Description = "Chacun pour soi, réapparition : le premier à 20 éliminations gagne.",
				TeamSizes = new[] { 1 }, TwoTeams = false, MapFile = "relais.world", MapScene = "res://scenes/maps/Relais.tscn", PlayersFor = s => 10, RespawnSeconds = 3f },
			new ModeInfo { Mode = GameMode.TeamDeathmatch, Key = "tdm", Label = "Team Deathmatch", Description = "Deux équipes, réapparition : la première à 50 éliminations gagne.",
				TeamSizes = new[] { 2, 3, 4, 5 }, TwoTeams = true, MapFile = "relais.world", MapScene = "res://scenes/maps/Relais.tscn", PlayersFor = s => 2 * s, RespawnSeconds = 3f },
			new ModeInfo { Mode = GameMode.CaptureTheFlag, Key = "ctf", Label = "Capture du drapeau", Description = "Prends le drapeau adverse, rapporte-le au tien : 3 prises pour gagner.",
				TeamSizes = new[] { 4, 5, 10 }, TwoTeams = true, MapFile = "drapeau.world", MapScene = "res://scenes/maps/Drapeau.tscn", PlayersFor = s => 2 * s, RespawnSeconds = 5f },
			new ModeInfo { Mode = GameMode.Sabotage, Key = "sab", Label = "Sabotage", Description = "Attaquants contre défenseurs : poser ou désamorcer la bombe, manches et économie.",
				TeamSizes = new[] { 4, 5 }, TwoTeams = true, MapFile = "sabotage.world", MapScene = "res://scenes/maps/Sabotage.tscn", PlayersFor = s => 2 * s, RespawnSeconds = 0f },
			new ModeInfo { Mode = GameMode.Control, Key = "ctl", Label = "Contrôle", Description = "Tiens les points de contrôle : 100 points pour gagner.",
				TeamSizes = new[] { 5, 6, 7, 8, 9, 10 }, TwoTeams = true, MapFile = "controle.world", MapScene = "res://scenes/maps/Controle.tscn", PlayersFor = s => 2 * s, RespawnSeconds = 4f },
			new ModeInfo { Mode = GameMode.Duel, Key = "1v1", Label = "1 VS 1", Description = "Un contre un, en manches : le même équipement tiré au hasard pour les deux à chaque manche. Le premier à 5 manches gagne.",
				TeamSizes = new[] { 1 }, TwoTeams = true, MapFile = "duel.world", MapScene = "res://scenes/maps/Duel.tscn", PlayersFor = s => 2, RespawnSeconds = 0f },
		};

		public static bool UsesArenaRules(GameMode mode) => mode == GameMode.Deathmatch || mode == GameMode.TeamDeathmatch;

		/// <summary>Modes où l'on peut rejoindre une partie déjà commencée (on y réapparaît de toute façon) : Deathmatch, Team Deathmatch, Capture du drapeau.</summary>
		public static bool JoinInProgress(GameMode mode) => mode == GameMode.Deathmatch || mode == GameMode.TeamDeathmatch || mode == GameMode.CaptureTheFlag;

		public static ModeInfo Get(GameMode mode) => All.First(m => m.Mode == mode);

		public static bool TryParse(string key, out GameMode mode)
		{
			var m = All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
			mode = m?.Mode ?? GameMode.BattleRoyale;
			return m != null;
		}

		/// <summary>Taille d'équipe valide la plus proche pour ce mode.</summary>
		public static int ValidTeamSize(GameMode mode, int teamSize)
		{
			var sizes = Get(mode).TeamSizes;
			return sizes.Contains(teamSize) ? teamSize : sizes.OrderBy(s => Math.Abs(s - teamSize)).First();
		}

		/// <summary>Nom du mode et de la taille d'équipe : sert à nommer les conteneurs (jamais de nom anonyme).</summary>
		public static string Name(GameMode mode, int teamSize)
			=> mode == GameMode.BattleRoyale ? Matchmaking.ModeName(teamSize) : mode == GameMode.Deathmatch ? "dm" : mode == GameMode.Duel ? "1v1" : $"{Get(mode).Key}-{teamSize}";

		public static string Label(GameMode mode, int teamSize)
			=> mode == GameMode.BattleRoyale ? Get(mode).Label + " — " + Matchmaking.ModeLabel(teamSize)
			: mode == GameMode.Deathmatch || mode == GameMode.Duel ? Get(mode).Label : $"{Get(mode).Label} — {teamSize} contre {teamSize}";
	}
}
