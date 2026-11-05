using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

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
