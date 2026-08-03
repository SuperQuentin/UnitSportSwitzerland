using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx;

/// <summary>One road centreline in LV95, with cumulative lengths so a position along it is cheap.</summary>
public sealed class RoadEdge
{
    public required double[] E { get; init; }
    public required double[] N { get; init; }

    /// <summary>Cumulative length to each vertex; <c>[^1]</c> is the whole edge.</summary>
    public required double[] Cumulative { get; init; }

    public RoadClass Class { get; init; }
    public RoadFlags Flags { get; init; }

    /// <summary>Graph nodes at the two ends.</summary>
    public int NodeA { get; init; }
    public int NodeB { get; init; }

    public double Length => Cumulative[^1];

    /// <summary>Position at an arc length along the edge.</summary>
    public (double E, double N) At(double arc)
    {
        if (arc <= 0) return (E[0], N[0]);
        if (arc >= Length) return (E[^1], N[^1]);

        int lo = 0, hi = Cumulative.Length - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (Cumulative[mid] <= arc) lo = mid; else hi = mid;
        }

        double span = Cumulative[hi] - Cumulative[lo];
        double t = span > 1e-9 ? (arc - Cumulative[lo]) / span : 0;
        return (E[lo] + (E[hi] - E[lo]) * t, N[lo] + (N[hi] - N[lo]) * t);
    }
}

/// <summary>A point projected onto a road: which edge, how far along, and how far off it was.</summary>
public readonly record struct RoadHit(int Edge, double Arc, double Distance, double E, double N);

/// <summary>
/// The road network around a track, as a graph you can measure routes through.
///
/// <para>
/// Built at runtime from the same <c>.road</c> tiles the renderer draws, because the alternative
/// — a second, preprocessed routing graph — is another artefact to keep in step with the first.
/// The tiles are already indexed by kilometre, already clipped, and already in memory-friendly
/// form; all this adds is stitching them back together across tile seams and indexing them for
/// "what is near here".
/// </para>
///
/// <para>
/// Segments are stored per tile and clipped at its boundary, so a road crossing a seam arrives as
/// two features with coincident endpoints. Snapping endpoints onto a half-metre lattice is what
/// rejoins them — without it every kilometre boundary is a dead end and no route crosses one.
/// </para>
/// </summary>
public sealed class RoadNetwork
{
    private readonly List<RoadEdge> _edges = new();
    private readonly List<List<(int Node, double Cost)>> _adjacency = new();
    private readonly Dictionary<long, int> _nodes = new();

    /// <summary>Uniform grid of segment references, for nearest-road queries.</summary>
    private readonly Dictionary<long, List<(int Edge, int Vertex)>> _cells = new();
    private const double CellSize = 50.0;

    /// <summary>Endpoints within this distance are the same junction. Half the lattice spacing.</summary>
    private const double NodeSnap = 0.5;

    public IReadOnlyList<RoadEdge> Edges => _edges;
    public int NodeCount => _adjacency.Count;

    /// <summary>
    /// Whether a GPS track could plausibly have been recorded on this kind of feature.
    ///
    /// <para>
    /// The <c>.road</c> file carries far more than roads — cable cars, rivers, avalanche
    /// barriers and dry-stone walls all live in it. Matching a run onto a wall or a watercourse
    /// is not a near miss, it is nonsense, and those classes are often the closest line to a
    /// track that runs beside a river. Railways are excluded for the same reason: they parallel
    /// valley roads for kilometres and would happily capture a ride.
    /// </para>
    /// </summary>
    public static bool IsTravellable(RoadClass c) => c switch
    {
        RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major
            or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Track
            or RoadClass.Path or RoadClass.Link or RoadClass.Square => true,
        _ => false,
    };

    /// <summary>
    /// Loads every tile the bounding box touches, padded so a track running along a tile edge
    /// still sees the roads on the far side.
    /// </summary>
    public static async Task<RoadNetwork> LoadAsync(IChunkSource source,
        double minE, double minN, double maxE, double maxN, double pad = 200,
        CancellationToken ct = default)
    {
        var network = new RoadNetwork();

        var from = TileId.FromLv95(minE - pad, minN - pad);
        var to = TileId.FromLv95(maxE + pad, maxN + pad);

        for (int e = from.E; e <= to.E; e++)
            for (int n = from.N; n <= to.N; n++)
            {
                ct.ThrowIfCancellationRequested();
                var tile = new TileId(e, n);
                RoadTile? roads;
                try
                {
                    roads = await source.LoadRoadsAsync(tile, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    continue;   // a missing or unreadable tile is simply no roads here
                }
                if (roads != null) network.AddTile(roads);
            }

        return network;
    }

    private void AddTile(RoadTile tile)
    {
        foreach (var segment in tile.Segments)
        {
            if (!IsTravellable(segment.Class)) continue;

            int count = segment.PointCount;
            if (count < 2) continue;

            var e = new double[count];
            var n = new double[count];
            var cum = new double[count];

            for (int i = 0; i < count; i++)
            {
                // tile-local is X east, Z south from the NW corner
                e[i] = tile.Id.MinE + segment.Points[i * 3];
                n[i] = tile.Id.MaxN - segment.Points[i * 3 + 2];
                if (i > 0)
                {
                    double dx = e[i] - e[i - 1], dy = n[i] - n[i - 1];
                    cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
                }
            }

            if (cum[^1] < 0.5) continue;   // a stub left by tile clipping

            int index = _edges.Count;
            var edge = new RoadEdge
            {
                E = e, N = n, Cumulative = cum,
                Class = segment.Class, Flags = segment.Flags,
                NodeA = NodeAt(e[0], n[0]),
                NodeB = NodeAt(e[^1], n[^1]),
            };
            _edges.Add(edge);

            Connect(edge.NodeA, edge.NodeB, edge.Length);
            Connect(edge.NodeB, edge.NodeA, edge.Length);

            for (int i = 0; i < count - 1; i++) Index(index, i);
        }
    }

    private int NodeAt(double e, double n)
    {
        long key = Key(Math.Round(e / NodeSnap), Math.Round(n / NodeSnap));
        if (_nodes.TryGetValue(key, out int existing)) return existing;

        int id = _adjacency.Count;
        _adjacency.Add(new List<(int, double)>());
        _nodes[key] = id;
        return id;
    }

    private void Connect(int from, int to, double cost)
    {
        if (from == to) return;
        _adjacency[from].Add((to, cost));
    }

    private void Index(int edge, int vertex)
    {
        var g = _edges[edge];
        // stamp every cell the segment's bounding box touches, so a long straight run between
        // two vertices is still found from the middle of it
        double e0 = Math.Min(g.E[vertex], g.E[vertex + 1]), e1 = Math.Max(g.E[vertex], g.E[vertex + 1]);
        double n0 = Math.Min(g.N[vertex], g.N[vertex + 1]), n1 = Math.Max(g.N[vertex], g.N[vertex + 1]);

        for (long cx = (long)Math.Floor(e0 / CellSize); cx <= (long)Math.Floor(e1 / CellSize); cx++)
            for (long cy = (long)Math.Floor(n0 / CellSize); cy <= (long)Math.Floor(n1 / CellSize); cy++)
            {
                long key = Key(cx, cy);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new();
                list.Add((edge, vertex));
            }
    }

    private static long Key(double x, double y) => ((long)x << 32) ^ (uint)(long)y;

    /// <summary>
    /// The nearest point on each road within <paramref name="radius"/>, at most one per edge.
    ///
    /// <para>
    /// One per edge matters: a road is many short segments, so without it the candidate list
    /// fills with twenty projections onto the same street and the matcher never considers the
    /// parallel one that is the actual answer.
    /// </para>
    /// </summary>
    public List<RoadHit> Near(double e, double n, double radius, int limit = 6)
    {
        var best = new Dictionary<int, RoadHit>();

        long c0 = (long)Math.Floor((e - radius) / CellSize), c1 = (long)Math.Floor((e + radius) / CellSize);
        long r0 = (long)Math.Floor((n - radius) / CellSize), r1 = (long)Math.Floor((n + radius) / CellSize);

        for (long cx = c0; cx <= c1; cx++)
            for (long cy = r0; cy <= r1; cy++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var list)) continue;
                foreach (var (edgeIndex, vertex) in list)
                {
                    var hit = Project(edgeIndex, vertex, e, n);
                    if (hit.Distance > radius) continue;
                    if (!best.TryGetValue(edgeIndex, out var previous) || hit.Distance < previous.Distance)
                        best[edgeIndex] = hit;
                }
            }

        var results = new List<RoadHit>(best.Values);
        results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (results.Count > limit) results.RemoveRange(limit, results.Count - limit);
        return results;
    }

    /// <summary>Foot of the perpendicular from a point to one segment of an edge.</summary>
    private RoadHit Project(int edgeIndex, int vertex, double e, double n)
    {
        var g = _edges[edgeIndex];
        double ax = g.E[vertex], ay = g.N[vertex];
        double bx = g.E[vertex + 1], by = g.N[vertex + 1];

        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 1e-12 ? ((e - ax) * dx + (n - ay) * dy) / len2 : 0;
        t = Math.Clamp(t, 0, 1);

        double px = ax + dx * t, py = ay + dy * t;
        double distance = Math.Sqrt((e - px) * (e - px) + (n - py) * (n - py));
        double arc = g.Cumulative[vertex] + (g.Cumulative[vertex + 1] - g.Cumulative[vertex]) * t;

        return new RoadHit(edgeIndex, arc, distance, px, py);
    }

    /// <summary>
    /// Shortest distance along the network between two projected points, or
    /// <see cref="double.PositiveInfinity"/> if no route is found inside <paramref name="limit"/>.
    ///
    /// <para>
    /// Bounded on purpose. The transition model only cares whether a route of roughly the right
    /// length exists; an unbounded search across the whole valley to prove that one does not is
    /// both slow and pointless, since anything far longer than the GPS displacement already
    /// scores as impossible.
    /// </para>
    /// </summary>
    public double RouteDistance(RoadHit from, RoadHit to, double limit)
    {
        if (from.Edge == to.Edge) return Math.Abs(to.Arc - from.Arc);

        var a = _edges[from.Edge];
        var b = _edges[to.Edge];

        // cost from the projection out to each end of its own edge, and in from each end of the target's
        Span<(int Node, double Cost)> exits = stackalloc (int, double)[2];
        exits[0] = (a.NodeA, from.Arc);
        exits[1] = (a.NodeB, a.Length - from.Arc);

        Span<(int Node, double Cost)> entries = stackalloc (int, double)[2];
        entries[0] = (b.NodeA, to.Arc);
        entries[1] = (b.NodeB, b.Length - to.Arc);

        double best = double.PositiveInfinity;
        foreach (var exit in exits)
        {
            if (exit.Cost >= limit) continue;
            var reached = Reach(exit.Node, limit - exit.Cost);
            foreach (var entry in entries)
                if (reached.TryGetValue(entry.Node, out double cost))
                    best = Math.Min(best, exit.Cost + cost + entry.Cost);
        }
        return best;
    }

    private readonly Dictionary<(int Node, double Limit), Dictionary<int, double>> _reachCache = new();

    /// <summary>Dijkstra from one node, stopping at <paramref name="limit"/> metres.</summary>
    private Dictionary<int, double> Reach(int start, double limit)
    {
        // Rounded so successive queries from the same junction with near-identical budgets share
        // one search — which is most of them, since the track walks along a road one step at a time.
        var key = (start, Math.Ceiling(limit / 25.0) * 25.0);
        if (_reachCache.TryGetValue(key, out var cached)) return cached;

        var distances = new Dictionary<int, double> { [start] = 0 };
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, 0);

        while (queue.TryDequeue(out int node, out double cost))
        {
            if (cost > distances.GetValueOrDefault(node, double.PositiveInfinity)) continue;
            if (cost > key.Item2) break;

            foreach (var (next, step) in _adjacency[node])
            {
                double through = cost + step;
                if (through > key.Item2) continue;
                if (through >= distances.GetValueOrDefault(next, double.PositiveInfinity)) continue;
                distances[next] = through;
                queue.Enqueue(next, through);
            }
        }

        // bounded so a long track cannot accumulate a search per junction it passes
        if (_reachCache.Count > 4096) _reachCache.Clear();
        _reachCache[key] = distances;
        return distances;
    }
}
