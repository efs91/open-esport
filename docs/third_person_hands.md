# Prises des armes en troisième personne

`ItemVisuals.BuildHeldModel` échantillonne la première image du clip `idle` de l'arme
avant de supprimer ses bras FPS. `WeaponHandPose` conserve, pour chaque main, un
repère de paume et l'orientation des quinze phalanges. Les transformations de
centrage, de rotation et de mise à l'échelle de l'arme sont aussi appliquées aux
prises. Les points de contact ne dépendent donc plus de pourcentages de sa boîte
englobante, ni de la pose sauvegardée dans l'éditeur.

Les repères sont construits à partir des articulations, pour adapter le rig FPS
au rig humanoïde du personnage. `ArmIK` calcule le décalage paume–poignet sur la
main morphée, résout les bras puis applique les poses des doigts. Si une prise
dépasse la portée des bras, il rapproche l'arme entière ; il ne détache pas une
main de sa poignée. La position de référence suit les épaules animées. Pour les armes longues, le buste pivote et le réglage de portée préserve en priorité le dégagement latéral de la crosse. En glissade, la prise descend avec le haut du corps.

Le couteau et le C19 utilisent leur idle d'origine à une main. Les six autres
armes utilisent les deux mains. Les mains libres reçoivent une pose détendue,
avec balancement à la marche et à la course. En glissade, le bras libre se replie le long du buste. Aucun clip source n'est modifié.

Pour ajouter une arme issue du même kit, fournir un clip `idle` avec ses pistes
de transformation et les os `L_/R_wrist`, `thumb`, `point`, `middle`, `ring`,
`pink` (suffixes numériques acceptés). Un autre rig nécessite une correspondance
dans `WeaponHandPose`. Pour changer une prise, ajuster l'idle de cette arme.

## Contrôle

Après `dotnet build`, lancer avec l'exécutable Godot .NET :

```text
Godot --path . --fixed-fps 60 --resolution 900x800 --audio-driver Dummy res://tools/character_pose_review.tscn
```

Cette scène hors jeu photographie les huit armes et les mains nues de trois quarts, de profil et en glissade dans `.godot/pose-review/`, puis contrôle les contacts en marche/course,
sur trois morphologies extrêmes, avec rotation, translation et échelle du parent.
Elle vérifie aussi les prises, les phalanges, la direction du poignet au couteau, la position des coudes, le dégagement de la crosse autour du thorax et la hauteur des mains en glissade. Elle termine avec un code de sortie non nul en cas d'échec. Ajouter
`--headless` pour exécuter seulement les contrôles numériques, sans captures.

L'écart maximal accepté entre la cible et la paume est de 2 mm. Ce contrôle ne
mesure pas les intersections de surface entre les doigts et l'arme : les
captures complètent la vérification. Le masquage du corps sous les vêtements
et leurs poids de déformation restent un sujet distinct.
