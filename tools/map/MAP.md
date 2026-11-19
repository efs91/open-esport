# Carte « Frontière 01 » — description pour lecture (IA ou humain)

> Fichier **généré** par `python tools/map/gen_map_md.py` à partir de `scenes/maps/Frontiere01.tscn` et de `tools/map/layout.py`. Ne pas l'éditer à la main : relancer le script après une modification de la carte.
> La scène reste la référence et s'édite dans Godot ; ce fichier ne sert qu'à se repérer.

## Repère et dimensions

- Carte de 1 536 m × 1 536 m (1 pixel = 1 m), origine au centre : **X** vers l'est, **Z** vers le sud (le nord est en **Z négatif**), **Y** = altitude.
- Zone jouable : ±600 m autour de l'origine ; au-delà, rebord montagneux puis panorama alpin.
- Terrain : nœud `Terrain3D` (données dans `maps/frontiere01/terrain/`, hauteur brute dans `maps/frontiere01/data/height.r32`, altitudes de 26 m à 311 m).
- Positions ci-dessous : `(x, z)` en mètres.

## Vue d'ensemble des zones

| Lettre | Nom | Nœud de la scène | Centre (x, z) | Rayon du plateau | Altitude du plateau | Bâtiments et accessoires |
|---|---|---|---|---|---|---|
| A | Complexe Orion | `Zones/A_Orion` | (-372, -288) | 135 m | 78 m | 21 |
| B | Terminal Delta | `Zones/B_Delta` | (336, -288) | 135 m | 84 m | 28 |
| C | Bassin Nama | `Zones/C_Nama` | (0, 340) | 165 m | 52 m | 32 |
| — | Massif central | (relief du terrain) | (18, -170) | 215 m | 215 m | — |

Les trois zones sont de grands **plateaux plats** reliés par des routes ; le massif central les sépare (il sert de couverture et de point de vue).

## Zone A — Complexe Orion

- **Nœud** : `Zones/A_Orion` dans `scenes/maps/Frontiere01.tscn` (chercher `parent="Zones/A_Orion"` pour lister ses 21 instances).
- **Centre** : (-372, -288), rayon 135 m, plateau à 78 m d'altitude.
- **Emprise des bâtiments** : x de -436 à -316, z de -376 à -206, altitude 78 à 84 m.
- **Contenu le plus fréquent** : `Tour_Orion` ×1, `Aile_Laboratoires` ×1, `Data_Center` ×1, `Quartiers` ×1, `Hangar` ×1, `Centrale_Energie` ×1, `Poste_Securite` ×1, `Centre_Medical` ×1.
- **Butin** dans l'emprise de la zone : 38 coffre(s) (`Loot/Coffres`), 262 objet(s) au sol (`Loot/Objets`).
- **Passes proches** : Col Nord, Tunnel Ouest.
- **Routes qui y arrivent** : A-B (Col Nord), A-C (Tunnel Ouest).

## Zone B — Terminal Delta

- **Nœud** : `Zones/B_Delta` dans `scenes/maps/Frontiere01.tscn` (chercher `parent="Zones/B_Delta"` pour lister ses 28 instances).
- **Centre** : (336, -288), rayon 135 m, plateau à 84 m d'altitude.
- **Emprise des bâtiments** : x de 248 à 432, z de -340 à -236, altitude 0 à 90 m.
- **Contenu le plus fréquent** : `Conteneur_EO` ×8, `Conteneur_Pile` ×6, `Conteneur_NS` ×4, `Halle_Terminal` ×1, `Tour_Controle` ×1, `Hangar_Fret_A` ×1, `Hangar_Fret_B` ×1, `Quai_Fret` ×1.
- **Butin** dans l'emprise de la zone : 33 coffre(s) (`Loot/Coffres`), 200 objet(s) au sol (`Loot/Objets`).
- **Passes proches** : Col Nord, Col Est.
- **Routes qui y arrivent** : A-B (Col Nord), B-C (Col Est).

## Zone C — Bassin Nama

- **Nœud** : `Zones/C_Nama` dans `scenes/maps/Frontiere01.tscn` (chercher `parent="Zones/C_Nama"` pour lister ses 32 instances).
- **Centre** : (0, 340), rayon 165 m, plateau à 52 m d'altitude.
- **Emprise des bâtiments** : x de -104 à 96, z de 248 à 436, altitude 0 à 53 m.
- **Contenu le plus fréquent** : `Station_Extraction` ×1, `Derrick` ×1, `Poste_Nord` ×1, `Poste_Est` ×1, `Poste_Sud` ×1, `Poste_Ouest` ×1, `Abri_1` ×1, `Abri_2` ×1.
- **Butin** dans l'emprise de la zone : 42 coffre(s) (`Loot/Coffres`), 161 objet(s) au sol (`Loot/Objets`).
- **Passes proches** : Col Est, Ravin Sud.
- **Routes qui y arrivent** : A-C (Tunnel Ouest), B-C (Col Est).

## Passes (goulets d'étranglement)

| Nom | Position (x, z) |
|---|---|
| Col Nord | (12, -392) |
| Tunnel Ouest | (-402, -16) |
| Col Est | (372, 14) |
| Ravin Sud | (-108, 205) |

## Routes et chemins

| Nom | Largeur | Points (x, z) |
|---|---|---|
| A-B (Col Nord) | 8.0 m | (-372, -288) → (-300, -352) → (-170, -378) → (12, -392) → (170, -376) → (290, -340) → (336, -288) |
| A-C (Tunnel Ouest) | 8.0 m | (-372, -288) → (-402, -190) → (-418, -90) → (-402, -16) → (-410, 70) → (-370, 170) → (-290, 260) → (-180, 320) → (-70, 345) → (0, 340) |
| B-C (Col Est) | 8.0 m | (336, -288) → (372, -190) → (380, -90) → (372, 14) → (350, 110) → (290, 220) → (190, 300) → (90, 335) → (0, 340) |
| Vallon | 4.0 m | (12, -392) → (20, -300) → (0, -190) → (-20, -80) → (-10, 30) → (-60, 110) → (-108, 205) → (-70, 290) → (0, 340) |
| Boucle A | 3.5 m | (-372, -288) → (-300, -250) → (-210, -190) → (-120, -190) → (-20, -80) |
| Boucle B | 3.5 m | (336, -288) → (270, -230) → (200, -170) → (120, -130) → (40, -120) → (-20, -80) |
| Boucle Est | 3.5 m | (372, 14) → (300, 40) → (210, 40) → (120, 30) → (-10, 30) |
| Boucle Ouest | 3.5 m | (-402, -16) → (-330, -10) → (-250, 0) → (-160, 15) → (-10, 30) |

## Autres parties de la scène

| Nœud | Contenu |
|---|---|
| `Terrain3D` | le relief et sa peinture (aucun enfant) |
| `Sun`, `WorldEnvironment` | lumière, ciel, brouillard, occlusion ambiante |
| `WorldEnvironment` | panorama alpin derrière les crêtes de Terrain3D |
| `BuildingColliders` | 0 boîtes de collision des bâtiments (séparées des modèles) |
| `Loot/Coffres`, `Loot/Objets` | 194 coffres, 759 objets au sol (points de butin éditables) |
| `Ziplines` | 645 éléments de tyroliennes (piliers, câbles, tremplins) |

## Fichiers liés

- Scène (référence, éditée dans Godot) : `scenes/maps/Frontiere01.tscn`
- Fichier serveur (collisions, butin, tyroliennes) : `assets/maps/frontiere01.world` — **à régénérer après toute modification** (commande « Fichier serveur » de `tools/map/README.md`).
- Image de la carte (touche M) : `assets/maps/frontiere01.png` (`tools/map/gen_minimap.py`)
- Données : `maps/frontiere01/data/` (hauteurs, bâtiments, tyroliennes, boîtes de la nature), modèles : `maps/frontiere01/models/`
- Positions de référence des zones, passes et routes : `tools/map/layout.py`

## Comment travailler sur une zone

1. Repérer la zone ci-dessus (nœud, centre, rayon).
2. Lister ses éléments : chercher `parent="Zones/<NOEUD>"` dans `scenes/maps/Frontiere01.tscn` (le nom du modèle est dans la ressource externe de chaque instance, l'emplacement dans `transform`).
3. Les collisions sont dans `BuildingColliders` (une boîte par bâtiment, à la même position) ; le butin est dans `Loot` (filtrer par position à l'intérieur du rayon de la zone).
4. Après toute modification de la zone : régénérer `assets/maps/frontiere01.world`, puis relancer le serveur.
