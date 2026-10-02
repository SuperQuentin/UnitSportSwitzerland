using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The corner map (#190), top right: 1.2 km around you, the map image under the zone, your arrow,
/// the line to safety and your waypoint (<see cref="BrMapDraw"/>). It shows while a match runs and
/// you are in it, and follows whoever you watch once you are out. Its size, and whether it turns so
/// that where you look is up, are <see cref="BrPrefs"/> (#231).
/// </summary>
public partial class Minimap : Control
{
    public const float Margin = 16f;
    /// <summary>The side in pixels, as chosen (<see cref="BrPrefs.MinimapSide"/>).</summary>
    public static float Side => BrPrefs.Current.MinimapSide;
    /// <summary>Metres across the minimap.</summary>
    public const float Span = 1200f;

    private readonly BrManager _br;

    public Minimap(BrManager br)
    {
        _br = br;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public override void _Process(double delta)
    {
        Visible = _br.State.Running && _br.InMatch && _br.MapTexture != null;
        if (!Visible) return;
        Position = new Vector2(GetViewportRect().Size.X - Side - Margin, Margin);
        Size = new Vector2(Side, Side);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_br.MapTexture is not { } tex || _br.ViewPoint() is not { } view) return;
        float s = Side, ppm = s / Span;
        var mid = new Vector2(s, s) * 0.5f;
        // turning: the whole picture drawn about the middle, rotated so your heading points up
        float turn = BrPrefs.Current.MinimapTurns ? -Mathf.DegToRad(BrCompass.Bearing(view.Heading)) : 0f;
        Vector2 ToScreen(Vector2 z) => BrMapDraw.Screen(z - view.Position) * ppm;

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.08f, 0.09f, 0.1f));
        DrawSetTransform(mid, turn, Vector2.One);
        float side = _br.State.Side;
        var nw = ToScreen(new Vector2(-side * 0.5f, side * 0.5f));
        DrawTextureRect(tex, new Rect2(nw, new Vector2(side, side) * ppm), false);
        BrMapDraw.Overlays(this, _br, ToScreen, ppm, s * 2f);
        // the radar (#359): opponents close by, unless hidden under a net or in a bale
        foreach (var at in _br.Nearby())
        {
            var p = ToScreen(at);
            DrawCircle(p, 5f, new Color(0, 0, 0, 0.75f));
            DrawCircle(p, 3.5f, new Color(0.95f, 0.22f, 0.18f));
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.9f), false, 3f);
        // N where north is: at the top edge, or round the rim as the map turns
        var north = mid + new Vector2(0, -(s * 0.5f - 12f)).Rotated(turn);
        DrawString(ThemeDB.FallbackFont, north + new Vector2(-4, 5), "N", HorizontalAlignment.Left, -1, 13, Colors.White);
        // the scale bar: 200 m
        float bar = 200f * ppm;
        DrawLine(new Vector2(8, s - 10), new Vector2(8 + bar, s - 10), Colors.White, 2f);
        DrawString(ThemeDB.FallbackFont, new Vector2(10 + bar, s - 6), "200 m", HorizontalAlignment.Left, -1, 11, Colors.White);
    }
}
