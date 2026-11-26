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

**Apparence.** L’éditeur MakeHuman propose cinq paramètres généraux et 283 réglages détaillés sur le corps standard adulte (18 à 90 ans). Le protocole 33 transmet les dix octets historiques, puis une valeur signée par réglage pour les apparences détaillées. Dans l’octet partagé avec les sourcils, les bits 6–7 conservent les quatre premiers choix de couvre-chef et les bits 2–5 étendent le catalogue. La valeur zéro correspond à la tête nue. Le serveur relaie l’apparence choisie dans le roster fiable. La taille règle la vitesse à pied entre 97 % et 103 %. Les dégâts directs des balles dépendent de la taille et de la vitesse horizontale observée par le serveur, dans une plage de 100 à 104 %. Les volumes de collision et de touche restent communs. Le serveur peut atténuer ces effets dans `balance.json` avec `movement.bodyBalance` (1 par défaut, 0 pour un gabarit entièrement cosmétique).

Le modèle de base provient de `tools/blender/build_character_kit.py`. Pour régénérer le catalogue et les déplacements MakeHuman avec NumPy, SciPy et Godot .NET, lancer `python tools/blender/build_character_details.py <dossier-data-de-MakeHuman> <exécutable-Godot> <rig.mixamo.json-de-MPFB>`. L’ordre des réglages existants est conservé pour les bibliothèques de personnages et le réseau. Les pièces du kit sont ajustées par les poids des proxies MakeHuman. Cette commande génère aussi les coiffures de `assets/skins/Hairs/hair01_cc0(1)` dans `assets/characters/kit/hair`. Le catalogue conserve les indices existants et chaque personnage charge uniquement sa coiffure de bibliothèque.

Après une reconstruction du kit de base, lancer `python tools/blender/build_scifi_outfit.py` (NumPy, SciPy, Pillow), réimporter le projet avec Godot, puis régénérer les déplacements avec `build_character_details.py`. Le générateur lit le fedora dans le dossier de données de MakeHuman (variable `MAKEHUMAN_DATA`) et extrait les deux autres couvre-chefs des modèles sources. Les trois tenues importées (Sci-Fi Battle Royale, Mandalorien et Renegade Raider) utilisent leurs propres bottes et conservent la tête et les mains MakeHuman. Le générateur ajoute leurs entrées en fin de catalogue pour préserver les indices des tenues enregistrées et transmises sur le réseau.

Le dossier `assets/skins/newskins` fournit dix tenues et leurs accessoires indépendants. `build_character_details.py` régénère leurs maillages dans `assets/characters/kit/wardrobe`, leurs données de morphologie et les masques de peau.

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

**Fusils à pompe** : canon scié (8 plombs de 13, 2 cartouches qui s'enchaînent) et PeaceMaker (8 plombs de 13, 6 cartouches, pompe entre deux tirs, rechargement cartouche par cartouche :
`ReloadSeconds` pour un chargeur vide, moins s'il en manque peu). `spread` / `aimSpread` : rayon du groupé à la hanche / en visée ; `falloffStart` / `falloffEnd` : les dégâts baissent de l'une à l'autre distance, jusqu'à zéro.
Leur chargeur ne grandit pas avec le niveau. Les plombs d'un tir sur une même cible sont confirmés au tireur en un seul total.

**Coup par coup** : `weapon.<arme>.tapSeconds` (armes automatiques) : durée d'appui avant la deuxième balle (AK : 0,15 s), un clic plus bref n'en tire qu'une.

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

- **Une partie par processus.** Le matchmaking lance un conteneur par partie (`open-esport-<mode>-<port>`).
- **Hitbox calées sur des poses relevées, pas sur le squelette en direct.** Le serveur juge les balles contre une sphère pour la tête et des capsules pour le cou, le tronc, les bras, les mains, les jambes et les pieds. Ces volumes sont posés sur des poses relevées dans le jeu (`tools/hitbox/bake_body_poses.gd` → `OpenEsport.Core/Game/body_poses.bin`) selon l'arme en main, le clip (repos, marche, course, glissade, saut, accroupi, marche accroupie), la phase du pas (répliquée) et la morphologie (sexe, taille, corpulence). Le serveur ignore l'atterrissage, les coups de poing et le buste qui suivrait le regard ; à terre, en tyrolienne, en escalade et en chute libre, il garde des volumes simples. Toute nouvelle animation ou nouvelle prise d'arme impose de relancer le relevé.
- **Le décor côté serveur se résume à des boîtes.** La carte exportée (`.world`) contient le relief, des boîtes de collision et les vitres. Ce qui n'est pas converti en boîtes ne bloque ni le déplacement ni les tirs.
- **Mesures de performance faites en local** (robots et serveur sur le même ordinateur, latence quasi nulle). Les corrections et la compensation sont testées avec des pertes de paquets simulées en mémoire. Le déploiement sur VPS sert en vrai, sans campagne de mesure dédiée.
- **Déterminisme entre systèmes.** Windows et Linux calculent `sin` et `cos` avec des bibliothèques différentes : un client Windows et un serveur Linux peuvent différer d'une unité du dernier chiffre. C'est bien en dessous du seuil de correction (2 mm), mais à surveiller.
- **Protection contre la triche limitée à l'autorité du serveur** : cadence de commandes bornée, paquets invalides ignorés, tirs et déplacements décidés par le serveur. Pas d'anti-triche logiciel.
- **Animations du personnage** : le buste ne suit pas encore le regard (ni à l'image, ni dans les hitbox).

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

Menu Multijoueur > page « Mode de jeu » : Battle Royale, Deathmatch, Team Deathmatch, Capture du drapeau, Sabotage, Contrôle, 1 VS 1. Chaque mode a sa taille d'équipe et sa carte
(`assets/maps/relais.world` pour les deux modes Deathmatch, `drapeau.world`, `sabotage.world`, `controle.world`, `duel.world`, et `frontiere01.world` pour le battle royale ; à copier toutes dans `MAPS_DIR` sur le serveur). Le serveur se lance avec `--mode br|dm|tdm|ctf|sab|ctl|1v1`
(le matchmaking le fait : demande `MM?|version|taille|mode`, conteneur `open-esport-<mode>-<taille>-<port>`). Un seul humain suffit pour démarrer, des bots complètent les équipes.

| Mode | Équipes | Règles par défaut (réglables : panneau Godot, groupes « Modes de jeu » et « Sabotage ») |
|---|---|---|
| Deathmatch | chacun pour soi, 2 à 20 | 20 éliminations ou 10 min ; réapparition 3 s, 2 s de protection ; règles d'arène (ci-dessous) |
| Team Deathmatch | 2 équipes de 2 à 5 | 50 éliminations d'équipe ou 12 min |
| Capture du drapeau | 2 équipes de 4, 5 ou 10 | 3 prises ou 12 min ; drapeau lâché : retour à la base après 20 s ; porteur ralenti de 10 % |
| Sabotage | 2 équipes de 4 ou 5 | premier à 7 manches, mi-temps (camps échangés, argent remis à zéro) ; achat 15 s, manche 1 min 55, bombe 40 s, pose 3 s, désamorçage 5 s (3 s avec kit) ; économie à la Counter-Strike (touche B : boutique) |
| 1 VS 1 | un contre un | premier à 5 manches ; chaque manche : 3 s de préparation (immobiles), 1 min 30 de combat sans réapparition, toute la vie et un bouclier bleu plein ; au temps, la manche va à celui qui a le plus de vie + bouclier (égalité : manche nulle) ; un joueur qui quitte perd la partie |
| Contrôle | 2 équipes de 5 à 10 | Cinq relais A à E initialement neutres. Chaque seconde, l’équipe qui tient le plus de relais marque l’écart. La première à 100 points gagne. |

En Contrôle, chaque base dispose d’un hover monoplace et d’un mécha à cinq places. E permet d’embarquer puis de sortir. Le hover vole en vue extérieure (saut pour monter, accroupi pour descendre). Dans le marcheur, F1 commande la conduite et la mitrailleuse, F2 le canon, F3 à F5 les trois meurtrières pour les armes personnelles. Maintenir la commande Viser (clic droit par défaut) grossit la vue du pilote ×2 et celle de l’artilleur ×4. La mitrailleuse du marcheur inflige 32 dégâts de base, le canon 140 à l’impact direct avec une explosion. Les trois passagers tirent depuis une cabine blindée à travers leur propre meurtrière. Le serveur attribue les sièges libres, valide les déplacements et les arcs de tir, applique les impacts et réplique les véhicules aux deux équipes. Utiliser le protocole 33 et la même version de `controle.world` des deux côtés.

**1 VS 1** (`DuelRules`, `GameServer.Duel.cs`), carte « Duel » (`tools/map/gen_duel_map.py`, décor de Relais : une salle de 32 x 20 m, un bloc central de 2,2 m pour se cacher et l'escalader, une apparition de chaque côté).
L'équipement (deux armes avec leur viseur, une grenade, un soin ; munitions infinies) suit `--duel-loadout` :
- `random` (défaut, partie normale) : tiré au hasard à chaque manche, le même pour les deux joueurs ;
- `choice` : chacun choisit le sien au début de la partie (25 s, écran de choix, puis « Valider » ; sans choix, au hasard) et le garde pour toutes les manches ;
- `fixed:arme,viseur,arme2,viseur2,grenade,soin` (ex. `fixed:ak,x2,smg,point,frag,soins`) : imposé pour toute la partie. Clés : armes `c19 p2020 smg ak lmg pompe sniper peacemaker`, viseurs `aucun point holo x2 x4 x6`,
  grenades `aucune frag flash collante feu`, soins `soins recharges grand-soin grande-recharge`.
`--score-limit` règle le nombre de manches à gagner. Dans un tournoi, chaque phase de 1 VS 1 choisit la règle d'équipement et le nombre de manches.

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
- **Tournois** : l'organisation est faite par **ManageComp** (`MANAGECOMP_API_KEY`, `MANAGECOMP_URL` dans le `.env` du serveur). Page « Tournois » du jeu (liste, détail, création, modification) ; rappels et invitations dans l'encart du menu.
  1. **Configuration** (`POST /api/tournaments` `{config, participate}`, modifiable jusqu'au départ par `PUT /api/tournaments/{id}`, voir `TournamentConfig`) :
     inscription **en solo ou par équipes de 2 à 5**, inscrits mini / maxi, départ **à heure fixe** (lancé automatiquement, annulé s'il manque des inscrits)
     ou **par l'organisateur** (heure indicative), période d'inscription (dès la création par défaut, jusqu'à un an à l'avance), **check-in** (5 à 60 min avant un départ fixe :
     sans confirmation, l'équipe est retirée), et jusqu'à 8 **étapes** (`PhaseConfig`), chacune avec son déroulement et son **mode de jeu** :
     - **série de manches** (`lobby`) : points par place et par élimination, additionnés. Battle Royale (30 joueurs au plus par lobby), Deathmatch (solo, 10 par lobby),
       ou un mode à deux équipes (un lobby = un match, points au vainqueur et au perdant ; une équipe seule est exemptée et gagne) ;
     - **poules** (`pools`), **tableau** (`table`) et **poules de division** (`divisions` : D1, D2…, montées et descentes d'une étape de ce type à la suivante) :
       1 VS 1 (solo) et modes à deux équipes (Team Deathmatch, Sabotage, Contrôle, Capture du drapeau), en 1, 3 ou 5 parties par match (`BestOf`) ;
     - **séparation en divisions** (`split`) : le classement est coupé en 2 à 8 divisions, à parts égales ou de tailles choisies. La première continue dans ce tournoi,
       chacune des autres devient un tournoi rattaché (`parent`, `division`), comme dans ManageComp, et joue la suite de la formule avec ses inscrits.
     Équipes d'une étape : celles de l'inscription, ou **tirées au sort à chaque manche** parmi des inscrits en solo (séries).
     Sélections : qualifiés au total, ou par lobby dans une série ; répartition des lobbys équilibrée ou « par niveau ».
     Points par élimination : ManageComp ne connaît que des places, il reçoit un barème « une place = un score » (`ManageCompApi.EncodedScale`) ; les vraies places
     et éliminations sont gardées dans `tournament_results`.
     **Modèles** (`TournamentTemplates`, `/api/tournament-templates`) : ceux du jeu, ceux du joueur — privés d'office, publics s'il le veut — et ceux que les autres ont partagés.
  2. **Équipes par invitation** : le chef s'inscrit (`/join`), puis invite ses coéquipiers par leur nom (`/invite {login}`) ; chacun voit l'invitation dans son jeu
     (encart du menu, page du tournoi) et l'accepte (`/accept {reference}`) ou la refuse (`/decline`). Le chef retire un joueur ou annule une invitation (`/kick {login}`) ;
     un coéquipier qui part (`/leave`) ne fait partir que lui, le chef qui part retire toute l'équipe. Le groupe en jeu n'a plus rien à voir avec l'inscription.
     Avant le départ, une équipe n'existe que dans le jeu (référence négative) ; au départ, les équipes incomplètes ou absentes au check-in sont retirées,
     les autres sont inscrites dans ManageComp (nom « a & b »), dans l'ordre d'inscription.
  3. **Départ** (`/start` ou automatique, minuterie de 30 s) : la formule ManageComp est construite d'après la configuration du moment (appel → phases → classement général), la première étape est composée. À la fin de l'appel, ManageComp renumérote les participants : chacun est retrouvé par son nom.
  4. **Séries de manches** : « Jouer ma manche » → matchmaking (`MM?…|taille|mode|…|T<tournoi>:<manche>:<lobby>`, la manche a un numéro global)
     → serveur réservé au lobby (`--mode --tournament --race --lobby`), complété par des bots en Battle Royale et avec des équipes tirées au sort (`bots`), sinon entre inscrits. Le serveur lit la partie (`/internal/tournament/{id}/lobby/{n}` : comptes, mode, taille
     des équipes, score à atteindre, et les équipes : `groups` en battle royale — équipe inscrite ou tirée au sort —, `sides` dans les modes à deux équipes) ; si le mode ne correspond pas,
     il s'arrête. Il rapporte les places et les éliminations (`/internal/tournament-race`) ; un inscrit du lobby qui n'a pas joué compte comme abandon. Une phase finie, ses qualifiés passent à la suivante.
  5. **Matchs** (poules, tableau) : « Jouer mon match » (`/api/tournaments/{id}/me`, clé `p<poule>m<match>` ou `t<jeu>n<noeud>`, précédée de `f<étape>` et suivie de `g<partie>` au besoin) → matchmaking (`MM?…|TD<tournoi>:<clé>`)
     → serveur du mode de l'étape réservé aux deux camps (`/internal/tournament/{id}/match/{clé}` : camps, mode, score, équipement, bots).
     Un camp absent après 120 s perd par forfait ; une égalité n'est pas enregistrée (le match reste à jouer) ; le score est saisi dans ManageComp (`/internal/tournament-match`), après la dernière partie si le match en compte plusieurs.
  6. **Déroulé** : `GET /api/tournaments/{id}` renvoie aussi `flow` (résultats de chaque étape : lobbys, poules, tours du tableau) et `divisions`. Un tableau de ManageComp
     ne montre que trois tours et un seul tableau à la fois : `ManageCompApi.Matches` les fait défiler.
  7. **Bots** : chaque étape dit si ses parties sont complétées par des bots (`PhaseConfig.Bots`, vide : battle royale et équipes tirées au sort oui, le reste non)
     et combien au plus (`MaxBots`, 0 : sans limite). Le serveur de partie lit `bots` et `maxBots` ; le matchmaking lance toujours les parties de tournoi avec `--fill-bots`.
  8. **Lobby du tournoi** (`GET /api/tournaments/{id}/lobby?since=<dernier message>`, toutes les 3 s) : le détail (règles, classement provisoire, déroulé), les parties
     en cours, le tchat (`POST /api/tournaments/{id}/chat`, 200 derniers messages gardés en mémoire, un message toutes les 2 s au plus, réservé aux inscrits et
     aux observateurs) et les présents (ceux dont le jeu affiche le lobby). Le classement et le déroulé sont gardés 8 s : ManageComp n'est pas interrogé à chaque fois.
     Le matchmaking rapporte toutes les 3 s ses parties de tournoi (`POST /internal/tournament-live` : manche et lobby ou match, état, joueurs, scores, adresse).
     L'organisateur ouvre le check-in quand il veut (`POST /api/tournaments/{id}/opencheckin` ; `TournamentConfig.CheckInOpenedAt`).
     Dans le jeu, `TournamentWatcher` tient la liste à jour (`GET /api/tournaments?rev=N` : « inchangé » si rien n'a été écrit depuis la révision N) et emmène les
     inscrits au lobby 3 min avant le départ ; en partie, un bandeau prévient 30 s avant (F8 : se désinscrire et rester, F9 : partir tout de suite).
  9. **Observateurs** (`TournamentConfig.Observers`, 20 au plus, l'organisateur l'est d'office) : depuis le lobby, « Regarder » une partie en cours. Le jeu se connecte
     au serveur avec `MsgType.Spectate` (protocole 37) ; le serveur vérifie le jeton puis `/internal/tournament/{id}/observer/{compte}`. L'observateur n'a pas de
     personnage (identifiant 0xFFFF) : il reçoit tous les joueurs de la carte (fréquence selon la distance au joueur suivi), le butin, les tirs, les grenades et
     la zone autour du joueur suivi, le fil des éliminations et les scores ; `MsgType.Follow` change de joueur suivi. 16 observateurs au plus par partie.
     Limites : ni l'inventaire ni la visée exacte du joueur suivi ; pas de différé ni de rediffusion.
  Tests contre un vrai ManageComp local : `OE_MANAGECOMP_TEST_URL` et `OE_MANAGECOMP_TEST_KEY` (`TournamentLiveTests`). Lobby et observateurs : `TournamentLobbyTests`, `SpectatorTests`.
  Les tournois publiés directement dans ManageComp apparaissent aussi dans l'encart du menu (`/api/tournaments`, champ `tournaments`).

### Classement

- « Classement » dans l'encart du menu : `GET /api/tournaments/{id}` renvoie `standings` (place, nom, détail), tiré du classement cumulé de ManageComp (battle royale) ou du classement final (duels).
