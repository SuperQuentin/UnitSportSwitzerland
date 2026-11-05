using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A leaf's lattice kinds from a producer's functions of its own coordinates (#1509, G2 B5): every vertex computed at
/// its own place (<see cref="SourceOnLeaf.SourceAt"/>), never an LV95 raster resampled, as the build stages do
/// (<c>tools/cube-frames</c>). Heights above sea level become ellipsoidal by the leaf's own undulation.
/// </summary>
public static class LeafLattice
{
    /// <summary>The heights; <paramref name="hSea"/> NaN where there is no ground.</summary>
    public static CubeTile Heights(SourceOnLeaf on, Func<double, double, double> hSea)
    {
        var t = on.Transform;
        int n = on.Side;
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var (a, b) = on.SourceAt(i, j);
                double s = hSea(a, b);
                h[i + j * n] = double.IsNaN(s) ? float.NaN : (float)(s + t.UndulationAt(i, j));
            }
        return new CubeTile { Key = on.Leaf, Bits = t.Bits, Heights = h };
    }

    /// <summary>
    /// The heights from a ground that is costly per point: taken every <paramref name="stride"/> vertices and bilinear
    /// between, as the LV95 sources hold a 10 m lattice (<paramref name="stride"/> must divide the cells).
    /// </summary>
    public static CubeTile Heights(SourceOnLeaf on, Func<double, double, double> hSea, int stride)
    {
        int cells = on.Side - 1, m = cells / stride + 1;
        if (cells % stride != 0) throw new ArgumentException($"stride {stride} does not divide {cells}");
        var coarse = new double[m * m];
        for (int cj = 0; cj < m; cj++)
            for (int ci = 0; ci < m; ci++)
            {
                var (a, b) = on.SourceAt(ci * stride, cj * stride);
                coarse[ci + cj * m] = hSea(a, b);
            }
        return HeightsAtVertices(on, (i, j) =>
        {
            double fi = (double)i / stride, fj = (double)j / stride;
            int ci = Math.Min((int)fi, m - 2), cj = Math.Min((int)fj, m - 2);
            double u = fi - ci, v = fj - cj;
            int k = ci + cj * m;
            double south = coarse[k] + (coarse[k + 1] - coarse[k]) * u;
            double north = coarse[k + m] + (coarse[k + m + 1] - coarse[k + m]) * u;
            return south + (north - south) * v;
        });
    }

    /// <summary>The heights from a function of the lattice point itself (i, j), above sea level.</summary>
    public static CubeTile HeightsAtVertices(SourceOnLeaf on, Func<int, int, double> hSeaAtVertex)
    {
        var t = on.Transform;
        int n = on.Side;
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double s = hSeaAtVertex(i, j);
                h[i + j * n] = double.IsNaN(s) ? float.NaN : (float)(s + t.UndulationAt(i, j));
            }
        return new CubeTile { Key = on.Leaf, Bits = t.Bits, Heights = h };
    }

    /// <summary>The cover class at every vertex.</summary>
    public static byte[] Cover(SourceOnLeaf on, Func<double, double, CoverClass> cover)
    {
        int n = on.Side;
        var cells = new byte[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var (a, b) = on.SourceAt(i, j);
                cells[i + j * n] = (byte)cover(a, b);
            }
        return cells;
    }

    /// <summary>
    /// Still water on every second vertex: the level above sea level (NaN dry) and the fetch in metres. Null when the
    /// whole leaf is dry, as a dry LV95 tile has no water file.
    /// </summary>
    public static CubeWater? Water(SourceOnLeaf on, Func<double, double, (double Level, double Fetch)> water)
    {
        var t = on.Transform;
        int bits = t.Bits, n = CubeWater.SideFor(bits);
        var w = CubeWater.Dry(bits);
        bool any = false;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                int i = c * CubeWater.Stride, j = r * CubeWater.Stride;
                var (a, b) = on.SourceAt(i, j);
                var (level, fetch) = water(a, b);
                if (double.IsNaN(level)) continue;
                w.Level[c + r * n] = (float)(level + t.UndulationAt(i, j));
                w.Fetch[c + r * n] = WaterFormat.QuantizeFetch(fetch);
                any = true;
            }
        return any ? w : null;
    }
}
