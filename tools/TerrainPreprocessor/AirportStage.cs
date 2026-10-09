using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--airports</c> (#422): the region's airports into <c>airports.json</c>, with the stands the game
/// parks aircraft at and the runways' profiles. swissTLM3D gives the airports
/// (<c>tlm_areale_verkehrsareal</c> <c>Flughafenareal</c>) and their paved movement area
/// (<c>tlm_bauten_verkehrsbaute_ply</c> <c>Rollfeld Hartbelag</c>, <c>Hartbelagpiste</c>); it has no
/// stands, so they come from OpenStreetMap (<c>aeroway=parking_position</c>, <c>apron</c>,
/// <c>taxiway</c>, <c>runway</c>, read from the Geofabrik PBF, ODbL). The ground is the built tiles'.
/// <see cref="AirportPlanner"/> chooses; see <c>docs/notes/world/airports.md</c>. Seconds.
/// </summary>
public static class AirportStage
{
    private static readonly HashSet<string> Aeroways = new() { "parking_position", "apron", "taxiway", "taxilane", "runway", "aerodrome" };
    private static readonly HashSet<string> TagKeys = new() { "aeroway", "ref", "name", "icao", "width", "parking_position" };

    public static int Run(string outDir, string tlmPath, string pbf, string? file = null, int jobs = 8)
    {
        foreach (var (path, what) in new[] { (tlmPath, "swissTLM3D GeoPackage"), (pbf, "OpenStreetMap PBF") })
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"{what} not found: {path}");
                return 1;
            }
        var manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"--airports needs a built region: no {manifestPath}");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        var tiles = TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToHashSet();
        if (tiles.Count == 0) return 0;
        var sampler = new LandingStage.TileSampler(outDir, tiles);
        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MaxN) - ChunkFormat.TileSizeM, maxN = tiles.Max(t => t.MaxN);

        using var conn = GeoPackageReader.Open(tlmPath);
        // the airports and their paved areas
        var areas = new List<(string Name, List<(double E, double N)> Ring, (double E0, double N0, double E1, double N1) Box)>();
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_areale_verkehrsareal", new[] { "objektart", "name" }, minE, minN, maxE, maxN))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.GetString(0) != "Flughafenareal") continue;
                string name = r.IsDBNull(1) ? "Airport" : r.GetString(1);
                foreach (var ring in GeoPackageReader.ParsePolygons((byte[])r[2]))
                {
                    var pts = Ring(ring);
                    if (pts.Count < 3) continue;
                    var box = (pts.Min(p => p.E), pts.Min(p => p.N), pts.Max(p => p.E), pts.Max(p => p.N));
                    if (!tiles.Contains(TileId.FromLv95((box.Item1 + box.Item3) / 2, (box.Item2 + box.Item4) / 2))) continue;
                    areas.Add((name, pts, box));
                }
            }
        var paved = new List<(List<(double E, double N)> Ring, (double E0, double N0, double E1, double N1) Box)>();
        foreach (var a in areas)
            using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_bauten_verkehrsbaute_ply", new[] { "objektart" }, a.Box.E0, a.Box.N0, a.Box.E1, a.Box.N1))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (r.IsDBNull(0) || r.GetString(0) is not ("Rollfeld Hartbelag" or "Hartbelagpiste")) continue;
                    foreach (var ring in GeoPackageReader.ParsePolygons((byte[])r[1]))
                    {
                        var pts = Ring(ring);
                        if (pts.Count >= 3) paved.Add((pts, (pts.Min(p => p.E), pts.Min(p => p.N), pts.Max(p => p.E), pts.Max(p => p.N))));
                    }
                }
        bool Paved(double e, double n)
        {
            foreach (var (ring, box) in paved)
                if (e >= box.E0 && e <= box.E1 && n >= box.N0 && n <= box.N1 && AirportPlanner.Inside(ring, e, n)) return true;
            return false;
        }

        // OpenStreetMap inside the airports' boxes (each 300 m wider)
        var boxes = areas.Select(a =>
        {
            var (la0, lo0) = SwissProjection.ToWgs84(a.Box.E0 - 300, a.Box.N0 - 300);
            var (la1, lo1) = SwissProjection.ToWgs84(a.Box.E1 + 300, a.Box.N1 + 300);
            return (Math.Min(la0, la1), Math.Min(lo0, lo1), Math.Max(la0, la1), Math.Max(lo0, lo1));
        }).ToList();
        var osm = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (lat, lon) => boxes.Any(b => lat >= b.Item1 && lat <= b.Item3 && lon >= b.Item2 && lon <= b.Item4),
            KeepWay = tags => tags.TryGetValue("aeroway", out var a) && Aeroways.Contains(a),
            WayTags = TagKeys,
            KeepTaggedNode = tags => tags.TryGetValue("aeroway", out var a) && a is "parking_position" or "aerodrome",
            NodeTags = TagKeys,
            // the aerodrome is a multipolygon at GVA and ZRH: only its ICAO code is wanted
            KeepRelation = tags => tags.GetValueOrDefault("aeroway") == "aerodrome" && tags.ContainsKey("icao"),
            RelationTags = TagKeys,
        });
        double osmSeconds = sw.Elapsed.TotalSeconds;
        List<(double E, double N)> Line(PbfReader.Way w)
        {
            var pts = new List<(double E, double N)>();
            foreach (long id in w.Refs)
                if (osm.Nodes.TryGetValue(id, out var ll)) pts.Add(SwissProjection.ToLv95(ll.Lat, ll.Lon));
            return pts;
        }
        var ways = osm.Ways.Select(w => (Way: w, Pts: Line(w), Aeroway: w.Tags["aeroway"])).Where(w => w.Pts.Count > 0).ToList();

        var index = new AirportIndex();
        foreach (var area in areas.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            bool In((double E, double N) p) => AirportPlanner.Inside(area.Ring, p.E, p.N);
            var mine = ways.Where(w => w.Pts.Any(In)).ToList();
            var taxiways = mine.Where(w => w.Aeroway is "taxiway" or "taxilane").Select(w => (IReadOnlyList<(double E, double N)>)w.Pts).ToList();
            var aprons = mine.Where(w => w.Aeroway == "apron" && w.Pts.Count >= 4)
                .Select(w => (Name: w.Way.Tags.GetValueOrDefault("name") ?? w.Way.Tags.GetValueOrDefault("ref") ?? $"apron {w.Way.Id}", w.Pts)).ToList();
            string ApronOf(double e, double n) => aprons.FirstOrDefault(a => AirportPlanner.Inside(a.Pts, e, n)).Name ?? "";
            static bool IsCargo(string s) => s.Contains("cargo", StringComparison.OrdinalIgnoreCase)
                || s.Contains("fret", StringComparison.OrdinalIgnoreCase) || s.Contains("fracht", StringComparison.OrdinalIgnoreCase);

            var candidates = new List<AirportPlanner.Candidate>();
            int unknownHeading = 0;
            foreach (var w in mine.Where(w => w.Aeroway == "parking_position"))
            {
                string reference = w.Way.Tags.GetValueOrDefault("ref") ?? w.Way.Tags.GetValueOrDefault("name") ?? "";
                if (AirportPlanner.FromLine(w.Pts, taxiways) is not { } s) continue;
                string apron = ApronOf(s.E, s.N);
                candidates.Add(new(reference, s.E, s.N, s.Heading, apron, IsCargo(apron) || IsCargo(reference)));
            }
            string code = area.Name;
            foreach (var node in osm.TaggedNodes)
            {
                var p = SwissProjection.ToLv95(node.Lat, node.Lon);
                if (!In(p)) continue;
                if (node.Tags.GetValueOrDefault("aeroway") == "aerodrome" && node.Tags.TryGetValue("icao", out var icao)) code = icao;
                if (node.Tags.GetValueOrDefault("aeroway") != "parking_position") continue;
                string reference = node.Tags.GetValueOrDefault("ref") ?? node.Tags.GetValueOrDefault("name") ?? "";
                if (candidates.Any(c => c.Ref == reference && reference.Length > 0)) continue;
                var heading = AirportPlanner.FromPoint(p.E, p.N, taxiways);
                if (heading == null) unknownHeading++;
                string apron = ApronOf(p.E, p.N);
                candidates.Add(new(reference, p.E, p.N, heading, apron, IsCargo(apron) || IsCargo(reference)));
            }
            foreach (var w in mine.Where(w => w.Aeroway == "aerodrome" && w.Way.Tags.ContainsKey("icao")))
                code = w.Way.Tags["icao"];
            if (code == area.Name)
                foreach (var rel in osm.Relations)
                    if (rel.Tags.GetValueOrDefault("name") is { } n && n.Contains(area.Name, StringComparison.OrdinalIgnoreCase))
                        code = rel.Tags["icao"];

            var airport = new Airport { Name = area.Name, Code = code, E = Math.Round((area.Box.E0 + area.Box.E1) / 2), N = Math.Round((area.Box.N0 + area.Box.N1) / 2) };
            foreach (var w in mine.Where(w => w.Aeroway == "runway" && w.Pts.Count >= 2))
            {
                double width = double.TryParse(w.Way.Tags.GetValueOrDefault("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var wd) ? wd : 45;
                var (a, b) = (w.Pts[0], w.Pts[^1]);
                airport.Runways.Add(AirportPlanner.Profile(w.Way.Tags.GetValueOrDefault("ref") ?? "", a.E, a.N, b.E, b.N, width, sampler.Ground));
            }
            airport.Runways.Sort((x, y) => y.Length.CompareTo(x.Length));
            bool big = airport.Runways.Any(r => r.Usable && r.Length >= AirportPlanner.MinRunway);
            // paved and free of buildings (terminals, docks, hangars, jet bridges: swissBUILDINGS3D in the tiles)
            var built = new BuildingRaster(outDir, tiles, area.Box.E0 - 100, area.Box.N0 - 100, area.Box.E1 + 100, area.Box.N1 + 100);
            bool Free(double e, double n) => Paved(e, n) && !built.At(e, n);
            airport.Stands = AirportPlanner.Choose(code, candidates, Free, sampler.Ground, big);
            var cargoAprons = string.Join(", ", candidates.Where(c => c.Cargo).Select(c => c.Apron).Distinct());
            Console.WriteLine($"  {area.Name} ({code}): {candidates.Count} OSM stands ({unknownHeading} with no taxiway to face from, {candidates.Count(c => c.Cargo)} cargo"
                + $"{(cargoAprons.Length > 0 ? $": {cargoAprons}" : "")}), {aprons.Count} aprons, {taxiways.Count} taxiways; chosen {airport.Stands.Count}");
            foreach (var r in airport.Runways)
                Console.WriteLine(FormattableString.Invariant($"    runway {r.Ref}: {r.Length:F0} x {r.Width:F0} m, steepest {r.Grade:F2} %, worst bump {r.Bump:F2} m{(r.Usable ? "" : " (NOT usable)")}"));
            foreach (var s in airport.Stands)
                Console.WriteLine(FormattableString.Invariant($"    {s.Use} at {s.Ref} ({ApronOf(s.E, s.N)}): {s.E:F1}/{s.N:F1} heading {s.Heading:F0}°, ground {s.Ground:F1} m"));
            if (airport.Stands.Count > 0) index.Airports.Add(airport);
        }

        file ??= Path.Combine(outDir, AirportIndex.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, index.ToJson());
        Console.WriteLine($"Airports: {index.Airports.Count} with parked aircraft ({index.Stands().Count()} stands) of {areas.Count} airport areas, "
            + $"OSM read in {osmSeconds:F1}s, total {sw.Elapsed.TotalSeconds:F1}s -> {file}");
        return 0;
    }

    /// <summary>Where the built tiles' buildings stand, on a 2 m raster over a box (each triangle's plan filled).</summary>
    private sealed class BuildingRaster
    {
        private const double Cell = 2;
        private readonly double _e0, _n1;
        private readonly int _w, _h;
        private readonly bool[] _cells;

        public BuildingRaster(string dir, HashSet<TileId> tiles, double e0, double n0, double e1, double n1)
        {
            (_e0, _n1) = (e0, n1);
            _w = (int)Math.Ceiling((e1 - e0) / Cell);
            _h = (int)Math.Ceiling((n1 - n0) / Cell);
            _cells = new bool[_w * _h];
            var a = TileId.FromLv95(e0, n0);
            var b = TileId.FromLv95(e1, n1);
            for (int te = Math.Min(a.E, b.E); te <= Math.Max(a.E, b.E); te++)
                for (int tn = Math.Min(a.N, b.N); tn <= Math.Max(a.N, b.N); tn++)
                {
                    var id = new TileId(te, tn);
                    string path = Path.Combine(dir, BuildingFormat.FileName(id));
                    if (!tiles.Contains(id) || !File.Exists(path)) continue;
                    BuildingTile tile;
                    using (var fs = File.OpenRead(path)) tile = BuildingCodec.Decode(fs);
                    foreach (var bld in tile.Buildings)
                        for (int t = 0; t + 8 < bld.Triangles.Length; t += 9)
                            Fill(id.MinE + bld.Triangles[t], id.MaxN - bld.Triangles[t + 2], id.MinE + bld.Triangles[t + 3], id.MaxN - bld.Triangles[t + 5],
                                id.MinE + bld.Triangles[t + 6], id.MaxN - bld.Triangles[t + 8]);
                }
        }

        private void Fill(double ae, double an, double be, double bn, double ce, double cn)
        {
            double area = (be - ae) * (cn - an) - (ce - ae) * (bn - an);
            if (Math.Abs(area) < 1e-6) return;   // a wall seen from above
            int c0 = Math.Max(0, (int)((Math.Min(ae, Math.Min(be, ce)) - _e0) / Cell)), c1 = Math.Min(_w - 1, (int)((Math.Max(ae, Math.Max(be, ce)) - _e0) / Cell));
            int r0 = Math.Max(0, (int)((_n1 - Math.Max(an, Math.Max(bn, cn))) / Cell)), r1 = Math.Min(_h - 1, (int)((_n1 - Math.Min(an, Math.Min(bn, cn))) / Cell));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double e = _e0 + (c + 0.5) * Cell, n = _n1 - (r + 0.5) * Cell;
                    double w0 = (be - ae) * (n - an) - (bn - an) * (e - ae);
                    double w1 = (ce - be) * (n - bn) - (cn - bn) * (e - be);
                    double w2 = (ae - ce) * (n - cn) - (an - cn) * (e - ce);
                    if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0)) _cells[r * _w + c] = true;
                }
        }

        public bool At(double e, double n)
        {
            int c = (int)((e - _e0) / Cell), r = (int)((_n1 - n) / Cell);
            return c >= 0 && r >= 0 && c < _w && r < _h && _cells[r * _w + c];
        }
    }

    private static List<(double E, double N)> Ring(GeoPackageReader.Ring ring)
    {
        var pts = new List<(double E, double N)>(ring.Count);
        for (int i = 0; i < ring.Count; i++) pts.Add((ring.Xyz[i * 3], ring.Xyz[i * 3 + 1]));
        return pts;
    }
}
