using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--dem-check</c> (#936, #965): compares a neighbour's DEM with the swissALTI3D tiles already built
/// where both exist (swissALTI3D reaches a few km past the border). Prints the median height offset —
/// the value to give that DEM as its dz (IGN69, DHHN2016, GHA, Genova 1942 vs LN02) — and the residual
/// for horizontal shifts of up to 2 m, which should be smallest at zero if the projection is right.
/// Reads only, writes nothing.
/// </summary>
public static class ForeignDemCheck
{
    private const int Step = 10;     // one vertex in ten each way
    private const double MaxShift = 2, ShiftStep = 0.5;

    public static int Run(ForeignDem dem, string outDir, int minE, int minN, int maxE, int maxN, int jobs)
    {
        string mPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(mPath)) { Console.Error.WriteLine($"no manifest in {outDir}"); return 1; }
        var built = TerrainManifest.FromJson(File.ReadAllText(mPath)).Tiles.Select(t => t.Id).ToHashSet();
        var overlap = dem.TilesIn(minE, minN, maxE, maxN, new HashSet<TileId>()).Where(built.Contains).ToList();
        Console.WriteLine($"DEM check {dem.Name}: {overlap.Count} Swiss tiles with its data under them");
        if (overlap.Count == 0) return 1;

        int shifts = (int)(2 * MaxShift / ShiftStep) + 1;
        var sum = new double[shifts * shifts];
        var sumSq = new double[shifts * shifts];
        var count = new long[shifts * shifts];
        var zeroDiffs = new ConcurrentBag<float[]>();
        var perTile = new ConcurrentBag<(TileId Id, int N, double Median, double P10, double P90)>();
        var gate = new object();

        Parallel.ForEach(overlap, new ParallelOptions { MaxDegreeOfParallelism = jobs }, id =>
        {
            ChunkGrid g;
            using (var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)))) g = ChunkCodec.Decode(fs);
            var s = new double[sum.Length];
            var q = new double[sum.Length];
            var c = new long[sum.Length];
            var mine = new List<float>();
            var cur = new ForeignDem.Cursor();
            for (int j = Step; j < 1000; j += Step)
                for (int i = Step; i < 1000; i += Step)
                {
                    double e = id.MinE + i, n = id.MaxN - j;
                    double swiss = g.SampleHeight(e, n);
                    var (x, y) = DemProjection.FromLv95(dem.Crs, e, n);
                    for (int sy = 0; sy < shifts; sy++)
                        for (int sx = 0; sx < shifts; sx++)
                        {
                            double dx = -MaxShift + sx * ShiftStep, dy = -MaxShift + sy * ShiftStep;
                            double fr = dem.Sample(x + dx, y + dy, ref cur);
                            if (double.IsNaN(fr)) continue;
                            double d = swiss - fr;
                            if (Math.Abs(d) > 20) continue; // lake surface vs bed, a data hole: not a datum
                            int k = sy * shifts + sx;
                            s[k] += d; q[k] += d * d; c[k]++;
                            if (dx == 0 && dy == 0) mine.Add((float)d);
                        }
                }
            zeroDiffs.Add(mine.ToArray());
            if (mine.Count > 0)
            {
                mine.Sort();
                perTile.Add((id, mine.Count, mine[mine.Count / 2], mine[mine.Count / 10], mine[mine.Count * 9 / 10]));
            }
            lock (gate)
                for (int k = 0; k < sum.Length; k++) { sum[k] += s[k]; sumSq[k] += q[k]; count[k] += c[k]; }
        });

        var all = zeroDiffs.SelectMany(a => a).OrderBy(v => v).ToArray();
        if (all.Length == 0) { Console.Error.WriteLine("no overlapping samples"); return 1; }
        double median = all[all.Length / 2];
        double p10 = all[all.Length / 10], p90 = all[all.Length * 9 / 10];
        Console.WriteLine($"  {all.Length} samples: swiss - theirs median {median:+0.000;-0.000} m (p10 {p10:+0.00;-0.00}, p90 {p90:+0.00;-0.00})");

        // per tile, for where the two models disagree (written next to the tiles' temp dir)
        string report = Path.Combine(outDir.TrimEnd('/', '\\') + "_temp", $"dem_check_{dem.Crs}.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllLines(report, new[] { "E\tN\tsamples\tmedian\tp10\tp90" }.Concat(perTile.OrderBy(t => t.Id.N).ThenBy(t => t.Id.E)
            .Select(t => FormattableString.Invariant($"{t.Id.E}\t{t.Id.N}\t{t.N}\t{t.Median:F2}\t{t.P10:F2}\t{t.P90:F2}"))));
        Console.WriteLine($"  per tile: {report}");

        Console.WriteLine("  residual std (m) by their shift dx (columns) / dy (rows):");
        int best = 0;
        for (int sy = 0; sy < shifts; sy++)
        {
            var line = new System.Text.StringBuilder($"  dy {-MaxShift + sy * ShiftStep,5:+0.0;-0.0}");
            for (int sx = 0; sx < shifts; sx++)
            {
                int k = sy * shifts + sx;
                double mean = sum[k] / Math.Max(1, count[k]);
                double std = Math.Sqrt(Math.Max(0, sumSq[k] / Math.Max(1, count[k]) - mean * mean));
                double bestStd = Math.Sqrt(Math.Max(0, sumSq[best] / Math.Max(1, count[best]) - Math.Pow(sum[best] / Math.Max(1, count[best]), 2)));
                if (std < bestStd) best = k;
                line.Append($" {std,6:F3}");
            }
            Console.WriteLine(line.ToString());
        }
        Console.WriteLine($"  best shift dx {-MaxShift + best % shifts * ShiftStep:+0.0;-0.0}, dy {-MaxShift + best / shifts * ShiftStep:+0.0;-0.0} m"
            + $" -> dz {median.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        return 0;
    }
}
