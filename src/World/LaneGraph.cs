using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>One road or rail centreline in world space, ready to be driven along.</summary>
public sealed class LaneEdge
{
    public required Vector3[] Points { get; init; }
    /// <summary>Cumulative length to each point; <c>[^1]</c> is the whole edge.</summary>
    public required float[] Cumulative { get; init; }
    public RoadClass Class { get; init; }
    public RoadFlags Flags { get; init; }
    public float Width { get; init; }
    public long KeyStart { get; init; }
    public long KeyEnd { get; init; }

    /// <summary>
    /// Which way traffic may use it: +1 only in drawing order, −1 only against it, 0 both.
    /// Read from the tile (v3 <see cref="RoadAttributes.OneWay"/>); a divided carriageway the
    /// tile gives no direction (v1/v2) gets one from where its partner lies (see
    /// <see cref="LaneGraph.OrientDivided"/>).
    /// </summary>
    public int OneWay { get; set; }

    /// <summary>
    /// Metres right of the centreline, in the direction of travel, of the rightmost lane when the
    /// edge is one-way (<see cref="RoadCrossSection.RightLaneOffset"/>, #117); 0 for a v1/v2 tile's
    /// narrow carriageway.
    /// </summary>
    public float RightLane { get; init; }

    /// <summary>
    /// Traffic leaving this edge at its first (<see cref="RoadAttrFlags.YieldAtStart"/>) or last
    /// point (<see cref="RoadAttrFlags.YieldAtEnd"/>) gives way there: the side road of a junction
    /// with a main road (#121, v3 tiles; none in v1/v2).
    /// </summary>
    public RoadAttrFlags Yield { get; init; }

    public float Length => Cumulative[^1];

    /// <summary>Position and unit tangent at arc length <paramref name="s"/> along the drawing order.</summary>
    public (Vector3 Pos, Vector3 Tangent) Sample(float s)
    {
        int n = Points.Length;
        if (s <= 0) return (Points[0], Dir(0));
        if (s >= Length) return (Points[n - 1], Dir(n - 2));
        int lo = 0, hi = n - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (Cumulative[mid] <= s) lo = mid; else hi = mid;
        }
        float span = Cumulative[hi] - Cumulative[lo];
        float t = span > 1e-5f ? (s - Cumulative[lo]) / span : 0f;
        return (Points[lo].Lerp(Points[hi], t), Dir(lo));
    }

    private Vector3 Dir(int i)
    {
        var d = Points[i + 1] - Points[i];
        return d.LengthSquared() > 1e-8f ? d.Normalized() : Vector3.Forward;
    }
}

/// <summary>
/// The drivable roads (or the railway) around the player, stitched into a graph traffic can
/// follow. Built from the same <c>.road</c> tiles the renderer draws, straight into world
/// coordinates, with endpoints snapped onto a half-metre lattice so a line cut at a kilometre
/// seam carries on into the next tile instead of ending there.
///
/// <para>
/// Immutable once built. The traffic manager rebuilds it as the player moves and simply swaps
/// the reference; a vehicle keeps the edge it is on and finds its next one by the endpoint key,
/// which means the same junction in any rebuild.
/// </para>
/// </summary>
public sealed class LaneGraph
{
    public List<LaneEdge> Edges { get; } = new();
    private readonly Dictionary<long, List<(LaneEdge Edge, bool AtStart)>> _incident = new();

    private const float Snap = 0.5f;

    /// <summary>RoadMeshBuilder's raised rail height (0.18) less the paint lift (0.02): RoadGen's RailRoadOverlap.RailTop.</summary>
    private const float EmbeddedRailSink = 0.16f;

    public static long KeyOf(Vector3 p) =>
        ((long)Mathf.RoundToInt(p.X / Snap) << 32) ^ (uint)Mathf.RoundToInt(p.Z / Snap);

    public static LaneGraph Build(IEnumerable<RoadTile> tiles, WorldOrigin origin, Func<RoadSegment, bool> keep)
    {
        var g = new LaneGraph();
        foreach (var tile in tiles)
            foreach (var seg in tile.Segments)
            {
                if (seg.PointCount < 2 || !keep(seg)) continue;
                var pts = new Vector3[seg.PointCount];
                var cum = new float[seg.PointCount];
                // a rail embedded in a road (#124) lies at the road's height and its rail head is
                // the groove paint, not the raised rail a train's lift is measured from
                float sink = seg.Attributes.Has(RoadAttrFlags.Embedded) ? EmbeddedRailSink : 0f;
                for (int i = 0; i < pts.Length; i++)
                {
                    double e = tile.Id.MinE + seg.Points[i * 3];
                    double n = tile.Id.MaxN - seg.Points[i * 3 + 2];
                    pts[i] = origin.ToWorld(e, n, seg.Points[i * 3 + 1] - sink);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                }
                if (cum[^1] < 1f) continue;

                var edge = new LaneEdge
                {
                    Points = pts, Cumulative = cum, Class = seg.Class, Flags = seg.Flags,
                    Width = seg.Width, KeyStart = KeyOf(pts[0]), KeyEnd = KeyOf(pts[^1]),
                    OneWay = seg.Attributes.OneWay,
                    Yield = seg.Attributes.Flags & (RoadAttrFlags.YieldAtStart | RoadAttrFlags.YieldAtEnd),
                    RightLane = RoadCrossSection.RightLaneOffset(seg.Class, seg.Width,
                        Math.Max(seg.Attributes.LanesForward, seg.Attributes.LanesBackward)),
                };
                g.Edges.Add(edge);
                g.Link(edge.KeyStart, edge, true);
                g.Link(edge.KeyEnd, edge, false);
            }
        g.OrientDivided();
        return g;
    }

    private void Link(long key, LaneEdge edge, bool atStart)
    {
        if (!_incident.TryGetValue(key, out var list)) _incident[key] = list = new();
        list.Add((edge, atStart));
    }

    /// <summary>Everything leaving a junction that may be driven away from it.</summary>
    public IEnumerable<(LaneEdge Edge, bool Forward)> Leaving(long key)
    {
        if (!_incident.TryGetValue(key, out var list)) yield break;
        foreach (var (edge, atStart) in list)
        {
            bool forward = atStart;   // leaving from its start means driving in drawing order
            if (edge.OneWay == 1 && !forward) continue;
            if (edge.OneWay == -1 && forward) continue;
            yield return (edge, forward);
        }
    }

    /// <summary>
    /// swissTLM3D draws a divided road as two centrelines and records nothing about which way
    /// each carries traffic. Switzerland drives on the right, so each carriageway runs with the
    /// other one on its LEFT: find the partner beside the middle of each line and read the
    /// direction off which side it is on. Guessing instead sends half of all motorway traffic
    /// the wrong way down its carriageway.
    /// </summary>
    private void OrientDivided()
    {
        const float Cell = 30f;
        var grid = new Dictionary<long, List<(LaneEdge Edge, Vector3 P)>>();
        long CellKey(Vector3 p) => ((long)Mathf.FloorToInt(p.X / Cell) << 32) ^ (uint)Mathf.FloorToInt(p.Z / Cell);

        var divided = Edges.Where(e => (e.Flags & RoadFlags.Divided) != 0).ToList();
        foreach (var e in divided)
            foreach (var p in e.Points)
            {
                long k = CellKey(p);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                l.Add((e, p));
            }

        foreach (var e in divided)
        {
            if (e.OneWay != 0) continue;   // stored in the tile (v3): the build already decided
            var (mid, t) = e.Sample(e.Length * 0.5f);
            var right = new Vector3(-t.Z, 0, t.X).Normalized();
            float votes = 0;
            int cx = Mathf.FloorToInt(mid.X / Cell), cz = Mathf.FloorToInt(mid.Z / Cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long k = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (!grid.TryGetValue(k, out var l)) continue;
                    foreach (var (other, p) in l)
                    {
                        if (other == e) continue;
                        var d = p - mid;
                        float along = Mathf.Abs(d.Dot(t)), side = d.Dot(right);
                        // beside it, a carriageway's width away, not ahead on the same line
                        if (along > 25f || Mathf.Abs(side) < 3f || Mathf.Abs(side) > 30f) continue;
                        votes += side > 0 ? -1 : 1;   // partner on the right: we run against the drawing
                    }
                }
            e.OneWay = votes > 0 ? 1 : votes < 0 ? -1 : 0;
        }
    }

    /// <summary>A random edge with a point inside the given distance band from <paramref name="around"/>.</summary>
    public (LaneEdge Edge, float Arc)? RandomSpot(Random rng, Vector3 around, float minDist, float maxDist,
        Func<LaneEdge, float> weight)
    {
        if (Edges.Count == 0) return null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var e = Edges[rng.Next(Edges.Count)];
            if (rng.NextDouble() > weight(e)) continue;
            float s = (float)rng.NextDouble() * e.Length;
            var (p, _) = e.Sample(s);
            float d = new Vector2(p.X - around.X, p.Z - around.Z).Length();
            if (d >= minDist && d <= maxDist) return (e, s);
        }
        return null;
    }
}
