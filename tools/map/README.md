# Carte « Frontière 01 » — édition dans Godot

Carte multijoueur de 1,2 km (Terrain3D), 3 zones (A Complexe Orion, B Terminal Delta, C Bassin Nama), massif central, routes, forêt de sapins et désert de Namaqualand.
La scène `res://scenes/maps/Frontiere01.tscn` est désormais la référence pour l'édition. Elle est sauvegardée sur disque et n'est pas générée au lancement du jeu. Ouvrir cette scène et enregistrer normalement dans Godot. `build_frontiere.gd` refuse d'écraser une scène existante.

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
