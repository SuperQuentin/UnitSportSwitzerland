using UnitSport.Playtest.Recording;

namespace UnitSport.Playtest.TopView;

/// <summary>A point in LV95 metres.</summary>
public readonly record struct EN(double E, double N)
{
    public static EN operator +(EN a, EN b) => new(a.E + b.E, a.N + b.N);
    public static EN operator -(EN a, EN b) => new(a.E - b.E, a.N - b.N);
    public static EN operator *(EN a, double k) => new(a.E * k, a.N * k);
    public double Length => Math.Sqrt(E * E + N * N);
    /// <summary>The unit vector of compass bearing <paramref name="heading"/> (0 north, π/2 east).</summary>
    public static EN Bearing(double heading) => new(Math.Sin(heading), Math.Cos(heading));
}

/// <summary>A colour as the top view draws it, <c>#rrggbb</c>.</summary>
public readonly record struct TopColor(string Hex);

public sealed record TopTrack(int Id, TopColor Color, List<EN> Points, List<EN> Ticks, bool Server, bool Minor = false);
/// <summary>An oriented box: <paramref name="Across"/> by <paramref name="Along"/> metres, its long side along <paramref name="Heading"/>.</summary>
public sealed record TopBox(EN Center, double Across, double Along, double Heading, TopColor Color, string Label, bool Server);
public sealed record TopArrow(EN At, double Heading, TopColor Color, string Label, bool Server);
public sealed record TopVector(EN From, EN To);
public sealed record TopPin(EN At, string Label);
/// <summary>Where the client drew a thing (<paramref name="Client"/>) against where the server had it.</summary>
public sealed record TopOffset(EN Client, EN Server, double Metres);

/// <summary>The shapes of one top view, in metres, and the square they fit in.</summary>
public sealed class TopViewShapes
{
    public readonly List<TopTrack> Tracks = new();
    public readonly List<TopBox> Boxes = new();
    public readonly List<TopArrow> Arrows = new();
    public readonly List<TopVector> Vectors = new();
    public readonly List<TopPin> Pins = new();
    public readonly List<TopOffset> Offsets = new();
    /// <summary>South-west corner and side of the square shown, metres.</summary>
    public EN Min;
    public double Side;
    public double T0, T1, At;
    public string Title = "";
}

/// <summary>A note's pin on the map.</summary>
public readonly record struct TopNote(double T, double E, double N, string Text);

/// <summary>What the top view is drawn from: a trace window (the recorder's ring, or a file) and what goes on it.</summary>
public sealed class TopViewInput
{
    public required IReadOnlyList<TraceFrame> Frames { get; init; }
    public required IReadOnlyDictionary<int, TraceEntity> Entities { get; init; }
    public double T0 { get; init; }
    public double T1 { get; init; }
    /// <summary>The instant the boxes and arrows stand at; the window's end when null.</summary>
    public double? At { get; init; }
    /// <summary>The square's centre and half-side; fitted to the tracks when null.</summary>
    public EN? Around { get; init; }
    public double? Radius { get; init; }
    public IReadOnlyList<TopNote> Notes { get; init; } = [];
    /// <summary>The server's trace of the same session, if there is one, and how many seconds its frames run ahead of the client's.</summary>
    public IReadOnlyList<TraceFrame>? ServerFrames { get; init; }
    public IReadOnlyDictionary<int, TraceEntity>? ServerEntities { get; init; }
    public double ServerShift { get; init; }
    /// <summary>Things drawn at most (the nearest to the centre first).</summary>
    public int MaxThings { get; init; } = 120;
}

/// <summary>
/// The top view's model (#844, docs/notes/general/topview.md): the shapes, in metres, of a stretch of
/// a playtest trace. One renderer for both outputs: the live overlay draws them with
/// <c>Control._Draw</c>, the offline <c>--topview</c> as an SVG (<see cref="TopViewSvg"/>). No engine.
/// </summary>
public static class TopViewScene
{
    public static readonly TopColor PlayerColor = new("#2f7de1"), LocalColor = new("#1450a8"), VehicleColor = new("#e8822a"),
        TrafficColor = new("#8a8f98"), ItemColor = new("#3aa65a"), OtherColor = new("#9b59b6"), ServerColor = new("#d23c3c"),
        MinorColor = new("#b9b2a3");

    public static TopColor ColorOf(TraceEntity e) =>
        e.IsPlayer ? e.Local ? LocalColor : PlayerColor
        : e.IsMinor ? MinorColor
        : e.Class.Contains("Traffic", StringComparison.Ordinal) ? TrafficColor
        : e.Class.Contains("Item", StringComparison.Ordinal) ? ItemColor
        : e.Class.Contains("Vehicle", StringComparison.Ordinal) ? VehicleColor
        : OtherColor;

    public static TopViewShapes Build(TopViewInput input)
    {
        var shapes = new TopViewShapes { T0 = input.T0, T1 = input.T1, At = input.At ?? input.T1 };
        var frames = input.Frames.Where(f => f.T >= input.T0 && f.T <= input.T1).ToList();
        var at = Nearest(frames, shapes.At);

        // which things: seen in the window, inside the square when one is given, nearest first
        var seen = new Dictionary<int, EN>();
        foreach (var f in frames)
            for (int i = 0; i < f.Count; i++)
                seen.TryAdd(f.States[i].Id, new EN(f.States[i].E, f.States[i].N));
        EN? around = input.Around;
        double? radius = input.Radius;
        IEnumerable<int> ids = seen.Keys;
        if (around is { } c && radius is { } rad)
            ids = ids.Where(id => frames.Any(f => f.TryGet(id, out var s) && Math.Abs(s.E - c.E) <= rad && Math.Abs(s.N - c.N) <= rad));
        var reference = around ?? Centroid(seen.Values);
        int Rank(int id) => !input.Entities.TryGetValue(id, out var e) ? 2 : e.IsPlayer ? 0 : e.IsMinor ? 3 : 1;
        var chosen = ids.OrderBy(Rank)
            .ThenBy(id => (seen[id] - reference).Length)
            .Take(input.MaxThings)
            .ToHashSet();

        foreach (int id in chosen)
        {
            var e = input.Entities.GetValueOrDefault(id) ?? new TraceEntity { Id = id, Kind = "?", Label = $"#{id}" };
            var color = ColorOf(e);
            var track = TrackOf(frames, id, color, server: false) with { Minor = e.IsMinor };
            if (track.Points.Count > 1) shapes.Tracks.Add(track);
            // small fry: the track only, faint, no box and no label
            if (!e.IsMinor && at != null && at.TryGet(id, out var s)) Mark(shapes, e, s, color, server: false);
        }

        // the server's view of the same things, matched by node name: its tracks, its boxes, and how far off the client was
        if (input.ServerFrames is { } sf && input.ServerEntities is { } se)
        {
            var shifted = sf.Select(f => { var g = new TraceFrame(); g.CopyFrom(f); g.T -= input.ServerShift; return g; })
                .Where(f => f.T >= input.T0 && f.T <= input.T1).ToList();
            var serverAt = Nearest(shifted, shapes.At);
            foreach (int id in chosen)
            {
                if (!input.Entities.TryGetValue(id, out var ce)) continue;
                var twin = se.Values.FirstOrDefault(x => x.Name == ce.Name && x.IsPlayer == ce.IsPlayer);
                if (twin == null) continue;
                var track = TrackOf(shifted, twin.Id, ServerColor, server: true);
                if (track.Points.Count > 1) shapes.Tracks.Add(track);
                if (serverAt == null || !serverAt.TryGet(twin.Id, out var ss)) continue;
                Mark(shapes, twin, ss, ServerColor, server: true);
                if (at != null && at.TryGet(id, out var cs))
                {
                    var a = new EN(cs.E, cs.N);
                    var b = new EN(ss.E, ss.N);
                    shapes.Offsets.Add(new TopOffset(a, b, (a - b).Length));
                }
            }
        }

        foreach (var n in input.Notes)
            if (n.T >= input.T0 - 1 && n.T <= input.T1 + 1) shapes.Pins.Add(new TopPin(new EN(n.E, n.N), n.Text));

        Fit(shapes, around, radius);
        return shapes;
    }

    private static TraceFrame? Nearest(List<TraceFrame> frames, double t)
    {
        TraceFrame? best = null;
        foreach (var f in frames)
            if (best == null || Math.Abs(f.T - t) < Math.Abs(best.T - t)) best = f;
        return best;
    }

    private static EN Centroid(IEnumerable<EN> points)
    {
        double e = 0, n = 0;
        int k = 0;
        foreach (var p in points) { e += p.E; n += p.N; k++; }
        return k == 0 ? new EN(0, 0) : new EN(e / k, n / k);
    }

    private static TopTrack TrackOf(List<TraceFrame> frames, int id, TopColor color, bool server)
    {
        var track = new TopTrack(id, color, new List<EN>(), new List<EN>(), server);
        double nextTick = double.NegativeInfinity;
        foreach (var f in frames)
        {
            if (!f.TryGet(id, out var s)) continue;
            var p = new EN(s.E, s.N);
            track.Points.Add(p);
            if (f.T >= nextTick)
            {
                track.Ticks.Add(p);
                nextTick = Math.Floor(f.T) + 1;
            }
        }
        return track;
    }

    private static void Mark(TopViewShapes shapes, TraceEntity e, in TraceState s, TopColor color, bool server)
    {
        var at = new EN(s.E, s.N);
        string label = e.Label.Length > 0 ? e.Label : e.Name;
        if (e.IsPlayer)
        {
            // the vehicle it drives, under its arrow
            if (s.RideAlong > 0) shapes.Boxes.Add(new TopBox(at, Math.Max(s.RideAcross, 0.5), s.RideAlong, s.Heading, color, "", server));
            shapes.Arrows.Add(new TopArrow(at, s.Heading, color, label, server));
        }
        else
            // a thing with no size still shows: a metre square
            shapes.Boxes.Add(new TopBox(at, Math.Max(e.Size[0], 1), Math.Max(e.Size[2], 1), s.Heading, color, label, server));
        if (!server && s.VE * s.VE + s.VN * s.VN > 0.25f)
            shapes.Vectors.Add(new TopVector(at, at + new EN(s.VE, s.VN)));   // where it is a second later
    }

    /// <summary>The square shown: the given one, else every track and mark with a margin, at least 20 m.</summary>
    private static void Fit(TopViewShapes shapes, EN? around, double? radius)
    {
        if (around is { } c && radius is { } r)
        {
            shapes.Min = new EN(c.E - r, c.N - r);
            shapes.Side = 2 * r;
            return;
        }
        var points = shapes.Tracks.Where(t => !t.Minor).SelectMany(t => t.Points)
            .Concat(shapes.Boxes.Select(b => b.Center)).Concat(shapes.Arrows.Select(a => a.At)).Concat(shapes.Pins.Select(p => p.At)).ToList();
        if (points.Count == 0)
        {
            shapes.Min = new EN(-10, -10);
            shapes.Side = 20;
            return;
        }
        double minE = points.Min(p => p.E), maxE = points.Max(p => p.E), minN = points.Min(p => p.N), maxN = points.Max(p => p.N);
        double side = Math.Max(20, Math.Max(maxE - minE, maxN - minN) * 1.15 + 8);
        shapes.Min = new EN((minE + maxE - side) / 2, (minN + maxN - side) / 2);
        shapes.Side = side;
    }

    /// <summary>A round length for the scale bar near a fifth of <paramref name="side"/>: 1, 2 or 5 times a power of ten.</summary>
    public static double ScaleBar(double side)
    {
        double target = side / 5;
        double p = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (double k in new[] { 5.0, 2.0, 1.0 })
            if (k * p <= target) return k * p;
        return p;
    }
}
