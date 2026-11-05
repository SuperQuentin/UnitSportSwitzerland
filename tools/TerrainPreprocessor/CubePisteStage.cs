using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A leaf's lattice as a piste grid (#1507, G2 B3 row 7): vertex (row j, col i) at its plane point
/// (<see cref="LeafTransform.LatticeToPlane"/>), boxes taken into the lattice by the inverse, rings filled by the LV95
/// scanline run in lattice coordinates (each edge cut every <see cref="Step"/> m first, so the plane's lines stay
/// lines), the groom phase measured from the slope's own first point (frames agree to a millimetre), record paths as
/// anchor-frame (x, z) on the leaf's ground. Distances are plane metres at the vertices: no LV95 raster is resampled.
/// </summary>
public sealed class LeafPisteGrid(LeafTransform t, IGround ground) : PisteStage.IPisteGrid
{
    public const double Step = 10;

    public int Size => t.Cells + 1;

    public (double X, double Y) At(int row, int col) => t.LatticeToPlane(col, row);

    public (int R0, int R1, int C0, int C1) Range(double x0, double y0, double x1, double y1)
    {
        double i0 = double.MaxValue, j0 = double.MaxValue, i1 = double.MinValue, j1 = double.MinValue;
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(x0, y0), (x1, y0), (x0, y1), (x1, y1)])
        {
            var (i, j) = t.PlaneToLattice(x, y);
            (i0, j0, i1, j1) = (Math.Min(i0, i), Math.Min(j0, j), Math.Max(i1, i), Math.Max(j1, j));
        }
        // the box's sides bow a little in the lattice: a vertex of slack
        return (Math.Max(0, (int)Math.Floor(j0) - 1), Math.Min(Size - 1, (int)Math.Ceiling(j1) + 1),
                Math.Max(0, (int)Math.Floor(i0) - 1), Math.Min(Size - 1, (int)Math.Ceiling(i1) + 1));
    }

    public void Fill(List<double[]> rings, List<int> inside)
    {
        var lattice = new List<double[]>(rings.Count);
        double j0 = double.MaxValue, j1 = double.MinValue;
        foreach (var ring in rings)
        {
            var pts = new List<double>();
            int n = ring.Length / 2;
            for (int k = 0; k < n; k++)
            {
                double ax = ring[k * 2], ay = ring[k * 2 + 1], bx = ring[(k + 1) % n * 2], by = ring[(k + 1) % n * 2 + 1];
                int cuts = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / Step));
                for (int c = 0; c < cuts; c++)
                {
                    var (i, j) = t.PlaneToLattice(ax + (bx - ax) * c / cuts, ay + (by - ay) * c / cuts);
                    pts.Add(i);
                    pts.Add(j);
                    (j0, j1) = (Math.Min(j0, j), Math.Max(j1, j));
                }
            }
            lattice.Add(pts.ToArray());
        }
        int r0 = Math.Max(0, (int)Math.Floor(j0)), r1 = Math.Min(Size - 1, (int)Math.Ceiling(j1));
        PisteStage.Scanline(lattice, r0, r1, row => row, x => (int)Math.Ceiling(x), x => (int)Math.Floor(x), Size, inside);
    }

    public (double X, double Y) Origin(PisteStage.Slope s) => (s.Rings![0][0], s.Rings[0][1]);

    public float[] RecordPath(double[] path)
    {
        var rel = new float[path.Length];
        for (int k = 0; k + 1 < path.Length; k += 2)
        {
            var (x, _, z) = t.PlaneToLocal(path[k], path[k + 1], ground.Sample(path[k], path[k + 1]) ?? 0);
            (rel[k], rel[k + 1]) = ((float)x, (float)z);
        }
        return rel;
    }

    /// <summary>
    /// Per slope: altitude 10th and 90th percentiles, sun exposure and mean steepness from every 4th vertex it owns
    /// (<see cref="PisteStage.Summarise"/>), on the leaf's heights above sea level; the gradient from the neighbours'
    /// plane positions (east, north on the plane).
    /// </summary>
    public Dictionary<int, (float, float, float, float)> Terrain(int[] owner, CubeTile heights)
    {
        int size = Size;
        double H(int i, int j) => heights.HeightAt(i, j) - t.UndulationAt(i, j);
        var alts = new Dictionary<int, List<float>>();
        var sums = new Dictionary<int, (double Sun, double Steep, int N)>();
        for (int row = 1; row < size - 1; row += 4)
            for (int col = 1; col < size - 1; col += 4)
            {
                int s = owner[row * size + col] - 1;
                if (s < 0) continue;
                double h = H(col, row), hi0 = H(col - 1, row), hi1 = H(col + 1, row), hj0 = H(col, row - 1), hj1 = H(col, row + 1);
                if (double.IsNaN(h) || double.IsNaN(hi0) || double.IsNaN(hi1) || double.IsNaN(hj0) || double.IsNaN(hj1)) continue;
                if (!alts.TryGetValue(s, out var list)) alts[s] = list = new List<float>();
                list.Add((float)h);
                // the gradient (east, north) from the two lattice differences: [a b]ᵀ g = (dh along i, dh along j)
                var (ax0, ay0) = At(row, col - 1); var (ax1, ay1) = At(row, col + 1);
                var (bx0, by0) = At(row - 1, col); var (bx1, by1) = At(row + 1, col);
                double ax = ax1 - ax0, ay = ay1 - ay0, bx = bx1 - bx0, by = by1 - by0, di = hi1 - hi0, dj = hj1 - hj0;
                double det = ax * by - ay * bx;
                double ge = (di * by - dj * ay) / det, gn = (ax * dj - bx * di) / det;
                double norm = Math.Sqrt(1 + ge * ge + gn * gn);
                // higher to the north = the ground faces south, toward the sun
                var have = sums.GetValueOrDefault(s);
                sums[s] = (have.Sun + gn / norm, have.Steep + Math.Atan(Math.Sqrt(ge * ge + gn * gn)) * 180 / Math.PI, have.N + 1);
            }
        return PisteStage.Summarise(alts, sums);
    }
}

/// <summary>
/// The ski slopes on work frames (#1507, G2 B3 row 7; docs/notes/tools/cube-fields-pistes-photos.md): the LV95 build's
/// OSM slopes (<see cref="PisteStage.Load"/>, the same tags, widths and ids), kept in latitude and longitude, moved onto
/// each frame's plane exactly, and rasterised at every core leaf's vertices through <see cref="LeafPisteGrid"/> with the
/// LV95 build's rules (<see cref="PisteStage.BuildTile(TileId, PisteStage.IPisteGrid, IReadOnlyList{PisteStage.Slope}, IReadOnlyList{int}, out int, Func{int[], Dictionary{int, ValueTuple{float, float, float, float}}})"/>):
/// <see cref="LeafKind.Pistes"/>. The lifts' <c>ropeways.json</c> is a region-wide file (row 8).
///
/// <code>
/// --cube-pistes --cube-out REGION --osm-pbf PBF [--cube-box minE,minN,maxE,maxN] [--jobs N]
/// </code>
/// </summary>
public static class CubePisteStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-pistes");

    public static int Run(IReadOnlyList<string> args)
    {
        string? region = null, pbf = null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        int jobs = Math.Max(1, Environment.ProcessorCount - 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-pistes": break;
                case "--cube-out": region = args[++i]; break;
                case "--osm-pbf": pbf = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-pistes: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || pbf == null) { Console.Error.WriteLine("--cube-pistes needs --cube-out REGION and --osm-pbf PBF"); return 2; }

        var sw = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var wanted = heights.Leaves.Where(k => box is not { } b || CubeFrames.InBox(k, b)).ToList();
        if (wanted.Count == 0) { Console.Error.WriteLine($"--cube-pistes: no leaf with heights in {region}"); return 2; }
        var driver = new FrameDriver(wanted, 0, SwissSeaLevel.Instance, heights.FrameLevel);
        // the leaves' latitude and longitude box, 3 km wider (as the LV95 build's round its tiles)
        double la0 = 90, la1 = -90, lo0 = 180, lo1 = -180;
        foreach (var k in wanted) { var c = k.Centre; (la0, la1, lo0, lo1) = (Math.Min(la0, c.LatDeg), Math.Max(la1, c.LatDeg), Math.Min(lo0, c.LonDeg), Math.Max(lo1, c.LonDeg)); }
        var latLonBox = (la0 - 0.04, la1 + 0.04, lo0 - 0.06, lo1 + 0.06);
        var slopes = PisteStage.Load(pbf, jobs, latLonBox, (lat, lon) => (lat, lon));
        Console.WriteLine($"cube pistes: {slopes.Count} slopes read; {wanted.Count} leaves in {driver.Frames.Count} frames, {sw.Elapsed.TotalSeconds:F0} s");

        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Pistes]);
        long leavesWith = 0, vertices = 0, dropped = 0;
        foreach (var plan in driver.Frames)
        {
            var ground = new FrameGround(plan.Frame, heights.Read, driver.LeafLevel);
            var onPlane = slopes.Select(s => s with
            {
                Line = s.Line is { } l ? ToPlane(plan.Frame, l) : null,
                Rings = s.Rings?.Select(r => ToPlane(plan.Frame, r)).ToList(),
            }).ToList();
            var bounds = onPlane.Select(PisteStage.Bounds).ToList();
            var sections = new System.Collections.Concurrent.ConcurrentDictionary<CubeKey, List<LeafSection>>();
            long frameVertices = 0, frameDropped = 0;
            Parallel.ForEach(plan.Core, new ParallelOptions { MaxDegreeOfParallelism = jobs }, leaf =>
            {
                var t = plan.Transform(leaf);
                var (x0, y0, x1, y1) = CubeFrames.PlaneBox(t, 1);
                var touching = Enumerable.Range(0, onPlane.Count).Where(s => bounds[s].MaxE >= x0 && bounds[s].MinE <= x1 && bounds[s].MaxN >= y0 && bounds[s].MinN <= y1).ToList();
                var list = new List<LeafSection>();
                if (touching.Count > 0)
                {
                    var grid = new LeafPisteGrid(t, ground);
                    var tile = heights.Read(leaf);
                    var built = PisteStage.BuildTile(default, grid, onPlane, touching, out int drop,
                        tile != null ? owner => grid.Terrain(owner, tile) : null);
                    if (built != null)
                    {
                        list.Add(LeafPistes.Encode(built, t.Bits));
                        Interlocked.Add(ref frameVertices, built.Slot.Count(b => b != 0));
                        Interlocked.Add(ref frameDropped, drop);
                    }
                }
                sections[leaf] = list;
            });
            writer.WriteFrame(plan.Cell, new Dictionary<CubeKey, List<LeafSection>>(sections));
            heights.Reload(plan.Cell);
            leavesWith += sections.Count(e => e.Value.Count > 0);
            vertices += frameVertices;
            dropped += frameDropped;
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {sections.Count(e => e.Value.Count > 0)} with slopes; {sw.Elapsed.TotalSeconds:F0} s");
        }
        var manifestPath = Path.Combine(region, CubeManifest.FileName);
        var sources = File.Exists(manifestPath) ? CubeManifest.FromJson(File.ReadAllText(manifestPath)).Sources : new List<string>();
        const string source = "ski slopes: OpenStreetMap piste:type (ODbL)";
        if (!sources.Contains(source)) sources.Add(source);
        LeafWriter.WriteManifest(region, driver.LeafLevel, driver.FrameLevel, sources);
        Console.WriteLine($"cube pistes: {leavesWith} leaves with slopes, {vertices:N0} slope vertices, {dropped} slopes over the {PisteFormat.MaxRecords} a leaf limit; "
            + $"{sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return 0;
    }

    /// <summary>Latitude and longitude pairs onto the frame's plane (exact whatever the height).</summary>
    private static double[] ToPlane(WorkFrame frame, double[] latLon)
    {
        var p = new double[latLon.Length];
        for (int i = 0; i + 1 < latLon.Length; i += 2) (p[i], p[i + 1]) = frame.Plane.ToPlane(latLon[i], latLon[i + 1]);
        return p;
    }
}
