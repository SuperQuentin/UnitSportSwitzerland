using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The region-wide files of a cube region (#1507, G2 B3 row 8; from the sources and the leaves since #1711, G2 D1;
/// docs/notes/tools/cube-region-files.md). The region-wide stages run on the region itself, reading of it what they read of an
/// LV95 build (<see cref="BuiltLeaves"/>: its LV95 km cells, ground, still water, network roads and buildings), and write into it;
/// then every position also as latitude, longitude and ellipsoidal height (<see cref="GeoPoint"/>) and every grid heading also
/// from true north, rigorously (<see cref="SwissGeodesy"/>), at the point's own height (a summit's, a deck's, still water, a
/// stand's ground) or, where the file has none, the leaves' ground there. The LV95 fields stay until D (the game reads them
/// until then). Each stage keeps its maths. No <c>horizon.bin</c> is written: the region's far terrain is its pyramid.
///
/// <code>
/// --cube-sources --cube-out REGION --tlm GPKG [--osm-pbf PBF] [--sitg] [--cube-temp DIR] [--jobs N]
///     the network's source files into the region's temp: the OSM overlay and nodes (with --osm-pbf), tram stops, SITG signs
///     (--sitg: fetched from the canton's service), before --cube-network
/// --cube-places --cube-out REGION --gwr DB [--tlm GPKG]
///     places.json (GWR towns, TLM summits and passes), before --cube-network (its gantries read the towns)
/// --cube-region-files --cube-out REGION [--tlm GPKG] [--osm-pbf PBF] [--gwr DB] [--boundaries GPKG] [--atlas-cards F] [--jobs N]
///     after the network and the water: POIs (--tlm), landings (--tlm), airports (--tlm, --osm-pbf), lifts (--osm-pbf), and the
///     Earth positions of every region file there (places.json too)
/// </code>
///
/// <para>
/// The check, at the end of every <c>--cube-places</c> and <c>--cube-region-files</c>: each written file read back, every Earth
/// position taken back to LV95 at its ellipsoidal height and held to the LV95 fields within 2 cm (the bar of row 8).
/// </para>
/// </summary>
public static class CubeRegionFiles
{
    public static bool Wants(IReadOnlyList<string> args) =>
        args.Contains("--cube-region-files") || args.Contains("--cube-places") || args.Contains("--cube-sources");

    /// <summary>The bar: an Earth position taken back to LV95 against the LV95 fields (m).</summary>
    public const double Bar = 0.02;

    public static int Run(IReadOnlyList<string> args)
    {
        string? region = null, temp = null, tlm = null, pbf = null, gwr = null, boundaries = null, cards = null, mode = null;
        bool sitg = false;
        string? probe = null;
        int jobs = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-region-files" or "--cube-places" or "--cube-sources": mode = args[i]; break;
                case "--cube-out": region = args[++i]; break;
                case "--cube-temp": temp = args[++i]; break;
                case "--tlm": tlm = args[++i]; break;
                case "--osm-pbf": pbf = args[++i]; break;
                case "--gwr": gwr = args[++i]; break;
                case "--boundaries": boundaries = args[++i]; break;
                case "--atlas-cards": cards = args[++i]; break;
                case "--sitg": sitg = true; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                // a diagnostic: the leaves' ground and still water at an LV95 point
                case "--ground-at": probe = args[++i]; break;
                case "--chunks":
                    Console.Error.WriteLine($"{mode}: --chunks is gone (#1711): the region files come from the sources and the region's leaves");
                    return 2;
                default: Console.Error.WriteLine($"{mode}: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || !File.Exists(Path.Combine(region, CubeManifest.FileName)))
        {
            Console.Error.WriteLine($"{mode} needs --cube-out REGION, a region with leaves (build its heights first: --cube)");
            return 2;
        }
        temp ??= region.TrimEnd('/', '\\') + "_temp";
        var sw = Stopwatch.StartNew();
        var leaves = new BuiltLeaves(region);
        var tiles = leaves.Tiles;
        Console.WriteLine($"{mode}: {leaves.Heights.Leaves.Count} leaves over {tiles.Count} LV95 km cells");
        if (tiles.Count == 0) return 2;
        if (probe != null)
        {
            var v = probe.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"at {v[0]:F1},{v[1]:F1}: in a leaf {leaves.Contains(v[0], v[1])}, ground {leaves.Ground(v[0], v[1]):F3}, still water {leaves.Level(v[0], v[1]):F3}"));
            return 0;
        }

        if (mode == "--cube-sources")
        {
            if (tlm == null) { Console.Error.WriteLine("--cube-sources needs --tlm GPKG"); return 2; }
            Directory.CreateDirectory(temp);
            int rc = 0;
            if (pbf != null) rc = OsmOverlay.Run(pbf, tlm, temp, tiles, jobs, geo: leaves.Geo);
            if (rc == 0) rc = TramStopStage.Run(tiles, tlm, temp);
            if (rc == 0 && sitg) rc = SitgSignStage.Run(tiles.ToList(), temp, force: false);
            Console.WriteLine($"cube sources: into {temp}, {sw.Elapsed.TotalSeconds:F1} s");
            return rc;
        }

        string Out(string name) => Path.Combine(region, name);
        if (mode == "--cube-places")
        {
            if (gwr == null) { Console.Error.WriteLine("--cube-places needs --gwr data.sqlite"); return 2; }
            int rc = PlaceStage.Run(gwr, region, tiles, tlm);
            if (rc != 0) return rc;
        }
        else
        {
            int rc = 0;
            if (tlm != null)
            {
                rc = PoiStage.Run(tlm, region, tiles, new PoiStage.Options(boundaries, pbf, gwr, Out(PoiFile.FileName), cards, jobs, leaves, leaves.Geo));
                if (rc == 0) rc = LandingStage.Run(tiles, leaves, (e, n) => leaves.RoadsAround(e, n, LandingPlanner.RoadEndReach), tlm, Out(LandingIndex.FileName));
                if (rc == 0 && pbf != null)
                    rc = AirportStage.Run(tiles, leaves, leaves.Geo, leaves.Buildings.Lv95Tile, tlm, pbf, Out(AirportIndex.FileName), jobs);
            }
            if (rc == 0 && pbf != null)
            {
                var lifts = PisteStage.LoadAerialways(pbf, jobs, PisteStage.LatLonBox(tiles), leaves.Geo);
                File.WriteAllText(Out(RopewayInfoIndex.FileName), new RopewayInfoIndex { Ropeways = lifts }.ToJson());
                Console.WriteLine($"cube region files: {lifts.Count} aerialways written to {RopewayInfoIndex.FileName}");
            }
            if (rc != 0) return rc;
        }

        var check = new Check();
        // off the leaves (a source point in a km cell they reach), the Plateau's height, as GeoInput places such a point
        int files = AddEarthPositions(region, (e, n) => leaves.Ground(e, n) is var g && double.IsNaN(g) ? SwissGeodesy.GroundH - 50 : g, check, placesOnly: mode == "--cube-places");
        Console.WriteLine($"{mode}: {files} files in Earth positions in {region}, {sw.Elapsed.TotalSeconds:F1} s");
        return check.Report() ? 0 : 1;
    }

    /// <summary>
    /// The Earth positions of the region files in <paramref name="region"/>, each file read, given its <see cref="GeoPoint"/>s at
    /// its own heights (else <paramref name="ground"/>, LHN95 at an LV95 point) and true headings, written back, read again and
    /// checked. The number of files.
    /// </summary>
    public static int AddEarthPositions(string region, Func<double, double, double> ground, Check check, bool placesOnly = false)
    {
        int files = 0;
        string Out(string name) => Path.Combine(region, name);

        if (File.Exists(Out(PlaceIndex.FileName)))
        {
            var index = PlaceIndex.FromJson(File.ReadAllText(Out(PlaceIndex.FileName)));
            foreach (var p in index.Places)
                p.Geo = GeoPoint.FromLv95(p.E, p.N, p.Elevation != 0 ? p.Elevation : ground(p.E, p.N));
            File.WriteAllText(Out(PlaceIndex.FileName), index.ToJson());
            var back = PlaceIndex.FromJson(File.ReadAllText(Out(PlaceIndex.FileName)));
            check.Count("places", index.Places.Count, back.Places.Count);
            foreach (var p in back.Places) check.Point("places", p.Geo, p.E, p.N);
            files++;
        }
        if (placesOnly) return files;

        if (File.Exists(Out(PoiFile.FileName)))
        {
            var data = PoiFile.Decode(File.ReadAllBytes(Out(PoiFile.FileName)));
            foreach (var p in data.Pois)
            {
                double h = p.Elevation;
                p.Geo = GeoPoint.FromLv95(p.E, p.N, h);
                if (p.Outline is { } o)
                {
                    var g = new double[o.Length];
                    for (int k = 0; k + 1 < o.Length; k += 2)
                    {
                        var q = GeoPoint.FromLv95(o[k], o[k + 1], h);   // a lake's outline at its surface, the POI's height
                        (g[k], g[k + 1]) = (q.Lat, q.Lon);
                    }
                    p.GeoOutline = g;
                }
            }
            File.WriteAllBytes(Out(PoiFile.FileName), PoiFile.Encode(data));
            var back = PoiFile.Decode(File.ReadAllBytes(Out(PoiFile.FileName)));
            check.Count("POIs", data.Pois.Count, back.Pois.Count);
            foreach (var p in back.Pois)
            {
                check.Point("POIs", p.Geo, p.E, p.N, 0.05);   // the v1 records keep LV95 to the decimetre
                if (p.Outline is { } o && p.GeoOutline is { } g)
                    for (int k = 0; k + 1 < o.Length; k += 2) check.Point("POI outlines", new GeoPoint(g[k], g[k + 1], p.Geo!.Value.H), o[k], o[k + 1], 0.05);
            }
            files++;
        }

        if (File.Exists(Out(LandingIndex.FileName)))
        {
            var index = LandingIndex.FromJson(File.ReadAllText(Out(LandingIndex.FileName)));
            void Ribbon(PierRibbon r) => r.GeoPoints = r.Points.Select(q => GeoPoint.FromLv95(q[0], q[1], q[2])).ToList();
            foreach (var l in index.Landings)
            {
                double h = l.Berth?.Level ?? ground(l.E, l.N);
                l.Geo = GeoPoint.FromLv95(l.E, l.N, h);
                if (l.Berth is { } b)
                {
                    b.Geo = GeoPoint.FromLv95(b.E, b.N, b.Level);
                    b.GeoHeading = GeoPoint.TrueBearing(b.Heading, b.E, b.N, b.Level);
                }
                foreach (var r in l.Ribbons) Ribbon(r);
                l.GeoBollards = l.Bollards.Select(q => GeoPoint.FromLv95(q[0], q[1], q[2])).ToList();
            }
            foreach (var j in index.Jetties) Ribbon(j.Ribbon);
            File.WriteAllText(Out(LandingIndex.FileName), index.ToJson());
            var back = LandingIndex.FromJson(File.ReadAllText(Out(LandingIndex.FileName)));
            check.Count("landings", index.Landings.Count, back.Landings.Count);
            check.Count("jetties", index.Jetties.Count, back.Jetties.Count);
            foreach (var l in back.Landings)
            {
                check.Point("landings", l.Geo, l.E, l.N);
                if (l.Berth is { } b) { check.Point("berths", b.Geo, b.E, b.N); check.Heading("berth headings", b.GeoHeading, b.Heading, b.E, b.N, b.Level); }
                for (int k = 0; k < l.Bollards.Count; k++) check.Point("bollards", l.GeoBollards?[k], l.Bollards[k][0], l.Bollards[k][1]);
            }
            foreach (var r in back.Landings.SelectMany(l => l.Ribbons).Concat(back.Jetties.Select(j => j.Ribbon)))
                for (int k = 0; k < r.Points.Count; k++) check.Point("pier points", r.GeoPoints?[k], r.Points[k][0], r.Points[k][1]);
            files++;
        }

        if (File.Exists(Out(AirportIndex.FileName)))
        {
            var index = AirportIndex.FromJson(File.ReadAllText(Out(AirportIndex.FileName)));
            foreach (var a in index.Airports)
            {
                a.Geo = GeoPoint.FromLv95(a.E, a.N, ground(a.E, a.N));
                foreach (var r in a.Runways)
                {
                    r.Geo1 = GeoPoint.FromLv95(r.E1, r.N1, ground(r.E1, r.N1));
                    r.Geo2 = GeoPoint.FromLv95(r.E2, r.N2, ground(r.E2, r.N2));
                }
                foreach (var s in a.Stands)
                {
                    double h = double.IsNaN(s.Ground) ? ground(s.E, s.N) : s.Ground;
                    s.Geo = GeoPoint.FromLv95(s.E, s.N, h);
                    s.GeoHeading = GeoPoint.TrueBearing(s.Heading, s.E, s.N, h);
                }
            }
            File.WriteAllText(Out(AirportIndex.FileName), index.ToJson());
            var back = AirportIndex.FromJson(File.ReadAllText(Out(AirportIndex.FileName)));
            check.Count("airports", index.Airports.Count, back.Airports.Count);
            foreach (var a in back.Airports)
            {
                check.Point("airports", a.Geo, a.E, a.N);
                foreach (var r in a.Runways) { check.Point("runway ends", r.Geo1, r.E1, r.N1); check.Point("runway ends", r.Geo2, r.E2, r.N2); }
                foreach (var s in a.Stands) { check.Point("stands", s.Geo, s.E, s.N); check.Heading("stand headings", s.GeoHeading, s.Heading, s.E, s.N, s.Geo!.Value.ToLv95().H); }
            }
            files++;
        }

        if (File.Exists(Out(RopewayInfoIndex.FileName)))
        {
            var index = RopewayInfoIndex.FromJson(File.ReadAllText(Out(RopewayInfoIndex.FileName)));
            index.Ropeways = index.Ropeways.Select(r => r with
            {
                Geo0 = GeoPoint.FromLv95(r.E0, r.N0, ground(r.E0, r.N0)),
                Geo1 = GeoPoint.FromLv95(r.E1, r.N1, ground(r.E1, r.N1)),
            }).ToList();
            File.WriteAllText(Out(RopewayInfoIndex.FileName), index.ToJson());
            var back = RopewayInfoIndex.FromJson(File.ReadAllText(Out(RopewayInfoIndex.FileName)));
            check.Count("lifts", index.Ropeways.Count, back.Ropeways.Count);
            foreach (var r in back.Ropeways) { check.Point("lift ends", r.Geo0, r.E0, r.N0); check.Point("lift ends", r.Geo1, r.E1, r.N1); }
            files++;
        }
        return files;
    }

    /// <summary>Counts per file and every Earth position against its LV95 fields.</summary>
    public sealed class Check
    {
        private readonly SortedDictionary<string, (int Points, double Worst, int Missing)> _points = new(StringComparer.Ordinal);
        private readonly List<string> _failures = new();
        private readonly List<string> _counts = new();

        public void Count(string what, int lv95, int written)
        {
            _counts.Add($"{what} {written}/{lv95}");
            if (lv95 != written) _failures.Add($"{what}: {written} read back, {lv95} written");
        }

        public void Point(string what, GeoPoint? geo, double e, double n, double bar = Bar)
        {
            var have = _points.GetValueOrDefault(what);
            if (geo is not { } g) { _points[what] = (have.Points, have.Worst, have.Missing + 1); return; }
            var (be, bn, _) = g.ToLv95();
            double d = Math.Sqrt((be - e) * (be - e) + (bn - n) * (bn - n));
            _points[what] = (have.Points + 1, Math.Max(have.Worst, d), have.Missing);
            if (d > bar) _failures.Add(string.Create(CultureInfo.InvariantCulture, $"{what} at {e:F1},{n:F1}: {d * 100:F2} cm back to LV95"));
        }

        public void Heading(string what, double? trueDeg, double gridDeg, double e, double n, double h)
        {
            if (trueDeg is not { } t) { _failures.Add($"{what}: no true heading"); return; }
            // a true bearing turned back to the grid must give the stored grid bearing (0.001°)
            double grid = (t + SwissGeodesy.GridConvergenceDeg(e, n, h)) % 360;
            double diff = Math.Abs(((grid - gridDeg) % 360 + 540) % 360 - 180);
            var have = _points.GetValueOrDefault(what);
            _points[what] = (have.Points + 1, Math.Max(have.Worst, diff), have.Missing);
            if (diff > 1e-3) _failures.Add(string.Create(CultureInfo.InvariantCulture, $"{what} at {e:F1},{n:F1}: {diff:F4}° off"));
        }

        public bool Report()
        {
            Console.WriteLine("  counts (read back/written): " + string.Join(", ", _counts));
            foreach (var (what, (points, worst, missing)) in _points)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {what}: {points} back to LV95, worst {(what.Contains("heading") ? $"{worst:F5}°" : $"{worst * 100:F3} cm")}{(missing > 0 ? $", {missing} without an Earth position" : "")}"));
            foreach (var f in _failures.Take(10)) Console.WriteLine("  FAIL " + f);
            bool ok = _failures.Count == 0 && _points.Values.All(p => p.Missing == 0);
            Console.WriteLine(ok ? "cube region files: PASS" : $"cube region files: FAIL ({_failures.Count})");
            return ok;
        }
    }
}
