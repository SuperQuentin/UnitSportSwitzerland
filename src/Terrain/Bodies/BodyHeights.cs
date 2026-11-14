using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// A body's ground height at any point (#1850, the surface chain of docs/plans/planets.md section 4): its pyramid's
/// finest level, bilinear between the lattice's heights, and the profile's craters under that (<see cref="CraterField"/>).
/// What the walker stands on and the near ground draws; a tile the pyramid has not got is the reference surface (0),
/// with the craters still on it.
///
/// <para>
/// Everything is a function of the point and the data: the same ground on every peer, whichever patch asks. Thread-safe:
/// the near ground's workers share one, its few tiles cached.
/// </para>
/// </summary>
public sealed class BodyHeights
{
    /// <summary>Tiles kept decoded: a patch touches four at most, the walk round it a few more.</summary>
    public const int CachedTiles = 16;

    private readonly CubeShape _cube;
    private readonly int _level;
    private readonly Func<CubeKey, CubeTile?> _load;
    private readonly CraterField? _craters;

    /// <summary>The generated craters (null where the body has none): the boulders read their fresh ones.</summary>
    public CraterField? Craters => _craters;
    private readonly SurfaceStamps? _stamps;
    private readonly Dictionary<CubeKey, CubeTile?> _tiles = new();
    private readonly object _lock = new();

    /// <param name="cube">The body's cube.</param>
    /// <param name="level">The pyramid's finest level (the Moon's 5: 167 m cells).</param>
    /// <param name="load">A tile of that level, or null where the pyramid has none (called once a tile, on any thread).</param>
    /// <param name="craters">The profile's craters under the data, or null for the data alone.</param>
    /// <param name="stamps">Where the ground is made ready for a site (#1939: craters calmed under a lander, a colony's
    /// level plateau), or null for none.</param>
    public BodyHeights(CubeShape cube, int level, Func<CubeKey, CubeTile?> load, CraterField? craters, SurfaceStamps? stamps = null)
    {
        _cube = cube;
        _level = level;
        _load = load;
        _craters = craters;
        _stamps = stamps;
    }

    /// <summary>The sites' stamps on this ground, or null.</summary>
    public SurfaceStamps? Stamps => _stamps;

    public CubeShape Cube => _cube;

    /// <summary>The height over the reference surface at a unit normal (body-fixed), metres: the data, the craters and the stamps.</summary>
    public double At(EcefPos n) => Stamped(n, Data(n), _craters?.At(n.X, n.Y, n.Z) ?? 0);

    /// <summary>The data, the craters from <paramref name="smallestCrater"/> metres up (a coarser mesh's ground) and the stamps.</summary>
    public double At(EcefPos n, double smallestCrater) =>
        Stamped(n, Data(n), _craters?.At(n.X, n.Y, n.Z, smallest: smallestCrater) ?? 0);

    private double Stamped(EcefPos n, double data, double craters) =>
        _stamps == null ? data + craters : _stamps.Apply(n, data, craters, Data);

    /// <summary>The data's height alone at a unit normal: the finest tile, bilinear; 0 where there is none.</summary>
    public double Data(EcefPos n)
    {
        var (face, u, v) = _cube.FromNormal(n);
        var key = CubeKey.FromFace(face, u, v, _level);
        if (Tile(key) is not { } tile) return 0;
        int bits = tile.Bits, side = 1 << bits;
        double scale = Math.Pow(2, _level + bits);
        double gx = Math.Clamp((u + 1) * 0.5 * scale - ((long)key.X << bits), 0, side);
        double gy = Math.Clamp((v + 1) * 0.5 * scale - ((long)key.Y << bits), 0, side);
        int i = Math.Min((int)gx, side - 1), j = Math.Min((int)gy, side - 1);
        double fx = gx - i, fy = gy - j;
        double h00 = Finite(tile.HeightAt(i, j)), h10 = Finite(tile.HeightAt(i + 1, j));
        double h01 = Finite(tile.HeightAt(i, j + 1)), h11 = Finite(tile.HeightAt(i + 1, j + 1));
        return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
    }

    /// <summary>
    /// The globe's own surface at a unit normal: the finest tile's every <paramref name="stride"/>th height, flat between
    /// them in the two triangles a quad of the globe's mesh is cut into (its diagonal from (i+1, j) to (i, j+1)). The
    /// near ground's edge eases into it, so the two meet without a step.
    /// </summary>
    public double Coarse(EcefPos n, int stride)
    {
        var (face, u, v) = _cube.FromNormal(n);
        var key = CubeKey.FromFace(face, u, v, _level);
        if (Tile(key) is not { } tile) return 0;
        int bits = tile.Bits, side = 1 << bits, cells = side / stride;
        double scale = Math.Pow(2, _level + bits);
        double gx = Math.Clamp(((u + 1) * 0.5 * scale - ((long)key.X << bits)) / stride, 0, cells);
        double gy = Math.Clamp(((v + 1) * 0.5 * scale - ((long)key.Y << bits)) / stride, 0, cells);
        int i = Math.Min((int)gx, cells - 1), j = Math.Min((int)gy, cells - 1);
        double fx = gx - i, fy = gy - j;
        double a = Finite(tile.HeightAt(i * stride, j * stride)), b = Finite(tile.HeightAt((i + 1) * stride, j * stride));
        double c = Finite(tile.HeightAt(i * stride, (j + 1) * stride)), d = Finite(tile.HeightAt((i + 1) * stride, (j + 1) * stride));
        return fx + fy <= 1 ? a + fx * (b - a) + fy * (c - a) : d + (1 - fx) * (c - d) + (1 - fy) * (b - d);
    }

    /// <summary>The body-fixed point of the ground at a unit normal.</summary>
    public EcefPos Ground(EcefPos n) => _cube.OnEllipsoid(n, At(n));

    /// <summary>The unit normal of the body's reference surface under a body-fixed point.</summary>
    public EcefPos NormalUnder(EcefPos p)
    {
        var shape = _cube.Shape;
        if (shape.E2 == 0)
        {
            double len = p.Length;
            return new EcefPos(p.X / len, p.Y / len, p.Z / len);
        }
        var g = shape.ToGeodetic(p);
        return CubeSphere.NormalAt(g.LatDeg, g.LonDeg);
    }

    /// <summary>
    /// The ground round a point as a square grid in the tangent frame there (the near ground's collision and mesh):
    /// <paramref name="cells"/> cells of <paramref name="cellM"/> a side, centred under <paramref name="near"/>. The
    /// frame stands on the ground under the centre (x east, y up, z south); each vertex is the ground under the grid
    /// point, its height in that frame (the curve of the body included). The ground's own sideways offset from the grid
    /// is dropped: (height difference) · (distance) / radius, a centimetre for 100 m at 256 m on the Moon.
    /// <para>
    /// With <paramref name="blendM"/>, the last metres before the edge ease from this ground into the globe's own coarse
    /// surface (<see cref="Coarse"/> at <paramref name="stride"/>): the edge is the globe's, so the square and the globe
    /// round it meet without a step.
    /// </para>
    /// </summary>
    public (EnuFrame Frame, float[] Y) Grid(EcefPos near, int cells, double cellM, double blendM = 0, int stride = 1) =>
        Grid(near, cells, cellM, blendM, blendM > 0 ? n => Coarse(n, stride) : null);

    /// <summary>
    /// <see cref="Grid(EcefPos, int, double, double, int)"/> with the edge eased into any surface (<paramref name="edge"/>,
    /// a height at a unit normal: the medium ground's, under the near one), and the craters from
    /// <paramref name="smallestCrater"/> up (a coarser grid's).
    /// </summary>
    public (EnuFrame Frame, float[] Y) Grid(EcefPos near, int cells, double cellM, double blendM, Func<EcefPos, double>? edge,
        double smallestCrater = 0)
    {
        var n0 = NormalUnder(near);
        var g0 = _cube.Shape.ToGeodetic(_cube.OnEllipsoid(n0, 0));
        var frame = new EnuFrame(g0 with { H = At(n0) }, _cube.Shape);
        int side = cells + 1;
        var y = new float[side * side];
        System.Threading.Tasks.Parallel.For(0, side, j =>
        {
            double z = (j - cells / 2.0) * cellM;
            for (int i = 0; i < side; i++)
            {
                double x = (i - cells / 2.0) * cellM;
                var n = NormalUnder(frame.ToEcef(x, 0, z));
                double h = At(n, smallestCrater);
                double toEdge = cells / 2.0 * cellM - Math.Max(Math.Abs(x), Math.Abs(z));
                if (edge != null && blendM > 0 && toEdge < blendM)
                {
                    double t = 1 - toEdge / blendM;
                    t = t * t * (3 - 2 * t);
                    h += (edge(n) - h) * t;
                }
                var (_, ly, _) = frame.ToLocal(_cube.OnEllipsoid(n, h));
                y[j * side + i] = (float)ly;
            }
        });
        return (frame, y);
    }

    private static double Finite(float h) => float.IsFinite(h) ? h : 0;

    private CubeTile? Tile(CubeKey key)
    {
        lock (_lock)
            if (_tiles.TryGetValue(key, out var t)) return t;
        // decoded outside the lock: two threads may load one tile at once, both get the same heights
        var tile = _load(key);
        lock (_lock)
        {
            if (_tiles.Count >= CachedTiles) _tiles.Clear();
            _tiles[key] = tile;
        }
        return tile;
    }
}
