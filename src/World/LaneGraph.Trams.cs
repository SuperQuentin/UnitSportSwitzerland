using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The tram graph (#1057): the tram tracks of the tiles (<see cref="RoadFlags.Tramway"/>, left out of the rails), each track the way
/// its trams run (<see cref="TramTracks.Directions"/>: TLM maps a line's tracks one by one, the trams keep right), on its own
/// line; a line mapped as one with two tracks (<see cref="RoadFlags.DoubleTrack"/>) both ways, a tram on its right track as a train.
/// Through a junction core RoadGen laid tram paths on (<c>JPTH</c> kind <see cref="JunctionPathKind.Tram"/>), the tracks are cut
/// where the paths begin and end and the trams drive the paths there instead, like the cars theirs: at lights each path carries its
/// tram group and how far before it its trams stop.
/// </summary>
public sealed partial class LaneGraph
{
    /// <summary>The tram graph's tracks laid in a carriageway (#124 <c>Embedded</c>): its trams share the cars' lanes there.</summary>
    private readonly HashSet<LaneEdge> _inStreet = new();

    /// <summary>Whether a tram on <paramref name="edge"/> runs in a car lane (street running): the cars behind it follow it.</summary>
    public bool InStreet(LaneEdge edge) => _inStreet.Contains(edge);

    /// <summary>Per tram path through a lit junction (#1057): how far before its start its tram stop line lies (m).</summary>
    private readonly Dictionary<LaneEdge, float> _tramStopBack = new();

    /// <summary>How far before the start of tram path <paramref name="path"/> its trams stop at red (m); 0 without a record.</summary>
    public float TramStopBack(LaneEdge path) => _tramStopBack.TryGetValue(path, out float back) ? back : 0f;

    /// <summary>The tram paths (#1057) laid through junction cores in place of their tracks.</summary>
    public int TramPaths { get; private set; }

    /// <summary>A tram path's end lies on its track's line within this (m): the track is cut there.</summary>
    private const float TramCutReach = 0.75f;

    /// <summary>A tram stop line lies at most this far before its path (m): its approach's cars' line.</summary>
    private const float TramStopReach = 25f;

    /// <summary>Whether a segment is a tram track the trams run on (not out of service).</summary>
    public static bool IsTramTrack(RoadSegment s) =>
        s.Class == RoadClass.Railway && (s.Flags & RoadFlags.Tramway) != 0 && (s.Flags & (RoadFlags.Disused | RoadFlags.Funicular)) == 0;

    /// <summary>
    /// Builds the tram graph from <paramref name="tiles"/> in <paramref name="origin"/>'s frame (on a worker thread, as
    /// <see cref="Build"/>): every tram track, oriented the way its trams run, cut where a junction's tram paths take over.
    /// </summary>
    public static LaneGraph BuildTrams(IReadOnlyList<RoadTile> tiles, OriginFrame origin)
    {
        var g = new LaneGraph(origin);
        var lines = new List<(double E, double N)[]>();
        var doubles = new List<bool>();
        foreach (var tile in tiles)
            foreach (var seg in tile.Segments)
            {
                if (seg.PointCount < 2 || !IsTramTrack(seg)) continue;
                var pts = new Vector3[seg.PointCount];
                var cum = new float[seg.PointCount];
                var plan = new (double E, double N)[seg.PointCount];
                // (a tram is a box standing on its track's line: on an embedded track the road's surface, no rail sink as a train's)
                for (int i = 0; i < pts.Length; i++)
                {
                    plan[i] = seg.Lv95(tile.Id, i);
                    pts[i] = origin.ToWorld(plan[i].E, plan[i].N, seg.Points[i * 3 + 1]);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                }
                if (cum[^1] < 0.5f) continue;
                var edge = new LaneEdge
                {
                    Points = pts, Cumulative = cum, Class = seg.Class, Flags = seg.Flags, Kind = MoverKind.Tram, Width = seg.Width,
                    KeyStart = Key(plan[0].E, plan[0].N), KeyEnd = Key(plan[^1].E, plan[^1].N),
                };
                g.Edges.Add(edge);
                if (seg.Attributes.Has(RoadAttrFlags.Embedded)) g._inStreet.Add(edge);
                lines.Add(plan);
                doubles.Add((seg.Flags & RoadFlags.DoubleTrack) != 0);
            }
        var ways = TramTracks.Directions(lines, doubles);
        for (int i = 0; i < g.Edges.Count; i++) g.Edges[i].OneWay = ways[i];
        g.SpliceTramPaths(tiles, origin);
        foreach (var e in g.Edges)
        {
            g.Link(e.KeyStart, e, true);
            g.Link(e.KeyEnd, e, false);
        }
        return g;
    }

    /// <summary>
    /// The tiles' tram paths (#1057) in place of their tracks through the cores: each track cut where a path begins or ends on it,
    /// the piece between two ends of one core's paths dropped; each path an edge from its start to its end, with its lights.
    /// </summary>
    private void SpliceTramPaths(IReadOnlyList<RoadTile> tiles, OriginFrame origin)
    {
        var paths = new List<LaneEdge>();
        var cuts = new List<(Vector3 At, long Key, RoadJunction Cap)>();
        foreach (var tile in tiles)
        {
            if (!tile.JunctionPaths.Any(p => p.Kind == JunctionPathKind.Tram)) continue;
            Vector3 World(float x, float y, float z) => origin.ToWorld(tile.Id.MinE + x, tile.Id.MaxN - z, y);
            // the lights as the trams see them (the plans the lamps and the cars read: the same objects)
            var sites = tile.Signals.Select(s => new SignalSite { Plan = s.Plan, Out = s.Plan.Arms.Select(a => OutOf(a.Heading)).ToArray() }).ToArray();
            foreach (var jp in tile.JunctionPaths)
            {
                if (jp.Kind != JunctionPathKind.Tram || jp.PointCount < 2) continue;
                int n = jp.PointCount;
                var pts = new Vector3[n];
                var cum = new float[n];
                for (int i = 0; i < n; i++)
                {
                    pts[i] = World(jp.Points[i * 3], jp.Points[i * 3 + 1], jp.Points[i * 3 + 2]);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                }
                if (cum[^1] < 0.5f) continue;
                var site = jp.Signal >= 0 && jp.Signal < sites.Length ? sites[jp.Signal] : null;
                long from = Key(tile.Id.MinE + jp.From.X, tile.Id.MaxN - jp.From.Z), to = Key(tile.Id.MinE + jp.To.X, tile.Id.MaxN - jp.To.Z);
                var edge = new LaneEdge
                {
                    Points = pts, Cumulative = cum, Class = RoadClass.Railway, Flags = RoadFlags.Tramway, Kind = MoverKind.Tram,
                    Width = jp.Width, OneWay = 1, Connector = true, KeyStart = from, KeyEnd = to,
                    Path = new JunctionPathInfo(jp.Move, jp.From.Offset, jp.To.Offset, site, site is null ? -1 : jp.Group),
                };
                paths.Add(edge);
                cuts.Add((World(jp.From.X, jp.From.Y, jp.From.Z), from, jp.Junction));
                cuts.Add((World(jp.To.X, jp.To.Y, jp.To.Z), to, jp.Junction));
                // its stop line: its approach's (the LANE record with a tram lane facing the way the path starts), before its start
                if (site is null) continue;
                var start = MathX.Flat(pts[1] - pts[0]).Normalized();
                foreach (var r in tile.Approaches)
                {
                    if (r.Signal != jp.Signal || !r.Lanes.Any(l => l.Kind == ApproachLaneKind.Tram)) continue;
                    var into = -OutOf(r.Heading);
                    if (into.Dot(start) < 0.7f) continue;
                    float back = (World(jp.From.X, jp.From.Y, jp.From.Z) - World(r.X, r.Y, r.Z)).Dot(into);
                    if (back >= 0f && back < TramStopReach) { _tramStopBack[edge] = back; break; }
                }
            }
        }
        if (paths.Count == 0) return;
        // the tracks cut at every path end on them
        foreach (var (at, key, _) in cuts)
            for (int i = 0; i < Edges.Count; i++)
            {
                var e = Edges[i];
                if (TramCut(e, at) is not { } s) continue;
                if (s < 0.05f || s > e.Length - 0.05f)
                {
                    // at an end already: that end is the path's
                    Edges[i] = Rekey(e, s < 0.05f ? key : e.KeyStart, s < 0.05f ? e.KeyEnd : key);
                    continue;
                }
                var (a, b) = Cut(e, s, key);
                Edges[i] = a;
                Edges.Add(b);
            }
        // the track between two ends of one core's paths lies under them
        var ends = cuts.GroupBy(c => c.Cap, ReferenceEqualityComparer.Instance).Select(gr => gr.Select(c => c.Key).ToHashSet()).ToList();
        Edges.RemoveAll(e => ends.Any(set => set.Contains(e.KeyStart) && set.Contains(e.KeyEnd)));
        Edges.AddRange(paths);
        TramPaths = paths.Count;
    }

    /// <summary>Where <paramref name="at"/> lies along a track (m), if within <see cref="TramCutReach"/> of it; null if not.</summary>
    private static float? TramCut(LaneEdge e, Vector3 at)
    {
        float best = TramCutReach, result = -1f;
        for (int k = 0; k + 1 < e.Points.Length; k++)
        {
            var a = MathX.Flat(e.Points[k]);
            var ab = MathX.Flat(e.Points[k + 1]) - a;
            float l2 = ab.LengthSquared();
            float t = l2 < 1e-8f ? 0f : Mathf.Clamp((MathX.Flat(at) - a).Dot(ab) / l2, 0f, 1f);
            float d = (MathX.Flat(at) - (a + ab * t)).Length();
            if (d < best) (best, result) = (d, e.Cumulative[k] + t * (e.Cumulative[k + 1] - e.Cumulative[k]));
        }
        return result < 0f ? null : result;
    }

    /// <summary>A track split at <paramref name="s"/> m into its two pieces, meeting at <paramref name="key"/>; its kind, way and street running kept.</summary>
    private (LaneEdge, LaneEdge) Cut(LaneEdge e, float s, long key)
    {
        var (mid, _) = e.Sample(s);
        int k = 1;
        while (k < e.Points.Length - 1 && e.Cumulative[k] <= s) k++;
        var first = e.Points.Take(k).Append(mid).ToArray();
        var second = e.Points.Skip(k).Prepend(mid).ToArray();
        var a = Piece(e, first, e.KeyStart, key);
        var b = Piece(e, second, key, e.KeyEnd);
        if (_inStreet.Remove(e)) { _inStreet.Add(a); _inStreet.Add(b); }
        return (a, b);
    }

    private LaneEdge Rekey(LaneEdge e, long start, long end)
    {
        var r = Piece(e, e.Points, start, end);
        if (_inStreet.Remove(e)) _inStreet.Add(r);
        return r;
    }

    private static LaneEdge Piece(LaneEdge e, Vector3[] points, long start, long end)
    {
        var cum = new float[points.Length];
        for (int i = 1; i < points.Length; i++) cum[i] = cum[i - 1] + points[i].DistanceTo(points[i - 1]);
        return new LaneEdge
        {
            Points = points, Cumulative = cum, Class = e.Class, Flags = e.Flags, Kind = e.Kind, Width = e.Width,
            KeyStart = start, KeyEnd = end, OneWay = e.OneWay,
        };
    }

    /// <summary>
    /// The points where cars on a junction without lights give way to its trams (#1057; trams have priority, SVG Art. 38): every car
    /// path of a core that a tram path of the same core crosses, where the car's body first comes into the tram's band.
    /// </summary>
    private static void TramYields(RoadTile tile, List<(RoadJunctionPath Path, LaneEdge Edge)> cars)
    {
        if (cars.Count == 0 || !tile.JunctionPaths.Any(p => p.Kind == JunctionPathKind.Tram)) return;
        foreach (var (car, edge) in cars)
        {
            if (edge.Path is not { } info) continue;
            var mine = new ConflictWay(car.Points, -1, ConflictMover.Car, car.Move, car.Width * 0.5f);
            var yields = new List<PathYield>(info.Yields);
            foreach (var tram in tile.JunctionPaths)
            {
                if (tram.Kind != JunctionPathKind.Tram || !ReferenceEquals(tram.Junction, car.Junction) || tram.PointCount < 2) continue;
                var way = new ConflictWay(tram.Points, -1, ConflictMover.Tram, tram.Move, tram.Width * 0.5f);
                if (ConflictPoints.Meet(mine, ConflictPoints.CarBody, way, ConflictPoints.TramBody) is var (at, outAt, enter))
                    yields.Add(new PathYield(at, outAt, -1, ConflictMover.Tram, null, enter));
            }
            if (yields.Count == info.Yields.Length) continue;
            yields.Sort((a, b) => a.At.CompareTo(b.At));
            info.Yields = yields.ToArray();
        }
    }
}
