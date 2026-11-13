namespace UnitSport.Terrain.Format;

/// <summary>
/// The Earth's tiling (#1443, phase G1, docs/plans/global-tiling.md §1): a cube projected onto the
/// WGS84 ellipsoid, six faces, each a quadtree of <see cref="CubeKey"/> cells.
///
/// <para>
/// <b>Mapping.</b> Equal-angle gnomonic: a face point (u, v) in [−1, 1]² is the cube point
/// <c>axis + tan(u·π/4)·right + tan(v·π/4)·up</c> in the cube's frame; turned into ECEF by
/// <see cref="Orientation"/> and normalised, it is the <b>geodetic normal</b> (the surface's up) of
/// the point it names, so latitude and longitude follow without iteration and a height moves the
/// point along that normal. Cells are within ~1.4x in area everywhere; no pole is singular.
/// </para>
///
/// <para>
/// <b>Exactness.</b> Lattice coordinates (u = −1 + 2i/2^L) are exact doubles; the cube point's
/// components are ±1 or ±tan of them, computed on |u| so a coordinate negated across a face edge
/// gives the negated component bit for bit; the turn and the normalisation run in one fixed order
/// over the cube point. So the same lattice point reached from two faces is the same ECEF point,
/// to the last bit: neighbouring tiles share their edge vertices exactly, corners included.
/// </para>
///
/// <para>
/// <b>Orientation</b> (fixed forever: it is part of every tile's address). Face 0 is centred on
/// Bern (<see cref="CentreLatDeg"/>, <see cref="CentreLonDeg"/>: the LV95 origin), its u axis east
/// and v axis north turned by <see cref="TwistDeg"/> about its centre. The twist puts all eight cube
/// corners in the oceans, the nearest 151 km from a coast (Natural Earth 1:110m land, searched every
/// 0.1° over the 90° that are distinct): Laptev Sea, Arabian Sea, Sargasso Sea, Gulf of Guinea,
/// the central Pacific, the Indian Ocean off Australia, the south-east Pacific, the Weddell Sea.
/// <c>CubeSphereTests</c> pins them.
/// </para>
///
/// <para>
/// Face centres: 0 Bern (Europe, North Africa), 1 19.0°N 119.1°E (East Asia), 2 47.0°S 172.6°W
/// (the South Pacific, Bern's antipode), 3 19.0°S 60.9°W (South America), 4 36.9°N 136.0°W (the
/// North Pacific, North America), 5 36.9°S 44.1°E (Southern Africa, the Indian Ocean). The North
/// Pole is an ordinary point of face 0 (u 0.53, v 0.88), the South Pole of face 2.
/// Their (axis, right, up) in the cube frame: 0 (+X, +Y, +Z), 1 (+Y, −X, +Z), 2 (−X, −Y, +Z),
/// 3 (−Y, +X, +Z), 4 (+Z, +Y, −X), 5 (−Z, +Y, +X), each right-handed.
/// </para>
/// </summary>
public static class CubeSphere
{
    public const double CentreLatDeg = 46.95108, CentreLonDeg = 7.43863, TwistDeg = 28.5;

    public const int Faces = 6;

    /// <summary>The deepest level a key can have: lattice indices stay ints (a 2^30 lattice is ~1 cm).</summary>
    public const int MaxLevel = 30;

    /// <summary>Per face: the cube-frame axis index and sign of its outward axis, its right (u) and its up (v).</summary>
    private static readonly (int I, int S)[,] Frame =
    {
        { (0, 1), (1, 1), (2, 1) },
        { (1, 1), (0, -1), (2, 1) },
        { (0, -1), (1, -1), (2, 1) },
        { (1, -1), (0, 1), (2, 1) },
        { (2, 1), (1, 1), (0, -1) },
        { (2, -1), (1, 1), (0, 1) },
    };

    /// <summary>Cube frame to ECEF, row-major: column 0 is face 0's axis (up at Bern), 1 its u, 2 its v.</summary>
    private static readonly double[] M = BuildOrientation();

    /// <summary>The rotation from the cube's frame to ECEF, row-major (for tests and tools).</summary>
    public static ReadOnlySpan<double> Orientation => M;

    private static double[] BuildOrientation()
    {
        double lat = CentreLatDeg * Math.PI / 180, lon = CentreLonDeg * Math.PI / 180, t = TwistDeg * Math.PI / 180;
        double sl = Math.Sin(lat), cl = Math.Cos(lat), so = Math.Sin(lon), co = Math.Cos(lon);
        double[] up = { cl * co, cl * so, sl }, east = { -so, co, 0 }, north = { -sl * co, -sl * so, cl };
        double ct = Math.Cos(t), st = Math.Sin(t);
        var m = new double[9];
        for (int r = 0; r < 3; r++)
        {
            m[r * 3 + 0] = up[r];
            m[r * 3 + 1] = ct * east[r] + st * north[r];
            m[r * 3 + 2] = -st * east[r] + ct * north[r];
        }
        return m;
    }

    /// <summary>tan(a·π/4), odd to the bit and exactly ±1 at ±1: what makes face edges shared exactly.</summary>
    public static double Warp(double a)
    {
        double m = Math.Abs(a);
        double t = m == 1.0 ? 1.0 : Math.Tan(m * (Math.PI / 4));
        return a < 0 ? -t : t;
    }

    /// <summary>The inverse of <see cref="Warp"/>.</summary>
    public static double Unwarp(double t) => Math.Atan(t) * (4 / Math.PI);

    /// <summary>The cube point (in the cube's frame, on the unit cube) of a face's (u, v).</summary>
    public static (double X, double Y, double Z) CubePoint(int face, double u, double v)
    {
        Span<double> c = stackalloc double[3];
        var (a, r, w) = (Frame[face, 0], Frame[face, 1], Frame[face, 2]);
        c[a.I] = a.S;
        double wu = Warp(u), wv = Warp(v);
        c[r.I] = r.S > 0 ? wu : -wu;
        c[w.I] = w.S > 0 ? wv : -wv;
        return (c[0], c[1], c[2]);
    }

    /// <summary>The geodetic normal (unit, ECEF) of a face's (u, v).</summary>
    public static EcefPos Normal(int face, double u, double v)
    {
        var (x, y, z) = CubePoint(face, u, v);
        double inv = 1.0 / Math.Sqrt(x * x + y * y + z * z);
        x *= inv; y *= inv; z *= inv;
        return new(M[0] * x + M[1] * y + M[2] * z, M[3] * x + M[4] * y + M[5] * z, M[6] * x + M[7] * y + M[8] * z);
    }

    /// <summary>The ECEF point at an ellipsoidal height above a face's (u, v).</summary>
    public static EcefPos ToEcef(int face, double u, double v, double h) => OnEllipsoid(Normal(face, u, v), h);

    /// <summary>The point of WGS84 whose geodetic normal is <paramref name="n"/> (unit), raised by <paramref name="h"/> along it.</summary>
    public static EcefPos OnEllipsoid(EcefPos n, double h)
    {
        var e = Ellipsoid.Wgs84;
        double big = e.A / Math.Sqrt(1.0 - e.E2 * n.Z * n.Z);
        return new((big + h) * n.X, (big + h) * n.Y, (big * (1.0 - e.E2) + h) * n.Z);
    }

    /// <summary>Latitude and longitude (degrees) and height of a face's (u, v) at an ellipsoidal height.</summary>
    public static Geodetic ToGeodetic(int face, double u, double v, double h = 0)
    {
        var n = Normal(face, u, v);
        return new(Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI, Math.Atan2(n.Y, n.X) * 180 / Math.PI, h);
    }

    /// <summary>The face and (u, v) a geodetic normal (unit, ECEF) falls on. On an edge either face is right; the lower index wins a tie.</summary>
    public static (int Face, double U, double V) FromNormal(EcefPos n)
    {
        // into the cube frame: the transpose
        double x = M[0] * n.X + M[3] * n.Y + M[6] * n.Z;
        double y = M[1] * n.X + M[4] * n.Y + M[7] * n.Z;
        double z = M[2] * n.X + M[5] * n.Y + M[8] * n.Z;
        Span<double> c = stackalloc double[] { x, y, z };
        int best = 0;
        double bestDot = double.NegativeInfinity;
        for (int f = 0; f < Faces; f++)
        {
            var a = Frame[f, 0];
            double d = a.S * c[a.I];
            if (d > bestDot) { bestDot = d; best = f; }
        }
        var (ax, r, w) = (Frame[best, 0], Frame[best, 1], Frame[best, 2]);
        double along = ax.S * c[ax.I];
        return (best, Clamp1(Unwarp(r.S * c[r.I] / along)), Clamp1(Unwarp(w.S * c[w.I] / along)));
    }

    /// <summary>
    /// The (u, v) of a geodetic normal on a given face's gnomonic, unclamped (#1507): past the face's edge it
    /// goes on beyond ±1, where <see cref="Normal"/> takes it back, so a leaf's lattice runs on into its
    /// margin. Meaningless for a normal more than 90° from the face's axis.
    /// </summary>
    public static (double U, double V) OnFace(int face, EcefPos n)
    {
        double x = M[0] * n.X + M[3] * n.Y + M[6] * n.Z;
        double y = M[1] * n.X + M[4] * n.Y + M[7] * n.Z;
        double z = M[2] * n.X + M[5] * n.Y + M[8] * n.Z;
        Span<double> c = stackalloc double[] { x, y, z };
        var (ax, r, w) = (Frame[face, 0], Frame[face, 1], Frame[face, 2]);
        double along = ax.S * c[ax.I];
        return (Unwarp(r.S * c[r.I] / along), Unwarp(w.S * c[w.I] / along));
    }

    /// <summary>The face and (u, v) under an ECEF point, and its ellipsoidal height.</summary>
    public static (int Face, double U, double V, double H) FromEcef(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        var (f, u, v) = FromNormal(NormalAt(g.LatDeg, g.LonDeg));
        return (f, u, v, g.H);
    }

    /// <summary>The geodetic normal (unit, ECEF) at a latitude and longitude, degrees.</summary>
    public static EcefPos NormalAt(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180, cl = Math.Cos(lat);
        return new(cl * Math.Cos(lon), cl * Math.Sin(lon), Math.Sin(lat));
    }

    /// <summary>The face and (u, v) of a latitude and longitude, degrees.</summary>
    public static (int Face, double U, double V) FromLatLon(double latDeg, double lonDeg) => FromNormal(NormalAt(latDeg, lonDeg));

    /// <summary>A face's lattice coordinate: index <paramref name="i"/> of 2^<paramref name="level"/> steps across [−1, 1], exact.</summary>
    public static double Lattice(int level, long i) => -1.0 + i * (2.0 / (1L << level));

    /// <summary>The ECEF point of lattice vertex (i, j) of a face at a level (0..2^level each), at an ellipsoidal height.</summary>
    public static EcefPos LatticeEcef(int face, int level, long i, long j, double h) =>
        ToEcef(face, Lattice(level, i), Lattice(level, j), h);

    /// <summary>
    /// The face, and indices on it, of the lattice vertex (i, j) of <paramref name="face"/> at
    /// <paramref name="level"/> seen from <paramref name="other"/>: the same point on the cube, or
    /// null when <paramref name="other"/> does not hold it (only edge and corner vertices are on two
    /// or three faces). Pure integer arithmetic on the doubled cube lattice.
    /// </summary>
    public static (long I, long J)? LatticeOn(int face, int level, long i, long j, int other)
    {
        long n = 1L << level;
        Span<long> p = stackalloc long[3];
        var (a, r, w) = (Frame[face, 0], Frame[face, 1], Frame[face, 2]);
        p[a.I] = a.S * n;
        p[r.I] = r.S * (2 * i - n);
        p[w.I] = w.S * (2 * j - n);
        var (oa, or, ow) = (Frame[other, 0], Frame[other, 1], Frame[other, 2]);
        if (oa.S * p[oa.I] != n) return null;
        return ((or.S * p[or.I] + n) / 2, (ow.S * p[ow.I] + n) / 2);
    }

    /// <summary>
    /// The lattice vertex (i, j) of a face at a level, when it may lie past one of the face's edges (i or j
    /// outside 0..2^level, at most a face away): the face and indices it folds onto, the same distance in from
    /// the shared edge as it is out (<see cref="CubeKey.Neighbour"/>'s fold, on vertices; #1552). Inside the face
    /// it is itself; past a cube corner (both out), null, as only three faces meet there. Integer arithmetic on
    /// the doubled cube lattice, so the point is the other face's own vertex, bit for bit.
    /// </summary>
    public static (int Face, long I, long J)? Fold(int face, int level, long i, long j)
    {
        long n = 1L << level;
        long a = 2 * i - n, b = 2 * j - n;
        bool outA = Math.Abs(a) > n, outB = Math.Abs(b) > n;
        if (!outA && !outB) return (face, i, j);
        if (outA && outB) return null;
        if (Math.Abs(a) >= 3 * n || Math.Abs(b) >= 3 * n) throw new ArgumentOutOfRangeException(nameof(i), "at most one face away");
        var (ax, r, w) = (Frame[face, 0], Frame[face, 1], Frame[face, 2]);
        Span<long> p = stackalloc long[3];
        long e = (outA ? Math.Abs(a) : Math.Abs(b)) - n;
        p[ax.I] = ax.S * (n - e);
        p[r.I] = r.S * (outA ? Math.Sign(a) * n : a);
        p[w.I] = w.S * (outA ? b : Math.Sign(b) * n);
        for (int f = 0; f < Faces; f++)
        {
            var (fa, fr, fw) = (Frame[f, 0], Frame[f, 1], Frame[f, 2]);
            if (f == face || fa.S * p[fa.I] != n) continue;
            return (f, (fr.S * p[fr.I] + n) / 2, (fw.S * p[fw.I] + n) / 2);
        }
        throw new InvalidOperationException($"no face holds face {face}'s vertex {i},{j} at level {level}");
    }

    /// <summary>A face's frame in the cube's: the axis indices and signs of its outward axis, right and up (for <see cref="CubeKey"/>).</summary>
    internal static ((int I, int S) Axis, (int I, int S) Right, (int I, int S) Up) FaceFrame(int face) =>
        (Frame[face, 0], Frame[face, 1], Frame[face, 2]);

    private static double Clamp1(double x) => x < -1 ? -1 : x > 1 ? 1 : x;
}
