using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Another body's coarse levels (#1849, H7c of docs/plans/planets.md; the Earth's are <see cref="CubeGlobal"/>): every
/// tile of levels 0..N of the body's own cube (<see cref="CubeShape"/>) from one whole-body DEM in simple cylindrical
/// form (<see cref="CylRaster"/>: LRO LOLA's 118 m for the Moon, MGS MOLA's 463 m for Mars), written in G1's format as
/// the Earth's: <see cref="CubeTileCodec"/> tiles in <see cref="CubeBundle"/>s and a <see cref="CubeManifest"/> that
/// names the body. Its colour is baked afterwards like the Earth's (<c>--globealbedo ... --body</c>).
///
/// <code>
/// --cube-body moon --dem FILE --cube-out DIR [--cube-levels 0-5] [--jobs N] [--cube-check]
/// --cube-body mars --dem MOLA.tif --areoid mega90n000eb.img --cube-out DIR --cube-levels 0-4
/// </code>
///
/// <para>
/// <b>A height is a function of its lattice point</b>, as <see cref="CubeGlobal"/>'s: the point's planetocentric
/// latitude and longitude, and the mean of bilinear DEM samples over the point's own footprint, so two faces that hold
/// one edge point compute the same bits. The DEM is read one face at a time, a window of rows and longitudes round the
/// face (about 1 GB of LOLA for an equatorial face, 2.6 GB for a polar one), each row once and in file order.
/// </para>
/// </summary>
public static class CubeBody
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-body");

    /// <summary>The bodies the tools tile, by the name <c>--cube-body</c> takes.</summary>
    public static CubeShape? CubeOf(string name) => name.ToLowerInvariant() switch
    {
        "moon" => CubeShape.Moon,
        "mars" => CubeShape.Mars,
        "mercury" => CubeShape.Mercury,
        "venus" => CubeShape.Venus,
        _ => null,
    };

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? body = null, dem = null, outDir = null, areoid = null;
        int minLevel = 0, maxLevel = 5, jobs = Math.Max(1, Environment.ProcessorCount / 2);
        bool check = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-body": body = args[++i]; break;
                case "--dem": dem = args[++i]; break;
                case "--areoid": areoid = args[++i]; break;
                case "--cube-out": outDir = args[++i]; break;
                case "--cube-levels":
                    var r = args[++i].Split('-');
                    minLevel = int.Parse(r[0], CultureInfo.InvariantCulture);
                    maxLevel = int.Parse(r[^1], CultureInfo.InvariantCulture);
                    break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-check": check = true; break;
                default: Console.Error.WriteLine($"--cube-body: unknown option {args[i]}"); return 2;
            }
        }
        if (body == null || dem == null || outDir == null) { Console.Error.WriteLine("--cube-body needs a body, --dem FILE and --cube-out DIR"); return 2; }
        var cube = CubeOf(body);
        if (cube == null) { Console.Error.WriteLine($"--cube-body: no body called {body} (moon, mars)"); return 2; }
        Directory.CreateDirectory(outDir);

        var sw = Stopwatch.StartNew();
        using var raster = CylRaster.Open(dem);
        // MOLA's heights are over its areoid (#1853): turned into heights over the IAU ellipsoid
        var datum = areoid != null ? Areoid.Load(areoid) : null;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cube-body {body}: {Path.GetFileName(dem)} {raster.Width} x {raster.Height} ({raster.Step * 3600:F1}″, {raster.Step * Math.PI / 180 * cube.Shape.A:F0} m a pixel), scale {raster.Scale}, offset {raster.Offset}"));

        var manifest = new CubeManifest
        {
            LeafLevel = maxLevel, Bits = CubeKey.LatticeBits(maxLevel), BundleLevel = CubeKey.PyramidBundleLevel(maxLevel),
            Body = char.ToUpperInvariant(body[0]) + body[1..].ToLowerInvariant(),
            Orientation = "face 0 at 0N 0E, twist 0",
        };
        manifest.Sources.Add($"{Path.GetFileName(dem)}: heights above the body's reference {(cube.Shape.F == 0 ? "sphere" : "ellipsoid")} (NASA/USGS, public domain)");
        if (areoid != null) manifest.Sources.Add($"{Path.GetFileName(areoid)}: the areoid the DEM is measured from, turned to the ellipsoid (MGS MOLA MEGDR, NASA, public domain)");
        for (int level = minLevel; level <= maxLevel; level++)
            manifest.Levels.Add(new CubeManifestLevel { Level = level, Bits = CubeKey.LatticeBits(level), BundleLevel = CubeKey.PyramidBundleLevel(level) });

        var built = new ConcurrentDictionary<CubeKey, CubeTile>();
        long bytes = 0;
        for (int face = 0; face < CubeSphere.Faces; face++)
        {
            ct.ThrowIfCancellationRequested();
            var (y0, y1, x0, w) = CubeBodyHeights.WindowOf(cube, face, raster, minLevel);
            var window = raster.Read(y0, y1, x0, w);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  face {face}: rows {Math.Max(0, y0)}-{Math.Min(raster.Height - 1, y1)}, {window.Columns} columns, {window.Bytes / 1e9:F2} GB read in {sw.Elapsed.TotalSeconds:F0} s"));

            var keys = new List<CubeKey>();
            for (int level = minLevel; level <= maxLevel; level++)
                for (int y = 0; y < 1 << level; y++)
                    for (int x = 0; x < 1 << level; x++)
                        keys.Add(new CubeKey(face, level, x, y));
            foreach (var bundle in keys.GroupBy(k => k.AncestorAt(CubeKey.PyramidBundleLevel(k.Level))).OrderBy(g => g.Key.Level).ThenBy(g => g.Key.Y).ThenBy(g => g.Key.X))
            {
                ct.ThrowIfCancellationRequested();
                var encoded = new ConcurrentBag<(CubeKey Key, byte[] Bytes)>();
                Parallel.ForEach(bundle, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, key =>
                {
                    int bits = CubeKey.LatticeBits(key.Level);
                    var tile = new CubeTile { Key = key, Bits = bits, Quantum = CubeKey.QuantumFor(key.Level), Heights = CubeBodyHeights.Heights(cube, window, key, bits, datum) };
                    var b = CubeTileCodec.Encode(tile);
                    encoded.Add((key, b));
                    if (check) built[key] = CubeTileCodec.Decode(b);
                });
                var list = encoded.OrderBy(e => e.Key.Level).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.X).ToList();
                CubeBundle.Write(Path.Combine(outDir, CubeBundle.FileName(bundle.Key)), bundle.Key, list);
                foreach (var (key, bb) in list)
                {
                    var (_, _, min, max) = CubeTileCodec.ReadHeader(bb);
                    manifest.Tiles.Add(new CubeManifestTile { Key = key.ToString(), Min = min, Max = max, Bytes = bb.Length });
                    bytes += bb.Length;
                }
            }
            // the window goes before the next is read (a polar one is 2.6 GB): never two in memory
            window = null!;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Console.WriteLine($"  face {face}: done, {bytes / 1e6:F0} MB so far, {sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        }
        File.WriteAllText(Path.Combine(outDir, CubeManifest.FileName), manifest.ToJson());
        foreach (var lv in manifest.Levels)
        {
            var tiles = manifest.Tiles.Where(t => CubeKey.Parse(t.Key).Level == lv.Level).ToList();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"cube-body {body}: level {lv.Level}: {tiles.Count} tiles, {tiles.Sum(t => (long)t.Bytes) / 1e6:F1} MB, {tiles.Min(t => t.Min):F0} to {tiles.Max(t => t.Max):F0} m"));
        }
        Console.WriteLine($"cube-body {body}: {manifest.Tiles.Count} tiles, {bytes / 1e6:F1} MB, {sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return check ? CubeGlobal.CheckEdges(built) : 0;
    }
}
