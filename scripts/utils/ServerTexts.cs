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

			// tournois : configuration
			"Le nom du tournoi doit faire entre 3 et 60 caractères.",
			"Le minimum d'inscrits dépasse le maximum.",
			"La date de début est déjà passée.",
			"Un tournoi se programme au plus un an à l'avance.",
			"Les inscriptions doivent fermer après leur ouverture.",
			"Les inscriptions doivent fermer au plus tard au départ.",
			"Les inscriptions doivent ouvrir avant le départ.",
			"Phase {0} : {1}",
			"une séparation en divisions se place après une étape jouée.",
			"une séparation en divisions est suivie d'au moins une étape.",
			"une division compte au moins deux participants.",
			"points par place : au plus {0} valeurs, de 0 à 100.",
			"Tu organises déjà {0} tournois : termine-en un d'abord.",
			"Le tournoi a commencé : sa configuration ne change plus.",
			"Il y a déjà des inscrits : la taille des équipes ne peut plus changer.",
			"Il y a déjà plus d'inscrits que ce maximum.",

			// tournois : inscriptions et équipes
			"Les inscriptions sont closes.",
			"Les inscriptions ne sont pas encore ouvertes.",
			"Le tournoi est complet.",
			"Le tournoi a commencé : les équipes ne changent plus.",
			"Aucun joueur ne porte ce nom.",
			"Tu fais déjà partie de ton équipe.",
			"{0} est déjà dans une équipe de ce tournoi.",
			"Ton équipe est au complet ({0} joueurs, invitations en attente comprises).",
			"Cette invitation n'existe plus.",
			"Tu fais déjà partie d'une équipe de ce tournoi.",
			"Cette équipe n'existe plus.",
			"Cette équipe est déjà au complet.",
			"Le tournoi a commencé : tu ne peux plus te désinscrire.",
			"Le check-in ouvre {0} minutes avant le départ.",
			"Tu n'es pas inscrit à ce tournoi.",

			// tournois : déroulement
			"Le tournoi a commencé.",
			"Le tournoi a déjà commencé.",
			"Le tournoi est déjà terminé.",
			"Annulé par l'organisateur.",
			"Annulé au départ : {0}",
			"Aucun inscrit présent.",
			"Pas assez d'inscrits présents ({0} sur {1} demandés).",
			"{0} équipe(s) incomplète(s) retirée(s).",
			"{0} équipe(s) incomplète(s) retirée(s) au départ.",
			"Pas de manche à jouer pour toi.",
			"ManageComp est injoignable : {0}",
			"{0} pts · lobby {1}",

			// tournois : modèles
			"Le nom d'un modèle doit faire entre 3 et 40 caractères.",
			"Ce modèle n'existe plus.",
			"Tu as déjà {0} modèles : supprimes-en un d'abord.",
		};
	}
}
