# Réseau d'open-esport : serveur dédié, 2 à 100 joueurs, équipes de 1 à 5

Un serveur autoritaire en .NET, sans moteur de jeu ni carte graphique, qui tourne dans Docker. Le client Godot n'envoie que les commandes du joueur
et affiche ce que le serveur décide. Ce dossier est ignoré par Godot (`.gdignore`) et exclu de sa compilation ; le jeu n'utilise que `OpenEsport.Core`.

## Dossiers

| Dossier | Rôle |
|---|---|
| `OpenEsport.Core` | Code **partagé** par le serveur et le client : déplacement, tirs, inventaire, équipes, protocole, client réseau (prédiction, interpolation). Aucune dépendance à Godot. |
| `OpenEsport.Server` | Le serveur : boucle à pas fixe, réseau (LiteNetLib), règles de jeu, bots. |
| `OpenEsport.LoadTest` | Simule jusqu'à 100 joueurs contre un serveur réel et mesure. |
| `OpenEsport.Tests` | 55 tests : unitaires, réplication avec pertes de paquets, intégration (vrai serveur, vrais clients en UDP). |
| `Dockerfile` | Image Linux du serveur. |

**Apparence.** Le joueur choisit son personnage dans un panneau (morphologie, peau, tenue, cheveux) avant la partie ; 10 octets sont envoyés à la connexion, le serveur les relaie dans la liste des joueurs (les bots en reçoivent au hasard), chaque client reconstruit le personnage. Le kit vient de MakeHuman : `tools/blender/build_character_kit.py`.

Côté Godot : `scripts/online/` (partie en ligne, autres joueurs, interface), `scripts/modules/character/` (corps entier, bras FPS), `tools/` (export de carte, animations).

## Commandes

```
dotnet test netcode/OpenEsport.Tests                       # tous les tests (environ 1 minute)

# carte exportée depuis Godot (relief de Terrain3D) : lue par le serveur ET par le client, pour des collisions identiques
Godot --headless --path . res://tools/export_world.tscn -- --map=res://scenes/maps/Frontiere01.tscn --out=res://assets/maps/frontiere01.world

# serveur local
dotnet run -c Release --project netcode/OpenEsport.Server -- --world assets/maps/frontiere01.world --players 100 --team-size 3 --fill-bots

# serveur dans Docker (sans carte graphique), limité à 2 cœurs
docker build -t open-esport-server netcode
docker run --rm --cpus=2 -p 27015:27015/udp -v <dossier du .world>:/data open-esport-server --world /data/frontiere01.world --fill-bots

# jeu : menu « Multijoueur », ou en ligne de commande
Godot --path . -- --server=127.0.0.1:27015 --name=MonNom       # l'option s'applique au bouton Multijoueur

# test de charge : 100 robots
dotnet run -c Release --project netcode/OpenEsport.LoadTest -- --world <fichier.world> --clients 100 --seconds 60
```

Options du serveur : `--help`. Bots : `--max-bots N` (0 = aucun) et `--bot-level easy|normal|hard|expert` (ou 0 à 1). Les principales : `--players` (2 à 100), `--team-size` (1 à 5), `--tick`, `--snap-div`, `--fill-bots`, `--bot-target`, `--bot-skill`, `--radius`, `--respawn`.

## Réglages du jeu (vitesses, armes, balles…)

Tout est dans un seul fichier, [balance.json](balance.json) : vitesse de déplacement et de sprint, accélération, saut, glissade, dégâts, cadence,
chargeur, portée, dispersion et rechargement de chaque arme (poings compris), vitesse et chute des balles, multiplicateurs de dégâts par zone,
probabilités de contenu des coffres et précision des bots par niveau. Chaque valeur est précédée d'un commentaire (`//`) avec son unité et sa valeur par défaut.

- Le serveur lit `balance.json` au démarrage (dossier courant, puis dossier du programme ; il est créé avec les valeurs par défaut s'il manque).
  Autre fichier : `--balance MON_FICHIER.json`. Les valeurs par défaut seules : `--write-balance fichier.json`.
- **Le serveur envoie ses valeurs à chaque client à la connexion** : tout le monde simule pareil (sinon la prédiction du déplacement se désynchroniserait).
  Il suffit donc de modifier le fichier côté serveur puis de le relancer : les clients n'ont rien à changer.
- Une ligne supprimée reprend sa valeur par défaut. Un nom inconnu ou une valeur invalide est signalé au démarrage.
- Pour ajouter un réglage : une ligne dans `GameBalance` (`Define(...)`), puis lire sa valeur avec `GameBalance.Get("nom")`.

**Niveaux d'équipement** (clés `tier.*`) : classique (blanc), rare (bleu), épique (violet), légendaire (rouge). Chaque arme tirée au sol ou dans un coffre reçoit un
niveau au hasard (poids 55 / 28 / 13 / 4 par défaut) ; ce niveau multiplie ses dégâts, la taille de son chargeur, sa durée de rechargement et sa dispersion
(légendaire : +25 % de dégâts, chargeur +35 %, rechargement 22 % plus rapide, dispersion -30 %). Le niveau suit l'arme quand on la ramasse ou qu'on la lâche.
Les sacs à dos ont un niveau fixe : le sac niveau 2 est rare, le niveau 3 épique. Côté jeu, le niveau est montré par un halo coloré au sol (disque, plus un faisceau
vertical dès le niveau rare), la couleur du nom dans l'invite « Ramasser » et la couleur des cartes d'armes dans l'inventaire.

Les valeurs de déplacement par défaut sont proches d'Apex Legends : 4 m/s en déplacement normal, 6,8 m/s en sprint (Apex : environ 3,8 / 6,5 à 7,4 m/s).

## Comment ça marche

**Le serveur décide.** Il simule tous les joueurs à 60 Hz avec le même code de déplacement que le client (`PlayerMovement`). Positions, tirs, dégâts, butin, inventaires et équipes sont à lui.

**Le client prédit.** Il applique ses propres commandes tout de suite, puis compare avec l'état renvoyé par le serveur (`GameClient.Reconcile`) : il repart de l'état du serveur et rejoue les commandes que celui-ci n'a pas encore vues. Comme le code et les commandes (arrondies au format du réseau) sont identiques, la correction est nulle en régime normal : 0 divergence mesurée avec 100 joueurs qui courent, et environ 0,1 par minute et par joueur avec des combats intenses.

**Les autres joueurs sont affichés légèrement dans le passé** (5 ticks par défaut, soit 83 ms), interpolés entre deux snapshots reçus.

**Compensation de latence.** Chaque commande de tir porte le tick que le joueur voyait à l'écran. Le serveur remet les cibles à leur place de ce tick (historique de 1 seconde, 250 ms maximum) avant de juger le tir. Test : sur une cible qui court, la précision passe d'environ 36 % à plus de 60 % avec la compensation.

**Réplication par cases (comme la Replication Graph de Fortnite).** La carte est découpée en cases de 64 m. Chaque client reçoit les joueurs de son voisinage (400 m), plus ses coéquipiers où qu'ils soient. La fréquence baisse avec la distance : chaque snapshot en dessous de 64 m, un sur deux jusqu'à 192 m, un sur quatre au-delà. Le butin n'est envoyé qu'à moins de 100 m.

**Delta par acquittement.** Le serveur compare chaque entité à la dernière version que le client a confirmée (champ de bits des 32 derniers snapshots), pas à la dernière envoyée : un paquet perdu est renvoyé tout seul, et un monde immobile ne coûte presque rien. Un budget de 960 octets par paquet (limite de LiteNetLib : 1020), des priorités qui montent avec l'attente (pas de famine des joueurs éloignés), et jusqu'à 3 paquets par snapshot quand beaucoup d'entités ont bougé.

**Bots.** Les parties non pleines sont complétées par des bots côté serveur (`--fill-bots`). Ils passent par la même simulation que les humains. Un humain qui arrive prend la place d'un bot (de préférence dans une équipe qui compte déjà des humains).

## Mesures

100 joueurs rassemblés dans un rayon de 150 m (le pire cas : tout le monde se voit et se bat), 100 robots qui courent, sautent, glissent, ramassent et tirent, carte de 1 × 1 km. Le budget d'un tick à 60 Hz est de 16,7 ms.

| Conteneur | Tick p99 | Tick max | Sortie serveur | Par joueur | Divergences de prédiction |
|---|---|---|---|---|---|
| Docker, 2 cœurs | 4,6 à 6,3 ms | pics de 20 à 40 ms, rares | 1,9 Mo/s (15 Mbit/s) | 22 Ko/s, 40 snapshots/s | 0,06 /min/joueur |
| Docker, 1 cœur | 9,8 ms (pire 13) | pics de 40 ms | 1,9 Mo/s | 22 Ko/s | 0,36 /min/joueur |
| 100 bots serveur, sans client, 1 cœur | 1,3 à 1,7 ms | 7 ms | — | — | — |

Des robots peureux (sans combat) : tick p99 1,7 ms, aucune divergence. Le défaut le plus visible est le pire tick (quelques dépassements sur une minute) : à surveiller avant une compétition.

## Limites connues

- **Une partie par processus.** Plusieurs parties sur une machine = plusieurs conteneurs.
- **Tirs instantanés** (pas de balle qui voyage ni de chute). Le serveur juge un rayon contre des cylindres par posture (jambes, torse, tête), sans squelette.
- **La carte exportée ne contient que le relief.** Les rochers, bâtiments et autres décors du jeu ne bloquent ni le déplacement ni les tirs côté serveur tant qu'ils ne sont pas exportés (le format sait déjà décrire des boîtes).
- **Pas de jeu de zone** (cercle qui se réduit), pas de matchmaking, pas de salon : un serveur = une partie qui tourne.
- **Mesures faites sur la machine locale** (robots et serveur sur le même ordinateur, latence quasi nulle). Les corrections et la compensation sont testées avec des pertes de paquets simulées en mémoire, pas sur un vrai réseau.
- **Déterminisme entre systèmes.** Windows et Linux calculent `sin` et `cos` avec des bibliothèques différentes : un client Windows et un serveur Linux peuvent différer d'une unité du dernier chiffre. C'est bien en dessous du seuil de correction (2 mm), mais à surveiller.
- **Protection contre la triche limitée à l'autorité du serveur** : cadence de commandes bornée, paquets invalides ignorés, tirs et déplacements décidés par le serveur. Pas d'anti-triche logiciel.
- **Animations du personnage** : pas d'accroupi (on écrase légèrement le modèle) et le buste ne suit pas encore le regard.

## Matchmaking et parties à la demande

Le service `OpenEsport.Matchmaker` tourne en permanence (sur ce poste aujourd'hui, en ligne demain). Quand un joueur choisit un mode (solo, équipe de 2, 3, 4 ou 5)
et clique sur « Jouer en ligne », le jeu lui envoie une demande (UDP, port 27000 par défaut) :

1. s'il existe déjà une salle d'attente de ce mode avec de la place, le joueur y est envoyé ;
2. sinon le service **lance un serveur de partie dans un nouveau conteneur Docker** et répond « WAIT » jusqu'à ce qu'il soit prêt.

Chaque conteneur a un **nom explicite** : `open-esport-<mode>-<port>` (`open-esport-duo-27101`…), avec les étiquettes `open-esport=1` et
`open-esport.mode=…` ; jamais de nom aléatoire de Docker. Le port UDP du serveur est publié à l'identique sur la machine hôte.
Le serveur de partie lance la partie dès qu'il y a assez de joueurs (1 pour l'instant), complète avec des bots, s'arrête une fois la partie terminée
(`--exit-when-over`, le conteneur disparaît avec `--rm`) ou si sa salle d'attente reste vide 2 minutes.

```bash
# construire l'image du serveur (une fois)
docker build -t open-esport-server:latest -f Dockerfile .

# lancer le service de matchmaking
dotnet run -c Release --project OpenEsport.Matchmaker -- --world ../assets/maps/frontiere01.world --public-host 127.0.0.1 --players 30 --netcode-dir .

# diagnostic : demander une partie en équipe de 2, comme le fait le jeu
dotnet run -c Release --project OpenEsport.Matchmaker -- --ask 2
```

Pour un service en ligne : `--public-host` est le nom ou l'IP vue d'internet de la machine, et les ports UDP du service (27000) et des serveurs
(27100-27199 par défaut, réglable avec `--ports`) doivent être ouverts. Sans Docker (développement) : `--launcher process --server-dll <OpenEsport.Server.dll>`.
`docker ps --filter label=open-esport=1` liste les parties en cours.

## Zones du battle royale

Le générateur de zones (`OpenEsport.Core/Game/Zones.cs`) déroule un réglage (`ZoneConfig`) avec une graine tirée au hasard à chaque partie :
la zone 1 reste seule, puis la zone suivante est **annoncée** (dessinée sur la carte et la minimap, rien ne bouge), puis la zone actuelle **se réduit** jusqu'à elle ;
hors de la zone, des coups de dégâts périodiques. Chaque étape a ses durées, sa taille (en % du rayon de référence) et ses dégâts.

| Préréglage | Principe |
|---|---|
| `apex` (par défaut) | 5 zones de 100 / 50 / 28 / 14 / 6 % puis la zone 5 se réduit jusqu'à ne plus laisser aucune place ; chaque zone dans la précédente ; temps d'attente, durées de réduction et dégâts par tick des cercles d'Apex Legends (saison 18 et suivantes) ; ≈ 19 min. Zone 1 pendant 2 min, annonce de la zone 2, réduction 1 min 30 plus tard. |
| `fortnite` | la zone se déplace, chevauche la précédente, revient parfois sur d'anciens emplacements, puis continue presque indéfiniment (mode cyclique). |

Réglage : `--zones apex|fortnite|fichier.json` (serveur), `--zones` / `--zones-file` (matchmaking, le fichier est monté dans chaque conteneur), `--dump-zones apex` affiche un réglage en JSON.
Dans Godot, l'onglet **Open Esport › Zones** édite ces fichiers (`data/zones/*.json`) avec un aperçu sur la carte.

## Panneau de configuration dans Godot

Plugin d'éditeur `addons/open_esport_config` (onglet « Open Esport » à côté de 2D / 3D / Script) :

- **Réglages du jeu** : généré automatiquement à partir de `GameBalance` (chaque `Define(clé, défaut, unité, description)` du code devient une ligne : déplacement, glissade, balles,
  armes, niveaux d'équipement, boucliers, soins, butin, bots…). « Enregistrer » écrit `balance.json` (et `netcode/balance.json` s'il existe) ; le serveur et les conteneurs le lisent.
  **Ajouter un réglage = une ligne dans `GameBalance`**, il apparaît dans le panneau sans autre travail.
- **Zones** : voir ci-dessus.
- **Objets** : liste des armes, viseurs, sacs, munitions, boucliers et soins avec leur modèle (double-clic pour l'ouvrir) et un lien vers leurs réglages.
  Prochaine étape prévue : les définitions d'objets (modèle, animations, taille au sol, accessoires acceptés) deviennent des ressources `.tres` éditables dans ce panneau.

## Modes de jeu

Menu Multijoueur > page « Mode de jeu » : Battle Royale, Deathmatch, Team Deathmatch, Capture du drapeau, Sabotage, Contrôle. Chaque mode a sa taille d'équipe et sa carte
(`assets/maps/relais.world` pour les deux modes Deathmatch, `drapeau.world`, `sabotage.world`, `controle.world`, et `frontiere01.world` pour le battle royale ; à copier toutes dans `MAPS_DIR` sur le serveur). Le serveur se lance avec `--mode br|dm|tdm|ctf|sab|ctl`
(le matchmaking le fait : demande `MM?|version|taille|mode`, conteneur `open-esport-<mode>-<taille>-<port>`). Un seul humain suffit pour démarrer, des bots complètent les équipes.

| Mode | Équipes | Règles par défaut (réglables : panneau Godot, groupes « Modes de jeu » et « Sabotage ») |
|---|---|---|
| Deathmatch | chacun pour soi, 2 à 20 | 20 éliminations ou 10 min ; réapparition 3 s, 2 s de protection ; règles d'arène (ci-dessous) |
| Team Deathmatch | 2 équipes de 2 à 5 | 50 éliminations d'équipe ou 12 min |
| Capture du drapeau | 2 équipes de 4, 5 ou 10 | 3 prises ou 12 min ; drapeau lâché : retour à la base après 20 s ; porteur ralenti de 10 % |
| Sabotage | 2 équipes de 4 ou 5 | premier à 7 manches, mi-temps (camps échangés, argent remis à zéro) ; achat 15 s, manche 1 min 55, bombe 40 s, pose 3 s, désamorçage 5 s (3 s avec kit) ; économie à la Counter-Strike (touche B : boutique) |
| Contrôle | 2 équipes de 5 à 10 | Cinq relais A à E initialement neutres. Chaque seconde, l’équipe qui tient le plus de relais marque l’écart. La première à 100 points gagne. |

En Contrôle, chaque base dispose d’un hover monoplace et d’un AT-ST à cinq places. E permet d’embarquer puis de sortir. Le hover vole en vue extérieure (saut pour monter, accroupi pour descendre). Dans le marcheur, F1 commande la conduite et la mitrailleuse, F2 le canon, F3 à F5 les trois meurtrières pour les armes personnelles. Maintenir la commande Viser (clic droit par défaut) grossit la vue du pilote ×2 et celle de l’artilleur ×4. La mitrailleuse du marcheur inflige 32 dégâts de base, le canon 140 à l’impact direct avec une explosion. Les trois passagers tirent depuis une cabine blindée à travers leur propre meurtrière. Le serveur attribue les sièges libres, valide les déplacements et les arcs de tir, applique les impacts et réplique les véhicules aux deux équipes. Utiliser le protocole 32 et la même version de `controle.world` des deux côtés.

En Capture du drapeau et en Contrôle, on choisit sa classe avec C. Le Sabotage utilise sa boutique. Les bots jouent les objectifs de chaque mode.

**Règles d'arène du Deathmatch et du Team Deathmatch** (`ArenaRules`, `GameServer.Arena.cs`), sur la carte « Relais » (3 niveaux, tremplins, téléporteurs) :
- départ au pistolet léger P2020 (18 dégâts, 420 coups/min, 10 balles) avec un viseur point rouge et un bouclier épique vide ; pas de classe, pas de grenade ;
- chaque point de butin de la carte est un emplacement fixe. Les armes (épiques, légendaires pour le sniper et la LMG, les plus dures d'accès) ont leur viseur monté d'office (non démontable) et se prennent en passant dessus (sauf une arme qu'on a déjà) : dans un emplacement libre, sinon à la place de l'arme en main, qui disparaît. Une arme revient à sa place 2 s après la mort de son porteur (ou dès qu'il l'a remplacée) ;
- soins, boucliers et munitions se prennent en marchant dessus (seulement s'ils servent), agissent tout de suite et reviennent après 20 s (soins), 30 s (boucliers), 15 s (munitions) ;
- on ne jette rien, rien ne tombe à la mort, les viseurs ne se déplacent pas ;
- la vie (pas le bouclier) remonte seule 5 s après la dernière blessure, 1 PV toutes les 0,3 s ; une élimination rend 50 PV (sans dépasser 100) ;
- tremplins (`JumpPad` : vol en cloche vers un point, sans correction en l'air) et téléporteurs (`Teleporter` : sortie et direction) sont dans le fichier de carte (format 9) et simulés par `PlayerMovement`, donc prédits par le client ;
- les bots utilisent une navigation à plusieurs niveaux (`LayeredNav` : sols superposés, escaliers, sauts de rebord, tremplins et téléporteurs) et vont chercher armes, soins et boucliers.

### Cartes des modes

Cartes simples (un sol, des abris, les points d'attention), scènes dans `scenes/maps/` générées par `python tools/map/gen_mode_maps.py` puis éditables dans Godot :
`CoverBox` (abris), `MapMarker` (apparitions, points de contrôle, bases de drapeau, sites de bombe). Onglet « Carte (export) » du panneau Open Esport : exporte la scène choisie en `.world`
(ou : `Godot --headless res://tools/export_world.tscn -- --map=res://scenes/maps/Arene.tscn --out=res://assets/maps/arene.world`).

Carte « Relais » du Deathmatch : `python tools/map/gen_relais_map.py` (premier jet, refuse d'écraser la scène ; `--check` vérifie les trajectoires des tremplins), puis édition dans Godot
(`ArenaJumpPad` et son enfant « Cible », `ArenaTeleporter` et son enfant « Sortie », points de butin `ObjetButin`) et export :
`Godot --headless res://tools/export_world.tscn -- --map=res://scenes/maps/Relais.tscn --out=res://assets/maps/relais.world`.

## Groupes, amis et tournois (service de comptes)

- **Groupes** (`PartyBook`) : jusqu'à 5 joueurs, un chef, invitations par nom de compte (`/api/party/...`). Le chef lance la partie (matchmaking avec la taille du groupe) et publie le serveur ; les autres membres le rejoignent. Le serveur de partie lit le groupe du joueur à la vérification du jeton (`partyId`, `partySize`) : mêmes équipes, départ retardé (40 s max) tant que le groupe n'est pas au complet ; les bots complètent les équipes de groupe.
- **Amis** (`/api/friends/...`) : demande puis acceptation, présence « en ligne » = a interrogé le service dans les 45 dernières secondes.
- **Tournois** : l'organisation est faite par **ManageComp** (`MANAGECOMP_API_KEY`, `MANAGECOMP_URL` dans le `.env` du serveur). Format actuel : battle royale en N manches (phase « Lobby (série) », points par place, classement cumulé calculé par ManageComp).
  1. un joueur crée le tournoi (`POST /api/tournaments`) : une compétition ManageComp est créée ; les joueurs s'inscrivent (`/join`) ;
  2. l'organisateur le lance (`/start`) : ManageComp répartit les inscrits en lobbys ;
  3. chaque joueur clique « Jouer la manche » : le matchmaking (`MM?…|T<tournoi>:<manche>:<lobby>`) lance un serveur réservé aux inscrits du lobby (`--tournament --race --lobby`), complété par des bots ;
  4. à la fin, le serveur rapporte les places (`/internal/tournament-race`) : elles sont saisies dans ManageComp, qui calcule le classement ; le tournoi avance de manche.
  Les tournois publiés directement dans ManageComp apparaissent aussi dans l'encart du menu (`/api/tournaments`, champ `tournaments`).

### Tournois en duels (1 contre 1 / 2 contre 2)

Format « duel » : poules puis tableau, calculés par ManageComp (formule par défaut). Chaque match est une partie de match à mort par équipes (TDM) sur un serveur réservé aux deux camps, sans bots, jouée jusqu'à N victimes (réglable à la création, 3 à 30, 10 par défaut).
- 2 contre 2 : l'équipe est le **groupe de deux joueurs** ; son chef l'inscrit (un seul participant côté ManageComp, nommé « a & b »).
- « Jouer mon match » : le jeu demande le match en attente du joueur (`/api/tournaments/{id}/me`, clé `p<poule>m<match>` ou `t<jeu>n<noeud>`), puis le matchmaking (`MM?…|TD<tournoi>:<clé>`) lance `--tournament --match <clé>` ; le serveur lit les camps (`/internal/tournament/{id}/match/{clé}`), impose les équipes, rejoue sans bots.
- Un camp absent après 120 s perd par forfait. Une égalité (fin du temps) n'est pas enregistrée : le match reste à jouer.
- À la fin, le score est saisi dans ManageComp (`/internal/tournament-match`) ; quand une phase est terminée, le tournoi passe à la suivante (poules → tableau → classement) puis se termine.

### Battle royale par équipes et classement

- Un tournoi battle royale peut se jouer par équipes de 2 à 5 : l'équipe est le groupe, son chef l'inscrit (un seul participant « a & b » côté ManageComp, toute l'équipe est placée dans le même lobby et dans la même équipe en partie).
- « Classement » dans l'encart du menu : `GET /api/tournaments/{id}` renvoie `standings` (place, nom, détail), tiré du classement cumulé de ManageComp (battle royale) ou du classement final (duels).
