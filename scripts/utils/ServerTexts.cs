namespace Openesport.Utils
{
	/// <summary>
	/// Les textes que le serveur de partie, le matchmaking et le service de comptes envoient tels quels au joueur (refus de connexion, erreurs, états de la recherche).
	/// Le client les traduit à l'affichage avec <see cref="I18n.T(string)"/> ; cette liste n'est pas lue par le jeu, elle sert à ce que chaque texte soit repris dans les fichiers de langue.
	/// Quand on ajoute un message côté serveur, on l'ajoute ici.
	/// </summary>
	public static class ServerTexts
	{
		public static readonly string[] All =
		{
			// serveur de partie : refus de connexion
			"Version du protocole différente",
			"Partie déjà commencée",
			"Serveur plein",
			"Partie terminée",
			"Compte requis : connecte-toi.",
			"Service de comptes injoignable : réessaie dans un instant.",
			"Session invalide : reconnecte-toi.",
			"Ce compte est déjà connecté.",

			// matchmaking
			"Demande invalide",
			"Version du jeu différente de celle du service",
			"Mode de jeu inconnu",
			"Tous les serveurs sont occupés, réessaie dans un instant",
			"Impossible de lancer un serveur de partie : {0}",
			"Adresse du matchmaking invalide : {0}",
			"Recherche d'une partie",
			"Le service de matchmaking ne répond pas (ou aucun serveur n'a pu démarrer).",
			"En attente",
			"Erreur du matchmaking",
			"Préparation d'une partie {0}",
			"Lancement d'une partie {0}",

			// service de comptes
			"Trop d'inscriptions depuis cette adresse. Réessaie plus tard.",
			"Identifiant : 3 à 20 caractères (lettres, chiffres, _ et -).",
			"Cet identifiant est déjà pris.",
			"Trop d'essais. Réessaie dans une minute.",
			"Identifiant ou mot de passe incorrect.",
			"Session expirée : reconnecte-toi.",
			"Personnage invalide (nom de 1 à 24 caractères).",
			"Tu as atteint le nombre maximal de personnages.",
			"Personnage introuvable.",
			"Mode inconnu.",
			"Clé de serveur invalide.",
			"Jeton invalide.",
			"Le mot de passe doit faire au moins 8 caractères.",
			"Le mot de passe est trop long (128 caractères au plus).",
		};
	}
}
