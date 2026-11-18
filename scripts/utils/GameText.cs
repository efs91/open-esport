using OpenEsport.Core;

namespace Openesport.Utils
{
	/// <summary>Textes de jeu traduits : noms et descriptions des modes, tailles d'équipe. (Les libellés d'origine sont en français dans le noyau du jeu, partagé avec le serveur.)</summary>
	public static class GameText
	{
		public static string Mode(GameMode mode) => I18n.T(Modes.Get(mode).Label);
		public static string Description(GameMode mode) => I18n.T(Modes.Get(mode).Description);

		/// <summary>« Solo » ou « Équipe de 4 ».</summary>
		public static string Team(int size) => size == 1 ? I18n.T("Solo") : I18n.T("Équipe de {0}", size);

		/// <summary>Le mode avec sa taille d'équipe : « Battle Royale — Équipe de 4 », « Sabotage — 5 contre 5 »…</summary>
		public static string Label(GameMode mode, int teamSize)
			=> mode == GameMode.BattleRoyale ? Mode(mode) + " — " + Team(teamSize)
			: mode == GameMode.Deathmatch ? Mode(mode)
			: Mode(mode) + " — " + I18n.T("{0} contre {0}", teamSize);
	}
}
