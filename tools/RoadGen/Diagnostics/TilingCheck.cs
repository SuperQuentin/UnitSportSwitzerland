namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Rewrite;
using UnitSport.Tools.RoadGen.TestRegion;

/// <summary>
/// The bar of #1660 ("junctions should be generated the same, no matter where the tile edges are"): the network stage run
/// on the same raw lines under several tilings of the LV95 plane (<see cref="GridUnits"/>: shifted, turned, other cell
/// sizes), every record compared in plane coordinates against the first tiling's. The raw lines of an LV95 build are
/// joined back across their km cuts (<see cref="LineJoin"/>) and cut again at each tiling's cells, so the runs differ in
/// their tiling and in nothing else; the stage, its inputs (ground, walls, OSM, cantons) and the plane are the same.
/// Nothing is written: each tiling's tiles are kept in memory.
/// </summary>
public static class TilingCheck
{
    /// <summary>The km grid (the reference), shifted three ways, turned like leaves, and turned 45° in small cells.</summary>
    public const string DefaultTilings = "0,0,0,1000;250,0,0,1000;0,500,0,1000;334,666,0,1000;0,0,28.5,610;100,200,45,400";

    /// <summary>
    /// Bars: positions of the placed records (m), a junction record's area (share and m2), paint per bin and type, centrelines (m).
    /// Paint 1 m: a line laid off a curved centreline steps at a cut by its offset times the curve's turn there (the format offsets
    /// a piece's end along its own last stretch, as at every tile seam), so a dash moves across a bin's edge by up to ~0.6 m.
    /// </summary>
    public const double PlaceBarM = 0.05, AreaShare = 0.02, AreaM2 = 1.0, PaintBarM = 1.0, PaintBarM2 = 0.05, CentrelineBarM = 0.01;

    /// <summary>Paint is summed per type on a fixed lattice of this many metres (the plane's, every tiling alike).</summary>
    public const double PaintBin = 25;

    public sealed record Settings(
        string Chunks, string Temp, (double MinE, double MinN, double MaxE, double MaxN) Box, IReadOnlyList<GridUnits> Tilings,
        double BlockM = 4000, double HaloM = 1000, bool Verbose = false, bool Smooth = true, (double E, double N, double R)? DumpNear = null);

    /// <summary>One tiling's run: its records by kind, its centrelines and paint, in plane coordinates.</summary>
    private sealed class Build
    {
        public required GridUnits Units { get; init; }
        public readonly Dictionary<string, List<RecordMarks.Mark>> Marks = new();
        public readonly List<(double X, double Y)[]> Lines = new();
        public readonly Dictionary<(long, long, PaintType, PaintShape), double> Paint = new();
        public int Cells, Blocks, Tiles;
        public double Seconds;
    }

    public static int Run(Settings s, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        string rawDir = RawRoads.DirFor(s.Temp);
        var (minE, minN, maxE, maxN) = s.Box;
        const double read = 3000;   // raw tiles read round the box
        var lv95 = new List<TileId>();
        if (Directory.Exists(rawDir))
            foreach (string path in Directory.EnumerateFiles(rawDir, "roads_*.road"))
            {
                var bits = Path.GetFileNameWithoutExtension(path).Split('_');
                if (bits.Length != 3 || !int.TryParse(bits[1], CultureInfo.InvariantCulture, out int e)
                    || !int.TryParse(bits[2], CultureInfo.InvariantCulture, out int n)) continue;
                var id = new TileId(e, n);
                if (id.MinE + 1000 < minE - read || id.MinE > maxE + read || id.MaxN < minN - read || id.MinN > maxN + read) continue;
                lv95.Add(id);
            }
        lv95.Sort((a, b) => a.E != b.E ? a.E.CompareTo(b.E) : a.N.CompareTo(b.N));
        if (lv95.Count == 0)
        {
            log($"tiling check: no raw roads in {rawDir}");
            return 1;
        }

        var pieces = new List<LineJoin.Piece>();
        int others = 0;
        foreach (var id in lv95)
        {
            var (tile, keys) = TileRewriter.LoadInput(s.Chunks, rawDir, id, stash: false);
            if (tile is null) continue;
            for (int i = 0; i < tile.Segments.Count; i++) pieces.Add(new LineJoin.Piece(id, i, tile.Segments[i], keys?[i]));
            others += tile.Junctions.Count + tile.Paint.Count + tile.PointProps.Count + tile.LinearProps.Count + tile.AreaProps.Count;
        }
        var lines = LineJoin.Join(pieces, Lv95Units.Instance);
        log(string.Create(CultureInfo.InvariantCulture,
            $"tiling check (#1660): {lv95.Count} raw LV95 tiles, {pieces.Count:N0} pieces joined into {lines.Count:N0} lines "
            + $"({lines.Count(l => l.Parts.Count > 1):N0} across cuts), {lines.Count(l => l.OpenStart || l.OpenEnd):N0} with an end on a km line; "
            + $"box {minE},{minN},{maxE},{maxN}"));
        if (others > 0) log($"  {others} raw records besides segments are left out (points, props): none were expected");

        var options = OptionsFor(s);
        var data = RegionData.Load(options, log);
        var builds = new List<Build>();
        foreach (var units in s.Tilings)
        {
            var build = RunTiling(units, lines, s, options, data, log);
            builds.Add(build);
        }

        int failed = 0;
        for (int k = 1; k < builds.Count; k++) failed += Compare(builds[0], builds[k], s, log);
        log(string.Create(CultureInfo.InvariantCulture, $"{sw.Elapsed.TotalSeconds:F0} s"));
        log(failed == 0 ? "[tilingcheck] RESULT: ok" : $"[tilingcheck] RESULT: FAILED {failed} bars");
        return failed == 0 ? 0 : 2;
    }

    private static TileRewriter.Options OptionsFor(Settings s)
    {
        bool testRegion = File.Exists(Path.Combine(s.Chunks, SignalTestRegion.MarkerFile));
        string? Beside(string name) => File.Exists(Path.Combine(s.Temp, name)) ? Path.Combine(s.Temp, name) : null;
        return new TileRewriter.Options(
            Smooth: s.Smooth, DryRun: false, Measure: false, AuditHeights: false, KeepPlans: false,
            OsmOverlay: Beside("osm_overlay.tsv"), TramStops: Beside("tram_stops.tsv"),
            SitgSigns: Beside("sitg_signs.tsv"), Places: File.Exists(Path.Combine(s.Chunks, "places.json")) ? Path.Combine(s.Chunks, "places.json") : null,
            GantryFacts: null, Cantons: testRegion ? TileRewriter.Cantons.Of(SignalTestRegion.CantonTiles) : null);
    }

    private static Build RunTiling(GridUnits units, List<LineJoin.Line> lines, Settings s, TileRewriter.Options baseOptions, RegionData data, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var build = new Build { Units = units };
        // the raw lines cut at the tiling's cells
        var cells = new Dictionary<TileId, List<(RoadSegment Segment, RawRoads.Key? Key)>>();
        foreach (var line in lines)
            foreach (var (cell, seg, key) in Cut(line, units))
            {
                if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new();
                list.Add((seg, key));
            }
        var (minE, minN, maxE, maxN) = s.Box;
        const double margin = 300;   // records anchored by the box's edge
        bool Wanted(TileId id)
        {
            double w = units.West(id), nn = units.North(id);
            return !(w + GridUnits.Box < minE - margin || w > maxE + margin || nn < minN - margin || nn - GridUnits.Box > maxN + margin);
        }
        var wanted = cells.Keys.Where(Wanted).ToHashSet();
        int k = Math.Max(1, (int)Math.Round(s.BlockM / units.CellM));
        int halo = Math.Max(1, (int)Math.Ceiling(s.HaloM / units.CellM));
        var blocks = wanted.GroupBy(c => (Math.Floor(c.E / (double)k), Math.Floor(c.N / (double)k)))
            .OrderBy(g => g.Key.Item2).ThenBy(g => g.Key.Item1).Select(g => g.ToHashSet()).ToList();

        var grids = new Dictionary<TileId, ChunkGrid?>();
        ChunkGrid? Grid(TileId id) => grids.TryGetValue(id, out var g) ? g : grids[id] = TileRewriter.LoadGrid(s.Chunks, id);
        IEnumerable<TileId> Lv95Under(TileId cell, double grow)
        {
            double w = units.West(cell) - grow, nn = units.North(cell) + grow, e = w + GridUnits.Box + 2 * grow, so = nn - GridUnits.Box - 2 * grow;
            for (int te = (int)Math.Floor(w / 1000); te <= (int)Math.Floor(e / 1000); te++)
                for (int tn = (int)Math.Floor(so / 1000); tn <= (int)Math.Floor(nn / 1000); tn++)
                    yield return new TileId(te, tn);
        }
        var bldg = new Dictionary<TileId, BuildingTile?>();
        BuildingTile? Buildings(TileId cell)
        {
            double west = units.West(cell), north = units.North(cell);
            var list = new List<Building>();
            foreach (var id in Lv95Under(cell, 50))
            {
                if (!bldg.TryGetValue(id, out var bt)) bldg[id] = bt = Network.Facades.Read(s.Chunks, id);
                if (bt is null) continue;
                foreach (var b in bt.Buildings)
                {
                    var t = b.Triangles;
                    double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                    for (int q = 0; q + 2 < t.Length; q += 3)
                    {
                        double x = id.MinE + t[q], y = id.MaxN - t[q + 2];
                        (x0, x1, y0, y1) = (Math.Min(x0, x), Math.Max(x1, x), Math.Min(y0, y), Math.Max(y1, y));
                    }
                    if (x1 < west || x0 > west + GridUnits.Box || y1 < north - GridUnits.Box || y0 > north) continue;
                    var moved = new float[t.Length];
                    for (int q = 0; q + 2 < t.Length; q += 3)
                        (moved[q], moved[q + 1], moved[q + 2]) = ((float)(id.MinE + t[q] - west), t[q + 1], (float)(north - (id.MaxN - t[q + 2])));
                    list.Add(new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = b.MinY, MaxY = b.MaxY, Triangles = moved });
                }
            }
            return list.Count == 0 ? null : new BuildingTile { Id = cell, Buildings = list };
        }
        string parkDir = RawParking.DirFor(s.Temp);
        var lots = new Dictionary<TileId, List<RawParking.Lot>>();
        List<RawParking.Lot> Parking(TileId cell)
        {
            var list = new List<RawParking.Lot>();
            foreach (var id in Lv95Under(cell, 0))
            {
                if (!lots.TryGetValue(id, out var l)) lots[id] = l = Directory.Exists(parkDir) ? RawParking.Read(parkDir, id) : [];
                foreach (var lot in l)
                {
                    // each lot once, in the cell holding its ring's middle (and in its LV95 tile once: the lot on its own tile)
                    double cx = 0, cy = 0;
                    int count = lot.Ring.Length / 2;
                    for (int q = 0; q < count; q++) { cx += lot.Ring[q * 2]; cy += lot.Ring[q * 2 + 1]; }
                    if (count > 0 && units.TileAt(cx / count, cy / count) == cell) list.Add(lot);
                }
            }
            return list;
        }
        IGround? Ground(IReadOnlyCollection<TileId> context) =>
            Lv95TileGround.Over(Grid, context.SelectMany(c => Lv95Under(c, 0)).Distinct().ToList());
        (RoadTile? Tile, RawRoads.Key?[]? Keys) Roads(TileId id)
        {
            if (!cells.TryGetValue(id, out var list)) return (null, null);
            // a fresh copy: the stage writes into the segments
            var segs = list.Select(p => new RoadSegment
            {
                Class = p.Segment.Class, Surface = p.Segment.Surface, Flags = p.Segment.Flags, Width = p.Segment.Width,
                Points = (float[])p.Segment.Points.Clone(), Attributes = p.Segment.Attributes,
            }).ToList();
            return (new RoadTile { Id = id, Segments = segs }, list.Select(p => p.Key).ToArray());
        }

        var options = baseOptions with { Halo = halo };
        var inputs = data.On(units, (e, n) => (e, n), (_, _, deg) => deg, (x, y) => (x, y),
            Roads, Ground, Buildings, Parking, data.LoadGantries(options, _ => { }));
        var loop = new MemoryLoop(blocks, wanted, inputs);
        TileRewriter.Run(loop, options, _ => { });

        foreach (var (id, tile) in loop.Tiles) Collect(build, tile, units.West(id), units.North(id), s.Box);
        if (s.DumpNear is { } dn)
            foreach (var (id, tile) in loop.Tiles.OrderBy(t => t.Key.E).ThenBy(t => t.Key.N))
            {
                double w = units.West(id), nn = units.North(id);
                bool Near(double x, double z) => Math.Abs(w + x - dn.E) < dn.R && Math.Abs(nn - z - dn.N) < dn.R;
                foreach (var seg in tile.Segments)
                    if (Enumerable.Range(0, seg.PointCount).Any(i => Near(seg.Points[i * 3], seg.Points[i * 3 + 2])))
                        log(string.Create(CultureInfo.InvariantCulture,
                            $"    [{units}] cell {id.E},{id.N} {seg.Class} w {seg.Width:F1} {seg.Flags} {w + seg.Points[0]:F2},{nn - seg.Points[2]:F2} -> {w + seg.Points[^3]:F2},{nn - seg.Points[^1]:F2} ({seg.PointCount} pts) {seg.Attributes.Flags}"));
                foreach (var pt in tile.Paint)
                    if (pt.Vertices.Length >= 6 && Enumerable.Range(0, pt.Vertices.Length / 3).Any(i => Near(pt.Vertices[i * 3], pt.Vertices[i * 3 + 2])))
                        log(string.Create(CultureInfo.InvariantCulture,
                            $"    [{units}] cell {id.E},{id.N} paint {pt.Type} {pt.Shape} dash {pt.Dash}/{pt.Gap} ref {(pt.Segment is null ? "-" : tile.Segments.Contains(pt.Segment) ? "held" : "unheld")} {w + pt.Vertices[0]:F2},{nn - pt.Vertices[2]:F2} -> {w + pt.Vertices[^3]:F2},{nn - pt.Vertices[^1]:F2} ({pt.Vertices.Length / 3} pts)"));
                foreach (var j in tile.Junctions)
                {
                    double cx = 0, cz = 0;
                    int c = j.Vertices.Length / 3;
                    for (int q = 0; q < c; q++) { cx += j.Vertices[q * 3]; cz += j.Vertices[q * 3 + 2]; }
                    if (c > 0 && Near(cx / c, cz / c))
                        log(string.Create(CultureInfo.InvariantCulture, $"    [{units}] cell {id.E},{id.N} junction {j.Class} at {w + cx / c:F2},{nn - cz / c:F2}, {c} vertices, {RecordMarks.Area(j):F1} m2"));
                }
            }
        build.Cells = cells.Count;
        build.Blocks = blocks.Count;
        build.Tiles = loop.Tiles.Count;
        build.Seconds = sw.Elapsed.TotalSeconds;
        log(string.Create(CultureInfo.InvariantCulture,
            $"  tiling {units}: {wanted.Count} cells in {blocks.Count} blocks of {k}x{k}, halo {halo}; {loop.Tiles.Count} tiles written; "
            + $"{build.Marks.GetValueOrDefault("junction")?.Count ?? 0} junctions, {build.Marks.GetValueOrDefault("signal")?.Count ?? 0} signals, "
            + $"{build.Marks.GetValueOrDefault("approach")?.Count ?? 0} approaches in the box; {build.Seconds:F0} s"));
        return build;
    }

    /// <summary>A finished tile's records, lines and paint into <paramref name="build"/>, those in the box (its frame at west, north).</summary>
    private static void Collect(Build build, RoadTile tile, double west, double north, (double MinE, double MinN, double MaxE, double MaxN) box)
    {
        (double, double, double) Place(double x, double y, double z) => (west + x, north - z, y);
        var marks = new Dictionary<string, List<RecordMarks.Mark>>();
        RecordMarks.Collect(tile, marks, Place);
        foreach (var (kind, list) in marks)
        {
            if (!build.Marks.TryGetValue(kind, out var into)) build.Marks[kind] = into = new();
            into.AddRange(list.Where(m => In(box, m.E, m.N)));
        }
        foreach (var seg in tile.Segments)
        {
            if (RoadFormat.IsAerial(seg.Class) || RoadFormat.IsWatercourse(seg.Class) || RoadFormat.IsWall(seg.Class)) continue;
            var pts = new (double, double)[seg.PointCount];
            for (int i = 0; i < seg.PointCount; i++) pts[i] = (west + seg.Points[i * 3], north - seg.Points[i * 3 + 2]);
            build.Lines.Add(pts);
        }
        foreach (var paint in tile.Paint) AddPaint(build, paint, west, north, box);
    }

    /// <summary>
    /// Two LV95 builds of the same region compared record by record (#1660: what the whole-line stage changed against main's),
    /// as the tiling check compares two tilings: <c>RoadGen --diff-builds A B --box ...</c>. Each difference with its distance to
    /// the nearest km line, where the LV95 build cut its lines.
    /// </summary>
    public static int Diff(string a, string b, (double MinE, double MinN, double MaxE, double MaxN) box, bool verbose, Action<string> log)
    {
        var km = new GridUnits(0, 0, 0, 1000);
        Build Read(string dir)
        {
            var build = new Build { Units = km };
            foreach (string path in Directory.EnumerateFiles(dir, "roads_*.road"))
            {
                RoadTile tile;
                using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
                var bits = Path.GetFileNameWithoutExtension(path).Split('_');
                var id = new TileId(int.Parse(bits[1], CultureInfo.InvariantCulture), int.Parse(bits[2], CultureInfo.InvariantCulture));
                Collect(build, tile, id.MinE, id.MinN + 1000, box);
                build.Tiles++;
            }
            return build;
        }
        var ba = Read(a);
        var bb = Read(b);
        log($"builds {a} ({ba.Tiles} tiles) and {b} ({bb.Tiles} tiles), box {box.MinE},{box.MinN},{box.MaxE},{box.MaxN}");
        int failed = Compare(ba, bb, new Settings(a, a, box, [km], Verbose: verbose), log);
        log(failed == 0 ? "[diffbuilds] RESULT: the same" : $"[diffbuilds] RESULT: {failed} kinds differ");
        return failed == 0 ? 0 : 2;
    }

    private static bool In((double MinE, double MinN, double MaxE, double MaxN) box, double e, double n) =>
        e >= box.MinE && e <= box.MaxE && n >= box.MinN && n <= box.MaxN;

    /// <summary>Paint lengths (polylines, 0.1 m steps) and areas (triangles, by centroid) per type on the plane's fixed lattice.</summary>
    private static void AddPaint(Build build, RoadPaint paint, double west, double north, (double MinE, double MinN, double MaxE, double MaxN) box)
    {
        var v = paint.Vertices;
        void Add(double e, double n, double amount)
        {
            if (!In(box, e, n)) return;
            var key = ((long)Math.Floor(e / PaintBin), (long)Math.Floor(n / PaintBin), paint.Type, paint.Shape);
            build.Paint[key] = build.Paint.GetValueOrDefault(key) + amount;
        }
        if (paint.Shape == PaintShape.Polyline)
        {
            // the length painted: a dashed line's dashes only (its pattern starts with a dash at its first point), so a line cut
            // anywhere in its pattern measures the same
            double period = paint.Dash + paint.Gap, at = 0;
            for (int i = 3; i + 2 < v.Length; i += 3)
            {
                double ax = west + v[i - 3], ay = north - v[i - 1], bx = west + v[i], by = north - v[i + 2];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int steps = Math.Max(1, (int)Math.Ceiling(len / 0.1));
                for (int q = 0; q < steps; q++)
                {
                    double t = (q + 0.5) / steps;
                    double s = at + len * t;
                    if (paint.Dash > 0 && period > 0 && s % period >= paint.Dash) continue;
                    Add(ax + (bx - ax) * t, ay + (by - ay) * t, len / steps);
                }
                at += len;
            }
            return;
        }
        var idx = paint.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int i0 = idx[t] * 3, i1 = idx[t + 1] * 3, i2 = idx[t + 2] * 3;
            double area = Math.Abs((v[i1] - v[i0]) * (v[i2 + 2] - v[i0 + 2]) - (v[i2] - v[i0]) * (v[i1 + 2] - v[i0 + 2])) * 0.5;
            Add(west + (v[i0] + v[i1] + v[i2]) / 3, north - (v[i0 + 2] + v[i1 + 2] + v[i2 + 2]) / 3, area);
        }
    }

    /// <summary>A whole line cut at the tiling's cell edges: each piece with its cell, its segment in the cell's frame and its key.</summary>
    public static List<(TileId Cell, RoadSegment Segment, RawRoads.Key? Key)> Cut(LineJoin.Line line, GridUnits units)
    {
        var p = line.Points;
        var result = new List<(TileId, RoadSegment, RawRoads.Key?)>();
        if (p.Count < 2) return result;
        // the points with every crossing of a cell edge inserted (one closer than a millimetre to a vertex is the vertex)
        var pts = new List<LineJoin.Point> { p[0] };
        for (int i = 1; i < p.Count; i++)
        {
            var a = p[i - 1];
            var b = p[i];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            foreach (double t in units.Crossings(a.X, a.Y, b.X, b.Y))
            {
                if (t * len < 0.001 || (1 - t) * len < 0.001) continue;
                pts.Add(new LineJoin.Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, (float)(a.H + (b.H - a.H) * t)));
            }
            pts.Add(b);
        }
        var along = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            along[i] = along[i - 1] + Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
        // each stretch's cell by its middle; a piece is a run of stretches in one cell
        TileId CellOf(int i) => units.TileAt((pts[i].X + pts[i + 1].X) * 0.5, (pts[i].Y + pts[i + 1].Y) * 0.5);
        int start = 0;
        var cell = CellOf(0);
        for (int i = 1; i <= pts.Count - 1; i++)
        {
            bool end = i == pts.Count - 1;
            var next = end ? cell : CellOf(i);
            if (!end && next == cell) continue;
            var piece = pts.GetRange(start, i - start + 1);
            RawRoads.Key? key = line.Key is { } k ? k with { FromM = k.FromM + along[start] } : null;
            result.Add((cell, LineJoin.Segment(line.Template, piece, units, cell), key));
            start = i;
            cell = next;
        }
        return result;
    }

    /// <summary>The nearest cell edge of a tiling to a plane point, metres.</summary>
    private static double EdgeM(GridUnits units, double e, double n)
    {
        var (u, v) = units.ToGrid(e, n);
        return Math.Min(Math.Abs(u - Math.Round(u)), Math.Abs(v - Math.Round(v))) * units.CellM;
    }

    /// <summary>A tiling's records against the reference's; the number of failed bars.</summary>
    private static int Compare(Build a, Build b, Settings s, Action<string> log)
    {
        int bad = 0;
        log("");
        log($"tiling {b.Units} against {a.Units}:");
        string Where(double e, double n) => string.Create(CultureInfo.InvariantCulture,
            $"{EdgeM(a.Units, e, n):F0} m from a cut of {a.Units}, {EdgeM(b.Units, e, n):F0} m from one of {b.Units}");
        foreach (string kind in a.Marks.Keys.Union(b.Marks.Keys).Order(StringComparer.Ordinal))
        {
            var ma = a.Marks.GetValueOrDefault(kind) ?? [];
            var mb = b.Marks.GetValueOrDefault(kind) ?? [];
            var (partner, dist, used) = RecordMarks.Match(ma, mb, 5);
            int alone = 0, look = 0, size = 0, far = 0;
            var ds = new List<double>();
            var detail = new List<string>();
            for (int i = 0; i < ma.Count; i++)
            {
                var m = ma[i];
                if (partner[i] < 0)
                {
                    alone++;
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"{a.Units} alone at {m.E:F1},{m.N:F1} ({m.Look}); {Where(m.E, m.N)}"));
                    continue;
                }
                var o = mb[partner[i]];
                ds.Add(dist[i]);
                if (o.Look != m.Look)
                {
                    look++;
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: {m.Look} -> {o.Look}; {Where(m.E, m.N)}"));
                }
                else if (!double.IsNaN(m.Size) && Math.Abs(o.Size - m.Size) > Math.Max(AreaM2, AreaShare * m.Size))
                {
                    size++;
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: {m.Size:F1} m2 -> {o.Size:F1} m2; {Where(m.E, m.N)}"));
                }
                else if (dist[i] > PlaceBarM)
                {
                    far++;
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: {dist[i]:F2} m apart; {Where(m.E, m.N)}"));
                }
            }
            int aloneB = 0;
            for (int k = 0; k < mb.Count; k++)
                if (!used[k])
                {
                    aloneB++;
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"{b.Units} alone at {mb[k].E:F1},{mb[k].N:F1} ({mb[k].Look}); {Where(mb[k].E, mb[k].N)}"));
                }
            ds.Sort();
            bool ok = alone + aloneB + look + size + far == 0;
            if (!ok) bad++;
            log(string.Create(CultureInfo.InvariantCulture,
                $"  {(ok ? "ok  " : "FAIL")} {kind}: {ma.Count} / {mb.Count}, alone {alone} / {aloneB}, other look {look}, other size {size}, "
                + $"apart > {PlaceBarM * 100:F0} cm {far}; distance p50 {P(ds, 0.5) * 100:F2} cm, p95 {P(ds, 0.95) * 100:F2} cm, max {P(ds, 1) * 100:F2} cm"));
            if (s.Verbose) foreach (string d in detail) log("      " + d);
        }

        // paint per bin and type
        int paintBad = 0;
        double paintWorst = 0;
        var paintDetail = new List<string>();
        foreach (var key in a.Paint.Keys.Union(b.Paint.Keys))
        {
            double va = a.Paint.GetValueOrDefault(key), vb = b.Paint.GetValueOrDefault(key);
            double bar = key.Item4 == PaintShape.Polyline ? PaintBarM : PaintBarM2;
            double d = Math.Abs(va - vb);
            if (d <= bar) continue;
            paintBad++;
            paintWorst = Math.Max(paintWorst, d);
            double e = (key.Item1 + 0.5) * PaintBin, n = (key.Item2 + 0.5) * PaintBin;
            paintDetail.Add(string.Create(CultureInfo.InvariantCulture,
                $"{key.Item3} {(key.Item4 == PaintShape.Polyline ? "m" : "m2")} in {e:F0},{n:F0} (±{PaintBin / 2:F1}): {va:F2} -> {vb:F2}; {Where(e, n)}"));
        }
        if (paintBad > 0) bad++;
        log(string.Create(CultureInfo.InvariantCulture,
            $"  {(paintBad == 0 ? "ok  " : "FAIL")} paint: {a.Paint.Count} / {b.Paint.Count} type bins of {PaintBin} m, {paintBad} differ "
            + $"(over {PaintBarM} m or {PaintBarM2} m2), worst {paintWorst:F2}"));
        if (s.Verbose) foreach (string d in paintDetail.Order(StringComparer.Ordinal)) log("      " + d);

        // centrelines: each reference vertex in the box to the other tiling's lines
        var index = new LineIndex(b.Lines);
        var cd = new List<double>();
        foreach (var line in a.Lines)
            foreach (var (e, n) in line)
                if (In(s.Box, e, n)) cd.Add(index.Distance(e, n));
        cd.Sort();
        bool clOk = cd.Count == 0 || P(cd, 1) <= CentrelineBarM;
        if (!clOk) bad++;
        log(string.Create(CultureInfo.InvariantCulture,
            $"  {(clOk ? "ok  " : "FAIL")} centrelines: {cd.Count:N0} vertices, to the other's lines p50 {P(cd, 0.5) * 100:F2} cm, "
            + $"p95 {P(cd, 0.95) * 100:F2} cm, p99 {P(cd, 0.99) * 100:F2} cm, max {P(cd, 1) * 100:F2} cm"));
        return bad;
    }

    private static double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)Math.Floor(q * v.Count))];

    /// <summary>Line pieces in 10 m cells: the distance from a point to the nearest (searched out to 5 m, else 5 m).</summary>
    private sealed class LineIndex
    {
        private const double Cell = 10, Reach = 5;
        private readonly Dictionary<(long, long), List<(double, double, double, double)>> _cells = new();

        public LineIndex(List<(double X, double Y)[]> lines)
        {
            foreach (var l in lines)
                for (int i = 1; i < l.Length; i++)
                {
                    var (ax, ay) = l[i - 1];
                    var (bx, by) = l[i];
                    long x0 = (long)Math.Floor(Math.Min(ax, bx) / Cell), x1 = (long)Math.Floor(Math.Max(ax, bx) / Cell);
                    long y0 = (long)Math.Floor(Math.Min(ay, by) / Cell), y1 = (long)Math.Floor(Math.Max(ay, by) / Cell);
                    for (long x = x0; x <= x1; x++)
                        for (long y = y0; y <= y1; y++)
                        {
                            if (!_cells.TryGetValue((x, y), out var list)) _cells[(x, y)] = list = new();
                            list.Add((ax, ay, bx, by));
                        }
                }
        }

        public double Distance(double px, double py)
        {
            double best = Reach;
            long cx = (long)Math.Floor(px / Cell), cy = (long)Math.Floor(py / Cell);
            for (long x = cx - 1; x <= cx + 1; x++)
                for (long y = cy - 1; y <= cy + 1; y++)
                    if (_cells.TryGetValue((x, y), out var list))
                        foreach (var (ax, ay, bx, by) in list)
                        {
                            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                            double t = l2 < 1e-18 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
                            double ex = ax + dx * t - px, ey = ay + dy * t - py;
                            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
                        }
            return best;
        }
    }

    /// <summary>A loop over tiles held in memory: the blocks and wanted cells given, the finished tiles kept.</summary>
    private sealed class MemoryLoop(IReadOnlyList<HashSet<TileId>> blocks, HashSet<TileId> wanted, TileRewriter.Inputs inputs) : ITileLoop
    {
        public Dictionary<TileId, RoadTile> Tiles { get; } = new();
        public IReadOnlyList<HashSet<TileId>> Blocks => blocks;
        public HashSet<TileId> Wanted => wanted;
        public TileRewriter.Inputs Inputs => inputs;
        public void Write(RoadTile tile, byte[] bytes) => Tiles[tile.Id] = tile;
        public void Finish() { }
    }
}
