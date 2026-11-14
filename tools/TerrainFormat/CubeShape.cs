namespace UnitSport.Terrain.Format;

/// <summary>
/// A body's cube tiling (#1848, docs/plans/planets.md section 4): <see cref="CubeSphere"/>'s faces, lattice and
/// exact shared edges, laid on one body's ellipsoid in one orientation. The Earth's is <see cref="Earth"/> (WGS84,
/// face 0 on Bern twisted 28.5°: what every Earth tile's address means, fixed forever); another body's cube has
/// face 0 on its 0° N 0° E, no twist, so its poles are the centres of faces 4 (north) and 5 (south).
///
/// <para>
/// The face maths (<see cref="CubeSphere.CubePoint"/>, <see cref="CubeSphere.Lattice"/>, <see cref="CubeSphere.LatticeOn"/>,
/// <see cref="CubeSphere.Fold"/>) are the same for every body: a <see cref="CubeKey"/> names the same cell on any
/// cube, only its place differs. <see cref="CubeSphere"/>'s own statics are the Earth's, bit for bit.
/// </para>
/// </summary>
public sealed class CubeShape
{
    /// <summary>The Earth's: WGS84, face 0 on Bern twisted 28.5° (<see cref="CubeSphere"/>).</summary>
    public static readonly CubeShape Earth = new(Ellipsoid.Wgs84, CubeSphere.CentreLatDeg, CubeSphere.CentreLonDeg, CubeSphere.TwistDeg);

    /// <summary>The ellipsoid it is laid on: latitudes, longitudes and heights are on it.</summary>
    public Ellipsoid Shape { get; }

    // cube frame to the body-fixed frame, row-major: column 0 is face 0's axis, 1 its u, 2 its v
    private readonly double[] _m;

    /// <summary>The rotation from the cube's frame to the body-fixed frame, row-major.</summary>
    public ReadOnlySpan<double> Orientation => _m;

    /// <summary>A cube on <paramref name="shape"/>, face 0 centred on a latitude and longitude, its u axis east and v north turned by a twist (degrees).</summary>
    public CubeShape(Ellipsoid shape, double centreLatDeg = 0, double centreLonDeg = 0, double twistDeg = 0)
    {
        Shape = shape;
        double lat = centreLatDeg * Math.PI / 180, lon = centreLonDeg * Math.PI / 180, t = twistDeg * Math.PI / 180;
        double sl = Math.Sin(lat), cl = Math.Cos(lat), so = Math.Sin(lon), co = Math.Cos(lon);
        double[] up = { cl * co, cl * so, sl }, east = { -so, co, 0 }, north = { -sl * co, -sl * so, cl };
        double ct = Math.Cos(t), st = Math.Sin(t);
        _m = new double[9];
        for (int r = 0; r < 3; r++)
        {
            _m[r * 3 + 0] = up[r];
            _m[r * 3 + 1] = ct * east[r] + st * north[r];
            _m[r * 3 + 2] = -st * east[r] + ct * north[r];
        }
    }

    /// <summary>The surface normal (unit, body-fixed) of a face's (u, v).</summary>
    public EcefPos Normal(int face, double u, double v)
    {
        var m = _m;
        var (x, y, z) = CubeSphere.CubePoint(face, u, v);
        double inv = 1.0 / Math.Sqrt(x * x + y * y + z * z);
        x *= inv; y *= inv; z *= inv;
        return new(m[0] * x + m[1] * y + m[2] * z, m[3] * x + m[4] * y + m[5] * z, m[6] * x + m[7] * y + m[8] * z);
    }

    /// <summary>The body-fixed point at a height above a face's (u, v).</summary>
    public EcefPos ToEcef(int face, double u, double v, double h) => OnEllipsoid(Normal(face, u, v), h);

    /// <summary>The point of the ellipsoid whose normal is <paramref name="n"/> (unit), raised by <paramref name="h"/> along it.</summary>
    public EcefPos OnEllipsoid(EcefPos n, double h)
    {
        var e = Shape;
        double big = e.A / Math.Sqrt(1.0 - e.E2 * n.Z * n.Z);
        return new((big + h) * n.X, (big + h) * n.Y, (big * (1.0 - e.E2) + h) * n.Z);
    }

    /// <summary>Latitude and longitude (degrees) and height of a face's (u, v) at a height.</summary>
    public Geodetic ToGeodetic(int face, double u, double v, double h = 0)
    {
        var n = Normal(face, u, v);
        return new(Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI, Math.Atan2(n.Y, n.X) * 180 / Math.PI, h);
    }

    /// <summary>The face and (u, v) a normal (unit, body-fixed) falls on. On an edge either face is right; the lower index wins a tie.</summary>
    public (int Face, double U, double V) FromNormal(EcefPos n)
    {
        var m = _m;
        double x = m[0] * n.X + m[3] * n.Y + m[6] * n.Z;
        double y = m[1] * n.X + m[4] * n.Y + m[7] * n.Z;
        double z = m[2] * n.X + m[5] * n.Y + m[8] * n.Z;
        Span<double> c = stackalloc double[] { x, y, z };
        int best = 0;
        double bestDot = double.NegativeInfinity;
        for (int f = 0; f < CubeSphere.Faces; f++)
        {
            var a = CubeSphere.FaceFrame(f).Axis;
            double d = a.S * c[a.I];
            if (d > bestDot) { bestDot = d; best = f; }
        }
        var (ax, r, w) = CubeSphere.FaceFrame(best);
        double along = ax.S * c[ax.I];
        return (best, Clamp1(CubeSphere.Unwarp(r.S * c[r.I] / along)), Clamp1(CubeSphere.Unwarp(w.S * c[w.I] / along)));
    }

    /// <summary>The (u, v) of a normal on a given face's gnomonic, unclamped (see <see cref="CubeSphere.OnFace"/>).</summary>
    public (double U, double V) OnFace(int face, EcefPos n)
    {
        var m = _m;
        double x = m[0] * n.X + m[3] * n.Y + m[6] * n.Z;
        double y = m[1] * n.X + m[4] * n.Y + m[7] * n.Z;
        double z = m[2] * n.X + m[5] * n.Y + m[8] * n.Z;
        Span<double> c = stackalloc double[] { x, y, z };
        var (ax, r, w) = CubeSphere.FaceFrame(face);
        double along = ax.S * c[ax.I];
        return (CubeSphere.Unwarp(r.S * c[r.I] / along), CubeSphere.Unwarp(w.S * c[w.I] / along));
    }

    /// <summary>The face and (u, v) under a body-fixed point, and its height above the ellipsoid.</summary>
    public (int Face, double U, double V, double H) FromEcef(EcefPos p)
    {
        var g = Shape.ToGeodetic(p);
        var (f, u, v) = FromNormal(CubeSphere.NormalAt(g.LatDeg, g.LonDeg));
        return (f, u, v, g.H);
    }

    /// <summary>The face and (u, v) of a latitude and longitude, degrees.</summary>
    public (int Face, double U, double V) FromLatLon(double latDeg, double lonDeg) => FromNormal(CubeSphere.NormalAt(latDeg, lonDeg));

    /// <summary>The body-fixed point of lattice vertex (i, j) of a face at a level (0..2^level each), at a height.</summary>
    public EcefPos LatticeEcef(int face, int level, long i, long j, double h) =>
        ToEcef(face, CubeSphere.Lattice(level, i), CubeSphere.Lattice(level, j), h);

    /// <summary>The east-north-up frame at a latitude, longitude and height on this cube's ellipsoid.</summary>
    public EnuFrame Enu(Geodetic at) => new(at, Shape);

    private static double Clamp1(double x) => x < -1 ? -1 : x > 1 ? 1 : x;
}
