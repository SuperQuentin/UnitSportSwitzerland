using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain;
using UnitSport.Terrain.Biomes;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using static System.Console;

/// <summary>
/// The earth fill's biomes (#1809, <c>docs/plans/fill-biomes.md</c>, <see cref="BiomePass"/>): a block of side × side
/// fill leaves at each test spot built as the game builds them (the generator on the pyramid), then put in their biomes.
/// Checks:
/// <list type="bullet">
/// <item>each far spot's main biome is the expected one (desert, steppe, tundra, ice, jungle, sea);</item>
/// <item>the Swiss controls (Lavaux, the Salève) are left to the bit: cover, trees, buildings and water unchanged;</item>
/// <item>shared leaf edges agree on their cover.</item>
/// </list>
/// A top view of each block (cover colours, relief shading, trees, buildings, water), the generator's on the left and the
/// biome's on the right, into <c>--out</c> as PNG.
///
///   dotnet run --project tools/BlendCheck -c Release -- --fill-biomes [--pyramid D:/UnitSportSwitzerland/cube_global]
///     [--side 3] [--out DIR] [--spots desert,sea]
/// </summary>
internal static class FillBiomesCheck
{
    private sealed record Spot(string Name, double Lat, double Lon, Biome? Expect);

    public static int Run(string[] args)
    {
        const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N
        int side = int.TryParse(Value(args, "--side"), out int s) ? s : 3;
        string? outDir = Value(args, "--out");
        if (outDir != null) Directory.CreateDirectory(outDir);
        var pyramid = PyramidBase.Open(Value(args, "--pyramid") ?? "D:/UnitSportSwitzerland/cube_global");
        WriteLine(pyramid != null ? $"the fill on the pyramid (to level {pyramid.FinestLevel})" : "no pyramid: the generator's own relief");
        WriteLine(PhotoStats.Embedded is { } ps ? $"photo statistics {ps.Cols} x {ps.Rows}" : "no photo statistics embedded");

        var (dLat, dLon) = Lv95Source.Instance.ToLatLon(1829400, -515818);
        var (lvLat, lvLon) = Lv95Source.Instance.ToLatLon(2547200, 1147800);
        var spots = new List<Spot>
        {
            new("desert", dLat, dLon, Biome.Desert),
            new("steppe", 48.5, 67, Biome.Steppe),
            new("tundra", 69, 70, Biome.Tundra),
            new("ice", 72, -40, Biome.Ice),
            new("jungle", -3, -62, Biome.TropicalForest),
            new("sea", 30, -40, Biome.Sea),
            new("lavaux", lvLat, lvLon, null),
            new("saleve", 46.12, 6.18, null),
        };
        if (Value(args, "--spots") is { } only)
        {
            var names = only.Split(',');
            spots = spots.Where(p => names.Contains(p.Name)).ToList();
        }

        var world = new ProceduralWorld(AnchorE, AnchorN);
        var gen = new GeneratedLeaves(world, pyramid: pyramid, noBiomes: true);
        var chain = FillBiome.Default;
        int fails = 0;
        void Check(string what, bool ok, string detail = "")
        {
            WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
            if (!ok) fails++;
        }

        foreach (var spot in spots)
        {
            var sw = Stopwatch.StartNew();
            var centre = CubeKey.FromLatLon(spot.Lat, spot.Lon, LeafFrames.LeafLevel);
            var (le, ln) = Lv95Source.Instance.FromLatLon(spot.Lat, spot.Lon);
            WriteLine(string.Create(CultureInfo.InvariantCulture, $"{spot.Name}: {spot.Lat:F3}, {spot.Lon:F3} (LV95 {le:F0},{ln:F0}), {centre}"));
            int r = side / 2;
            var keys = new Dictionary<(int, int), CubeKey>();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (centre.Neighbour(dx, dy) is { } k && k.Face == centre.Face) keys[(dx, dy)] = k;
            var before = new Dictionary<(int, int), CubeLeafContent>();
            var after = new Dictionary<(int, int), CubeLeafContent>();
            var stats = new Dictionary<(int, int), BiomePass.Stats>();
            Parallel.ForEach(keys, kv =>
            {
                var a = gen.OnPyramid(gen.Build(kv.Value));
                var b = gen.OnPyramid(gen.Build(kv.Value));
                var st = BiomePass.Apply(b, LeafFrames.Swiss.TransformOf(kv.Value), chain, GeneratedLeaves.InsideMapM);
                lock (before)
                {
                    before[kv.Key] = a;
                    after[kv.Key] = b;
                    stats[kv.Key] = st;
                }
            });
            var byBiome = new int[Enum.GetValues<Biome>().Length];
            var bySource = new int[Enum.GetValues<BiomeSource>().Length];
            int tb = 0, ta = 0, bb = 0, ba = 0, total = 0;
            foreach (var st in stats.Values)
            {
                for (int k = 0; k < byBiome.Length; k++) byBiome[k] += st.ByBiome[k];
                for (int k = 0; k < bySource.Length; k++) bySource[k] += st.BySource[k];
                total += st.Samples;
                tb += st.TreesBefore; ta += st.TreesAfter; bb += st.BuildingsBefore; ba += st.BuildingsAfter;
            }
            string shares = string.Join(", ", Enum.GetValues<Biome>().Where(b => byBiome[(int)b] > 0)
                .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b} {100.0 * byBiome[(int)b] / Math.Max(1, total):F0}%")));
            string sources = string.Join(", ", Enum.GetValues<BiomeSource>().Where(b => bySource[(int)b] > 0)
                .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b} {100.0 * bySource[(int)b] / Math.Max(1, total):F0}%")));
            WriteLine($"  {keys.Count} leaves, {sw.Elapsed.TotalSeconds:F1} s; biomes {(total == 0 ? "untouched (Swiss guard)" : shares)}; by {(total == 0 ? "-" : sources)}");
            WriteLine($"  trees {tb} -> {ta}, buildings {bb} -> {ba}");

            if (spot.Expect is { } expect)
            {
                int main = Array.IndexOf(byBiome, byBiome.Max());
                Check($"main biome {expect}", total > 0 && (Biome)main == expect, $"{(Biome)main}");
            }
            else
            {
                bool same = keys.Keys.All(k => Same(before[k], after[k]));
                Check("Swiss control unchanged to the bit", same);
            }
            Check("shared leaf edges agree on cover", EdgesAgree(after, keys));
            if (outDir != null)
            {
                string path = Path.Combine(outDir, $"fill_biomes_{spot.Name}.png");
                Render(path, keys, before, after, side);
                WriteLine($"  {path}");
            }
        }
        WriteLine(fails == 0 ? "RESULT: ok" : $"RESULT: FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }

    private static bool Same(CubeLeafContent a, CubeLeafContent b) =>
        (a.Cover ?? []).AsSpan().SequenceEqual(b.Cover ?? [])
        && (a.Trees?.Count ?? 0) == (b.Trees?.Count ?? 0) && (a.Trees ?? []).SequenceEqual(b.Trees ?? [])
        && (a.Buildings?.Buildings.Count ?? 0) == (b.Buildings?.Buildings.Count ?? 0)
        && (a.Water == null) == (b.Water == null)
        && (a.Water == null || a.Water.Level.AsSpan().SequenceEqual(b.Water!.Level));

    /// <summary>Each east and north neighbour pair: the shared edge's cover the same from both leaves.</summary>
    private static bool EdgesAgree(Dictionary<(int, int), CubeLeafContent> leaves, Dictionary<(int, int), CubeKey> keys)
    {
        foreach (var ((dx, dy), c) in leaves)
        {
            if (c.Cover is not { } a) continue;
            int n = (int)Math.Sqrt(a.Length);
            if (leaves.TryGetValue((dx + 1, dy), out var e) && e.Cover is { } b)
                for (int j = 0; j < n; j++)
                    if (a[j * n + n - 1] != b[j * n]) return false;
            if (leaves.TryGetValue((dx, dy + 1), out var no) && no.Cover is { } d)
                for (int i = 0; i < n; i++)
                    if (a[(n - 1) * n + i] != d[i]) return false;
        }
        return true;
    }

    private const int Px = 128;

    private static void Render(string path, Dictionary<(int, int), CubeKey> keys, Dictionary<(int, int), CubeLeafContent> before,
        Dictionary<(int, int), CubeLeafContent> after, int side)
    {
        int r = side / 2, w = side * Px, gap = 8, width = 2 * w + gap, height = w;
        var rgb = new byte[width * height * 3];
        for (int k = 0; k < rgb.Length; k++) rgb[k] = 255;
        foreach (var (pos, key) in keys)
        {
            int ox = (pos.Item1 + r) * Px, oy = (r - pos.Item2) * Px;
            Draw(before[pos], ox, oy);
            Draw(after[pos], w + gap + ox, oy);
        }
        Png.WriteRgb(path, rgb, width, height);

        void Draw(CubeLeafContent c, int ox, int oy)
        {
            var t = LeafFrames.Swiss.TransformOf(c.Key);
            int n = t.Cells + 1, step = t.Cells / Px;
            var h = c.Heights?.Heights;
            for (int y = 0; y < Px; y++)
                for (int x = 0; x < Px; x++)
                {
                    int i = x * step, j = y * step, k = j * n + i;
                    var (cr, cg, cb) = Colour(c.Cover is { } cv && k < cv.Length ? (CoverClass)cv[k] : CoverClass.Open);
                    double shade = 1;
                    if (h != null)
                    {
                        float hw = h[j * n + Math.Max(i - step, 0)], he = h[j * n + Math.Min(i + step, n - 1)];
                        float hs = h[Math.Max(j - step, 0) * n + i], hn = h[Math.Min(j + step, n - 1) * n + i];
                        if (!float.IsNaN(hw + he + hs + hn)) shade = Math.Clamp(1 + ((hw - he) + (hn - hs)) * 0.04, 0.55, 1.35);
                    }
                    if (c.Water is { } wt)
                    {
                        int wi = Math.Min(i / CubeWater.Stride, wt.Side - 1), wj = Math.Min(j / CubeWater.Stride, wt.Side - 1);
                        float lv = wt.Level[wj * wt.Side + wi];
                        if (!float.IsNaN(lv) && h != null && lv > h[k]) (cr, cg, cb, shade) = (0.20, 0.32, 0.42, 1);
                    }
                    Put(ox + x, oy + (Px - 1 - y), cr * shade, cg * shade, cb * shade);
                }
            foreach (var tree in c.Trees ?? [])
            {
                var (fi, fj, _) = t.LocalToLattice(tree.X, tree.Y, tree.Z);
                (double, double, double) col = tree.Kind == 1 ? (0.36, 0.40, 0.20) : (0.08, 0.20, 0.08);
                Dot(ox + (int)(fi / step), oy + Px - 1 - (int)(fj / step), col, tree.Kind == 1 ? 0 : 1);
            }
            foreach (var b in c.Buildings?.Buildings ?? [])
            {
                var (mx, mz) = SourceTileToLeaf.Middle(b);
                var (fi, fj, _) = t.LocalToLattice(mx, b.MinY, mz);
                Dot(ox + (int)(fi / step), oy + Px - 1 - (int)(fj / step), (0.75, 0.15, 0.12), 1);
            }
        }

        void Dot(int x, int y, (double R, double G, double B) c, int radius)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++) Put(x + dx, y + dy, c.R, c.G, c.B);
        }

        void Put(int x, int y, double cr, double cg, double cb)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int o = (y * width + x) * 3;
            rgb[o] = (byte)Math.Clamp(cr * 255, 0, 255);
            rgb[o + 1] = (byte)Math.Clamp(cg * 255, 0, 255);
            rgb[o + 2] = (byte)Math.Clamp(cb * 255, 0, 255);
        }
    }

    /// <summary>The ground colours of <c>CoverPalette</c> (sRGB) for the classes a fill leaf holds; BlendCheck has no Godot.</summary>
    private static (double, double, double) Colour(CoverClass c) => c switch
    {
        CoverClass.Forest => (0.16, 0.29, 0.15),
        CoverClass.OpenForest => (0.22, 0.35, 0.18),
        CoverClass.Woodland => (0.24, 0.36, 0.19),
        CoverClass.Shrub => (0.31, 0.38, 0.22),
        CoverClass.Rock or CoverClass.LooseRock or CoverClass.Boulders => (0.46, 0.43, 0.40),
        CoverClass.Scree or CoverClass.LooseScree => (0.55, 0.52, 0.47),
        CoverClass.Water => (0.20, 0.32, 0.42),
        CoverClass.Wetland => (0.34, 0.40, 0.28),
        CoverClass.Glacier => (0.86, 0.90, 0.94),
        CoverClass.Snowfield => (0.80, 0.84, 0.88),
        CoverClass.Sand => (0.78, 0.66, 0.46),
        CoverClass.DryGrass => (0.62, 0.57, 0.36),
        CoverClass.Tundra => (0.47, 0.45, 0.33),
        CoverClass.LushGround => (0.24, 0.44, 0.18),
        CoverClass.Vineyard or CoverClass.Orchard or CoverClass.Nursery or CoverClass.Allotment or CoverClass.Clearcut => (0.40, 0.46, 0.25),
        CoverClass.Open => (0.33, 0.47, 0.22),
        _ => (0.42, 0.42, 0.40),
    };

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
