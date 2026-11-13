namespace UnitSport.Terrain.Format;

/// <summary>
/// One leaf seen from a work frame (#1507, G2 B3; docs/plans/global-tiling-g2.md): a stage's plane point
/// (x east, y north on the frame's <see cref="WorkPlane"/>) and its height above sea level, against the
/// leaf's anchor frame (<see cref="CubeKey.Anchor"/>: x east, y up, z south) and its lattice (fractional
/// (i, j), i along u, j along v, 0..2^bits). Godot-free, so the stages and the game share it.
///
/// <para>
/// <b>Control points (G1's scheme, <see cref="CubeGeometry"/>).</b> Every <see cref="CubeGeometry.ControlStep"/>
/// lattice vertices (17 × 17 a level-14 leaf, spans of ~39 m) the chain is computed rigorously: the
/// vertex's geodetic normal, its point on the ellipsoid S and the normal U in the anchor frame, its plane
/// point, and the sea level there. A height moves a point along its normal, so S + h·U is the point at
/// ellipsoidal height h exactly ("two heights": the chain is linear in h); the plane point does not move
/// with height at all (the plane is the ellipsoid seen from above). Between control points every part is
/// bilinear: the ellipsoid's fall from the anchor's tangent plane (1.5 cm at a leaf corner) is in S, so
/// interpolating it sags 0.03 mm over a span. The undulation is cached on the control grid (one rigorous
/// call costs ~2 µs); a gridded sea level (<see cref="IGriddedSeaLevel"/>: CHGeo2004) has its smooth
/// parts interpolated and its grid read at the point, so its kinks stay sharp.
/// </para>
///
/// <para>
/// <b>Edges are rigorous.</b> A lattice point on the leaf's outer rows or columns is computed through the
/// chain, a pure function of the shared point, so two leaves sharing an edge map its lattice points to the
/// same plane point and the same ECEF point to the bit (across face edges too). Inside, everything stays
/// within 0.1 mm of the chain (<c>LeafTransformTests</c>).
/// </para>
///
/// <para>
/// <b>Inverses</b> (plane → lattice, local → lattice) are Newton on the bilinear patches, from an affine
/// guess; valid over the leaf and a few metres round it (farther out, ask the leaf that holds the point).
/// Directions turn through the Jacobian of plane → local at the point.
/// </para>
/// </summary>
public sealed class LeafTransform
{
    // per control point: plane x, y; S x, y, z; U x, y, z; then the sea level: N, or (smooth, a, b)
    private const int P = 0, S = 2, U = 5, Sea = 8;

    public CubeKey Key { get; }

    public WorkFrame Frame { get; }

    /// <summary>log2 of the lattice's cells a side (<see cref="CubeKey.LatticeBits"/>).</summary>
    public int Bits { get; }

    public int Cells { get; }

    /// <summary>Lattice vertices between control points.</summary>
    public int Step { get; }

    public EnuFrame Anchor { get; }

    private readonly int _c, _lv, _per;
    private readonly long _gi0, _gj0;
    private readonly double _du;
    private readonly IGriddedSeaLevel? _gridded;
    private readonly double[] _ctl;

    // the undulation at every lattice point of the outer rows (rigorous), south, north, west, east
    private readonly double[] _edgeN;

    // affine guesses: plane -> lattice and local (x, z) -> lattice
    private readonly double _px0, _py0, _pa, _pb, _pc, _pd;
    private readonly double _lx0, _lz0, _la, _lb, _lc, _ld;

    public LeafTransform(WorkFrame frame, CubeKey key, int bits = -1)
    {
        Frame = frame;
        Key = key;
        Bits = bits < 0 ? CubeKey.LatticeBits(key.Level) : bits;
        Cells = 1 << Bits;
        Step = CubeGeometry.ControlStep(key.Level, Bits);
        _c = Cells / Step + 1;
        _lv = key.Level + Bits;
        _gi0 = (long)key.X << Bits;
        _gj0 = (long)key.Y << Bits;
        _du = 2.0 / (1L << _lv);
        Anchor = key.Anchor;
        _gridded = frame.SeaLevel as IGriddedSeaLevel;
        _per = Sea + (_gridded != null ? 3 : 1);
        _ctl = new double[_c * _c * _per];
        for (int cj = 0; cj < _c; cj++)
            for (int ci = 0; ci < _c; ci++)
                Rigorous(ci * Step, cj * Step, _ctl.AsSpan((cj * _c + ci) * _per, _per));

        int n = Cells + 1;
        _edgeN = new double[4 * n];
        for (int k = 0; k < n; k++)
        {
            _edgeN[k] = ExactUndulation(k, 0);
            _edgeN[n + k] = ExactUndulation(k, Cells);
            _edgeN[2 * n + k] = ExactUndulation(0, k);
            _edgeN[3 * n + k] = ExactUndulation(Cells, k);
        }

        int o00 = 0, o10 = (_c - 1) * _per, o01 = (_c - 1) * _c * _per;
        (_px0, _py0) = (_ctl[o00 + P], _ctl[o00 + P + 1]);
        (_pa, _pb, _pc, _pd) = Inverse((_ctl[o10 + P] - _px0) / Cells, (_ctl[o01 + P] - _px0) / Cells,
            (_ctl[o10 + P + 1] - _py0) / Cells, (_ctl[o01 + P + 1] - _py0) / Cells);
        (_lx0, _lz0) = (_ctl[o00 + S], _ctl[o00 + S + 2]);
        (_la, _lb, _lc, _ld) = Inverse((_ctl[o10 + S] - _lx0) / Cells, (_ctl[o01 + S] - _lx0) / Cells,
            (_ctl[o10 + S + 2] - _lz0) / Cells, (_ctl[o01 + S + 2] - _lz0) / Cells);
    }

    /// <summary>The geodetic normal of the lattice point (i, j), fractional, past the leaf's edges too (on its face's gnomonic).</summary>
    public EcefPos NormalAt(double i, double j) =>
        // CubeSphere.Lattice's own arithmetic, so an integer point is the same double as there
        CubeSphere.Normal(Key.Face, -1.0 + (_gi0 + i) * _du, -1.0 + (_gj0 + j) * _du);

    private bool OnEdge(double i, double j) => i == 0 || j == 0 || i == Cells || j == Cells;

    private void Rigorous(double i, double j, Span<double> into)
    {
        var n = NormalAt(i, j);
        var s = CubeSphere.OnEllipsoid(n, 0);
        (into[P], into[P + 1]) = Frame.Plane.ToPlane(s);
        (into[S], into[S + 1], into[S + 2]) = Anchor.ToLocal(s);
        (into[U], into[U + 1], into[U + 2]) = Anchor.DirectionToLocal(n);
        var (lat, lon) = LatLon(n);
        if (_gridded != null) (into[Sea], into[Sea + 1], into[Sea + 2]) = _gridded.Split(lat, lon);
        else into[Sea] = Frame.SeaLevel.Undulation(lat, lon);
    }

    private double ExactUndulation(double i, double j)
    {
        var (lat, lon) = LatLon(NormalAt(i, j));
        return Frame.SeaLevel.Undulation(lat, lon);
    }

    private static (double Lat, double Lon) LatLon(EcefPos n) =>
        (Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI, Math.Atan2(n.Y, n.X) * 180 / Math.PI);

    /// <summary>The control cell of (i, j) (the outer ones carry on past the leaf) and the fractions in it.</summary>
    private void Cell(double i, double j, out int o00, out double fi, out double fj)
    {
        int ci = Math.Clamp((int)Math.Floor(i / Step), 0, _c - 2), cj = Math.Clamp((int)Math.Floor(j / Step), 0, _c - 2);
        fi = (i - ci * Step) / Step;
        fj = (j - cj * Step) / Step;
        o00 = (cj * _c + ci) * _per;
    }

    private double Lerp(int o00, int a, double fi, double fj)
    {
        int o10 = o00 + _per, o01 = o00 + _c * _per, o11 = o01 + _per;
        double v00 = _ctl[o00 + a];
        return v00 + (_ctl[o10 + a] - v00) * fi + (_ctl[o01 + a] - v00) * fj + (v00 - _ctl[o10 + a] - _ctl[o01 + a] + _ctl[o11 + a]) * fi * fj;
    }

    /// <summary>The bilinear value and its derivatives along i and j (per lattice step).</summary>
    private (double V, double Di, double Dj) LerpD(int o00, int a, double fi, double fj)
    {
        int o10 = o00 + _per, o01 = o00 + _c * _per, o11 = o01 + _per;
        double v00 = _ctl[o00 + a], v10 = _ctl[o10 + a], v01 = _ctl[o01 + a], v11 = _ctl[o11 + a];
        double cross = v00 - v10 - v01 + v11;
        return (v00 + (v10 - v00) * fi + (v01 - v00) * fj + cross * fi * fj,
            ((v10 - v00) + cross * fj) / Step, ((v01 - v00) + cross * fi) / Step);
    }

    // ---- lattice -> plane, sea level, local, ECEF -------------------------------------------------

    /// <summary>The plane point under lattice point (i, j).</summary>
    public (double X, double Y) LatticeToPlane(double i, double j)
    {
        if (OnEdge(i, j)) return Frame.Plane.ToPlane(CubeSphere.OnEllipsoid(NormalAt(i, j), 0));
        Cell(i, j, out int o, out double fi, out double fj);
        return (Lerp(o, P, fi, fj), Lerp(o, P + 1, fi, fj));
    }

    /// <summary>The ellipsoidal height of sea level at lattice point (i, j): add it to a height above sea level.</summary>
    public double UndulationAt(double i, double j)
    {
        if (OnEdge(i, j))
        {
            int n = Cells + 1;
            if (j == 0 && i == Math.Floor(i)) return _edgeN[(int)i];
            if (j == Cells && i == Math.Floor(i)) return _edgeN[n + (int)i];
            if (i == 0 && j == Math.Floor(j)) return _edgeN[2 * n + (int)j];
            if (i == Cells && j == Math.Floor(j)) return _edgeN[3 * n + (int)j];
            return ExactUndulation(i, j);
        }
        Cell(i, j, out int o, out double fi, out double fj);
        if (_gridded == null) return Lerp(o, Sea, fi, fj);
        return Lerp(o, Sea, fi, fj) + _gridded.Grid(Lerp(o, Sea + 1, fi, fj), Lerp(o, Sea + 2, fi, fj));
    }

    /// <summary>The anchor-frame point of lattice point (i, j) at ellipsoidal height <paramref name="hEll"/>.</summary>
    public (double X, double Y, double Z) LatticeToLocal(double i, double j, double hEll)
    {
        if (OnEdge(i, j)) return Anchor.ToLocal(CubeSphere.OnEllipsoid(NormalAt(i, j), hEll));
        Cell(i, j, out int o, out double fi, out double fj);
        return (Lerp(o, S, fi, fj) + hEll * Lerp(o, U, fi, fj), Lerp(o, S + 1, fi, fj) + hEll * Lerp(o, U + 1, fi, fj),
            Lerp(o, S + 2, fi, fj) + hEll * Lerp(o, U + 2, fi, fj));
    }

    /// <summary>
    /// The ECEF point of lattice point (i, j) at an ellipsoidal height: on the outer rows straight from the
    /// chain (the same bits from either leaf), inside through the anchor.
    /// </summary>
    public EcefPos LatticeToEcef(double i, double j, double hEll)
    {
        if (OnEdge(i, j)) return CubeSphere.OnEllipsoid(NormalAt(i, j), hEll);
        var (x, y, z) = LatticeToLocal(i, j, hEll);
        return Anchor.ToEcef(x, y, z);
    }

    // ---- plane -> lattice, local ------------------------------------------------------------------

    /// <summary>The fractional lattice point under a plane point (Newton on the bilinear patches, to 1e-9 of a step).</summary>
    public (double I, double J) PlaneToLattice(double x, double y)
    {
        double dx = x - _px0, dy = y - _py0;
        double i = _pa * dx + _pb * dy, j = _pc * dx + _pd * dy;
        for (int it = 0; it < 12; it++)
        {
            Cell(i, j, out int o, out double fi, out double fj);
            var (px, pxi, pxj) = LerpD(o, P, fi, fj);
            var (py, pyi, pyj) = LerpD(o, P + 1, fi, fj);
            double rx = px - x, ry = py - y, det = pxi * pyj - pxj * pyi;
            double di = (rx * pyj - ry * pxj) / det, dj = (pxi * ry - pyi * rx) / det;
            i -= di;
            j -= dj;
            if (Math.Abs(di) + Math.Abs(dj) < 1e-9) break;
        }
        return (i, j);
    }

    /// <summary>A plane point and its height above sea level in the anchor frame.</summary>
    public (double X, double Y, double Z) PlaneToLocal(double x, double y, double hSea)
    {
        var (i, j) = PlaneToLattice(x, y);
        return LatticeToLocal(i, j, hSea + UndulationAt(i, j));
    }

    /// <summary>
    /// <see cref="LocalToLattice"/>'s affine first guess alone (#1751): the fractional lattice point under an anchor-frame
    /// (x, z), height ignored. Within a fraction of a metre over a level-14 leaf on the ground (the normals' tilt moves a point
    /// 0.05 m a kilometre up at a corner); a fast hint, never the answer.
    /// </summary>
    public (double I, double J) LocalToLatticeGuess(double x, double z)
    {
        double dx = x - _lx0, dz = z - _lz0;
        return (_la * dx + _lb * dz, _lc * dx + _ld * dz);
    }

    /// <summary>An anchor-frame point as a fractional lattice point and its ellipsoidal height (Newton on S + h·U).</summary>
    public (double I, double J, double HEll) LocalToLattice(double x, double y, double z)
    {
        double dx = x - _lx0, dz = z - _lz0;
        double i = _la * dx + _lb * dz, j = _lc * dx + _ld * dz, h = 0;
        for (int it = 0; it < 12; it++)
        {
            Cell(i, j, out int o, out double fi, out double fj);
            var (sx, sxi, sxj) = LerpD(o, S, fi, fj);
            var (sy, syi, syj) = LerpD(o, S + 1, fi, fj);
            var (sz, szi, szj) = LerpD(o, S + 2, fi, fj);
            var (ux, uxi, uxj) = LerpD(o, U, fi, fj);
            var (uy, uyi, uyj) = LerpD(o, U + 1, fi, fj);
            var (uz, uzi, uzj) = LerpD(o, U + 2, fi, fj);
            if (it == 0) h = (x - sx) * ux + (y - sy) * uy + (z - sz) * uz;
            double fx = sx + h * ux - x, fy = sy + h * uy - y, fz = sz + h * uz - z;
            // columns: d/di, d/dj, d/dh
            double ax = sxi + h * uxi, ay = syi + h * uyi, az = szi + h * uzi;
            double bx = sxj + h * uxj, by = syj + h * uyj, bz = szj + h * uzj;
            double det = ax * (by * uz - bz * uy) - bx * (ay * uz - az * uy) + ux * (ay * bz - az * by);
            double di = (fx * (by * uz - bz * uy) - bx * (fy * uz - fz * uy) + ux * (fy * bz - fz * by)) / det;
            double dj = (ax * (fy * uz - fz * uy) - fx * (ay * uz - az * uy) + ux * (ay * fz - az * fy)) / det;
            double dh = (ax * (by * fz - bz * fy) - bx * (ay * fz - az * fy) + fx * (ay * bz - az * by)) / det;
            i -= di;
            j -= dj;
            h -= dh;
            if (Math.Abs(di) + Math.Abs(dj) < 1e-9 && Math.Abs(dh) < 1e-7) break;
        }
        return (i, j, h);
    }

    /// <summary>An anchor-frame point as a plane point and its height above sea level.</summary>
    public (double X, double Y, double HSea) LocalToPlane(double x, double y, double z)
    {
        var (i, j, h) = LocalToLattice(x, y, z);
        var (px, py) = LatticeToPlane(i, j);
        return (px, py, h - UndulationAt(i, j));
    }

    // ---- directions -------------------------------------------------------------------------------

    /// <summary>
    /// The Jacobian at a plane point: plane (x, y) to the anchor frame's (east, north) at the given height
    /// above sea level, row-major (dE/dx, dE/dy, dN/dx, dN/dy).
    /// </summary>
    public (double Ex, double Ey, double Nx, double Ny) Jacobian(double x, double y, double hSea = 0)
    {
        var (i, j) = PlaneToLattice(x, y);
        double h = hSea + UndulationAt(i, j);
        Cell(i, j, out int o, out double fi, out double fj);
        var (_, pxi, pxj) = LerpD(o, P, fi, fj);
        var (_, pyi, pyj) = LerpD(o, P + 1, fi, fj);
        var (_, sxi, sxj) = LerpD(o, S, fi, fj);
        var (_, szi, szj) = LerpD(o, S + 2, fi, fj);
        var (_, uxi, uxj) = LerpD(o, U, fi, fj);
        var (_, uzi, uzj) = LerpD(o, U + 2, fi, fj);
        // local east and north (−z) along i and j
        double ei = sxi + h * uxi, ej = sxj + h * uxj, ni = -(szi + h * uzi), nj = -(szj + h * uzj);
        var (a, b, c, d) = Inverse(pxi, pxj, pyi, pyj);
        return (ei * a + ej * c, ei * b + ej * d, ni * a + nj * c, ni * b + nj * d);
    }

    /// <summary>A horizontal plane direction (dx east, dy north) at a plane point, turned into the anchor frame: (dx east, dz south), unit length.</summary>
    public (double Dx, double Dz) DirectionToLocal(double x, double y, double dx, double dy, double hSea = 0)
    {
        var (ex, ey, nx, ny) = Jacobian(x, y, hSea);
        double e = ex * dx + ey * dy, n = nx * dx + ny * dy, len = Math.Sqrt(e * e + n * n);
        return (e / len, -n / len);
    }

    /// <summary>The inverse of <see cref="DirectionToLocal"/>: an anchor-frame direction (dx east, dz south) at a plane point as a unit plane direction (dx, dy).</summary>
    public (double Dx, double Dy) DirectionToPlane(double x, double y, double dx, double dz, double hSea = 0)
    {
        var (ex, ey, nx, ny) = Jacobian(x, y, hSea);
        var (a, b, c, d) = Inverse(ex, ey, nx, ny);
        double e = dx, n = -dz;
        double px = a * e + b * n, py = c * e + d * n, len = Math.Sqrt(px * px + py * py);
        return (px / len, py / len);
    }

    /// <summary>A heading on the plane (degrees clockwise from the plane's north) at a plane point, as a heading in the anchor frame (degrees clockwise from its north, −z), 0..360.</summary>
    public double HeadingToLocal(double x, double y, double headingDeg, double hSea = 0)
    {
        double r = headingDeg * Math.PI / 180;
        var (dx, dz) = DirectionToLocal(x, y, Math.Sin(r), Math.Cos(r), hSea);
        return Degrees(Math.Atan2(dx, -dz));
    }

    /// <summary>The inverse of <see cref="HeadingToLocal"/>.</summary>
    public double HeadingToPlane(double x, double y, double headingDeg, double hSea = 0)
    {
        double r = headingDeg * Math.PI / 180;
        var (dx, dy) = DirectionToPlane(x, y, Math.Sin(r), -Math.Cos(r), hSea);
        return Degrees(Math.Atan2(dx, dy));
    }

    private static double Degrees(double rad)
    {
        double d = rad * 180 / Math.PI;
        return d < 0 ? d + 360 : d;
    }

    private static (double A, double B, double C, double D) Inverse(double a, double b, double c, double d)
    {
        double det = a * d - b * c;
        return (d / det, -b / det, -c / det, a / det);
    }
}
