# Open Esport 2025

[🇦🇹 Deutsch](deutsch.md) [🇧🇪 Nederlands](nederlands.md) [🇧🇪 Français](français.md) [🇧🇬 Български](български.md) [🇭🇷 Hrvatski](hrvatski.md) [🇨🇿 Čeština](čeština.md) [🇩🇰 Dansk](dansk.md) [🇪🇪 Eesti](eesti.md) [🇬🇷 Ελληνικά](ελληνικά.md) [🇭🇺 Magyar](magyar.md) [🇮🇪 Gaeilge](gaeilge.md) [🇮🇹 Italiano](italiano.md) [🇱🇻 Latviešu](latviešu.md) [🇱🇹 Lietuvių](lietuvių.md) [🇲🇹 Malti](malti.md) [🇵🇱 Polski](polski.md) [🇵🇹 Português](português.md) [🇷🇴 Română](română.md) [🇸🇰 Slovenčina](slovenčina.md) [🇸🇮 Slovenščina](slovenščina.md) [🇪🇸 Español](español.md) [🇸🇪 Svenska](svenska.md)[🇸🇦 العربية](README-lang/العربية.md)

## Description

**Open Esport 2025** est un projet FPS compétitif open source développé avec le moteur **Godot 4**. Il vise à fournir une base modulaire, extensible et facilement déployable pour créer des expériences esport modernes.

L'objectif principal est d'offrir une infrastructure **auto-hébergée** et **autonome**, indépendante des éditeurs traditionnels, afin de contourner les limitations ou les changements de politique imposés à des fins commerciales.  
Le projet est conçu pour être **compatible avec les environnements de déploiement modernes** (Docker, Kubernetes, Azure, AWS, ...), tout en restant léger et facile à héberger localement.

Cette solution est destinée aux développeurs, aux organisateurs de tournois, aux communautés open source et à tous ceux qui souhaitent créer, gérer et faire évoluer une plateforme esport gratuite, transparente et souveraine.

## État actuel du projet
Le jeu est jouable en ligne avec un serveur dédié qui fait autorité :
- **6 modes de jeu** : Battle Royale (saut d'avion, zone qui se réduit, butin), Deathmatch, Team Deathmatch, Capture du drapeau, Sabotage (manches, économie, boutique, bombe) et Contrôle
- **Jeu en ligne** : de 2 à 100 joueurs, équipes de 2 à 5, bots pour compléter les places, matchmaking qui lance un serveur de jeu Docker par partie
- **Comptes** : inscription/connexion, jusqu'à 12 personnages par compte, classements général et par mode avec points et statistiques (page profil)
- **Combat** : armes inspirées d'Apex Legends, boucliers, soins, grenades, optiques, sacs à dos, système à terre/réanimation en équipe
- **Bots** : abris et tirs en alternance avec mise à couvert, ordres d'équipe par pings (attaque / défense), partage du butin avec les coéquipiers humains
- **Créateur de personnage** et 24 langues, tous les textes sont traduits

## Fonctionnalités
- Système de menus modulaire et scènes flexibles
- Serveur .NET qui fait autorité (LiteNetLib), avec prédiction et interpolation côté client
- Services de matchmaking et de comptes, déployables avec Docker Compose (voir [netcode/README.md](../netcode/README.md))
- Équilibrage réglable : armes, grenades et modes dans `netcode/balance.json`
- Contrôleur de personnage par capacités (marche, course, saut, accroupi, glissade, nage, contrôle en l'air, pentes)
- Journalisation centralisée et suite de tests automatisés du netcode (`netcode/OpenEsport.Tests`)

## Technologies utilisées
- Moteur Godot
- C#
- .NET
- Plugins :
  - [Character Controller](https://github.com/expressobits/character-controller) – Contrôleur de personnage modulaire
  - [AmbientCG](https://github.com/mohsenph69/godot-ambientcg) – Importateur de textures PBR
  - [FPS Hands](https://codeberg.org/Bytez/godot-fps-hands) – Système de mains FPS

## Structure du projet
```
open-esport/
├── scripts/                  # Code source
│   ├── managers/             # Gestionnaires globaux
│   ├── modules/              # Modules fonctionnels
│   ├── structures/           # Structures de données
│   ├── ui/                   # Composants d'interface utilisateur
│   └── utils/                # Utilitaires
├── netcode/                  # Serveur dédié, matchmaker, service de comptes, tests (.NET 8)
├── localization/             # Traductions (un fichier .lang par langue)
├── maps/                     # Données des cartes
├── scenes/                   # Scènes Godot
├── assets/                   # Ressources (images, sons, etc.)
├── configuration/            # Fichiers de configuration
└── addons/                   # Plugins Godot
    ├── character-controller/ # Contrôleur de personnage
    ├── ambientcg/            # Importateur de textures PBR
    └── fps-hands/            # Système de mains FPS
```

## Architecture
Le projet suit une architecture modulaire avec :
- Des gestionnaires pour les fonctionnalités globales
- Un système de communication basé sur les événements
- Une séparation claire des responsabilités
- Une gestion centralisée des logs
- Un système de personnage basé sur des capacités :
  - Marche
  - Course
  - Saut
  - Accroupissement
  - Vol
  - Nage
  - Contrôle aérien
  - Gestion des pentes

## Prérequis
- Moteur Godot
- SDK .NET
- Visual Studio ou VS Code (recommandé)

## Installation
1. Clonez le dépôt :
```bash
git clone https://github.com/efs91/open-esport.git
```
2. Ouvrez le projet dans le moteur Godot
3. Assurez-vous que tous les addons sont correctement installés

## Version
Dernière version (v0.3) pour Windows 64 bits :
- [Télécharger](https://github.com/efs91/open-esport/releases/latest) — dézipper puis lancer `open-esport.exe`

Le client se connecte au serveur public défini dans `data/network.cfg` (`host` / `port`).

## Héberger son propre serveur
Le serveur, le matchmaker et le service de comptes tournent avec Docker Compose depuis le dossier `netcode/`. Voir [netcode/README.md](../netcode/README.md).

## Contribution
Les contributions sont les bienvenues ! Veuillez suivre ces étapes :
1. Forkez le projet
2. Créez une branche pour votre fonctionnalité
3. Committez vos changements
4. Poussez vers votre branche
5. Ouvrez une Pull Request

## Licence
Ce projet est sous licence GNU General Public License v3.0 (GPL-3.0). Voir le fichier [LICENSE](LICENSE) pour plus de détails.

Cette licence garantit que :
- Le code source est librement accessible
- Les modifications doivent être partagées sous la même licence
- Les utilisateurs ont le droit d'utiliser, modifier et distribuer le code
- Les changements doivent être documentés

## Crédits
- [Character Controller](https://github.com/expressobits/character-controller) par Rafael Correa
- [AmbientCG](https://github.com/mohsenph69/godot-ambientcg) par mohsenph69
- [FPS Hands](https://codeberg.org/Bytez/godot-fps-hands) par Bytez

## Support
Pour toute question ou problème, veuillez ouvrir une issue dans le dépôt GitHub. 
