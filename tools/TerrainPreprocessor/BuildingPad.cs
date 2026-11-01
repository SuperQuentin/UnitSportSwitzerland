using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Building pads (#1419): the ground a solid stands on, graded into the tile heights. Plain C# so a unit
/// test and the grading stage (<see cref="GradeStage"/>) share it.
///
/// <para>
/// <b>Level.</b> <see cref="ChooseGround"/> picks the ground level of a footprint from heights sampled
/// along its outline: the one that costs least cut and fill within <see cref="Cap"/>, a gap under the
/// base (fill, then a visible plinth) counting double past the cap and a buried wall once.
/// The solid's base is that level minus <see cref="FoundationDepth"/>.
/// </para>
///
/// <para>
/// <b>Pad.</b> Inside the outline the heights go to the level, each cell by at most <see cref="Cap"/>
/// (the rest is the footings and plinths of #1417). Beyond it the same change fades out over
/// <see cref="Apron"/> metres with a smoothstep, so the pad never leaves a step in the ground.
/// Where pads overlap the cell takes the average of their changes weighted by how deep inside (or how
/// near) each is, so a footprint dominates its own ground but two neighbours never meet in a step.
/// </para>
///
/// <para>
/// A cell's result is a function of the original height and the footprints around it, in a fixed order, so
/// the shared border vertices of two tiles come out bit-identical in both.
/// </para>
/// </summary>
public static class BuildingPad
{
    /// <summary>Depth of the building base below the ground level (the source solids include a foundation block).</summary>
    public const double FoundationDepth = 0.8;

    /// <summary>Metres: the most a pad moves one cell; the rest is left to the footings and plinths.</summary>
    public const double Cap = 1.5;

    /// <summary>Metres: how far past the outline the change fades out.</summary>
    public const double Apron = 3.0;

    /// <summary>
    /// Metres: the pad stays at full strength this far past the outline before it fades, so the ground a door
    /// is read on (0.8 m in front, BuildingFootprint.FrontReach) is the level the sill was planned on, and a
    /// coarser ring's lattice cell that straddles the outline still sees it flat.
    /// </summary>
    public const double Shoulder = 3.0;

    /// <summary>Metres: how far past the outline a pad changes anything.</summary>
    public const double Reach = Shoulder + Apron;

    /// <summary>Metres between ground samples along an outline.</summary>
    public const double SampleStepM = 1.5;

    /// <summary>Metres: inside the outline the weight of a pad grows over this depth, so a footprint wins its own ground.</summary>
    private const double InsideRamp = 1.0;

    /// <summary>
    /// The cost of a ground level against one sample: the cut or fill the pad does (capped), and what it
    /// leaves to the footings, a fill (ground under the base) counting double.
    /// </summary>
    private static double Cost(double d) =>
        d > 0 ? Math.Min(d, Cap) + 2 * Math.Max(0, d - Cap) : Math.Min(-d, Cap) + Math.Max(0, -d - Cap);

    /// <summary>The ground level of a footprint from its outline samples: the candidate of least cost, the median among equals.</summary>
    public static double ChooseGround(IReadOnlyList<double> samples)
    {
        var sorted = samples.OrderBy(h => h).ToArray();
        double median = sorted[sorted.Length / 2];
        double best = median, bestCost = double.MaxValue;
        // every sample is a candidate, thinned to 64 so a big building stays cheap
        int step = Math.Max(1, sorted.Length / 64);
        for (int i = 0; i < sorted.Length; i += step)
        {
            double g = sorted[i], cost = 0;
            foreach (double h in sorted) cost += Cost(g - h);
            if (cost < bestCost - 1e-9 || (Math.Abs(cost - bestCost) <= 1e-9 && Math.Abs(g - median) < Math.Abs(best - median)))
            { best = g; bestCost = cost; }
        }
        return best;
    }

    /// <summary>One solid's outline in LV95 and the level its pad is graded to.</summary>
    public sealed class Footprint
    {
        /// <summary>E0, N0, E1, N1 per edge.</summary>
        public required double[] Edges { get; init; }
        public required double Level { get; init; }
        public double MinE { get; init; }
        public double MinN { get; init; }
        public double MaxE { get; init; }
        public double MaxN { get; init; }
        /// <summary>Whether the edges close into loops, so an even-odd test says what is inside; else the plan triangles do.</summary>
        public bool Closed { get; init; }
        public double[]? Plan { get; init; }

        public bool Reaches(double minE, double minN, double maxE, double maxN) =>
            MaxE + Reach >= minE && MinE - Reach <= maxE && MaxN + Reach >= minN && MinN - Reach <= maxN;

        /// <summary>Signed distance to the outline: positive outside, negative inside (how deep).</summary>
        public double SignedDistance(double e, double n)
        {
            double best = double.MaxValue;
            bool odd = false;
            var ed = Edges;
            for (int i = 0; i < ed.Length; i += 4)
            {
                double e0 = ed[i], n0 = ed[i + 1], e1 = ed[i + 2], n1 = ed[i + 3];
                double dx = e1 - e0, dn = n1 - n0, len2 = dx * dx + dn * dn;
                double t = len2 < 1e-12 ? 0 : Math.Clamp(((e - e0) * dx + (n - n0) * dn) / len2, 0, 1);
                double fe = e0 + dx * t - e, fn = n0 + dn * t - n;
                double d2 = fe * fe + fn * fn;
                if (d2 < best) best = d2;
                if ((n0 > n) != (n1 > n) && e < e0 + (n - n0) / (n1 - n0) * dx) odd = !odd;
            }
            double dist = Math.Sqrt(best);
            bool inside = Closed ? odd : InPlan(e, n);
            return inside ? -dist : dist;
        }

        private bool InPlan(double e, double n)
        {
            var t = Plan;
            if (t == null) return false;
            for (int i = 0; i + 5 < t.Length; i += 6)
            {
                double ae = t[i], an = t[i + 1], be = t[i + 2], bn = t[i + 3], ce = t[i + 4], cn = t[i + 5];
                double area = (be - ae) * (cn - an) - (bn - an) * (ce - ae);
                if (Math.Abs(area) < 0.01) continue;
                double d0 = (be - ae) * (n - an) - (bn - an) * (e - ae);
                double d1 = (ce - be) * (n - bn) - (cn - bn) * (e - be);
                double d2 = (ae - ce) * (n - cn) - (an - cn) * (e - ce);
                if (area > 0 ? d0 >= 0 && d1 >= 0 && d2 >= 0 : d0 <= 0 && d1 <= 0 && d2 <= 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A solid's footprint from its triangles (tile-local X east, Y up, Z south): the bottom edges of its
    /// wall triangles that stand on the base, level <c>MinY + FoundationDepth</c>. Null when it has none.
    /// </summary>
    public static Footprint? FootprintOf(Building b, TileId tile)
    {
        var t = b.Triangles;
        var edges = new Dictionary<(int, int, int, int), (double E0, double N0, double E1, double N1)>();
        var degree = new Dictionary<(int, int), int>();
        var plan = new List<double>();
        static (int, int) Key(double e, double n) => ((int)Math.Round(e * 20), (int)Math.Round(n * 20));
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            double ex = t[i + 3] - t[i], ey = t[i + 4] - t[i + 1], ez = t[i + 5] - t[i + 2];
            double fx = t[i + 6] - t[i], fy = t[i + 7] - t[i + 1], fz = t[i + 8] - t[i + 2];
            double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) continue;
            if (Math.Abs(ny) / len >= 0.5)
            {
                // a roof or floor: its plan, for the fallback inside test
                for (int k = 0; k < 3; k++)
                {
                    plan.Add(tile.MinE + t[i + k * 3]);
                    plan.Add(tile.MaxN - t[i + k * 3 + 2]);
                }
                continue;
            }
            for (int k = 0; k < 3; k++)
            {
                int p = i + k * 3, q = i + ((k + 1) % 3) * 3;
                if (t[p + 1] > b.MinY + 0.05f || t[q + 1] > b.MinY + 0.05f) continue;
                double e0 = tile.MinE + t[p], n0 = tile.MaxN - t[p + 2], e1 = tile.MinE + t[q], n1 = tile.MaxN - t[q + 2];
                if ((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0) < 0.05 * 0.05) continue;
                var ka = Key(e0, n0); var kb = Key(e1, n1);
                var key = ka.CompareTo(kb) <= 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2) : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
                if (edges.TryAdd(key, (e0, n0, e1, n1)))
                {
                    degree[ka] = degree.GetValueOrDefault(ka) + 1;
                    degree[kb] = degree.GetValueOrDefault(kb) + 1;
                }
            }
        }
        if (edges.Count < 3) return null;
        var flat = new double[edges.Count * 4];
        int w = 0;
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        foreach (var (_, s) in edges.OrderBy(kv => kv.Key))
        {
            flat[w++] = s.E0; flat[w++] = s.N0; flat[w++] = s.E1; flat[w++] = s.N1;
            minE = Math.Min(minE, Math.Min(s.E0, s.E1)); maxE = Math.Max(maxE, Math.Max(s.E0, s.E1));
            minN = Math.Min(minN, Math.Min(s.N0, s.N1)); maxN = Math.Max(maxN, Math.Max(s.N0, s.N1));
        }
        return new Footprint
        {
            Edges = flat,
            Level = b.MinY + FoundationDepth,
            MinE = minE, MinN = minN, MaxE = maxE, MaxN = maxN,
            Closed = degree.Values.All(d => d == 2),
            Plan = plan.Count >= 6 ? plan.ToArray() : null,
        };
    }

    /// <summary>The weight of a pad <paramref name="distance"/> outside its outline: 1 to <see cref="Shoulder"/>, then a smoothstep to 0 over <see cref="Apron"/>.</summary>
    public static double Fade(double distance)
    {
        double u = 1 - (distance - Shoulder) / Apron;
        if (u <= 0) return 0;
        if (u >= 1) return 1;
        return u * u * (3 - 2 * u);
    }

    /// <summary>
    /// Grades the heights of <paramref name="tile"/> (<c>GridSize</c> squared, row 0 north) in place under
    /// <paramref name="footprints"/>, given in a fixed order. Returns the number of cells changed.
    /// </summary>
    public static int Grade(TileId tile, ushort[] heights, IReadOnlyList<Footprint> footprints)
    {
        int n = ChunkFormat.GridSize;
        double sp = ChunkFormat.SpacingM;
        double tMinE = tile.MinE, tMaxN = tile.MaxN, tMaxE = tile.MinE + (n - 1) * sp, tMinN = tile.MaxN - (n - 1) * sp;
        double[]? sumA = null, sumAD = null, wMax = null;
        foreach (var f in footprints)
        {
            if (!f.Reaches(tMinE, tMinN, tMaxE, tMaxN)) continue;
            sumA ??= new double[n * n];
            sumAD ??= new double[n * n];
            wMax ??= new double[n * n];
            int c0 = Math.Max(0, (int)Math.Floor((f.MinE - Reach - tMinE) / sp)), c1 = Math.Min(n - 1, (int)Math.Ceiling((f.MaxE + Reach - tMinE) / sp));
            int r0 = Math.Max(0, (int)Math.Floor((tMaxN - f.MaxN - Reach) / sp)), r1 = Math.Min(n - 1, (int)Math.Ceiling((tMaxN - f.MinN + Reach) / sp));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double e = tMinE + c * sp, nn = tMaxN - r * sp;
                    double s = f.SignedDistance(e, nn);
                    double w, a;
                    if (s <= 0) { w = 1; a = 1 + 6 * Math.Min(-s, InsideRamp) / InsideRamp; }
                    else if (s < Reach) { w = a = Fade(s); }
                    else continue;
                    int k = r * n + c;
                    double h = ChunkFormat.Dequantize(heights[k]);
                    double d = Math.Clamp(f.Level - h, -Cap, Cap);
                    sumA[k] += a;
                    sumAD[k] += a * d;
                    if (w > wMax![k]) wMax[k] = w;
                }
        }
        if (sumA == null) return 0;
        int changed = 0;
        for (int k = 0; k < sumA.Length; k++)
        {
            if (sumA[k] <= 0) continue;
            double delta = wMax![k] * sumAD![k] / sumA[k];
            ushort q = ChunkFormat.Quantize(ChunkFormat.Dequantize(heights[k]) + delta);
            if (q == heights[k]) continue;
            heights[k] = q;
            changed++;
        }
        return changed;
    }
}
