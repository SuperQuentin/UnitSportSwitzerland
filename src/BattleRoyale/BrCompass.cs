using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The compass strip at the top centre of the screen (#190), as battle royales draw it: ticks every 5°,
/// the bearing every 15°, N/NE/E/... every 45°, all sliding as you turn. Your exact bearing sits
/// under the centre mark. Markers ride on the strip, and are pinned to its edge when behind you:
/// <list type="bullet">
/// <item>where the safe zone is (purple), with its distance;</item>
/// <item>your waypoint (yellow), with its distance.</item>
/// </list>
/// </summary>
public partial class BrCompass : Control
{
    public const float Width = 560f, Height = 34f, Top = 8f;
    /// <summary>Degrees either side of the centre that the strip shows.</summary>
    private const float HalfSpan = 80f;

    private static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private readonly BrManager _br;

    public BrCompass(BrManager br)
    {
        _br = br;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        Visible = _br.State.Running && _br.InMatch && BrPrefs.Current.Compass;
        if (!Visible) return;
        Position = new Vector2((GetViewportRect().Size.X - Width) * 0.5f, Top);
        Size = new Vector2(Width, Height + 26);
        QueueRedraw();
    }

    /// <summary>Bearing of a zone direction: 0 north, 90 east, clockwise.</summary>
    public static float Bearing(Vector2 zoneDir) => Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(zoneDir.X, zoneDir.Y)), 360f);

    public override void _Draw()
    {
        if (_br.ViewPoint() is not { } view) return;
        var font = ThemeDB.FallbackFont;
        float heading = Bearing(view.Heading);
        float ppd = Width / (HalfSpan * 2f);
        float mid = Width * 0.5f;

        // the band, fading out at both ends
        DrawRect(new Rect2(0, 0, Width, Height), new Color(0.05f, 0.05f, 0.08f, 0.55f));
        for (int deg = (int)Mathf.Floor((heading - HalfSpan) / 5f) * 5; deg <= heading + HalfSpan; deg += 5)
        {
            float x = mid + (deg - heading) * ppd;
            if (x < 0 || x > Width) continue;
            float a = 1f - Mathf.Clamp((Mathf.Abs(x - mid) - Width * 0.32f) / (Width * 0.18f), 0f, 1f);
            int d = (int)Mathf.PosMod(deg, 360);
            var col = new Color(1, 1, 1, a);
            if (d % 45 == 0)
            {
                string label = Cardinals[d / 45];
                var c = d == 0 ? new Color(1f, 0.35f, 0.3f, a) : col;
                float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, label.Length == 1 ? 17 : 14).X;
                DrawString(font, new Vector2(x - w * 0.5f, 22), label, HorizontalAlignment.Left, -1, label.Length == 1 ? 17 : 14, c);
                DrawLine(new Vector2(x, Height - 7), new Vector2(x, Height), c, 2f);
            }
            else if (d % 15 == 0)
            {
                string label = d.ToString();
                float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, 11).X;
                DrawString(font, new Vector2(x - w * 0.5f, 20), label, HorizontalAlignment.Left, -1, 11, col with { A = a * 0.8f });
                DrawLine(new Vector2(x, Height - 6), new Vector2(x, Height), col, 1.5f);
            }
            else DrawLine(new Vector2(x, Height - 4), new Vector2(x, Height), col with { A = a * 0.6f }, 1f);
        }

        // markers: the safe zone and the waypoint
        if (_br.ZoneNow is { } z && _br.Watching == 0)
        {
            var goal = z.Phase == 0 ? z.Centre : z.NextCentre;
            float radius = z.Phase == 0 ? z.Radius : z.NextRadius;
            float dist = view.Position.DistanceTo(goal) - radius;
            if (dist > 0) Marker(font, heading, goal - view.Position, BrMapDraw.Storm with { A = 1f }, $"{dist:F0} m", diamond: true, row: 0);
        }
        // the nearest supply drop within 2 km
        if (_br.Watching == 0 && BrMapDraw.Airdrops(_br).Select(d => d.At).OrderBy(d => d.DistanceTo(view.Position)).FirstOrDefault() is var drop
            && drop != Vector2.Zero && drop.DistanceTo(view.Position) < 2000f)
            Marker(font, heading, drop - view.Position, BrMapDraw.Drop, $"drop {drop.DistanceTo(view.Position):F0} m", diamond: false, row: 2);
        foreach (var mate in _br.Watching == 0 ? _br.Mates() : Enumerable.Empty<(string Name, Vector2 At, Vector2 Heading)>())
            Marker(font, heading, mate.At - view.Position, BrMapDraw.Mate, $"{mate.Name} {view.Position.DistanceTo(mate.At):F0} m", diamond: false, row: 3);
        foreach (var ping in _br.Pings)
            Marker(font, heading, ping.At - view.Position, BrMapDraw.Ping, $"{ping.Name} {view.Position.DistanceTo(ping.At):F0} m", diamond: true, row: 4);
        if (_br.Waypoint is { } wp && _br.Watching == 0)
            Marker(font, heading, wp - view.Position, BrMapDraw.Waypoint, $"{view.Position.DistanceTo(wp):F0} m", diamond: false, row: 1);

        // the centre mark and the exact bearing under it
        DrawColoredPolygon(new[] { new Vector2(mid, Height - 2), new Vector2(mid - 6, Height + 7), new Vector2(mid + 6, Height + 7) }, Colors.White);
        string b = ((int)Mathf.Round(heading) % 360).ToString();
        float bw = font.GetStringSize(b, HorizontalAlignment.Left, -1, 14).X;
        DrawRect(new Rect2(mid - bw * 0.5f - 6, Height + 8, bw + 12, 18), new Color(0.05f, 0.05f, 0.08f, 0.7f));
        DrawString(font, new Vector2(mid - bw * 0.5f, Height + 22), b, HorizontalAlignment.Left, -1, 14, Colors.White);
    }

    /// <param name="row">Which line under the strip its label goes on, so labels pinned to one edge do not overlap.</param>
    private void Marker(Font font, float heading, Vector2 toward, Color color, string label, bool diamond, int row)
    {
        if (toward.LengthSquared() < 1f) return;
        float rel = Mathf.Wrap(Bearing(toward) - heading, -180f, 180f);
        bool pinned = Mathf.Abs(rel) > HalfSpan;
        float x = Width * 0.5f + Mathf.Clamp(rel, -HalfSpan, HalfSpan) * Width / (HalfSpan * 2f);
        x = Mathf.Clamp(x, 8f, Width - 8f);
        float y = 6f;
        if (diamond)
            DrawColoredPolygon(new[] { new Vector2(x, y - 5), new Vector2(x + 6, y + 1), new Vector2(x, y + 7), new Vector2(x - 6, y + 1) }, color);
        else
            DrawColoredPolygon(new[] { new Vector2(x, y + 8), new Vector2(x - 5, y - 2), new Vector2(x + 5, y - 2) }, color);
        if (pinned)
        {
            // behind you: an arrow on the edge says which way to turn
            float s = Mathf.Sign(rel);
            DrawColoredPolygon(new[] { new Vector2(x + s * 14, y + 1), new Vector2(x + s * 8, y - 4), new Vector2(x + s * 8, y + 6) }, color);
        }
        float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, 11).X;
        DrawString(font, new Vector2(Mathf.Clamp(x - w * 0.5f, 0, Width - w), Height + 40 + row * 13), label, HorizontalAlignment.Left, -1, 11, color);
    }
}
