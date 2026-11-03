using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The cube-sphere tiler (#1443, G1; docs/plans/global-tiling.md §7, tools/cube-tiles note): every
/// source resampled onto cube tiles in true Earth positions, beside the LV95 build (nothing here
/// reads or writes it).
///
/// <code>
/// --cube --cube-out DIR [--in SWISSALTI3D_DIR ...] [--dem lambert93,DIR[,dz] ...] [--cube-level 13]
///   [--cube-bits 10] [--cube-bundle 8] [--cube-box minE,minN,maxE,maxN] [--tlm GPKG] [--buildings GPKG]
///   [--cube-bdtopo] [--cube-cache DIR] [--cube-cache-tiles 300] [--jobs N] [--cube-check]
/// </code>
/// </summary>
public static class CubeStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube");

    public sealed class Options
    {
        public List<string> In { get; } = new();
        public List<string> Dems { get; } = new();
        public string? Out, Cache, Tlm, Buildings;
        public int Level = 13, Bits = -1, Bundle = -1, CacheTiles = 300, Jobs = Environment.ProcessorCount;
        public (double MinE, double MinN, double MaxE, double MaxN)? Box;
        public bool BdTopo, Check, CheckOnly;
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
                case "--jobs": o.Jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"--cube: unknown option {args[i]}"); return 2;
            }
        }
        if (o.Out == null) { Console.Error.WriteLine("--cube needs --cube-out DIR"); return 2; }
        if (o.Bits < 0) o.Bits = 23 - o.Level; // ~1.2 m at a face centre whatever the level
        if (o.Bundle < 0) o.Bundle = o.Level - 5;
        o.Cache ??= o.Out.TrimEnd('/', '\\') + "_temp";
        Directory.CreateDirectory(o.Out);

        var swiss = o.In.Count > 0 ? new SwissAltiSource(o.In, Path.Combine(o.Cache, "alti")) { Capacity = o.CacheTiles } : null;
        var dems = new List<ForeignDem>();
        foreach (var d in o.Dems)
        {
            var p = d.Split(',');
            dems.Add(new ForeignDem(DemProjection.Parse(p[0]), p[1], p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 0) { Jobs = o.Jobs, CacheBytes = 2048L << 20 });
        }
        var src = new CubeHeightSource(swiss, dems);
        Console.WriteLine($"cube: level {o.Level}, {1 << o.Bits} cells a tile, bundles at level {o.Bundle}; "
            + $"swissALTI3D {swiss?.Tiles.Count ?? 0} tiles, {dems.Count} foreign DEM(s)");

        if (o.CheckOnly) return CubeCheck.Run(o, src);

        var sw = Stopwatch.StartNew();
        var (keys, french) = SelectTiles(o, swiss, dems);
        Console.WriteLine($"cube: {keys.Count} tiles to build ({french.Count} with foreign data), chosen in {sw.Elapsed.TotalSeconds:F1} s");

        var bundles = keys.GroupBy(k => k.AncestorAt(o.Bundle)).OrderBy(g => g.Key.Face).ThenBy(g => g.Key.Y).ThenBy(g => g.Key.X).ToList();
        var manifest = new CubeManifest { LeafLevel = o.Level, Bits = o.Bits, BundleLevel = o.Bundle };
        if (swiss != null) manifest.Sources.Add("swissALTI3D 0.5 m (LHN95 -> WGS84 by SwissGeodesy)");
        foreach (var d in dems) manifest.Sources.Add($"{d.Name} (dz {d.OffsetZ.ToString(CultureInfo.InvariantCulture)} m)");

        SqliteConnection? tlm = o.Tlm != null ? GeoPackageReader.Open(o.Tlm) : null;
        SqliteConnection? b3d = o.Buildings != null ? GeoPackageReader.Open(o.Buildings) : null;
        using var bdtopo = o.BdTopo ? new BdTopoClient() : null;
        var resampler = new CubeResampler(src);
        var features = new CubeFeatures(src, o.Level);
        long bytes = 0, roads = 0, buildings = 0;
        double heightSeconds = 0, featureSeconds = 0;
        var oldOffsets = new List<double>();
        int done = 0;

        foreach (var bundle in bundles)
        {
            ct.ThrowIfCancellationRequested();
            // features first, into empty holders: the heights are then encoded the moment they exist, so
            // a bundle never holds its tiles' heights all at once (1,024 x 4 MB at level 13)
            var holders = bundle.ToDictionary(k => k, k => new CubeTile { Key = k, Bits = o.Bits, Heights = Array.Empty<float>() });
            var fs = Stopwatch.StartNew();
            if (tlm != null || bdtopo != null)
            {
                var cur = new CubeHeightSource.Cursors(dems.Count);
                var raw = new List<CubeFeatures.Raw>();
                if (tlm != null && swiss != null)
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

            var encoded = new ConcurrentBag<(CubeKey Key, byte[] Bytes)>();
            var hs = Stopwatch.StartNew();
            Parallel.ForEach(bundle.OrderBy(k => k.Y).ThenBy(k => k.X), new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, key =>
            {
                var cur = new CubeHeightSource.Cursors(dems.Count);
                if (resampler.Heights(key, o.Bits, cur) is not { } h) return;
                var holder = holders[key];
                encoded.Add((key, CubeTileCodec.Encode(new CubeTile { Key = key, Bits = o.Bits, Heights = h, Roads = holder.Roads, Buildings = holder.Buildings })));
            });
            heightSeconds += hs.Elapsed.TotalSeconds;

            var list = encoded.OrderBy(e => e.Key.Y).ThenBy(e => e.Key.X).ToList();
            if (list.Count > 0) CubeBundle.Write(Path.Combine(o.Out, CubeBundle.FileName(bundle.Key)), bundle.Key, list);
            foreach (var (key, bb) in list)
            {
                var t = holders[key];
                var (_, _, min, max) = CubeTileCodec.ReadHeader(bb);
                manifest.Tiles.Add(new CubeManifestTile { Key = key.ToString(), Min = min, Max = max, Roads = t.Roads.Count, Buildings = t.Buildings.Count, Bytes = bb.Length });
                bytes += bb.Length;
                roads += t.Roads.Count;
                buildings += t.Buildings.Count;
            }
            done += bundle.Count();
            Console.WriteLine($"  [{done}/{keys.Count}] bundle {bundle.Key.Describe()}: {list.Count} tiles, {list.Sum(e => (long)e.Bytes.Length) / 1e6:F1} MB, "
                + $"{sw.Elapsed.TotalSeconds:F0} s");
        }
        tlm?.Dispose();
        b3d?.Dispose();

        File.WriteAllText(Path.Combine(o.Out, CubeManifest.FileName), manifest.ToJson());
        int n = manifest.Tiles.Count;
        Console.WriteLine($"cube: {n} tiles in {bundles.Count} bundles, {bytes / 1e6:F1} MB ({(n > 0 ? bytes / n / 1024.0 : 0):F0} KB a tile), "
            + $"{roads} road runs, {buildings} buildings; {sw.Elapsed.TotalSeconds:F0} s "
            + $"(heights and encoding {heightSeconds:F0} s = {(n > 0 ? heightSeconds * o.Jobs / n : 0):F2} CPU-s a tile on {o.Jobs} threads, features {featureSeconds:F0} s), "
            + $"peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
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
    /// data, a 100 m lattice of probe points, each giving the cell it falls in. The second set is the
    /// cells with foreign data (for BD TOPO).
    /// </summary>
    private static (HashSet<CubeKey> Keys, HashSet<CubeKey> Foreign) SelectTiles(Options o, SwissAltiSource? swiss, List<ForeignDem> dems)
    {
        var keys = new HashSet<CubeKey>();
        var foreign = new HashSet<CubeKey>();
        bool InBox(TileId t) => o.Box is not { } b || (t.MinE + 1000 > b.MinE && t.MinE < b.MaxE && t.MinN + 1000 > b.MinN && t.MinN < b.MaxN);
        void Probe(TileId t, HashSet<CubeKey> into)
        {
            for (int j = 0; j < 10; j++)
                for (int i = 0; i < 10; i++)
                {
                    double e = t.MinE + 50 + 100 * i, n = t.MinN + 50 + 100 * j;
                    if (o.Box is { } b && (e < b.MinE || e > b.MaxE || n < b.MinN || n > b.MaxN)) continue;
                    var g = SwissGeodesy.ToWgs84(e, n, 500);
                    into.Add(CubeKey.FromLatLon(g.LatDeg, g.LonDeg, o.Level));
                }
        }
        if (swiss != null)
            foreach (var t in swiss.Tiles.Where(InBox)) Probe(t, keys);
        foreach (var dem in dems)
        {
            var (minE, minN, maxE, maxN) = dem.BoundsLv95Km();
            if (o.Box is { } b) (minE, minN, maxE, maxN) = (Math.Max(minE, (int)(b.MinE / 1000)), Math.Max(minN, (int)(b.MinN / 1000)), Math.Min(maxE, (int)(b.MaxE / 1000) + 1), Math.Min(maxN, (int)(b.MaxN / 1000) + 1));
            var tiles = dem.TilesIn(minE, minN, maxE, maxN, new HashSet<TileId>(), 1);
            foreach (var t in tiles) Probe(t, foreign);
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
