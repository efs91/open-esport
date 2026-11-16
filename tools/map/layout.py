"""Plan de la carte Frontière 01 (mètres ; x vers l'est, z vers le sud ; le nord est en -z)."""

HALF = 768            # la grille couvre -768..+768 (1536 m = 6x6 régions de 256 m) ; la zone jouable fait 1200 m
PLAY = 600            # demi-côté de la zone jouable
N = 1536              # 1 pixel = 1 m

# zones principales : centre, rayon du plateau plat, altitude du plateau
ZONES = {
    "A": dict(name="Complexe Orion", c=(-372.0, -288.0), r=135.0, h=78.0),
    "B": dict(name="Terminal Delta", c=(336.0, -288.0), r=135.0, h=84.0),
    "C": dict(name="Bassin Nama", c=(0.0, 340.0), r=165.0, h=52.0),
}
MASSIF = dict(c=(18.0, -170.0), r=215.0, h=215.0)     # massif central
# goulets d'étranglement (cols)
PASSES = {
    "Col Nord": (12.0, -392.0),
    "Tunnel Ouest": (-402.0, -16.0),
    "Col Est": (372.0, 14.0),
    "Ravin Sud": (-108.0, 205.0),
}
# routes principales (polylignes) : largeur en mètres
ROADS = {
    "A-B (Col Nord)": dict(w=8.0, pts=[(-372, -288), (-300, -352), (-170, -378), (12, -392), (170, -376), (290, -340), (336, -288)]),
    "A-C (Tunnel Ouest)": dict(w=8.0, pts=[(-372, -288), (-402, -190), (-418, -90), (-402, -16), (-410, 70), (-370, 170), (-290, 260), (-180, 320), (-70, 345), (0, 340)]),
    "B-C (Col Est)": dict(w=8.0, pts=[(336, -288), (372, -190), (380, -90), (372, 14), (350, 110), (290, 220), (190, 300), (90, 335), (0, 340)]),
}
# chemins secondaires (plus étroits, de terre)
TRAILS = {
    "Vallon": dict(w=4.0, pts=[(12, -392), (20, -300), (0, -190), (-20, -80), (-10, 30), (-60, 110), (-108, 205), (-70, 290), (0, 340)]),
    "Boucle A": dict(w=3.5, pts=[(-372, -288), (-300, -250), (-210, -190), (-120, -190), (-20, -80)]),
    "Boucle B": dict(w=3.5, pts=[(336, -288), (270, -230), (200, -170), (120, -130), (40, -120), (-20, -80)]),
    "Boucle Est": dict(w=3.5, pts=[(372, 14), (300, 40), (210, 40), (120, 30), (-10, 30)]),
    "Boucle Ouest": dict(w=3.5, pts=[(-402, -16), (-330, -10), (-250, 0), (-160, 15), (-10, 30)]),
}
