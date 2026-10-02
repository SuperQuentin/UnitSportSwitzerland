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
    public static void Run(ReadOnlySpan<bool> isSite, int w, int h, float[] dist, int[]? site = null)
    {
        // pass 1, columns: squared distance to the nearest site in the same column, and its row
        var colD = new float[w * h];
        var colRow = new int[w * h];
        for (int x = 0; x < w; x++)
        {
            int last = -1;
            for (int y = 0; y < h; y++)
            {
                if (isSite[y * w + x]) last = y;
                colRow[y * w + x] = last;
            }
            last = -1;
            for (int y = h - 1; y >= 0; y--)
            {
                int i = y * w + x;
                if (isSite[i]) last = y;
                int up = colRow[i];
                int best = up < 0 ? last : last < 0 ? up : (y - up <= last - y ? up : last);
                colRow[i] = best;
                colD[i] = best < 0 ? Inf : (float)(y - best) * (y - best);
            }
        }

        // pass 2, rows: lower envelope of the parabolas colD[x'] + (x - x')^2
        var v = new int[w];
        var z = new double[w + 1];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int k = -1;
            for (int q = 0; q < w; q++)
            {
                if (colD[row + q] >= Inf) continue;
                if (k < 0) { k = 0; v[0] = q; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity; continue; }
                double fq = colD[row + q] + (double)q * q;
                double s = (fq - (colD[row + v[k]] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                while (s <= z[k])
                {
                    k--;
                    s = (fq - (colD[row + v[k]] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                }
                k++;
                v[k] = q;
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
    }
}
