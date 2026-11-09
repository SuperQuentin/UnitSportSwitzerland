using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using static System.Console;

/// <summary>
/// The fill blended into real leaves (#1510, G2 C1 PR 3, <see cref="LeafBlend"/>), on a region's built leaves at its
/// border: blocks of 3 × 3 leaves round border leaves spread along it, each leaf as the earth world gets it
/// (<see cref="EarthLeaves"/>: the region's whole, merged where the region holds part, the fill's elsewhere). Checks:
/// <list type="bullet">
/// <item>the real cells keep the region's heights to the bit;</item>
/// <item>no step at the seam: a fill vertex beside the real ground against the real surface carried one step out
/// (its second difference), no rougher than the real ground's own second differences (p99 within twice the real's
/// plus 5 cm);</item>
/// <item>shared leaf edges agree (within 1 cm, the frames' bar);</item>
/// <item>a part-real leaf has ground everywhere the generator does (no NaN left);</item>
/// <item>the blend is convex: the fill's ground lies between the generator's and the real low-pass's range round it.</item>
/// </list>
/// A shaded relief of each block (the fill tinted) into <c>--out</c> as PPM.
///
///   dotnet run --project tools/BlendCheck -c Release -- --leaf-blend --leaves &lt;dir&gt; [--count 6] [--out &lt;dir&gt;]
///     [--at E,N --side 5]   (one block of side × side leaves round an LV95 point instead)
/// </summary>
internal static class LeafBlendCheck
{
    public static int Run(string[] args)
    {
        const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N
        string dir = Value(args, "--leaves") ?? "D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube";
        string? outDir = Value(args, "--out");
        int count = int.TryParse(Value(args, "--count"), out int c) ? c : 6;
        var store = LeafStore.Open(dir);
        if (store.Count == 0)
        {
            WriteLine($"no leaves in {dir}: skipped");
            return 0;
        }
        var world = new ProceduralWorld(AnchorE, AnchorN);
        var (lat, lon) = Lv95Source.Instance.ToLatLon(AnchorE, AnchorN);
        var leaves = EarthLeaves.WithFill(store, world, lat, lon, roads: false);
        leaves.Log = WriteLine;
        var blend = leaves.Blend!;
        var frames = LeafFrames.Swiss;
        int fails = 0;
        void Check(string what, bool ok, string detail = "")
        {
            WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
            if (!ok) fails++;
        }

        // border leaves (a neighbour the region does not hold), spread along the sorted list
        var border = store.Keys.Where(k => k.Neighbours().Any(n => !store.Has(n)))
            .OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();
        var centres = Enumerable.Range(0, count).Select(i => border[(int)((i + 0.5) * border.Count / count)]).ToList();
        int half = 1;
        if (Value(args, "--at") is { } at)
        {
            var en = at.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var (alat, alon) = Lv95Source.Instance.ToLatLon(en[0], en[1]);
            centres = [LeafFrames.LeafAt(alat, alon)];
            half = (int.TryParse(Value(args, "--side"), out int sd) ? sd : 3) / 2;
        }
        WriteLine($"{store.Count} real leaves, {border.Count} at the border; blocks round {string.Join(", ", centres)}");

        var seam = new List<double>();
        var rough = new List<double>();
        long keptWrong = 0, keptAll = 0, nanLeft = 0, edgePairs = 0, edgeBad = 0, convexBad = 0;
        double edgeWorst = 0;
        var times = new List<double>();
        foreach (var centre in centres)
        {
            var block = new Dictionary<CubeKey, CubeLeafContent>();
            var kept = new Dictionary<CubeKey, bool[]>();
            for (int dy = -half; dy <= half; dy++)
                for (int dx = -half; dx <= half; dx++)
                {
                    if (centre.Neighbour(dx, dy) is not { } k) continue;
                    var sw = Stopwatch.StartNew();
                    if (leaves.Get(k) is not { Heights: not null } leaf) continue;
                    if (!store.Has(k) || blend.Summary(k) is not { Full: true }) times.Add(sw.Elapsed.TotalSeconds);
                    block[k] = leaf;
                    int n = leaf.Heights.Side;
                    var mask = new bool[n * n];
                    if (blend.Summary(k) is { } real)
                    {
                        var rh = store.HeightsOf(k)!.Heights;
                        for (int j = 0; j < n; j++)
                            for (int i = 0; i < n; i++)
                            {
                                if (!real.Kept(i, j) || float.IsNaN(rh[j * n + i])) continue;
                                mask[j * n + i] = true;
                                keptAll++;
                                if (BitConverter.SingleToInt32Bits(rh[j * n + i]) != BitConverter.SingleToInt32Bits(leaf.Heights.Heights[j * n + i])) keptWrong++;
                            }
                    }
                    kept[k] = mask;
                    var g = world.Height;
                    var on = new SourceOnLeaf(frames, k, Lv95Source.Instance);
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                        {
                            float h = leaf.Heights.Heights[j * n + i];
                            if (!float.IsNaN(h) || mask[j * n + i]) continue;
                            var (e, nn) = on.SourceAt(i, j);
                            if (!double.IsNaN(g(e, nn))) nanLeft++;
                        }
                }

            // second differences: across the seam (a fill vertex, then two real ones inward) and inside the real ground
            foreach (var (k, leaf) in block)
            {
                var h = leaf.Heights!.Heights;
                var m = kept[k];
                int n = leaf.Heights.Side;
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                        foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int i1 = i + di, j1 = j + dj, i2 = i + 2 * di, j2 = j + 2 * dj;
                            if ((uint)i2 >= n || (uint)j2 >= n) continue;
                            int a = j * n + i, b = j1 * n + i1, cc = j2 * n + i2;
                            if (!m[b] || !m[cc]) continue;
                            double second = Math.Abs(h[a] - (2.0 * h[b] - h[cc]));
                            if (double.IsNaN(second)) continue;
                            if (!m[a]) seam.Add(second);
                            else if ((i + j) % 7 == 0) rough.Add(second);
                        }
            }

            // shared edges between the block's leaves
            foreach (var (k, leaf) in block)
            {
                int n = leaf.Heights!.Side, last = n - 1;
                if (k.Neighbour(1, 0) is { } east && block.TryGetValue(east, out var e))
                    for (int j = 0; j < n; j++) Edge(leaf.Heights.Heights[j * n + last], e.Heights!.Heights[j * n]);
                if (k.Neighbour(0, 1) is { } north && block.TryGetValue(north, out var no))
                    for (int i = 0; i < n; i++) Edge(leaf.Heights.Heights[last * n + i], no.Heights!.Heights[i]);
            }

            // convex: every fill vertex between the generator's ground and the real ground's range within the band
            double realMin = double.MaxValue, realMax = double.MinValue;
            foreach (var (k, leaf) in block)
            {
                var m = kept[k];
                var h = leaf.Heights!.Heights;
                for (int q = 0; q < h.Length; q++)
                    if (m[q]) { realMin = Math.Min(realMin, h[q]); realMax = Math.Max(realMax, h[q]); }
            }
            if (realMin <= realMax)
                foreach (var (k, leaf) in block)
                {
                    if (store.Has(k) && blend.Summary(k) is { Full: true }) continue;
                    var gen = new GeneratedLeaves(world).Build(k).Heights!.Heights;
                    var h = leaf.Heights!.Heights;
                    var m = kept[k];
                    for (int q = 0; q < h.Length; q += 3)
                    {
                        if (m[q] || float.IsNaN(h[q])) continue;
                        // the band's real range reaches past this block: a generous margin, the detail term's
                        double lo = Math.Min(gen[q], realMin) - 150, hi = Math.Max(gen[q], realMax) + 150;
                        if (h[q] < lo || h[q] > hi) convexBad++;
                    }
                }

            if (outDir != null) Relief(outDir, centre, block, kept, half);
        }

        void Edge(float a, float b)
        {
            if (float.IsNaN(a) || float.IsNaN(b)) return;
            edgePairs++;
            double d = Math.Abs(a - b);
            edgeWorst = Math.Max(edgeWorst, d);
            if (d > 0.01) edgeBad++;
        }

        seam.Sort();
        rough.Sort();
        double P(List<double> v, double p) => v.Count == 0 ? double.NaN : v[Math.Min(v.Count - 1, (int)(p * v.Count))];
        WriteLine($"  produced leaves: {times.Count}, median {P(times.OrderBy(x => x).ToList(), 0.5):F2} s, worst {(times.Count > 0 ? times.Max() : 0):F2} s (one thread)");
        Check("real cells keep the region's heights", keptWrong == 0, $"{keptWrong} of {keptAll} vertices differ");
        Check("no NaN where the generator has ground", nanLeft == 0, $"{nanLeft} vertices");
        WriteLine($"  second differences: seam {seam.Count} p50 {P(seam, 0.5) * 100:F1} cm p99 {P(seam, 0.99) * 100:F1} cm max {(seam.Count > 0 ? seam[^1] : 0) * 100:F1} cm; "
            + $"real {rough.Count} p50 {P(rough, 0.5) * 100:F1} cm p99 {P(rough, 0.99) * 100:F1} cm max {(rough.Count > 0 ? rough[^1] : 0) * 100:F1} cm");
        Check("no step at the seam", seam.Count > 0 && P(seam, 0.99) <= 2 * P(rough, 0.99) + 0.05,
            $"seam p99 {P(seam, 0.99) * 100:F1} cm, bar {(2 * P(rough, 0.99) + 0.05) * 100:F1} cm");
        Check("shared leaf edges agree within 1 cm", edgeBad == 0, $"{edgePairs} pairs, worst {edgeWorst * 100:F2} cm");
        Check("the fill lies between the generator's and the real ground", convexBad == 0, $"{convexBad} vertices outside");
        WriteLine(fails == 0 ? "RESULT: ok" : $"RESULT: FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A block's shaded relief, every second vertex, the fill tinted warm, as a binary PPM.</summary>
    private static void Relief(string dir, CubeKey centre, Dictionary<CubeKey, CubeLeafContent> block, Dictionary<CubeKey, bool[]> kept, int half)
    {
        Directory.CreateDirectory(dir);
        int Skip = 2 * half;
        int n = block.Values.First().Heights!.Side, px = (n - 1) / Skip, size = (2 * half + 1) * px;
        var rgb = new byte[size * size * 3];
        double Step = 1.19 * Skip;
        foreach (var (k, leaf) in block)
        {
            int ox = (k.X - centre.X + half) * px, oy = (half - (k.Y - centre.Y)) * px;   // north up
            var h = leaf.Heights!.Heights;
            var m = kept[k];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    int i = x * Skip, j = (px - 1 - y) * Skip + Skip;
                    j = Math.Min(j, n - 1);
                    int i1 = Math.Min(i + Skip, n - 1), j0 = Math.Max(j - Skip, 0);
                    double hz = h[j * n + i], dx = (h[j * n + i1] - hz) / Step, dy = (hz - h[j0 * n + i]) / Step;
                    // light from the north-west, 45° up
                    double nx = -dx, ny = -dy, nz = 1, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    double shade = double.IsNaN(hz) ? 0 : Math.Clamp((nx * -0.5 + ny * 0.5 + nz * 0.707) / len, 0, 1);
                    bool real = m[j * n + i];
                    int o = ((oy + y) * size + ox + x) * 3;
                    rgb[o] = (byte)(255 * shade * (real ? 0.85 : 1.0));
                    rgb[o + 1] = (byte)(255 * shade * (real ? 0.9 : 0.85));
                    rgb[o + 2] = (byte)(255 * shade * (real ? 1.0 : 0.7));
                }
        }
        using var fs = File.Create(Path.Combine(dir, $"blend_{centre.FileStem}.ppm"));
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{size} {size}\n255\n");
        fs.Write(head);
        fs.Write(rgb);
    }

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
