using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The buildings stage on cube leaves (#1507, G2 B3 PR 3): swissBUILDINGS3D with GWR, and French buildings from
/// BD TOPO, written as each leaf's <see cref="LeafKind.Buildings"/> section in its anchor frame. The same
/// <see cref="BuildingExtractor"/> and <see cref="FranceBuildings"/> as the LV95 build, through a
/// <see cref="FrameFeatureSpace"/> per work frame: solids cleaned and joined to GWR in LV95, placed on the frame's
/// plane, owned whole by the leaf under their centre (as an LV95 tile owns them), re-seated on the leaf mesh
/// (<see cref="FrameGround"/> over the heights already in the region's <c>.cubl</c>), written through the
/// <see cref="LeafWriter"/>. French buildings take the stage's <c>YearBuilt</c> marker, as in the LV95 tiles.
///
/// <code>
/// --cube-buildings --cube-out REGION (--buildings-gdb ZIP ... | --buildings GPKG) [--gwr data.sqlite]
///   [--france minLon,minLat,maxLon,maxLat] [--cube-box minE,minN,maxE,maxN] [--tiles-file F] [--temp DIR]
///   [--cube-margin 250] [--jobs N]
/// </code>
/// </summary>
public static class CubeBuildingStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-buildings");

    /// <summary>
    /// How far round a frame its sources are read (metres): a building is kept by the frame owning its centre, and
    /// after the stray-face pass one spans at most a few hundred metres, so a vertex of every owned one is in reach.
    /// </summary>
    public const double DefaultMarginM = 250;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? dir = null, gpkg = null, gwr = null, temp = null, france = null, tilesFile = null;
        var gdb = new List<string>();
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        double margin = DefaultMarginM;
        int jobs = Environment.ProcessorCount;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-buildings": break;
                case "--cube-out": dir = args[++i]; break;
                case "--buildings-gdb": gdb.Add(args[++i]); break;
                case "--buildings": gpkg = args[++i]; break;
                case "--gwr": gwr = args[++i]; break;
                case "--temp": temp = args[++i]; break;
                case "--france": france = args[++i]; break;
                case "--tiles-file": tilesFile = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-margin": margin = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-buildings: unknown option {args[i]}"); return 2;
            }
        }
        if (dir == null || (gdb.Count == 0 && gpkg == null && france == null))
        {
            Console.Error.WriteLine("--cube-buildings needs --cube-out REGION and --buildings-gdb ZIP / --buildings GPKG and/or --france BOX");
            return 2;
        }
        string manifestPath = Path.Combine(dir, CubeManifest.FileName);
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"no {CubeManifest.FileName} in {dir}: build the heights first (--cube)"); return 2; }
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));
        var franceBox = france == null ? null : FranceStage.ParseBox(france);
        if (france != null && franceBox == null) { Console.Error.WriteLine("--france wants minLon,minLat,maxLon,maxLat in degrees"); return 2; }

        // the leaves with heights (the ground), inside the box and under the listed LV95 tiles if given
        var wanted = new HashSet<CubeKey>();
        foreach (var t in manifest.Tiles)
        {
            if (!t.Kinds.Contains(nameof(LeafKind.Heights))) continue;
            var key = CubeKey.Parse(t.Key);
            if (key.Level != manifest.LeafLevel) continue;
            if (box is { } b)
            {
                var c = key.Centre;
                var (e, n) = SwissGeodesy.ToLv95(c.LatDeg, c.LonDeg);
                if (e < b.MinE || e > b.MaxE || n < b.MinN || n > b.MaxN) continue;
            }
            wanted.Add(key);
        }
        if (tilesFile != null) wanted.IntersectWith(FrameDriver.LeavesUnderLv95(TileId.ReadList(tilesFile), manifest.LeafLevel));
        if (wanted.Count == 0) { Console.Error.WriteLine("--cube-buildings: no leaf with heights to put buildings on"); return 2; }

        var sw = Stopwatch.StartNew();
        BuildingExtractor? extractor = null;
        if (gdb.Count > 0) extractor = BuildingStage.OpenGdb(gdb, Path.Combine(temp ?? dir.TrimEnd('/', '\\') + "_temp", "buildgdb"), gwr);
        else if (gpkg != null) extractor = BuildingStage.OpenGeoPackage(gpkg, gwr);
        if ((gdb.Count > 0 || gpkg != null) && extractor == null) return 1;

        // BD TOPO once for the box, as the LV95 stage fetches it; each frame keeps what it owns
        List<BdFeature> footprints = new();
        if (franceBox is { } fb)
        {
            using var client = new BdTopoClient();
            footprints = await client.FetchAsync("BDTOPO_V3:batiment", fb.MinLat, fb.MinLon, fb.MaxLat, fb.MaxLon).ConfigureAwait(false);
            Console.WriteLine($"cube buildings: fetched {footprints.Count} BD TOPO footprints in {client.Requests} requests");
        }

        var driver = new FrameDriver(wanted, margin, SwissSeaLevel.Instance, manifest.BundleLevel);
        var writer = new LeafWriter(dir, manifest.BundleLevel, new[] { LeafKind.Buildings });
        Console.WriteLine($"cube buildings: {wanted.Count} leaves in {driver.Frames.Count} frames, margin {margin} m; setup {sw.Elapsed.TotalSeconds:F1} s");

        var franceStats = new FranceBuildings.Stats();
        long swiss = 0, french = 0, triangles = 0, bytes = 0, leaves = 0;
        var byKind = new SortedDictionary<BuildingKind, int>();
        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var frameClock = Stopwatch.StartNew();
            var ground = new FrameGround(plan.Frame, key => ReadHeights(writer, key, manifest.BundleLevel), manifest.LeafLevel);
            var space = new FrameFeatureSpace(plan, ground, margin);
            var byLeaf = extractor?.Extract(space) ?? plan.Core.ToDictionary(k => k, _ => new List<Building>());
            int frameSwiss = byLeaf.Values.Sum(l => l.Count), frameFrench = 0;
            foreach (var (leaf, list) in FranceBuildings.Build(footprints, space, franceStats))
            {
                foreach (var b in list)
                    byLeaf[leaf].Add(new Building
                    {
                        Kind = b.Kind, Egid = b.Egid, YearBuilt = FranceStage.FrenchMarker, Floors = b.Floors,
                        MinY = b.MinY, MaxY = b.MaxY, Triangles = b.Triangles,
                    });
                frameFrench += list.Count;
            }

            var sections = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            Parallel.ForEach(byLeaf.Where(kv => kv.Value.Count > 0), new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, kv =>
                sections[kv.Key] = new List<LeafSection> { LeafBuildings.Encode(new BuildingTile { Buildings = kv.Value }) });
            writer.WriteFrame(plan.Cell, new Dictionary<CubeKey, List<LeafSection>>(sections));

            foreach (var b in byLeaf.Values.SelectMany(l => l))
            {
                triangles += b.TriangleCount;
                byKind[b.Kind] = byKind.GetValueOrDefault(b.Kind) + 1;
            }
            long frameBytes = sections.Values.Sum(l => (long)l[0].Payload.Length);
            swiss += frameSwiss;
            french += frameFrench;
            bytes += frameBytes;
            leaves += sections.Count;
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {frameSwiss} Swiss + {frameFrench} French buildings in {sections.Count} of {plan.Core.Count} leaves, "
                + $"{frameBytes / 1e6:F1} MB, {frameClock.Elapsed.TotalSeconds:F1} s, working set {Process.GetCurrentProcess().WorkingSet64 / 1e9:F1} GB");
        }

        var sources = manifest.Sources.ToList();
        if (extractor != null) sources.Add("swissBUILDINGS3D 3.0 with GWR (LV95 -> WGS84 by SwissGeodesy)");
        if (franceBox != null) sources.Add("IGN BD TOPO batiment (Licence Ouverte 2.0)");
        LeafWriter.WriteManifest(dir, manifest.LeafLevel, manifest.BundleLevel, sources.Distinct());

        Console.WriteLine($"cube buildings: {swiss} Swiss + {french} French buildings in {leaves} leaves, {triangles:N0} triangles, {bytes / 1e6:F1} MB; "
            + $"{sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        if (extractor != null)
        {
            double pct = extractor.Total == 0 ? 0 : 100.0 * extractor.Matched / extractor.Total;
            Console.WriteLine($"  cadastre matched: {extractor.Matched}/{extractor.Total} ({pct:F0}%); re-seated on the leaf mesh: {extractor.Reseated}, max shift {extractor.MaxLift:F1} m; "
                + $"stray faces dropped: {extractor.StrayFaces} from {extractor.StrayBuildings} buildings");
        }
        if (franceBox != null)
            Console.WriteLine($"  French: {franceStats.Built} built ({franceStats.Pitched} pitched, {franceStats.Flat} flat), {franceStats.NoHeight} with no usable height, {franceStats.Skipped} skipped");
        Console.WriteLine("  by kind: " + string.Join(", ", byKind.Select(kv => $"{kv.Key}={kv.Value}")));
        return 0;
    }

    /// <summary>A leaf's heights from its frame's bundle, null when it has none.</summary>
    private static CubeTile? ReadHeights(LeafWriter writer, CubeKey key, int frameLevel)
    {
        string path = writer.PathOf(key.AncestorAt(frameLevel));
        if (!File.Exists(path) || CubeBundle.ReadBytes(path, key, CubeBundle.LeafMagic) is not { } bytes) return null;
        return CubeLeafCodec.ReadSection(bytes, LeafKind.Heights) is { } s ? LeafHeights.Decode(s) : null;
    }
}
