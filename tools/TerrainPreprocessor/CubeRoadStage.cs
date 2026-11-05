using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The road stage on work frames (#1507, G2 B3 PR 2; docs/notes/tools/cube-frames.md): the LV95 build's road
/// extraction, tunnel holes and car parks (<see cref="RoadExtractor"/>, <see cref="TunnelCarver"/>,
/// <see cref="ParkingAreaExtractor{TTile}"/>) and BD TOPO's French roads (<see cref="FranceRoads"/>), each run
/// unchanged on a frame's plane through <see cref="FrameRoadSpace"/>, over the leaves of a region whose heights
/// the <c>--cube</c> stage wrote. One frame at a time in the driver's order:
/// <list type="bullet">
/// <item>the region's <c>.cubl</c> gets each core leaf's <see cref="LeafKind.Holes"/> (final, as the LV95 build's
/// <c>.holes</c> beside its terrain);</item>
/// <item>the temporary <c>.cubl</c> (<c>&lt;temp&gt;/roads_raw/</c>) gets the raw kinds the network stage starts from:
/// <see cref="LeafKind.Roads"/> (the raw segments, no Network flag), <see cref="LeafKind.RoadKeys"/> and
/// <see cref="LeafKind.Parking"/>, as <see cref="RawRoads"/> and <see cref="RawParking"/> keep them for LV95.</item>
/// </list>
///
/// <code>
/// --cube-roads --cube-out REGION --tlm GPKG [--route-keys SQLITE] [--cover-overrides JSON] [--cube-bdtopo]
///   [--cube-box minE,minN,maxE,maxN] [--cube-temp DIR] [--jobs N]
/// </code>
/// </summary>
public static class CubeRoadStage
{
    /// <summary>How far round a frame its lines are read: the LV95 build's ring of one tile (<see cref="TileRegion.RingM"/>).</summary>
    public const double MarginM = TileRegion.RingM;

    /// <summary>How far round a frame's leaves car park rings are read: a ring's centroid can lie outside it, on a leaf it does not touch.</summary>
    public const double ParkingReadM = 500;

    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-roads");

    /// <summary>Where a region's raw road kinds go: <c>&lt;temp&gt;/roads_raw</c>, one <c>.cubl</c> per frame.</summary>
    public static string RawDir(string tempDir) => Path.Combine(tempDir, RawRoads.DirName);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // the hand-traced car parks and cover (#84), as the LV95 build takes them by default
        string? region = null, temp = null, tlm = null, routeKeys = null, overrides = File.Exists("docs/data/cover_overrides.json") ? "docs/data/cover_overrides.json" : null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        bool bdtopo = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-roads": break;
                case "--cube-out": region = args[++i]; break;
                case "--cube-temp": temp = args[++i]; break;
                case "--tlm": tlm = args[++i]; break;
                case "--route-keys": routeKeys = args[++i]; break;
                case "--cover-overrides": overrides = args[++i]; break;
                case "--cube-bdtopo": bdtopo = true; break;
                case "--jobs": i++; break;   // one frame at a time; kept so a --cube command line can be reused
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-roads: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || (tlm == null && !bdtopo)) { Console.Error.WriteLine("--cube-roads needs --cube-out REGION (with its heights) and --tlm GPKG and/or --cube-bdtopo"); return 2; }
        if (tlm != null && !File.Exists(tlm)) { Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlm}"); return 1; }
        temp ??= region.TrimEnd('/', '\\') + "_temp";
        string rawDir = RawDir(temp);

        var sw = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var wanted = heights.Leaves.Where(k => box is not { } b || InBox(k, b)).ToList();
        if (wanted.Count == 0) { Console.Error.WriteLine($"--cube-roads: no leaf with heights in {region}" + (box != null ? " inside the box" : "")); return 2; }
        var driver = new FrameDriver(wanted, MarginM, SwissSeaLevel.Instance, heights.FrameLevel);
        // without swissTLM3D (France alone, Brest) only BD TOPO's roads are written
        var extractor = tlm != null ? new RoadExtractor(tlm, routeKeys) : null;
        Console.WriteLine($"cube roads: {wanted.Count} leaves in {driver.Frames.Count} frames; "
            + (extractor != null ? $"{extractor.CycleKeyCount} cycle / {extractor.MtbKeyCount} MTB route keys" : "no swissTLM3D"));

        var holesWriter = new LeafWriter(region, driver.FrameLevel, [LeafKind.Holes]);
        var rawWriter = new LeafWriter(rawDir, driver.FrameLevel, [LeafKind.Roads, LeafKind.RoadKeys, LeafKind.Parking]);
        using var client = bdtopo ? new BdTopoClient() : null;
        long segments = 0, points = 0, holeQuads = 0, holeLeaves = 0, lots = 0, french = 0, frenchSkipped = 0;
        var byClass = new SortedDictionary<RoadClass, int>();
        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var fs = Stopwatch.StartNew();
            var ground = new FrameGround(plan.Frame, heights.Read, driver.LeafLevel);
            var space = new FrameRoadSpace(plan, ground);
            var tiles = plan.Core.ToDictionary(k => k, space.NewTile);
            var keys = plan.Core.ToDictionary(k => k, _ => new List<RoadSourceKey?>());
            var holeQuadsOf = new Dictionary<CubeKey, HashSet<int>>();
            if (extractor != null)
            {
                var x = extractor.Extract(space);
                (tiles, holeQuadsOf) = (x.Tiles, x.Holes);
                keys = x.SourceKeys.ToDictionary(e => e.Key, e => e.Value.Select(k => k is { } a ? new RoadSourceKey(a.Uuid, a.Part, a.FromM) : (RoadSourceKey?)null).ToList());
            }

            if (client != null)
            {
                var (minLat, minLon, maxLat, maxLon) = LatLonBox(plan);
                var lines = await client.FetchAsync("BDTOPO_V3:troncon_de_route", minLat, minLon, maxLat, maxLon).ConfigureAwait(false);
                var stats = new FranceRoads.Stats();
                foreach (var (leaf, list) in FranceRoads.Build(lines, space, stats))
                {
                    tiles[leaf].Segments.AddRange(list);
                    keys[leaf].AddRange(Enumerable.Repeat((RoadSourceKey?)null, list.Count));
                }
                french += stats.Segments;
                frenchSkipped += stats.Skipped;
            }

            var parking = tlm != null ? new ParkingAreaExtractor<CubeKey>(tlm, space, ParkingReadM) : null;
            parking?.Extract(overrides);

            var raw = new Dictionary<CubeKey, List<LeafSection>>();
            var holes = new Dictionary<CubeKey, List<LeafSection>>();
            foreach (var leaf in plan.Core)
            {
                var tile = tiles[leaf];
                var list = new List<LeafSection>();
                if (tile.Segments.Count > 0)
                {
                    list.Add(LeafRoads.Encode(tile));
                    list.Add(LeafRoadKeys.Encode(keys[leaf]));
                }
                if (parking != null && parking.Lots.TryGetValue(leaf, out var ls) && ls.Count > 0)
                {
                    list.Add(LeafParking.Encode(ls.Select(l => ToLeaf(plan, ground, leaf, l)).ToList()));
                    lots += ls.Count;
                }
                raw[leaf] = list;
                holes[leaf] = new List<LeafSection>();
                if (holeQuadsOf.TryGetValue(leaf, out var quads) && quads.Count > 0)
                {
                    holes[leaf].Add(LeafHoles.Encode(quads, plan.Transform(leaf).Bits));
                    holeQuads += quads.Count;
                    holeLeaves++;
                }
                segments += tile.Segments.Count;
                foreach (var s in tile.Segments)
                {
                    points += s.PointCount;
                    byClass[s.Class] = byClass.GetValueOrDefault(s.Class) + 1;
                }
            }
            rawWriter.WriteFrame(plan.Cell, raw);
            holesWriter.WriteFrame(plan.Cell, holes);
            // the bundle was rewritten: its entries moved, so the next frames' grounds read it by its new index
            heights.Reload(plan.Cell);
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {raw.Values.Count(l => l.Count > 0)} with raw roads, "
                + $"{holeQuadsOf.Where(e => space.Writes(e.Key)).Sum(e => e.Value.Count)} hole quads, {fs.Elapsed.TotalSeconds:F1} s, "
                + $"working set {Process.GetCurrentProcess().WorkingSet64 / 1e9:F1} GB; {sw.Elapsed.TotalSeconds:F0} s");
        }

        var manifestPath = Path.Combine(region, CubeManifest.FileName);
        var sources = File.Exists(manifestPath) ? CubeManifest.FromJson(File.ReadAllText(manifestPath)).Sources : new List<string>();
        const string tlmSource = "swissTLM3D roads: tunnel portal holes (LV95 -> WGS84 by SwissGeodesy)";
        if (!sources.Contains(tlmSource)) sources.Add(tlmSource);
        LeafWriter.WriteManifest(region, driver.LeafLevel, driver.FrameLevel, sources);
        Console.WriteLine($"cube roads: {segments:N0} raw segments ({points:N0} points), {french:N0} of them French ({frenchSkipped:N0} BD TOPO sections skipped), "
            + $"{holeQuads:N0} hole quads in {holeLeaves} leaves, {lots:N0} car parks; {sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        Console.WriteLine("  by class: " + string.Join(", ", byClass.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"  raw kinds in {rawDir}, holes in {region}");
        return 0;
    }

    /// <summary>A lot's ring from the frame's plane into its leaf, each point on the leaf mesh (0 m where there is none).</summary>
    private static LeafParkingLot ToLeaf(FramePlan plan, IGround ground, CubeKey leaf, RawParking.Lot lot)
    {
        var t = plan.Transform(leaf);
        var pts = new float[lot.Count * 3];
        for (int i = 0; i < lot.Count; i++)
        {
            double x = lot.Ring[i * 2], y = lot.Ring[i * 2 + 1];
            var (lx, ly, lz) = t.PlaneToLocal(x, y, ground.Sample(x, y) ?? 0);
            (pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) = ((float)lx, (float)ly, (float)lz);
        }
        return new LeafParkingLot(lot.Cover, lot.Source, pts);
    }

    /// <summary>A leaf whose centre lies in an LV95 box (the stages' --cube-box).</summary>
    public static bool InBox(CubeKey k, (double MinE, double MinN, double MaxE, double MaxN) b)
    {
        var c = k.Centre;
        var (e, n) = SwissGeodesy.ToLv95(c.LatDeg, c.LonDeg);
        return e >= b.MinE && e <= b.MaxE && n >= b.MinN && n <= b.MaxN;
    }

    /// <summary>A frame's outline and its margin as a latitude and longitude box (BD TOPO's WFS query).</summary>
    private static (double MinLat, double MinLon, double MaxLat, double MaxLon) LatLonBox(FramePlan plan)
    {
        var (a0, b0, a1, b1) = plan.QueryBox(new GeographicSource(SwissSeaLevel.Instance));
        return (a0, b0, a1, b1);
    }
}

/// <summary>
/// A cube region's leaf heights as a stage reads them (#1507): which leaves have a <see cref="LeafKind.Heights"/>
/// section, and each one decoded on demand from its frame's <c>.cubl</c> (indexes read once). Thread-safe.
/// </summary>
public sealed class RegionHeights
{
    private readonly string _dir;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CubeKey, Dictionary<CubeKey, (long Offset, int Length)>> _index = new();

    public IReadOnlyList<CubeKey> Leaves { get; }

    public int FrameLevel { get; }

    public RegionHeights(string dir)
    {
        _dir = dir;
        var leaves = new List<CubeKey>();
        int frameLevel = WorkFrame.Level;
        foreach (var path in Directory.EnumerateFiles(dir, "*.cubl").Order(StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(path);
            var index = CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic);
            if (index.Count == 0) continue;
            // F{face}_L{level}_{x}_{y}.cubl
            var stem = Path.GetFileNameWithoutExtension(path).Split('_');
            var frame = new CubeKey(int.Parse(stem[0][1..], CultureInfo.InvariantCulture), int.Parse(stem[1][1..], CultureInfo.InvariantCulture),
                int.Parse(stem[2], CultureInfo.InvariantCulture), int.Parse(stem[3], CultureInfo.InvariantCulture));
            frameLevel = frame.Level;
            _index[frame] = index;
            foreach (var (key, (offset, length)) in index)
            {
                fs.Position = offset;
                var head = new byte[CubeLeafCodec.HeadBytes];
                fs.ReadExactly(head);
                int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
                var table = new byte[count * CubeLeafCodec.TableEntryBytes];
                fs.ReadExactly(table);
                for (int k = 0; k < count; k++)
                    if ((LeafKind)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(k * CubeLeafCodec.TableEntryBytes)) == LeafKind.Heights)
                    {
                        leaves.Add(key);
                        break;
                    }
            }
        }
        Leaves = leaves;
        FrameLevel = frameLevel;
    }

    /// <summary>Reads a frame's bundle index again, after a stage rewrote the bundle (its entries move).</summary>
    public void Reload(CubeKey frame)
    {
        string path = Path.Combine(_dir, CubeBundle.LeafFileName(frame));
        if (!File.Exists(path)) { _index.TryRemove(frame, out _); return; }
        using var fs = File.OpenRead(path);
        _index[frame] = CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic);
    }

    /// <summary>A leaf's heights, or null when the region has none for it.</summary>
    public CubeTile? Read(CubeKey leaf) => ReadKind(leaf, LeafKind.Heights) is { } s ? LeafHeights.Decode(s) : null;

    /// <summary>A leaf's section of one kind (#1507: the network stage reads the raw kinds this way), or null.</summary>
    public LeafSection? ReadKind(CubeKey leaf, LeafKind kind)
    {
        var frame = leaf.AncestorAt(FrameLevel);
        if (!_index.TryGetValue(frame, out var index) || !index.TryGetValue(leaf, out var at)) return null;
        using var fs = File.OpenRead(Path.Combine(_dir, CubeBundle.LeafFileName(frame)));
        fs.Position = at.Offset;
        var bytes = new byte[at.Length];
        fs.ReadExactly(bytes);
        return CubeLeafCodec.ReadSection(bytes, kind);
    }
}
