using System.Globalization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// A cell of the cube sphere's quadtrees (#1443, G1; <see cref="CubeSphere"/>): face 0..5, level
/// (0 = the whole face), and x along the face's u axis, y along its v axis, each 0..2^level − 1
/// (y = 0 at v = −1). Printed <c>F0/L13/4123_4091</c>; <see cref="Describe"/> adds the centre's
/// latitude and longitude, which every tool's output carries.
/// </summary>
public readonly record struct CubeKey(int Face, int Level, int X, int Y)
{
    /// <summary>Cells a side at this level.</summary>
    public int Side => 1 << Level;

    /// <summary>The finest lattice any level carries, as log2 of its cells a side: 9 (the level-14 leaves).</summary>
    public const int MaxBits = 9;

    /// <summary>
    /// log2 of the lattice cells a side a tile at <paramref name="level"/> carries (#1430 H4, agreed on
    /// #1455): ~1.2 m at a face centre down to the leaves (23 − level), and at most 512 cells a side
    /// above them, so the coarse pyramid holds (level 0 is 19.6 km a cell, level 4 1.2 km: ETOPO
    /// 2022's own spacing). Every coarse lattice point is a leaf lattice point, so edges stay exact.
    /// The one source every writer and reader takes the lattice from.
    /// </summary>
    public static int LatticeBits(int level) => Math.Min(23 - level, MaxBits);

    /// <summary>
    /// A level's height quantum, metres: a thousandth of its lattice step, 1 cm to 1 m (1 cm at the
    /// leaves, 1 m at level 4's 1.2 km cells), so a coarse tile does not spend bits on centimetres
    /// nobody sees. One value a level, so shared edges quantise alike.
    /// </summary>
    public static float QuantumFor(int level) => (float)Math.Clamp(1.22 * Math.Pow(2, 23 - level - LatticeBits(level)) / 1000, 0.01, 1.0);

    /// <summary>Where a pyramid level's tiles are bundled: levels 0-3 together under their face (85 tiles), then three levels up (64 a bundle).</summary>
    public static int PyramidBundleLevel(int level) => Math.Max(0, level - 3);

    /// <summary>The cell's west (u) and south (v) edges and its size in face coordinates, exact.</summary>
    public double U0 => CubeSphere.Lattice(Level, X);

    public double V0 => CubeSphere.Lattice(Level, Y);

    public double Size => 2.0 / Side;

    public bool IsValid => (uint)Face < CubeSphere.Faces && (uint)Level <= CubeSphere.MaxLevel
        && (uint)X < (uint)Side && (uint)Y < (uint)Side;

    /// <summary>The cell's centre on the ellipsoid.</summary>
    public Geodetic Centre => CubeSphere.ToGeodetic(Face, CubeSphere.Lattice(Level + 1, 2L * X + 1), CubeSphere.Lattice(Level + 1, 2L * Y + 1));

    /// <summary>
    /// The tile's anchor: the east-north-up frame at its centre on the ellipsoid (height 0). Every
    /// position a tile holds is relative to it, in floats (within ~1 km the float step is under 0.1 mm).
    /// </summary>
    public EnuFrame Anchor => new(Centre);

    /// <summary>The ECEF point of the cell's lattice vertex (i, j) when it is cut into 2^<paramref name="bits"/> cells a side (0..2^bits each), at an ellipsoidal height.</summary>
    public EcefPos Vertex(int bits, int i, int j, double h) =>
        CubeSphere.LatticeEcef(Face, Level + bits, ((long)X << bits) + i, ((long)Y << bits) + j, h);

    /// <summary>The cell holding a point, at a level.</summary>
    public static CubeKey FromEcef(EcefPos p, int level)
    {
        var (f, u, v, _) = CubeSphere.FromEcef(p);
        return FromFace(f, u, v, level);
    }

    /// <summary>The cell's centre on another body's cube (#1848).</summary>
    public Geodetic CentreOn(CubeShape cube) =>
        cube.ToGeodetic(Face, CubeSphere.Lattice(Level + 1, 2L * X + 1), CubeSphere.Lattice(Level + 1, 2L * Y + 1));

    /// <summary>The cell's anchor on another body's cube (#1848): the east-north-up frame at its centre there.</summary>
    public EnuFrame AnchorOn(CubeShape cube) => cube.Enu(CentreOn(cube));

    /// <summary>Lattice vertex (i, j) of the cell cut into 2^<paramref name="bits"/> cells, on another body's cube (#1848), body-fixed.</summary>
    public EcefPos VertexOn(CubeShape cube, int bits, int i, int j, double h) =>
        cube.LatticeEcef(Face, Level + bits, ((long)X << bits) + i, ((long)Y << bits) + j, h);

    /// <summary>The cell holding a body-fixed point of another body's cube (#1848), at a level.</summary>
    public static CubeKey FromEcef(CubeShape cube, EcefPos p, int level)
    {
        var (f, u, v, _) = cube.FromEcef(p);
        return FromFace(f, u, v, level);
    }

    /// <summary>The cell holding a latitude and longitude (degrees), at a level.</summary>
    public static CubeKey FromLatLon(double latDeg, double lonDeg, int level)
    {
        var (f, u, v) = CubeSphere.FromLatLon(latDeg, lonDeg);
        return FromFace(f, u, v, level);
    }

    public static CubeKey FromFace(int face, double u, double v, int level)
    {
        int n = 1 << level;
        return new(face, level, Index(u, n), Index(v, n));
    }

    private static int Index(double t, int n) => Math.Clamp((int)Math.Floor((t + 1) * 0.5 * n), 0, n - 1);

    public CubeKey Parent => Level == 0 ? this : new(Face, Level - 1, X >> 1, Y >> 1);

    /// <summary>The ancestor at a level at or above this one.</summary>
    public CubeKey AncestorAt(int level) => new(Face, level, X >> (Level - level), Y >> (Level - level));

    /// <summary>The four children, south-west first, then south-east, north-west, north-east.</summary>
    public IEnumerable<CubeKey> Children()
    {
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                yield return new(Face, Level + 1, 2 * X + dx, 2 * Y + dy);
    }

    /// <summary>
    /// The cell <paramref name="dx"/> along this face's u axis and <paramref name="dy"/> along its v
    /// axis (at most a face away), across face edges: the cells there are the other face's, turned as
    /// that face is. Null past a cube corner, where only three cells meet and the diagonal one does not
    /// exist. Integer arithmetic on the doubled lattice (cell centres at odd coordinates, face edges
    /// at ±2^level): an overflow past an edge folds onto the next face at the same distance in.
    /// </summary>
    public CubeKey? Neighbour(int dx, int dy)
    {
        long n = Side;
        long a = 2L * X + 1 - n + 2L * dx, b = 2L * Y + 1 - n + 2L * dy;
        bool outA = Math.Abs(a) > n, outB = Math.Abs(b) > n;
        if (!outA && !outB) return new(Face, Level, (int)((a + n - 1) / 2), (int)((b + n - 1) / 2));
        if (outA && outB) return null;
        if (Math.Abs(a) >= 3 * n || Math.Abs(b) >= 3 * n) throw new ArgumentOutOfRangeException(nameof(dx), "at most one face away");

        var (ax, r, w) = CubeSphere.FaceFrame(Face);
        Span<long> p = stackalloc long[3];
        if (outA)
        {
            long e = Math.Abs(a) - n;
            p[ax.I] = ax.S * (n - e);
            p[r.I] = r.S * Math.Sign(a) * n;
            p[w.I] = w.S * b;
        }
        else
        {
            long e = Math.Abs(b) - n;
            p[ax.I] = ax.S * (n - e);
            p[r.I] = r.S * a;
            p[w.I] = w.S * Math.Sign(b) * n;
        }
        for (int f = 0; f < CubeSphere.Faces; f++)
        {
            var (fa, fr, fw) = CubeSphere.FaceFrame(f);
            if (fa.S * p[fa.I] != n) continue;
            return new(f, Level, (int)((fr.S * p[fr.I] + n - 1) / 2), (int)((fw.S * p[fw.I] + n - 1) / 2));
        }
        throw new InvalidOperationException($"no face holds the neighbour of {this} at {dx},{dy}");
    }

    /// <summary>The cells round this one: eight, or seven at a cube corner (where three cells meet).</summary>
    public List<CubeKey> Neighbours()
    {
        var list = new List<CubeKey>(8);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && Neighbour(dx, dy) is { } k)
                    list.Add(k);
        return list;
    }

    /// <summary>The four cells sharing an edge with this one, in the order +u, +v, −u, −v (never null).</summary>
    public CubeKey EdgeNeighbour(int side) => side switch
    {
        0 => Neighbour(1, 0)!.Value,
        1 => Neighbour(0, 1)!.Value,
        2 => Neighbour(-1, 0)!.Value,
        _ => Neighbour(0, -1)!.Value,
    };

    public override string ToString() => $"F{Face}/L{Level}/{X}_{Y}";

    /// <summary>The key with its centre's latitude and longitude, for tool output: <c>F0/L13/4123_4091 (46.95108°N 7.43863°E)</c>.</summary>
    public string Describe()
    {
        var c = Centre;
        return string.Create(CultureInfo.InvariantCulture,
            $"{this} ({Math.Abs(c.LatDeg):F5}°{(c.LatDeg >= 0 ? 'N' : 'S')} {Math.Abs(c.LonDeg):F5}°{(c.LonDeg >= 0 ? 'E' : 'W')})");
    }

    /// <summary>Reads <c>F0/L13/4123_4091</c> (what <see cref="ToString"/> prints); "/" may also be "-" or "_" for file names.</summary>
    public static CubeKey Parse(string s)
    {
        var parts = s.Trim().Split('/', '-', '_');
        if (parts.Length != 4 || parts[0].Length < 2 || char.ToUpperInvariant(parts[0][0]) != 'F'
            || parts[1].Length < 2 || char.ToUpperInvariant(parts[1][0]) != 'L')
            throw new FormatException($"not a cube key: '{s}' (F<face>/L<level>/<x>_<y>)");
        var key = new CubeKey(int.Parse(parts[0][1..], CultureInfo.InvariantCulture), int.Parse(parts[1][1..], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture));
        return key.IsValid ? key : throw new FormatException($"cube key out of range: '{s}'");
    }

    /// <summary>A file-name form: <c>F0_L13_4123_4091</c>.</summary>
    public string FileStem => $"F{Face}_L{Level}_{X}_{Y}";
}
