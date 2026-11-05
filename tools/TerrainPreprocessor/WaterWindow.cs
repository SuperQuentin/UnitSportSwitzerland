namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The water pass's maths over one window (#298; one copy since #1507, G2 B3 PR 6): a square of cells round the
/// vertices a tile or a leaf writes, each cell dry (0), wet (1) or unknown (2), with its still surface in integer
/// quanta and its body's global label. The LV95 build's windows are its 1 m vertices (<see cref="WaterStage"/>, cell
/// 1 m: these are its numbers bit for bit); a work frame's are a face's leaf lattice (<see cref="CubeWaterStage"/>,
/// ~1.2 m). Every constant is in metres, and a count of cells is taken from the window's cell size.
/// </summary>
public static class WaterWindow
{
    /// <summary>
    /// Window-sized scratch arrays handed back and forth between tiles (#1557). A tile's 2201² window takes about thirty
    /// arrays of 5-39 MB, all on the large object heap; a fresh set per tile left the GC holding many times the live
    /// set in freed-but-committed regions (23 GB of the 24 GB peak on the multi-batch region, 16 GB heap limit out of
    /// memory). One pool lives for one stage phase and is dropped with it, so a build run inside the game keeps nothing. A rented array is zeroed, so it is exactly what <c>new T[n]</c> gives. A null pool allocates, as before.
    /// </summary>
    public sealed class Pool
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, int), System.Collections.Concurrent.ConcurrentBag<Array>> _free = new();

        public T[] Rent<T>(int n)
        {
            if (_free.TryGetValue((typeof(T), n), out var bag) && bag.TryTake(out var a))
            {
                Array.Clear(a);
                return (T[])a;
            }
            return new T[n];
        }

        public void Return<T>(T[]? a)
        {
            if (a != null) _free.GetOrAdd((typeof(T), a.Length), _ => new()).Add(a);
        }
    }

    private static T[] Rent<T>(Pool? pool, int n) => pool != null ? pool.Rent<T>(n) : new T[n];

    /// <summary>
    /// Metres of neighbouring tiles each tile is computed with. Everything a core vertex reads must
    /// be exact inside it: the gap fill reads the smoothed edge depth up to 2·<see cref="EdgeSmoothM"/>
    /// out, whose nearest survey vertex may be <see cref="FillReachM"/> + 2·EdgeSmoothM further
    /// (482 m), so 600; the distance to the shore saturates at <see cref="DistanceCapM"/>.
    /// </summary>
    public const int Halo = 600;

    /// <summary>
    /// Distances to the shore saturate here (deep enough for every body's maximum but the biggest
    /// lakes', which are surveyed), well short of the halo: a window's outermost
    /// <see cref="BankProbeM"/> cells are refined from a truncated neighbourhood, so a shore site
    /// there must never decide a core vertex's distance.
    /// </summary>
    public const int DistanceCapM = 490;

    /// <summary>
    /// A Water cover vertex whose surface stands more than the rise (0.5 m: 7 LV95 quanta) above the lowest wet
    /// surface within <see cref="BankProbeM"/> is on the bank, not in the water: the TLM polygon's edge lies on the
    /// dyke or beach slope (the Rhône at Martigny has 468.2 m on its bank vertex over 466.8 m water), and taking its
    /// height as the level drew the surface climbing the bank. It stays dry ground.
    /// </summary>
    public const int BankProbeM = 4;

    /// <summary>How far from the survey a gap is still filled toward the survey's edge depth.</summary>
    public const double FillReachM = 400;

    /// <summary>Stop climbing toward the middle of the water past this distance: open water.</summary>
    public const double WidthProbeM = UnitSport.Terrain.Format.WaterBed.OpenWidthM / 2 + 10;

    /// <summary>
    /// The survey only counts in a body this large, at vertices within
    /// <see cref="SurveyLevelToleranceM"/> of the body's most common level: swissBATHY3D's grid
    /// reaches up the streams around Nyon, 2-10 m above the lake, where it would dig a 3 m pit in a
    /// brook. Not the mean: nationwide a lake's body takes in its rivers (Léman the Rhône up the
    /// Valais), which lifted Léman's mean 4 m off its 372.14 m surface and dropped the whole survey.
    /// </summary>
    public const double MinSurveyAreaM2 = 200_000, SurveyLevelToleranceM = 1.0;

    /// <summary>Max depth per metre from the shore (1:1): a quay in the survey becomes a 45° bank.</summary>
    public const double MaxBankSlope = 1.0;

    /// <summary>Box radius of the edge-depth smoothing, in metres (two passes, so a tent twice as wide).</summary>
    public const int EdgeSmoothM = 40;

    /// <summary>The survey vertices averaged round the nearest one for the edge depth, in metres either way.</summary>
    private const int EdgeSiteM = 2;

    /// <summary>A length in metres as a whole number of cells (the LV95 window's metres as they are).</summary>
    public static int Cells(double metres, double cellM) => cellM == 1 ? (int)metres : Math.Max(1, (int)Math.Round(metres / cellM));

    /// <summary>The wet cells on a bank (see <see cref="BankProbeM"/>) become dry: <paramref name="riseQ"/> quanta above the lowest wet surface round them.</summary>
    public static void RefineWet(byte[] state, int[] surfaceQ, int w, int probe, int riseQ, Pool? pool = null)
    {
        // separable min of the wet surface over a (2·probe+1)² square, in quanta (exact): along a row
        // in constant time per cell (van Herk: the running min from a block's start and from its end
        // meet in every window), on a row padded with "no water" either side
        int block = 2 * probe + 1, padded = w + 2 * probe;
        var rowMin = Rent<int>(pool, w * w);
        var row = new int[padded];
        var fromStart = new int[padded];
        var fromEnd = new int[padded];
        Array.Fill(row, int.MaxValue);
        for (int y = 0; y < w; y++)
        {
            bool any = false;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (state[i] == 1) { row[x + probe] = surfaceQ[i]; any = true; }
                else row[x + probe] = int.MaxValue;
            }
            var mins = rowMin.AsSpan(y * w, w);
            if (!any) { mins.Fill(int.MaxValue); continue; }
            for (int k = 0; k < padded; k++)
                fromStart[k] = k % block == 0 ? row[k] : Math.Min(fromStart[k - 1], row[k]);
            for (int k = padded - 1; k >= 0; k--)
                fromEnd[k] = k == padded - 1 || (k + 1) % block == 0 ? row[k] : Math.Min(fromEnd[k + 1], row[k]);
            for (int x = 0; x < w; x++)
                mins[x] = Math.Min(fromEnd[x], fromStart[x + 2 * probe]);
        }
        var dry = new List<int>();
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (state[i] != 1) continue;
                int m = int.MaxValue;
                for (int dy = Math.Max(0, y - probe); dy <= Math.Min(w - 1, y + probe); dy++)
                    m = Math.Min(m, rowMin[dy * w + x]);
                if (surfaceQ[i] > (long)m + riseQ) dry.Add(i);
            }
        foreach (int i in dry) state[i] = 0;
        pool?.Return(rowMin);
    }

    /// <summary>The exact Euclidean distance from every cell to the nearest site, in metres (cells × the cell size).</summary>
    public static float[] Distances(ReadOnlySpan<bool> sites, int w, double cellM, int[]? site = null, Pool? pool = null)
    {
        var d = Rent<float>(pool, w * w);
        DistanceTransform.Run(sites, w, w, d, site, pool);
        if (cellM != 1)
        {
            float s = (float)cellM;
            for (int i = 0; i < d.Length; i++) d[i] *= s;
        }
        return d;
    }

    /// <summary>
    /// Half the local width of the water at window cell (x, y): climb the distance-to-shore field to
    /// the ridge running down the middle and read the distance there. Infinity once the climb passes
    /// <see cref="WidthProbeM"/>: open water, where the width no longer matters.
    /// </summary>
    public static double HalfWidth(float[] ds, int w, int x, int y)
    {
        float here = ds[y * w + x];
        while (here < WidthProbeM)
        {
            int bx = x, by = y;
            float best = here;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)w) continue;
                    float v = ds[ny * w + nx];
                    if (v > best) { best = v; bx = nx; by = ny; }
                }
            if (bx == x && by == y) return here;
            x = bx;
            y = by;
            here = best;
        }
        return double.PositiveInfinity;
    }

    /// <summary>
    /// For every gap vertex the fill may reach: the survey's depth at the nearest surveyed vertex,
    /// smoothed. The nearest vertex alone jumps wherever two parts of the survey's edge are equally
    /// near (a Voronoi edge), which drew metre-high steps across the fill; two box passes of
    /// <see cref="EdgeSmoothM"/> over the gap turn those into gentle ramps. Box sums over the window
    /// only read cells within the halo, so the result stays a function of the region's data.
    /// </summary>
    public static float[] SmoothEdgeDepth(float[] survey, byte[] state, float[] dv, int[] site, int w, double cellM, Pool? pool = null)
    {
        var raw = Rent<float>(pool, w * w);
        float reach = (float)(FillReachM + 2 * EdgeSmoothM + 2 * cellM);
        int r2 = Cells(EdgeSiteM, cellM);
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = float.NaN;
            if (state[i] != 1 || !float.IsNaN(survey[i]) || dv[i] > reach || site[i] < 0) continue;
            int x0 = site[i] % w, y0 = site[i] / w;
            double sum = 0;
            int n = 0;
            for (int y = Math.Max(0, y0 - r2); y <= Math.Min(w - 1, y0 + r2); y++)
                for (int x = Math.Max(0, x0 - r2); x <= Math.Min(w - 1, x0 + r2); x++)
                {
                    float v = survey[y * w + x];
                    if (float.IsNaN(v)) continue;
                    sum += v;
                    n++;
                }
            raw[i] = n > 0 ? (float)(sum / n) : float.NaN;
        }
        int box = Cells(EdgeSmoothM, cellM);
        var once = BoxMean(raw, w, box, pool);
        var twice = BoxMean(once, w, box, pool);
        pool?.Return(raw);
        pool?.Return(once);
        return twice;
    }

    /// <summary>
    /// Mean of the defined (non-NaN) cells in a (2r+1)² box, NaN where the cell itself is NaN. The
    /// sums are exact integers (tenths of a millimetre): a floating-point summed-area table rounds
    /// differently from each window's origin, and a shared vertex would then differ by a quantum
    /// between the two tiles that hold it.
    /// </summary>
    public static float[] BoxMean(float[] f, int w, int r, Pool? pool = null)
    {
        const double Unit = 1e-4;
        int sw = w + 1;
        var sum = Rent<long>(pool, sw * sw);
        var cnt = Rent<int>(pool, sw * sw);
        for (int y = 0; y < w; y++)
        {
            long rowSum = 0;
            int rowCnt = 0;
            for (int x = 0; x < w; x++)
            {
                float v = f[y * w + x];
                if (!float.IsNaN(v)) { rowSum += (long)Math.Round(v / Unit); rowCnt++; }
                sum[(y + 1) * sw + x + 1] = sum[y * sw + x + 1] + rowSum;
                cnt[(y + 1) * sw + x + 1] = cnt[y * sw + x + 1] + rowCnt;
            }
        }
        var o = Rent<float>(pool, w * w);
        for (int y = 0; y < w; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(w, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                if (float.IsNaN(f[y * w + x])) { o[y * w + x] = float.NaN; continue; }
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                long s = sum[y1 * sw + x1] - sum[y0 * sw + x1] - sum[y1 * sw + x0] + sum[y0 * sw + x0];
                int n = cnt[y1 * sw + x1] - cnt[y0 * sw + x1] - cnt[y1 * sw + x0] + cnt[y0 * sw + x0];
                o[y * w + x] = (float)(s * Unit / n);
            }
        }
        pool?.Return(sum);
        pool?.Return(cnt);
        return o;
    }

    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The bed's depth below the still level at one wet cell, and where it came from (0 synthetic, 1 survey, 2 gap
    /// fill): the survey where there is one; within <see cref="FillReachM"/> of it, <c>Dedge · ds / (ds + dv)</c> fading
    /// to the synthetic profile; else <see cref="UnitSport.Terrain.Format.WaterBed.Depth"/>; never more than the
    /// distance to the shore (1:1).
    /// </summary>
    public static (double Depth, byte Kind) Depth(double dShore, double half, double maxDepth, float[]? survey, float[]? dv, float[]? edgeDepth, int wi)
    {
        double synthetic = UnitSport.Terrain.Format.WaterBed.Depth(dShore, half, maxDepth);
        double d;
        byte k = 0;
        if (survey != null && !float.IsNaN(survey[wi]))
        {
            d = survey[wi];
            k = 1;
        }
        else if (dv != null && dv[wi] < FillReachM)
        {
            double dSurvey = dv[wi];
            double edge = edgeDepth![wi];
            double fill = edge * dShore / (dShore + dSurvey);
            double w = 1 - SmoothStep(FillReachM / 2, FillReachM, dSurvey);
            d = synthetic + (fill - synthetic) * w;
            k = 2;
        }
        else d = synthetic;
        return (Math.Clamp(d, 0, dShore * MaxBankSlope), k);
    }
}
