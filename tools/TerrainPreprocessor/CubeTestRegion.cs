using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;
using UnitSport.Tools.RoadGen.TestRegion;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The traffic-lights test region on cube leaves (#1647, G2 C7; docs/notes/tools/signal-test-region.md): after
/// <see cref="SignalTestRegion.Run"/> has written the LV95 build, the same region written into <c>.cubl</c> leaves through
/// the cube stages' pieces, so it plays under <c>--frame earth</c> once D deletes the LV95 runtime:
/// <list type="bullet">
/// <item>heights flat at <see cref="SignalTestRegion.Ground"/> metres <b>above sea level</b> (the leaf's undulation added at
/// every vertex: the ellipsoidal heights a leaf holds), the same ground every stage drapes on in the LV95 build;</item>
/// <item>cover at every leaf vertex (<see cref="SignalTestRegion.CoverAt"/>: open grass, the canton borders' band);</item>
/// <item>the box houses, the LV95 build's <c>.bldg</c> moved rigorously into the leaf that holds their middle;</item>
/// <item>the raw lines cut at leaf edges on each frame's plane (<see cref="FrameRoadSpace"/>), draped on the leaf mesh, then the
/// network stage on work frames (4b's <see cref="FrameTileLoop"/>), the canton zones and border marks on the plane;</item>
/// <item>the region files with Earth positions (<see cref="CubeRegionFiles"/>: places, traffic endpoints).</item>
/// </list>
/// <see cref="Compare"/> holds it against the LV95 build: junctions, signals, approaches, poles, canton looks and border marks.
/// <code>
/// --cube-test-region DIR [--cube-out REGION] [--no-lv95] [--compare]
/// </code>
/// DIR is the LV95 build's (RoadGen's <c>--test-region</c>, built first unless <c>--no-lv95</c>); REGION defaults to
/// <c>DIR_cube</c>, the folder the game's earth world reads beside a chunk folder (<c>TerrainPaths.FindLeafDir</c>).
/// </summary>
public static partial class CubeTestRegion
{
    public const int LeafLevel = 14;

    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-test-region");

    /// <summary>Where the leaves of an LV95 build in <paramref name="chunkDir"/> go: its <c>_cube</c> sibling.</summary>
    public static string RegionOf(string chunkDir) => chunkDir.TrimEnd('/', '\\') + "_cube";

    public static int Run(IReadOnlyList<string> args)
    {
        string? chunks = null, region = null;
        bool lv95 = true, compare = false, compareOnly = false;
        (double E, double N, double R)? dump = null;
        bool signalCheck = false, real = false;
        string? at = null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-test-region": chunks = args[++i]; break;
                case "--cube-out": region = args[++i]; break;
                case "--no-lv95": lv95 = false; break;
                case "--compare": compare = true; break;
                case "--verbose": Verbose = true; break;
                // RoadGen's signal and lane check (--signal-check) on the leaves' records
                case "--signal-check": signalCheck = true; lv95 = false; break;
                case "--at": at = args[++i]; break;
                case "--dump-near":
                    var v = args[++i].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    dump = (v[0], v[1], v[2]);
                    break;
                // only the comparison, of a region built before
                case "--compare-only": compare = true; lv95 = false; compareOnly = true; break;
                // a real region's LV95 build (DIR) against its leaves (--cube-out): how often the leaf cut changes a junction
                case "--compare-real": compare = true; lv95 = false; compareOnly = true; real = true; break;
                case "--compare-box":
                    var bx = args[++i].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    box = (bx[0], bx[1], bx[2], bx[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-test-region: unknown option {args[i]}"); return 2;
            }
        }
        if (chunks == null) { Console.Error.WriteLine("--cube-test-region DIR [--cube-out REGION] [--no-lv95] [--compare]"); return 2; }
        region ??= RegionOf(chunks);
        if (dump is { } d) { DumpNear(chunks, region, d.E, d.N, d.R, Console.WriteLine); return 0; }
        if (signalCheck) return SignalCheckLeaves(region, at, Console.WriteLine);
        string temp = RawRoads.DefaultTempDir(chunks);
        if (lv95)
        {
            int lv = SignalTestRegion.Run(chunks, temp, Console.WriteLine);
            if (lv == 1) return 1;
        }
        int built = compareOnly ? 0 : Build(chunks, temp, region, Console.WriteLine);
        if (built != 0 || !compare) return built;
        return Compare(chunks, region, Console.WriteLine, real, box);
    }

    /// <summary>
    /// RoadGen's signal and lane check (<see cref="RoadGen.Diagnostics.SignalCheck"/>: every plan valid, every approach's poles,
    /// lane records against their pockets and arrows, junction paths) over the leaves' network, positions printed as LV95.
    /// </summary>
    public static int SignalCheckLeaves(string region, string? at, Action<string> log)
    {
        var heights = new RegionHeights(region);
        var anchors = new Dictionary<RoadTile, EnuFrame>(ReferenceEqualityComparer.Instance);
        var tiles = new List<RoadTile>();
        foreach (var leaf in heights.Leaves)
        {
            if (heights.ReadKind(leaf, LeafKind.Roads) is not { } sec) continue;
            var tile = LeafRoads.Decode(sec);
            anchors[tile] = leaf.Anchor;
            tiles.Add(tile);
        }
        log($"signal check on {tiles.Count} leaves with roads in {region}");
        return RoadGen.Diagnostics.SignalCheck.Run(tiles, (t, x, z) =>
        {
            var (e, n, _) = SwissGeodesy.FromEcef(anchors[t].ToEcef(x, 0, z));
            return (e, n);
        }, at, log);
    }

    /// <summary>The region's LV95 km tiles.</summary>
    public static List<TileId> Tiles()
    {
        var tiles = new List<TileId>();
        for (int e = SignalTestRegion.MinTileE; e <= SignalTestRegion.MaxTileE; e++)
            for (int n = SignalTestRegion.MinTileN; n <= SignalTestRegion.MaxTileN; n++)
                tiles.Add(new TileId(e, n));
        return tiles;
    }

    /// <summary>
    /// Writes the leaves into <paramref name="region"/> from the LV95 build in <paramref name="chunks"/> (its raw input in
    /// <paramref name="temp"/>). Returns 0, or 1 when the folder holds something this did not write.
    /// </summary>
    public static int Build(string chunks, string temp, string region, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        string regionTemp = region.TrimEnd('/', '\\') + "_temp";
        foreach (string dir in new[] { region, regionTemp })
            if (!Clear(dir, log)) return 1;
        foreach (string dir in new[] { region, regionTemp })
        {
            // first, so a run that stops half way can be cleared by the next
            File.WriteAllText(Path.Combine(dir, SignalTestRegion.MarkerFile), "the traffic-lights test region on cube leaves (#1647)\n");
            File.WriteAllText(Path.Combine(dir, ".gdignore"), "");
        }

        var wanted = FrameDriver.LeavesUnderLv95(Tiles(), LeafLevel);
        var driver = new FrameDriver(wanted, CubeNetworkStage.MarginM, SwissSeaLevel.Instance);
        int frameLevel = driver.FrameLevel;
        log($"cube test region (#1647): {wanted.Count} leaves in {driver.Frames.Count} frames");

        // ---- heights and cover: lattice kinds, every vertex computed (rigorous edges, so the seams copy nothing)
        var heightWriter = new LeafWriter(region, frameLevel, [LeafKind.Heights], [LatticeKind.Heights]);
        var coverWriter = new LeafWriter(region, frameLevel, [LeafKind.Cover], [LatticeKind.Cover]);
        var borders = SignalTestRegion.BorderLines();
        long borderVertices = 0;
        foreach (var plan in driver.Frames)
        {
            var heights = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            var cover = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            Parallel.ForEach(plan.Core, leaf =>
            {
                var t = plan.Transform(leaf);
                int n = t.Cells + 1;
                var h = new float[n * n];
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                        h[j * n + i] = (float)(SignalTestRegion.Ground + t.UndulationAt(i, j));
                heights[leaf] = [LeafHeights.Encode(new CubeTile { Key = leaf, Bits = t.Bits, Heights = h })];
                var cells = CoverOf(plan.Frame, t, borders, out int marked);
                Interlocked.Add(ref borderVertices, marked);
                cover[leaf] = [LeafCover.Encode(cells, t.Bits)];
            });
            heightWriter.WriteFrame(plan.Cell, new(heights));
            coverWriter.WriteFrame(plan.Cell, new(cover));
        }
        log(string.Create(CultureInfo.InvariantCulture, $"  heights {SignalTestRegion.Ground} m above sea level, cover: {borderVertices:N0} border vertices; {sw.Elapsed.TotalSeconds:F1} s"));

        // ---- buildings: the LV95 build's boxes, each in the leaf holding its middle, every vertex rigorous
        var byLeaf = new Dictionary<CubeKey, List<Building>>();
        int buildingCount = 0;
        foreach (var id in Tiles())
        {
            if (Facades.Read(chunks, id) is not { } tile) continue;
            foreach (var b in tile.Buildings)
            {
                var t = b.Triangles;
                double me = 0, mn = 0;
                int count = t.Length / 3;
                for (int k = 0; k + 2 < t.Length; k += 3) { me += id.MinE + t[k]; mn += id.MaxN - t[k + 2]; }
                var g = SwissGeodesy.ToWgs84(me / count, mn / count, SignalTestRegion.Ground);
                var leaf = CubeKey.FromLatLon(g.LatDeg, g.LonDeg, LeafLevel);
                var anchor = leaf.Anchor;
                var local = new float[t.Length];
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    var (x, y, z) = anchor.ToLocal(SwissGeodesy.ToEcef(id.MinE + t[k], id.MaxN - t[k + 2], t[k + 1]));
                    (local[k], local[k + 1], local[k + 2]) = ((float)x, (float)y, (float)z);
                    minY = Math.Min(minY, (float)y);
                    maxY = Math.Max(maxY, (float)y);
                }
                if (!byLeaf.TryGetValue(leaf, out var list)) byLeaf[leaf] = list = new();
                list.Add(new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = minY, MaxY = maxY, Triangles = local });
                buildingCount++;
            }
        }
        var buildingWriter = new LeafWriter(region, frameLevel, [LeafKind.Buildings]);
        foreach (var plan in driver.Frames)
            buildingWriter.WriteFrame(plan.Cell, plan.Core.ToDictionary(k => k,
                k => byLeaf.TryGetValue(k, out var l) ? new List<LeafSection> { LeafBuildings.Encode(new BuildingTile { Buildings = l }) } : new List<LeafSection>()));
        log($"  {buildingCount} buildings in {byLeaf.Count} leaves");

        // ---- raw roads: each line onto the frame's plane, cut at leaf edges, densified and draped on the leaf mesh
        string rawDir = CubeRoadStage.RawDir(regionTemp);
        Directory.CreateDirectory(rawDir);
        var rawWriter = new LeafWriter(rawDir, frameLevel, [LeafKind.Roads, LeafKind.RoadKeys, LeafKind.Parking]);
        var heightsRead = new RegionHeights(region);
        var lines = SignalTestRegion.RawLines();
        long rawSegments = 0;
        foreach (var plan in driver.Frames)
        {
            var ground = new FrameGround(plan.Frame, heightsRead.Read, LeafLevel);
            var space = new FrameRoadSpace(plan, ground);
            var segs = plan.Core.ToDictionary(k => k, _ => new List<RoadSegment>());
            var keys = plan.Core.ToDictionary(k => k, _ => new List<RoadSourceKey?>());
            foreach (var line in lines)
            {
                var pts = line.Points;
                var x = new double[pts.Length];
                var y = new double[pts.Length];
                var z = new double[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    (x[i], y[i]) = space.FromLv95(pts[i].E, pts[i].N, SignalTestRegion.Ground);
                    z[i] = line.Height;
                }
                double along = 0;
                foreach (var piece in space.Split(new GeoPackageReader.Polyline(x, y, z)))
                {
                    var p = piece.Points;
                    if (space.Writes(piece.Tile))
                    {
                        var dense = new List<(double X, double Y)> { (p[0].E, p[0].N) };
                        for (int i = 1; i < p.Count; i++)
                        {
                            double d = Math.Sqrt((p[i].E - p[i - 1].E) * (p[i].E - p[i - 1].E) + (p[i].N - p[i - 1].N) * (p[i].N - p[i - 1].N));
                            int steps = Math.Max(1, (int)Math.Ceiling(d / SignalTestRegion.RawSpacing));
                            for (int s = 1; s <= steps; s++)
                                dense.Add((p[i - 1].E + (p[i].E - p[i - 1].E) * s / steps, p[i - 1].N + (p[i].N - p[i - 1].N) * s / steps));
                        }
                        var arr = new float[dense.Count * 3];
                        for (int i = 0; i < dense.Count; i++)
                        {
                            // the extractor's drape: the leaf mesh under the point, lifted as the LV95 build lifts it off its flat ground
                            double h = (ground.Sample(dense[i].X, dense[i].Y) ?? SignalTestRegion.Ground) + (line.Height - SignalTestRegion.Ground);
                            (arr[i * 3], arr[i * 3 + 1], arr[i * 3 + 2]) = space.ToTile(piece.Tile, dense[i].X, dense[i].Y, h);
                        }
                        var tpl = line.Template;
                        segs[piece.Tile].Add(new RoadSegment
                        {
                            Class = tpl.Class, Surface = tpl.Surface, Flags = tpl.Flags, Width = tpl.Width, Points = arr, Attributes = tpl.Attributes,
                        });
                        keys[piece.Tile].Add(new RoadSourceKey(line.Uuid, 0, along));
                        rawSegments++;
                    }
                    for (int i = 1; i < p.Count; i++)
                        along += Math.Sqrt((p[i].E - p[i - 1].E) * (p[i].E - p[i - 1].E) + (p[i].N - p[i - 1].N) * (p[i].N - p[i - 1].N));
                }
            }
            rawWriter.WriteFrame(plan.Cell, plan.Core.ToDictionary(k => k, k => segs[k].Count == 0 ? new List<LeafSection>()
                : new List<LeafSection> { LeafRoads.Encode(new RoadTile { Segments = segs[k] }), LeafRoadKeys.Encode(keys[k]) }));
        }
        log($"  {lines.Count} lines as {rawSegments:N0} raw pieces; {sw.Elapsed.TotalSeconds:F1} s");

        // ---- the network stage on work frames, with the LV95 build's options (its overlay, nodes, tram stops, cantons, marks)
        var options = new TileRewriter.Options(
            Halo: CubeNetworkStage.DefaultHalo, OsmOverlay: Path.Combine(temp, "osm_overlay.tsv"), TramStops: Path.Combine(temp, "tram_stops.tsv"),
            Cantons: TileRewriter.Cantons.Of(SignalTestRegion.CantonTiles), GantryFacts: Path.Combine(regionTemp, "gantry_facts.tsv"));
        var data = RegionData.Load(options, log);
        var gantries = data.LoadGantries(options, log);
        var raw = new RegionHeights(rawDir);
        var roadWriter = new LeafWriter(region, frameLevel, [LeafKind.Roads]);
        var totals = new FrameTileLoop.Counts();
        foreach (var plan in driver.Frames)
        {
            var frame = plan.Frame;
            var frameOptions = options with
            {
                Decorate = tile => SignalTestRegion.MarkBorders(tile, (px, py) =>
                {
                    var (lat, lon) = frame.Plane.FromPlane(px, py);
                    return SwissGeodesy.ToLv95(lat, lon);
                }),
            };
            var loop = new FrameTileLoop(plan, LeafLevel, heightsRead, raw, chunks, data, gantries, frameOptions, CubeNetworkStage.DefaultBlock, totals);
            var stats = TileRewriter.Run(loop, frameOptions, _ => { });
            roadWriter.WriteFrame(plan.Cell, loop.Sections);
            heightsRead.Reload(plan.Cell);
            log($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {loop.Sections.Count(e => e.Value.Count > 0)} with roads, {stats.Junctions} junctions, {loop.Frame.Signals} signals");
        }
        log($"  network: {totals.Tiles} leaves with roads, {totals.Segments:N0} segments, {totals.Junctions} junction records, {totals.Signals} signals, {totals.Approaches} approaches");

        // ---- the region files with Earth positions (places, traffic endpoints), then the manifest
        int files = CubeRegionFiles.Run(["--cube-region-files", "--chunks", chunks, "--cube-out", region]);
        if (files != 0) log($"  region files: exit {files}");
        LeafWriter.WriteManifest(region, LeafLevel, frameLevel, [
            "the traffic-lights test region (#386, #1647): flat at 500 m above sea level, RoadGen's design through the cube stages",
        ]);
        foreach (string dir in new[] { region, regionTemp })
        {
            File.WriteAllText(Path.Combine(dir, SignalTestRegion.MarkerFile), "the traffic-lights test region on cube leaves (#1647)\n");
            File.WriteAllText(Path.Combine(dir, ".gdignore"), "");
        }
        log(string.Create(CultureInfo.InvariantCulture, $"cube test region: {sw.Elapsed.TotalSeconds:F1} s, leaves in {region}"));
        return files == 0 ? 0 : 2;
    }

    /// <summary>A leaf's cover lattice (j·side + i): LV95 at every 32nd vertex rigorously, bilinear between (a millimetre).</summary>
    private static byte[] CoverOf(WorkFrame frame, LeafTransform t, IReadOnlyList<(bool AlongN, double At, double From, double To)> borders, out int marked)
    {
        int side = t.Cells + 1, step = 32, c = t.Cells / step + 1;
        var ctl = new (double E, double N)[c * c];
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        for (int cj = 0; cj < c; cj++)
            for (int ci = 0; ci < c; ci++)
            {
                var (x, y) = t.LatticeToPlane(ci * step, cj * step);
                var (lat, lon) = frame.Plane.FromPlane(x, y);
                var p = ctl[cj * c + ci] = SwissGeodesy.ToLv95(lat, lon);
                (minE, minN, maxE, maxN) = (Math.Min(minE, p.E), Math.Min(minN, p.N), Math.Max(maxE, p.E), Math.Max(maxN, p.N));
            }
        var cells = new byte[side * side];
        marked = 0;
        // no border within reach of the leaf: open grass all over
        const double reach = 10;
        if (!borders.Any(b => b.AlongN
                ? b.At >= minE - reach && b.At <= maxE + reach && b.To >= minN - reach && b.From <= maxN + reach
                : b.At >= minN - reach && b.At <= maxN + reach && b.To >= minE - reach && b.From <= maxE + reach))
            return cells;
        for (int j = 0; j < side; j++)
        {
            int cj = Math.Min(j / step, c - 2);
            double fj = (j - cj * step) / (double)step;
            for (int i = 0; i < side; i++)
            {
                int ci = Math.Min(i / step, c - 2);
                double fi = (i - ci * step) / (double)step;
                var (a, b, d, e) = (ctl[cj * c + ci], ctl[cj * c + ci + 1], ctl[(cj + 1) * c + ci], ctl[(cj + 1) * c + ci + 1]);
                double pe = (a.E * (1 - fi) + b.E * fi) * (1 - fj) + (d.E * (1 - fi) + e.E * fi) * fj;
                double pn = (a.N * (1 - fi) + b.N * fi) * (1 - fj) + (d.N * (1 - fi) + e.N * fi) * fj;
                var cls = SignalTestRegion.CoverAt(pe, pn, borders);
                if (cls == CoverClass.Open) continue;
                cells[j * side + i] = (byte)cls;
                marked++;
            }
        }
        return cells;
    }

    /// <summary>Empties a folder this wrote before (it holds the marker); refuses one that holds anything else.</summary>
    private static bool Clear(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return true; }
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) return true;
        if (!File.Exists(Path.Combine(dir, SignalTestRegion.MarkerFile)))
        {
            log($"{dir} is not empty and was not written by --cube-test-region (no {SignalTestRegion.MarkerFile}): refusing to clear it");
            return false;
        }
        foreach (string f in Directory.EnumerateFiles(dir)) File.Delete(f);
        foreach (string d in Directory.EnumerateDirectories(dir)) Directory.Delete(d, recursive: true);
        return true;
    }
}
