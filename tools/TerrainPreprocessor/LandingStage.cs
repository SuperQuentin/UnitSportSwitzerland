using System.Collections.Concurrent;
using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--landings</c> (#377): the boat landings and harbour jetties of the region into
/// <c>landings.json</c>, from swissTLM3D (<c>tlm_oev_haltestelle</c> <c>Haltestelle Schiff</c>,
/// <c>tlm_oev_schifffahrt</c> ferry ends, <c>tlm_bauten_verkehrsbaute_lin</c> <c>Hafensteg</c>) over
/// the tiles already built: their beds (<c>.terr</c>) and still water (<c>.water</c>, so after the
/// water pass). <see cref="LandingPlanner"/> plans every pier; the game builds them
/// (<c>docs/notes/world/landings.md</c>). Re-runnable, one region-wide file, seconds.
/// </summary>
public static class LandingStage
{
    public static int Run(string outDir, string tlmPath, string? file = null)
    {
        if (!File.Exists(tlmPath))
        {
            Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlmPath}");
            return 1;
        }
        var manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"--landings needs a built region: no {manifestPath}");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        var tiles = TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToHashSet();
        if (tiles.Count == 0) return 0;
        var sampler = new TileSampler(outDir, tiles);
        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MaxN) - ChunkFormat.TileSizeM, maxN = tiles.Max(t => t.MaxN);
        bool InRegion(double e, double n) => tiles.Contains(TileId.FromLv95(e, n));

        var index = new LandingIndex();
        using var conn = GeoPackageReader.Open(tlmPath);

        // the boat stops
        var stops = new List<(string Name, double E, double N, string Source)>();
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_oev_haltestelle", new[] { "objektart", "name" }, minE, minN, maxE, maxN))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.GetString(0) != "Haltestelle Schiff") continue;
                string name = r.IsDBNull(1) ? "Landing" : r.GetString(1);
                foreach (var (e, n, _) in GeoPackageReader.ParsePoints((byte[])r[2]))
                    if (InRegion(e, n)) stops.Add((name, e, n, "Haltestelle Schiff"));
            }
        // a ferry's ends are landings too, where no stop is listed already
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_oev_schifffahrt", new[] { "objektart", "name" }, minE, minN, maxE, maxN))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string kind = r.IsDBNull(0) ? "Faehre" : r.GetString(0);
                string name = r.IsDBNull(1) ? kind : r.GetString(1);
                foreach (var line in GeoPackageReader.ParseLines((byte[])r[2]))
                    foreach (int i in new[] { 0, line.Count - 1 })
                    {
                        double e = line.E[i], n = line.N[i];
                        if (!InRegion(e, n) || stops.Any(s => Math.Abs(s.E - e) < 200 && Math.Abs(s.N - n) < 200)) continue;
                        stops.Add(($"{name} ({(i == 0 ? "a" : "b")})", e, n, kind));
                    }
            }
        stops.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        int skipped = 0;
        int surveyed = 0;
        foreach (var (name, e, n, source) in stops)
        {
            // a pier swissTLM3D maps (a Steg the roads draw) ending at the stop
            var road = LandingPlanner.FindRoadEnd(e, n, RoadsAround(outDir, e, n), sampler);
            if (road != null) surveyed++;
            if (LandingPlanner.PlanLanding(name, e, n, sampler, source: source, road: road) is { } landing) index.Landings.Add(landing);
            else skipped++;
        }

        // the harbour jetties
        int jettySkipped = 0;
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_bauten_verkehrsbaute_lin", new[] { "objektart", "uuid" }, minE, minN, maxE, maxN))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.GetString(0) != "Hafensteg") continue;
                string id = r.IsDBNull(1) ? "" : r.GetString(1);
                foreach (var line in GeoPackageReader.ParseLines((byte[])r[2]))
                {
                    var pts = Enumerable.Range(0, line.Count).Select(i => (line.E[i], line.N[i], line.Z[i])).ToList();
                    if (!pts.Any(p => InRegion(p.Item1, p.Item2))) continue;
                    if (LandingPlanner.PlanJetty(id, pts, sampler) is { } jetty) index.Jetties.Add(jetty);
                    else jettySkipped++;
                }
            }
        index.Jetties.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        file ??= Path.Combine(outDir, LandingIndex.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, index.ToJson());
        Console.WriteLine($"Landings: {index.Landings.Count} landings ({surveyed} at a surveyed pier, {skipped} stops with no water or shore by them), "
            + $"{index.Jetties.Count} jetties ({jettySkipped} dry), in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var l in index.Landings)
            if (l.Berth is { } b)
                Console.WriteLine(FormattableString.Invariant(
                    $"  {l.Name}: berth {b.E:F0}/{b.N:F0} heading {b.Heading:F0}°, {b.Depth:F1} m of water{(b.Fits ? "" : " (too shallow for the steamer)")}, pier {PierLength(l):F0} m"));
        return 0;
    }

    /// <summary>The road segments of the tiles round a point (the built <c>.road</c> files: what the game draws).</summary>
    private static IEnumerable<(TileId, RoadSegment)> RoadsAround(string dir, double e, double n)
    {
        var at = TileId.FromLv95(e, n);
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var id = new TileId(at.E + de, at.N + dn);
                if (Math.Abs(e - Math.Clamp(e, id.MinE, id.MinE + ChunkFormat.TileSizeM)) > LandingPlanner.RoadEndReach
                    || Math.Abs(n - Math.Clamp(n, id.MaxN - ChunkFormat.TileSizeM, id.MaxN)) > LandingPlanner.RoadEndReach) continue;
                string path = Path.Combine(dir, RoadFormat.FileName(id));
                if (!File.Exists(path)) continue;
                RoadTile tile;
                using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
                foreach (var seg in tile.Segments) yield return (id, seg);
            }
    }

    private static double PierLength(Landing l)
    {
        double len = 0;
        foreach (var r in l.Ribbons)
            for (int i = 1; i < r.Points.Count; i++)
                len += Math.Sqrt(Math.Pow(r.Points[i][0] - r.Points[i - 1][0], 2) + Math.Pow(r.Points[i][1] - r.Points[i - 1][1], 2));
        return len;
    }

    /// <summary>The bed and the still water from the built tiles, nearest vertex (1 m), decoded on demand.</summary>
    private sealed class TileSampler : IShoreSampler
    {
        private readonly string _dir;
        private readonly HashSet<TileId> _tiles;
        private readonly ConcurrentDictionary<TileId, (ChunkGrid? Grid, ushort[]? Levels)> _cache = new();

        public TileSampler(string dir, HashSet<TileId> tiles) => (_dir, _tiles) = (dir, tiles);

        private (ChunkGrid? Grid, ushort[]? Levels) Load(TileId id) => _cache.GetOrAdd(id, t =>
        {
            if (!_tiles.Contains(t)) return (null, null);
            ChunkGrid? grid = null;
            ushort[]? levels = null;
            string terr = Path.Combine(_dir, ChunkFormat.ChunkFileName(t));
            if (File.Exists(terr)) using (var fs = File.OpenRead(terr)) grid = ChunkCodec.Decode(fs);
            string water = Path.Combine(_dir, WaterFormat.FileName(t));
            if (File.Exists(water)) using (var fs = File.OpenRead(water)) levels = WaterFormat.Decode(fs).Levels;
            return (grid, levels);
        });

        private static (int Col, int Row) Vertex(TileId id, double e, double n) =>
            (Math.Clamp((int)Math.Round(e - id.MinE), 0, ChunkFormat.GridSize - 1),
             Math.Clamp((int)Math.Round(id.MaxN - n), 0, ChunkFormat.GridSize - 1));

        public double Ground(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            var (grid, _) = Load(id);
            if (grid == null) return double.NaN;
            var (c, r) = Vertex(id, e, n);
            return grid.HeightMetersAt(c, r);
        }

        public double Level(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            var (_, levels) = Load(id);
            if (levels == null) return double.NaN;
            var (c, r) = Vertex(id, e, n);
            ushort q = levels[r * WaterFormat.Size + c];
            return q == 0 ? double.NaN : ChunkFormat.Dequantize(q);
        }
    }
}
