namespace UnitSport.Terrain.Format;

/// <summary>
/// A cube tile's surface in its anchor frame (#1443, G1): the mesh vertices from the heights, and
/// a regular height field in the tangent plane for collision (docs/plans/global-tiling.md §2).
///
/// <para>
/// <b>Vertices.</b> Vertex (i, j) is the lattice point's normal raised by its height. Computing that
/// rigorously for a million vertices is ~0.1 s; instead the ground point S (height 0) and the unit
/// normal U are rigorous at a control grid every <c>step</c> vertices (17 × 17 for 1024 cells) and
/// bilinear in between, then <c>S + h·U</c>. The sag of the ellipsoid over a 76 m control span is
/// 0.1 mm and the warp's curvature less: <c>CubeGeometryTests</c> holds every vertex within 1 cm of
/// its rigorous point (measured far below).
/// </para>
///
/// <para>
/// <b>Mesh.</b> Each lattice quad is two triangles split along (i, j)–(i+1, j+1).
/// </para>
/// </summary>
public static class CubeGeometry
{
    /// <summary>
    /// Control points every this many vertices: 17 × 17 a tile at the leaves (the plan's figure), and
    /// never a span over ~100 m, where the ellipsoid's sag between two control points stays under a
    /// tenth of a millimetre. A coarse tile (#1430 H4) spans kilometres a cell, so there every vertex
    /// is rigorous: with the leaves' 32 the span would be 39 km at level 4 (a 30 m sag).
    /// </summary>
    public static int ControlStep(int level, int bits)
    {
        // a lattice step at level + bits is ~1.2 m · 2^(23 − level − bits) at a face centre
        int shift = 23 - level - bits;
        if (shift < 0) shift = 0;
        double spacing = 1.22 * Math.Pow(2, shift);
        int step = Math.Max(1, (1 << bits) / 16);
        while (step > 1 && step * spacing > 100) step >>= 1;
        return step;
    }

    /// <summary>The rigorous position of vertex (i, j) at height <paramref name="h"/> in the tile's anchor frame (x east, y up, z south).</summary>
    public static (double X, double Y, double Z) Exact(CubeKey key, int bits, int i, int j, double h) =>
        key.Anchor.ToLocal(key.Vertex(bits, i, j, h));

    /// <summary>
    /// Every vertex of a tile in its anchor frame, xyz floats, row-major as the heights. A NaN height
    /// (no data) gives a NaN vertex.
    /// </summary>
    public static float[] Vertices(CubeKey key, int bits, float[] heights, int step = 0)
    {
        if (step <= 0) step = ControlStep(key.Level, bits);
        int cells = 1 << bits, n = cells + 1, c = cells / step + 1;
        var anchor = key.Anchor;
        // ground point and normal, local, at the control grid
        var s = new double[c * c * 3];
        var u = new double[c * c * 3];
        for (int cj = 0; cj < c; cj++)
            for (int ci = 0; ci < c; ci++)
            {
                long gi = ((long)key.X << bits) + ci * step, gj = ((long)key.Y << bits) + cj * step;
                int lv = key.Level + bits;
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, gi), CubeSphere.Lattice(lv, gj));
                var (x, y, z) = anchor.ToLocal(CubeSphere.OnEllipsoid(normal, 0));
                var (nx, ny, nz) = anchor.DirectionToLocal(normal);
                int k = (cj * c + ci) * 3;
                s[k] = x; s[k + 1] = y; s[k + 2] = z;
                u[k] = nx; u[k + 1] = ny; u[k + 2] = nz;
            }

        var v = new float[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            int cj = Math.Min(j / step, c - 2);
            double fj = (j - cj * step) / (double)step;
            for (int i = 0; i < n; i++)
            {
                int ci = Math.Min(i / step, c - 2);
                double fi = (i - ci * step) / (double)step;
                double h = heights[j * n + i];
                int k00 = (cj * c + ci) * 3, k10 = k00 + 3, k01 = k00 + c * 3, k11 = k01 + 3;
                int o = (j * n + i) * 3;
                for (int a = 0; a < 3; a++)
                {
                    double sa = Bilerp(s[k00 + a], s[k10 + a], s[k01 + a], s[k11 + a], fi, fj);
                    double ua = Bilerp(u[k00 + a], u[k10 + a], u[k01 + a], u[k11 + a], fi, fj);
                    v[o + a] = (float)(sa + h * ua);
                }
            }
        }
        return v;
    }

    private static double Bilerp(double a, double b, double c, double d, double fx, double fy) =>
        a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;

    /// <summary>
    /// The mesh's height (local y) straight below a point of the tangent plane (x, z), or NaN off the
    /// mesh. Walks from <paramref name="hint"/> (a quad's (i, j), updated) to the quad whose projection
    /// holds the point; a neighbouring call starts where the last one ended.
    /// </summary>
    public static double MeshHeightAt(float[] vertices, int n, double x, double z, ref (int I, int J) hint)
    {
        int qi = Math.Clamp(hint.I, 0, n - 2), qj = Math.Clamp(hint.J, 0, n - 2);
        for (int walk = 0; walk < 4 * n; walk++)
        {
            int k00 = (qj * n + qi) * 3, k10 = k00 + 3, k01 = k00 + n * 3, k11 = k01 + 3;
            // lower triangle (00, 10, 11), upper (00, 11, 01): barycentrics in the xz projection
            if (Bary(vertices, k00, k10, k11, x, z, out double a, out double b, out double g) is var lower && lower >= -1e-9)
            {
                hint = (qi, qj);
                return a * vertices[k00 + 1] + b * vertices[k10 + 1] + g * vertices[k11 + 1];
            }
            if (Bary(vertices, k00, k11, k01, x, z, out a, out b, out g) is var upper && upper >= -1e-9)
            {
                hint = (qi, qj);
                return a * vertices[k00 + 1] + b * vertices[k11 + 1] + g * vertices[k01 + 1];
            }
            // step towards the point: compare with the quad's centre along its own two edges
            double cx = (vertices[k00] + vertices[k11]) * 0.5, cz = (vertices[k00 + 2] + vertices[k11 + 2]) * 0.5;
            double ex = vertices[k10] - vertices[k00], ez = vertices[k10 + 2] - vertices[k00 + 2];
            double fx = vertices[k01] - vertices[k00], fz = vertices[k01 + 2] - vertices[k00 + 2];
            double dx = x - cx, dz = z - cz;
            double det = ex * fz - ez * fx;
            if (double.IsNaN(det) || det == 0) return double.NaN;
            double si = (dx * fz - dz * fx) / det, sj = (ex * dz - ez * dx) / det;
            int ni = qi + (si > 0.5 ? 1 : si < -0.5 ? -1 : 0), nj = qj + (sj > 0.5 ? 1 : sj < -0.5 ? -1 : 0);
            ni = Math.Clamp(ni, 0, n - 2);
            nj = Math.Clamp(nj, 0, n - 2);
            if (ni == qi && nj == qj) return double.NaN; // off the mesh
            qi = ni; qj = nj;
        }
        return double.NaN;
    }

    /// <summary>The smallest barycentric coordinate of (x, z) in the projected triangle, and all three.</summary>
    private static double Bary(float[] v, int p, int q, int r, double x, double z, out double a, out double b, out double g)
    {
        double x1 = v[p], z1 = v[p + 2], x2 = v[q], z2 = v[q + 2], x3 = v[r], z3 = v[r + 2];
        double det = (z2 - z3) * (x1 - x3) + (x3 - x2) * (z1 - z3);
        a = ((z2 - z3) * (x - x3) + (x3 - x2) * (z - z3)) / det;
        b = ((z3 - z1) * (x - x3) + (x1 - x3) * (z - z3)) / det;
        g = 1 - a - b;
        return Math.Min(a, Math.Min(b, g));
    }

    /// <summary>
    /// A height field for collision in the tile's tangent frame: <paramref name="side"/> × side local
    /// y values (row-major, z rows from north to south, as Godot's <c>HeightMapShape3D</c> takes them),
    /// samples every <paramref name="spacing"/> metres centred on the anchor, each the mesh's height
    /// straight below it (NaN off the mesh: a hole). The caller composes several tiles' meshes to
    /// overlap a neighbour by one sample.
    /// </summary>
    public static float[] CollisionField(Func<double, double, double> meshHeight, int side, double spacing)
    {
        var f = new float[side * side];
        double half = (side - 1) * spacing / 2;
        for (int r = 0; r < side; r++)
            for (int col = 0; col < side; col++)
                f[r * side + col] = (float)meshHeight(-half + col * spacing, -half + r * spacing);
        return f;
    }
}
