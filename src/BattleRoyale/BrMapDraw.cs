using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// What the minimap and the full map both draw over the map image: the storm outside the
/// circle, the circle and the next one, the line to safety, the waypoint and the player's arrow.
/// Points are zone metres (east, north of the region centre); <c>toScreen</c> places them.
/// </summary>
public static class BrMapDraw
{
    public static readonly Color Storm = new(0.45f, 0.18f, 0.75f, 0.32f);
    public static readonly Color Edge = new(1f, 1f, 1f, 0.95f);
    public static readonly Color Next = new(1f, 1f, 1f, 0.75f);
    public static readonly Color Waypoint = new(1f, 0.82f, 0.2f);
    public static readonly Color Me = new(0.2f, 0.85f, 1f);
    public static readonly Color Drop = new(0.25f, 0.5f, 1f);
    public static readonly Color Rare = new(1f, 0.6f, 0.2f);
    public static readonly Color Mate = new(0.35f, 1f, 0.45f);
    public static readonly Color Postauto = new(1f, 0.8f, 0f);
    public static readonly Color Ping = new(1f, 0.45f, 0.85f);
    public static readonly Color Plane = new(0.95f, 0.95f, 0.85f);

    /// <summary>The supply drops of the match: zone position, and whether still falling.</summary>
    public static IEnumerable<(Vector2 At, bool Falling)> Airdrops(BrManager br) =>
        BrCrates.Instance?.All.Where(c => c.Style == CrateStyle.Airdrop)
            .Select(c => (new Vector2((float)(c.E - br.State.AreaE), (float)(c.N - br.State.AreaN)), !BrCrates.Instance.Landed(c)))
        ?? Enumerable.Empty<(Vector2, bool)>();

    /// <summary>A screen direction for a zone direction: north is up.</summary>
    public static Vector2 Screen(Vector2 zoneDir) => new(zoneDir.X, -zoneDir.Y);

    public static void Overlays(CanvasItem c, BrManager br, Func<Vector2, Vector2> toScreen, float ppm, float reach)
    {
        var zone = br.ZoneNow;
        if (zone is { } z)
        {
            // the storm: a band from the edge outwards, as wide as anything on screen
            var centre = toScreen(z.Centre);
            float r = z.Radius * ppm;
            c.DrawArc(centre, r + reach * 0.5f, 0, Mathf.Tau, 128, Storm, reach);
            c.DrawArc(centre, r, 0, Mathf.Tau, 128, Edge, 2f);
            if (z.Phase > 0 && !z.Over) Dashed(c, toScreen(z.NextCentre), z.NextRadius * ppm, Next);
        }

        // the cargo plane (#207) until its doors close: its line, the stretch the doors are open, the plane
        if (br.State.Flight is { } flight && Net.ClockSync.ServerNow < flight.ClosesAt)
        {
            var (open, shut) = flight.JumpStretch;
            DashedLine(c, toScreen(flight.From), toScreen(open), Plane with { A = 0.6f });
            c.DrawLine(toScreen(open), toScreen(shut), Plane, 2.5f);
            Arrow(c, toScreen(flight.At(Net.ClockSync.ServerNow)), flight.Dir, Plane);
        }

        // supply drops (#194): a blue crate, with its canopy while it is still coming down
        foreach (var drop in Airdrops(br))
        {
            var p = toScreen(drop.At);
            if (drop.Falling) c.DrawArc(p + new Vector2(0, -9), 7f, Mathf.Pi, Mathf.Tau, 8, Colors.White, 2f);
            c.DrawRect(new Rect2(p - new Vector2(5, 5), new Vector2(10, 10)), Drop);
            c.DrawRect(new Rect2(p - new Vector2(5, 5), new Vector2(10, 10)), Colors.Black, false, 1.2f);
        }

        // rare sites (#198): the wreck always (its smoke shows it), a bunker once you are within 400 m
        var here = br.ViewPoint()?.Position;
        foreach (var site in BrCrates.Instance?.All.Where(x => x.Style is CrateStyle.Wreck or CrateStyle.Bunker) ?? Enumerable.Empty<Crate>())
        {
            var spot = new Vector2((float)(site.E - br.State.AreaE), (float)(site.N - br.State.AreaN));
            if (site.Style == CrateStyle.Bunker && (here is not { } h || h.DistanceTo(spot) > 400f)) continue;
            var p = toScreen(spot);
            c.DrawCircle(p, 8f, new Color(0, 0, 0, 0.7f));
            c.DrawArc(p, 8f, 0, Mathf.Tau, 16, Rare, 1.5f);
            c.DrawString(ThemeDB.FallbackFont, p + new Vector2(-4, 5), "?", HorizontalAlignment.Left, -1, 14, Rare);
        }

        // team-mates (#231): green arrows with their names
        foreach (var mate in br.Mates())
        {
            var p = toScreen(mate.At);
            Arrow(c, p, mate.Heading, Mate);
            c.DrawString(ThemeDB.FallbackFont, p + new Vector2(9, -6), mate.Name, HorizontalAlignment.Left, -1, 11, Mate);
        }

        // the team's pings (#469): a pin in the pinger's name
        foreach (var ping in br.Pings)
        {
            var p = toScreen(ping.At);
            Pin(c, p, Ping);
            c.DrawString(ThemeDB.FallbackFont, p + new Vector2(9, -12), ping.Name, HorizontalAlignment.Left, -1, 11, Ping);
        }

        // the Postauto stops (#480): yellow squares, where a dogtag recalls a team-mate
        foreach (var (stop, _) in br.Stops())
        {
            var p = toScreen(stop);
            c.DrawRect(new Rect2(p - new Vector2(6, 6), new Vector2(12, 12)), Postauto);
            c.DrawRect(new Rect2(p - new Vector2(6, 6), new Vector2(12, 12)), Colors.Black, false, 1.2f);
            c.DrawString(ThemeDB.FallbackFont, p + new Vector2(-3.5f, 4.5f), "P", HorizontalAlignment.Left, -1, 11, Colors.Black);
        }

        if (br.Waypoint is { } wp) Pin(c, toScreen(wp), Waypoint);

        // whose position: yours, or the one you are watching
        if (br.ViewPoint() is not { } view) return;
        var at = toScreen(view.Position);
        if (zone is { Phase: > 0 } zz && br.Watching == 0)
        {
            // a dashed line to the nearest point of the next circle, once you are outside it
            float d = view.Position.DistanceTo(zz.NextCentre);
            if (d > zz.NextRadius && d > 1f)
            {
                var edge = zz.NextCentre + (view.Position - zz.NextCentre) / d * zz.NextRadius;
                DashedLine(c, at, toScreen(edge), new Color(1f, 1f, 1f, 0.85f));
            }
        }
        Arrow(c, at, view.Heading, br.Watching != 0 ? new Color(1f, 0.55f, 0.3f) : Me);
    }

    public static void Arrow(CanvasItem c, Vector2 at, Vector2 zoneHeading, Color color)
    {
        var dir = Screen(zoneHeading).Normalized();
        if (dir == Vector2.Zero) dir = Vector2.Up;
        var side = new Vector2(-dir.Y, dir.X);
        var pts = new[] { at + dir * 9f, at - dir * 6f + side * 6.5f, at - dir * 2.5f, at - dir * 6f - side * 6.5f };
        c.DrawColoredPolygon(pts, color);
        c.DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, new Color(0, 0, 0, 0.8f), 1.2f);
    }

    public static void Pin(CanvasItem c, Vector2 at, Color color)
    {
        c.DrawColoredPolygon(new[] { at, at + new Vector2(-6, -12), at + new Vector2(6, -12) }, color);
        c.DrawCircle(at + new Vector2(0, -14), 6f, color);
        c.DrawCircle(at + new Vector2(0, -14), 2.5f, new Color(0, 0, 0, 0.7f));
    }

    private static void Dashed(CanvasItem c, Vector2 centre, float r, Color color)
    {
        int n = Math.Clamp((int)(r * Mathf.Tau / 10f), 24, 240);
        for (int i = 0; i < n; i += 2)
            c.DrawArc(centre, r, i * Mathf.Tau / n, (i + 1) * Mathf.Tau / n, 3, color, 1.6f);
    }

    private static void DashedLine(CanvasItem c, Vector2 a, Vector2 b, Color color)
    {
        float len = a.DistanceTo(b);
        var dir = (b - a) / len;
        for (float s = 0; s < len; s += 12f)
            c.DrawLine(a + dir * s, a + dir * Mathf.Min(s + 6f, len), color, 1.6f);
    }
}
