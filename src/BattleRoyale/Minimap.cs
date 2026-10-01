using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The corner map (#190), top right: 1.2 km around you, north up, the map image under the zone,
/// your arrow, the line to safety and your waypoint (<see cref="BrMapDraw"/>). It shows while a
/// match runs and you are in it, and follows whoever you watch once you are out.
/// </summary>
public partial class Minimap : Control
{
    public const float Side = 220f, Margin = 16f;
    /// <summary>Metres across the minimap.</summary>
    public const float Span = 1200f;

    private readonly BrManager _br;

    public Minimap(BrManager br)
    {
        _br = br;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        CustomMinimumSize = new Vector2(Side, Side);
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
        float ppm = Side / Span;
        var mid = new Vector2(Side, Side) * 0.5f;
        Vector2 ToScreen(Vector2 z) => mid + BrMapDraw.Screen(z - view.Position) * ppm;

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.08f, 0.09f, 0.1f));
        float side = _br.State.Side;
        var nw = ToScreen(new Vector2(-side * 0.5f, side * 0.5f));
        DrawTextureRect(tex, new Rect2(nw, new Vector2(side, side) * ppm), false);
        BrMapDraw.Overlays(this, _br, ToScreen, ppm, Side * 2f);

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.9f), false, 3f);
        DrawString(ThemeDB.FallbackFont, new Vector2(Side * 0.5f - 4, 14), "N", HorizontalAlignment.Left, -1, 13, Colors.White);
        // the scale bar: 200 m
        float bar = 200f * ppm;
        DrawLine(new Vector2(8, Side - 10), new Vector2(8 + bar, Side - 10), Colors.White, 2f);
        DrawString(ThemeDB.FallbackFont, new Vector2(10 + bar, Side - 6), "200 m", HorizontalAlignment.Left, -1, 11, Colors.White);
    }
}
