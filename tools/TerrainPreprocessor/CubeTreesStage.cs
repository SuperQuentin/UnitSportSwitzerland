using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Trees on cube leaves (#1507, G2 B3 PR 5b; docs/notes/tools/cube-cover.md): <see cref="CoverExtractor"/>'s four kinds of
/// tree with its rules (<see cref="CoverExtractor.ScatterTree"/>, <see cref="CoverExtractor.RowTree"/>,
/// <see cref="CoverExtractor.SurveyedHeight"/>, <see cref="CoverStage.PlanterHeight"/>, <see cref="CoverStage.TreeClearance"/>),
/// placed on each leaf of a region with its cover and roads, written as its <see cref="LeafKind.Trees"/> in the anchor frame.
/// <list type="bullet">
/// <item><b>Scattered</b> in the wooded classes: each vertex a leaf owns draws its chance (the class's density per hectare
/// over the vertex's area) and then its jitter and height from a generator seeded by the <b>global lattice vertex</b>
/// (face, i, j at the leaf lattice's level): the same tree whatever the frame, the batch or the run.</item>
/// <item><b>Planted rows</b> in orchards and nurseries: on the frame's plane every <see cref="CoverFormat.PlantingSpacing"/>
/// metres (continuous across leaves; a frame edge restarts the grid, as an LV95 tile edge does not).</item>
/// <item><b>Surveyed</b> (<c>tlm_bb_einzelbaum</c>): each point onto the plane at its own height, on the leaf mesh.</item>
/// <item><b>Planters</b> of the laid-out car parks: the network's islands (4b), their heights from their LV95 position.</item>
/// </list>
/// No tree grows within a road's clearance (the leaf's raw roads and its network's, as the LV95 build masks both).
///
/// <code>
/// --cube-trees --cube-out REGION --tlm GPKG [--cube-temp DIR] [--cube-box minE,minN,maxE,maxN] [--jobs N]
/// </code>
/// </summary>
public static class CubeTreesStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-trees");

    private sealed class Stats { public long Scattered, Planted, Surveyed, Planters; }

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? region = null, temp = null, tlm = null, lv95Roads = null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        int jobs = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-trees": break;
                case "--cube-out": region = args[++i]; break;
                case "--cube-temp": temp = args[++i]; break;
                case "--tlm": tlm = args[++i]; break;
                // a check: the masks and planters from the LV95 build's own roads (its final ones and DIR_temp/roads_raw), moved into each leaf
                case "--lv95-roads": lv95Roads = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-trees: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || tlm == null) { Console.Error.WriteLine("--cube-trees needs --cube-out REGION (with its heights, cover and roads) and --tlm GPKG"); return 2; }
        temp ??= region.TrimEnd('/', '\\') + "_temp";
        string rawDir = CubeRoadStage.RawDir(temp);

        var clock = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var wanted = heights.Leaves.Where(k => box is not { } b || CubeRoadStage.InBox(k, b)).ToList();
        var driver = new FrameDriver(wanted, 0, SwissSeaLevel.Instance, heights.FrameLevel);
        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Trees]);
        var stats = new Stats();
        using var conn = GeoPackageReader.Open(tlm);
        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var fs = Stopwatch.StartNew();
            var ground = new FrameGround(plan.Frame, heights.Read, driver.LeafLevel);
            var space = new FrameRoadSpace(plan, ground);
            // the surveyed trees of the frame, each on the leaf under it
            var surveyed = new Dictionary<CubeKey, List<(double X, double Y, double E, double N)>>();
            foreach (var reader in GeoPackageReader.TileRows(conn, "tlm_bb_einzelbaum", new[] { "objektart" }, plan.Region(Lv95Source.Instance, 0)))
            {
                if (reader.IsDBNull(1)) continue;
                foreach (var (e, n, z) in GeoPackageReader.ParsePoints((byte[])reader.GetValue(1)))
                {
                    var (x, y, _) = plan.Frame.FromSource(Lv95Source.Instance, e, n, z);
                    var leaf = space.TileAt(x, y);
                    if (!space.Writes(leaf)) continue;
                    if (!surveyed.TryGetValue(leaf, out var list)) surveyed[leaf] = list = new();
                    list.Add((x, y, e, n));
                }
            }
            var sections = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            Parallel.ForEach(plan.Core, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, key =>
            {
                var leaf = heights.ReadLeaf(key);
                if (leaf?.Find(LeafKind.Cover) is not { } cs || leaf.Find(LeafKind.Heights) is not { } hs) return;
                var raw = File.Exists(Path.Combine(rawDir, CubeBundle.LeafFileName(plan.Cell)))
                    ? CubeBundle.ReadLeaf(Path.Combine(rawDir, CubeBundle.LeafFileName(plan.Cell)), key)?.Find(LeafKind.Roads) : null;
                List<RoadTile> roads = lv95Roads != null
                    ? [CubeCoverNetworkStage.Lv95RoadsIn(lv95Roads, key), Raw(CubeCoverNetworkStage.Lv95RoadsIn(lv95Roads.TrimEnd('/', '\\') + "_temp/roads_raw", key))]
                    : [.. new[] { raw, leaf.Find(LeafKind.Roads) }.OfType<LeafSection>().Select(LeafRoads.Decode)];
                var trees = Grow(plan, key, LeafHeights.Decode(hs), LeafCover.Decode(cs), roads,
                    surveyed.GetValueOrDefault(key) ?? [], ground, stats);
                sections[key] = trees.Count > 0 ? [LeafTrees.Encode(trees)] : [];
            });
            writer.WriteFrame(plan.Cell, new Dictionary<CubeKey, List<LeafSection>>(sections));
            heights.Reload(plan.Cell);   // the bundle was rewritten: the next frames' grounds read it by its new index
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {fs.Elapsed.TotalSeconds:F1} s; {clock.Elapsed.TotalSeconds:F0} s");
        }
        Console.WriteLine($"cube trees: {stats.Scattered + stats.Planted + stats.Surveyed + stats.Planters:N0} trees ({stats.Scattered:N0} scattered, {stats.Planted:N0} planted, "
            + $"{stats.Surveyed:N0} surveyed singles, {stats.Planters:N0} in car park planters) in {wanted.Count} leaves; {clock.Elapsed.TotalSeconds:F0} s, "
            + $"peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return 0;
    }

    private static List<TreeInstance> Grow(FramePlan plan, CubeKey key, CubeTile tile, byte[] cover, List<RoadTile> roads,
        List<(double X, double Y, double E, double N)> surveyed, FrameGround ground, Stats stats)
    {
        var t = plan.Transform(key);
        int n = t.Cells, side = n + 1;
        var lattice = new LeafCoverLattice(t, tile, (_, _) => 0);
        var trees = new List<TreeInstance>();

        // the roads' corridors, as the LV95 build masks its 1 m cells: a disc of the clearance in whole cells round the
        // line (BuildRoadMask), so the clearance rounded up to whole metres
        var blocked = new bool[side * side];
        foreach (var seg in roads.SelectMany(r => r.Segments))
        {
            double radius = Math.Ceiling(CoverStage.TreeClearance(seg));
            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                double ax = seg.Points[i * 3], az = seg.Points[i * 3 + 2], bx = seg.Points[i * 3 + 3], bz = seg.Points[i * 3 + 5];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                foreach (int k in lattice.Near(Math.Min(ax, bx) - radius, Math.Min(az, bz) - radius, Math.Max(ax, bx) + radius, Math.Max(az, bz) + radius))
                {
                    var (x, z) = lattice.At(k);
                    double f = len < 1e-9 ? 0 : Math.Clamp(((x - ax) * (bx - ax) + (z - az) * (bz - az)) / (len * len), 0, 1);
                    double dx = ax + (bx - ax) * f - x, dz = az + (bz - az) * f - z;
                    if (dx * dx + dz * dz <= radius * radius) blocked[k] = true;
                }
            }
        }

        // the vertex's area and spacing: the leaf's corners on the ellipsoid
        var p00 = key.Vertex(t.Bits, 0, 0, 0);
        double su = p00.DistanceTo(key.Vertex(t.Bits, n, 0, 0)) / n, sv = p00.DistanceTo(key.Vertex(t.Bits, 0, n, 0)) / n;
        double area = su * sv;

        // scattered: every vertex the leaf owns, seeded by its global lattice vertex
        long gi0 = (long)key.X * n, gj0 = (long)key.Y * n;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * side + i;
                var cls = (CoverClass)cover[k];
                if (!CoverFormat.IsWooded(cls) || blocked[k] || float.IsNaN(tile.Heights[k])) continue;
                ulong seed = TreeSeed.Of(key.Face, gi0 + i, gj0 + j);
                if (TreeSeed.Unit(seed) > CoverExtractor.ScatterChance(cls, area)) continue;
                var rng = new Random((int)(seed >> 33));
                double fi = i + (rng.NextDouble() - 0.5) * 2 * CoverExtractor.ScatterJitterM / su;
                double fj = j + (rng.NextDouble() - 0.5) * 2 * CoverExtractor.ScatterJitterM / sv;
                fi = Math.Clamp(fi, 0, n);
                fj = Math.Clamp(fj, 0, n);
                double h = FrameGround.MeshHeight(tile.Heights, n, fi, fj);
                if (double.IsNaN(h)) continue;
                var (height, kind) = CoverExtractor.ScatterTree(cls, rng);
                var (lx, ly, lz) = t.LatticeToLocal(fi, fj, h);
                trees.Add(new TreeInstance((float)lx, (float)ly, (float)lz, height, kind));
                Interlocked.Increment(ref stats.Scattered);
            }

        // planted rows: the frame's plane every PlantingSpacing metres, the trees whose vertex the leaf owns
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (ci, cj) in new[] { (0, 0), (n, 0), (0, n), (n, n) })
        {
            var (x, y) = t.LatticeToPlane(ci, cj);
            (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
        }
        foreach (var cls in new[] { CoverClass.Orchard, CoverClass.Nursery })
        {
            double step = CoverFormat.PlantingSpacing(cls);
            for (long mx = (long)Math.Ceiling(x0 / step); mx * step <= x1; mx++)
                for (long my = (long)Math.Ceiling(y0 / step); my * step <= y1; my++)
                {
                    double x = mx * step, y = my * step;
                    var (fi, fj) = t.PlaneToLattice(x, y);
                    int i = (int)Math.Round(fi), j = (int)Math.Round(fj);
                    if (fi < 0 || fj < 0 || fi >= n || fj >= n || (uint)i > (uint)n || (uint)j > (uint)n) continue;
                    int k = j * side + i;
                    if ((CoverClass)cover[k] != cls || blocked[k]) continue;
                    if (ground.Sample(x, y) is not { } hSea) continue;
                    var rng = new Random((int)(TreeSeed.Of(plan.Cell.Face * 64 + (int)cls, mx, my) >> 33));
                    var (jitter, height) = CoverExtractor.RowTree(cls, rng);
                    var (lx, ly, lz) = t.PlaneToLocal(x, y, hSea);
                    trees.Add(new TreeInstance((float)lx + jitter, (float)ly, (float)lz + jitter, height, CoverExtractor.KindFruitTree));
                    Interlocked.Increment(ref stats.Planted);
                }
        }

        // surveyed singles, off the roads
        foreach (var (x, y, e, nn) in surveyed)
        {
            var (fi, fj) = t.PlaneToLattice(x, y);
            int i = Math.Clamp((int)Math.Round(fi), 0, n), j = Math.Clamp((int)Math.Round(fj), 0, n);
            if (blocked[j * side + i]) continue;
            if (ground.Sample(x, y) is not { } hSea) continue;
            var (lx, ly, lz) = t.PlaneToLocal(x, y, hSea);
            trees.Add(new TreeInstance((float)lx, (float)ly, (float)lz, CoverExtractor.SurveyedHeight(e, nn), CoverExtractor.KindSolitaryTree));
            Interlocked.Increment(ref stats.Surveyed);
        }

        // the car parks' planters (the network's islands), on a copy: the cover itself is written by --cube-cover-network
        var network = roads.FirstOrDefault(r => (r.Flags & RoadTileFlags.Network) != 0);
        if (network != null)
            CoverStage.LaidOutLots(network, (byte[])cover.Clone(), lattice, (cx, cy, cz) =>
            {
                var (e, nn, _) = SwissGeodesy.FromEcef(key.Anchor.ToEcef(cx, cy, cz));
                trees.Add(new TreeInstance((float)cx, (float)cy, (float)cz, CoverStage.PlanterHeight(e, nn), CoverExtractor.KindSolitaryTree));
                Interlocked.Increment(ref stats.Planters);
            });
        return trees;
    }

    /// <summary>The LV95 raw roads as moved: no Network flag (they mask, they lay no planters).</summary>
    private static RoadTile Raw(RoadTile t)
    {
        t.Flags &= ~RoadTileFlags.Network;
        return t;
    }
}
