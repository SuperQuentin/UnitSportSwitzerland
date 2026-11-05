using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The region-wide files in Earth positions (#1507, G2 B3 row 8; docs/notes/tools/cube-region-files.md): the LV95
/// build's places, POIs, landings, airports, lifts and test-region traffic endpoints, each written into a cube region
/// with every position also as latitude, longitude and ellipsoidal height (<see cref="GeoPoint"/>) and every grid
/// heading also from true north, rigorously (<see cref="SwissGeodesy"/>) at the point's own height (a summit's, a
/// deck's, still water, a stand's ground) or, where the file has none, the LV95 build's ground there. The LV95 fields
/// stay until D (the game reads them until then). Each stage keeps its maths: this writes their output in Earth
/// positions. No <c>horizon.bin</c> is written: the cube region's far terrain is its pyramid (<c>--cube-swiss</c>).
///
/// <code>
/// --cube-region-files --chunks LV95_CHUNKS --cube-out REGION
/// </code>
///
/// <para>
/// The check, at the end of every run: each written file read back, every Earth position taken back to LV95 at its
/// ellipsoidal height and held to the LV95 fields within 2 cm (the bar of row 8); counts equal file by file.
/// </para>
/// </summary>
public static class CubeRegionFiles
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-region-files");

    /// <summary>The bar: an Earth position taken back to LV95 against the LV95 fields (m).</summary>
    public const double Bar = 0.02;

    public static int Run(IReadOnlyList<string> args)
    {
        string? chunks = null, region = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-region-files": break;
                case "--chunks": chunks = args[++i]; break;
                case "--cube-out": region = args[++i]; break;
                default: Console.Error.WriteLine($"--cube-region-files: unknown option {args[i]}"); return 2;
            }
        }
        if (chunks == null || region == null) { Console.Error.WriteLine("--cube-region-files needs --chunks LV95_CHUNKS and --cube-out REGION"); return 2; }
        Directory.CreateDirectory(region);
        var sw = Stopwatch.StartNew();
        var ground = new LazyGround(chunks);
        var check = new Check();
        int files = 0;

        string In(string name) => Path.Combine(chunks, name);
        string Out(string name) => Path.Combine(region, name);

        if (File.Exists(In(PlaceIndex.FileName)))
        {
            var index = PlaceIndex.FromJson(File.ReadAllText(In(PlaceIndex.FileName)));
            foreach (var p in index.Places)
                p.Geo = GeoPoint.FromLv95(p.E, p.N, p.Elevation != 0 ? p.Elevation : ground.At(p.E, p.N));
            File.WriteAllText(Out(PlaceIndex.FileName), index.ToJson());
            var back = PlaceIndex.FromJson(File.ReadAllText(Out(PlaceIndex.FileName)));
            check.Count("places", index.Places.Count, back.Places.Count);
            foreach (var p in back.Places) check.Point("places", p.Geo, p.E, p.N);
            files++;
        }

        if (File.Exists(In(PoiFile.FileName)))
        {
            var data = PoiFile.Decode(File.ReadAllBytes(In(PoiFile.FileName)));
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

        if (File.Exists(In(LandingIndex.FileName)))
        {
            var index = LandingIndex.FromJson(File.ReadAllText(In(LandingIndex.FileName)));
            void Ribbon(PierRibbon r) => r.GeoPoints = r.Points.Select(q => GeoPoint.FromLv95(q[0], q[1], q[2])).ToList();
            foreach (var l in index.Landings)
            {
                double h = l.Berth?.Level ?? ground.At(l.E, l.N);
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

        if (File.Exists(In(AirportIndex.FileName)))
        {
            var index = AirportIndex.FromJson(File.ReadAllText(In(AirportIndex.FileName)));
            foreach (var a in index.Airports)
            {
                a.Geo = GeoPoint.FromLv95(a.E, a.N, ground.At(a.E, a.N));
                foreach (var r in a.Runways)
                {
                    r.Geo1 = GeoPoint.FromLv95(r.E1, r.N1, ground.At(r.E1, r.N1));
                    r.Geo2 = GeoPoint.FromLv95(r.E2, r.N2, ground.At(r.E2, r.N2));
                }
                foreach (var s in a.Stands)
                {
                    double h = double.IsNaN(s.Ground) ? ground.At(s.E, s.N) : s.Ground;
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

        if (File.Exists(In(RopewayInfoIndex.FileName)))
        {
            var index = RopewayInfoIndex.FromJson(File.ReadAllText(In(RopewayInfoIndex.FileName)));
            index.Ropeways = index.Ropeways.Select(r => r with
            {
                Geo0 = GeoPoint.FromLv95(r.E0, r.N0, ground.At(r.E0, r.N0)),
                Geo1 = GeoPoint.FromLv95(r.E1, r.N1, ground.At(r.E1, r.N1)),
            }).ToList();
            File.WriteAllText(Out(RopewayInfoIndex.FileName), index.ToJson());
            var back = RopewayInfoIndex.FromJson(File.ReadAllText(Out(RopewayInfoIndex.FileName)));
            check.Count("lifts", index.Ropeways.Count, back.Ropeways.Count);
            foreach (var r in back.Ropeways) { check.Point("lift ends", r.Geo0, r.E0, r.N0); check.Point("lift ends", r.Geo1, r.E1, r.N1); }
            files++;
        }

        if (File.Exists(In(TrafficEndpointsFile.FileName)))
        {
            var list = TrafficEndpointsFile.Read(In(TrafficEndpointsFile.FileName)).Select(x =>
            {
                double h = ground.At(x.E, x.N);
                // the heading: radians counter-clockwise from grid east; as a grid bearing, then true
                double grid = 90 - x.Heading * 180 / Math.PI;
                return x with { Geo = GeoPoint.FromLv95(x.E, x.N, h), GeoHeading = GeoPoint.TrueBearing(grid, x.E, x.N, h) };
            }).ToList();
            TrafficEndpointsFile.Write(Out(TrafficEndpointsFile.FileName), list);
            var back = TrafficEndpointsFile.Read(Out(TrafficEndpointsFile.FileName));
            check.Count("traffic endpoints", list.Count, back.Count);
            foreach (var x in back) check.Point("traffic endpoints", x.Geo, x.E, x.N);
            files++;
        }

        Console.WriteLine($"cube region files: {files} files into {region}, {sw.Elapsed.TotalSeconds:F1} s");
        return check.Report() ? 0 : 1;
    }

    /// <summary>The LV95 build's ground (its chunk grids, loaded on first use), 0 where it has none.</summary>
    private sealed class LazyGround(string dir)
    {
        private readonly Dictionary<TileId, ChunkGrid> _grids = new();
        private readonly HashSet<TileId> _tried = new();

        public double At(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            foreach (var t in (ReadOnlySpan<TileId>)[id, new TileId(id.E - 1, id.N), new TileId(id.E, id.N - 1), new TileId(id.E - 1, id.N - 1)])
                if (_tried.Add(t) && TileRewriterGrid(dir, t) is { } g) _grids[t] = g;
            return new Lv95Ground(_grids).Sample(e, n) ?? 0;
        }

        private static ChunkGrid? TileRewriterGrid(string dir, TileId id)
        {
            string path = Path.Combine(dir, ChunkFormat.ChunkFileName(id));
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return ChunkCodec.Decode(fs);
        }
    }

    /// <summary>Counts per file and every Earth position against its LV95 fields.</summary>
    private sealed class Check
    {
        private readonly SortedDictionary<string, (int Points, double Worst, int Missing)> _points = new(StringComparer.Ordinal);
        private readonly List<string> _failures = new();
        private readonly List<string> _counts = new();

        public void Count(string what, int lv95, int written)
        {
            _counts.Add($"{what} {written}/{lv95}");
            if (lv95 != written) _failures.Add($"{what}: {written} written, {lv95} in the LV95 file");
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
            Console.WriteLine("  counts (written/LV95): " + string.Join(", ", _counts));
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
