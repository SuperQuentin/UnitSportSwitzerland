namespace UnitSport.Terrain.Format;

/// <summary>
/// How a work frame lays the curved Earth flat (#1443 G2, B2 #1506): a place's latitude and longitude to
/// metres east (X) and north (Y) on a plane, and back. Its axes are east and true north at the frame's
/// centre. Three are built so they can be compared (<c>WorkPlaneComparisonTests</c>); the user picks one.
/// </summary>
public interface IWorkPlane
{
    string Name { get; }

    (double X, double Y) ToPlane(double latDeg, double lonDeg);

    (double LatDeg, double LonDeg) FromPlane(double x, double y);
}

/// <summary>
/// The ellipsoid seen straight down onto the tangent plane at the centre: a place's plane point is its
/// point on the ellipsoid, (s − O)·east and (s − O)·north. The inverse is closed form (the line along the
/// centre's up meets the ellipsoid). Not conformal: the scale falls off radially by d²/2R².
/// </summary>
public sealed class OrthographicPlane(EnuFrame centre) : IWorkPlane
{
    public string Name => "orthographic";

    public (double X, double Y) ToPlane(double latDeg, double lonDeg)
    {
        var s = Ellipsoid.Wgs84.ToEcef(latDeg * Math.PI / 180, lonDeg * Math.PI / 180, 0);
        double dx = s.X - centre.Origin.X, dy = s.Y - centre.Origin.Y, dz = s.Z - centre.Origin.Z;
        return (dx * centre.East.X + dy * centre.East.Y + dz * centre.East.Z,
            dx * centre.North.X + dy * centre.North.Y + dz * centre.North.Z);
    }

    public (double LatDeg, double LonDeg) FromPlane(double x, double y)
    {
        var (o, e, n, u) = (centre.Origin, centre.East, centre.North, centre.Up);
        double px = o.X + e.X * x + n.X * y, py = o.Y + e.Y * x + n.Y * y, pz = o.Z + e.Z * x + n.Z * y;
        // (p + t·up) on the ellipsoid: a·t² + b·t + c = 0 in the metric diag(1/a², 1/a², 1/b²); the small root
        var ell = Ellipsoid.Wgs84;
        double ia = 1 / (ell.A * ell.A), ib = 1 / (ell.B * ell.B);
        double qa = (u.X * u.X + u.Y * u.Y) * ia + u.Z * u.Z * ib;
        double qb = 2 * ((px * u.X + py * u.Y) * ia + pz * u.Z * ib);
        double qc = (px * px + py * py) * ia + pz * pz * ib - 1;
        double t = -2 * qc / (qb + Math.Sqrt(qb * qb - 4 * qa * qc));
        double sx = px + u.X * t, sy = py + u.Y * t, sz = pz + u.Z * t;
        // the geodetic normal there
        double nx = sx * ia, ny = sy * ia, nz = sz * ib;
        return (Math.Atan2(nz, Math.Sqrt(nx * nx + ny * ny)) * 180 / Math.PI, Math.Atan2(ny, nx) * 180 / Math.PI);
    }
}

/// <summary>
/// The oblique stereographic projection of the ellipsoid (EPSG method 9809, the "double" stereographic:
/// Gauss's conformal sphere, then stereographic), centred on the frame, scale 1 at the centre. Conformal:
/// angles are exact, the scale falls by d²/4R². Written with vectors on the conformal sphere rather than
/// EPSG's angle formulas, which divide by zero for a frame on a pole, and with no 1 − sin near the poles.
/// </summary>
public sealed class StereographicPlane : IWorkPlane
{
    private readonly double _e, _r, _n, _c, _lam0;
    // the centre on the conformal sphere and its east and north, its longitude taken as 0
    private readonly double _cx, _cz, _nx, _nz;

    public StereographicPlane(double latDeg, double lonDeg)
    {
        var ell = Ellipsoid.Wgs84;
        double e2 = ell.E2;
        _e = Math.Sqrt(e2);
        double phi0 = latDeg * Math.PI / 180, s0 = Math.Sin(phi0), c0 = Math.Cos(phi0);
        double rho0 = ell.A * (1 - e2) / Math.Pow(1 - e2 * s0 * s0, 1.5);
        double nu0 = ell.A / Math.Sqrt(1 - e2 * s0 * s0);
        _r = Math.Sqrt(rho0 * nu0);
        _n = Math.Sqrt(1 + e2 * Math.Pow(c0, 4) / (1 - e2));
        double w1 = Isometric(phi0, _n);
        double sinChi00 = (w1 - 1) / (w1 + 1);
        _c = (_n + s0) * (1 - sinChi00) / ((_n - s0) * (1 + sinChi00));
        if (!double.IsFinite(_c)) _c = 1;   // on a pole the sphere's latitude is the pole's too
        (double sinChi0, double cosChi0) = Conformal(phi0);
        (_cx, _cz) = (cosChi0, sinChi0);
        (_nx, _nz) = (-sinChi0, cosChi0);
        _lam0 = lonDeg * Math.PI / 180;
    }

    public string Name => "stereographic";

    /// <summary>((1 + sin φ)/(1 − sin φ) · ((1 − e sin φ)/(1 + e sin φ))^e)^n, written with cos φ (no 1 − sin φ).</summary>
    private double Isometric(double phi, double n)
    {
        double s = Math.Sin(phi), c = Math.Cos(phi);
        double sa = (1 + s) / c;
        return Math.Pow(sa * sa * Math.Pow((1 - _e * s) / (1 + _e * s), _e), n);
    }

    /// <summary>The conformal sphere's sine and cosine of latitude for a geodetic latitude.</summary>
    private (double Sin, double Cos) Conformal(double phi)
    {
        if (Math.Abs(phi) >= Math.PI / 2) return (Math.Sign(phi), 0);
        double w = _c * Isometric(phi, _n);
        if (double.IsInfinity(w)) return (1, 0);
        return ((w - 1) / (w + 1), 2 * Math.Sqrt(w) / (w + 1));
    }

    public (double X, double Y) ToPlane(double latDeg, double lonDeg)
    {
        var (sc, cc) = Conformal(latDeg * Math.PI / 180);
        double dl = _n * Wrap(lonDeg * Math.PI / 180 - _lam0);
        double vx = cc * Math.Cos(dl), vy = cc * Math.Sin(dl), vz = sc;
        double b = 1 + vx * _cx + vz * _cz;
        return (2 * _r * vy / b, 2 * _r * (vx * _nx + vz * _nz) / b);
    }

    public (double LatDeg, double LonDeg) FromPlane(double x, double y)
    {
        double a = x / (2 * _r), b = y / (2 * _r), q = a * a + b * b;
        double vx = ((1 - q) * _cx + 2 * b * _nx) / (1 + q);
        double vy = 2 * a / (1 + q);
        double vz = ((1 - q) * _cz + 2 * b * _nz) / (1 + q);
        double cc = Math.Sqrt(vx * vx + vy * vy);
        double lam = Math.Atan2(vy, vx) / _n + _lam0;
        // ψ = ln(tan(π/4 + χ/2)) / n on the sphere, written with cos χ; then the ellipsoid's latitude for it
        double psi = Math.Log((1 + vz) / cc / Math.Sqrt(_c)) / _n;
        if (!double.IsFinite(psi)) return (Math.Sign(vz) * 90, Wrap(lam) * 180 / Math.PI);
        double phi = 2 * Math.Atan(Math.Exp(psi)) - Math.PI / 2;
        double e2 = _e * _e;
        for (int k = 0; k < 30; k++)
        {
            double s = Math.Sin(phi), c = Math.Cos(phi);
            double psiI = Math.Log((1 + s) / c * Math.Pow((1 - _e * s) / (1 + _e * s), _e / 2));
            double next = phi - (psiI - psi) * c * (1 - e2 * s * s) / (1 - e2);
            if (Math.Abs(next - phi) < 1e-14) { phi = next; break; }
            phi = next;
        }
        return (phi * 180 / Math.PI, Wrap(lam) * 180 / Math.PI);
    }

    private static double Wrap(double a) => Math.IEEERemainder(a, 2 * Math.PI);
}

/// <summary>
/// The cube's own lattice as the plane: a face's (u, v) turned into metres by the surface's Jacobian at the
/// cell's centre (east, north there). The leaves' rasters fall on it exactly, but the lattice is skewed near
/// the cube's corners (up to 30°) and its scale drifts with the gnomonic warp across the frame.
/// </summary>
public sealed class LatticePlane : IWorkPlane
{
    private readonly int _face;
    private readonly double _u0, _v0, _a, _b, _c, _d, _ia, _ib, _ic, _id;

    public LatticePlane(CubeKey cell)
    {
        _face = cell.Face;
        _u0 = cell.U0 + cell.Size / 2;
        _v0 = cell.V0 + cell.Size / 2;
        var anchor = new EnuFrame(CubeSphere.ToGeodetic(_face, _u0, _v0));
        double step = cell.Size * 1e-4;
        (double E, double N) Along(double du, double dv)
        {
            var p = CubeSphere.ToEcef(_face, _u0 + du, _v0 + dv, 0);
            var m = CubeSphere.ToEcef(_face, _u0 - du, _v0 - dv, 0);
            var d = new EcefPos((p.X - m.X) / (2 * step), (p.Y - m.Y) / (2 * step), (p.Z - m.Z) / (2 * step));
            var (x, _, z) = anchor.DirectionToLocal(d);
            return (x, -z);
        }
        (_a, _c) = Along(step, 0);
        (_b, _d) = Along(0, step);
        double det = _a * _d - _b * _c;
        (_ia, _ib, _ic, _id) = (_d / det, -_b / det, -_c / det, _a / det);
    }

    public string Name => "lattice";

    public (double X, double Y) ToPlane(double latDeg, double lonDeg)
    {
        var n = CubeSphere.NormalAt(latDeg, lonDeg);
        var (u, v) = CubeSphere.OnFace(_face, n);
        double du = u - _u0, dv = v - _v0;
        return (_a * du + _b * dv, _c * du + _d * dv);
    }

    public (double LatDeg, double LonDeg) FromPlane(double x, double y)
    {
        double u = _u0 + _ia * x + _ib * y, v = _v0 + _ic * x + _id * y;
        var g = CubeSphere.ToGeodetic(_face, u, v);
        return (g.LatDeg, g.LonDeg);
    }
}

/// <summary>
/// The flat frame a build stage works in (#1443 G2, B2 #1506): one level-9 cube cell (~20 km), metres east
/// and north on its <see cref="IWorkPlane"/>, heights above its <see cref="ISeaLevel"/>. Every source enters
/// through <see cref="FromSource"/>, and every result leaves through <see cref="ToEcef"/> into the leaves.
/// </summary>
public sealed class WorkFrame
{
    /// <summary>The level of the cube cells work frames are made for: 32 × 32 leaves, ~20 km.</summary>
    public const int Level = 9;

    public CubeKey Cell { get; }

    public IWorkPlane Plane { get; }

    public ISeaLevel SeaLevel { get; }

    public WorkFrame(CubeKey cell, IWorkPlane plane, ISeaLevel seaLevel)
    {
        Cell = cell;
        Plane = plane;
        SeaLevel = seaLevel;
    }

    /// <summary>A plane point and its height above sea level as ECEF.</summary>
    public EcefPos ToEcef(double x, double y, double hSea)
    {
        var (lat, lon) = Plane.FromPlane(x, y);
        return Ellipsoid.Wgs84.ToEcef(lat * Math.PI / 180, lon * Math.PI / 180, hSea + SeaLevel.Undulation(lat, lon));
    }

    /// <summary>The inverse of <see cref="ToEcef"/>.</summary>
    public (double X, double Y, double HSea) FromEcef(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        var (x, y) = Plane.ToPlane(g.LatDeg, g.LonDeg);
        return (x, y, g.H - SeaLevel.Undulation(g.LatDeg, g.LonDeg));
    }

    /// <summary>A source's point and its height above the source's sea level, in this frame.</summary>
    public (double X, double Y, double HSea) FromSource(ISourceProjection source, double a, double b, double h)
    {
        if (source is Lv95Source) return FromEcef(SwissGeodesy.ToEcef(a, b, h));
        var (lat, lon) = source.ToLatLon(a, b);
        double hEll = h + source.SeaLevel.Undulation(lat, lon);
        return FromEcef(Ellipsoid.Wgs84.ToEcef(lat * Math.PI / 180, lon * Math.PI / 180, hEll));
    }
}
