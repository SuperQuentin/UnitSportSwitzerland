using System.Numerics;
using System.Runtime.InteropServices;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Exact Euclidean distance transform on a grid (Felzenszwalb and Huttenlocher, "Distance
/// Transforms of Sampled Functions"): for every cell, the distance in cells to the nearest site,
/// and optionally which site. Linear in the cell count, two passes. Ties resolve the same way
/// wherever the grid is cut, so the result is a pure function of the sites.
/// </summary>
public static class DistanceTransform
{
    private const float Inf = 1e20f;

    /// <param name="isSite">Sites, row-major <paramref name="w"/> x <paramref name="h"/>.</param>
    /// <param name="dist">Out: distance in cells to the nearest site (<see cref="float.PositiveInfinity"/> when there is none).</param>
    /// <param name="site">Out, optional: row-major index of that site (-1 when there is none).</param>
    public static void Run(ReadOnlySpan<bool> isSite, int w, int h, float[] dist, int[]? site = null, WaterWindow.Pool? pool = null)
    {
        // pass 1, columns: squared distance to the nearest site in the same column, and its row
        var colD = pool != null ? pool.Rent<float>(w * h) : new float[w * h];
        var colRow = pool != null ? pool.Rent<int>(w * h) : new int[w * h];
        // (a row at a time, the columns side by side: a column at a time misses the cache on every cell)
        var lastSite = new int[w];
        Array.Fill(lastSite, -1);
        for (int y = 0; y < h; y++) ForwardRow(isSite, y, w, lastSite, colRow);
        Array.Fill(lastSite, -1);
        for (int y = h - 1; y >= 0; y--) BackwardRow(isSite, y, w, lastSite, colRow, colD);

        // pass 2, rows: lower envelope of the parabolas colD[x'] + (x - x')^2
        var v = new int[w];
        var z = new double[w + 1];
        var fv = new double[w];   // each parabola on the envelope at its own site, colD + q²: the same double as recomputing it at every comparison
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            // a row that is all sites (every column's nearest site is the cell itself): each cell is its own site at distance 0
            if (!colD.AsSpan(row, w).ContainsAnyExcept(0f))
            {
                dist.AsSpan(row, w).Clear();
                if (site != null) for (int q = 0; q < w; q++) site[row + q] = row + q;
                continue;
            }
            int k = -1;
            for (int q = 0; q < w; q++)
            {
                if (colD[row + q] >= Inf) continue;
                double fq = colD[row + q] + (double)q * q;
                if (k < 0) { k = 0; v[0] = q; fv[0] = fq; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity; continue; }
                double s = (fq - fv[k]) / (2.0 * (q - v[k]));
                while (s <= z[k])
                {
                    k--;
                    s = (fq - fv[k]) / (2.0 * (q - v[k]));
                }
                k++;
                v[k] = q;
                fv[k] = fq;
                z[k] = s;
                z[k + 1] = double.PositiveInfinity;
            }

            if (k < 0)
            {
                for (int q = 0; q < w; q++)
                {
                    dist[row + q] = float.PositiveInfinity;
                    if (site != null) site[row + q] = -1;
                }
                continue;
            }

            int j = 0;
            for (int q = 0; q < w; q++)
            {
                while (z[j + 1] < q) j++;
                int p = v[j];
                double d2 = colD[row + p] + (double)(q - p) * (q - p);
                dist[row + q] = (float)Math.Sqrt(d2);
                if (site != null) site[row + q] = colRow[row + p] * w + p;
            }
        }
        pool?.Return(colD);
        pool?.Return(colRow);
    }

    /// <summary>
    /// Pass 1, downwards, one row: the nearest site above (or in) each column, into <paramref name="colRow"/>. Integer selects,
    /// so a vector holds exactly what the scalar loop does; the scalar loop takes the columns a whole vector does not cover.
    /// </summary>
    private static void ForwardRow(ReadOnlySpan<bool> isSite, int y, int w, int[] lastSite, int[] colRow)
    {
        int row = y * w, x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var sites = MemoryMarshal.AsBytes(isSite);
            int n = Vector<int>.Count;
            var yv = new Vector<int>(y);
            for (; x <= w - Vector<byte>.Count; x += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(sites.Slice(row + x)), out var s0, out var s1);
                Vector.Widen(s0, out var a0, out var a1);
                Vector.Widen(s1, out var a2, out var a3);
                ForwardStep(a0, yv, lastSite, colRow, x, row);
                ForwardStep(a1, yv, lastSite, colRow, x + n, row);
                ForwardStep(a2, yv, lastSite, colRow, x + 2 * n, row);
                ForwardStep(a3, yv, lastSite, colRow, x + 3 * n, row);
            }
        }
        for (; x < w; x++)
        {
            if (isSite[row + x]) lastSite[x] = y;
            colRow[row + x] = lastSite[x];
        }
    }

    private static void ForwardStep(Vector<uint> site, Vector<int> y, int[] lastSite, int[] colRow, int x, int row)
    {
        var last = Vector.ConditionalSelect(Vector.AsVectorInt32(Vector.GreaterThan(site, Vector<uint>.Zero)), y, new Vector<int>(lastSite, x));
        last.CopyTo(lastSite, x);
        last.CopyTo(colRow, row + x);
    }

    /// <summary>
    /// Pass 1, upwards, one row: the nearest site of the column above or below (the upper on a tie), and its squared distance.
    /// The square is a float product of two whole numbers below 2^12 (a window is at most 4096 cells), exact in a float either way.
    /// </summary>
    private static void BackwardRow(ReadOnlySpan<bool> isSite, int y, int w, int[] lastSite, int[] colRow, float[] colD)
    {
        int row = y * w, x = 0;
        if (Vector.IsHardwareAccelerated && w <= 4096)
        {
            var sites = MemoryMarshal.AsBytes(isSite);
            int n = Vector<int>.Count;
            var yv = new Vector<int>(y);
            for (; x <= w - Vector<byte>.Count; x += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(sites.Slice(row + x)), out var s0, out var s1);
                Vector.Widen(s0, out var a0, out var a1);
                Vector.Widen(s1, out var a2, out var a3);
                BackwardStep(a0, yv, lastSite, colRow, colD, x, row);
                BackwardStep(a1, yv, lastSite, colRow, colD, x + n, row);
                BackwardStep(a2, yv, lastSite, colRow, colD, x + 2 * n, row);
                BackwardStep(a3, yv, lastSite, colRow, colD, x + 3 * n, row);
            }
        }
        for (; x < w; x++)
        {
            int i = row + x;
            if (isSite[i]) lastSite[x] = y;
            int last = lastSite[x], up = colRow[i];
            int best = up < 0 ? last : last < 0 ? up : (y - up <= last - y ? up : last);
            colRow[i] = best;
            colD[i] = best < 0 ? Inf : (float)(y - best) * (y - best);
        }
    }

    private static void BackwardStep(Vector<uint> site, Vector<int> y, int[] lastSite, int[] colRow, float[] colD, int x, int row)
    {
        var last = Vector.ConditionalSelect(Vector.AsVectorInt32(Vector.GreaterThan(site, Vector<uint>.Zero)), y, new Vector<int>(lastSite, x));
        last.CopyTo(lastSite, x);
        var up = new Vector<int>(colRow, row + x);
        var nearer = Vector.ConditionalSelect(Vector.LessThanOrEqual(y - up, last - y), up, last);
        var best = Vector.ConditionalSelect(Vector.LessThan(up, Vector<int>.Zero), last,
            Vector.ConditionalSelect(Vector.LessThan(last, Vector<int>.Zero), up, nearer));
        best.CopyTo(colRow, row + x);
        var d = Vector.ConvertToSingle(y - best);
        Vector.ConditionalSelect(Vector.LessThan(best, Vector<int>.Zero), new Vector<float>(Inf), d * d).CopyTo(colD, row + x);
    }
}
