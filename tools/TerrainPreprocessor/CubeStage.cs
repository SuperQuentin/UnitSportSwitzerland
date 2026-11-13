using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The cube-sphere tiler (#1443, G1; docs/plans/global-tiling.md §7, tools/cube-tiles note): every
/// source resampled onto cube tiles in true Earth positions, beside the LV95 build (nothing here
/// reads or writes it). Since #1507 (G2 B3) it runs on the work-frame driver (<see cref="FrameDriver"/>:
/// one level-9 frame at a time, (face, y, x) order) and writes each leaf's <see cref="LeafKind.Heights"/>
/// section through the <see cref="LeafWriter"/> into the frame's <c>.cubl</c> bundle (the coarse pyramid
/// keeps <c>.cubb</c>). swissALTI3D at 0.5 m and at 2 m (the corridor) are both read, 0.5 m first.
///
/// <code>
/// --cube --cube-out DIR [--in SWISSALTI3D_DIR ...] [--dem lambert93,DIR[,dz] ...] [--cube-level 14]
///   [--cube-bits 9] [--cube-bundle 9] [--cube-box minE,minN,maxE,maxN] [--tlm GPKG] [--buildings GPKG]
///   [--cube-bdtopo] [--cube-cache DIR] [--cube-cache-tiles 300] [--jobs N] [--cube-check]
///   [--cube-terr LV95_TERRAIN_DIR]
/// </code>
/// </summary>
public static class CubeStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube");

    public sealed class Options
    {
        public List<string> In { get; } = new();
        public List<string> Dems { get; } = new();
        public string? Out, Cache, Tlm, Buildings, Terr;
        public int Level = 14, Bits = -1, Bundle = -1, CacheTiles = 300, Jobs = Environment.ProcessorCount;
        public (double MinE, double MinN, double MaxE, double MaxN)? Box;
        public bool BdTopo, Check, CheckOnly;
        public string? Dump;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var o = new Options();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube": break;
                case "--in": o.In.Add(args[++i]); break;
                case "--dem": o.Dems.Add(args[++i]); break;
                case "--rgealti": o.Dems.Add("lambert93," + args[++i] + ",-0.28"); break;
                case "--cube-out": o.Out = args[++i]; break;
                case "--cube-cache": o.Cache = args[++i]; break;
                case "--cube-level": o.Level = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-bits": o.Bits = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-bundle": o.Bundle = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-cache-tiles": o.CacheTiles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    o.Box = (v[0], v[1], v[2], v[3]);
                    break;
                case "--tlm": o.Tlm = args[++i]; break;
                case "--buildings": o.Buildings = args[++i]; break;
                case "--cube-bdtopo": o.BdTopo = true; break;
                case "--cube-check": o.Check = true; break;
                case "--cube-check-only": o.Check = o.CheckOnly = true; break;
                case "--cube-dump": o.Dump = args[++i]; break;
                case "--cube-terr": o.Terr = args[++i]; break;
                case "--jobs": o.Jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"--cube: unknown option {args[i]}"); return 2;
            }
        }
        if (o.Out == null) { Console.Error.WriteLine("--cube needs --cube-out DIR"); return 2; }
        if (o.Bits < 0) o.Bits = CubeKey.LatticeBits(o.Level); // ~1.2 m at the leaves, at most 512 cells above them
        if (o.Bundle < 0) o.Bundle = o.Level - 5;
        o.Cache ??= o.Out.TrimEnd('/', '\\') + "_temp";
        Directory.CreateDirectory(o.Out);

        // swissALTI3D, finest first: 0.5 m, then the 2 m corridor where 0.5 m has nothing
        var swiss = new List<SwissAltiSource>();
        if (o.In.Count > 0)
            foreach (double spacing in new[] { 0.5, 2.0 })
            {
                var s = new SwissAltiSource(o.In, Path.Combine(o.Cache, "alti"), spacing) { Capacity = spacing == 0.5 ? o.CacheTiles : o.CacheTiles * 16 };
                if (s.Tiles.Count > 0) swiss.Add(s);
            }
        var dems = new List<ForeignDem>();
        foreach (var d in o.Dems)
        {
            var p = d.Split(',');
            dems.Add(new ForeignDem(DemProjection.Parse(p[0]), p[1], p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 0) { Jobs = o.Jobs, CacheBytes = 2048L << 20 });
        }
        var src = new CubeHeightSource(swiss, dems);
        Console.WriteLine($"cube: level {o.Level}, {1 << o.Bits} cells a tile, bundles at level {o.Bundle}; "
            + $"swissALTI3D {string.Join(" + ", swiss.Select(s => $"{s.Tiles.Count} tiles at {s.Spacing.ToString(CultureInfo.InvariantCulture)} m"))}, {dems.Count} foreign DEM(s)");

        if (o.Dump != null) return CubeCheck.Dump(o, CubeKey.Parse(o.Dump));
        if (o.CheckOnly) return CubeCheck.Run(o, src);

        var sw = Stopwatch.StartNew();
        var (keys, french) = SelectTiles(o, swiss, dems);
        var driver = new FrameDriver(keys, 0, SwissSeaLevel.Instance, o.Bundle);
        Console.WriteLine($"cube: {keys.Count} tiles to build ({french.Count} with foreign data) in {driver.Frames.Count} frames, chosen in {sw.Elapsed.TotalSeconds:F1} s");

        var sources = new List<string>();
        foreach (var s in swiss) sources.Add($"swissALTI3D {s.Spacing.ToString(CultureInfo.InvariantCulture)} m (LHN95 -> WGS84 by SwissGeodesy)");
        foreach (var d in dems) sources.Add($"{d.Name} (dz {d.OffsetZ.ToString(CultureInfo.InvariantCulture)} m)");

        SqliteConnection? tlm = o.Tlm != null ? GeoPackageReader.Open(o.Tlm) : null;
        SqliteConnection? b3d = o.Buildings != null ? GeoPackageReader.Open(o.Buildings) : null;
        using var bdtopo = o.BdTopo ? new BdTopoClient() : null;
        var resampler = new CubeResampler(src);
        var features = new CubeFeatures(src, o.Level);
        var writer = new LeafWriter(o.Out, o.Bundle, new[] { LeafKind.Heights }, new[] { LatticeKind.Heights });
        long bytes = 0, roads = 0, buildings = 0, seamSamples = 0, seamCopied = 0, seamAcross = 0;
        double heightSeconds = 0, featureSeconds = 0, writeSeconds = 0, worstFrame = 0;
        long frameMemory = 0;
        var oldOffsets = new List<double>();
        int done = 0, built = 0;

        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var frameClock = Stopwatch.StartNew();
            // features first, into empty holders: the heights are then encoded the moment they exist, so
            // a frame never holds its leaves' heights all at once (1,024 x 1 MB at level 14)
            var holders = plan.Core.ToDictionary(k => k, k => new CubeTile { Key = k, Bits = o.Bits, Heights = Array.Empty<float>() });
            var fs = Stopwatch.StartNew();
            if (tlm != null || bdtopo != null)
            {
                var cur = new CubeHeightSource.Cursors(dems.Count);
                var raw = new List<CubeFeatures.Raw>();
                if (tlm != null && swiss.Count > 0)
                {
                    var box = Lv95Box(holders.Keys, o.Bits);
                    raw.AddRange(features.SwissRoads(tlm, box, cur));
                    raw.AddRange(features.SwissBuildings(tlm, b3d, box, cur));
                }
                if (bdtopo != null && holders.Keys.Any(french.Contains))
                {
                    var (minLat, minLon, maxLat, maxLon) = LatLonBox(holders.Keys.Where(french.Contains));
                    var r = await bdtopo.FetchAsync("BDTOPO_V3:troncon_de_route", minLat, minLon, maxLat, maxLon).ConfigureAwait(false);
                    var b = await bdtopo.FetchAsync("BDTOPO_V3:batiment", minLat, minLon, maxLat, maxLon).ConfigureAwait(false);
                    var fr = features.French(r, b, cur);
                    oldOffsets.AddRange(fr.Select(f => f.OldOffsetM));
                    raw.AddRange(fr);
                }
                CubeFeatures.AddTo(raw, holders);
            }
            featureSeconds += fs.Elapsed.TotalSeconds;

            var sections = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            var hs = Stopwatch.StartNew();
            Parallel.ForEach(plan.Core, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, key =>
            {
                var cur = new CubeHeightSource.Cursors(dems.Count);
                if (resampler.Heights(key, o.Bits, cur) is not { } h) return;
                var holder = holders[key];
                sections[key] = new List<LeafSection> { LeafHeights.Encode(new CubeTile { Key = key, Bits = o.Bits, Heights = h, Roads = holder.Roads, Buildings = holder.Buildings }) };
            });
            heightSeconds += hs.Elapsed.TotalSeconds;
            frameMemory = Math.Max(frameMemory, Process.GetCurrentProcess().WorkingSet64);

            var ws = Stopwatch.StartNew();
            var seams = writer.WriteFrame(plan.Cell, new Dictionary<CubeKey, List<LeafSection>>(sections));
            writeSeconds += ws.Elapsed.TotalSeconds;
            foreach (var st in seams)
            {
                seamSamples += st.Samples;
                seamCopied += st.Copied;
                seamAcross += st.CopiedAcrossFrames;
            }
            long frameBytes = sections.Values.Sum(l => (long)l.Sum(s => s.Payload.Length));
            bytes += frameBytes;
            roads += sections.Keys.Sum(k => holders[k].Roads.Count);
            buildings += sections.Keys.Sum(k => holders[k].Buildings.Count);
            built += sections.Count;
            done += plan.Core.Count;
            worstFrame = Math.Max(worstFrame, frameClock.Elapsed.TotalSeconds);
            string seamText = string.Join(", ", seams.Select(st => $"seam {st.Kind}: {st.Copied} of {st.Samples} shared samples copied ({st.CopiedAcrossFrames} across frames)"));
            Console.WriteLine($"  [{done}/{keys.Count}] frame {plan.Cell.Describe()}: {sections.Count} leaves, {frameBytes / 1e6:F1} MB, "
                + $"{frameClock.Elapsed.TotalSeconds:F1} s, working set {Process.GetCurrentProcess().WorkingSet64 / 1e9:F1} GB; {seamText}; {sw.Elapsed.TotalSeconds:F0} s");
        }
        tlm?.Dispose();
        b3d?.Dispose();

        var manifest = LeafWriter.WriteManifest(o.Out, o.Level, o.Bundle, sources);
        int n = manifest.Tiles.Count;
        Console.WriteLine($"cube: {n} leaves in {driver.Frames.Count} frames, {bytes / 1e6:F1} MB of heights ({(built > 0 ? bytes / built / 1024.0 : 0):F0} KB a leaf), "
            + $"{roads} road runs, {buildings} buildings; {sw.Elapsed.TotalSeconds:F0} s "
            + $"(heights and encoding {heightSeconds:F0} s = {(built > 0 ? heightSeconds * o.Jobs / built : 0):F2} CPU-s a leaf on {o.Jobs} threads, features {featureSeconds:F0} s, "
            + $"seam and write {writeSeconds:F1} s), slowest frame {worstFrame:F1} s, working set after a frame's heights up to {frameMemory / 1e9:F1} GB, "
            + $"peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        Console.WriteLine($"cube: seam pass over the heights: {seamCopied} of {seamSamples} shared edge samples copied from their owner, {seamAcross} across frames (0 expected: G1's edges are rigorous)");
        if (oldOffsets.Count > 0)
        {
            oldOffsets.Sort();
            Console.WriteLine($"cube: French features: SwissProjection would have put them {oldOffsets[oldOffsets.Count / 2]:F1} m (median), "
                + $"{oldOffsets[^1]:F1} m (max) from where they are");
        }
        return o.Check ? CubeCheck.Run(o, src) : 0;
    }

    /// <summary>
    /// The leaf cells to build: under every swissALTI3D tile and every LV95 kilometre with foreign
    /// data, a 100 m lattice of probe points, each giving the cell it falls in (<see cref="FrameDriver.LeavesUnderLv95"/>).
    /// The second set is the cells with foreign data (for BD TOPO).
    /// </summary>
    private static (HashSet<CubeKey> Keys, HashSet<CubeKey> Foreign) SelectTiles(Options o, List<SwissAltiSource> swiss, List<ForeignDem> dems)
    {
        var keys = FrameDriver.LeavesUnderLv95(swiss.SelectMany(s => s.Tiles).Distinct(), o.Level, o.Box);
        var foreign = new HashSet<CubeKey>();
        foreach (var dem in dems)
        {
            var (minE, minN, maxE, maxN) = dem.BoundsLv95Km();
            if (o.Box is { } b) (minE, minN, maxE, maxN) = (Math.Max(minE, (int)(b.MinE / 1000)), Math.Max(minN, (int)(b.MinN / 1000)), Math.Min(maxE, (int)(b.MaxE / 1000) + 1), Math.Min(maxN, (int)(b.MaxN / 1000) + 1));
            var tiles = dem.TilesIn(minE, minN, maxE, maxN, new HashSet<TileId>(), 1);
            foreign.UnionWith(FrameDriver.LeavesUnderLv95(tiles, o.Level, o.Box));
        }
        keys.UnionWith(foreign);
        return (keys, foreign);
    }

    /// <summary>The LV95 box round a set of tiles (their corners and edge midpoints at 500 m), with 50 m to spare.</summary>
    public static (double MinE, double MinN, double MaxE, double MaxN) Lv95Box(IEnumerable<CubeKey> keys, int bits)
    {
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        int s = 1 << bits;
        foreach (var k in keys)
            foreach (var (i, j) in new[] { (0, 0), (s, 0), (0, s), (s, s), (s / 2, 0), (0, s / 2), (s, s / 2), (s / 2, s) })
            {
                var (e, n, _) = SwissGeodesy.FromEcef(k.Vertex(bits, i, j, 550));
                minE = Math.Min(minE, e); maxE = Math.Max(maxE, e);
                minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
            }
        return (minE - 50, minN - 50, maxE + 50, maxN + 50);
    }

    private static (double MinLat, double MinLon, double MaxLat, double MaxLon) LatLonBox(IEnumerable<CubeKey> keys)
    {
        double minLat = 90, minLon = 180, maxLat = -90, maxLon = -180;
        foreach (var k in keys)
            foreach (var (i, j) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                var g = Ellipsoid.Wgs84.ToGeodetic(k.Vertex(0, i, j, 0));
                minLat = Math.Min(minLat, g.LatDeg); maxLat = Math.Max(maxLat, g.LatDeg);
                minLon = Math.Min(minLon, g.LonDeg); maxLon = Math.Max(maxLon, g.LonDeg);
            }
        return (minLat - 0.0005, minLon - 0.0005, maxLat + 0.0005, maxLon + 0.0005);
    }
}
