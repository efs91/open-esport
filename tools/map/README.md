# Carte « Frontière 01 » — édition dans Godot

Carte multijoueur de 1,2 km (Terrain3D), 3 zones (A Complexe Orion, B Terminal Delta, C Bassin Nama), massif central, routes, forêt de sapins et désert de Namaqualand.
La scène `res://scenes/maps/Frontiere01.tscn` est désormais la référence pour l'édition. Elle est sauvegardée sur disque et n'est pas générée au lancement du jeu. Ouvrir cette scène et enregistrer normalement dans Godot. `build_frontiere.gd` refuse d'écraser une scène existante.

Description lisible de la carte (zones, centres, passes, routes, butin) : **[MAP.md](MAP.md)**, généré par `python tools/map/gen_map_md.py` (à relancer après une modification de la carte).

## Se repérer et tester

- Les 711 instances de bâtiments et accessoires sont réparties dans `Zones/A_Orion`, `Zones/B_Delta` et `Zones/C_Nama`. Les groupes ont une transformation identité afin de conserver exactement les positions existantes.
- Dans `EditorBookmarks`, sélectionner le repère de la zone puis appuyer sur **F** dans la vue 3D pour cadrer cet endroit.
- Ouvrir `res://tools/map/visual_preview.tscn` et lancer **F6** : **1** Orion, **2** Delta, **3** Nama. Maintenir le clic droit pour regarder et voler avec ZQSD (WASD sur QWERTY), E pour monter, A (Q sur QWERTY) pour descendre, Maj pour accélérer. Échap libère la souris.
- Cet aperçu charge uniquement la carte et une caméra locale : aucun joueur réseau, aucune connexion au serveur. Il ne change pas la scène principale du jeu.

## Réglages visuels

`WorldEnvironment > Environment` contient le tonemapping ACES, l'occlusion ambiante (SSAO), l'éclairage indirect d'écran (SSIL), le glow discret et les brouillards atmosphérique et volumétrique. `Sun` contrôle la lumière chaude et les ombres. Ces réglages s'appliquent à toute la carte. Les sapins proches projettent des ombres ; les représentations lointaines restent sans ombres. Le projet utilise MSAA 2×.

Ces effets coûtent du temps GPU côté client. Ils n'ajoutent aucun message réseau, mais une baisse des FPS peut augmenter la latence ressentie. Pour réduire le coût, désactiver d'abord SSIL et le brouillard volumétrique dans Environment. Aucun objectif de FPS n'a été mesuré.

## Géométrie et serveur

Cette passe conserve les positions, les volumes de collision, les régions Terrain3D et le monde exporté pour le serveur. Les collisions des bâtiments restent séparées sous `BuildingColliders`. Modifier la lumière ou les matériaux est purement visuel ; déplacer un mur, un obstacle ou modifier le relief nécessitera ensuite une mise à jour cohérente des collisions et du monde serveur. Déplacer seulement un mesh dans l'éditeur ne réalise pas cette synchronisation.


## Zone A « Complexe Orion » — entièrement éditable dans Godot

Orion n'est plus générée : chaque bâtiment est une **scène** (`res://scenes/maps/orion/*.tscn`) instanciée sous `Zones/A_Orion`. Ouvrir une scène (double-clic sur le nœud, ou **Ouvrir la scène**) pour déplacer un mur, supprimer une fenêtre, ajouter un étage ou une pièce du MegaKit. Les groupes de nœuds d'un bâtiment : `Sols`, `Murs`, `Cloisons`, `Escaliers`, `Garde-corps`, et le `StaticBody3D` **`Colliders`** (une `CollisionShape3D` boîte par obstacle).

- **Bâtiments** : `Tour_Orion` (5×5, 4 niveaux, atrium vitré), `Aile_Laboratoires` (12×3, 3 niveaux), `Data_Center`, `Quartiers`, `Hangar` (nef + coursive), `Centrale_Energie`, `Poste_Securite`, `Centre_Medical`, quatre `Portail_*`. Passerelles : `Passerelle_*` (tubes vitrés ou ouvertes) et `Arche_Portail_*`. Rues, places et monument : `Rues_Orion`, décor : `Decor_Orion`, piliers : `Piliers_Passerelle`.
- **Grille** : tuiles de 4 m, étages de 3 m, sol commun à 78,10 m (le relief du plateau varie de 77,6 à 78,06 m : la marche d'entrée fait au plus 0,46 m, sous le seuil de 0,5 m du netcode). Escaliers : `Platform_Stairs_4WideTall` étiré à 8 m, marches de collision de 0,25 m.
- **Collisions** : modifier un mur sans déplacer sa boîte `Box_n` du `Colliders` laisse la collision à l'ancienne place. Déplacer le bâtiment entier déplace tout (collisions comprises). L'exporteur (`WorldExporter.cs`) lit directement toutes les `CollisionShape3D` boîtes sous `Zones` ; les boîtes (rotations multiples de 90°) sont converties en boîtes alignées sur les axes.
- **Fenêtres** : un mur à fenêtre a deux boîtes (allège jusqu'à 1,35 m et linteau à partir de 2,35 m) : on voit et on tire à travers, on ne passe pas.
- **Butin** : groupes de coffres prêts à poser dans `res://scenes/loot/groupes/` (`Coffres_U6`, `Coffres_Face4` [2 paires qui se font face], `Coffres_Ligne5`, `Coffres_Rond6`, `Coffres_Carre4`, `Coffres_Arc5`), instanciés dans `Loot/Coffres` sous le nom `Orion_<Forme>_NN`. Les objets au sol sont `Orion_Objet_NNN` dans `Loot/Objets`. Tous gardent la qualité par défaut. Ouvrir un groupe pour changer le nombre ou la qualité de ses coffres (le changement s'applique à toutes ses instances sauf celles qui l'ont redéfini).
- **Après toute modification** : régénérer `assets/maps/frontiere01.world` (commande « Fichier serveur » ci-dessous).
- **Premier jet** : `tools/map/gen_orion_once.py`, à **ne pas relancer** (il refuse si `scenes/maps/orion/` existe). Il a aussi retiré les anciens bâtiments, boîtes de collision et points de butin d'Orion de `Frontiere01.tscn` et de `buildings_final.json`. Les scripts `build_frontiere.gd` et `buildings.gd` reconstruisent toute la carte (et réécriraient les zones à l'ancienne) : ne pas les lancer.

## Zones B « Terminal Delta » et C « Bassin Nama » — éditables comme Orion

Même principe qu'Orion : chaque bâtiment est une scène (`res://scenes/maps/delta/`, `res://scenes/maps/nama/`) instanciée sous `Zones/B_Delta` et `Zones/C_Nama`, avec son `Colliders`. Mêmes règles de collision, de grille (tuiles de 4 m, étages de 3 m) et de butin (groupes dans `res://scenes/loot/groupes/`, noms `Delta_<Forme>_NN` et `Nama_<Forme>_NN`, objets `Delta_Objet_NNN` et `Nama_Objet_NNN`). Premier jet : `tools/map/gen_zones_once.py` (usage unique, refuse si `scenes/maps/delta/` existe).

- **Delta** (plateau plat à 84,10 m) : `Halle_Terminal` (nef vitrée à coursive), `Tour_Controle` (5 niveaux), `Hangar_Fret_A` et `Hangar_Fret_B` (nef / mezzanine), `Quai_Fret` (structure ouverte), `Bureaux_Douane`, `Depot_Carburant` (silos), cour de conteneurs (`Conteneur_EO`, `Conteneur_NS`, `Conteneur_Pile` à escalier, instanciés sous `Conteneur_*_NN`), voies et places dans `Voies_Delta`, passerelles `Passerelle_Halle_Tour` et `Passerelle_Hangars`.
- **Nama** (cuvette : butte plate centrale vers 52 m, anneau creux vers 43 m) : `Station_Extraction` et `Derrick` sur l'`Esplanade_Nama`, quatre postes (`Poste_Nord/Est/Sud/Ouest`) sur l'anneau, reliés à l'esplanade par de longues passerelles ouvertes (`Passerelle_Poste_*`) sur pylônes (`Piliers_Poste_*`), quatre `Abri_n`, `Silos_n`, `Camp_Nama` (Starter Kit orange), `Socle_*` (dalles sous les bâtiments pour absorber les petites variations du relief).
- **Végétation** : dans Nama, les rochers, herbes et arbres situés sous les structures (648 instances) ont été retirés des régions Terrain3D (`maps/frontiere01/terrain/`, 4 régions) et leurs boîtes de `nature_boxes.json` (344). Si vous déplacez un bâtiment de Nama sur des rochers, ils le traverseront : retirez-les avec le pinceau de suppression de Terrain3D.
- **Collisions et butin** : comme Orion (le serveur lit toutes les boîtes sous `Zones`). Environ 155 coffres par zone : si c'est trop, supprimez des groupes sous `Loot/Coffres`. Après toute modification, régénérer `assets/maps/frontiere01.world` et, si besoin, la minimap (`python tools/map/gen_minimap.py`, qui lit maintenant les scènes des trois zones).

## Coffres et butin (éditable dans la scène)

Les coffres et les objets au sol sont des nœuds de `Frontiere01.tscn`, dossier `Loot` (`Coffres` et `Objets`). Chacun est une **instance d'un prefab** (scène Godot) :
`res://scenes/loot/CoffreButin.tscn` et `res://scenes/loot/ObjetButin.tscn`. Pour en ajouter un : glisser le prefab dans la vue 3D ou dans `Loot` (Ctrl+Maj+A), ou copier un point existant (Ctrl+D), puis le déplacer. Le repère coloré dans l'éditeur indique le type et l'activation.
Les réglages communs à tous les coffres (ou tous les objets) se changent dans la scène du prefab et s'appliquent à toutes ses instances, sauf celles qui les ont remplacés.

Dans l'éditeur, un point montre le vrai modèle (le coffre du jeu ; l'objet imposé ou le premier de la liste ; sinon une boîte de munitions), un disque coloré (vert = activé, orange = au hasard, rouge = désactivé) et une étiquette.
Réglages d'un point (Inspecteur) : **Kind** (Objet / Coffre), **Activation** (Désactivé = absent, Activé = à chaque partie, AuHasard = tiré au sort à chaque partie), **OwnChance** + **Chance** (probabilité propre à ce point ; sinon la probabilité commune), **Quality** (niveau des armes et boucliers : comme le jeu, en majorité classique / rare / épique / légendaire, ou que du rare, que du légendaire…), **Item** (objet imposé, « random » = tiré dans la table de butin), **Pool** (liste précise d'objets avec un poids chacun, optionnel : un objet de la liste pour un point Objet, les 4 objets du contenu pour un coffre), **Count** (quantité), **SnapToTerrain** (l'exporteur pose le point sur le relief). « En majorité » = probabilité `loot.qualityMajority` (0,7), sinon tirage normal.
Probabilités communes : réglages du jeu `loot.pointChance` (objets) et `loot.chestPointChance` (coffres), dans le panneau Godot « Réglages du jeu ».
Un coffre se tourne comme n'importe quel nœud (sa rotation est exportée, le modèle et le contenu suivent). Sa collision est la boîte alignée sur les axes qui le contient : exacte aux quarts de tour (0°, 90°…), un peu plus large entre deux. Il bloque joueurs et balles seulement les parties où le tirage le garde.

Après toute modification des points : régénérer le fichier serveur (commande « Fichier serveur » ci-dessus), puis relancer le serveur / le matchmaking.

## Ancienne chaîne de fabrication — référence historique

Les commandes ci-dessous décrivent la construction initiale. Ne pas relancer cette chaîne sur la carte éditée : certains outils écrivent directement les données de terrain ou les fichiers exportés. Ils ne font pas partie du workflow d'édition courant.

| Étape | Commande | Produit |
|---|---|---|
| Textures du terrain | `python tools/map/pack_textures.py <dossier ambientCG>` | `maps/frontiere01/textures/*_alb_ht.png`, `*_nrm_rgh.png` |
| Textures de végétation | `python tools/map/prepare_veg_textures.py` | `maps/frontiere01/textures/veg/` |
| Modèles (Blender en arrière-plan) | `blender -b <scène>.blend --python tools/blender/export_props.py -- maps/frontiere01/models tools/blender/specs_nama.json` (idem `specs_pine.json`), `build_fir.py` | `maps/frontiere01/models/*.glb` |
| Matériaux des kits sci-fi | `Godot --headless --script res://tools/map/make_kit_materials.gd` puis `python tools/map/apply_kit_materials.py`, puis réimport | `maps/frontiere01/materials/` |
| Relief et peinture | `python tools/map/gen_terrain.py` | `maps/frontiere01/data/height.r32`, `control.u32`, `masks.npz` |
| Végétation | `python tools/map/place_vegetation.py` | `data/inst_*.bin`, `manifest.json` |
| Bâtiments | `python tools/map/gen_buildings.py` | `data/buildings.json` |
| Scène + régions Terrain3D | `Godot --headless --script res://tools/map/build_frontiere.gd` | `scenes/maps/Frontiere01.tscn`, `maps/frontiere01/terrain/` |
| Dessin de la carte (minimap, touche M) | `python tools/map/gen_minimap.py` (après `gen_buildings.py` et un `build_frontiere.gd`) | `assets/maps/frontiere01.png` |
| Boîtes arbres/rochers | `Godot --headless --script res://tools/map/measure_models.gd` puis `python tools/map/gen_nature_boxes.py` | `data/nature_boxes.json` |
| Tyroliennes (câbles, piliers, tremplins) | `python tools/map/gen_ziplines.py`, pièces : `blender -b -P tools/blender/build_zipline_parts.py -- maps/frontiere01/models`, puis `build_frontiere.gd` | `data/ziplines.json`, scène |
| Fichier serveur | `Godot --headless res://tools/export_world.tscn -- --map=res://scenes/maps/Frontiere01.tscn --out=res://assets/maps/frontiere01.world` | `assets/maps/frontiere01.world` |

Captures : `Godot --path . --script res://tools/map/shot.gd -- --out=<dossier> --view=nom:x,y,z:cible_x,y,z[:ground[:pitch]] [--cap=x,y,z]` (`--cap` pose un joueur de 1,8 m pour juger l'échelle).

Notes : la grille Terrain3D est de 1536 m (6×6 régions de 256 m, origine −768) ; la zone jouable fait ±600 m, le reste est un rebord montagneux.
