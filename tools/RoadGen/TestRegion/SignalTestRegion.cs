namespace UnitSport.Tools.RoadGen.TestRegion;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Rewrite;

/// <summary>
/// <c>RoadGen --test-region DIR</c> (#386): a small synthetic chunk directory for traffic lights.
/// Flat ground, a hand-designed road network written as the road extractor's raw output (TLM class
/// names, widths, importance and owner through the extractor's own <see cref="RoadFormat"/> parsers,
/// keys per segment), an OSM overlay and <c>osm_nodes.tsv</c> with a <c>highway=traffic_signals</c>
/// node at each signalised junction (data first: OSM decides where the lights are), and a few box
/// buildings where room has to run short; then the normal network stage (<see cref="TileRewriter"/>),
/// so pockets, paint, bike lanes, boxes, signals, poles, <c>SGNL</c> and <c>LANE</c> all come from
/// the real pipeline. Nothing here special-cases the planner: the lane sets come from the room the
/// design leaves (arm length, buildings, road class). Region and junctions:
/// docs/notes/tools/signal-test-region.md.
/// </summary>
public static class SignalTestRegion
{
    /// <summary>Tiles written: a row of junction tiles (N 1322) with a flat tile row either side. Outside Switzerland on purpose: no real tile, cached or not, shares an id.</summary>
    public const int MinTileE = 2910, MaxTileE = 2915, MinTileN = 1321, MaxTileN = 1323;
    /// <summary>The junctions' row, LV95 N: the middle of the tile row N 1322.</summary>
    public const double RowN = 1322500;
    /// <summary>The ground, metres (as the fixture courses).</summary>
    public const double Ground = 500;
    /// <summary>Region centre: the manifest's suggested origin.</summary>
    public const double OriginE = 2913000, OriginN = RowN;
    /// <summary>Written into both directories; a directory holding anything without it is never wiped.</summary>
    public const string MarkerFile = "signal_test_region.txt";

    // the road extractor's drape (RoadExtractor.DrapeOffset, ClassLift) and densifying (MaxDrapeSpacing)
    private const double DrapeOffset = 0.35, MaxDrapeSpacing = 4.0;

    /// <summary>A signalised junction of the region: what it is for and the car lanes each approach is designed to get (left to right, "|" between lanes; null = not designed, reported only).</summary>
    public sealed record Junction(string Name, double E, double N, string Case, IReadOnlyDictionary<string, string?> Design);

    /// <summary>One TLM line as the extractor would read it, with its OSM overlay row.</summary>
    private sealed record Line(string Id, string Objektart, string Verkehrsbedeutung, string Eigentuemer, (double E, double N)[] Points,
        string Highway, string Sidewalk = "", string Cycleway = "")
    {
        public string Uuid => "{386-" + Id + "}";
    }

    /// <summary>A box building, LV95 footprint corners and height.</summary>
    private sealed record Box(double E0, double N0, double E1, double N1, double Height);

    private sealed record Design(List<Line> Lines, List<Box> Buildings, List<Junction> Junctions);

    // ---- the design ----------------------------------------------------------------------------

    public static IReadOnlyList<Junction> Junctions => Build().Junctions;

    private static Design Build()
    {
        var lines = new List<Line>();
        var boxes = new List<Box>();
        var junctions = new List<Junction>();
        const double n = RowN;
        static Dictionary<string, string?> Arms(string? w, string? e, string? s, string? nn) =>
            new() { ["W"] = w, ["E"] = e, ["S"] = s, ["N"] = nn };

        // the main road A, west to east through every junction: a 10 m cantonal through road at the two
        // ideal crossroads, 8 m from the mismatched one on; split where TLM would (junctions, attribute changes)
        double j1 = 2910500, j2 = 2911500, j3 = 2912500, j3b = 2913500, j4 = 2914500, j5a = 2915470, j5b = 2915530;
        void A(string id, double from, double to, string objektart, string sidewalk = "") =>
            lines.Add(new Line(id, objektart, "Durchgangsstrasse", "Kanton", [(from, n), (to, n)], "secondary", sidewalk));
        A("A0", 2910050, j1, "10m Strasse");
        A("A1", j1, 2910900, "10m Strasse");
        A("A2", 2910900, j2, "10m Strasse", "yes");
        A("A3", j2, 2911900, "10m Strasse", "yes");
        A("A4", 2911900, j3, "8m Strasse");
        A("A5", j3, j3b, "8m Strasse");
        A("A6", j3b, j4, "8m Strasse");
        A("A7", j4, j5a, "8m Strasse");
        A("A8", j5a, j5b, "8m Strasse");
        A("A9", j5b, 2915950, "8m Strasse");

        // cross roads, drawn south to north
        void Cross(string id, double e, double from, double to, string objektart, string importance, string highway,
            string sidewalk = "", string cycleway = "") =>
            lines.Add(new Line(id, objektart, importance, "Gemeinde", [(e, from), (e, to)], highway, sidewalk, cycleway));

        // 1. ideal crossroads, painted bike lanes (rural: the #120 rule paints them on both 10 m roads).
        // The north road's cyclists share the lane (OSM cycleway=shared_lane), so a left turn into it from
        // the west waits in a bike box; the other left turns get an advanced bike line.
        Cross("J1S", j1, n - 400, n, "10m Strasse", "Verbindungsstrasse", "secondary");
        Cross("J1N", j1, n, n + 400, "10m Strasse", "Verbindungsstrasse", "secondary", cycleway: "shared_lane");
        junctions.Add(new Junction("J1-ideal-lanes", j1, n, "ideal crossroads, painted bike lanes, bike box (W) and advanced lines",
            Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));

        // 2. the same crossroads in town (OSM maps sidewalks): separated bike paths
        Cross("J2S", j2, n - 400, n, "10m Strasse", "Verbindungsstrasse", "secondary", sidewalk: "yes");
        Cross("J2N", j2, n, n + 400, "10m Strasse", "Verbindungsstrasse", "secondary", sidewalk: "yes");
        junctions.Add(new Junction("J2-ideal-paths", j2, n, "ideal crossroads in town, separated bike paths: bike signals, square crossings",
            Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));

        // 3. mismatched room. W: a long arm, L|T|R. E: houses 1.5 m from the kerb from 52 m out: only the
        // shortest left pocket (20 + 20 m) fits, and a right pocket never reaches past the left's storage:
        // L|TR. N: houses 1 m from the kerb from the corner on: no room for a widening, one lane. S: a 6 m
        // road 52 m long to a T where it gives way: too short for a left pocket, long enough for a right.
        Cross("J3N", j3, n, n + 400, "8m Strasse", "Verbindungsstrasse", "tertiary");
        Cross("J3S", j3, n - 52, n, "6m Strasse", "k_W", "tertiary");
        lines.Add(new Line("J3D0", "6m Strasse", "k_W", "Gemeinde", [(j3 - 300, n - 52), (j3, n - 52)], "tertiary"));
        lines.Add(new Line("J3D1", "6m Strasse", "k_W", "Gemeinde", [(j3, n - 52), (j3 + 300, n - 52)], "tertiary"));
        boxes.Add(new Box(j3 + 52, n + 5.5, j3 + 68, n + 15.5, 9));
        boxes.Add(new Box(j3 + 71, n + 5.5, j3 + 88, n + 15.5, 12));
        boxes.Add(new Box(j3 + 91, n + 5.5, j3 + 108, n + 15.5, 9));
        boxes.Add(new Box(j3 - 17, n + 12, j3 - 5, n + 30, 12));
        boxes.Add(new Box(j3 - 17, n + 33, j3 - 5, n + 52, 9));
        boxes.Add(new Box(j3 - 17, n + 55, j3 - 5, n + 75, 12));
        boxes.Add(new Box(j3 - 17, n + 78, j3 - 5, n + 100, 9));
        junctions.Add(new Junction("J3-mismatch", j3, n, "mismatched room: long arm, houses at the kerb, a short arm",
            Arms("L|T|R", "L|TR", "LT|R", "LTR")));

        // 3b. a narrower road class: a 4 m road (no pockets on that class) meets an 8 m and a 6 m road. The 6 m
        // road's left pocket has no through lane to carry on into the 4 m road (#123: no main road out), so
        // that approach keeps its own lane for left and through, and gets a right pocket
        Cross("J3bS", j3b, n - 400, n, "6m Strasse", "Verbindungsstrasse", "tertiary");
        Cross("J3bN", j3b, n, n + 400, "4m Strasse", "k_W", "unclassified");
        junctions.Add(new Junction("J3b-narrow-arm", j3b, n, "a 4 m road (single lane) across the 8 m main road and a 6 m road",
            Arms("L|T|R", "L|T|R", "LT|R", "LTR")));

        // 4. a T: the main road's left pocket (from the east) gets its own phase
        Cross("J4S", j4, n - 400, n, "6m Strasse", "Verbindungsstrasse", "tertiary");
        junctions.Add(new Junction("J4-tee", j4, n, "T junction: the main road's left-pocket phase",
            Arms("T|R", "L|T", "L|R", null)));

        // 5. two crossroads 60 m apart: queues reach back across the one before
        foreach (var (id, e) in new[] { ("J5a", j5a), ("J5b", j5b) })
        {
            Cross(id + "S", e, n - 400, n, "6m Strasse", "Verbindungsstrasse", "tertiary");
            Cross(id + "N", e, n, n + 400, "6m Strasse", "Verbindungsstrasse", "tertiary");
        }
        junctions.Add(new Junction("J5a-pair-west", j5a, n, "two lights 60 m apart, the west one", Arms("L|T|R", null, "L|T|R", "L|T|R")));
        junctions.Add(new Junction("J5b-pair-east", j5b, n, "two lights 60 m apart, the east one", Arms(null, "L|T|R", "L|T|R", "L|T|R")));
        return new Design(lines, boxes, junctions);
    }

    // ---- build ---------------------------------------------------------------------------------

    public static int Run(string chunkDir, string? tempDir, Action<string> log)
    {
        tempDir ??= RawRoads.DefaultTempDir(chunkDir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (string dir in new[] { chunkDir, tempDir })
            if (!Clear(dir, log)) return 1;

        var design = Build();
        var tiles = new List<TileId>();
        for (int e = MinTileE; e <= MaxTileE; e++)
            for (int nn = MinTileN; nn <= MaxTileN; nn++)
                tiles.Add(new TileId(e, nn));

        WriteTerrain(chunkDir, tiles);
        WriteBuildings(chunkDir, tiles, design.Buildings);
        WritePlaces(chunkDir, design.Junctions);
        var roadTiles = WriteRawRoads(RawRoads.DirFor(tempDir), design.Lines);
        WriteOverlay(tempDir, design.Lines);
        WriteSignals(tempDir, design);
        string about = About(design);
        foreach (string dir in new[] { chunkDir, tempDir })
        {
            File.WriteAllText(Path.Combine(dir, MarkerFile), about);
            File.WriteAllText(Path.Combine(dir, ".gdignore"), "");
        }
        log($"test region (#386): {tiles.Count} flat tiles, {design.Lines.Count} lines on {roadTiles.Count} road tiles, "
            + $"{design.Buildings.Count} buildings, {design.Junctions.Count} signal nodes; written in {clock.Elapsed.TotalSeconds:F1} s");

        var stats = TileRewriter.Run(chunkDir, roadTiles, new TileRewriter.Options(
            RawDir: RawRoads.DirFor(tempDir), OsmOverlay: Path.Combine(tempDir, "osm_overlay.tsv")), log);
        log(stats.Network.Format(stats.TilesWritten));
        log("");
        int bad = Report(chunkDir, design.Junctions, log);
        log(string.Create(CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds:F1} s; chunks {chunkDir}, raw input {tempDir}"));
        log(bad == 0 ? "[testregion] RESULT: ok" : $"[testregion] RESULT: FAILED {bad} approaches not as designed");
        return bad == 0 ? 0 : 2;
    }

    /// <summary>Empties a directory this command wrote before (it holds the marker); refuses one that holds anything else.</summary>
    private static bool Clear(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return true; }
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) return true;
        if (!File.Exists(Path.Combine(dir, MarkerFile)))
        {
            log($"{dir} is not empty and was not written by --test-region (no {MarkerFile}): refusing to clear it");
            return false;
        }
        foreach (string f in Directory.EnumerateFiles(dir)) File.Delete(f);
        foreach (string d in Directory.EnumerateDirectories(dir)) Directory.Delete(d, recursive: true);
        return true;
    }

    private static void WriteTerrain(string dir, List<TileId> tiles)
    {
        ushort q = ChunkFormat.Quantize(Ground);
        float h = (float)ChunkFormat.Dequantize(q);
        var manifest = new TerrainManifest
        {
            SuggestedOriginLv95 = new Lv95Point { E = OriginE, N = OriginN },
            BoundsLv95 = new Lv95Bounds
            {
                MinE = MinTileE * 1000.0, MinN = MinTileN * 1000.0, MaxE = (MaxTileE + 1) * 1000.0, MaxN = (MaxTileN + 1) * 1000.0,
            },
        };
        var horizon = new Dictionary<TileId, ushort[]>();
        int size = ChunkFormat.GridSize, coarse = (size - 1) / ChunkFormat.CoarseStride + 1;
        foreach (var id in tiles)
        {
            var grid = new ChunkGrid(id, Enumerable.Repeat(q, size * size).ToArray(), h, h);
            using (var fs = File.Create(Path.Combine(dir, ChunkFormat.ChunkFileName(id)))) ChunkCodec.Encode(grid, fs);
            using (var fs = File.Create(Path.Combine(dir, ChunkFormat.CoarseFileName(id))))
                ChunkCodec.Encode(new ChunkGrid(id, Enumerable.Repeat(q, coarse * coarse).ToArray(), h, h, ChunkFormat.CoarseStride), fs);
            using (var fs = File.Create(Path.Combine(dir, CoverFormat.FileName(id))))
                CoverFormat.Encode(id, new byte[CoverFormat.Size * CoverFormat.Size], fs);   // open grass
            using (var fs = File.Create(Path.Combine(dir, TreeFormat.FileName(id)))) TreeFormat.Encode(id, [], fs);
            horizon[id] = HorizonFormat.Extract(grid);
            manifest.Tiles.Add(new ManifestTile { E = id.E, N = id.N, Min = h, Max = h });
        }
        using (var fs = File.Create(Path.Combine(dir, HorizonFormat.FileName))) HorizonFormat.Encode(horizon, fs);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJson());
    }

    /// <summary>Box buildings (walls and a flat roof) as the building stage writes them: tile-local triangles, X east, Y up, Z south.</summary>
    private static void WriteBuildings(string dir, List<TileId> tiles, List<Box> boxes)
    {
        foreach (var id in tiles)
        {
            var list = new List<Building>();
            foreach (var b in boxes.Where(b => TileId.FromLv95((b.E0 + b.E1) * 0.5, (b.N0 + b.N1) * 0.5) == id))
            {
                float x0 = (float)(b.E0 - id.MinE), x1 = (float)(b.E1 - id.MinE);
                float z0 = (float)(id.MaxN - b.N1), z1 = (float)(id.MaxN - b.N0);   // z0 north
                float y0 = (float)Ground, y1 = (float)(Ground + b.Height);
                var t = new List<float>();
                void Quad(float ax, float az, float bx, float bz)   // a wall from a to b, outward on its left seen from above
                {
                    t.AddRange([ax, y0, az, bx, y0, bz, bx, y1, bz]);
                    t.AddRange([ax, y0, az, bx, y1, bz, ax, y1, az]);
                }
                Quad(x0, z0, x1, z0);   // north
                Quad(x1, z0, x1, z1);   // east
                Quad(x1, z1, x0, z1);   // south
                Quad(x0, z1, x0, z0);   // west
                t.AddRange([x0, y1, z0, x0, y1, z1, x1, y1, z1]);   // roof
                t.AddRange([x0, y1, z0, x1, y1, z1, x1, y1, z0]);
                list.Add(new Building { Kind = BuildingKind.Apartment, Floors = (byte)Math.Round(b.Height / 3), MinY = y0, MaxY = y1, Triangles = [.. t] });
            }
            using var fs = File.Create(Path.Combine(dir, BuildingFormat.FileName(id)));
            BuildingCodec.Encode(new BuildingTile { Id = id, Buildings = list }, fs);
        }
    }

    /// <summary>The junctions as places: Tab (or <c>--goto J3</c>) finds them. No buildings counted, so no occasion or town sound picks them.</summary>
    private static void WritePlaces(string dir, List<Junction> junctions)
    {
        var index = new PlaceIndex();
        foreach (var j in junctions)
            index.Places.Add(new Place { Name = j.Name, Canton = "", E = j.E, N = j.N, Kind = PlaceKind.Town });
        File.WriteAllText(Path.Combine(dir, PlaceIndex.FileName), index.ToJson());
    }

    /// <summary>
    /// The lines as the road extractor leaves them in <c>roads_raw/</c>: clipped to tiles, densified,
    /// draped on the flat ground (offset and class lift as the extractor's), attributes from the TLM
    /// values through the extractor's parsers, a key per segment (uuid, part 0, along-line metre).
    /// </summary>
    private static List<TileId> WriteRawRoads(string rawDir, List<Line> lines)
    {
        var segments = new SortedDictionary<(int E, int N), (List<RoadSegment> Segments, List<RawRoads.Key?> Keys)>();
        foreach (var line in lines)
        {
            var cls = RoadFormat.ParseClass(line.Objektart);
            var surface = RoadFormat.ParseSurface("Hart");
            var flags = RoadFormat.ParseFlags(null, null, null, "Falsch");
            var attr = new RoadAttributes(
                Flags: RoadFormat.ParseAttrFlags("Falsch", line.Eigentuemer),
                Layer: RoadFormat.LayerFor("0", flags),
                Priority: RoadFormat.PriorityFor(cls, line.Verkehrsbedeutung),
                WidthCm: RoadFormat.NominalWidthCm(line.Objektart));
            float y = (float)(Ground + DrapeOffset + (12 - (int)cls) * 0.012);
            double along = 0;
            foreach (var (tile, piece) in SplitAtTiles(line.Points))
            {
                var dense = Densify(piece);
                var pts = new float[dense.Count * 3];
                for (int i = 0; i < dense.Count; i++)
                {
                    pts[i * 3] = (float)(dense[i].E - tile.MinE);
                    pts[i * 3 + 1] = y;
                    pts[i * 3 + 2] = (float)(tile.MaxN - dense[i].N);
                }
                if (!segments.TryGetValue((tile.E, tile.N), out var into)) segments[(tile.E, tile.N)] = into = (new(), new());
                into.Segments.Add(new RoadSegment
                {
                    Class = cls, Surface = surface, Flags = flags, Width = RoadFormat.WidthFor(cls, flags), Points = pts, Attributes = attr,
                });
                into.Keys.Add(new RawRoads.Key(line.Uuid, 0, along));
                for (int i = 1; i < piece.Count; i++) along += Dist(piece[i - 1], piece[i]);
            }
        }
        var ids = new List<TileId>();
        foreach (var ((e, nn), (list, keys)) in segments)
        {
            var id = new TileId(e, nn);
            RawRoads.Write(rawDir, new RoadTile { Id = id, Segments = list }, keys);
            ids.Add(id);
        }
        return ids;
    }

    private static double Dist((double E, double N) a, (double E, double N) b) =>
        Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));

    /// <summary>A polyline cut where it crosses a tile edge, the crossing point ending one piece and starting the next.</summary>
    private static List<(TileId Tile, List<(double E, double N)> Piece)> SplitAtTiles((double E, double N)[] line)
    {
        var pieces = new List<(TileId Tile, List<(double E, double N)> Piece)>();
        for (int i = 1; i < line.Length; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            var cuts = new List<double> { 0, 1 };
            Crossings(a.E, b.E, cuts);
            Crossings(a.N, b.N, cuts);
            cuts.Sort();
            for (int k = 1; k < cuts.Count; k++)
            {
                var p = At(cuts[k - 1]);
                var q = At(cuts[k]);
                var tile = TileId.FromLv95((p.E + q.E) / 2, (p.N + q.N) / 2);
                if (pieces.Count == 0 || pieces[^1].Tile != tile) pieces.Add((tile, new() { p }));
                pieces[^1].Piece.Add(q);
            }

            (double E, double N) At(double t) => t == 0 ? a : t == 1 ? b : (a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t);
        }
        return pieces;

        static void Crossings(double from, double to, List<double> into)
        {
            for (double k = Math.Floor(Math.Min(from, to) / 1000) + 1; k * 1000 < Math.Max(from, to); k++)
                into.Add((k * 1000 - from) / (to - from));
        }
    }

    /// <summary>At most <see cref="MaxDrapeSpacing"/> between points, as the extractor drapes.</summary>
    private static List<(double E, double N)> Densify(List<(double E, double N)> piece)
    {
        var dense = new List<(double E, double N)> { piece[0] };
        for (int i = 1; i < piece.Count; i++)
        {
            var (a, b) = (piece[i - 1], piece[i]);
            int steps = Math.Max(1, (int)Math.Ceiling(Dist(a, b) / MaxDrapeSpacing));
            for (int s = 1; s <= steps; s++)
                dense.Add((a.E + (b.E - a.E) * s / steps, a.N + (b.N - a.N) * s / steps));
        }
        return dense;
    }

    private static double Length(Line line)
    {
        double total = 0;
        for (int i = 1; i < line.Points.Length; i++) total += Dist(line.Points[i - 1], line.Points[i]);
        return total;
    }

    private const string FileTag = "pbf=signal-test-region tlm=signal-test-region";

    /// <summary><c>osm_overlay.tsv</c> (format: docs/notes/tools/osm-overlay.md): one row over each whole line, sorted by uuid.</summary>
    private static void WriteOverlay(string tempDir, List<Line> lines)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"# osm_overlay v1 {FileTag} bbox={MinTileE * 1000},{MinTileN * 1000},{(MaxTileE + 1) * 1000},{(MaxTileN + 1) * 1000} synthetic (#386)\n");
        sb.Append("uuid\tpart\tfrom_m\tto_m\tosm_way\tdir\thighway\toneway\tlanes\tlanes_fwd\tlanes_bwd\twidth"
            + "\tsidewalk_left\tsidewalk_right\tcycleway_left\tcycleway_right\tturn_lanes_fwd\tturn_lanes_bwd\troundabout\ttram\n");
        int way = 0;
        foreach (var line in lines.OrderBy(l => l.Uuid, StringComparer.Ordinal))
            sb.Append(inv, $"{line.Uuid}\t0\t0.0\t{Length(line):F1}\t{386000 + ++way}\t+\t{line.Highway}\t\t\t\t\t")
                .Append(inv, $"\t{line.Sidewalk}\t{line.Sidewalk}\t{line.Cycleway}\t{line.Cycleway}\t\t\t0\t0\n");
        File.WriteAllText(Path.Combine(tempDir, "osm_overlay.tsv"), sb.ToString());
    }

    /// <summary>
    /// <c>osm_nodes.tsv</c>: a <c>highway=traffic_signals</c> node on each designed junction's node
    /// (junction <c>node</c>, at the end of one of its lines), sorted by kind and uuid as the writer sorts.
    /// </summary>
    private static void WriteSignals(string tempDir, Design design)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(string Uuid, string Text)>();
        long id = 386100;
        foreach (var j in design.Junctions)
        {
            // the line ending (else starting) at the junction
            var (line, atEnd) = design.Lines.Select(l => (l, true)).FirstOrDefault(x => Dist(x.l.Points[^1], (j.E, j.N)) < 0.01);
            if (line is null) (line, atEnd) = (design.Lines.First(l => Dist(l.Points[0], (j.E, j.N)) < 0.01), false);
            double along = atEnd ? Length(line) : 0;
            rows.Add((line.Uuid, string.Create(inv,
                $"signal\t{++id}\t{j.E:F1}\t{j.N:F1}\t{line.Uuid}\t0\t{along:F1}\tnode\t{(atEnd ? "end" : "start")}\t{j.E:F1}\t{j.N:F1}\t\t\t\t\t\thighway=traffic_signals\n")));
        }
        var sb = new StringBuilder();
        sb.Append(inv, $"# osm_nodes v1 {FileTag} bbox={MinTileE * 1000},{MinTileN * 1000},{(MaxTileE + 1) * 1000},{(MaxTileN + 1) * 1000} synthetic (#386)\n");
        sb.Append("kind\tosm_id\te\tn\tuuid\tpart\talong_m\tjunction\tline_end\tend_e\tend_n\tdir\tvalue\tto_uuid\tto_part\tto_end\ttags\n");
        foreach (var (_, text) in rows.OrderBy(r => r.Uuid, StringComparer.Ordinal)) sb.Append(text);
        File.WriteAllText(Path.Combine(tempDir, OsmNodesReader.FileName), sb.ToString());
    }

    private static string About(Design design)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Traffic lights test region (#386), written by RoadGen --test-region; rebuilt from scratch on every run.\n");
        sb.Append(inv, $"Tiles E {MinTileE}-{MaxTileE} N {MinTileN}-{MaxTileN}, flat at {Ground:F0} m. docs/notes/tools/signal-test-region.md\n\n");
        foreach (var j in design.Junctions)
            sb.Append(inv, $"{j.Name,-16} {j.E:F0},{j.N:F0}  {j.Case}\n");
        return sb.ToString();
    }

    // ---- report --------------------------------------------------------------------------------

    /// <summary>
    /// The junction table: per designed junction its plan's cycle and phases, and per approach the
    /// lanes as built (the <c>LANE</c> record, left to right) against the design. Returns how many
    /// designed approaches came out otherwise (or are missing).
    /// </summary>
    public static int Report(string chunkDir, IReadOnlyList<Junction> junctions, Action<string> log)
    {
        var inv = CultureInfo.InvariantCulture;
        var tiles = new Dictionary<TileId, RoadTile>();
        RoadTile? Tile(TileId id)
        {
            if (tiles.TryGetValue(id, out var t)) return t;
            string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return tiles[id] = RoadCodec.Decode(fs);
        }

        int bad = 0;
        log("junction         LV95 centre        approach: lanes as built, left to right (designed)        cycle  phases");
        foreach (var j in junctions)
        {
            var id = TileId.FromLv95(j.E, j.N);
            var tile = Tile(id);
            int k = -1;
            double best = 15;
            for (int i = 0; tile is not null && i < tile.Signals.Count; i++)
            {
                double d = Math.Sqrt(Math.Pow(id.MinE + tile.Signals[i].X - j.E, 2) + Math.Pow(id.MaxN - tile.Signals[i].Z - j.N, 2));
                if (d < best) { best = d; k = i; }
            }
            if (tile is null || k < 0)
            {
                log(string.Create(inv, $"{j.Name,-16} {j.E:F0},{j.N:F0}    NO TRAFFIC LIGHTS"));
                bad += j.Design.Count(a => a.Value is not null);
                continue;
            }
            var signal = tile.Signals[k];
            var plan = signal.Plan;
            var arms = new List<string>();
            var seen = new HashSet<string>();
            foreach (var a in tile.Approaches.Where(a => a.Signal == k).OrderBy(a => Compass(a.Heading)))
            {
                string dir = Compass(a.Heading);
                seen.Add(dir);
                string car = string.Join("|", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l => Moves(l.Moves)));
                string all = string.Join(" | ", a.Lanes.Select(l => (l.Kind == ApproachLaneKind.Bike ? "b" : "") + Moves(l.Moves)
                    + (l.StopBehind > 0.01f ? "[box]" : l.StopBehind < -0.01f ? "[adv]" : "")));
                var arm = plan.Arms[a.SignalArm];
                string designed = j.Design.GetValueOrDefault(dir) ?? "-";
                bool ok = designed == "-" || designed == car;
                if (!ok) bad++;
                arms.Add($"{dir}: {all}{(arm.BikeSignal ? " +bike signal" : "")} ({designed}{(ok ? "" : " MISMATCH")})");
            }
            foreach (var (dir, want) in j.Design)
                if (want is not null && !seen.Contains(dir)) { bad++; arms.Add($"{dir}: MISSING ({want})"); }
            log(string.Create(inv, $"{j.Name,-16} {j.E:F0},{j.N:F0}   {string.Join("; ", arms)}   {plan.Cycle:F0} s   {Phases(plan)}"));
        }
        return bad;
    }

    /// <summary>The approach direction an arm's outward heading names: the west arm's traffic comes from the west.</summary>
    private static string Compass(double heading)
    {
        double deg = (heading * 180 / Math.PI % 360 + 360) % 360;
        return deg < 45 || deg >= 315 ? "E" : deg < 135 ? "N" : deg < 225 ? "W" : "S";
    }

    private static string Moves(SignalMoves m) =>
        (m.HasFlag(SignalMoves.Left) ? "L" : "") + (m.HasFlag(SignalMoves.Through) ? "T" : "") + (m.HasFlag(SignalMoves.Right) ? "R" : "");

    /// <summary>The plan's phases: the car groups green together, in cycle order (arrows marked ^), all-red gaps left out.</summary>
    public static string Phases(SignalPlan plan)
    {
        var phases = new List<string>();
        string last = "";
        for (double t = 0; t < plan.Cycle; t += 0.25)
        {
            var green = new List<string>();
            for (int g = 0; g < plan.Groups.Count; g++)
            {
                var group = plan.Groups[g];
                if (group.Kind is not (SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow)) continue;
                var iv = group.Intervals.FirstOrDefault(x => x.From <= t && t < x.To);
                if (iv.Aspect != SignalAspect.Green) continue;
                green.Add(Compass(plan.Arms[group.Arm].Heading) + ":" + Moves(group.Moves) + (group.Kind == SignalGroupKind.Car ? "" : "^"));
            }
            string set = string.Join(" ", green.Order(StringComparer.Ordinal));
            if (set.Length == 0 || set == last) continue;
            if (phases.Count == 0 || phases[^1] != set) phases.Add(set);
            last = set;
        }
        if (phases.Count > 1 && phases[0] == phases[^1]) phases.RemoveAt(phases.Count - 1);   // the cycle wraps
        return string.Join(" / ", phases.Select(p => "[" + p + "]"));
    }
}
