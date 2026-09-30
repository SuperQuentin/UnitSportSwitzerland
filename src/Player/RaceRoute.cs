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

    public float Length => Line.Length;

    public static async Task<RaceRoute?> BuildAsync(IChunkSource source, WorldOrigin origin, Vector3 at,
        CancellationToken ct = default)
    {
        var (e, n) = origin.ToLv95(at);
        var here = TileId.FromLv95(e, n);
        var tiles = new List<RoadTile>();
        for (int de = -4; de <= 4; de++)
            for (int dn = -4; dn <= 4; dn++)
            {
                try { if (await source.LoadRoadsAsync(new TileId(here.E + de, here.N + dn), ct) is { } t) tiles.Add(t); }
                catch (System.Exception) { /* no roads there */ }
            }
        var graph = LaneGraph.Build(tiles, origin, s =>
            s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Major or RoadClass.Road or RoadClass.Minor
            && (s.Flags & (RoadFlags.Stairs | RoadFlags.Tunnel)) == 0);

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
        var route = FromPoints(pts.Select(p => p.P).ToList(), pts.Select(p => p.W).ToList(), best.Class);
        foreach (var (p, w) in back.Take(300)) { route.Behind.Add(p); route.BehindWidth.Add(w); }   // 600 m is plenty
        return route;
    }

    /// <summary>A route from a centreline already known (sent by the race server, say).</summary>
    public static RaceRoute FromPoints(IReadOnlyList<Vector3> centre, IReadOnlyList<float> width, RoadClass cls = RoadClass.Road)
    {
        var r = new RaceRoute { Class = cls };
        float s = 0;
        for (int i = 0; i < centre.Count; i++)
        {
            if (i > 0) s += centre[i].DistanceTo(centre[i - 1]);
            r.Centre.Add(centre[i]);
            r.Arc.Add(s);
            r.Width.Add(width[i]);
        }
        r.Line = RaceLine.Build(centre, width, 0.9f);
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
