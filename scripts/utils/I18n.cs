using System;
using Openesport.Managers;

namespace Openesport.Utils
{
	/// <summary>
	/// Traduction des textes du jeu. Le texte français écrit dans le code sert de clé : <c>I18n.T("Choisis ton personnage")</c> rend la traduction dans la langue choisie
	/// (fichiers localization/*.lang), ou le texte français lui-même s'il n'y en a pas. Les textes à valeurs s'écrivent avec des positions :
	/// <c>I18n.T("{0} partie(s) jouée(s)", n)</c>, et chaque langue place la valeur où sa grammaire le demande.
	/// </summary>
	public static class I18n
	{
		public static string T(string french) => LocalizationManager.Translate(french);

		public static string T(string french, params object[] args)
		{
			string text = LocalizationManager.Translate(french);
			try { return string.Format(text, args); }
			catch (FormatException) { return string.Format(french, args); }      // traduction mal formée : on retombe sur le français
		}
	}
}
