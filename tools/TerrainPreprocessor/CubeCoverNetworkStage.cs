using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A leaf's cover lattice in its anchor frame (#1507, G2 B3 PR 5c), where the network stage writes its road records
/// (B3 4b): each vertex at its own ground (<see cref="LeafTransform.LatticeToLocal"/>), the cells near a box found
/// through <see cref="LeafTransform.LocalToLattice"/>, the urban density at the vertex's LV95 position.
/// </summary>
public sealed class LeafCoverLattice : ICoverLattice
{
    private readonly LeafTransform _t;
    private readonly int _side;
    private readonly float[] _x, _y, _z;
    private readonly double _meanY;
    private readonly Func<double, double, double> _density;
    private readonly CubeTile _heights;

    /// <param name="density">The urban density at an LV95 point (<see cref="UrbanField.Density"/>), called from one thread at a time.</param>
    public LeafCoverLattice(LeafTransform t, CubeTile heights, Func<double, double, double> density)
    {
        _t = t;
        _heights = heights;
        _density = density;
        _side = t.Cells + 1;
        _x = new float[_side * _side];
        _y = new float[_side * _side];
        _z = new float[_side * _side];
        double sum = 0;
        for (int j = 0; j < _side; j++)
            for (int i = 0; i < _side; i++)
            {
                int k = j * _side + i;
                float h = heights.Heights[k];
                var (x, y, z) = t.LatticeToLocal(i, j, float.IsNaN(h) ? 0 : h);
                (_x[k], _y[k], _z[k]) = ((float)x, float.IsNaN(h) ? float.NaN : (float)y, (float)z);
                if (!float.IsNaN(h)) sum += y;
            }
        _meanY = sum / Math.Max(1, _x.Length);
    }

    public int Count => _side * _side;

    public (double X, double Z) At(int cell) => (_x[cell], _z[cell]);

    public double Height(int cell) => _y[cell];

    public IEnumerable<int> Near(double x0, double z0, double x1, double z1)
    {
        // the box's corners in the lattice (at the leaf's mean height: a vertex's own height moves it a few cm), a few cells wider
        double i0 = double.MaxValue, j0 = double.MaxValue, i1 = double.MinValue, j1 = double.MinValue;
        foreach (var (x, z) in new[] { (x0, z0), (x1, z0), (x0, z1), (x1, z1) })
        {
            var (i, j, _) = _t.LocalToLattice(x, _meanY, z);
            (i0, j0, i1, j1) = (Math.Min(i0, i), Math.Min(j0, j), Math.Max(i1, i), Math.Max(j1, j));
        }
        int a0 = Math.Max(0, (int)Math.Floor(i0) - 2), a1 = Math.Min(_side - 1, (int)Math.Ceiling(i1) + 2);
        int b0 = Math.Max(0, (int)Math.Floor(j0) - 2), b1 = Math.Min(_side - 1, (int)Math.Ceiling(j1) + 2);
        for (int j = b0; j <= b1; j++)
            for (int i = a0; i <= a1; i++)
                yield return j * _side + i;
    }

    public IEnumerable<int> InBox(double x0, double z0, double x1, double z1)
    {
        foreach (int k in Near(x0, z0, x1, z1))
            if (_x[k] >= x0 && _x[k] <= x1 && _z[k] >= z0 && _z[k] <= z1) yield return k;
    }

    public double UrbanDensity(int cell)
    {
        int i = cell % _side, j = cell / _side;
        float h = _heights.Heights[cell];
        var (e, n, _) = SwissGeodesy.FromEcef(_t.LatticeToEcef(i, j, float.IsNaN(h) ? 0 : h));
        return _density(e, n);
    }
}

/// <summary>
/// The cover passes that read the network stage's final roads (#1507, G2 B3 PR 5c; docs/notes/tools/cube-cover.md):
/// <see cref="CoverStage.PaveStreets"/>, <see cref="CoverStage.RoofTunnels"/> and <see cref="CoverStage.LaidOutLots"/>
/// on each leaf that has both a <see cref="LeafKind.Cover"/> and the network's <see cref="LeafKind.Roads"/>, through
/// <see cref="LeafCoverLattice"/>; the urban field from the LV95 build's buildings (<c>--facades</c>, until stage 3 writes
/// buildings per leaf). The planters' trees come with the trees (5b): counted here, not planted.
///
/// <code>
/// --cube-cover-network --cube-out REGION --facades LV95_CHUNKS [--cube-box minE,minN,maxE,maxN] [--jobs N]
/// </code>
/// </summary>
public static class CubeCoverNetworkStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-cover-network");

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? region = null, facades = null, lv95Roads = null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        int jobs = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-cover-network": break;
                case "--cube-out": region = args[++i]; break;
                case "--facades": facades = args[++i]; break;
                // a check: the LV95 build's network roads moved into each leaf, so the passes see the same roads as the LV95 cover
                case "--lv95-roads": lv95Roads = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-cover-network: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || facades == null) { Console.Error.WriteLine("--cube-cover-network needs --cube-out REGION (with its cover and network roads) and --facades LV95_CHUNKS"); return 2; }

        var clock = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var wanted = heights.Leaves.Where(k => box is not { } b || CubeRoadStage.InBox(k, b)).ToList();
        var driver = new FrameDriver(wanted, 0, SwissSeaLevel.Instance, heights.FrameLevel);
        var field = new UrbanField(new Facades(facades));
        var fieldLock = new object();
        double Density(double e, double n) { lock (fieldLock) return field.Density(e, n); }
        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Cover], [LatticeKind.Cover]);
        long paved = 0, roofs = 0, lotCells = 0, planters = 0, done = 0;
        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            // every leaf of the bundle keeps its cover as it is; those with network roads get the passes
            var sections = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            string path = Path.Combine(region, CubeBundle.LeafFileName(plan.Cell));
            var entries = File.Exists(path) ? CubeBundle.ReadAllEntries(path) : new();
            var core = plan.Core.ToHashSet();
            Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, entry =>
            {
                var (key, bytes) = entry;
                if (CubeLeafCodec.ReadSection(bytes, LeafKind.Cover) is not { } cs) return;
                var roads = !core.Contains(key) ? null
                    : lv95Roads != null ? Lv95RoadsIn(lv95Roads, key)
                    : CubeLeafCodec.ReadSection(bytes, LeafKind.Roads) is { } rs ? LeafRoads.Decode(rs) : null;
                if (roads == null || (roads.Flags & RoadTileFlags.Network) == 0) { sections[key] = [cs]; return; }
                var tile = LeafHeights.Decode(CubeLeafCodec.ReadSection(bytes, LeafKind.Heights)!);
                var cells = LeafCover.Decode(cs);
                var lattice = new LeafCoverLattice(plan.Transform(key), tile, Density);
                long p = CoverStage.PaveStreets(roads, cells, lattice);
                long r = CoverStage.RoofTunnels(roads, cells, lattice);
                var (lc, lt) = CoverStage.LaidOutLots(roads, cells, lattice, null);
                sections[key] = [LeafCover.Encode(cells, tile.Bits)];
                Interlocked.Add(ref paved, p);
                Interlocked.Add(ref roofs, r);
                Interlocked.Add(ref lotCells, lc);
                Interlocked.Add(ref planters, lt);
                Interlocked.Increment(ref done);
            });
            if (sections.IsEmpty) continue;
            var seams = writer.WriteFrame(plan.Cell, new Dictionary<CubeKey, List<LeafSection>>(sections));
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {done} leaves with network roads so far; "
                + string.Join(", ", seams.Select(st => $"seam {st.Copied} of {st.Samples} copied ({st.CopiedAcrossFrames} across frames)")) + $"; {clock.Elapsed.TotalSeconds:F0} s");
        }
        Console.WriteLine($"cube cover after the network: {done} leaves; {paved:N0} vertices paved along the streets, {roofs:N0} over shallow tunnels concrete, "
            + $"{lotCells:N0} under laid-out car parks off the bay pattern, {planters} planters (their trees: 5b); {clock.Elapsed.TotalSeconds:F0} s");
        return 0;
    }

    private static readonly ConcurrentDictionary<TileId, RoadTile?> Lv95Tiles = new();

    /// <summary>
    /// The LV95 build's network records (segments, junction caps, area props) of the tiles round a leaf, every point moved
    /// into the leaf's anchor frame through ECEF (<c>--lv95-roads</c>, a check: the passes on the LV95 build's own roads).
    /// </summary>
    internal static RoadTile Lv95RoadsIn(string dir, CubeKey key)
    {
        double e0 = double.MaxValue, n0 = double.MaxValue, e1 = double.MinValue, n1 = double.MinValue;
        for (int j = 0; j <= 2; j++)
            for (int i = 0; i <= 2; i++)
            {
                var (e, n, _) = SwissGeodesy.FromEcef(key.Vertex(1, i, j, 0));
                (e0, n0, e1, n1) = (Math.Min(e0, e), Math.Min(n0, n), Math.Max(e1, e), Math.Max(n1, n));
            }
        var anchor = key.Anchor;
        var tile = new RoadTile { Segments = new(), Flags = RoadTileFlags.Network };
        for (int te = (int)Math.Floor((e0 - 50) / 1000); te <= (int)Math.Floor((e1 + 50) / 1000); te++)
            for (int tn = (int)Math.Floor((n0 - 50) / 1000); tn <= (int)Math.Floor((n1 + 50) / 1000); tn++)
            {
                var id = new TileId(te, tn);
                var src = Lv95Tiles.GetOrAdd(id, k =>
                {
                    string path = Path.Combine(dir, RoadFormat.FileName(k));
                    if (!File.Exists(path)) return null;
                    using var fs = File.OpenRead(path);
                    return RoadCodec.Decode(fs);
                });
                if (src == null) continue;
                float[] Move(float[] xyz)
                {
                    var o = new float[xyz.Length];
                    for (int k = 0; k + 2 < xyz.Length; k += 3)
                    {
                        var (x, y, z) = anchor.ToLocal(SwissGeodesy.ToEcef(id.MinE + xyz[k], id.MaxN - xyz[k + 2], xyz[k + 1]));
                        (o[k], o[k + 1], o[k + 2]) = ((float)x, (float)y, (float)z);
                    }
                    return o;
                }
                foreach (var sgm in src.Segments)
                    tile.Segments.Add(new RoadSegment { Class = sgm.Class, Surface = sgm.Surface, Flags = sgm.Flags, Width = sgm.Width, Attributes = sgm.Attributes, Points = Move(sgm.Points) });
                foreach (var jn in src.Junctions)
                    tile.Junctions.Add(new RoadJunction { Class = jn.Class, Layer = jn.Layer, Vertices = Move(jn.Vertices), Indices = jn.Indices });
                foreach (var ap in src.AreaProps)
                    tile.AreaProps.Add(new RoadAreaProp { Type = ap.Type, Variant = ap.Variant, Flags = ap.Flags, Height = ap.Height, Vertices = Move(ap.Vertices), Indices = ap.Indices });
            }
        return tile;
    }
}
