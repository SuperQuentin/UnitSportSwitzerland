using Godot;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// Pattern space on the round world (#1512, G2 C3): where a surface pattern (asphalt, roof tiles, house hashes,
/// waves) is drawn from, fixed to the Earth instead of to the world frame. A point's pattern point is its cube
/// face's (u, v) in metres (<see cref="MetresPerUnit"/>; X along u, Z along −v, so a face's pattern axes turn
/// as x east and z south do): the same for every leaf and every frame, so a turning re-anchor moves no pattern.
/// The flat world keeps <c>world_origin_offset</c> (its patterns are LV95 modulo 9.6 km) until D.
///
/// <para>
/// <b>Face edges.</b> Within <see cref="BandM"/> of a face edge a point carries the pattern points of both faces
/// (A the lower face index, B the higher) and B's weight, rising smoothly across the edge; the shader picks one
/// per fragment by a noise on A's point (a ragged seam, never a straight line of cut patterns), and the waves mix
/// the two fields' displacements. Near a cube corner (all eight are in the oceans) only the two nearest faces
/// blend.
/// </para>
///
/// <para>
/// <b>Directions.</b> Waves and ripples move along pattern axes; <see cref="AngleIn"/> gives the angle of the
/// pattern X axis in a frame's horizontal (from x towards z), and pattern Z is X turned by +90°. The cube's
/// equal-angle faces are close to conformal near their centres (Switzerland is on face 0, by Bern); further out
/// the pattern is sheared a little, the same for the drawn surface and the physics.
/// </para>
/// </summary>
public static class PatternSpace
{
    /// <summary>Metres per face unit: a unit of u is 45° of arc, at the Earth's mean radius.</summary>
    public const double MetresPerUnit = Math.PI / 4 * 6_371_000;

    /// <summary>The patterns' common period, <c>OriginShifter.PatternPeriodM</c>'s: every pattern repeats in it.</summary>
    public const double PeriodM = 9600;

    /// <summary>Half the width of the blend across a face edge, metres.</summary>
    public const double BandM = 60;

    /// <summary>Two faces blended across their shared edge: B lies past A's edge where <c>Sign·(A's coordinate Axis) = 1</c>.</summary>
    public readonly record struct Pair(int A, int B, int Axis, int Sign)
    {
        public bool Single => A == B;

        public static Pair Of(int face) => new(face, face, 0, 0);
    }

    /// <summary>The geodetic normal of an ECEF point.</summary>
    public static EcefPos NormalOf(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        return CubeSphere.NormalAt(g.LatDeg, g.LonDeg);
    }

    /// <summary>A face's pattern point of a geodetic normal (past the face's edge too), metres.</summary>
    public static (double X, double Z) OnFace(int face, EcefPos normal)
    {
        var (u, v) = CubeSphere.OnFace(face, normal);
        return (u * MetresPerUnit, -v * MetresPerUnit);
    }

    /// <summary>
    /// The faces a normal's pattern comes from: its own alone, or, within <paramref name="margin"/> metres of a face
    /// edge, the two faces of that edge.
    /// </summary>
    public static Pair PairAt(EcefPos normal, double margin = BandM)
    {
        var (f, u, v) = CubeSphere.FromNormal(normal);
        double m = Math.Max(Math.Abs(u), Math.Abs(v));
        if ((1 - m) * MetresPerUnit > margin) return Pair.Of(f);
        int dx = Math.Abs(u) >= Math.Abs(v) ? Math.Sign(u) : 0, dy = dx == 0 ? Math.Sign(v) : 0;
        if (new CubeKey(f, 0, 0, 0).Neighbour(dx, dy) is not { } across) return Pair.Of(f);
        int a = Math.Min(f, across.Face), b = Math.Max(f, across.Face);
        // A's edge towards B: the coordinate of the normal on A that runs out (past ±1 on B's side)
        var (ua, va) = CubeSphere.OnFace(a, normal);
        return Math.Abs(ua) >= Math.Abs(va) ? new Pair(a, b, 0, Math.Sign(ua)) : new Pair(a, b, 1, Math.Sign(va));
    }

    /// <summary>What a single face's <see cref="PastEdge"/> is: far inside A.</summary>
    public const double FarInside = -100_000;

    /// <summary>How far a normal lies past A's edge towards B, metres (negative inside A); <see cref="FarInside"/> for one face.</summary>
    public static double PastEdge(Pair pair, EcefPos normal)
    {
        if (pair.Single) return FarInside;
        var (u, v) = CubeSphere.OnFace(pair.A, normal);
        return (pair.Sign * (pair.Axis == 0 ? u : v) - 1) * MetresPerUnit;
    }

    /// <summary>
    /// B's weight at a distance past A's edge, 0..1: ½ on the edge, 0 at <see cref="BandM"/> inside A, 1 as far
    /// inside B (the shader's <c>smoothstep</c>).
    /// </summary>
    public static float WeightAt(double past)
    {
        double t = Math.Clamp((past + BandM) / (2 * BandM), 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>B's weight at a normal.</summary>
    public static float WeightB(Pair pair, EcefPos normal) => WeightAt(PastEdge(pair, normal));

    /// <summary>The direction of a face's pattern X axis (its u) at a normal, ECEF, unit.</summary>
    public static EcefPos AxisX(int face, EcefPos normal)
    {
        var (u, v) = CubeSphere.OnFace(face, normal);
        const double d = 1e-7;
        var a = CubeSphere.Normal(face, u - d, v);
        var b = CubeSphere.Normal(face, u + d, v);
        double x = b.X - a.X, y = b.Y - a.Y, z = b.Z - a.Z, l = Math.Sqrt(x * x + y * y + z * z);
        return new(x / l, y / l, z / l);
    }

    /// <summary>The angle of an ECEF direction in a frame's horizontal, radians from its x axis towards its z axis.</summary>
    public static double AngleIn(EcefPos dir, EcefPos frameX, EcefPos frameZ) =>
        Math.Atan2(Dot(dir, frameZ), Dot(dir, frameX));

    /// <summary>A pattern-space vector (X, Z) turned into the frame whose pattern X axis lies at <paramref name="angle"/>.</summary>
    public static (double X, double Z) Turn(double x, double z, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return (x * c - z * s, x * s + z * c);
    }

    /// <summary>A value brought into [0, <see cref="PeriodM"/>).</summary>
    public static double Wrap(double v) => v - Math.Floor(v / PeriodM) * PeriodM;

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

/// <summary>
/// A leaf's pattern points baked into its furniture's meshes (#1512): <c>CUSTOM1</c> = (A's X, A's Z, B's X, B's Z)
/// in metres, <c>CUSTOM2</c> = (1, A's axis angle, B's axis angle in the leaf's stand-in frame, metres past A's edge
/// towards B: <see cref="PatternSpace.PastEdge"/>, which interpolates where a weight would not)
/// (<c>shaders/world_origin.gdshaderinc</c> reads them; a mesh without them is the flat world's and reads
/// <c>world_origin_offset</c>). Exact on a 9 × 9 grid over the stand-in square, at height 0 and per metre of height,
/// and bilinear between: under a millimetre from <see cref="PatternSpace"/> at any vertex (<c>LeafPatternTests</c>).
/// Each face's points are brought near [0, 9.6 km) by one whole number of periods for the whole leaf, so floats
/// keep them to the millimetre and a triangle never straddles a wrap; two leaves may differ by whole periods,
/// which no pattern sees.
/// </summary>
public sealed class LeafPattern
{
    private const int N = 9;
    private const float Span = 1000f, Step = Span / (N - 1), Rise = 1000f;

    // per grid node: 7 values at height 0, then their change per metre of height
    private readonly float[] _at = new float[N * N * 7], _perM = new float[N * N * 7];

    public PatternSpace.Pair Pair { get; }

    private LeafPattern(PatternSpace.Pair pair) => Pair = pair;

    /// <summary>A leaf's pattern in its stand-in tile's frame (<see cref="LeafStandIn"/>).</summary>
    public static LeafPattern For(CubeKey leaf)
    {
        var anchor = leaf.Anchor;
        double off = LeafStandIn.Offset;
        var centre = PatternSpace.NormalOf(anchor.Origin);
        // a leaf within the band plus its own size of an edge blends: every vertex then gets both faces
        var p = new LeafPattern(PatternSpace.PairAt(centre, PatternSpace.BandM + Span));
        var frameX = anchor.East;
        var frameZ = new EcefPos(-anchor.North.X, -anchor.North.Y, -anchor.North.Z);

        var (cax, caz) = PatternSpace.OnFace(p.Pair.A, centre);
        var (cbx, cbz) = PatternSpace.OnFace(p.Pair.B, centre);
        double bax = cax - PatternSpace.Wrap(cax), baz = caz - PatternSpace.Wrap(caz);
        double bbx = cbx - PatternSpace.Wrap(cbx), bbz = cbz - PatternSpace.Wrap(cbz);
        double angA0 = PatternSpace.AngleIn(PatternSpace.AxisX(p.Pair.A, centre), frameX, frameZ);
        double angB0 = PatternSpace.AngleIn(PatternSpace.AxisX(p.Pair.B, centre), frameX, frameZ);

        Span<double> lo = stackalloc double[7], hi = stackalloc double[7];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                double x = i * Step - off, z = j * Step - off;
                Exact(anchor.ToEcef(x, 0, z), lo);
                Exact(anchor.ToEcef(x, Rise, z), hi);
                int k = (j * N + i) * 7;
                for (int c = 0; c < 7; c++)
                {
                    p._at[k + c] = (float)lo[c];
                    p._perM[k + c] = (float)((hi[c] - lo[c]) / Rise);
                }
            }
        return p;

        void Exact(EcefPos at, Span<double> v)
        {
            var n = PatternSpace.NormalOf(at);
            var (ax, az) = PatternSpace.OnFace(p.Pair.A, n);
            var (bx, bz) = PatternSpace.OnFace(p.Pair.B, n);
            v[0] = ax - bax; v[1] = az - baz; v[2] = bx - bbx; v[3] = bz - bbz;
            v[4] = PatternSpace.PastEdge(p.Pair, n);
            v[5] = Unwrap(PatternSpace.AngleIn(PatternSpace.AxisX(p.Pair.A, n), frameX, frameZ), angA0);
            v[6] = Unwrap(PatternSpace.AngleIn(PatternSpace.AxisX(p.Pair.B, n), frameX, frameZ), angB0);
        }
    }

    private static double Unwrap(double a, double near) => a + Math.Round((near - a) / Math.Tau) * Math.Tau;

    /// <summary>The seven values at a stand-in point: pattern A (X, Z), B (X, Z), metres past A's edge, A's angle, B's angle.</summary>
    public void At(Vector3 local, Span<float> v)
    {
        float fx = Math.Clamp(local.X / Step, 0f, N - 1.001f), fz = Math.Clamp(local.Z / Step, 0f, N - 1.001f);
        int i = (int)fx, j = (int)fz;
        // past the square (a mesh's few metres of overhang) the cell's plane carries on
        float tx = local.X / Step - i, tz = local.Z / Step - j;
        int k00 = (j * N + i) * 7, k10 = k00 + 7, k01 = k00 + N * 7, k11 = k01 + 7;
        for (int c = 0; c < 7; c++)
        {
            float a = Lerp(_at, _perM, k00 + c, local.Y), b = Lerp(_at, _perM, k10 + c, local.Y);
            float d = Lerp(_at, _perM, k01 + c, local.Y), e = Lerp(_at, _perM, k11 + c, local.Y);
            v[c] = (a + (b - a) * tx) + ((d + (e - d) * tx) - (a + (b - a) * tx)) * tz;
        }
    }

    private static float Lerp(float[] at, float[] perM, int k, float y) => at[k] + perM[k] * y;

    /// <summary>The two custom channels for a mesh's vertices (RGBA float each).</summary>
    public (float[] Custom1, float[] Custom2) Bake(Vector3[] vertices)
    {
        var c1 = new float[vertices.Length * 4];
        var c2 = new float[vertices.Length * 4];
        Span<float> v = stackalloc float[7];
        for (int i = 0; i < vertices.Length; i++)
        {
            At(vertices[i], v);
            int o = i * 4;
            c1[o] = v[0]; c1[o + 1] = v[1]; c1[o + 2] = v[2]; c1[o + 3] = v[3];
            c2[o] = 1f; c2[o + 1] = v[5]; c2[o + 2] = v[6]; c2[o + 3] = v[4];
        }
        return (c1, c2);
    }

    /// <summary>The mesh format flags for the two channels, to add to a surface's.</summary>
    public const Mesh.ArrayFormat Flags =
        (Mesh.ArrayFormat)(((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
                           | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift));

    /// <summary>Adds the channels to a surface's arrays; returns the format flags to add.</summary>
    public Mesh.ArrayFormat Into(Godot.Collections.Array arrays)
    {
        var (c1, c2) = Bake(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array());
        arrays[(int)Mesh.ArrayType.Custom1] = c1;
        arrays[(int)Mesh.ArrayType.Custom2] = c2;
        return Flags;
    }
}
