using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// What stands on the ground, baked into the pyramid for the far view (#1657, <see cref="CubeFarFeatures"/>): from the leaves'
/// cover, trees and buildings, per pyramid tile from level 13 up to <c>--cube-levels</c>' coarsest, the canopy, roof and
/// paved shares and the height of what stands there, on the lattice the globe draws. Written as <c>.cubf</c> bundles into
/// a pyramid beside its heights, and named in its manifest (each level's <c>features</c>).
/// <code>
/// --cube-features --cube-leaves DIR[,DIR...] --cube-out PYRAMID_DIR [--cube-levels 9-13] [--jobs N]
/// </code>
/// <list type="bullet">
/// <item>Each leaf first becomes a grid of <see cref="CubeFarFeatures.Cells"/>² cells (9.4 m): the cover's shares (the
/// lattice's vertices in the cell), the roofs' area (roof triangles, split until a piece is under a quarter cell, at the
/// cell of their middle) and their height over the ground, the trees' heights.</item>
/// <item>A level-13 vertex is the mean of the four leaf cells round it (it stands on their corner), a coarser one the tent
/// mean of the nine finer vertices round it: a function of the lattice point, so neighbours agree. No leaf, nothing.</item>
/// <item>Tiles: every ancestor of a leaf and its eight neighbours (so a tile edge never steps where the leaves end).</item>
/// </list>
/// </summary>
public static class CubeFeatureStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-features");

    private const int LeafLevel = 14, C = CubeFarFeatures.Cells, Lat = 512;

    /// <summary>The highest a roof counts over its ground, metres.</summary>
    private const double MaxBuildingM = 40;

    /// <summary>A roof: a triangle facing up at least this much (walls are skipped).</summary>
    private const float RoofNormalY = 0.2f;

    /// <summary>A leaf's cells: shares 0..1 and the heights of what stands there.</summary>
    private sealed class LeafCells
    {
        public readonly float[] Forest = new float[C * C], Built = new float[C * C], Paved = new float[C * C];
        public readonly float[] BuildingH = new float[C * C], TreeH = new float[C * C];
    }

    public static int Run(IReadOnlyList<string> args)
    {
        string? leavesDir = null, outDir = null;
        int minLevel = 9, maxLevel = 13, jobs = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-features": break;
                case "--cube-leaves": leavesDir = args[++i]; break;
                case "--cube-out": outDir = args[++i]; break;
                case "--cube-levels":
                    var r = args[++i].Split('-');
                    minLevel = int.Parse(r[0], CultureInfo.InvariantCulture);
                    maxLevel = int.Parse(r[^1], CultureInfo.InvariantCulture);
                    break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"--cube-features: unknown option {args[i]}"); return 2;
            }
        }
        if (leavesDir == null || outDir == null) { Console.Error.WriteLine("--cube-features needs --cube-leaves DIR and --cube-out PYRAMID_DIR"); return 2; }
        if (maxLevel >= LeafLevel) { Console.Error.WriteLine($"--cube-levels must stay above the leaves' level {LeafLevel}"); return 2; }
        string manifestPath = Path.Combine(outDir, CubeManifest.FileName);
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));
        var sw = Stopwatch.StartNew();

        // every leaf's cells, the first region that holds a leaf gives it
        var cells = new ConcurrentDictionary<CubeKey, LeafCells>();
        foreach (var dir in leavesDir.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int before = cells.Count;
            Parallel.ForEach(Directory.GetFiles(dir, "*.cubl"), new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
            {
                foreach (var (key, bytes) in CubeBundle.ReadAllEntries(path))
                {
                    if (key.Level != LeafLevel || cells.ContainsKey(key)) continue;
                    try
                    {
                        if (Cells(CubeLeafContent.Decode(bytes)) is { } c) cells.TryAdd(key, c);
                    }
                    catch (Exception e) when (e is InvalidDataException or ArgumentException)
                    {
                        Console.Error.WriteLine($"cube-features: {key}: {e.Message}");
                    }
                }
            });
            Console.WriteLine($"cube-features: {cells.Count - before} leaves from {dir}, {sw.Elapsed.TotalSeconds:F0} s");
        }

        // level by level: 13 from the leaves' cells, coarser from the finer level
        // the features are made whole each run: the old bundles go
        foreach (var old in Directory.GetFiles(outDir, "*.cubf")) File.Delete(old);
        Dictionary<CubeKey, CubeFarFeatures>? finer = null;
        long bytesOut = 0;
        for (int level = maxLevel; level >= minLevel; level--)
        {
            var keys = new HashSet<CubeKey>();
            foreach (var leaf in cells.Keys)
            {
                var k = leaf.AncestorAt(level);
                keys.Add(k);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (k.Neighbour(dx, dy) is { } n) keys.Add(n);
            }
            var made = new ConcurrentDictionary<CubeKey, CubeFarFeatures>();
            var source = finer;
            Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = jobs }, key =>
            {
                var f = level == LeafLevel - 1 ? FromLeaves(key, cells) : FromFiner(key, source!);
                if (f.Any) made[key] = f;
            });
            foreach (var bundle in made.Keys.GroupBy(k => k.AncestorAt(CubeKey.PyramidBundleLevel(k.Level))))
            {
                var list = bundle.OrderBy(k => k.Y).ThenBy(k => k.X).Select(k => (k, CubeFarFeaturesCodec.Encode(made[k]))).ToList();
                CubeBundle.Write(Path.Combine(outDir, CubeFarFeaturesCodec.FileName(bundle.Key)), bundle.Key, list, CubeFarFeaturesCodec.BundleMagic);
                bytesOut += list.Sum(e => (long)e.Item2.Length);
            }
            if (manifest.LevelOf(level) is { } lv) lv.Features = made.IsEmpty ? 0 : C;
            Console.WriteLine($"cube-features: level {level}: {made.Count} tiles with something standing on them, {sw.Elapsed.TotalSeconds:F0} s");
            finer = new Dictionary<CubeKey, CubeFarFeatures>(made);
        }
        foreach (var lv in manifest.Levels)
            if (lv.Level < minLevel || lv.Level > maxLevel) lv.Features = 0;
        manifest.Sources.RemoveAll(s => s.StartsWith("features ", StringComparison.Ordinal));
        manifest.Sources.Add($"features {minLevel}-{maxLevel}: canopy, roofs and paving baked from {cells.Count} leaves' cover, trees and buildings (#1657)");
        File.WriteAllText(manifestPath, manifest.ToJson());
        Console.WriteLine($"cube-features: {bytesOut / 1e6:F1} MB in levels {minLevel}-{maxLevel}, {sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return 0;
    }

    // ---- a leaf's cells -----------------------------------------------------------------------------------

    /// <summary>Default heights where the cover says trees and no tree record stands in the cell, metres.</summary>
    private static float DefaultTreeH(CoverClass c) => c switch
    {
        CoverClass.Forest => 22, CoverClass.OpenForest => 16, CoverClass.Woodland => 10, CoverClass.Shrub => 4,
        CoverClass.Orchard => 6, CoverClass.Park => 12, _ => 0,
    };

    /// <summary>How much of a cover class's ground is under canopy.</summary>
    private static float CanopyShare(CoverClass c) => c switch
    {
        CoverClass.Forest => 1f, CoverClass.OpenForest => 0.5f, CoverClass.Woodland => 0.8f, CoverClass.Shrub => 0.6f,
        CoverClass.Orchard => 0.35f, CoverClass.Park => 0.3f, CoverClass.Nursery => 0.4f, _ => 0f,
    };

    private static bool IsPaved(CoverClass c) => c is CoverClass.PavedArea or CoverClass.ParkingPublic or CoverClass.ParkingPrivate
        or CoverClass.RestArea or CoverClass.TownPaving or CoverClass.Runway or CoverClass.Platform or CoverClass.TunnelRoof
        || (int)c == 40; // the unpatterned public parking

    private static LeafCells? Cells(CubeLeafContent leaf)
    {
        if (leaf.Heights is not { } heights) return null;
        var key = leaf.Key;
        int bits = heights.Bits, step = (1 << bits) / C;
        var c = new LeafCells();
        // the lattice in the anchor frame, affine from three corners (a leaf sags 7 mm over 600 m)
        var (x00, y00, z00) = CubeGeometry.Exact(key, bits, 0, 0, 0);
        var (x10, y10, z10) = CubeGeometry.Exact(key, bits, 1 << bits, 0, 0);
        var (x01, y01, z01) = CubeGeometry.Exact(key, bits, 0, 1 << bits, 0);
        double ax = (x10 - x00) / C, az = (z10 - z00) / C, bx = (x01 - x00) / C, bz = (z01 - z00) / C;
        double det = ax * bz - az * bx;
        (double I, double J) CellOf(double x, double z)
        {
            double dx = x - x00, dz = z - z00;
            return ((dx * bz - dz * bx) / det, (ax * dz - az * dx) / det);
        }
        double cellArea = Math.Abs(det);
        // the ground's height in the anchor frame at a cell point: the ellipsoid's y there and the height over it
        // NaN past the leaf's data (a border leaf): what stands there is not counted
        double GroundY(double ci, double cj)
        {
            int i = Math.Clamp((int)Math.Round(ci * step), 0, 1 << bits), j = Math.Clamp((int)Math.Round(cj * step), 0, 1 << bits);
            float h = heights.HeightAt(i, j);
            double ey = y00 + (y10 - y00) * ci / C + (y01 - y00) * cj / C;
            return float.IsNaN(h) ? double.NaN : ey + h;
        }

        // the cover: each cell's lattice vertices (its south-west corner's step x step)
        var treeDefault = new float[C * C];
        if (leaf.Cover is { } cover)
        {
            int side = (1 << bits) + 1;
            for (int cj = 0; cj < C; cj++)
                for (int ci = 0; ci < C; ci++)
                {
                    float forest = 0, paved = 0, treeH = 0;
                    for (int b = 0; b < step; b++)
                        for (int a = 0; a < step; a++)
                        {
                            var cls = (CoverClass)cover[(cj * step + b) * side + ci * step + a];
                            float share = CanopyShare(cls);
                            forest += share;
                            treeH += share * DefaultTreeH(cls);
                            if (IsPaved(cls)) paved++;
                        }
                    int k = cj * C + ci;
                    float n = step * step;
                    c.Forest[k] = forest / n;
                    c.Paved[k] = paved / n;
                    treeDefault[k] = forest > 0 ? treeH / forest : 0;
                }
        }

        // the trees' own heights where they stand (the canopy share stays the cover's)
        var treeSum = new float[C * C];
        var treeCount = new int[C * C];
        if (leaf.Trees is { } trees)
            foreach (var t in trees)
            {
                var (ci, cj) = CellOf(t.X, t.Z);
                int i = (int)Math.Floor(ci), j = (int)Math.Floor(cj);
                if (i < 0 || j < 0 || i >= C || j >= C) continue;
                treeSum[j * C + i] += t.Height;
                treeCount[j * C + i]++;
            }
        for (int k = 0; k < C * C; k++)
            c.TreeH[k] = treeCount[k] > 0 ? treeSum[k] / treeCount[k] : treeDefault[k];

        // the roofs: each upward triangle's area at the cell of its middle (split until small), its height over the ground
        var roofArea = new double[C * C];
        var roofVolume = new double[C * C];
        void Roof(double ax0, double ay0, double az0, double bx0, double by0, double bz0, double cx0, double cy0, double cz0, int depth)
        {
            double area = Math.Abs((bx0 - ax0) * (cz0 - az0) - (cx0 - ax0) * (bz0 - az0)) / 2;
            if (area > cellArea / 4 && depth < 8)
            {
                double mabx = (ax0 + bx0) / 2, maby = (ay0 + by0) / 2, mabz = (az0 + bz0) / 2;
                double mbcx = (bx0 + cx0) / 2, mbcy = (by0 + cy0) / 2, mbcz = (bz0 + cz0) / 2;
                double mcax = (cx0 + ax0) / 2, mcay = (cy0 + ay0) / 2, mcaz = (cz0 + az0) / 2;
                Roof(ax0, ay0, az0, mabx, maby, mabz, mcax, mcay, mcaz, depth + 1);
                Roof(mabx, maby, mabz, bx0, by0, bz0, mbcx, mbcy, mbcz, depth + 1);
                Roof(mcax, mcay, mcaz, mbcx, mbcy, mbcz, cx0, cy0, cz0, depth + 1);
                Roof(mabx, maby, mabz, mbcx, mbcy, mbcz, mcax, mcay, mcaz, depth + 1);
                return;
            }
            double mx = (ax0 + bx0 + cx0) / 3, my = (ay0 + by0 + cy0) / 3, mz = (az0 + bz0 + cz0) / 3;
            var (ci, cj) = CellOf(mx, mz);
            int i = (int)Math.Floor(ci), j = (int)Math.Floor(cj);
            if (i < 0 || j < 0 || i >= C || j >= C) return;
            double ground = GroundY(ci, cj);
            if (double.IsNaN(ground)) return;
            // a roof over its ground, a tower's top capped: the far view's skin is a town's mass, not its spires
            double over = Math.Clamp(my - ground, 0, MaxBuildingM);
            roofArea[j * C + i] += area;
            roofVolume[j * C + i] += area * over;
        }
        if (leaf.Buildings is { } buildings)
            foreach (var bld in buildings.Buildings)
            {
                var t = bld.Triangles;
                for (int n = 0; n + 8 < t.Length; n += 9)
                {
                    // the triangle's normal: up enough to be a roof
                    double ux = t[n + 3] - t[n], uy = t[n + 4] - t[n + 1], uz = t[n + 5] - t[n + 2];
                    double vx = t[n + 6] - t[n], vy = t[n + 7] - t[n + 1], vz = t[n + 8] - t[n + 2];
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len < 1e-9 || Math.Abs(ny) / len < RoofNormalY) continue;
                    // a floor lies at the solid's base, whichever way it is wound: only what stands over it counts
                    if ((t[n + 1] + t[n + 4] + t[n + 7]) / 3 < bld.MinY + 0.5f) continue;
                    Roof(t[n], t[n + 1], t[n + 2], t[n + 3], t[n + 4], t[n + 5], t[n + 6], t[n + 7], t[n + 8], 0);
                }
            }
        for (int k = 0; k < C * C; k++)
        {
            c.Built[k] = (float)Math.Min(1, roofArea[k] / cellArea);
            c.BuildingH[k] = roofArea[k] > 0 ? (float)(roofVolume[k] / roofArea[k]) : 0;
        }
        return c;
    }

    // ---- the levels ---------------------------------------------------------------------------------------

    private static byte Share(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static byte SkinByte(double m) => (byte)Math.Clamp(Math.Round(m / CubeFarFeatures.SkinStepM), 0, 255);

    /// <summary>A level-13 tile: each drawn vertex the mean of the four leaf cells round it (it is their common corner).</summary>
    private static CubeFarFeatures FromLeaves(CubeKey key, ConcurrentDictionary<CubeKey, LeafCells> cells)
    {
        var f = CubeFarFeatures.Empty(key);
        int n = CubeFarFeatures.N;
        // in the leaves' global cell lattice: the tile's vertex a sits on cell corner 2a
        long gx0 = (long)key.X * C * 2, gy0 = (long)key.Y * C * 2;
        long sideCells = (1L << LeafLevel) * C;
        for (int b = 0; b < n; b++)
            for (int a = 0; a < n; a++)
            {
                double forest = 0, built = 0, paved = 0, skin = 0;
                for (int dy = -1; dy <= 0; dy++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        long gx = gx0 + 2 * a + dx, gy = gy0 + 2 * b + dy;
                        if (gx < 0 || gy < 0 || gx >= sideCells || gy >= sideCells) continue;
                        var leaf = new CubeKey(key.Face, LeafLevel, (int)(gx / C), (int)(gy / C));
                        if (!cells.TryGetValue(leaf, out var c)) continue;
                        int k = (int)(gy % C) * C + (int)(gx % C);
                        forest += c.Forest[k];
                        built += c.Built[k];
                        paved += c.Paved[k];
                        skin += c.Forest[k] * c.TreeH[k] + c.Built[k] * c.BuildingH[k];
                    }
                int v = b * n + a;
                f.Forest[v] = Share(forest / 4);
                f.Built[v] = Share(built / 4);
                f.Paved[v] = Share(paved / 4);
                f.Skin[v] = SkinByte(skin / 4);
            }
        return f;
    }

    private static (CubeFarFeatures? Tile, int Index) Finer(Dictionary<CubeKey, CubeFarFeatures> finer, int face, int level, long gx, long gy)
    {
        long last = (1L << level) - 1;
        foreach (long tx in gx % C == 0 && gx > 0 ? new[] { gx / C, gx / C - 1 } : new[] { gx / C })
            foreach (long ty in gy % C == 0 && gy > 0 ? new[] { gy / C, gy / C - 1 } : new[] { gy / C })
            {
                if (tx < 0 || ty < 0 || tx > last || ty > last) continue;
                if (finer.TryGetValue(new CubeKey(face, level, (int)tx, (int)ty), out var t))
                    return (t, (int)(gy - ty * C) * CubeFarFeatures.N + (int)(gx - tx * C));
            }
        return (null, 0);
    }

    /// <summary>A coarser tile: each drawn vertex the tent mean (¼ ½ ¼ each way) of the nine finer vertices round it.</summary>
    private static CubeFarFeatures FromFiner(CubeKey key, Dictionary<CubeKey, CubeFarFeatures> finer)
    {
        var f = CubeFarFeatures.Empty(key);
        int n = CubeFarFeatures.N;
        long side = (1L << (key.Level + 1)) * C;
        ReadOnlySpan<float> w = [0.25f, 0.5f, 0.25f];
        for (int b = 0; b < n; b++)
            for (int a = 0; a < n; a++)
            {
                long fx = ((long)key.X * C + a) * 2, fy = ((long)key.Y * C + b) * 2;
                double forest = 0, built = 0, paved = 0, skin = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        long gx = Math.Clamp(fx + dx, 0, side), gy = Math.Clamp(fy + dy, 0, side);
                        // a finer tile holding the vertex: a vertex on a tile edge is in both, the same value
                        var (t, k) = Finer(finer, key.Face, key.Level + 1, gx, gy);
                        if (t == null) continue;
                        float wt = w[dx + 1] * w[dy + 1];
                        forest += wt * t.Forest[k];
                        built += wt * t.Built[k];
                        paved += wt * t.Paved[k];
                        skin += wt * t.Skin[k];
                    }
                int v = b * n + a;
                f.Forest[v] = (byte)Math.Round(forest);
                f.Built[v] = (byte)Math.Round(built);
                f.Paved[v] = (byte)Math.Round(paved);
                f.Skin[v] = (byte)Math.Round(skin);
            }
        return f;
    }
}
