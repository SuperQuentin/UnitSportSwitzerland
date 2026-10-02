using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// The road a race runs on: the main road from a point — the nearest drivable edge, then at every
/// junction the edge of the same class or better that carries on straightest, both ways, keeping
/// the longer run — its centreline and width (what "on the tarmac" means), and the
/// <see cref="RaceLine"/> on it. Built off the main thread from the <c>.road</c> tiles.
/// </summary>
public sealed class RaceRoute
{
    public readonly List<Vector3> Centre = new();
    public readonly List<float> Arc = new();
    public readonly List<float> Width = new();
    public RaceLine Line = null!;
    public RoadClass Class;
    /// <summary>
    /// The road behind the start, from the start outward (the other way from the host, the walk
    /// not kept): where a race NPC arriving "from behind" comes up from (<c>World/NpcArrival</c>).
    /// Server-built routes only; empty on one rebuilt from points.
    /// </summary>
    public readonly List<Vector3> Behind = new();
    public readonly List<float> BehindWidth = new();

    /// <summary>
    /// The frame <see cref="Centre"/>, <see cref="Behind"/> and the line are in (#185): the origin
    /// they were built or received in, or a race's own frame on the server. Null: not known yet,
    /// taken to be the first frame <see cref="Follow"/> is given.
    /// </summary>
    public OriginFrame? Frame { get; private set; }

    /// <summary>
    /// Moves every point into <paramref name="now"/>: the origin moved (#185). Whoever holds the
    /// route calls it from its shift handler; a route held by several (a race's runner, its pilot,
    /// an NPC's driver) moves once, on the first call.
    /// </summary>
    public void Follow(OriginFrame now)
    {
        if (Frame is { } was && !(was.E == now.E && was.N == now.N))
        {
            var shift = now.Since(was);
            for (int i = 0; i < Centre.Count; i++) Centre[i] = shift.Point(Centre[i]);
            for (int i = 0; i < Behind.Count; i++) Behind[i] = shift.Point(Behind[i]);
        }
        Frame = now;
        Line?.Follow(now);
    }

    public float Length => Line.Length;

    /// <param name="smallest">The narrowest class of road it may take (a truck's probe keeps to Road and wider).</param>
    /// <param name="toward">A point to drive to: the shortest way there by road, and on past it (default: the longer of the two ways along the road).</param>
    public static Task<RaceRoute?> BuildAsync(IChunkSource source, WorldOrigin origin, Vector3 at,
        CancellationToken ct = default, RoadClass smallest = RoadClass.Minor, Vector3? toward = null) =>
        // one origin frame for the whole build, which runs off the main thread (#185)
        BuildAsync(source, origin.Frame, at, ct, smallest, toward);

    /// <summary>The route from <paramref name="at"/>, a point in <paramref name="frame"/>, which the route is in too (and <paramref name="toward"/>).</summary>
    public static async Task<RaceRoute?> BuildAsync(IChunkSource source, OriginFrame frame, Vector3 at,
        CancellationToken ct = default, RoadClass smallest = RoadClass.Minor, Vector3? toward = null)
    {
        var (e, n) = frame.ToLv95(at);
        var here = TileId.FromLv95(e, n);
        var tiles = new List<RoadTile>();
        for (int de = -4; de <= 4; de++)
            for (int dn = -4; dn <= 4; dn++)
            {
                try { if (await source.LoadRoadsAsync(new TileId(here.E + de, here.N + dn), ct) is { } t) tiles.Add(t); }
                catch (System.Exception) { /* no roads there */ }
            }
        var graph = LaneGraph.Build(tiles, frame, s =>
            s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Major or RoadClass.Road or RoadClass.Minor
            && s.Class <= smallest && (s.Flags & (RoadFlags.Stairs | RoadFlags.Tunnel)) == 0);

        LaneEdge? best = null;
        float bestD = float.MaxValue, bestS = 0;
        foreach (var edge in graph.Edges)
            for (int i = 0; i < edge.Points.Length; i++)
            {
                // prefer the bigger road when two are close: a farm track beside the pass is not the pass
                float d = Flat(edge.Points[i] - at).Length() + (int)edge.Class * 4f;
                if (d < bestD) { bestD = d; best = edge; bestS = edge.Cumulative[i]; }
            }
        if (best == null) return null;

        var a = Walk(graph, best, true, bestS);
        var b = Walk(graph, best, false, best.Length - bestS);
        var (pts, back) = a.Count >= b.Count ? (a, b) : (b, a);
        if (toward is { } to && Towards(graph, best, bestS, to) is { } path)
        {
            pts = path;
            back = Flat(a[Mathf.Min(5, a.Count - 1)].P - path[Mathf.Min(5, path.Count - 1)].P).Length() < 1f ? b : a;
        }
        var route = FromPoints(pts.Select(p => p.P).ToList(), pts.Select(p => p.W).ToList(), best.Class, frame);
        foreach (var (p, w) in back.Take(300)) { route.Behind.Add(p); route.BehindWidth.Add(w); }   // 600 m is plenty
        return route;
    }

    /// <summary>A route from a centreline already known (sent by the race server, say), in <paramref name="frame"/>.</summary>
    public static RaceRoute FromPoints(IReadOnlyList<Vector3> centre, IReadOnlyList<float> width, RoadClass cls = RoadClass.Road,
        OriginFrame? frame = null)
    {
        var r = new RaceRoute { Class = cls, Frame = frame };
        float s = 0;
        for (int i = 0; i < centre.Count; i++)
        {
            if (i > 0) s += centre[i].DistanceTo(centre[i - 1]);
            r.Centre.Add(centre[i]);
            r.Arc.Add(s);
            r.Width.Add(width[i]);
        }
        r.Line = RaceLine.Build(centre, width, 0.9f);
        r.Line.Frame = frame;
        return r;
    }

    private static List<(Vector3 P, float W)> Walk(LaneGraph graph, LaneEdge edge, bool forward, float from)
    {
        var pts = new List<(Vector3, float)>();
        const float Step = 2f, MaxLength = 8000f;
        float total = 0;
        var visited = new HashSet<LaneEdge>();
        while (total < MaxLength && visited.Add(edge))
        {
            for (float s = from; s <= edge.Length; s += Step)
            {
                var (p, _) = edge.Sample(forward ? s : edge.Length - s);
                pts.Add((p, edge.Width));
                total += Step;
            }
            // leave the far end: the next edge of this class or better that turns least
            var (endP, endT) = edge.Sample(forward ? edge.Length : 0);
            var dirIn = forward ? endT : -endT;
            long key = forward ? edge.KeyEnd : edge.KeyStart;
            LaneEdge? next = null;
            bool nextFwd = true;
            float bestScore = -2f;
            // RoadGen --rewrite trims every road back from its junction polygon, so after it the
            // roads meeting at a junction no longer share an endpoint: take any edge that starts
            // or ends within a junction's reach of where this one stopped
            foreach (var (cand, candFwd) in graph.Leaving(key).Concat(NearbyStarts(graph, endP)))
            {
                if (cand == edge || visited.Contains(cand) || cand.Class > edge.Class) continue;
                var (_, t) = cand.Sample(candFwd ? 1f : cand.Length - 1f);
                float score = (candFwd ? t : -t).Dot(dirIn);
                if (score > bestScore) { bestScore = score; next = cand; nextFwd = candFwd; }
            }
            if (next == null || bestScore < -0.2f) break;
            edge = next;
            forward = nextFwd;
            from = 0;
        }
        return pts;
    }

    /// <summary>
    /// The shortest way by road from <paramref name="start"/> (at <paramref name="s"/> m along it) to the edge passing
    /// nearest <paramref name="to"/>, then on from there as <see cref="Walk"/> goes (a run-out past the finish). For a
    /// course between two given points: the straightest-road walk turned off at the first col.
    /// </summary>
    private static List<(Vector3 P, float W)>? Towards(LaneGraph graph, LaneEdge start, float s, Vector3 to)
    {
        var goal = graph.Edges.MinBy(e => e.Points.Min(p => Flat(p - to).LengthSquared()))!;
        var cost = new Dictionary<(LaneEdge, bool), float>();
        var from = new Dictionary<(LaneEdge, bool), (LaneEdge, bool)>();
        var open = new PriorityQueue<(LaneEdge E, bool F), float>();
        foreach (bool f in new[] { true, false })
        {
            float c = f ? start.Length - s : s;
            cost[(start, f)] = c;
            open.Enqueue((start, f), c);
        }
        (LaneEdge E, bool F)? hit = null;
        while (open.TryDequeue(out var cur, out float c))
        {
            if (c > cost[cur]) continue;
            if (cur.E == goal) { hit = cur; break; }
            var (endP, _) = cur.E.Sample(cur.F ? cur.E.Length : 0);
            long key = cur.F ? cur.E.KeyEnd : cur.E.KeyStart;
            foreach (var next in graph.Leaving(key).Concat(NearbyStarts(graph, endP)))
            {
                if (next.Item1 == cur.E) continue;
                float nc = c + next.Item1.Length;
                if (cost.TryGetValue(next, out float old) && old <= nc) continue;
                cost[next] = nc;
                from[next] = cur;
                open.Enqueue(next, nc);
            }
        }
        if (hit is not { } last) return null;
        var chain = new List<(LaneEdge E, bool F)> { last };
        while (from.TryGetValue(chain[^1], out var prev)) chain.Add(prev);
        chain.Reverse();
        var pts = new List<(Vector3, float)>();
        for (int k = 0; k < chain.Count - 1; k++)
        {
            var (e, f) = chain[k];
            for (float d = k == 0 ? (f ? s : e.Length - s) : 0f; d <= e.Length; d += 2f)
                pts.Add((e.Sample(f ? d : e.Length - d).Item1, e.Width));
        }
        pts.AddRange(Walk(graph, last.E, last.F, chain.Count == 1 ? (last.F ? s : last.E.Length - s) : 0f));
        return pts;
    }

    private static IEnumerable<(LaneEdge, bool)> NearbyStarts(LaneGraph graph, Vector3 at)
    {
        const float Reach = 18f;
        foreach (var e in graph.Edges)
        {
            if (e.OneWay != -1 && Flat(e.Points[0] - at).Length() < Reach) yield return (e, true);
            if (e.OneWay != 1 && Flat(e.Points[^1] - at).Length() < Reach) yield return (e, false);
        }
    }

    /// <summary>Distance from the road's centreline, searched near the closest point.</summary>
    public float Off(Vector3 pos)
    {
        int near = NearestCentre(pos);
        float off = float.MaxValue;
        for (int i = Mathf.Max(0, near - 3); i < Mathf.Min(Centre.Count - 1, near + 3); i++)
            off = Mathf.Min(off, DistanceToSegment(Flat(pos), Flat(Centre[i]), Flat(Centre[i + 1])));
        return off;
    }

    public float HalfWidthAt(Vector3 pos) => Width[NearestCentre(pos)] * 0.5f;

    /// <summary>The centreline index at arc length s (clamped).</summary>
    public int NearestCentreIndexAt(float s)
    {
        int i = Arc.BinarySearch(s);
        if (i < 0) i = ~i;
        return Mathf.Clamp(i, 0, Centre.Count - 1);
    }

    /// <summary>Nearest centreline point: a coarse stride over the 2 m polyline, then a fine search around it.</summary>
    public int NearestCentre(Vector3 pos)
    {
        int i = 0;
        float best = float.MaxValue;
        for (int k = 0; k < Centre.Count; k += 16)
        {
            float d = Flat(Centre[k] - pos).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        int c = i;
        for (int k = Mathf.Max(0, c - 16); k < Mathf.Min(Centre.Count, c + 16); k++)
        {
            float d = Flat(Centre[k] - pos).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        return i;
    }

    public static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Angle from a to b about +Y, radians; + is anticlockwise from above (to the left).</summary>
    public static float SignedAngle(Vector3 a, Vector3 b) =>
        Mathf.Atan2(a.Z * b.X - a.X * b.Z, a.X * b.X + a.Z * b.Z);

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return p.DistanceTo(a + ab * t);
    }
}
