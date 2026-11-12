using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The globe's coarse levels (#1430 H4; docs/plans/ellipsoid-handover.md §6a, tools/cube-tiles note):
/// every tile of levels 0..N over the whole Earth from NOAA ETOPO 2022 60″ (ice surface and sea
/// floor on EGM2008, plus its EGM2008 undulation grid: h = max(H, 0) + N, WGS84 ellipsoidal as the
/// leaves; the sea at its surface, the floor's relief left to the colour),
/// written in G1's format (<see cref="CubeTileCodec"/>, <see cref="CubeBundle"/>, a version-2
/// <see cref="CubeManifest"/>). The leaves' own tiler (<see cref="CubeStage"/>) is untouched: this is
/// its global source, as G1 expected (#1455).
///
/// <code>
/// --cube-global --etopo DIR --cube-out DIR [--cube-levels 0-4] [--jobs N] [--cube-check]
/// </code>
///
/// <para>
/// <b>A height is a function of its lattice point</b>, never of its tile, so neighbours' edges are
/// the same bits: the point's geodetic normal gives its latitude and longitude, and the height is the
/// mean of a k × k grid of bilinear ETOPO samples over the point's own footprint (half a lattice
/// step each way, k up to 8), the same from any face. <c>--cube-check</c> proves it: every shared
/// point of every built tile, keyed by its ECEF bits, holds one height.
/// </para>
/// </summary>
public static class CubeGlobal
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-global");

    private static int BundleLevel(int level) => CubeKey.PyramidBundleLevel(level);

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? etopo = null, outDir = null;
        int minLevel = 0, maxLevel = 4, jobs = Environment.ProcessorCount;
        bool check = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-global": break;
                case "--etopo": etopo = args[++i]; break;
                case "--cube-out": outDir = args[++i]; break;
                case "--cube-levels":
                    var r = args[++i].Split('-');
                    minLevel = int.Parse(r[0], CultureInfo.InvariantCulture);
                    maxLevel = int.Parse(r[^1], CultureInfo.InvariantCulture);
                    break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-check": check = true; break;
                default: Console.Error.WriteLine($"--cube-global: unknown option {args[i]}"); return 2;
            }
        }
        if (etopo == null || outDir == null) { Console.Error.WriteLine("--cube-global needs --etopo DIR and --cube-out DIR"); return 2; }
        Directory.CreateDirectory(outDir);

        var sw = Stopwatch.StartNew();
        var grid = EtopoGrid.Load(etopo, jobs);
        Console.WriteLine($"cube-global: ETOPO 2022 {grid.Width} x {grid.Height}, h = H + N, loaded in {sw.Elapsed.TotalSeconds:F0} s");

        var manifest = new CubeManifest { LeafLevel = maxLevel, Bits = CubeKey.LatticeBits(maxLevel), BundleLevel = BundleLevel(maxLevel) };
        manifest.Sources.Add("NOAA NCEI ETOPO 2022 60 arc-second, surface (ice) on EGM2008 + its EGM2008 geoid grid (public domain); below sea level at the geoid");
        long bytes = 0;
        var built = new ConcurrentDictionary<CubeKey, CubeTile>();
        for (int level = minLevel; level <= maxLevel; level++)
        {
            int bits = CubeKey.LatticeBits(level);
            manifest.Levels.Add(new CubeManifestLevel { Level = level, Bits = bits, BundleLevel = BundleLevel(level) });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cube-global: level {level}: {1 << bits} cells a tile, quantum {CubeKey.QuantumFor(level)} m"));
        }

        // tiles grouped by bundle: levels 0-3 share their face's bundle, as the index carries each key's level
        var keys = new List<CubeKey>();
        for (int level = minLevel; level <= maxLevel; level++)
            for (int f = 0; f < CubeSphere.Faces; f++)
                for (int y = 0; y < 1 << level; y++)
                    for (int x = 0; x < 1 << level; x++)
                        keys.Add(new CubeKey(f, level, x, y));
        foreach (var bundle in keys.GroupBy(k => k.AncestorAt(BundleLevel(k.Level))).OrderBy(g => g.Key.Level).ThenBy(g => g.Key.Face).ThenBy(g => g.Key.Y).ThenBy(g => g.Key.X))
        {
            ct.ThrowIfCancellationRequested();
            var encoded = new ConcurrentBag<(CubeKey Key, byte[] Bytes, CubeTile Tile)>();
            Parallel.ForEach(bundle, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, key =>
            {
                int bits = CubeKey.LatticeBits(key.Level);
                var tile = new CubeTile { Key = key, Bits = bits, Quantum = CubeKey.QuantumFor(key.Level), Heights = Heights(grid, key, bits) };
                // what the file holds, so the check compares the stored heights
                if (check) tile = CubeTileCodec.Decode(CubeTileCodec.Encode(tile));
                encoded.Add((key, CubeTileCodec.Encode(tile), tile));
                if (check) built[key] = tile;
            });
            var list = encoded.OrderBy(e => e.Key.Level).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.X).ToList();
            CubeBundle.Write(Path.Combine(outDir, CubeBundle.FileName(bundle.Key)), bundle.Key, list.Select(e => (e.Key, e.Bytes)).ToList());
            foreach (var (key, bb, _) in list)
            {
                var (_, _, min, max) = CubeTileCodec.ReadHeader(bb);
                manifest.Tiles.Add(new CubeManifestTile { Key = key.ToString(), Min = min, Max = max, Bytes = bb.Length });
                bytes += bb.Length;
            }
            Console.WriteLine($"  bundle {bundle.Key.Describe()}: {list.Count} tiles, {list.Sum(e => (long)e.Bytes.Length) / 1e6:F1} MB, {sw.Elapsed.TotalSeconds:F0} s");
        }
        File.WriteAllText(Path.Combine(outDir, CubeManifest.FileName), manifest.ToJson());
        foreach (var lv in manifest.Levels)
        {
            var tiles = manifest.Tiles.Where(t => CubeKey.Parse(t.Key).Level == lv.Level).ToList();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"cube-global: level {lv.Level}: {tiles.Count} tiles, {tiles.Sum(t => (long)t.Bytes) / 1e6:F1} MB ({tiles.Average(t => t.Bytes) / 1024:F0} KB a tile), {tiles.Min(t => t.Min):F0} to {tiles.Max(t => t.Max):F0} m"));
        }
        Console.WriteLine($"cube-global: {manifest.Tiles.Count} tiles, {bytes / 1e6:F1} MB, {sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return check ? CheckEdges(built) : 0;
    }

    /// <summary>A tile's (2^bits + 1)² heights, each from its own lattice point alone.</summary>
    public static float[] Heights(EtopoGrid grid, CubeKey key, int bits)
    {
        int n = (1 << bits) + 1, lv = key.Level + bits;
        long gi0 = (long)key.X << bits, gj0 = (long)key.Y << bits;
        // half a lattice step at a face centre, metres: the footprint's half side
        double half = 0.5 * 1.22 * Math.Pow(2, Math.Max(0, 23 - lv));
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, gi0 + i), CubeSphere.Lattice(lv, gj0 + j));
                double lat = Math.Atan2(normal.Z, Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y)) * 180 / Math.PI;
                double lon = Math.Atan2(normal.Y, normal.X) * 180 / Math.PI;
                h[j * n + i] = (float)grid.Footprint(lat, lon, half);
            }
        return h;
    }

    /// <summary>Every shared lattice point (keyed by its level and ECEF bits) holds one height across a level's tiles.</summary>
    internal static int CheckEdges(ConcurrentDictionary<CubeKey, CubeTile> tiles)
    {
        var seen = new Dictionary<(int Level, EcefPos P), float>();
        long points = 0, differ = 0;
        foreach (var (key, t) in tiles)
        {
            int s = 1 << t.Bits;
            for (int k = 0; k <= s; k++)
                foreach (var (i, j) in new[] { (k, 0), (k, s), (0, k), (s, k) })
                {
                    var p = key.Vertex(t.Bits, i, j, 0);
                    float h = t.HeightAt(i, j);
                    points++;
                    if (seen.TryGetValue((key.Level, p), out var other)) { if (other != h) differ++; }
                    else seen[(key.Level, p)] = h;
                }
        }
        Console.WriteLine($"cube-global check: {points} edge vertices, {seen.Count} distinct points, {differ} differ");
        return differ == 0 ? 0 : 1;
    }
}

/// <summary>
/// ETOPO 2022's global grid in memory as ellipsoidal heights (surface + geoid undulation, one float
/// array, ~0.9 GB at 60″), sampled bilinearly with longitude wrapping and over a footprint.
/// </summary>
public sealed class EtopoGrid
{
    public int Width, Height;
    /// <summary>Pixel (0, 0)'s centre and the pixel size, degrees (rows north to south).</summary>
    public double Lon0, Lat0, Step;
    public float[] H = Array.Empty<float>();

    /// <summary>
    /// A finer regional ground over ETOPO where it holds (#1746): the game's 500 m Swiss relief, which the flat world's
    /// fill stands on, handed over to ETOPO over its edge (<see cref="SwissRelief.EdgeFadeM"/>). Null: ETOPO alone.
    /// </summary>
    public SwissRelief? Regional { get; set; }

    public static EtopoGrid Load(string dir, int jobs)
    {
        string surface = Directory.GetFiles(dir, "*_surface.tif").Single(), geoid = Directory.GetFiles(dir, "*_geoid.tif").Single();
        using var s = TiffFloat.Open(surface);
        using var g = TiffFloat.Open(geoid);
        if (s.Width != g.Width || s.Height != g.Height) throw new InvalidDataException("ETOPO surface and geoid grids differ");
        var grid = new EtopoGrid { Width = s.Width, Height = s.Height, Lon0 = s.OriginX, Lat0 = s.OriginY, Step = s.PixelX, H = new float[(long)s.Width * s.Height] };
        var blocks = new List<(int X, int Y)>();
        for (int by = 0; by < s.BlocksDown; by++)
            for (int bx = 0; bx < s.BlocksAcross; bx++)
                blocks.Add((bx, by));
        Parallel.ForEach(blocks, new ParallelOptions { MaxDegreeOfParallelism = jobs }, b =>
        {
            var hs = s.ReadBlock(b.X, b.Y);
            var ns = g.ReadBlock(b.X, b.Y);
            for (int r = 0; r < s.BlockH; r++)
            {
                int y = b.Y * s.BlockH + r;
                if (y >= s.Height) break;
                for (int c = 0; c < s.BlockW; c++)
                {
                    int x = b.X * s.BlockW + c;
                    if (x >= s.Width) break;
                    // the sea at its surface (the geoid), the sea floor dropped: the globe shows the
                    // water, and Blue Marble's shading already draws the floor's relief
                    grid.H[(long)y * s.Width + x] = Math.Max(hs[r * s.BlockW + c], 0f) + ns[r * s.BlockW + c];
                }
            }
        });
        return grid;
    }

    /// <summary>The height at a latitude and longitude, degrees: <see cref="Regional"/>'s where it holds, else ETOPO's bilinear.</summary>
    public double At(double lat, double lon)
    {
        if (Regional != null && Regional.TryAt(lat, lon, out double regional, out double weight))
            return weight >= 1 ? regional : weight * regional + (1 - weight) * Etopo(lat, lon);
        return Etopo(lat, lon);
    }

    /// <summary>ETOPO's own bilinear height at a latitude and longitude, degrees.</summary>
    public double Etopo(double lat, double lon)
    {
        double fx = (lon - Lon0) / Step, fy = (Lat0 - lat) / Step;
        fy = Math.Clamp(fy, 0, Height - 1);
        int x0 = (int)Math.Floor(fx), y0 = Math.Min((int)fy, Height - 2);
        double tx = fx - x0, ty = fy - y0;
        int xa = Wrap(x0), xb = Wrap(x0 + 1);
        long r0 = (long)y0 * Width, r1 = r0 + Width;
        double top = H[r0 + xa] + (H[r0 + xb] - H[r0 + xa]) * tx;
        double bottom = H[r1 + xa] + (H[r1 + xb] - H[r1 + xa]) * tx;
        return top + (bottom - top) * ty;
    }

    private int Wrap(int x) => ((x % Width) + Width) % Width;

    /// <summary>
    /// The mean over a square footprint <paramref name="halfM"/> metres each way round a point: a
    /// k × k grid of bilinear samples, k from the footprint against the grid's own spacing (1 to 8).
    /// </summary>
    public double Footprint(double lat, double lon, double halfM)
    {
        const double R = 6_371_000;
        double pixelM = Step * Math.PI / 180 * R;
        int k = Math.Clamp((int)Math.Ceiling(2 * halfM / pixelM), 1, 8);
        if (k == 1) return At(lat, lon);
        double cos = Math.Max(Math.Cos(lat * Math.PI / 180), 0.01), sum = 0;
        for (int b = 0; b < k; b++)
        {
            double dy = -halfM + (b + 0.5) * 2 * halfM / k;
            double la = Math.Clamp(lat + dy / R * 180 / Math.PI, -90, 90);
            for (int a = 0; a < k; a++)
            {
                double dx = -halfM + (a + 0.5) * 2 * halfM / k;
                sum += At(la, lon + dx / (R * cos) * 180 / Math.PI);
            }
        }
        return sum / (k * k);
    }
}
