using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

/// <summary>
/// "--roads DIR": the road embankment blend (#125, <c>TerrainMeshBuilder.ComputeRoadBlend</c>) on
/// real tiles. Per tile it times the blend and its collision application, and counts the drops the
/// blended ground still has: lattice edges inside the corridors that fall more than
/// <see cref="CliffM"/> in one metre, split into those a retaining wall hides and the rest.
/// <c>--render E,N</c> (repeatable) writes a 400 m shaded relief, bare | blended with wall faces
/// in white, to test_output/blend/roads_E_N.png.
///
///   dotnet run --project tools/BlendCheck -c Release -- --roads DIR [--tiles E_N,E_N] [--render E,N]
/// </summary>
static class RoadBlendCheck
{
    private const double CliffM = 3.0;

    /// <summary>Metres per pixel of a render (<c>--scale</c>, default 1).</summary>
    private static double Scale = 1;

    public static int Run(string[] args)
    {
        string dir = Arg(args, "--roads") ?? "terrain_chunks";
        if (Arg(args, "--scale") is { } sc) Scale = double.Parse(sc, CultureInfo.InvariantCulture);
        var ids = Arg(args, "--tiles") is { } spec
            ? spec.Split(',').Select(Parse).ToList()
            : Directory.GetFiles(dir, "roads_*.road").Select(f => Parse(Path.GetFileNameWithoutExtension(f)[6..])).OrderBy(t => t.N).ThenBy(t => t.E).ToList();
        var renders = new List<(double E, double N)>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--render")
            {
                var p = args[i + 1].Split(',');
                renders.Add((double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)));
            }
        foreach (var r in renders)
        {
            var t = TileId.FromLv95(r.E, r.N);
            if (!ids.Contains(t)) ids.Add(t);
        }

        var blendMs = new List<double>();
        var firstMs = new List<double>();
        var applyMs = new List<double>();
        long cells = 0, cliffs = 0, hidden = 0, walls = 0, tlmWalls = 0, tlmSegments = 0;
        var heights = new int[7];   // wall peak heights: <2, <4, <6, <10, <15, <25, more
        double wallM = 0, wallM2 = 0, maxWall = 0, roadKm = 0;
        int n = ChunkFormat.GridSize;

        foreach (var id in ids)
        {
            string terr = Path.Combine(dir, ChunkFormat.ChunkFileName(id));
            string road = Path.Combine(dir, RoadFormat.FileName(id));
            if (!File.Exists(terr) || !File.Exists(road)) continue;
            ChunkGrid grid;
            using (var s = File.OpenRead(terr)) grid = ChunkCodec.Decode(s);
            RoadTile tile;
            using (var s = File.OpenRead(road)) tile = RoadCodec.Decode(s);

            // the first call on a tile, then the median of three like a steady worker would see it;
            // the first tiles' first calls are what a cold game pays (tiered JIT)
            var cold = Stopwatch.StartNew();
            TerrainMeshBuilder.ComputeRoadBlend(tile);
            if (firstMs.Count < 20) firstMs.Add(cold.Elapsed.TotalMilliseconds);
            var times = new double[3];
            TerrainMeshBuilder.RoadBlend blend = null!;
            for (int k = 0; k < times.Length; k++)
            {
                var sw = Stopwatch.StartNew();
                blend = TerrainMeshBuilder.ComputeRoadBlend(tile);
                times[k] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            blendMs.Add(times[1]);

            var bare = new float[n * n];
            for (int i = 0; i < bare.Length; i++) bare[i] = (float)ChunkFormat.Dequantize(grid.Heights[i]);
            var map = (float[])bare.Clone();
            var sw2 = Stopwatch.StartNew();
            TerrainMeshBuilder.ApplyRoadBlend(map, blend, 0.0);
            applyMs.Add(sw2.Elapsed.TotalMilliseconds);
            cells += blend.Cells.Length;

            foreach (var seg in tile.Segments)
                if (seg.Class <= RoadClass.Railway && RoadEmbankment.IsAtGrade(seg))
                    for (int i = 3; i < seg.Points.Length; i += 3)
                        roadKm += Math.Sqrt(Sq(seg.Points[i] - seg.Points[i - 3]) + Sq(seg.Points[i + 2] - seg.Points[i - 1])) / 1000;

            var inWall = WallCells(tile);
            tlmSegments += tile.Segments.Count(x => x.Class == RoadClass.Wall);
            foreach (var w in tile.LinearProps)
            {
                if (w.Type is not (LinearPropType.RetainingWallFill or LinearPropType.RetainingWallCut)) continue;
                if (w.Variant == RoadEmbankment.VariantTlmWall) { tlmWalls++; continue; }
                walls++;
                for (int i = 1; i < w.PointCount; i++)
                {
                    double len = Math.Sqrt(Sq(w.Points[i * 4] - w.Points[i * 4 - 4]) + Sq(w.Points[i * 4 + 2] - w.Points[i * 4 - 2]));
                    wallM += len;
                    wallM2 += len * (w.Points[i * 4 + 3] + w.Points[i * 4 - 1]) / 2;
                }
                double high = 0;
                for (int i = 0; i < w.PointCount; i++) high = Math.Max(high, w.Points[i * 4 + 3]);
                maxWall = Math.Max(maxWall, high);
                heights[high < 2 ? 0 : high < 4 ? 1 : high < 6 ? 2 : high < 10 ? 3 : high < 15 ? 4 : high < 25 ? 5 : 6]++;
                int mid = w.PointCount / 2;
                if (args.Contains("--walls"))
                {
                    // a --shot from 30 m out on the open side (right of the points), looking back at the face
                    int m0 = Math.Max(0, mid - 1), m1 = Math.Min(w.PointCount - 1, mid + 1);
                    double fx = w.Points[m1 * 4] - w.Points[m0 * 4], fz = w.Points[m1 * 4 + 2] - w.Points[m0 * 4 + 2];
                    double fl = Math.Max(1e-6, Math.Sqrt(fx * fx + fz * fz));
                    double rx = -fz / fl, rz = fx / fl;
                    double e0 = id.MinE + w.Points[mid * 4], n0 = id.MaxN - w.Points[mid * 4 + 2];
                    double top = w.Points[mid * 4 + 1] + w.Points[mid * 4 + 3];
                    var (oe, on) = Arg(args, "--origin") is { } o ? (double.Parse(o.Split(',')[0], CultureInfo.InvariantCulture), double.Parse(o.Split(',')[1], CultureInfo.InvariantCulture)) : (2583500.0, 1113500.0);
                    double cx = e0 + rx * 30 - oe, cz = on - n0 + rz * 30;
                    double yaw = Math.Atan2(rx, rz) * 180 / Math.PI;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  wall {w.Type,-17} {w.PointCount,3} pts  up to {high,5:F1} m  at {e0:F0},{n0:F0} top {top:F1}  shot {cx:F1},{top + 8:F1},{cz:F1},-15,{yaw:F1}"));
                }
            }

            // a drop counts once per lattice edge, and only where the blend moved the ground
            var touched = new bool[n * n];
            foreach (int c in blend.Cells) touched[c] = true;
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    int i = r * n + c;
                    foreach (int j in (ReadOnlySpan<int>)[c + 1 < n ? i + 1 : -1, r + 1 < n ? i + n : -1])
                    {
                        if (j < 0 || !(touched[i] || touched[j])) continue;
                        if (Math.Abs(map[i] - map[j]) <= CliffM) continue;
                        // the ground's own cliff, left alone, is not the blend's doing
                        if (Math.Abs(bare[i] - bare[j]) > CliffM && Math.Abs(map[i] - bare[i]) < 0.05 && Math.Abs(map[j] - bare[j]) < 0.05) continue;
                        if (inWall.Contains(i) || inWall.Contains(j)) hidden++;
                        else cliffs++;
                    }
                }

            foreach (var (e, nn) in renders)
                if (TileId.FromLv95(e, nn) == id) Render(id, bare, map, tile, e, nn);
        }

        if (blendMs.Count == 0) { Console.WriteLine("no tiles"); return 2; }
        var c0 = CultureInfo.InvariantCulture;
        string Dist(List<double> v)
        {
            var a = v.OrderBy(x => x).ToArray();
            return string.Create(c0, $"mean {a.Average():F2}  p50 {a[a.Length / 2]:F2}  p95 {a[(int)(a.Length * 0.95)]:F2}  max {a[^1]:F2} ms");
        }
        Console.WriteLine(string.Create(c0, $"road blend over {blendMs.Count} tiles, {roadKm:F0} km of at-grade lines"));
        Console.WriteLine($"  ComputeRoadBlend   {Dist(blendMs)}");
        Console.WriteLine($"  first call, first {firstMs.Count} tiles (cold JIT): {Dist(firstMs)}");
        Console.WriteLine($"  apply (collision)  {Dist(applyMs)}");
        Console.WriteLine(string.Create(c0, $"  blend cells {cells / (double)blendMs.Count:F0}/tile"));
        Console.WriteLine(string.Create(c0, $"  walls {walls} ({wallM / 1000:F2} km, {wallM2:F0} m2 of face, highest {maxWall:F1} m), TLM walls kept {tlmWalls}"));
        Console.WriteLine($"  wall peak heights <2 / <4 / <6 / <10 / <15 / <25 / more m: {string.Join(" / ", heights)};  TLM wall segments {tlmSegments}");
        Console.WriteLine(string.Create(c0, $"  drops > {CliffM} m per lattice edge: {cliffs} unhidden ({cliffs / Math.Max(roadKm, 1e-9):F1}/km), {hidden} inside a wall"));
        return 0;
    }

    /// <summary>Cells inside any wall's solid (face to back), where the one-cell transition belongs.</summary>
    private static HashSet<int> WallCells(RoadTile tile)
    {
        var set = new HashSet<int>();
        int n = ChunkFormat.GridSize;
        foreach (var w in tile.LinearProps)
        {
            if (w.Type is not (LinearPropType.RetainingWallFill or LinearPropType.RetainingWallCut)) continue;
            for (int i = 1; i < w.PointCount; i++)
            {
                double ax = w.Points[i * 4 - 4], az = w.Points[i * 4 - 2], bx = w.Points[i * 4], bz = w.Points[i * 4 + 2];
                double len = Math.Sqrt(Sq(bx - ax) + Sq(bz - az));
                if (len < 1e-6) continue;
                double ux = (bx - ax) / len, uz = (bz - az) / len;
                // left of the point order, seen from above with X east and Z south: (uz, -ux)
                double lx = uz, lz = -ux;
                for (double s = 0; s <= len; s += 0.5)
                    for (double d = -0.5; d <= w.Thickness + 0.5; d += 0.5)
                    {
                        int c = (int)Math.Round(ax + ux * s + lx * d), r = (int)Math.Round(az + uz * s + lz * d);
                        if ((uint)c < (uint)n && (uint)r < (uint)n) set.Add(r * n + c);
                    }
            }
        }
        return set;
    }

    private static void Render(TileId id, float[] bare, float[] map, RoadTile tile, double e, double nn)
    {
        const int size = 400;
        int n = ChunkFormat.GridSize;
        double span = size * Scale;
        double c0 = Math.Clamp(e - id.MinE - span / 2, 0, n - 1 - span), r0 = Math.Clamp(id.MaxN - nn - span / 2, 0, n - 1 - span);
        // bilinear between lattice vertices, so a zoomed render shows the slopes, not blocks
        float At(float[] h, int x, int y)
        {
            double fx = c0 + x * Scale, fy = r0 + y * Scale;
            int ix = Math.Min((int)fx, n - 2), iy = Math.Min((int)fy, n - 2);
            double tx = fx - ix, ty = fy - iy;
            double top = h[iy * n + ix] * (1 - tx) + h[iy * n + ix + 1] * tx;
            double bot = h[(iy + 1) * n + ix] * (1 - tx) + h[(iy + 1) * n + ix + 1] * tx;
            return (float)(top * (1 - ty) + bot * ty);
        }
        var pixels = new byte[size * size * 2];
        Shade(pixels, size, 0, (x, y) => At(bare, x, y));
        Shade(pixels, size, size, (x, y) => At(map, x, y));
        foreach (var w in tile.LinearProps)
            for (int i = 1; i < w.PointCount; i++)
                for (double t = 0; t <= 1; t += 0.02)
                {
                    int x = (int)Math.Round((w.Points[i * 4 - 4] + (w.Points[i * 4] - w.Points[i * 4 - 4]) * t - c0) / Scale);
                    int y = (int)Math.Round((w.Points[i * 4 - 2] + (w.Points[i * 4 + 2] - w.Points[i * 4 - 2]) * t - r0) / Scale);
                    if ((uint)x < size && (uint)y < size) pixels[y * 2 * size + size + x] = 255;
                }
        string outDir = Path.Combine(FindRepo(), "test_output", "blend");
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"roads_{e:F0}_{nn:F0}.png"));
        Png.WriteGray(path, pixels, 2 * size, size);
        Console.WriteLine($"  render: {path}");
    }

    /// <summary>Hillshade from the north-west at 45 degrees, 1 m per pixel, into one half of the image.</summary>
    private static void Shade(byte[] pixels, int size, int x0, Func<int, int, float> h)
    {
        double step = Scale;
        double l = 1 / Math.Sqrt(3);
        for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
            {
                double gx = (h(x + 1, y) - h(x - 1, y)) / (2 * step), gy = (h(x, y + 1) - h(x, y - 1)) / (2 * step);
                double nx = -gx, ny = gy, len = Math.Sqrt(nx * nx + ny * ny + 1);
                double shade = Math.Max(0, (nx * -l + ny * l + l) / len);
                pixels[y * 2 * size + x0 + x] = (byte)Math.Clamp(40 + 200 * shade, 0, 254);
            }
    }

    private static double Sq(double v) => v * v;

    private static TileId Parse(string s)
    {
        var p = s.Split('_');
        return new TileId(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "project.godot"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }
}
