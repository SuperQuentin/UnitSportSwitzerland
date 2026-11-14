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
    private readonly Dictionary<CubeKey, CubeTile?> _tiles = new();
    private readonly object _lock = new();

    /// <param name="cube">The body's cube.</param>
    /// <param name="level">The pyramid's finest level (the Moon's 5: 167 m cells).</param>
    /// <param name="load">A tile of that level, or null where the pyramid has none (called once a tile, on any thread).</param>
    /// <param name="craters">The profile's craters under the data, or null for the data alone.</param>
    public BodyHeights(CubeShape cube, int level, Func<CubeKey, CubeTile?> load, CraterField? craters)
    {
        _cube = cube;
        _level = level;
        _load = load;
        _craters = craters;
    }

    public CubeShape Cube => _cube;

    /// <summary>The height over the reference surface at a unit normal (body-fixed), metres: the data and the craters.</summary>
    public double At(EcefPos n) => Data(n) + (_craters?.At(n.X, n.Y, n.Z) ?? 0);

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
    /// </summary>
    public (EnuFrame Frame, float[] Y) Grid(EcefPos near, int cells, double cellM)
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
                var (_, ly, _) = frame.ToLocal(Ground(n));
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
