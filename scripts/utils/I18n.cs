using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

		private static List<(Regex pattern, string template)> _serverTemplates;

		/// <summary>
		/// Traduit un texte reçu d'un serveur. Ceux qui portent une valeur (« Tu organises déjà 3 tournois… ») sont reconnus grâce à leur modèle
		/// dans <see cref="ServerTexts"/> ; les valeurs qui sont elles-mêmes des textes, et plusieurs phrases à la suite, sont traduites aussi.
		/// </summary>
		public static string Server(string text)
		{
			if (string.IsNullOrEmpty(text)) return text;
			string exact = T(text);
			if (exact != text) return exact;
			_serverTemplates ??= ServerTexts.All.Where(s => s.Contains("{0")).Select(s =>
				(new Regex("^" + Regex.Replace(Regex.Escape(s), @"\\\{\d+\}", s.StartsWith("{0}") ? "([^.]+?)" : "(.+?)") + "$", RegexOptions.Singleline), s)).ToList();
			foreach (var (pattern, template) in _serverTemplates)
			{
				var m = pattern.Match(text);
				if (m.Success) return T(template, m.Groups.Cast<Group>().Skip(1).Select(g => (object)Server(g.Value)).ToArray());
			}
			int cut = text.IndexOf(". ", StringComparison.Ordinal);
			return cut < 0 ? text : Server(text[..(cut + 1)]) + " " + Server(text[(cut + 2)..]);
		}
	}
}
