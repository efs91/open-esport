using Godot;
using OpenEsport.Core;

namespace Openesport.Online
{
    public partial class VehicleInstruments : Control
    {
        public ControlVehicle Vehicle;
        public int Seat, ZoomPower;
        public Vector2 HoverSight;
        public override void _Draw()
        {
            if (Vehicle == null) return;
            var v = Vehicle;
            if (!v.Hover) { DrawWalker(); return; }
            DrawString(ThemeDB.FallbackFont, new Vector2(0, Size.Y * .91f), "HOVER   ·   Saut / accroupi : altitude   |   E : sortir", HorizontalAlignment.Center, Size.X, 16, Colors.White);
            var center = Size * new Vector2(.22f,.72f);
            float width = Mathf.Min(390, Size.X * .42f);
            DrawRect(new Rect2(center - new Vector2(width / 2, 24), new Vector2(width, 55)), new Color(.025f, .06f, .075f, .91f));
            var ink = new Color(.5f, .97f, .95f);
            var font = ThemeDB.FallbackFont;
            int heading = Mathf.PosMod(Mathf.RoundToInt(-Mathf.RadToDeg(v.Yaw)), 360);
            string info = $"{v.Velocity.Length() * 3.6f:0} km/h     CAP {heading:000}°     ALT {v.Pos.Y:0} m";
            DrawString(font, center + new Vector2(-width / 2, -2), info, HorizontalAlignment.Center, width, 18, ink);
            DrawString(font, center + new Vector2(-width / 2, 20), $"TANGAGE {Mathf.RadToDeg(v.Pitch):+0;-0;0}°     ROULIS {Mathf.RadToDeg(v.Roll):+0;-0;0}°", HorizontalAlignment.Center, width, 14, ink);
            var mid = HoverSight;
            DrawArc(mid, 8, 0, Mathf.Tau, 32, ink, 1.4f, true);
            DrawLine(mid + Vector2.Left * 18, mid + Vector2.Left * 10, ink, 2);
            DrawLine(mid + Vector2.Right * 18, mid + Vector2.Right * 10, ink, 2);
        }

        private void DrawWalker()
        {
            var v = Vehicle;
            var accent = new Color("#a5f5d4");
            var plate = new Color(.025f, .065f, .067f, .92f);
            var hullColor = v.Integrity > v.FullIntegrity / 2 ? accent : v.Integrity > v.FullIntegrity / 4 ? new Color("#ffc465") : new Color("#ff796a");
            // Anchor the console above the lower edge. Scale its drawing coordinates together on small viewports.
            float scale = Mathf.Min(1f, Mathf.Min(Size.X / 770f, Size.Y / 480f));
            DrawSetTransform(new Vector2(Size.X / 2, Size.Y - (Seat >= 2 ? 106 : 14) * scale), 0, Vector2.One * scale);
            DrawRect(new Rect2(-348, -81, 696, 81), plate);
            DrawLine(new Vector2(-348,-81), new Vector2(348,-81), accent, 2);
            void Text(float x, float y, string value, int size, Color color) =>
                DrawString(ThemeDB.FallbackFont, new Vector2(x,y), value, HorizontalAlignment.Left, -1, size, color);
            string station = Seat == 0 ? "Pilote · Mitrailleuse" : Seat == 1 ? "Artilleur · Canon" : "Passager · Arme personnelle";
            if (Seat < 2) station += ZoomPower > 1 ? $"   ×{ZoomPower}" : "   ·   Viser pour zoomer";
            Text(-334, -60, "MÉCHA   /   " + station, 15, accent);
            Text(-334, -35, $"{v.Velocity.Length() * 3.6f:0} km/h     CAP {Mathf.PosMod(Mathf.RoundToInt(-Mathf.RadToDeg(v.Yaw)), 360):000}°     ALT {v.Pos.Y:0} m", 18, Colors.White);
            Text(-334, -12, $"Tangage {Mathf.RadToDeg(v.Pitch):+0;-0;0}°     Roulis {Mathf.RadToDeg(v.Roll):+0;-0;0}°", 15, accent);
            Text(18, -60, $"BLINDAGE    {v.Integrity} / {v.FullIntegrity}", 15, hullColor);
            float fraction = Mathf.Clamp((float)v.Integrity / v.FullIntegrity, 0, 1);
            DrawRect(new Rect2(18, -51, 310, 8), new Color("#43595b"));
            DrawRect(new Rect2(18, -51, 310 * fraction, 8), hullColor);
            Text(18, -24, $"Dégâts subis   {v.FullIntegrity - v.Integrity} PV", 16, hullColor);
            Text(18, -6, "F1–F5  Changer de siège       E  Sortir", 13, Colors.White);

            DrawSetTransform(Size * .5f, 0, Vector2.One * scale);
            foreach (float sign in new[] { -1f, 1f })
            {
                DrawLine(new Vector2(sign * 22,-6), new Vector2(sign * 22,6), accent, 2);
                DrawLine(new Vector2(sign * 22,0), new Vector2(sign * 9,0), accent, 2);
            }
            DrawCircle(Vector2.Zero, 2, accent);
        }
    }
}
