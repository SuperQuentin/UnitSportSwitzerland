using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A work frame's cover space (#1507, G2 B3 PR 5a): every core leaf's lattice, a <see cref="CoverClass"/> byte a
/// vertex. No LV95 raster is resampled: each ring is taken onto the frame's plane (<see cref="WorkFrame.FromSource"/>,
/// at its own height; a ring with none at the ground), clipped to a box a little wider than each leaf it may cover,
/// taken into that leaf's lattice coordinates (<see cref="LeafTransform.PlaneToLattice"/>), and filled row by row as the
/// LV95 scanline fills its grid (half-open rows, vertices between crossing pairs), by
/// <see cref="CoverExtractor.Stamped"/>'s rule. Edges are cut every <see cref="EdgeStepM"/> before they are taken into
/// the lattice, so a straight edge stays straight there to well under a millimetre.
/// </summary>
public sealed class FrameCoverSpace : ICoverSpace
{
    /// <summary>Longest ring edge taken into a leaf's lattice in one piece.</summary>
    public const double EdgeStepM = 10;

    /// <summary>How much wider than a leaf its clip box is: its vertices all inside, the inverse valid on the clipped ring.</summary>
    private const double PadM = 2;

    private const double BucketM = 500;

    private readonly FramePlan _plan;
    private readonly IGround? _ground;
    private readonly Dictionary<CubeKey, (double X0, double Y0, double X1, double Y1)> _boxes = new();
    private readonly Dictionary<(long, long), List<CubeKey>> _buckets = new();

    /// <summary>The core leaves' cover, row-major from the south-west (j·side + i), as <see cref="LeafCover"/> takes it.</summary>
    public Dictionary<CubeKey, byte[]> Cover { get; } = new();

    public FrameCoverSpace(FramePlan plan, IGround? ground = null)
    {
        _plan = plan;
        _ground = ground;
        foreach (var leaf in plan.Core)
        {
            var t = plan.Transform(leaf);
            int side = t.Cells + 1;
            Cover[leaf] = new byte[side * side];
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int k = 0; k <= 8; k++)
                foreach (var (fi, fj) in new[] { (k / 8.0, 0.0), (k / 8.0, 1.0), (0.0, k / 8.0), (1.0, k / 8.0) })
                {
                    var (x, y) = t.LatticeToPlane(fi * t.Cells, fj * t.Cells);
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                }
            var box = (x0 - PadM, y0 - PadM, x1 + PadM, y1 + PadM);
            _boxes[leaf] = box;
            for (long bx = Bucket(box.Item1); bx <= Bucket(box.Item3); bx++)
                for (long by = Bucket(box.Item2); by <= Bucket(box.Item4); by++)
                {
                    if (!_buckets.TryGetValue((bx, by), out var list)) _buckets[(bx, by)] = list = new List<CubeKey>();
                    list.Add(leaf);
                }
        }
    }

    private static long Bucket(double m) => (long)Math.Floor(m / BucketM);

    public void Stamp(GeoPackageReader.Ring ring, CoverClass cls)
    {
        int n = ring.Count;
        if (n < 4) return;
        // on the plane, every edge cut to EdgeStepM
        var pts = new List<(double X, double Y)>(n * 2);
        (double X, double Y) prev = default;
        for (int k = 0; k < n; k++)
        {
            var p = OnPlane(ring.Xyz[k * 3], ring.Xyz[k * 3 + 1], ring.Xyz[k * 3 + 2]);
            if (k > 0)
            {
                double len = Math.Sqrt((p.X - prev.X) * (p.X - prev.X) + (p.Y - prev.Y) * (p.Y - prev.Y));
                int steps = Math.Min(4096, (int)Math.Ceiling(len / EdgeStepM));
                for (int s = 1; s < steps; s++)
                {
                    double f = (double)s / steps;
                    pts.Add((prev.X + (p.X - prev.X) * f, prev.Y + (p.Y - prev.Y) * f));
                }
            }
            pts.Add(p);
            prev = p;
        }
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        var seen = new HashSet<CubeKey>();
        for (long bx = Bucket(minX); bx <= Bucket(maxX); bx++)
            for (long by = Bucket(minY); by <= Bucket(maxY); by++)
            {
                if (!_buckets.TryGetValue((bx, by), out var leaves)) continue;
                foreach (var leaf in leaves)
                {
                    if (!seen.Add(leaf)) continue;
                    var b = _boxes[leaf];
                    if (maxX < b.X0 || minX > b.X1 || maxY < b.Y0 || minY > b.Y1) continue;
                    Fill(leaf, Clip(pts, b), cls);
                }
            }
    }

    /// <summary>A ring point on the plane: at its own height, or (a hand-traced ring, height 0) at the ground under it.</summary>
    private (double X, double Y) OnPlane(double e, double n, double h)
    {
        var (x, y, _) = _plan.Frame.FromSource(Lv95Source.Instance, e, n, h);
        if (h == 0 && _ground?.Sample(x, y) is { } g) (x, y, _) = _plan.Frame.FromSource(Lv95Source.Instance, e, n, g);
        return (x, y);
    }

    /// <summary>Sutherland-Hodgman against an axis-aligned box: the ring inside it, closed (first point last).</summary>
    public static List<(double X, double Y)> Clip(List<(double X, double Y)> ring, (double X0, double Y0, double X1, double Y1) box)
    {
        var cur = ring;
        for (int edge = 0; edge < 4 && cur.Count > 0; edge++)
        {
            bool In((double X, double Y) p) => edge switch { 0 => p.X >= box.X0, 1 => p.X <= box.X1, 2 => p.Y >= box.Y0, _ => p.Y <= box.Y1 };
            (double X, double Y) Cut((double X, double Y) a, (double X, double Y) b)
            {
                double t = edge switch
                {
                    0 => (box.X0 - a.X) / (b.X - a.X),
                    1 => (box.X1 - a.X) / (b.X - a.X),
                    2 => (box.Y0 - a.Y) / (b.Y - a.Y),
                    _ => (box.Y1 - a.Y) / (b.Y - a.Y),
                };
                return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
            var next = new List<(double X, double Y)>(cur.Count + 4);
            for (int k = 0; k < cur.Count; k++)
            {
                var a = cur[k];
                var b = cur[(k + 1) % cur.Count];
                bool ia = In(a), ib = In(b);
                if (ia) next.Add(a);
                if (ia != ib) next.Add(Cut(a, b));
            }
            cur = next;
        }
        if (cur.Count > 0) cur.Add(cur[0]);
        return cur;
    }

    /// <summary>The scanline in a leaf's lattice coordinates: rows j, crossings along i, the vertices between each pair.</summary>
    private void Fill(CubeKey leaf, List<(double X, double Y)> clipped, CoverClass cls)
    {
        if (clipped.Count < 4) return;
        var t = _plan.Transform(leaf);
        var cells = Cover[leaf];
        int side = t.Cells + 1;
        var lat = new (double I, double J)[clipped.Count];
        double minJ = double.MaxValue, maxJ = double.MinValue;
        for (int k = 0; k < clipped.Count; k++)
        {
            lat[k] = t.PlaneToLattice(clipped[k].X, clipped[k].Y);
            minJ = Math.Min(minJ, lat[k].J);
            maxJ = Math.Max(maxJ, lat[k].J);
        }
        var xs = new List<double>(16);
        for (int j = Math.Max(0, (int)Math.Ceiling(minJ)); j <= Math.Min(t.Cells, (int)Math.Floor(maxJ)); j++)
        {
            xs.Clear();
            for (int k = 0; k + 1 < lat.Length; k++)
            {
                double j0 = lat[k].J, j1 = lat[k + 1].J;
                if (j0 == j1) continue;
                // half-open, as the LV95 scanline: a shared vertex is counted once
                if (j < Math.Min(j0, j1) || j >= Math.Max(j0, j1)) continue;
                xs.Add(lat[k].I + (j - j0) / (j1 - j0) * (lat[k + 1].I - lat[k].I));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
                for (int i = Math.Max(0, (int)Math.Ceiling(xs[k])); i <= Math.Min(t.Cells, (int)Math.Floor(xs[k + 1])); i++)
                {
                    int index = j * side + i;
                    cells[index] = CoverExtractor.Stamped(cells[index], cls);
                }
        }
    }
}

/// <summary>
/// The cover on work frames (#1507, G2 B3 PR 5a; docs/notes/tools/cube-cover.md): <see cref="CoverExtractor"/>'s area
/// layers and overrides at every vertex of a region's leaves (<see cref="FrameCoverSpace"/>), one frame at a time in the
/// driver's order, written as each leaf's <see cref="LeafKind.Cover"/> through the <see cref="LeafWriter"/>'s cover seam
/// (a vertex two leaves share takes its owner's class). The passes that need the network stage's final roads (street
/// paving, tunnel roofs, laid-out car parks) and the buildings' facades, and the trees, come with B3 4b and PR 5b.
///
/// <code>
/// --cube-cover --cube-out REGION --tlm GPKG [--cover-overrides JSON] [--cube-box minE,minN,maxE,maxN] [--jobs N]
/// </code>
/// </summary>
public static class CubeCoverStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-cover");

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? region = null, tlm = null, overrides = File.Exists("docs/data/cover_overrides.json") ? "docs/data/cover_overrides.json" : null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        int jobs = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-cover": break;
                case "--cube-out": region = args[++i]; break;
                case "--tlm": tlm = args[++i]; break;
                case "--cover-overrides": overrides = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-cover: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || tlm == null) { Console.Error.WriteLine("--cube-cover needs --cube-out REGION (with its heights) and --tlm GPKG"); return 2; }
        if (!File.Exists(tlm)) { Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlm}"); return 1; }

        var sw = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var wanted = heights.Leaves.Where(k => box is not { } b || CubeRoadStage.InBox(k, b)).ToList();
        if (wanted.Count == 0) { Console.Error.WriteLine($"--cube-cover: no leaf with heights in {region}"); return 2; }
        var driver = new FrameDriver(wanted, 0, SwissSeaLevel.Instance, heights.FrameLevel);
        Console.WriteLine($"cube cover: {wanted.Count} leaves in {driver.Frames.Count} frames");
        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Cover], [LatticeKind.Cover]);
        var histogram = new SortedDictionary<CoverClass, long>();
        long vertices = 0, copied = 0, samples = 0;
        using var conn = GeoPackageReader.Open(tlm);
        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var fs = Stopwatch.StartNew();
            var space = new FrameCoverSpace(plan, new FrameGround(plan.Frame, heights.Read, driver.LeafLevel));
            var x = new CoverExtractor(tlm) { OverridesPath = overrides };
            // no margin: an area that touches no wanted leaf draws on none (as the LV95 build's tiles)
            x.RasteriseLayers(conn, space, plan.Region(Lv95Source.Instance, 0));
            var sections = new Dictionary<CubeKey, List<LeafSection>>();
            foreach (var leaf in plan.Core)
            {
                var cells = space.Cover[leaf];
                sections[leaf] = [LeafCover.Encode(cells, plan.Transform(leaf).Bits)];
                vertices += cells.Length;
                foreach (byte c in cells) histogram[(CoverClass)c] = histogram.GetValueOrDefault((CoverClass)c) + 1;
            }
            var seams = writer.WriteFrame(plan.Cell, sections);
            heights.Reload(plan.Cell);
            foreach (var st in seams) { samples += st.Samples; copied += st.Copied; }
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {x.LayerRings.Values.Sum()} rings, {fs.Elapsed.TotalSeconds:F1} s; "
                + string.Join(", ", seams.Select(st => $"seam: {st.Copied} of {st.Samples} shared vertices copied ({st.CopiedAcrossFrames} across frames)"))
                + $"; {sw.Elapsed.TotalSeconds:F0} s");
        }
        var manifestPath = Path.Combine(region, CubeManifest.FileName);
        var sources = File.Exists(manifestPath) ? CubeManifest.FromJson(File.ReadAllText(manifestPath)).Sources : new List<string>();
        const string source = "swissTLM3D area layers and the cover overrides: land cover at every leaf vertex (LV95 -> WGS84 by SwissGeodesy)";
        if (!sources.Contains(source)) sources.Add(source);
        LeafWriter.WriteManifest(region, driver.LeafLevel, driver.FrameLevel, sources);
        Console.WriteLine($"cube cover: {vertices:N0} vertices in {wanted.Count} leaves, {copied:N0} of {samples:N0} shared vertices copied from their owner; "
            + $"{sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        Console.WriteLine("  " + string.Join(", ", histogram.Select(kv => $"{kv.Key}={100.0 * kv.Value / vertices:F2}%")));
        return 0;
    }
}
