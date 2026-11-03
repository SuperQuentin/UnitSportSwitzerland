using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A cube tile's heights from the sources (#1443, G1; docs/plans/global-tiling.md §7).
///
/// <para>
/// <b>Control points.</b> At every <see cref="CubeGeometry.ControlStep"/>-th lattice vertex (17 × 17 a
/// tile) the way from the vertex's normal to the sources is computed rigorously, at two heights (0
/// and 1,000 m on the normal): its LV95 position and LHN95 height, and its grid position in every
/// foreign DEM. All are linear in the height along the normal to far below a millimetre, so each is
/// kept as a value and a slope, bilinear between control points. A vertex then finds its ground by
/// the same fixed point as <see cref="CubeHeightSource.ExactHeight"/> on those interpolated maps.
/// </para>
///
/// <para>
/// <b>Edges are rigorous.</b> The outer rows and columns are computed exactly, vertex by vertex: they
/// are a pure function of the shared lattice point, so a tile and its neighbour (even across a face
/// edge, where their control grids turn) hold bit-identical edge heights.
/// </para>
/// </summary>
public sealed class CubeResampler
{
    private readonly CubeHeightSource _src;

    public CubeResampler(CubeHeightSource src) => _src = src;

    /// <summary>The tile's (2^bits + 1)² ellipsoidal heights, NaN where no source has data; null when it has none at all.</summary>
    public float[]? Heights(CubeKey key, int bits, CubeHeightSource.Cursors cur)
    {
        int cells = 1 << bits, n = cells + 1, step = CubeGeometry.ControlStep(key.Level, bits), c = cells / step + 1;
        int dems = _src.Dems.Count, per = 6 + 4 * dems;
        int lv = key.Level + bits;
        long gi0 = (long)key.X << bits, gj0 = (long)key.Y << bits;

        // per control point: E0, N0, H0, dE, dN, dH (per metre along the normal), then per DEM x0, y0, dx, dy
        var ctl = new double[c * c * per];
        bool any = false;
        for (int cj = 0; cj < c; cj++)
            for (int ci = 0; ci < c; ci++)
            {
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, gi0 + ci * step), CubeSphere.Lattice(lv, gj0 + cj * step));
                var p0 = CubeSphere.OnEllipsoid(normal, 0);
                var p1 = CubeSphere.OnEllipsoid(normal, 1000);
                var (e0, n0, h0) = SwissGeodesy.FromEcef(p0);
                var (e1, n1, h1) = SwissGeodesy.FromEcef(p1);
                int o = (cj * c + ci) * per;
                ctl[o] = e0; ctl[o + 1] = n0; ctl[o + 2] = h0;
                ctl[o + 3] = (e1 - e0) / 1000; ctl[o + 4] = (n1 - n0) / 1000; ctl[o + 5] = (h1 - h0) / 1000;
                if (dems > 0)
                {
                    var g0 = Ellipsoid.Wgs84.ToGeodetic(p0);
                    var g1 = Ellipsoid.Wgs84.ToGeodetic(p1);
                    for (int d = 0; d < dems; d++)
                    {
                        var (x0, y0) = CubeHeightSource.GridOf(_src.Dems[d].Crs, Rad(g0.LatDeg), Rad(g0.LonDeg));
                        var (x1, y1) = CubeHeightSource.GridOf(_src.Dems[d].Crs, Rad(g1.LatDeg), Rad(g1.LonDeg));
                        int k = o + 6 + 4 * d;
                        ctl[k] = x0; ctl[k + 1] = y0; ctl[k + 2] = (x1 - x0) / 1000; ctl[k + 3] = (y1 - y0) / 1000;
                    }
                }
            }

        var heights = new float[n * n];
        Span<double> m = stackalloc double[per];
        Span<double> xy = stackalloc double[Math.Max(2, 2 * dems)];
        for (int j = 0; j < n; j++)
        {
            int cj = Math.Min(j / step, c - 2);
            double fj = (j - cj * step) / (double)step;
            double h = 550;
            for (int i = 0; i < n; i++)
            {
                double result;
                if (i == 0 || j == 0 || i == cells || j == cells)
                {
                    var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, gi0 + i), CubeSphere.Lattice(lv, gj0 + j));
                    result = _src.ExactHeight(normal, cur);
                }
                else
                {
                    int ci = Math.Min(i / step, c - 2);
                    double fi = (i - ci * step) / (double)step;
                    int o00 = (cj * c + ci) * per, o10 = o00 + per, o01 = o00 + c * per, o11 = o01 + per;
                    for (int a = 0; a < per; a++)
                    {
                        double v00 = ctl[o00 + a];
                        m[a] = v00 + (ctl[o10 + a] - v00) * fi + (ctl[o01 + a] - v00) * fj + (v00 - ctl[o10 + a] - ctl[o01 + a] + ctl[o11 + a]) * fi * fj;
                    }
                    result = double.NaN;
                    for (int it = 0; it < 8; it++)
                    {
                        for (int d = 0; d < dems; d++)
                        {
                            xy[2 * d] = m[6 + 4 * d] + h * m[8 + 4 * d];
                            xy[2 * d + 1] = m[7 + 4 * d] + h * m[9 + 4 * d];
                        }
                        double hs = _src.Height(m[0] + h * m[3], m[1] + h * m[4], xy, cur);
                        if (double.IsNaN(hs)) { result = double.NaN; break; }
                        // the point's own LHN95 height moves ~1:1 with h; solve for where it meets the source's
                        double step2 = (hs - (m[2] + h * m[5])) / m[5];
                        h += step2;
                        result = h;
                        if (Math.Abs(step2) < 1e-5) break;
                    }
                }
                heights[j * n + i] = (float)result;
                if (!double.IsNaN(result)) { any = true; h = result; }
            }
        }
        return any ? heights : null;
    }

    private static double Rad(double deg) => deg * Math.PI / 180;
}
