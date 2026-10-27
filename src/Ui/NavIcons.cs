using Godot;

namespace UnitSport.Ui;

/// <summary>
/// The six map-pin icons (#1226, navigation plan 2), drawn in code (no PNGs) on any canvas item: a flag, a star, an
/// edelweiss ("pretty spot"), a house, a camera (photo spot), a warning. Each has its colour (<see cref="ColorOf"/>).
/// Used by the compass strip, the adventure map and the pin popup.
/// </summary>
public static class NavIcons
{
    public static readonly string[] Names = { "Flag", "Star", "Edelweiss", "House", "Camera", "Warning" };

    private static readonly Color[] Colors =
    {
        new(0.90f, 0.22f, 0.20f), new(0.98f, 0.78f, 0.15f), new(0.96f, 0.96f, 0.92f),
        new(0.80f, 0.55f, 0.32f), new(0.35f, 0.68f, 1.00f), new(1.00f, 0.55f, 0.10f),
    };

    public static Color ColorOf(byte icon) => Colors[icon % Colors.Length];

    /// <summary>Draws icon <paramref name="icon"/> centred on <paramref name="c"/>, <paramref name="r"/> its half-size.</summary>
    public static void Draw(CanvasItem on, byte icon, Vector2 c, float r, Color ink)
    {
        switch (icon % 6)
        {
            case 0: // flag: a pole and a pennant
                on.DrawLine(c + new Vector2(-r * 0.6f, r), c + new Vector2(-r * 0.6f, -r), ink, Mathf.Max(1.2f, r * 0.22f));
                on.DrawColoredPolygon(new[] { c + new Vector2(-r * 0.6f, -r), c + new Vector2(r, -r * 0.45f), c + new Vector2(-r * 0.6f, r * 0.1f) }, ink);
                break;
            case 1: // star: five points
            {
                var pts = new Vector2[10];
                for (int i = 0; i < 10; i++)
                {
                    float a = -Mathf.Pi / 2 + i * Mathf.Pi / 5, rr = i % 2 == 0 ? r : r * 0.42f;
                    pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
                }
                on.DrawColoredPolygon(pts, ink);
                break;
            }
            case 2: // edelweiss: six woolly petals round a yellow heart
                for (int i = 0; i < 6; i++)
                {
                    float a = i * Mathf.Tau / 6;
                    on.DrawCircle(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.55f, r * 0.38f, ink);
                }
                on.DrawCircle(c, r * 0.3f, new Color(0.95f, 0.8f, 0.2f));
                break;
            case 3: // house: a roof over a box
                on.DrawColoredPolygon(new[] { c + new Vector2(-r, -r * 0.05f), c + new Vector2(0, -r), c + new Vector2(r, -r * 0.05f) }, ink);
                on.DrawRect(new Rect2(c + new Vector2(-r * 0.7f, -r * 0.05f), new Vector2(r * 1.4f, r * 1.05f)), ink);
                break;
            case 4: // camera: a body, a lens
                on.DrawRect(new Rect2(c + new Vector2(-r, -r * 0.55f), new Vector2(r * 2, r * 1.3f)), ink);
                on.DrawRect(new Rect2(c + new Vector2(-r * 0.35f, -r * 0.85f), new Vector2(r * 0.7f, r * 0.35f)), ink);
                on.DrawCircle(c + new Vector2(0, r * 0.1f), r * 0.42f, new Color(0.1f, 0.1f, 0.12f));
                break;
            default: // warning: a triangle with a bar
                on.DrawColoredPolygon(new[] { c + new Vector2(0, -r), c + new Vector2(r, r * 0.8f), c + new Vector2(-r, r * 0.8f) }, ink);
                on.DrawLine(c + new Vector2(0, -r * 0.35f), c + new Vector2(0, r * 0.3f), new Color(0.1f, 0.1f, 0.1f), Mathf.Max(1.2f, r * 0.22f));
                on.DrawCircle(c + new Vector2(0, r * 0.55f), Mathf.Max(0.8f, r * 0.12f), new Color(0.1f, 0.1f, 0.1f));
                break;
        }
    }
}
