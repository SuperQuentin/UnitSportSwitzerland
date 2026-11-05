using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using UnitSport.Tools.RoadGen.Rewrite;
using static System.Console;

/// <summary>
/// "--leaf-roads [--side 5]" (#1509, G2 B5): the generated world's roads on cube leaves (<see cref="GeneratedLeafRoads"/>,
/// RoadGen's stage on a work frame) round the biggest generated village near the anchor, against the same block built as
/// LV95 tiles (the stage on the generator's tiles, as <c>--generated-roads</c>' reference). Checks: every leaf with raw
/// roads finished without the stage failing; segment ends on a shared leaf edge meet another leaf's (1 cm); the junctions
/// and traffic lights of the inner leaves match the LV95 build's (positions compared in ECEF, the generator's notation
/// for both: a point's latitude and longitude at sea level, its height above it).
/// </summary>
static class GeneratedLeafRoadsCheck
{
    const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N

    public static int Run(string[] args)
    {
        int side = int.Parse(Arg(args, "--side") ?? "5");
        var world = new ProceduralWorld(AnchorE, AnchorN);
        var v = world.VillageCentres()
            .Where(c => Math.Abs(c.E - AnchorE) < 20000 && Math.Abs(c.N - AnchorN) < 20000)
            .OrderByDescending(c => c.Buildings).First();
        var frames = LeafFrames.Swiss;
        var roads = new GeneratedLeafRoads(world, frames);
        var failures = new List<string>();
        roads.Failed = (k, e) => failures.Add($"{k}: {e.Message}");
        int fails = 0;
        void Check(string what, bool ok, string detail = "")
        {
            WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
            if (!ok) fails++;
        }

        var (lat, lon) = Lv95Source.Instance.ToLatLon(v.E, v.N);
        var centre = LeafFrames.LeafAt(lat, lon);
        var block = new List<CubeKey>();
        for (int dy = -(side / 2); dy <= side / 2; dy++)
            for (int dx = -(side / 2); dx <= side / 2; dx++)
                if (centre.Neighbour(dx, dy) is { } k) block.Add(k);
        var inner = block.Where(k => Math.Abs(k.X - centre.X) <= side / 2 - 1 && Math.Abs(k.Y - centre.Y) <= side / 2 - 1).ToHashSet();
        WriteLine($"{v.Name} ({v.Buildings} buildings) at {v.E:F0}/{v.N:F0}: {block.Count} leaves round {centre}, {inner.Count} inner");

        var clock = Stopwatch.StartNew();
        var built = new Dictionary<CubeKey, RoadTile>();
        foreach (var k in block)
            if (roads.Build(k) is { } t) built[k] = t;
        WriteLine($"  {built.Count} leaves with roads in {clock.Elapsed.TotalSeconds:F1} s ({clock.Elapsed.TotalMilliseconds / Math.Max(1, block.Count):F0} ms a leaf)");
        Check("the stage finished every leaf", failures.Count == 0, failures.Count == 0 ? "" : string.Join("; ", failures.Take(3)));
        Check("the block has roads, junctions and paint", built.Values.Sum(t => t.Junctions.Count) > 0 && built.Values.Sum(t => t.Paint.Count) > 0,
            $"{built.Values.Sum(t => t.Segments.Count)} segments, {built.Values.Sum(t => t.Junctions.Count)} junctions, "
            + $"{built.Values.Sum(t => t.Signals.Count)} signals, {built.Values.Sum(t => t.Paint.Count)} paint");

        // segment ends on a shared leaf edge meet another leaf's end
        var ends = new List<(CubeKey Leaf, EcefPos At, bool OnEdge)>();
        foreach (var (k, t) in built)
        {
            var tr = frames.TransformOf(k);
            foreach (var s in t.Segments)
                foreach (int i in new[] { 0, s.PointCount - 1 })
                {
                    var (li, lj, h) = tr.LocalToLattice(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2]);
                    bool edge = Math.Min(Math.Min(li, tr.Cells - li), Math.Min(lj, tr.Cells - lj)) < 0.01;
                    ends.Add((k, tr.LatticeToEcef(li, lj, h), edge));
                }
        }
        int onEdges = 0, alone = 0;
        double worstEnd = 0;
        foreach (var e in ends.Where(e => e.OnEdge))
        {
            // only ends whose other side was built
            var others = ends.Where(o => o.Leaf != e.Leaf).Select(o => (o.At - e.At).Length).DefaultIfEmpty(double.MaxValue).Min();
            if (others > 5) { if (EdgeNeighbourBuilt(e, built.Keys)) alone++; continue; }
            onEdges++;
            worstEnd = Math.Max(worstEnd, others);
        }
        Check("ends on leaf edges meet another leaf's", alone == 0 && worstEnd < 0.01, $"{onEdges} ends, worst {worstEnd * 100:F2} cm, {alone} alone");

        // the LV95 build of the same block: the tiles under the leaves, the stage over them at once
        var lv95 = block.SelectMany(k => GeneratedLeaves.TilesUnder(new SourceOnLeaf(frames, k, Lv95Source.Instance))).ToHashSet();
        var reference = TileRewriter.Rewrite(lv95, new TileRewriter.Options(BlockSize: 1, Halo: 1), new TileRewriter.Inputs
        {
            Roads = t =>
            {
                var (r, keys) = world.BuildRoadsKeyed(t);
                return (r, r is null ? null : keys.Select(k => (RawRoads.Key?)new RawRoads.Key(k.Line, 0,
                    Math.Round(k.FromM, 2, MidpointRounding.AwayFromZero))).ToArray());
            },
            Grid = t => world.BuildGrid(t, 5),
            Buildings = t => world.BuildBuildings(t),
        });
        var lvJunctions = new List<EcefPos>();
        var lvSignals = new List<EcefPos>();
        foreach (var t in reference)
        {
            foreach (var j in t.Junctions) Add(lvJunctions, t.Id, Centre(j.Vertices));
            foreach (var s in t.Signals) Add(lvSignals, t.Id, (s.X, s.Y, s.Z));
        }
        var leafJunctions = new List<EcefPos>();
        var leafSignals = new List<EcefPos>();
        foreach (var (k, t) in built.Where(b => inner.Contains(b.Key)))
        {
            var tr = frames.TransformOf(k);
            EcefPos Ecef((float X, float Y, float Z) p)
            {
                var (li, lj, h) = tr.LocalToLattice(p.X, p.Y, p.Z);
                return tr.LatticeToEcef(li, lj, h);
            }
            leafJunctions.AddRange(t.Junctions.Select(j => Ecef(Centre(j.Vertices))));
            leafSignals.AddRange(t.Signals.Select(s => Ecef((s.X, s.Y, s.Z))));
        }
        // the LV95 build's in the inner leaves
        bool InInner(EcefPos p)
        {
            var g = Ellipsoid.Wgs84.ToGeodetic(p);
            return inner.Contains(LeafFrames.LeafAt(g.LatDeg, g.LonDeg));
        }
        lvJunctions = lvJunctions.Where(InInner).ToList();
        lvSignals = lvSignals.Where(InInner).ToList();
        var (jm, j50, j95) = Match(lvJunctions, leafJunctions, 5);
        var (sm, s50, s95) = Match(lvSignals, leafSignals, 5);
        WriteLine($"  junctions: {lvJunctions.Count} LV95 / {leafJunctions.Count} leaves, {jm} matched within 5 m, p50 {j50 * 100:F2} cm, p95 {j95 * 100:F1} cm");
        WriteLine($"  signals:   {lvSignals.Count} LV95 / {leafSignals.Count} leaves, {sm} matched within 5 m, p50 {s50 * 100:F2} cm, p95 {s95 * 100:F1} cm");
        Check("junctions as the LV95 build's (95 % matched within 5 m)", lvJunctions.Count > 0 && jm >= 0.95 * lvJunctions.Count && jm >= 0.95 * leafJunctions.Count);
        Check("signals as the LV95 build's (all matched)", sm == lvSignals.Count && sm == leafSignals.Count);

        WriteLine(fails == 0 ? "[leaf-roads] RESULT: ok" : $"[leaf-roads] RESULT: FAILED ({fails})");
        return fails == 0 ? 0 : 1;

        void Add(List<EcefPos> into, TileId id, (float X, float Y, float Z) p)
        {
            var (la, lo) = Lv95Source.Instance.ToLatLon(id.MinE + p.X, id.MaxN - p.Z);
            into.Add(Ellipsoid.Wgs84.ToEcef(la * Math.PI / 180, lo * Math.PI / 180, p.Y + SwissSeaLevel.Instance.Undulation(la, lo)));
        }
    }

    /// <summary>Whether the leaf across the edge an end lies on was built (an end at the block's rim has no partner).</summary>
    static bool EdgeNeighbourBuilt((CubeKey Leaf, EcefPos At, bool OnEdge) end, IEnumerable<CubeKey> built)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(end.At);
        var set = built.ToHashSet();
        // a step of a metre each way finds the leaves round the point
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var k = LeafFrames.LeafAt(g.LatDeg + dy * 1e-5, g.LonDeg + dx * 1.4e-5);
                if (k != end.Leaf && set.Contains(k)) return true;
            }
        return false;
    }

    static (float X, float Y, float Z) Centre(float[] v)
    {
        double x = 0, y = 0, z = 0;
        int n = v.Length / 3;
        for (int i = 0; i < n; i++) (x, y, z) = (x + v[i * 3], y + v[i * 3 + 1], z + v[i * 3 + 2]);
        return ((float)(x / n), (float)(y / n), (float)(z / n));
    }

    /// <summary>Greedy nearest matches within <paramref name="within"/> metres: how many, the median and p95 distance.</summary>
    static (int Matched, double P50, double P95) Match(List<EcefPos> a, List<EcefPos> b, double within)
    {
        var used = new bool[b.Count];
        var d = new List<double>();
        foreach (var p in a)
        {
            int best = -1;
            double bestD = within;
            for (int i = 0; i < b.Count; i++)
                if (!used[i] && (b[i] - p).Length is var dd && dd < bestD) (best, bestD) = (i, dd);
            if (best < 0) continue;
            used[best] = true;
            d.Add(bestD);
        }
        d.Sort();
        return (d.Count, d.Count == 0 ? 0 : d[d.Count / 2], d.Count == 0 ? 0 : d[(int)(d.Count * 0.95)]);
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
